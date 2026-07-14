# Known gaps

An honest inventory of what Yession does **not** do yet, as of `1.0.0-beta.*`. Phases
1–3 are accepted ([tracker](plans/TODO.md)); everything below is deliberate scope,
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
- **`LocalProcessBackend` provides no OS isolation.** Commands run as child processes of
  the Manager with the Manager's own user and environment. The *authority* contract
  (session scoping, handle validation) is enforced and tested, but the *engine* is not a
  sandbox. Use the Docker backend for isolation — see next.
- **The Docker backend is shipped but only smoke-verified where a daemon exists.** The
  dev container has no daemon, so CI/dev runs report the smoke as skipped. Mounts,
  build specs, secret refs, and env-var refs in `EnvironmentSpec` are typed but not yet
  interpreted by any backend.
- **Secrets**: `SecretRef` exists in the spec vocabulary; there is no secret store.

## Runtime & topology

- **Manager and Session Processes share one Node process.** Each Process is its own
  composition root on its own port, and the capability boundary is real (closures), but
  a crashing Process takes the Manager with it and vice versa. The OS-process split
  (and with it, capability delivery across a process boundary) is future work.
- **One session per product launch.** `Main.fs` starts a single default session; the
  Manager API supports many, but there is no session-management UI/CLI (create, list,
  join by URL) yet.
- **The default port is OS-assigned (random)** so instances coexist; there is no
  stable well-known address without setting `YESSION_PORT`, and no port-conflict
  message beyond Node's raw error when a fixed port is taken.
- **Peer-to-peer is star-shaped through the Process.** Clients sync Yjs state via the
  Session Process relay, not directly with each other; y-webrtc-style meshes are not
  used.

## Persistence & data

- **Everything durable is now persisted** (Phase 3): the event log and the Yjs
  document both survive Process restarts (sidecar `*.doc.jsonl`, compacted at open),
  and browser clients keep the document in IndexedDB (`y-indexeddb`) — but the
  **event log has no browser-side cache**: a cold client replays the conversation
  over the wire every load.
- **The JSONL event log loads fully into memory** and has no compaction, rotation, or
  checksumming; a corrupt line fails the whole open (loud by design). The doc store
  compacts only at open — a very long-lived Process grows its sidecar until restart.
- **The browser doc store is keyed by host + path**, not session id (the client only
  learns the session id after connecting, and persistence must load before/without the
  network). Multiple sessions served from one origin+path would share a store.

## Browser client

- **Rendering is innerHTML-replacement with a focus/caret restore hack** for the draft
  being typed. Fine at this scale; a proper reconciling renderer (Elmish.React or
  morphdom) is the upgrade path. Caret position is restored to end-of-text, so editing
  mid-string while remote edits land can jump the caret.
- **One draft textarea UX**: no presence cursors, no per-peer selections, no rich text.
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

- **The release workflow is untested until the first master push reaches GitHub
  Actions** — the packaging script is verified locally (linux-x64 boot smoke), but the
  workflow itself (mise action, Playwright install on runners, macOS packaging, release
  creation) needs its first real run watched.
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
