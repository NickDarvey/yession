# Known gaps

An honest inventory of what Yession does **not** do yet, as of the `5.x-beta` line. Phases
1–4 are accepted, plus later work delivered outside the numbered phases — client
presentation (Metro/Zune styling, rich-text editing, collaborative presence cursors),
telemetry (Plan 04), the Manager→Session control-RPC reverse legs, secrets + ABAC
(Plan 06), BYO user authorization (Plan 07), connections and Claude sign-in (Plan 08),
remote and mounted session access (Plans 09/10/12), idle reaping (Plan 11), and
terminals on the WorkSandbox (Plan 13, every stage). The plans those numbers name are gone;
each one's reasoning now sits beside the code it governs.
Everything below is deliberate scope, recorded so nobody discovers it in production.
Items are roughly ordered by how much they matter.

## Security & trust

- **Event attribution is threaded, but presentation is thin** (Plan 07): under a real strategy,
  events attribute to the Manager-verified user (`ActorRef.UserRef`, riding the OIDC bounce →
  cookie → session-minted peer token — never a peer-controlled frame). Remaining: peer display
  names stay self-asserted, and the client UI renders a bare `UserId.value` (no names/avatars
  from verified claims yet — `UserClaims` are carried and recorded, not displayed).
- **BYO authorization is trusted plaintext headers** (Plan 07): `--auth trusted-headers` trusts
  canonical `x-yession-*` identity headers from an operator-run authenticating proxy — anyone
  who can reach the loopback port directly can forge them (the same trust boundary as
  `--auth localhost`). The proxy MUST strip inbound `x-yession-*` headers and be the only
  non-local path in. A signed-JWT header strategy (verify against an operator JWKS;
  `Fable.Jose.jwtVerify` is already bound) is the recorded hardening follow-up.
- **No `nonce` in ID tokens yet, and the flow is why that is tolerable** rather than an
  oversight: the ID token is never delivered through the browser — the confidential RP
  redeems the code over its own back channel, with its client secret plus the PKCE
  verifier — so there is no injected-token path for a nonce to close. It becomes worth
  adding the first time a strategy federates to an upstream OP.
- **A declared MCP server's tool descriptions are untrusted text in the model's context**
  (Plan 16, Plan 17): an external server's `tools/list` descriptions go straight into the
  prompt, and with always-available servers they do so without a second human confirming.
  `instructions` are dropped for exactly this reason, but tool descriptions cannot be — the
  model must read them to call anything. `ToolDescriptor.Foreign` already marks the affected
  set; the recorded mitigation is an `AutoApprove` flag on the DECLARATION, where the operator
  already is, rather than a per-call prompt.
- **A declared MCP server is unconfined, and so is what it owns** (Plan 16): there is no
  srt/docker analogue for a serial port, and the serial provider (`examples/serial`) runs on
  the host with whatever access its user has. A device is more physical than a filesystem path
  — writing to the wrong tty can reflash a board. The provider narrows this by refusing to list
  ports it does not recognise (an unrecognised tty is usually the machine's own console), which
  is a policy, not a boundary. Its control leg is unauthenticated and must stay on loopback. A
  stream it OFFERS (Plan 19) is admitted the same way: the url must share the host the operator
  declared the server at, `ws`/`wss`, no credentials — which stops a tool result pointing a
  session at another machine, and is again a policy rather than a boundary. A declared server
  can still hand the session a socket to anything on the box it already runs on.
- **A second local address, unauthenticated, beside the provider** (Plan 18): the jumpstarter
  example talks to an exporter that serves gRPC with `--tls-grpc-insecure` and no passphrase,
  so its claim arbitrates the provider's clients and nothing else — any process on the box can
  dial the exporter directly and take the hardware out from under a holder. One host, one
  operator and loopback make that acceptable; a shared machine would need the passphrase
  upstream already supports, and a controller would replace the claim with a lease outright.
- **A driver method that never returns wedges one connection** (Plan 18): the jumpstarter
  example calls the SDK on a thread of its own, and nothing can interrupt a library call from
  outside. A method that blocks forever — or a stream whose next item never arrives — is
  answered with a timeout and that connection is dropped so later calls reconnect, but the
  thread stays parked on it, holding one SDK client until the process ends. Bounded (one thread
  per wedge, and only a driver that misbehaves can cause one) and visible in the answer, rather
  than fixed.
- **A provider's lifecycle is nobody's** (Plan 16): who starts a provider — systemd, launchd,
  an operator, nothing — is unsettled, and "the Manager only declares" argues for nothing.
  Softened by Plan 17's poll, which retries a server forever and picks it up whenever it
  appears, so nothing has to restart to notice; but nothing starts it either.
- **Remote WebRTC has no relay fallback** (Plan 09). Sessions are remotely reachable through an
  operator's proxy — the `/sessions/stream` registry drives the serving binding, and
  `YESSION_SESSION_URL` is a template over `{id}`/`{port}` (Plan 10) that threads the public
  address into open links and redirect URIs, whether the operator mirrors ports, gives each
  session a subdomain, or mounts each under a path — but the data channel still connects
  peer-to-peer on host candidates only (no STUN/TURN), so remote use needs a network where the
  session host's addresses route directly (e.g. an overlay like a tailnet, verified per
  deployment); an unauthenticated visitor can also hold open refused-at-`PeerHello` peer
  connections (no `/signal` throttling).
- **User- and local-scoped secrets have exactly one writer: the connection broker** (Plan 08).
  The Claude sign-in stores an owner-scoped credential through the narrow `ConnectionAction`
  policy family — under an attributed strategy that owner is the user; under `--auth localhost`
  it is `LocalScope`, the deployment itself ([ADR](decisions/2026-08-10-local-scope.md)). The
  GENERIC `/secrets` write surface for users is still absent — sessions still cannot
  `SetSecret` on `UserScope`, and there is no management-UI secrets page. The policy rows for a
  session-less, user-only `AuthzSubject` (`Session = None`) exist and are pinned by tests, but
  nothing constructs that subject yet.
- **Under `--auth localhost` a connected credential is deployment-wide.** Every visitor is
  the same unattributed subject, so one Claude/GitHub connection serves every session and
  browser that reaches the Manager, and every visitor's agent turn spends against it. Same
  boundary as the rest of that trust rule (they can already drive every session), and both
  exits are documented: `--secrets ephemeral` bounds the lifetime to the Manager process,
  `--auth trusted-headers` removes the sharing. Event ATTRIBUTION is unaffected — authors
  stay `PeerRef`.
