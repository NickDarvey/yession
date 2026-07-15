# Plan 02 — The Manager as its own process

> **Status: delivered.** Steps 21–27 implemented and accepted — see the
> [tracker](TODO.md) Phase 4 table for evidence per step.
>
> Phase 4 · Process split, management UI, real packaging
> Addresses the top items from [GAPS.md](../GAPS.md) § Runtime & topology: the Manager
> and Session Processes share one Node process, and there is no session-management
> surface (one session per launch, no create/list/resume). Also replaces the ad-hoc
> tarball packaging with per-binary Node single-file executables, and splits the test
> suite into a cheap always-on tier and an expensive `verify` tier that gates releases.

## Product behaviour

1. **The Manager is a program you run**; it owns no session content. It serves a small
   **management UI** (server-side rendered, htmx) on a stable local port: list
   sessions with their status (running / stopped / crashed), **create** a session
   (name + token), **launch / resume** it, **stop** it, and open a running session's
   client URL. This is deliberately *not* the collaborative client app — it is an
   admin surface, so SSR + htmx fragments, no Elmish, no Yjs.
2. **Each session runs in its own OS process.** A crashing session cannot take the
   Manager (or its siblings) down; the Manager observes child exits and shows them.
3. **Resume is just launch.** A session's durable state (event log + doc sidecar)
   lives in its data directory; resuming spawns a fresh Session Process over the same
   directory — the boot replay (Step 19) does the rest. Nothing new to build for
   resume beyond the button.
4. **The Manager does not assume it is a singleton** — all state is per-instance
   (under its own data directory), ports are explicit or OS-assigned, and nothing
   global is touched — but it practically is one: two Managers over the *same* data
   directory are unsupported (documented; a lock arrives with SQLite).

## Topology & process contract

```
yession (Manager binary)                    yession-session (Session Process binary)
  ├── management UI  http://127.0.0.1:8321    ├── bootstrap/client/signalling HTTP (port from Manager, default 0)
  ├── manager state  <data>/manager.json      ├── WebRTC session transport
  ├── container authority (registry+backends) ├── event log + doc store  <data>/sessions/<id>/
  └── spawns/observes ──────────────────────▶ └── control client ──▶ Manager control endpoint
```

- **Spawn contract**: the Manager launches `yession-session` with environment
  variables only (no argv parsing to get wrong): session id, token, data directory,
  requested port (default 0), the Manager's control endpoint + a per-launch secret
  (below). The child prints exactly one **readiness line** to stdout — a JSON object
  with the bound port — which the Manager awaits with a timeout; anything else on
  stdout/stderr is passed through as logs. Exit before readiness = failed launch;
  exit after = crashed/stopped, reflected in the UI.
- **Stop** = SIGTERM, escalating to SIGKILL after a grace period. Durability does not
  depend on graceful shutdown (write+fsync before visibility, torn-tail recovery).
- **Manager restart**: children are child processes and die with it (no daemonising,
  no orphan adoption — out of scope). At boot every session is therefore `stopped`;
  runtime state (pid, port) is never persisted, only reconciled.

## Authority across the process boundary

The design invariant stands: **the Manager owns launch and container authority**
(design.md §3); a Session Process never holds engine access, only a scoped capability.
In-process that capability was a closure; across processes it becomes a **control
RPC**:

- At launch the Manager mints a random per-launch **control secret** and passes it to
  the child. The Manager's control endpoint (same HTTP server as the UI, `/control/*`,
  127.0.0.1) authenticates each call by secret → resolves the launch → the session —
  so the session scope is *established by the Manager*, exactly like the closure was;
  a Session Process still cannot name another session.
