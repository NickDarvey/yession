# Known gaps

An honest inventory of what Yession does **not** do yet, as of `1.0.0-beta.*`. Phases
1–4 are accepted, plus later work delivered outside the numbered phases — client
presentation (Metro/Zune styling, rich-text editing, collaborative presence cursors),
telemetry (Plan 04), and the Manager→Session control-RPC reverse legs
([tracker](plans/TODO.md)). Everything below is deliberate scope, recorded so nobody
discovers it in production. Items are roughly ordered by how much they matter.

## Security & trust

- **User authorization gates ACCESS, not identity.** Sessions authorize users through
  the Manager's OIDC provider ([plan](plans/04-session-authorization.md)): the
  shared session token is gone, the browser rides an authorization-code + PKCE bounce
  into an HttpOnly cookie, and peer/event access is minted per user. But ID-token
  claims are NOT yet threaded into `PeerId`/`ActorRef` — display names stay
  self-asserted and events are not attributed to authenticated users (the recorded
  follow-up).
- **The only authentication strategy is trust-localhost.** The provider's
  `AuthenticationStrategy` seam exists precisely so an upstream OIDC (or any other)
  strategy can slot in, but until one does, every loopback request IS the single
  local user — the OIDC machinery adds structure, not secrets, over the same
  local-dev threat model. No `nonce` in ID tokens yet (PKCE + confidential client +
  loopback); add it with the upstream strategy.
- **No transport encryption guarantees beyond WebRTC/DTLS.** Everything binds
  127.0.0.1; loopback HTTP is the RFC 8252 pattern, but nothing here is LAN-safe.
- **The Manager and Process read plaintext** (per the stated Phase 1–2 threat model);
  command-to-container encryption is designed for but not implemented.
