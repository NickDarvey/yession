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

// `--version` answers before any configuration is read: no data directory, no ports, no
// sessions launched.
if Interop.versionFlag () then
    printfn "%s" Version.current
    Interop.exit 0

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
    | Error e -> failwith e

// Who the humans at this Manager are (docs/plans/07): `--auth localhost` trusts the
// loopback interface (single-machine deployment), `--auth trusted-headers` trusts the
// canonical x-yession-* identity headers an operator-run authenticating proxy asserts.
// No `--auth` means nobody authenticates — choosing a trust rule is deliberate, and an
// unknown name fails the boot loudly rather than defaulting to anything.
let private strategy =
    match Yession.Oidc.Strategy.ofName (Interop.argValue "auth") with
    | Ok s -> s
    | Error e -> failwith e

// How this deployment is reached from outside (docs/plans/09). Parsed once, HERE, so a
// combination that cannot work is a refused boot rather than links and redirect URIs that
// point somewhere unreachable. Sessions inherit the same variables by env and parse them
// the same way.
let private publicAccess =
    match Interop.publicAccess () with
    | Ok access -> access
    | Error e -> failwith e

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
        let! keyStore = KeyStore.detect ()
        let secretsBacking =
            match keyStore with
            | Some store -> ProcessManager.DurableSecrets store
            | None -> ProcessManager.EphemeralSecrets
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
