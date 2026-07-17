module Yession.Host.SessionMain

// The Session Process entry (Phase 4, Steps 23–24): runs exactly ONE session,
// configured from the environment — the Manager's spawn contract — over the session's
// own data directory. Once listening it prints exactly one JSON readiness line to
// stdout; everything else it writes is logging. Environment authority arrives as a
// control endpoint + per-launch secret: the capability calls cross back to the
// Manager, which owns the registry and the engines.

open Yession.Domain
open Yession.Host

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

let private sessionId = SessionId.create (Interop.envOr "YESSION_SESSION" "local-session") |> expect
let private token = Interop.envOr "YESSION_TOKEN" "local-dev-token"
let private port = Interop.envOr "YESSION_PORT" "0" |> int
let private dataDir =
    Interop.envOr "YESSION_SESSION_DATA" (sprintf ".yession/sessions/%s" (SessionId.value sessionId))

// Environment capabilities over the control RPC (Step 24), when the Manager granted
// them to this launch. Absent, the session runs environment-less.
let private environmentCapabilities =
    match Interop.envOr "YESSION_CONTROL_URL" "", Interop.envOr "YESSION_CONTROL_SECRET" "" with
    | "", _
    | _, "" -> None
    | url, secret -> Some (ControlClient.capabilities url secret)

// The same control channel carries the collaborative title back to the Manager as the
// session's display name, when a control channel exists for this launch.
let private reportName =
    match Interop.envOr "YESSION_CONTROL_URL" "", Interop.envOr "YESSION_CONTROL_SECRET" "" with
    | "", _
    | _, "" -> None
    | url, secret -> Some (ControlClient.nameReporter url secret)

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

// The real agent runs only when the process has credentials; without them the session
// still works as a human-only collaborative session.
let private runAgent =
    match Interop.envOr "YESSION_AGENT" "" with
    | "diagnostic" -> Some diagnosticAgent
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
        let! host = Host.startFull runAgent environmentCapabilities (Some log) (Some docStore) reportName sessionId token port
        // Sessions never outlive their Manager: spawned under the guard, the Manager's
        // death closes our stdin (the kernel does this even on SIGKILL) and we exit.
        if Interop.envOr "YESSION_PARENT_GUARD" "" = "1" then
            onStdinClosed (fun () -> Interop.exit 0)
        // The one readiness line of the spawn contract — last, so the Manager can
        // treat everything before it as logs and everything after as a live session.
        printfn """{"yession":"ready","port":%d}""" host.Port
    })