- `SessionEnvironmentCapabilities` (StartContainer / Execute / Stop) is implemented
  client-side as RPCs; command output streams back as the response body (NDJSON
  chunks — same shape as the log's `CommandOutputReceived` slices). The existing
  `Authority` registry and `ContainerBackend`s stay in the Manager unchanged; all
  Step 11 rejection guarantees keep their tests, now exercised across the boundary.
- Wire shapes get explicit Thoth codecs like every other boundary. No session content
  crosses the control channel — only environment/command traffic.

**Interim slice**: the process split lands one step before the RPC; in between, spawned
sessions run with `SessionEnvironment.unavailable` (conversational agent only, needs
recorded as unavailable) — honest, shippable, and already a tested mode.

## Manager state: file persistence behind an explicit codec

- `ManagerState` is a plain record: a schema `Version` plus the session registry —
  per session: id, display name, token, created-at, data-directory name. Runtime facts
  (pid, port, status) are deliberately not in it.
- A hand-written Thoth **codec** (`Codec.managerState`) is the only way the state
  touches disk — same discipline as the event envelope. Unknown future fields decode
  tolerantly; the `Version` field is the SQLite migration hook.
- Storage is one JSON file, written **atomically** (temp file + rename, fsync) on
  every mutation, loaded at boot; corrupt state fails loudly. Moving to SQLite later
  swaps the storage adapter behind the same codec — callers never see the difference.
- Tokens are stored plaintext, consistent with the local-development threat model
  (recorded in GAPS).

## Management UI (htmx, server-side rendered)

- Pure F# render functions (like `View.fs`) produce full pages and **fragments**; htmx
  swaps fragments on `hx-post`/`hx-get` — no client bundle, no Elmish. htmx itself is
  vendored and embedded in the binary (no CDN; local-first).
- Routes: `GET /` (session table + create form), `POST /sessions` (create),
  `POST /sessions/{id}/launch`, `POST /sessions/{id}/stop` (each returns the updated
  row fragment), `GET /sessions/{id}` (detail: status, client URL, recent child log
  tail). Status updates via `hx-trigger="every 2s"` polling on the row — no
  websockets for an admin page.
- Binds 127.0.0.1 on `YESSION_MANAGER_PORT` (default 8321 — a management UI wants a
  bookmarkable address); a second Manager instance must choose its own port (clear
  error on conflict, not a silent fallback).

## Packaging: two Node single-file executables

- **Binaries**: `yession` (Manager: node:http + child_process + fs — no native deps)
  and `yession-session` (Session Process: needs `node-datachannel`). Built with
  Node's built-in **SEA** (single executable applications): esbuild-bundle each entry
  to CJS (SEA requires a CommonJS entry), generate the SEA blob
  (`node --experimental-sea-config`), copy the node binary, inject with postject.
- **Assets ride the blob** (`sea.getAsset`): the browser client bundle, htmx, and —
  for the session binary — the `node-datachannel` prebuilt `.node` addon, extracted
  at startup to a per-version cache directory and loaded from there (native addons
  cannot load from inside a SEA blob).
- **The Claude Code executable stays external** (the agent SDK spawns it): resolved
  from `PATH`/`YESSION_CLAUDE_PATH` at runtime; absent ⇒ the session runs human-only,
  as today.
- **`scripts/package.mjs` is deleted.** The build/packaging orchestration becomes an
  F# script (`scripts/build.fsx`, run by `dotnet fsi` — the .NET toolchain is already
  pinned), invoked from mise tasks; release artifacts are the two binaries per
  platform (tar.gz per OS/arch as before).
- The Manager spawns `yession-session` found **next to its own executable** (fallback:
  `YESSION_SESSION_BIN`), so the two binaries ship and install as one directory.

## Test tiers: tags, not folders

Pyxpecto has no CLI filter, so the tag is ours and lives in code, vitest-style:

- `Tag.verify tests` — includes the tests when `YESSION_TEST_TIER` is `verify` (or
  `all`); otherwise emits a single visible `skipped: verify tier` case (skips are
  reported, never hidden — the repo's standing rule). One combinator, no folder moves.
- **Cheap tier (every PR, every `mise run test`)**: domain/codec/model units, drain
  race tests, scheduler properties (seconds, no IO), in-memory transport tests.
- **`verify` tier (master push, gates the release)**: everything that binds ports or
  spawns processes — the WebRTC E2E suites, browser E2E, Docker smoke, live SDK
  smokes, and the new executable-composition E2E. `mise run verify` = build binaries +
  full suite with `YESSION_TEST_TIER=verify` + browser E2E.
- CI: the `test` job runs the cheap tier on PRs; a `verify` job (master push only)
  replaces it as the gate for `package`/`release`. PRs get faster and stop needing
  Chromium or credentials.

## The executable-composition E2E (verify tier)

The point of the split, tested on the real artifacts — not the Fable output in-tree:

1. Build both binaries; start `yession` with a fresh data directory.
2. Drive the management UI over plain HTTP: create a session, launch it, parse the
   client URL from the fragment.
3. Connect a real client (Node WebRTC harness) to the child Session Process; enqueue
   and observe the message in the timeline; run a command through the control-RPC
   capability (proves authority across the process boundary on the shipped binaries).
4. Kill the Session Process; the UI shows it stopped; **resume** it; history and
   collaborative state are intact (boot replay on the same data directory).
5. Restart the Manager; the session registry is intact (state file + codec).

## Delivery steps (tracker Phase 4)

| # | Step | Outcome | Verification |
|---|------|---------|--------------|
| 21 | Test tiers & verify gate | `Tag.verify` combinator; expensive suites tagged; `mise run verify`; CI: cheap on PR, verify gates release | Cheap run is green and fast without Chromium/credentials; verify run includes all skipped-by-default suites; CI wiring exercised on a PR + master push |
| 22 | Manager state & codec | `ManagerState` + `Codec.managerState`; atomic file store; create/list survive restart | Codec round-trip incl. unknown-field tolerance; corrupt-file fails loudly; restart-keeps-registry test |
| 23 | Session Process as an OS process | `yession-session` entry (env contract, readiness line); Manager spawns/observes/stops children; resume over the same data dir | Spawn/readiness/stop/crash-observation tests; resume-with-history E2E (verify tier); capability-less interim mode explicit |
| 24 | Authority over the control RPC | Control endpoint + per-launch secret; capability RPC client; streaming command output; Step 11 rejections hold across the boundary | Authority tests re-run across processes (forged secret, cross-session, stopped container); streamed-output ordering test |
| 25 | Management UI (htmx) | SSR pages + fragments; create/launch/stop/resume/list; status polling; vendored htmx | Fragment render units (pure functions); UI-flow E2E over HTTP (create→launch→stop→resume); no-CDN check |
| 26 | SEA binaries & F# build script | Two single-file executables; native addon extraction; `scripts/build.fsx` replaces `package.mjs`; release workflow ships them | Boot smoke per binary from a clean directory; addon loads from the extraction cache; `package.mjs` gone; release artifacts contain exactly the two binaries + docs |
| 27 | Composition E2E & Phase 4 acceptance | The full scenario above on built artifacts; acceptance recorded | The verify-tier composition E2E green on Linux + macOS runners; 5 consecutive cheap-tier runs + 1 verify run recorded in the tracker |

## Risks & open questions

- **SEA is still flagged experimental in Node 24** (`--experimental-sea-config`); the
  runtime warning is cosmetic, but the blob format may move between Node majors — the
  Node version is pinned in mise, so this is a controlled upgrade, not drift.
- **CJS bundling**: the session binary's dependencies are ESM-heavy and the agent SDK
  is loaded via dynamic `import()`; esbuild converts these when bundling to CJS, but
  the SDK also spawns its own executable — that path must be verified on the packaged
  binary early in Step 26, not last.
- **Native addon extraction** (node-datachannel) writes to a cache directory at first
  run; a read-only install location must still work (cache under `$HOME`/tmp, not
  next to the binary).
- **Readiness race**: the child must print the readiness line only after `listen`
  resolves (it already resolves the port that way in-process); the Manager needs a
  launch timeout so a wedged child fails the launch rather than the UI.
- **Control endpoint hardening** is deliberately minimal (127.0.0.1 + per-launch
  secret) — same local-dev threat model as the session token; revisit with real authn.
- **Windows** remains out of scope (as today); SEA would work there but nothing else
  is tested.
