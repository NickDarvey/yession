module Yession.Host.Main

// The Manager entry point (`start`; the `yession` binary). The Manager is a
// process supervisor + management surface: sessions run as CHILD OS PROCESSES
// (`yession-session`; in development, node over the Fable output), so a crashing
// session never takes the Manager down.
//
// What this Manager DECIDES is said on its command line; what it PASSES DOWN stays in the
// environment, because inheritance is how a child gets it. So the addresses this deployment
// answers at (`YESSION_MANAGER_URL`, `YESSION_SESSION_URL`), the per-session policy
// (`YESSION_SESSION_*`) and the binaries a session runs (`YESSION_BIN_*`) are read from the
// environment here and by the session alike, while the port, the data directory, the idle
// window, the default session and the spawn command are options — nothing downstream reads
// them, and an option that is mistyped is refused where a variable that is mistyped is
// ignored.
//
// For product continuity a default session is ensured and launched at boot; creating,
// launching, resuming, and stopping further sessions arrives with the management UI
// (Step 25).

open Yession.Domain
open Yession.Host

// What this bin accepts, declared once. `--version` and `--help` answer from here before
// any configuration is read — no data directory, no ports, no sessions launched — and an
// unknown option or a missing value stops the boot with the reason and the usage, rather
// than being ignored into a deny-everything Manager.
let private authOption =
    Cli.value "auth" "rule" "how a request's subject is established: none, localhost, trusted-headers"

let private secretsOption =
    Cli.value "secrets" "mode" "whether secrets persist across restarts: durable, ephemeral"

let private portOption =
    Cli.value "port" "port" "the port the Manager listens on; 0 lets the OS choose (default 8321)"

let private dataDirOption =
    Cli.value "data-dir" "path" "where this Manager keeps its state (default .yession)"

let private idleTimeoutOption =
    Cli.value "idle-timeout" "window" "stop a session unused for this long: 90s, 30m, 2h (default never)"

let private defaultSessionOption =
    Cli.value "default-session" "id" "the session ensured and launched at boot (default local-session)"

let private spawnBinOption =
    Cli.value "spawn-bin" "command" "the command that runs a session (default this Node on the packaged entry)"

let private cli =
    Cli.spec
        "yession-manager"
        [ authOption; secretsOption; portOption; dataDirOption; idleTimeoutOption
          defaultSessionOption; spawnBinOption ]

let private args = Cli.parseOrExit cli Version.current

// Before anything is read: is the environment still setting something that moved onto the
// command line above? Refused, and every one of them named at once — see Retirements for
// why a moved setting must never be merely ignored.
match Retirements.found Retirements.manager (fun name -> Interop.envOr name "") with
| [] -> ()
| stale -> Cli.rejectValue cli (Retirements.complaint stale)

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

// The session this Manager creates and launches at boot. An OPERATOR variable, despite
// having once shared a name with the per-launch identity the Manager MINTS for a child
// (`Launch.Variable`) — one name meaning two different things in two processes, on opposite
// sides of the trust boundary.
let private defaultSession =
    Cli.valueOf defaultSessionOption args |> Option.defaultValue (SessionId.value SessionId.local)
let private dataDir = Cli.valueOf dataDirOption args |> Option.defaultValue ".yession"
// Where the management UI answers. Parsed by `ManagerPort.ofName`, beside the port it
// configures and where the cheap tier can reach it, for the reason `--secrets` is.
let private managerPort =
    match ProcessManager.ManagerPort.ofName (Cli.valueOf portOption args) with
    | Ok port -> port
    | Error e -> Cli.rejectValue cli e

// How long a session may go unused before the Manager stops it (Plan 11). Unset = never,
// which is the default: reaping trades a launch on the next visit for everything an idle
// session holds, and on a deployment that tracks a fast-moving build, for sessions that
// return on the new one without the Manager having to restart. Both are choices.
let private idleTimeout =
    // Not given is answered HERE, rather than handed down as an empty string: absence is the
    // default (reaping off), and spelling it `""` would ask the parser to rediscover from a
    // value what this already knows from the option.
    match Cli.valueOf idleTimeoutOption args with
    | None -> None
    | Some given ->
        match Yession.Manager.IdleWindow.parse given with
        | Ok window -> window
        | Error e -> Cli.rejectValue cli e

// Who the humans at this Manager are (Plan 07): `--auth localhost` trusts the
// loopback interface (single-machine deployment), `--auth trusted-headers` trusts the
// canonical x-yession-* identity headers an operator-run authenticating proxy asserts.
// No `--auth` means nobody authenticates — choosing a trust rule is deliberate, and an
// unknown name fails the boot loudly rather than defaulting to anything.
let private strategy =
    match Yession.Oidc.Strategy.ofName (Cli.valueOf authOption args) with
    | Ok s -> s
    | Error e -> Cli.rejectValue cli e

