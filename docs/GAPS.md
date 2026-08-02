# Known gaps

An honest inventory of what Yession does **not** do yet, as of `1.0.0-beta.*`. Phases
1–4 are accepted, plus later work delivered outside the numbered phases — client
presentation (Metro/Zune styling, rich-text editing, collaborative presence cursors),
telemetry (Plan 04), and the Manager→Session control-RPC reverse legs
([tracker](plans/TODO.md)). Everything below is deliberate scope, recorded so nobody
discovers it in production. Items are roughly ordered by how much they matter.

## Security & trust

- **Event attribution is threaded, but presentation is thin**
  ([Plan 07](plans/07-byo-user-authorization.md)): under a real strategy, events
  attribute to the Manager-verified user (`ActorRef.UserRef`, riding the OIDC bounce →
  cookie → session-minted peer token — never a peer-controlled frame). Remaining:
  peer display names stay self-asserted, and the client UI renders a bare
  `UserId.value` (no names/avatars from verified claims yet — `UserClaims` are
  carried and recorded, not displayed).
- **BYO authorization is trusted plaintext headers** ([Plan 07](plans/07-byo-user-authorization.md)):
  `--auth trusted-headers` trusts canonical `x-yession-*` identity headers from an
  operator-run authenticating proxy — anyone who can reach the loopback port directly
  can forge them (the same trust boundary as `--auth localhost`). The proxy MUST strip
  inbound `x-yession-*` headers and be the only non-local path in. A signed-JWT header
  strategy (verify against an operator JWKS; `Fable.Jose.jwtVerify` is already bound)
  is the recorded hardening follow-up. No `nonce` in ID tokens yet (PKCE +
  confidential client); the Plan 04 note stands.
- **Remote WebRTC has no relay fallback** ([Plan 09](plans/09-remote-session-access.md)).
  Sessions are remotely reachable through an operator's proxy — the `/sessions/stream`
  registry drives the serving binding, and `YESSION_SESSION_URL` is a template over
  `{id}`/`{port}` ([Plan 10](plans/10-mounted-sessions.md)) that threads the public
  address into open links and redirect URIs, whether the operator mirrors ports, gives
  each session a subdomain, or mounts each under a path — but the data channel
  still connects peer-to-peer on host candidates only (no STUN/TURN), so remote use
  needs a network where the session host's addresses route directly (e.g. an overlay
  like a tailnet, verified per deployment); an unauthenticated visitor can also hold
  open refused-at-`PeerHello` peer connections (no `/signal` throttling).
- **User-scoped secrets have exactly one writer: the connection broker**
  ([Plan 08](plans/08-connections-and-claude-auth.md)). The Claude sign-in stores an
  owner-scoped (user/peer) credential through the narrow `ConnectionAction` policy
  family; the GENERIC `/secrets` write surface for users is still absent — sessions
  still cannot `SetSecret` on `UserScope`, and there is no management-UI secrets page.
  The policy rows for a session-less, user-only `AuthzSubject` (`Session = None`) exist
  and are pinned by tests, but nothing constructs that subject yet.
- **No transport encryption guarantees beyond WebRTC/DTLS.** Everything binds
  127.0.0.1; loopback HTTP is the RFC 8252 pattern, but nothing here is LAN-safe
  without the operator's proxy in front.
- **The Manager and Process read plaintext** (per the stated Phase 1–2 threat model);
  command-to-container encryption is designed for but not implemented.
- **The management UI is gated by the authentication strategy** (Plan 07): a `Denied`
  outcome is a 401 on every UI route, and the default `--auth none` denies everything.
  Under `--auth localhost` anyone with local access can still manage sessions — that
  is the localhost trust rule working as stated, not an oversight.
