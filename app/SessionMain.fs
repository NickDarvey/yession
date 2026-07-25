module Yession.Host.SessionMain

// The Session Process entry (Phase 4, Steps 23–24): runs exactly ONE session,
// configured from the environment — the Manager's spawn contract — over the session's
// own data directory. Once listening it prints exactly one JSON readiness line to
// stdout; everything else it writes is logging. Environment authority arrives as a
// control endpoint + per-launch secret: the capability calls cross back to the
// Manager, which owns the registry and the engines.

open Fable.Core
open Yession.Domain
open Yession.Host

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

let private sessionId = SessionId.create (Interop.envOr "YESSION_SESSION" "local-session") |> expect
let private port = Interop.envOr "YESSION_PORT" "0" |> int
let private dataDir =
    Interop.envOr "YESSION_SESSION_DATA" (sprintf ".yession/sessions/%s" (SessionId.value sessionId))

// The control channel to the Manager (Step 24): environment capability calls, the
// display-name report, AND this launch's OAuth client registration all authenticate
// with the same per-launch secret. Absent (a bare `yession-session` run), the session
// is environment-less and its HTTP surface is ungated.
let private controlChannel =
    match Interop.envOr "YESSION_CONTROL_URL" "", Interop.envOr "YESSION_CONTROL_SECRET" "" with
    | "", _
    | _, "" -> None
    | url, secret -> Some (url, secret)

// Environment capabilities over the control RPC, when the Manager granted them. A
// launch without a grant still holds the channel (403 on environment routes).
let private environmentCapabilities =
    controlChannel |> Option.map (fun (url, secret) -> ControlClient.capabilities url secret)

// The same control channel carries the collaborative title back to the Manager as the
// session's display name.
let private reportName =
    controlChannel |> Option.map (fun (url, secret) -> ControlClient.nameReporter url secret)

// Secrets (Plan 06): the session's write/list/delete surface over the same channel,
// pre-bound to this session's own scope. Built after the session id parses (below).
let private secretsCapabilitiesFor (sessionId: SessionId) =
    controlChannel |> Option.map (fun (url, secret) -> ControlClient.secretsCapabilities url secret sessionId)

// User authorization: with a Manager, this session is an OIDC client of it; the RP
// configuration completes after listen (the redirect URI needs the bound port).
let private auth =
    controlChannel |> Option.map (fun _ -> SessionAuth.create sessionId)

// Telemetry: this session is a direct OTel emitter — one OTel log record per completed turn.
// Destination (stdout / a collector / both / off) comes from the standard OTEL_* env the
// Manager passes through; identity (service.name=yession-session, service.instance.id=<id>)
// the Manager adapts per child. No Manager-side collector, no bespoke endpoint.
let private telemetry = Telemetry.fromEnv sessionId

// The reverse leg over the same control channel: subscribe to the Manager's notification
// stream so an out-of-band change can reach this session. Absent a control channel, the
// session simply runs without it (nothing pushes notifications in-process).
let private subscribeNotifications =
    match Interop.envOr "YESSION_CONTROL_URL" "", Interop.envOr "YESSION_CONTROL_SECRET" "" with
    | "", _
    | _, "" -> None
    | url, secret -> Some (fun handler -> ControlClient.subscribeNotifications url secret handler)

// The MCP tool stream over the same control channel: the current tool list on subscribe, then
// updates as MCP services come and go. Absent a control channel, the session runs without it.
let private subscribeMcp =
    match Interop.envOr "YESSION_CONTROL_URL" "", Interop.envOr "YESSION_CONTROL_SECRET" "" with
    | "", _
    | _, "" -> None
    | url, secret -> Some (fun handler -> ControlClient.subscribeMcp url secret handler)