- **The management UI itself has no login.** It binds 127.0.0.1 and its open links are
  now plain URLs (no embedded tokens — the session's own OIDC gate does the work), but
  anyone with local access can manage sessions. The authentication-strategy seam is
  where a UI login would reuse the same policy.
- **`LocalProcessBackend` provides no OS isolation.** Commands run as child processes of
  the Manager with the Manager's own user and environment. The *authority* contract
  (session scoping, handle validation) is enforced and tested, but the *engine* is not a
  sandbox. Use the Docker backend for isolation — see next.
- **The Docker backend runs through the `dockerode` SDK and is integration-tested in the
  verify gate.** Containers and a per-session named workspace volume are named by the
  session id (a Crockford base32 id, always a valid Docker object name), and `EnvironmentSpec`
  is fully interpreted — image/build, mounts (incl. the persistent workspace volume),
  working directory, env-var refs, secret refs, and command timeouts. The suite
  (`tests/Yession.Tests/DockerIntegration.fs`) runs where a daemon exists; on the CI
  `verify` runner `YESSION_REQUIRE_DOCKER=1` makes a missing daemon a hard failure rather
  than a silent skip. The dev container has no daemon, so local runs still report a skip.
- **Secrets**: `SecretRef` resolves from a process-env store (local-dev only — see
  `DockerBackend`); there is no real secret store yet.

## Runtime & topology

- **The process split is done** (Phase 4): each session is a child OS process of the
  Manager, capabilities cross the boundary as a secret-scoped control RPC, and
  sessions are created/launched/resumed/stopped from the htmx management UI — but
  **children die with the Manager** (no daemonising, no orphan adoption): a Manager
  restart stops every running session, and resumes are manual clicks.
- **The Manager is practically a singleton.** Nothing global is assumed (per-instance
  data directories, OS-assigned session ports), but two Managers over the SAME data
  directory are unsupported — there is no lock until the SQLite move — and the
  management UI's fixed default port (8321) means a second instance must configure its
  own.
- **Session ports are OS-assigned and change on every launch**, so a session's client
  URL is not stable across resumes; the management UI's open link is the way in.
- **No health checks beyond the readiness line**: a child that wedges after readiness
  shows as running until it exits.
- **The Manager→Session notification channel is a transport without a producer yet.**
  The reverse leg of the control RPC exists end to end — the child subscribes to
  `GET /control/notifications` (SSE, per-launch secret), the Manager multiplexes and
  fans out via `ProcessManager.Notify : SessionId -> SessionNotification -> unit`, and
  the child dispatches each notification through a handler that MAY record a durable
  `SessionEvent` — but **nothing calls `Notify` in production yet**, the payload
  (`SessionNotification.EnvironmentChanged of unit`) is an explicit placeholder, and the
  default handler only logs. The intended first producer — the Manager autonomously
  detecting an out-of-band environment change (e.g. a container it owns dying without
  the session having stopped it) — and the real notification payload are the follow-up.
- **The MCP tool stream is a transport without a producer yet.** A second reverse leg
  exists end to end — the child subscribes to `GET /control/mcp` (SSE), gets the current
  `ListToolsResult` immediately (McpHub's retained snapshot) and a fresh list on every
  change, and the Manager announces lists via `ProcessManager.PublishMcpTools`. But
  **nothing calls `PublishMcpTools` in production yet** (the list is always empty), the
  child's default handler only logs the count, and no MCP client actually consumes the
  list. Discovering real MCP services and exposing their tools to agent turns is the
  follow-up; the tool set is currently Manager-global (not scoped per session).
- **Peer-to-peer is star-shaped through the Process.** Clients sync Yjs state via the
  Session Process relay, not directly with each other; y-webrtc-style meshes are not
  used.

## Persistence & data

- **Everything durable is now persisted** (Phase 3): the event log and the Yjs
  document both survive Process restarts (sidecar `*.doc.jsonl`, compacted at open),
  and browser clients keep the document in IndexedDB (`y-indexeddb`, keyed by the
  session id embedded in the bootstrap page). The event log's browser-side cache is
  the browser's own HTTP cache: the log is served as fixed-size immutable chunks
  (`/events/{n}`, 3-day `max-age` on full chunks), so cold loads replay history from
  disk and only the growing tail chunk hits the network.
- **The JSONL event log loads fully into memory** and has no compaction, rotation, or
  checksumming; a corrupt line fails the whole open (loud by design). The doc store
  compacts only at open — a very long-lived Process grows its sidecar until restart.
- **Event chunks are cookie-gated for browsers; headless clients still put a minted
  peer token in the chunk URL** (`?token=`). The browser path is clean (the same-origin
  auth cookie rides each fetch, so URLs — and cache keys — carry no secrets); the
  token-in-URL path remains for Node clients and tests, scoped to per-process minted
  tokens that die with the session.
- **A session served offline has no app shell**: IndexedDB restores state instantly
  once the page loads, but the page itself still needs the Session Process (no
  service worker).

## Browser client

- **Rendering is Fable.Lit (lit-html), a reconciling renderer.** The view is a total
  function of the model, rendered into `#app` on every change, and Lit diffs the DOM so
  focus and caret survive re-renders with no manual restore hack (this replaced the old
  innerHTML-replacement approach). The only remaining manual DOM work is pinning the chat
  scroll and pixel-positioning collaborators' cursor markers (a native `<input>` exposes no
  per-character geometry).
- **One WIP draft per client, co-editable by any peer** ([plan](plans/03-one-draft-per-client.md)):
  drafts are keyed by author (`Map<PeerId, DraftState>`), so each client owns at most one —
  structurally, not by a runtime cap. Any peer may co-edit any slot (collaboration); the
  owner sends their own. The queue is untouched (send clears the slot, so a client still
  queues many by sending repeatedly). Drafts and queued messages are now **rich ProseMirror
  editors** on a Yjs `XmlFragment` (markdown typing, bold/italic/code, lists, paste-as-markdown,
  undo/redo) — not textareas, not plain text — and **collaborative presence cursors** overlay
  every collaborative field (the title input and the body editors), showing each peer's caret
  and selection with a colour + name label, relayed over ephemeral `Presence` frames (never
  durable). Invariant 4 (clean send) has a dedicated Hedgehog property; broader draft-op
  schedules (participation, offline rejoin) are the follow-up.
- **Reconnect is manual** (reload). The model reaches `Reconnecting`, but the browser
  shell does not yet redial and resume (the protocol supports it — E2E-4 proves resume
  works, and the client now pushes its full local state on every accept — the browser
  redial wiring is what's missing).
- **A 401 from `/me` renavigates to `/login` unconditionally** — there is no in-app
  "signed out" state; the client simply rides the OIDC bounce again. Offline, the
  probe's network failure keeps the cached shell read-only with no reconnect UI.
- **No browser support matrix**: verified on Chromium (headless, in CI); the ICE
  gathering settle-fallback should cover Safari/Firefox mDNS behaviour, but they are
  untested.

## Agent

- **The live agent's tool results are text renderings** of the typed capability
  results; there is no structured tool-result schema and no tool for reading the
  command log or session history beyond the prompt transcript.
- **The context pack is a flat transcript** rebuilt per turn from the full projection —
  no windowing, summarisation, or token budgeting. Bodies are now Markdown (rich text
  landed), but the transcript is still a naive `author: body` join with no multi-line
  handling, so long sessions or large rich bodies will eventually overflow the model context.
- **Turn discipline is done** (Phase 3): single-flight is enforced by the queue drain,
  interrupt is explicit, and the invariants are property-tested — but the queue has
  **no size cap**, a drain coalesces any backlog into ONE turn (no per-message turns
  option), and the queued-message UI has no "locked" visual during the drain broadcast
  window (a peer can briefly type into an entry that is about to vanish — the edit is
  safely discarded, but the UX flickers).
- **No repository integration** (`.yession.yml`, clone, commit/push) — explicitly later
  phases per the delivery plan.
- **Live-path verification is credential-gated by design.** Without
  `ANTHROPIC_API_KEY`/`CLAUDE_CODE_OAUTH_TOKEN` the two live tests self-report skipped;
  a dedicated low-privilege key in CI would exercise them on every merge
  (recommended). `YESSION_CLAUDE_PATH` matters in sandboxes that kill the SDK's
  vendored binary.

## Delivery & operations

- **Node is a required runtime.** Yession ships as one npm package with two bins
  (`yession`, `yession-session`); `npm i -g yession-*.tgz` pulls the platform-native
  deps — `node-datachannel`'s addon AND the SDK's native `claude` — via npm's optional
  dependencies, so install is all it takes and the agent works offline afterward. But
  there is no self-contained binary anymore: a machine without Node ≥24 can't run it.
- **First install downloads the native `claude`** (~240 MB, platform-specific): it is
  not in the 300 KB tarball, npm fetches it. So the *first* install needs network and
  disk; the SDK's own resolution finds it thereafter (no `YESSION_CLAUDE_PATH` needed).
- **The composition E2E and install smoke run on Linux/CI**; other platforms' native
  resolution rides npm's own optional-dependency machinery, unverified per-commit.
- **No per-platform build matrix, no code signing.** Release CI is a single `ubuntu-latest`
  job that ships one platform-neutral npm tarball; the platform-native pieces are resolved
  by npm's `optionalDependencies` on whichever machine runs `npm install`, not built by
  Yession (this replaced the earlier SEA per-platform binaries — see Step 26→28). Yession
  therefore has no compiled binary of its own to sign or notarise; the native `claude` and
  `node-datachannel` addon npm pulls in are unsigned third-party downloads that may still
  trip macOS Gatekeeper. Darwin and Windows resolution rides npm's own machinery, exercised
  only by the Linux install-smoke — unverified per-commit on those platforms.
- **Telemetry is agent-turn usage only** (Plan 04): each completed turn emits one OpenTelemetry
  **log record** — the token/cache counts plus session/turn/model ids, never message content —
  over OTLP/HTTP to the Manager, which acts as the collector (`/v1/logs`) and logs + aggregates
  per-session totals to stdout. Off unless the Manager enables it. Still **no metrics pipeline,
  no traces, no downstream re-export** (all behind the collector's `onRecord` seam), **no
  structured app logging or crash reporting** beyond stdout.
- **Interactive terminal, multi-node/remote sessions, and work-intake integrations
  (Slack/Linear)** remain out of scope, as planned.

## Testing debt

- Browser E2E runs on Chromium only, and drives one host platform per CI run.
- The Yjs relay trusts ordered delivery per data channel; the Phase 3 property
  schedules cover arbitrary *delivery timing* (staleness, partitions, restarts) but
  not corrupted/duplicated *frames* on the wire.
- The vendored Hedgehog does no shrinking: a failing property prints the whole
  schedule, not a minimal one.
- Load/scale characteristics (many peers, large logs, long drafts) are unmeasured.
