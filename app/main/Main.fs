module Yession.Host.Main

// The Manager entry point (`start`; the `yession` binary). The Manager is a
// process supervisor + management surface: sessions run as CHILD OS PROCESSES
// (`yession-session`; in development, node over the Fable output), so a crashing
// session never takes the Manager down. Configuration comes from the environment so
// the repo interface stays declarative.
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

let private cli = Cli.spec "yession-manager" [ authOption; secretsOption ]
let private args = Cli.parseOrExit cli Version.current

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

let private sessionKey = Interop.envOr "YESSION_SESSION" "local-session"
let private dataDir = Interop.envOr "YESSION_DATA_DIR" ".yession"
// The management UI wants a bookmarkable address, so its default is fixed; a second
// Manager instance must choose its own port (bind conflicts fail loudly).
let private managerPort = Interop.envOr "YESSION_MANAGER_PORT" "8321" |> int

// How long a session may go unused before the Manager stops it (Plan 11). Unset = never,
// which is the default: reaping trades a launch on the next visit for everything an idle
// session holds, and on a deployment that tracks a fast-moving build, for sessions that
// return on the new one without the Manager having to restart. Both are choices.
let private idleTimeout =
    match Yession.Manager.IdleWindow.parse (Interop.envOr "YESSION_SESSION_IDLE_TIMEOUT" "") with
    | Ok window -> window
    | Error e -> Cli.abort e

// Who the humans at this Manager are (docs/plans/07): `--auth localhost` trusts the
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

// How this deployment is reached from outside (docs/plans/09). Parsed once, HERE, so a
// combination that cannot work is a refused boot rather than links and redirect URIs that
// point somewhere unreachable. Sessions inherit the same variables by env and parse them
// the same way.
let private publicAccess =
    match Interop.publicAccess () with
    | Ok access -> access
    | Error e -> Cli.abort e

[<Fable.Core.Emit("process.execPath")>]
let private nodePath : string = Fable.Core.Util.jsNative

// The session process command: this Node running the session entry. In the npm
// package both bins live in one install, and the `yession` bin shim sets
// `YESSION_SESSION_MAIN` to the packaged `session.js`; in development it defaults to
// the Fable output. `YESSION_SESSION_BIN` overrides with a standalone command.
let private sessionCommand, sessionArgs =
    match Interop.envOr "YESSION_SESSION_BIN" "" with
    | "" -> nodePath, [ Interop.envOr "YESSION_SESSION_MAIN" "app/SessionMain.js" ]
    | binary -> binary, []

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
        let sessionId = SessionId.create sessionKey |> expect
        match manager.TryFind sessionId with
        | Some _ -> ()
        | None -> manager.CreateSession sessionKey sessionKey |> expect |> ignore

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
                sessionKey
        else
            match! manager.Launch sessionId with
            | Error reason -> failwithf "default session failed to launch: %s" reason
            | Ok sessionPort ->
                printfn
                    "Yession Manager: session %s launched at http://127.0.0.1:%d/  (child process, data=%s)"
                    sessionKey
                    sessionPort
                    dataDir

        match manager.EndpointPort with
        | Some uiPort -> printfn "Yession Manager: management UI at http://127.0.0.1:%d/" uiPort
        | None -> ()
    })