- **Pre-`LocalScope` peer-scoped connection entries are never migrated.** They stay in
  `secrets.json`, encrypted and untouched, and are inert: `PeerScope` was dropped from the
  connection path entirely (a half-live entry would shadow the new one at turn time). No UI
  addresses them and no route deletes them; the operator connects once and moves on.
  `PeerScope` is unchanged for generic Plan 07 secrets.
- **No transport encryption guarantees beyond WebRTC/DTLS.** Everything binds
  127.0.0.1; loopback HTTP is the RFC 8252 pattern, but nothing here is LAN-safe
  without the operator's proxy in front.
- **The Manager and Process read plaintext** (per the stated Phase 1–2 threat model);
  command-to-container encryption is designed for but not implemented.
- **The management UI is gated by the authentication strategy** (Plan 07): a `Denied`
  outcome is a 401 on every UI route, and the default `--auth none` denies everything.
  Under `--auth localhost` anyone with local access can still manage sessions — that
  is the localhost trust rule working as stated, not an oversight.
- **Sandboxes confine by default; `host` is the opt-out.** Both sandboxes default to
  `srt` (bubblewrap on Linux, Seatbelt on macOS): agent-issued commands and the agent CLI
  are OS-confined unless an operator asks for something else. `YESSION_SESSION_WORK_BACKEND=host`
  is still there and still honest about what it is — no filesystem or network confinement,
  only the env allowlist (which the credential-leak regression test pins) — it just has to
  be chosen now. `=docker` remains the full-userland option for the WorkSandbox.
  - **A Linux host without the confinement tools cannot start a session.** srt needs
    bubblewrap, socat and ripgrep; the Nix installable names all three, but an
    `npm i -g yession` on a bare box does not have them, and the session fails when its
    sandbox is created rather than starting unconfined. That is the intended trade — the
    fix is to install them, or to choose `host` deliberately.
  - **srt reports a probe it could not RUN as a tool that is not there.** Its dependency
    check is not a stat: `whichSync` forks `which` — a shell script on a Debian-derived
    host, so two execs — under a one-second timeout, and every way that fork can fail (no
    `which` on this process's PATH, a box too busy to hand one out inside a second, EMFILE,
    ENOMEM) comes back as `ripgrep (<path>) not found`. Only ripgrep's named path goes
    through `which`; a named bwrap or socat is checked with `accessSync(X_OK)`, which is
    why a bad minute refuses every srt sandbox in a run on that one line, about a file
    sitting right there, executable. So a refusal is read against this process's own stat
    before it is believed: a start that settled nothing is asked again (three times, 250ms
    apart) and then forgotten rather than memoized into every later sandbox, and what it
    reports contradicts srt's sentence instead of repeating it. Undo when srt stats an
    absolute path — still `which` in 0.0.73 — or at least surfaces the spawn's errno.
  - **An unprivileged container needs `YESSION_NESTED_SANDBOX=weak`** (below), which is
    now on the default path rather than an opt-in one.
  - **An srt sandbox reads what its policy names and nothing else** (Plan 24). srt's read model
    is permissive by default, so denying only the invoking user's home — which is what this did
    until Plan 24 — left every region nobody had thought to name readable by every agent-issued
    command: `/etc`, a checkout the session was never given, and, when the Manager runs outside
    the operator's home, another session's data directory. The deny is now `/`, re-expanded by
    srt from the children of `/` at each spawn, with three holes: the policy's read paths,
    everything it may write, and the host runtime.
  - **The host runtime is the half of that scope this code cannot derive.** An interpreter
    can be anywhere, so the allow-back is a platform list (`SrtTools.Runtime`) plus the
    install prefix of whatever is already running — `process.execPath`, srt's own package
    (its wrapped argv execs a vendored helper from inside the sandbox), and
    `YESSION_BIN_CLAUDE`, `YESSION_BIN_GIT` — plus `YESSION_SESSION_READ` for a
    toolchain neither finds. That last one ADDS to the platform list rather than replacing
    it. A path the list is missing fails loudly and locally: a command cannot find its
    interpreter.
    - **The darwin list is unverified.** No job here executes a suite on darwin (pr.yaml's
      macos job builds the package and enters the dev shell), so `linuxRuntimePaths` is
      pinned by the `Srt` tier and `darwinRuntimePaths` is what a Seatbelt profile
      conventionally allows back, checked against srt's implementation and nothing else.
      It has already cost one release: with the read scope on, every git verb on macOS ran
      PATH's `/usr/bin/git`, which is not git but a shim that resolves a developer
      directory through a `/var/select` symlink — and srt's macOS escape hatch allows
      metadata on DIRECTORIES, so the symlink stayed denied. Sessions reported
      `xcode-select: error: unable to read data link … (Operation not permitted)`, which
      reads as a broken Xcode install and is not one. The fix was to stop asking the host
      which git to run (below), not to widen the scope for a shim.
  - **Every binary a confined spawn execs is NAMED; git was the last exception.**
    `YESSION_BIN_GIT` (the installable sets it on both platforms, unlike the Linux-only
    srt tools) joins `YESSION_BIN_BWRAP`, `YESSION_BIN_SOCAT`, `YESSION_BIN_RIPGREP`
    and `YESSION_BIN_CLAUDE`. Unset, the verbs still fall back to PATH so an off-Nix
    install does not regress — and an `npm i -g yession` on macOS is exactly where that
    fallback is wrong, so the git sandbox proves `git --version` before any verb runs one
    and refuses with a sentence naming `YESSION_BIN_GIT` and `YESSION_SESSION_READ`
    instead of passing the host binary's excuse through.
    - **That probe then refused a git that worked, because it was the one git spawn built
      without the hardened env.** `git --version` ran with an EMPTY env, which is an env no
      verb ever runs with: git resolves its global config path before it does anything at
      all, tolerates an `EACCES` there and treats every other errno as fatal, and Seatbelt
      answers `EPERM`. So on a macOS host whose operator has a `~/.config/git/config` — a
      home-manager install always does — the probe died `fatal: unable to access …
      (Operation not permitted)`, exit 128, and every repo verb was refused for the
      sandbox's whole lifetime in words blaming the binary and the read scope. Neither was
      at fault, and taking the sentence's advice (`YESSION_SESSION_READ=$HOME/.config
      /git`) would have widened the scope to hand back part of the home Plan 24 exists to
      deny. Every git spawned by the repo verbs is now built by one function that carries
      the env, so a probe cannot again gate verbs it does not run as.
    - **Linux cannot reproduce that class of fault, which is why it shipped.** srt denies a
      read on Linux by mounting emptiness over the path, so a denied config reads as ENOENT
      and git shrugs; Seatbelt denies it in place, and `EPERM` is fatal. Any suite that
      plants an unreadable file and expects git to fail is therefore green on the platform
      CI runs no matter which env the spawn carries. The regression test instead plants a
      MALFORMED config somewhere the sandbox may read, which fails identically on both
      platforms — it pins that no git spawned here reads the operator's global config, not
      the errno that made the difference visible.
    - **`/proc` and `/sys` are outside the scope by construction.** srt's root-deny
      expansion skips both (it remounts `/proc` itself, and a tmpfs over `/sys` breaks
      tooling for a tree that is read-only anyway). Neither is a route back to the denied
      paths — `/proc/<pid>/root` does not resolve to the host root from inside, and bwrap
      gives the sandbox its own pid namespace — but neither is scoped either.
    - **srt's own default write paths reach into the denied home.** `getDefaultWritePaths()`
      always allows `~/.npm/_logs` and `~/.claude/debug`, and srt re-binds an allowed write
      path that its read-deny tmpfs wiped — so both are readable AND writable from inside a
      sandbox whose policy names neither. The home they resolve against is the Session
      Process's `os.homedir()`, so the fix is that process's own `HOME`, not the policy.
- **The agent CLI runs through the `spawnClaudeCodeProcess` seam with a policy env**
  (AgentSandbox): allowlisted baseline + proxy passthrough, a per-session scratch HOME
  (`<data>/agent-home` — `~/.claude` state lives and dies with the session), exactly one
  credential, and a process-group kill on the SDK's forwarded abort signal.
  srt — the default — adds OS confinement around it: the CLI reads and writes its scratch
  HOME and reads the host runtime, and nothing else of the operator's files; and it reaches
  only `AgentSandbox`'s domains (`YESSION_SESSION_AGENT_NET`). `YESSION_SESSION_AGENT_BACKEND=host` opts out, leaving the file
  system and network open to the CLI. Docker is BY DESIGN not an agent backend: a container
  per session boot is the opposite of the sub-second start the agent needs, and the
  WorkSandbox keeps it.
  - The SDK's spawn seam is SYNCHRONOUS and srt's wrap is not, so the srt tier hands the
    SDK a stand-in process whose streams are live immediately and joins the real child to
    them when the wrap resolves. It is plumbing, not policy, and the `Srt` suite drives it
    end to end (stdin in, stdout out, exit code) rather than trusting it.
- **srt's egress allowlist is per PROCESS, not per sandbox.** `SandboxManager` is a
  singleton with one filtering proxy pair, so a session whose AgentSandbox and WorkSandbox
  are both srt confines their FILES exactly (the profile rides each spawn) but can only
  UNION their allowlists — the work sandbox can reach the agent's API hosts, without any
  credential for them. Splitting it needs either a manager instance per sandbox (srt does
  not offer one) or a Session Process per sandbox.
- **The strict confinement profile needs a nested user namespace, which an unprivileged
  container refuses.** srt's seccomp helper creates one inside bubblewrap's to drop
  capabilities and mount a fresh `/proc`; Docker's default (and this repo's dev container)
  denies it. `YESSION_NESTED_SANDBOX=weak` is srt's documented answer — the host's `/proc`
  stays visible and capabilities are not dropped, so it is genuinely weaker confinement,
  and it is therefore CONFIGURED rather than fallen back to: an unset value means strict,
  and a session on a host that cannot host it fails at boot instead of quietly running
  wide open. `check` probes whichever profile the run configures before declaring the
  `Srt` capability.
- **The Docker backend runs through the `dockerode` SDK and is integration-tested in the
  verify gate.** Containers and a per-sandbox named workspace volume are named by the
  session id (a Crockford base32 id, always a valid Docker object name), and `EnvironmentSpec`
  is fully interpreted — image/build, mounts (incl. the persistent workspace volume),
  working directory, env-var refs, and secret refs (resolved at sandbox spawn). The
  container drops all capabilities and sets `no-new-privileges`. It runs the image's
  default (usually root) user, and that is a DECISION rather than an omission: without
  `CAP_DAC_OVERRIDE` that root does not bypass file permissions — the mount suite proved
  it by failing to write into a `0700` host directory — so the main thing a non-root user
  buys is already bought, while running as one breaks the named workspace volume Docker
  creates root-owned. What remains is that files written through a BIND mount are owned by
  root on the host, which is a nuisance rather than an escape. Resource limits are
  likewise absent, as they are for every backend. The suite (`tests/Yession.Tests/DockerIntegration.fs`) runs
  where a daemon exists; asking for the capability requires it, so a `verify` on a
  daemon-less runner fails rather than skipping. The dev container has no daemon, so
  `check Docker` refuses to start there — run a tier that does not ask for it.
- **Secrets are a real Manager-owned store now** (Plan 06): AES-256-GCM per-entry ciphertext in
  `<DataDir>/secrets.json`, the KEK in the OS credential manager (`@napi-rs/keyring`, imported
  non-extractably each start), a `/control/secrets/*` surface whose only value-returning
  route is `resolve` (below), a pure default-deny `Policy.authorize` over the composite
  session+user+peer identity, and store-backed `SecretRef` injection (session scope ▸ bound
  users' scopes ▸ witnessed peers' scopes ▸ Manager process env — peers per Plan 07).
  Remaining, deliberate:
  - **Hosts without a credential manager run in-memory only** (dev containers, CI,
    headless servers): secrets die with the Manager; loud at boot, never a plaintext
    key file. In-memory is now also an operator CHOICE (`--secrets ephemeral`, recorded
    at info rather than warn), and `--secrets durable` refuses the boot on such a host
    rather than degrading to it. (Tests cover the real keyring via the `Keyring`
    capability — `check Keyring` self-wraps with dbus + gnome-keyring when headless.)
  - **No AMBIENT scope.** `LocalScope` is deployment-wide but not ambient — it is
    authorized against unattributed access the Manager recorded for that launch, and it
    holds connection credentials only (generic `SecretAction`s on it deny). User-scoped
    GENERIC secrets still have no writer (the Plan 08 connection broker writes only its
    own credential entries, through its own policy family). User↔launch, peer↔launch and
    local↔launch bindings are all launch-lifetime (re-login re-forms them).
  - **Two deliberate value-returning routes, both resolve-shaped**:
    `/control/connections/resolve` (Plan 08) returns a connection credential to the
    calling session (an agent turn needs the token in-process), and
    `/control/secrets/resolve` feeds `SecretRef` env injection at the session's
    sandbox spawn (sandboxes are session-owned, so injection happens there). Both are
    gated to the caller's readable scopes; refresh tokens still never leave the
    Manager, and no agent-facing tool wraps either.
  - **Secret-token-injection is future work.** A resolved secret enters the sandbox
    as a plain env var, so anything the agent runs inside can read it. Under the srt
    backend the enforced egress allowlist now bounds where a read value can be *sent*,
    which is most of the practical risk. The real fix is egress substitution — a
    placeholder inside the sandbox, the real value substituted at the proxy toward
    declared hosts — and srt implements exactly that (`credentials.envVars` with
    `mode: "mask"` and `injectHosts`), so what remains is wiring `SecretRef` injection
    through it instead of through the policy env.
  - **No KEK rotation/recovery** (a lost credential entry orphans the store loudly;
    the operator deletes the file), and multi-user same-name injection precedence is
    unresolved until a real multi-user strategy lands.
  - `@napi-rs/keyring`'s per-platform prebuilds join `node-datachannel` in the
    "unsigned third-party native binaries" trust bucket below; macOS/Windows paths
    are field-verified only (CI exercises Linux/Secret Service).

## Runtime & topology

- **The process split is done** (Phase 4): each session is a child OS process of the Manager,
  supervision and secrets custody cross the boundary as a secret-scoped control RPC
  (environments are session-owned via the sandbox seam), and sessions are
  created/launched/resumed/stopped from the htmx management UI — but **children die with the
  Manager** (no daemonising, no orphan adoption): a Manager restart stops every running
  session. Relaunching one is no longer a manual click: `GET /sessions/{id}/open` launches a
  stopped session and lands on its address (Plan 11), and the client offers that route when the
  session it was talking to has gone.
- **The Manager is practically a singleton.** Nothing global is assumed (per-instance
  data directories, OS-assigned session ports), but two Managers over the SAME data
  directory are unsupported — there is no lock until the SQLite move — and the
  management UI's fixed default port (8321) means a second instance must configure its
  own.
- **Session ports are OS-assigned and change on every launch.** A session has one stable URL —
  `/sessions/{id}/open` — but the address it lands on is only stable where
  `YESSION_SESSION_URL` derives it from `{id}` (Plan 12). On the zero-config loopback default
  the origin moves with the port, so everything the browser kept for that session is left
  behind — the document's IndexedDB store, the Cache API history store, and one transcript
  store per terminal, all partitioned by origin. The client says so instead of promising
  otherwise (`PublicAccess.sessionAddressIsStable`), and that is the whole remedy — nothing
  migrates the stranded stores.
- **Health is a liveness report, not a health check.** A launch reports busy/idle on
  `POST /control/activity` and the Manager reaps on silence — but only when an operator
  sets an idle timeout (`IdleTimeout` is `None` by default). Unset, a child that wedges
  after readiness still shows as running until it exits; set, it is stopped as
  `NeverReported` rather than diagnosed.
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
- **A second reverse leg — the MCP server set — is shipped end to end** (Plan 17): the child
  subscribes to `GET /control/mcp` (SSE), gets its session's resolved set immediately and a
  fresh set on every change, and `ProcessManager.publishMcpServers` is the producer — from
  the operator's durable declarations, republished on every registry write and seeded at
  boot. It is per SESSION, not Manager-global: the hub is keyed (`KeyedRetainedHub`), so a
  session that is not up yet finds its set waiting. The child holds the set
  (`app/SessionMain.fs`) and `McpClient` connects it, so a declared server's tools reach
  agent turns.
- **A third reverse leg — connection statuses — has a real producer** (Plan 08):
  `GET /control/connections` streams each launch its readable connection metadata (snapshot on
  subscribe, fresh list on every credential change or new binding), and the session's agent
  gate and `/claude` status surface consume it. The hub mechanism is generic
  (`NotificationHub<'n>` for this leg and the notification one; `KeyedRetainedHub` where a
  leg retains a value per session, as the MCP one does).
- **Peer-to-peer is star-shaped through the Process.** Clients sync Yjs state via the
  Session Process relay, not directly with each other; y-webrtc-style meshes are not
  used.

## Persistence & data

- **Everything durable is now persisted** (Phase 3): the event log and the Yjs
  document both survive Process restarts (sidecar `*.doc.jsonl`, compacted at open),
  and browser clients keep the document in IndexedDB (`y-indexeddb`, keyed by the
  session id embedded in the bootstrap page). The event log's browser-side cache is the
  client's own, not the browser's HTTP cache: a client asks from the position it has folded
  through (`GET /events/after/{n}`) and the server redirects to the range it minted
  (`GET /events/{first}-{last}`), whose bounds never move, so its bytes are the same for
  ever. Every response is `no-store` — a second copy in the HTTP cache would be a spare
  nobody reads — and the client keeps the ranges it was given in the Cache API under a name
  derived from the session id, so a cold load replays what it kept and only the tail hits
  the network.
- **The JSONL event log loads fully into memory** and has no compaction, rotation, or
  checksumming; a corrupt line fails the whole open (loud by design). The doc store
  compacts only at open — a very long-lived Process grows its sidecar until restart.
- **Event ranges are cookie-gated for browsers; headless clients still put a minted
  peer token in the range URL** (`?token=`). The browser path is clean (the same-origin
  auth cookie rides each fetch, so URLs — and the cache keys the client stores them under —
  carry no secrets); the token-in-URL path remains for Node clients and tests, scoped to
  per-process minted tokens that die with the session.
- **A session opens cold with no network** (Plan 20): a service worker at the mount keeps
  the shell (network-first, since it names the fingerprinted assets) and this build's assets
  (cache-first, since their address pins their bytes), and nothing else — the event log is
  the page's own cache, and `/me`, `/signal` and `/queries` are liveness questions a cached
  answer would answer wrongly. It needs a secure context, so a session served over plain
  HTTP at a non-loopback address still cannot: there the settings pane names the missing
  capability and the flag that restores it.
- **Nothing measures whether the history store holds.** `storage.persist()` is a request,
  not a guarantee — granted for an engaged site on Chrome, essentially only for an installed
  app on Safari, which additionally caps script-writable storage at seven days without user
  interaction. So the exposure is the session nobody has opened in a week, which is also the
  one somebody most wants back. A walk knows how many answers it served locally and how many
  went to the network, and that ratio is the number that would settle it; it is not on the
  OTel resource yet, so the decision to replace the Cache API store with an IndexedDB one
  keyed by offset has no evidence to rest on.
- **Nothing drops a session's kept history when the session is gone.** Each session names
  its own caches (`yession/session/<id>/events`, and one per terminal), and a deleted or
  reaped session leaves them behind for ever. The names are the remedy rather than the
  problem: `caches.keys()` makes a sweep a filter over the sessions the Manager says still
  exist, not an archaeology dig — but no surface does it yet.
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
  local doc. Recovery is no longer only a hint away (Plan 20): the session leg is driven by
  one supervised loop, capped at a minute, poked by the browser's `online` event and by a
  *Try again* control on the status line — and a REFUSED peer, which no schedule can help
  because the same token would be refused again, parks until somebody asks rather than
  ending. That control is the affordance this entry used to say was missing.

## Browser client

- **Rendering is Fable.Lit (lit-html), a reconciling renderer.** The view is a total
  function of the model, rendered into `#app` on every change, and Lit diffs the DOM so
  focus and caret survive re-renders with no manual restore hack (this replaced the old
  innerHTML-replacement approach). The only remaining manual DOM work is pinning the chat
  scroll and pixel-positioning collaborators' cursor markers (a native `<input>` exposes no
  per-character geometry).
- **One WIP draft per client, co-editable by any peer** (Plan 03): drafts are keyed by author
  (`Map<PeerId, DraftState>`), so each client owns at most one — structurally, not by a runtime
  cap. Any peer may co-edit any slot, and any co-editor may send it: the entry is attributed to
  the draft's AUTHOR, and the key the author minted (`QueueId`, carried by the slot since it
  was published) is what makes two concurrent sends one entry rather than two. The queue is
  untouched (send clears the slot, so a client still queues many by sending repeatedly). Drafts
  and queued messages are now **rich ProseMirror editors** on a Yjs `XmlFragment` (markdown
  typing, bold/italic/code, lists, paste-as-markdown, undo/redo) — not textareas, not plain
  text — and **collaborative presence cursors** overlay every collaborative field (the title
  input and the body editors), showing each peer's caret and selection with a colour + name
  label, relayed over ephemeral `Presence` frames (never durable). Invariant 4 (clean send) has
  a dedicated Hedgehog property; broader draft-op schedules (participation, offline rejoin) are
  the follow-up.
- **A 401 from `/me` renavigates to `/login` unconditionally** — there is no in-app
  "signed out" state; the client simply rides the OIDC bounce again. (The other axis is
  handled: an unreachable session keeps the cached shell local-first and, once the
  disconnection settles, offers the `/sessions/{id}/open` card rather than a status word.)
- **No browser support matrix**: verified on Chromium (headless, in CI); the ICE
  gathering settle-fallback should cover Safari/Firefox mDNS behaviour, but they are
  untested.

## Agent

- **The live agent's tool results are text renderings** of the typed capability
  results; there is no structured tool-result schema. The agent can read back what its
  own terminal commands did (`read_terminal_block`, plus the digest on the context pack
  — Plan 13 stages 3a/3b), but there is still no tool for reading session history beyond
  the prompt transcript.
- **The context pack is a flat transcript** rebuilt per turn from the full projection —
  no windowing, summarisation, or token budgeting. Bodies are now Markdown (rich text
  landed), but the transcript is still a naive `author: body` join with no multi-line
  handling, so long sessions or large rich bodies will eventually overflow the model context.
  Only the terminal digest is bounded (an output tail, with the elided count stated).
- **Turn discipline is done** (Phase 3): single-flight is enforced by the queue drain,
  interrupt is explicit, and the invariants are property-tested — but the queue has
  **no size cap**, a drain coalesces any backlog into ONE turn (no per-message turns
  option), and the queued-message UI has no "locked" visual during the drain broadcast
  window (a peer can briefly type into an entry that is about to vanish — the edit is
  safely discarded, but the UX flickers).
- **The Node suite's timeout is one budget for every tier.** `tasks.fsx` gives the whole Node
  run 240s, so a per-case deadline is spent out of a pool every other suite is drawing on: a
  case that legitimately needs two minutes cannot have them without risking the runner killing
  the run before any suite reports. That is not hypothetical — it is how the first live clone
  case failed, and the reason the one that ships is capped at 90s (ten times its real 6.9s)
  rather than at something comfortable. A tier that spawns real processes and a tier of pure
  folds want different budgets; they have one.
- **A turn has no step ceiling, and nothing automatic bounds a runaway one.** `maxTurns` is
  OPTIONAL on the Agent SDK's `query()`, and unset means no cap — which is what `Agent.fs`
  now passes, the same setting interactive Claude Code runs under. The ceiling that used to
  be here (32 model turns) was a bound that ENDED the turn as a failure, and an errand long
  enough to hit it was the errand least well served by being cut off mid-way.

  What replaces it is only the interrupt. A turn spends the TURN HUMAN's credential in a
  session other people can watch and nobody need be watching, so a bound that requires
  somebody present is no bound at all for an unattended session. Missing: a budget the model
  can see, any signal that a turn is running long, and any cost ceiling per turn or session.
- **A wake can start a turn nobody asked for, and wake→turn→command→wake is a legitimate
  loop.** A background command finishing makes a turn due; that turn may background another
  command. The loop is bounded only by visibility — every woken turn carries its reason
  durably on `AgentTurnStarted.Woke` and shows it in the chat — and by the human interrupt,
  which is no bound at all for a session nobody is watching. A per-session woken-turn budget
  is the next dial and is deliberately not built in advance.
- **Repo integration is the read-only bootstrap slice** (Plan 14): typed clone-and-orient verbs
  beside the agent, one repos dir shared into the WorkSandbox, GitHub sign-in per user over the
  device flow. Remaining, deliberate:
  - **A session's repos are session-readable, and bytes outlive revocation.** One
    user's private repo, once added, is readable by every peer and everything in the
    WorkSandbox — the same shared-trust boundary as "terminal access equals session
    access". The `RepoAdded` event names who brought it in; GitHub-side revocation
    does not claw back what is already on disk.
  - **A pasted PAT bypasses the App-installation scope rule.** The device-flow token
    is a GitHub App user-to-server token, so it can only reach repos where the App is
    installed; a pasted `github_pat_`/`ghp_` answers to no such bound.
  - **A GitHub token rotates only if the App expires it** (Plan 21): a device-flow grant is
    stored as a grant now and the Manager refreshes it on use, but an App registered with
    user-token expiration disabled still yields a permanent token, and revocation is at GitHub
    either way. Nothing tells an operator which of the two they have registered.
  - **`git push` in the `default` WorkSandbox terminal has no forwarded credential.**
    Forwarding itself shipped as `start_work_sandbox`'s `forward` argument
    (`app/WorkSandboxes.fs`), so a sandbox the agent asked for can carry `github` — but
    `default` is the one nobody asks for, and it is created with `Forwarded = []`. Its
    terminals do local git only until somebody starts a named sandbox. Commit/push
    attribution machinery (author = requesting user, `Co-Authored-By`) is absent
    everywhere, forwarded credential or not.
  - **Under `YESSION_SESSION_AGENT_BACKEND=host` the git verbs run unconfined** — the
    operator's explicitly lax choice, as everywhere `host` is chosen. The per-invocation
    hardening (hooks/fsmonitor/ext off, no global config, protocol pinned) still
    applies; the filesystem and egress boundaries do not.
  - **Every srt sandbox may write a checkout's `.git/config`.** srt denies that write by
    default; a `git clone` makes it, so the flag is on. It cannot be scoped to the git
    sandbox: srt reads it from the session config that whichever sandbox came up first
    initialized the process-wide manager with, and ignores the per-spawn one. So the
    WorkSandbox and the
    agent can write a `.git/config` too — planting a `core.fsmonitor`, an alias, or a
    pager that runs when git next runs in that checkout. Inside the session that is the
    shared-trust boundary already stated above, and the verbs themselves are immune (the
    per-invocation `GIT_CONFIG_*` hardening wins over any repo config); OUTSIDE it, a
    human running git in the checkout on their own host is not. `.git/hooks` — the other
    half of the same vector — stays denied.
  - **The clone verb runs with no filesystem confinement.** srt refuses writes to
    `.vscode`, `.idea`, `.claude/commands|agents`, `.mcp.json`, `.gitmodules`, `.git/hooks`
    and the shell rc names WHEREVER they appear, and no allow-path outranks that refusal —
    so a confined process cannot materialize a checkout containing any of them, and srt
    scopes the exemption per SPAWN, never per path. So `add_repo`'s clone has its own
    sandbox with srt's filesystem rules off: for that one command git can read and write
    whatever the session's user can, including the credential files srt would otherwise
    mask. `filesystem.disabled` drops the READ policy with the write one, so the read scope
    above does not reach this spawn either. Egress stays pinned to github.com, the env stays the hardened one, and every
    other verb keeps the confined policy — none of them writes a path srt objects to.
    macOS enforces the refusal as patterns and Linux as a scan of what already exists, so
    a Linux clone would have been fine confined; it is exempt there too rather than ship a
    production path no CI here exercises. Both of these go away the day srt can exempt a
    subtree rather than a spawn, or reads `allowGitConfig` per spawn — the clone takes the
    ordinary confined policy again and `FilesystemConfinement` loses its only caller.
  - **A docker terminal does not open on the checkouts.** Under the host-family backends
    the repos directory sits inside the workspace a terminal starts in
    (`Sandboxes.SessionLayout`), so the agent's first `ls` shows what it cloned. The docker
    backend cannot: its workspace is whatever the image's working directory is — this
    session composes no `WorkingDirectory`, so nothing here knows the path — and the repos
    dir arrives as a bind mount at `/repos` beside it. A verb still ANSWERS with the right
    path (`reposVisibleAt`), so nothing is unreachable; it is one `cd` the other backends
    no longer need.
  - **`.yession.yml` is still unconsumed**: the bootstrap files land in the checkout,
    and nothing reads them into the environment spec yet — that is the follow-up plan.
- **The session's imperative API is split, and only half of it is built** (Plan 15): commands
  mutate and belong to the agent alone; queries read and are declared once, reaching the agent
  as generated MCP tools (`readOnlyHint`) and the humans as a generated settings surface fed by
  one multiplexed SSE stream. Stage 1 shipped, which retired the Repos panel's add/remove/
  switch controls and the `/repos*` routes. Remaining, deliberate:
  - **Every session member reads every query.** There is no per-query authorization —
    the same stance the timeline already takes, where every member reads every
    attributed act-line. A query that should not be session-wide has nowhere to hide
    yet.
  - **The shipped classifier approves everything.** Every terminal block and every structured
    command passes the classifier (`Classify.fs`, Plan 23) on its way to happening, but the
    only implementation is `Classifier.approveAll` — so until an AI-driven classifier lands,
    nothing stands between an agent turn and any command except the work sandbox's confinement.
    Manual approval was removed deliberately, not lost: the queue stays visible and editable,
    refusals stay recorded and attributed, and the seam is where the real classifier plugs in.
  - **A forwarded credential lives in a sandbox's env for that sandbox's lifetime** (Plan 15
    stage 2), readable by everyone in the session and by everything running in it — the same
    shared trust boundary Plan 14 states. Revoking at the provider does not claw back what was
    injected; `stop_work_sandbox` is what removes it. Only `github` is forwardable so far. Now
    that such a token can EXPIRE (Plan 21), the same freeze cuts the other way: a refreshed
    token never reaches a sandbox already running, so terminal git in one older than the
    token's life starts failing auth and a new sandbox is the fix. Withholding refreshable
    credentials instead would break terminal git for everyone today to fix it for the
    long-lived case.
  - **An external MCP server's read-only tools are not queries yet.** `readOnlyHint` is
    declared, not inferred, precisely so a third-party server's queries could be listed into
    the registry without a yession-specific convention — but only the in-process
    registrations reach it. What is in the way is the rendering: a foreign tool's answer has
    an arbitrary JSON Schema, and the generated surface draws rows, fields and a value. A
    JSON-Schema-subset renderer is the missing piece.
  - **A person cannot mint a stream without the agent.** Someone who wants a device terminal
    before the agent has opened one has to ask the agent for it. Yession cannot offer a
    button: the tool that mints a stream offer has a name and an argument schema learned at
    runtime, and the product has no opinion about either. The general answer is a human
    surface for invoking a declared tool — a form generated from its JSON Schema, plus an
    authorization story for a person calling a foreign tool directly.
- **Per-user agent credentials landed** (Plan 08): a human signs into their Claude account from
  the session's Connections panel — "this session only" (`SessionScope`) or "all my sessions"
  (their user/peer scope) — the Manager brokers the OAuth exchange as pure standards (it never
  learns the provider), and each agent turn runs on the TURN ACTOR's credential (session-scoped
  ▸ actor's own ▸ ambient env), resolved fresh per turn with Manager-side lazy refresh.
  Remaining, deliberate: the ambient `ANTHROPIC_API_KEY`/`CLAUDE_CODE_OAUTH_TOKEN` process env
  stays as the documented last resort (it is how CI's LiveAgent tier feeds the agent, and it
  applies to ANY actor); a refresh failure surfaces only as the turn's failure (no panel-level
  health indicator); the panel's status is polled by the browser (the SESSION learns of changes
  live over its control stream, the open browser tab re-asks).
- **Live-path verification is credential-gated by design**, and asking for it now
  requires it: `verify` declares the `LiveAgent` capability, so a run without
  `ANTHROPIC_API_KEY`/`CLAUDE_CODE_OAUTH_TOKEN` fails rather than skipping quietly (which
  is how every release up to `v5.0.0-beta.0` shipped with the live suite silently
  skipped). A cheap-tier run simply never asks. `YESSION_BIN_CLAUDE` matters in sandboxes
  that kill the SDK's vendored binary.

  What that gate does NOT cover is the credential going missing after it passes. It is
  probed once, in the parent, before the suite starts; the suites read it out of the process
  env, and the suite is one process, so anything that mutates that env changes what every
  later suite is testing. That happened: `Phase2`'s credential-leak regression planted a key
  and DELETED it on the way out, and the live clone case — compiled after it — got a session
  with no credential. `SessionMain` answers no credential by starting **no agent at all**, so
  the turn produced no reply, no error, and nothing anywhere said why. `Support.withEnv` (one
  verb: take and give back, absence included) closes the known cause, and three cheap-tier
  cases pin it. The CLASS is still open: nothing re-checks a declared capability at the point
  a suite uses it, so the next mutation of process-wide state is silent in the same way.

## Delivery & operations

- **Node is a required runtime.** Yession ships as one npm package with two bins
  (`yession-manager`, `yession-session`); `npm i -g yession-*.tgz` pulls the platform-native
  deps — `node-datachannel`'s addon AND the SDK's native `claude` — via npm's optional
  dependencies, so install is all it takes and the agent works offline afterward. But
  there is no self-contained binary anymore: a machine without Node ≥24 can't run it from
  npm. (The Nix installable wraps `nodejs_24`, so that route brings its own — at the cost
  of needing Nix.)
- **First install downloads the native `claude`** (~240 MB, platform-specific): it is
  not in the 300 KB tarball, npm fetches it. So the *first* install needs network and
  disk; the SDK's own resolution finds it thereafter (no `YESSION_BIN_CLAUDE` needed).
- **The composition E2E and install smoke run on Linux/CI**; other platforms' npm
  resolution rides npm's own optional-dependency machinery, unverified per-commit.
- **No per-platform build matrix for the npm route, no code signing.** Release CI ships one
  platform-neutral npm tarball and a Nix package, both from `ubuntu-latest`; the
  platform-native pieces are resolved by npm's `optionalDependencies` on whichever machine
  runs `npm install`, not built by Yession (this replaced the earlier SEA per-platform
  binaries — see Step 26→28). Yession therefore has no compiled binary of its own to sign
  or notarise; the native `claude` and `node-datachannel` addon npm pulls in are unsigned
  third-party downloads that may still trip macOS Gatekeeper. Darwin has one foothold — the
  PR gate builds the flake package on `macos-latest`, enters the dev shell, and loads the
  native WebRTC addon there — but the npm INSTALL path on darwin, and everything on
  Windows, is exercised only by the Linux install-smoke.
- **Telemetry is agent-turn usage plus Manager audit records** (Plans 04 + 06): each completed
  turn emits one OpenTelemetry **log record** — the token/cache counts plus session/turn/model
  ids, never message content. Every process (Manager and each session) is a **direct OTel
  emitter**; there is no Manager-side collector. Destination is chosen by the standard OTEL_*
  env the Manager is started with (`OTEL_LOGS_EXPORTER=console|otlp|none`, comma-separated for
  a stdout+collector tee; `OTEL_EXPORTER_OTLP_*` for the collector) and passed through to each
  child, whose identity the Manager adapts. Default `console` (stdout); no collector configured
  ⇒ forwarding is dropped. Separately, the Manager emits its own in-process `yession.*` **audit
  records** for the secrets/ABAC surface (ops, denies, injection, KEK/store lifecycle,
  user↔launch bindings, control 401s — see Plan 06 § Telemetry), one greppable stdout line
  each. Still **no metrics pipeline, no traces** (the emitter path generalises to both — same
  env selection, no collector to touch), **audit records not yet forwarded to a collector**,
  and **no structured app logging or crash reporting** beyond stdout.
- **Multi-node operation and work-intake integrations (Slack/Linear)** remain out of
  scope, as planned. (Terminals landed as Plan 13 and remote access as Plans 09/10/12 —
  what is still out of scope there is a session that runs on a machine other than its
  Manager's.)

## Testing debt

- Browser E2E runs on Chromium only, and drives one host platform per CI run.
- The Yjs relay trusts ordered delivery per data channel; the Phase 3 property
  schedules cover arbitrary *delivery timing* (staleness, partitions, restarts) but
  not corrupted/duplicated *frames* on the wire.
- The vendored Hedgehog does no shrinking: a failing property prints the whole
  schedule, not a minimal one.
- Load/scale characteristics (many peers, large logs, long drafts) are unmeasured.
- **The serial engine is tested against a tty, never against a chip** (Plan 16, part E).
  `check Serial` drives `Ports.real` over a socat PTY pair, so the open, the line settings, the
  read and write paths and the vanish path all cross a real kernel tty. What no CI box can
  cover is a specific adapter: a baud rate the driver silently rounds, a chip that needs
  DTR/RTS toggled to come out of reset, a USB stack that reuses `/dev/ttyUSB0` for a different
  device after a replug. Those are found on hardware or not at all.

## Terminals

- **A queued command whose terminal closes stays queued for ever, and is now unreachable.**
  Nothing runs it and nothing removes it — deliberately non-destructive rather than silently
  dropping someone's text. But a closed terminal renders its recording instead of its
  composer (stage 3e), so the entry that was at least visible and deletable before is
  neither now: it sits in the doc with no surface at all.
- **Terminal access equals session access.** A terminal can read the sandbox's environment
  (`env`), which after resolve-at-spawn includes secrets the session's spec references.
  This is not a new privilege — any peer could already ask the agent to run `env` — but a
  terminal makes it one keystroke, and a future per-user terminal gate would attach here.
- **A screen that showed a secret is in the recording, permanently, one tap from the chat.**
  Keystrokes are deliberately not captured (`SessionProcess/Terminals.fs`, `Input`) because
  live mode makes typing a password ordinary — but output is, and the replay work both
  removed the age at which a closed transcript was deleted and put a tappable chip on every
  block and lease stretch. So the distance between a secret and a casual reader is now one
  tap, for every peer in the session, for as long as the session's data exists. Nothing here
  is newly privileged — terminal access already equals session access — but anything that
  widens who may read a session (a link scope, a public view, an export) widens this with
  it.
- **`renewable` is a provider's claim about its own tool, and nothing verifies it** (Plan 19,
  step 4). A closed stream offers a way back when the provider said asking again is safe;
  pressing it replays that tool call with its original arguments. A provider that marks a
  destructive tool renewable makes the button destructive. Default false, and the field is
  documented as a promise — the same standing `SourceCapabilities` already has, and
  unverifiable for the same reason: only the thing on the other end knows.
- **A tool-use chip does not point at the terminal its call opened** (Plan 19).
  `ToolUseFinished` carries `Block` for exactly this reason in the block case; the stream case
  has no equivalent, so the audit says a call happened and the timeline says a terminal
  appeared, and nothing joins them. A `Terminal : TerminalId option` beside `Block` is the
  obvious symmetry and was deliberately not smuggled into the step that would have needed it.
- **The jumpstarter console has an echo floor of one quiet period** (~50ms measured at 61-67ms
  round trip; `QUIET_SECONDS` in `examples/jumpstarter`). Its stream is a drain loop over a
  `pexpect` handle rather than ownership of the fd, so a person typing sees their own echo that
  much late. The serial provider, which owns its fd, has no such floor.

  This entry used to say the floor was one DRAIN interval (~200ms) and was inherent to teeing a
  handle the SDK owns. Both halves were wrong, and the reason is worth keeping: the read under
  the drain was `console.expect([pexpect.TIMEOUT])`, which always waits its timeout out and —
  because pexpect consumes what it MATCHED and a timeout matches nothing — returned the whole
  buffer every time without ever clearing it. So the floor was the timeout rather than the
  device, and the stream re-sent the console's entire history five times a second. Now the
  drain matches a real pattern, returns on the first byte, and waits only long enough not to
  split a line. What is left is a coalescing window we chose, not a cost of teeing.
- **An agent holds a lease where its own block has taken the screen, and nowhere else it could
  have run a block instead.** Closed in two steps, and what is left is a boundary rather than a
  shortfall. Plan 19 step 3 was the first: a live-only source has no blocks, so
  `execute_command` has nothing to do there and the alternative was the provider's own write
  tool, past the lease entirely. `write_terminal` takes the lease exactly as a peer does —
  visible in the holder field, stealable back mid-sentence, every byte in the transcript.
  Plan 20 stage 6a was the second, and it was a bug rather than a policy: the alt-screen flip
  refused to hand an agent-authored block its terminal, so a full-screen program waited for a
  keystroke nobody was allowed to send, its block never finished, and every command queued
  behind it stalled for ever. The flip now follows the AUTHOR, agent included. The boundary
  that remains is that the agent cannot TAKE an instrumented terminal — it may only use the one
  detection hands it, over a block already classified and on the record — because taking it
  would be the door around the classifier that `execute_command` is the one door for (Plan 23).
  The drain gate that accompanies live mode (Plan 13, stage 2e) is unchanged — a leased
  terminal holds its queue rather than typing into a session someone else owns.
- **A block waiting on a keystroke is announced to the agent and to nobody else.**
  `execute_command` answers `TerminalCommandInteractive` the moment detection hands the
  terminal over, and the agent has `write_terminal`/`read_terminal` to resolve it — but if it
  does not, the chat shows only a pulsing chip. The affordance a person needs is the terminal
  panel's ordinary lease bar, which they have to go and find. A handoff card in the chat was
  designed and not built.
- **The live viewport is proven host-free, never against a real pty end to end** (Plan 14,
  stage 6 — which closed the older "no browser viewport" gap: the panel renders a live screen,
  the holder's copy takes keystrokes, and the client composes it with the same emulator the
  Session Process uses). What no suite drives is the whole loop at once. Stage 6's own note
  says so: the keystroke translation is answered host-free under `Browser`, because a
  `KeyboardEvent` is the part only a real browser can answer, and the Process half is pinned
  separately in the pty suite. Two peers sharing one real pty, one of them typing into it, is
  covered by neither end.