- **The `host` sandbox backend provides no OS isolation — and it is the default.**
  Sandboxes are session-owned (the `CreateSandbox` seam): agent-issued commands run as
  child processes of the Session Process. The host backend passes an allowlisted env
  (never the process's own — the credential-leak regression test pins this), but file
  system and network are unconfined. `YESSION_WORK_SANDBOX=docker` opts into isolation;
  flipping the default to a confining backend (srt — bubblewrap/Seatbelt via
  `@anthropic-ai/sandbox-runtime` — is the planned sub-second tier) is deliberate
  future work.
- **The agent CLI itself still runs unconfined in the Session Process** (`tools: []`
  bounds the model's tool surface, not the process). Moving the CLI behind the sandbox
  seam via the SDK's `spawnClaudeCodeProcess` is the next planned step (AgentSandbox).
- **The Docker backend runs through the `dockerode` SDK and is integration-tested in the
  verify gate.** Containers and a per-sandbox named workspace volume are named by the
  session id (a Crockford base32 id, always a valid Docker object name), and `EnvironmentSpec`
  is fully interpreted — image/build, mounts (incl. the persistent workspace volume),
  working directory, env-var refs, and secret refs (resolved at sandbox spawn). The
  container drops all capabilities and sets `no-new-privileges`; it still runs the
  image's default (usually root) user with no resource limits — deliberate until a
  config surface exists. The suite (`tests/Yession.Tests/DockerIntegration.fs`) runs
  where a daemon exists; on the CI `verify` runner `YESSION_REQUIRE_DOCKER=1` makes a
  missing daemon a hard failure rather than a silent skip. The dev container has no
  daemon, so local runs still report a skip.
- **Secrets are a real Manager-owned store now** ([Plan 06](plans/06-secrets-and-abac.md)):
  AES-256-GCM per-entry ciphertext in `<DataDir>/secrets.json`, the KEK in the OS
  credential manager (`@napi-rs/keyring`, imported non-extractably each start), a
  a `/control/secrets/*` surface whose only value-returning route is
  `resolve` (below), a pure default-deny `Policy.authorize` over the composite
  session+user+peer identity, and store-backed `SecretRef` injection (session scope ▸
  bound users' scopes ▸ witnessed peers' scopes ▸ Manager process env — peers per
  [Plan 07](plans/07-byo-user-authorization.md)). Remaining, deliberate:
  - **Hosts without a credential manager run in-memory only** (dev containers, CI,
    headless servers): secrets die with the Manager; loud at boot, never a plaintext
    key file. (Tests cover the real keyring via the `Keyring` capability —
    `check Keyring` self-wraps with dbus + gnome-keyring when headless.)
  - **No shared/Manager-global scope.** User-scoped GENERIC secrets still have no
    writer (the Plan 08 connection broker writes only its own credential entries,
    through its own policy family). User↔launch and peer↔launch bindings are
    launch-lifetime (re-login re-forms them).
  - **Two deliberate value-returning routes, both resolve-shaped**:
    `/control/connections/resolve` (Plan 08) returns a connection credential to the
    calling session (an agent turn needs the token in-process), and
    `/control/secrets/resolve` feeds `SecretRef` env injection at the session's
    sandbox spawn (sandboxes are session-owned, so injection happens there). Both are
    gated to the caller's readable scopes; refresh tokens still never leave the
    Manager, and no agent-facing tool wraps either.
  - **Secret-token-injection is future work.** A resolved secret enters the sandbox
    as a plain env var, so anything the agent runs inside can read it. The srt
    backend's enforced egress allowlist will bound where a read value can be *sent*
    — most of the practical risk — and the real fix is egress substitution (a
    placeholder inside the sandbox, the real value substituted at the proxy toward
    declared hosts); srt's mandatory proxy is the natural host for that.
  - **No KEK rotation/recovery** (a lost credential entry orphans the store loudly;
    the operator deletes the file), and multi-user same-name injection precedence is
    unresolved until a real multi-user strategy lands.
  - `@napi-rs/keyring`'s per-platform prebuilds join `node-datachannel` in the
    "unsigned third-party native binaries" trust bucket below; macOS/Windows paths
    are field-verified only (CI exercises Linux/Secret Service).

## Runtime & topology

- **The process split is done** (Phase 4): each session is a child OS process of the
  Manager, supervision and secrets custody cross the boundary as a secret-scoped
  control RPC (environments are session-owned via the sandbox seam), and
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
- **A third reverse leg — connection statuses — has a real producer**
  ([Plan 08](plans/08-connections-and-claude-auth.md)): `GET /control/connections`
  streams each launch its readable connection metadata (snapshot on subscribe, fresh
  list on every credential change or new binding), and the session's agent gate and
  `/claude` status surface consume it. The hub mechanism is now generic
  (`NotificationHub<'n>`) and serves all three legs.
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
- **The history feed degrades explicitly, and only history degrades.** The event feed is
  the one leg that is HTTP rather than the data channel, so it fails on its own — and it
  used to fail silently: a rejected fetch became an empty final page, which the read loop
  read as "nothing new yet" and re-requested immediately, forever, with nothing in the
  model to show it. Drafts, title, and presence kept syncing over WebRTC throughout, so
  the only symptom was a timeline that never filled. Now a read returns
  `Result<EventPage, FeedFault>`; a resilience policy (`Yession.Domain.Resilience`,
  composed with the transport in `app/browser/Browser.fs`) retries only transient faults
  — five times, exponentially backed off from 250ms to a 10s ceiling, jittered so a
  Process restart does not bring every peer back in lockstep — and a settled failure
  parks the loop and surfaces as `FeedHealth`: a sidebar line, a banner, and a header
  status. Nothing is disabled while it is down, because writing is CRDT state in the
  local doc. What is still missing is a *manual* retry affordance: recovery waits for the
  next availability hint or reconnect, which a peer with a permanently rejected token
  (401) will never get, so that peer must reload to re-authorize.

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
- **Per-user agent credentials landed** ([Plan 08](plans/08-connections-and-claude-auth.md)):
  a human signs into their Claude account from the session's Connections panel — "this
  session only" (`SessionScope`) or "all my sessions" (their user/peer scope) — the
  Manager brokers the OAuth exchange as pure standards (it never learns the provider),
  and each agent turn runs on the TURN ACTOR's credential (session-scoped ▸ actor's
  own ▸ ambient env), resolved fresh per turn with Manager-side lazy refresh.
  Remaining, deliberate: the ambient `ANTHROPIC_API_KEY`/`CLAUDE_CODE_OAUTH_TOKEN`
  process env stays as the documented last resort (it is how CI's LiveAgent tier
  feeds the agent, and it applies to ANY actor); a refresh failure surfaces only as
  the turn's failure (no panel-level health indicator); the panel's status is polled
  by the browser (the SESSION learns of changes live over its control stream, the
  open browser tab re-asks).
- **Live-path verification is credential-gated by design.** Without
  `ANTHROPIC_API_KEY`/`CLAUDE_CODE_OAUTH_TOKEN` the live tests self-report skipped;
  a dedicated low-privilege key in CI would exercise them on every merge
  (recommended). `YESSION_CLAUDE_PATH` matters in sandboxes that kill the SDK's
  vendored binary.

## Delivery & operations

- **Node is a required runtime.** Yession ships as one npm package with two bins
  (`yession-manager`, `yession-session`); `npm i -g yession-*.tgz` pulls the platform-native
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
- **Telemetry is agent-turn usage plus Manager audit records** (Plans 04 + 06): each
  completed turn emits one OpenTelemetry **log record** — the token/cache counts plus
  session/turn/model ids, never message content. Every process (Manager and each session)
  is a **direct OTel emitter**; there is no Manager-side collector. Destination is chosen by
  the standard OTEL_* env the Manager is started with (`OTEL_LOGS_EXPORTER=console|otlp|none`,
  comma-separated for a stdout+collector tee; `OTEL_EXPORTER_OTLP_*` for the collector) and
  passed through to each child, whose identity the Manager adapts. Default `console` (stdout);
  no collector configured ⇒ forwarding is dropped. Separately, the Manager emits its own
  in-process `yession.*` **audit records** for the secrets/ABAC surface (ops, denies, injection,
  KEK/store lifecycle, user↔launch bindings, control 401s — see
  [Plan 06 § Telemetry](plans/06-secrets-and-abac.md)), one greppable stdout line each.
  Still **no metrics pipeline, no traces** (the emitter path generalises to both — same env
  selection, no collector to touch), **audit records not yet forwarded to a collector**, and
  **no structured app logging or crash reporting** beyond stdout.
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

## Terminals (Plan 12)

- **The browser-side terminal input binding has no browser E2E.** `syncTerminalInputs`
  (app/browser/Browser.fs) pushes a `Y.Text` root's value into an `<input>`, writes
  keystrokes back as the minimal CRDT edit, and preserves the caret when a collaborator's
  edit re-renders under it. Every layer beneath it is tested (the minimal-diff edit, the
  slot rule, the queue, the drain), but the binding itself is only exercised by a real
  browser, and PR 2's live mode is built directly on top of it.
- **A queued command whose terminal closes stays queued for ever.** Nothing runs it and
  nothing removes it; it is visible in the doc against a closed terminal and a person can
  delete it. Deliberately non-destructive rather than silently dropping someone's text, but
  the UI does not yet say why it will not run.
- **Terminal access equals session access.** A terminal can read the sandbox's environment
  (`env`), which after resolve-at-spawn includes secrets the session's spec references.
  This is not a new privilege — any peer could already ask the agent to run `env` — but a
  terminal makes it one keystroke, and a future per-user terminal gate would attach here.
- **Agent commands cannot hold a live-mode lease** (PR 2). A policy decision, not a
  mechanism gap: leases are human-only until there is a reason to change that.
