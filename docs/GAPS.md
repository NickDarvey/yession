# Known gaps

An honest inventory of what Yession does **not** do yet, as of `1.0.0-beta.*`. Phases
1–4 are accepted ([tracker](plans/TODO.md)); everything below is deliberate scope,
recorded so nobody discovers it in production. Items are roughly ordered by how much
they matter.

## Security & trust

- **No transport encryption guarantees beyond WebRTC/DTLS, and no authn beyond the
  session token.** A single shared random token gates a session (acceptable for local
  development per design.md §3's threat model). There are no identities, no per-peer
  authorization, no token rotation, and the token rides a query parameter into the
  browser client.
- **The Manager and Process read plaintext** (per the stated Phase 1–2 threat model);
  command-to-container encryption is designed for but not implemented.
- **The management UI and control endpoint are unauthenticated beyond locality +
  secrets.** Both bind 127.0.0.1 only; the control RPC is gated by per-launch secrets,
  but the management UI itself has no login — anyone with local access can manage
  sessions (and session tokens appear in its open links). Same local-dev threat model
  as everything else; revisit with real authn.
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
- **The session token rides the chunk URLs** (`?token=`), consistent with the page
  URL, and therefore ends up in the browser's cache keys and history — acceptable for
  the local-development threat model, revisit with real authn.
- **A session served offline has no app shell**: IndexedDB restores state instantly
  once the page loads, but the page itself still needs the Session Process (no
  service worker).

## Browser client

- **Rendering is innerHTML-replacement with a focus/caret restore hack** for the draft
  being typed. Fine at this scale; a proper reconciling renderer (Elmish.React or
  morphdom) is the upgrade path. Caret position is restored to end-of-text, so editing
  mid-string while remote edits land can jump the caret.
- **One WIP draft per client, co-editable by any peer** ([plan](plans/03-one-draft-per-client.md)):
  drafts are keyed by author (`Map<PeerId, DraftState>`), so each client owns at most one —
  structurally, not by a runtime cap. Any peer may co-edit any slot (collaboration); the
  owner sends their own. The queue is untouched (send clears the slot, so a client still
  queues many by sending repeatedly). Still **one textarea each: no presence cursors, no
  per-peer selections, no rich text**. Invariant 4 (clean send) has a dedicated Hedgehog
  property; broader draft-op schedules (participation, offline rejoin) are the follow-up.
- **Reconnect is manual** (reload). The model reaches `Reconnecting`, but the browser
  shell does not yet redial and resume (the protocol supports it — E2E-4 proves resume
  works, and the client now pushes its full local state on every accept — the browser
  redial wiring is what's missing).
- **The session token defaults to `local-dev-token`** or a `?token=` query parameter.
- **No browser support matrix**: verified on Chromium (headless, in CI); the ICE
  gathering settle-fallback should cover Safari/Firefox mDNS behaviour, but they are
  untested.

## Agent

- **The live agent's tool results are text renderings** of the typed capability
  results; there is no structured tool-result schema and no tool for reading the
  command log or session history beyond the prompt transcript.
- **The context pack is a flat transcript** rebuilt per turn from the full projection —
  no windowing, summarisation, or token budgeting; long sessions will eventually
  overflow the model context.
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
- **darwin-x64 is not built** (runners produce linux-x64 and darwin-arm64); Intel Mac
  users need Rosetta or a matrix addition.
- **No Windows build**; no signing/notarisation for macOS binaries (Gatekeeper will
  warn).
- **No telemetry, structured logging, or crash reporting**; the Process logs to stdout.
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