/// A built-in diagnostic runner (`YESSION_AGENT=diagnostic`): exercises the session's
/// environment capability end to end — ensure, execute, stream — without model
/// credentials. The verify suite drives it across real process boundaries; it doubles
/// as a field smoke test.
let private diagnosticAgent : RunAgent =
    fun _ capabilities _signal onChunk ->
        async {
            match! capabilities.EnsureEnvironment "diagnostic run" with
            | EnvironmentUnavailable reason -> return AgentFailed (sprintf "environment unavailable: %s" reason)
            | EnvironmentAvailable ->
                let request =
                    { CommandId = CommandId.create (string (System.Guid.NewGuid ())) |> expect
                      Executable = "node"
                      Arguments = [ "-e"; "console.log('diagnostic-ok')" ]
                      WorkingDirectory = None
                      Environment = Map.empty
                      Timeout = Some (System.TimeSpan.FromSeconds 30.0) }
                let mutable output = ""
                let! result = capabilities.ExecuteCommand request (fun chunk -> output <- output + chunk.Text)
                match result with
                | CommandSucceeded 0 ->
                    onChunk { Text = output.Trim () }
                    return AgentCompleted (sprintf "diagnostic: %s" (output.Trim ()), None)
                | other -> return AgentFailed (sprintf "diagnostic command failed: %A" other)
        }

/// A built-in probe (`YESSION_AGENT=usage-probe`, Plan 04): completes a turn with fixed,
/// non-zero usage and no credentials, so the cross-process telemetry e2e can assert the
/// counts reach the Manager collector over the real spawn + OTLP path.
let private usageProbeAgent : RunAgent =
    fun _ _ _ _ ->
        async {
            return
                AgentCompleted (
                    "usage probe",
                    Some
                        { InputTokens = 111
                          OutputTokens = 22
                          CacheReadTokens = 3
                          CacheCreationTokens = 4
                          Model = Some "probe-model" })
        }

// The real agent runs only when the process has credentials; without them the session
// still works as a human-only collaborative session.
let private runAgent =
    match Interop.envOr "YESSION_AGENT" "" with
    | "diagnostic" -> Some diagnosticAgent
    | "usage-probe" -> Some usageProbeAgent
    | _ ->
        if Interop.envOr "ANTHROPIC_API_KEY" (Interop.envOr "CLAUDE_CODE_OAUTH_TOKEN" "") <> "" then Some Agent.run
        else None

[<Fable.Core.Emit("(process.stdin.on('close', $0), process.stdin.on('end', $0), process.stdin.resume())")>]
let private onStdinClosed (handler: unit -> unit) : unit = Fable.Core.Util.jsNative

Async.StartImmediate (
    async {
        let log =
            EventStore.openLog (sprintf "%s/events.jsonl" dataDir) sessionId (fun () -> System.DateTimeOffset.UtcNow)
        let docStore = DocStore.openStore (sprintf "%s/doc.jsonl" dataDir)
        let! host = Host.startFull runAgent environmentCapabilities (secretsCapabilitiesFor sessionId) (Some log) (Some docStore) reportName telemetry.Emit subscribeNotifications subscribeMcp sessionId auth port
        // Register this launch's OAuth client with the Manager — HERE, after listen
        // (the redirect URI needs the OS-assigned port) and BEFORE the readiness line
        // (readiness implies the login surface works). A session that cannot register
        // cannot authorize users, so failure is fatal, never a half-open session.
        match controlChannel, auth with
        | Some (url, secret), Some auth ->
            let redirectUri = sprintf "http://127.0.0.1:%d/callback" host.Port
            match! ControlClient.registerClient url secret redirectUri with
            | Error e ->
                eprintfn "client registration with the manager failed: %s" e
                Interop.exit 1
            | Ok registration ->
                match! auth.Configure registration.Issuer registration.ClientId registration.ClientSecret redirectUri with
                | Error e ->
                    eprintfn "%s" e
                    Interop.exit 1
                | Ok () -> ()
        | _ -> ()
        // Sessions never outlive their Manager: spawned under the guard, the Manager's
        // death closes our stdin (the kernel does this even on SIGKILL) and we exit.
        if Interop.envOr "YESSION_PARENT_GUARD" "" = "1" then
            // Flush buffered telemetry before exiting (the Manager's death closes stdin).
            onStdinClosed (fun () ->
                Async.StartImmediate (
                    async {
                        do! telemetry.Shutdown () |> Async.AwaitPromise
                        Interop.exit 0
                    }))
        // The one readiness line of the spawn contract — last, so the Manager can
        // treat everything before it as logs and everything after as a live session.
        printfn """{"yession":"ready","port":%d}""" host.Port
    })