// Whether secrets persist across restarts (`--secrets`). Only the NAME is settled here —
// what it resolves to needs the host probed for a credential manager, which happens in the
// async below. Parsed up here beside `--auth` so an unknown value refuses the boot before
// anything else is touched.
let private secretsMode =
    match ProcessManager.SecretsMode.ofName (Cli.valueOf secretsOption args) with
    | Ok m -> m
    | Error e -> Cli.rejectValue cli e

// How this deployment is reached from outside (Plan 09). Parsed once, HERE, so a
// combination that cannot work is a refused boot rather than links and redirect URIs that
// point somewhere unreachable. Sessions inherit the same variables by env and parse them
// the same way.
let private publicAccess =
    match Interop.publicAccess () with
    | Ok access -> access
    | Error e -> Cli.abort e

[<Fable.Core.Emit("process.execPath")>]
let private nodePath : string = Fable.Core.Util.jsNative

// The session process command: this Node running the session entry. `--spawn-bin` overrides
// with a standalone command, which is what a deployment doing rolling upgrades points at a
// path that floats with its builds (Plan 11).
//
// `YESSION_SPAWN_MAIN` stays a VARIABLE, and the difference is who sets it: the npm `yession`
// bin shim does, to name the packaged `session.js` beside itself. That is a packaging fact
// like `YESSION_BIN_*`, not a decision an operator takes — a shim that had to append an
// argument would also have to know whether the operator had already given one.
let private sessionCommand, sessionArgs =
    match Cli.valueOf spawnBinOption args with
    | None -> nodePath, [ Interop.envOr "YESSION_SPAWN_MAIN" "app/SessionMain.js" ]
    | Some binary -> binary, []

Async.StartImmediate(
    async {
        // Environments are session-owned (the sandbox seam): each child creates its own
        // sandboxes, and only secrets custody stays here — resolved to a child at
        // sandbox spawn over the control endpoint with its per-launch secret.
        // The Manager is a direct OTel emitter, configured by how it was started (the standard
        // OTEL_* env — stdout, a collector, or both; see app/Telemetry.fs). It emits its own
        // session-lifecycle signals and passes its OTEL_* environment through to each child.
        let telemetry = Telemetry.managerFromEnv ()
        telemetry.Log "manager started" [ "yession.manager.data_dir", box dataDir ]
        // Secrets (Plan 06): the OS credential manager keys the durable store; a host
        // without one runs in-memory only (loud at boot) — never a plaintext key file.
        // `--secrets` overrides both directions: `ephemeral` refuses persistence this host
        // could have had, `durable` refuses the BOOT on a host that cannot offer it.
        let! keyStore =
            if ProcessManager.SecretsMode.needsCredentialManager secretsMode then KeyStore.detect ()
            else async { return None }
        let secretsBacking =
            match ProcessManager.SecretsBacking.forMode secretsMode keyStore with
            | Ok backing -> backing
            // `--secrets durable` on a host with no credential manager. A configuration
            // refusal like the ones above, and reported the same way — it just could not be
            // decided until the host had been probed.
            | Error e -> Cli.abort e
        let! manager =
            ProcessManager.createWithUi
                { ProcessManager.Options.defaults dataDir sessionCommand sessionArgs with
                    IdleTimeout = idleTimeout
                    ManagerPort = Some managerPort
                    // Behind an authenticating proxy the issuer must be the proxy's
                    // origin, or off-host browsers cannot follow the authorize bounce.
                    Public = publicAccess
                    OnEvent = telemetry.Log
                    Strategy = Some strategy
                    Secrets = Some secretsBacking }
                (Some ManagerUi.tryHandle)

        // Ensure the default session exists (an existing registration is resume).
        let sessionId = SessionId.create defaultSession |> expect
        match manager.TryFind sessionId with
        | Some _ -> ()
        | None -> manager.CreateSession defaultSession defaultSession |> expect |> ignore

        // An ARCHIVED default session is not launched, and is not fatal. Launching it is a
        // convenience, not a precondition for the Manager — and a boot that died here would
        // lock the operator out completely, because the management UI is the only place to
        // unarchive it and it would never come up. This reads `ArchivedAt` to decide whether
        // to ATTEMPT; the refusal itself still lives in `ManagerState.launchable`, so the
        // two cannot disagree. Every OTHER launch failure stays fatal: a child that cannot
        // start is still a boot that should not claim to have succeeded.
        let archived =
            manager.TryFind sessionId
            |> Option.bind (fun view -> view.Record.ArchivedAt)
            |> Option.isSome
        if archived then
            printfn
                "Yession Manager: session %s is archived — not launching it. Unarchive it in the management UI."
                defaultSession
        else
            match! manager.Launch sessionId with
            | Error reason -> failwithf "default session failed to launch: %s" reason
            | Ok sessionPort ->
                printfn
                    "Yession Manager: session %s launched at http://127.0.0.1:%d/  (child process, data=%s)"
                    defaultSession
                    sessionPort
                    dataDir

        match manager.EndpointPort with
        | Some uiPort -> printfn "Yession Manager: management UI at http://127.0.0.1:%d/" uiPort
        | None -> ()
    })
