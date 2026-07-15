module Yession.Host.ProcessManager

// The Manager as a process supervisor (Phase 4, Step 23): sessions are child OS
// processes, so a crashing session can never take the Manager or its siblings down.
// The durable registry lives behind ManagerStore (Step 22); runtime state (child
// handle, port) is memory-only and reconciled at boot — after a Manager restart every
// session is simply stopped, and RESUME IS JUST LAUNCH: spawning over the same data
// directory replays the event log and doc sidecar (Step 19).
//
// Not a singleton by assumption: everything lives under this instance's data
// directory and session ports default to OS-assigned. Two Managers over the SAME data
// directory are unsupported (documented; a lock arrives with SQLite).

open System
open Yession.Domain
open Yession.Manager

/// A session's runtime status — never persisted.
type SessionStatus =
    | NotRunning
    | Running of port: int * pid: int
    /// The child exited without the Manager stopping it (crash or self-exit).
    | Exited of code: int option

type SessionView =
    { Record : SessionRecord
      Status : SessionStatus }

type ProcessManager =
    { /// Register a new session (durable): id, display name, token. Does not launch.
      CreateSession : string -> string -> string -> Result<SessionRecord, string>
      /// Launch (or resume — same thing) a registered session; resolves with its port.
      Launch : SessionId -> Async<Result<int, string>>
      /// Stop a running session (SIGTERM, SIGKILL after a grace period).
      Stop : SessionId -> Async<Result<unit, string>>
      /// Every registered session with its runtime status.
      Sessions : unit -> SessionView list
      /// Resolves when the session's running child exits (immediately if none runs).
      WaitForExit : SessionId -> Async<unit>
      TryFind : SessionId -> SessionView option
      /// Stop every running child and the control endpoint (Manager shutdown).
      StopAll : unit -> Async<unit> }

type Options =
    { /// This Manager instance's data directory (state file + session stores).
      DataDir : string
      /// The command that runs a session process — the `yession-session` binary in the
      /// product, `node <SessionMain.js>` in development and tests.
      SessionCommand : string
      SessionArgs : string list
      /// Fixed port for a launched session; None = OS-assigned per launch.
      SessionPort : int option
      /// How long a child may take to print its readiness line.
      LaunchTimeoutMs : int
      /// SIGTERM → SIGKILL escalation grace.
      StopGraceMs : int
      /// Environment authority (Step 24): grants session-scoped capabilities, served
      /// to children over the control endpoint with a per-launch secret. None =
      /// sessions run environment-less.
      Grant : (SessionId -> SessionEnvironmentCapabilities) option }

module Options =
    let defaults (dataDir: string) (sessionCommand: string) (sessionArgs: string list) : Options =
        { DataDir = dataDir
          SessionCommand = sessionCommand
          SessionArgs = sessionArgs
          SessionPort = None
          LaunchTimeoutMs = 15000
          StopGraceMs = 3000
          Grant = None }

[<Fable.Core.Emit("setTimeout($1, $0)")>]
let private setTimeout (ms: int) (callback: unit -> unit) : obj = Fable.Core.Util.jsNative

let private clock () = DateTimeOffset.UtcNow

let create (options: Options) : Async<ProcessManager> =
  async {
    let statePath = sprintf "%s/manager.json" options.DataDir
    let mutable state = ManagerStore.load statePath

    // Runtime-only: the child handle per running session, and the last observed exit
    // for sessions that died without a Stop.
    let mutable children : Map<string, Spawn.RunningChild * int> = Map.empty
    let mutable lastExit : Map<string, int option> = Map.empty
    // Stops in flight: their exits are expected, not crashes.
    let mutable stopping : Set<string> = Set.empty

    // The control endpoint (Step 24): per-launch secrets resolve to the capabilities
    // the Manager granted that launch — the RPC equivalent of the Step 11 closure. A
    // secret dies with its launch.
    let mutable secrets : Map<string, SessionEnvironmentCapabilities> = Map.empty
    let! controlServer =
        match options.Grant with
        | None -> async { return None }
        | Some _ ->
            async {
                let handler (req: Interop.IncomingMessage) (res: Interop.ServerResponse) =
                    if not (Control.tryHandle (fun secret -> Map.tryFind secret secrets) req res) then
                        res.writeHead (404, Fable.Core.JsInterop.createObj [ "content-type", box "text/plain" ]) |> ignore
                        res.``end`` "not found"
                let server = Interop.createServer handler
                let! listening =
                    Async.FromContinuations (fun (cont, _, _) ->
                        server.listen (0, "127.0.0.1", fun () -> cont server) |> ignore)
                return Some listening
            }
    let controlUrl () =
        controlServer |> Option.map (fun s -> sprintf "http://127.0.0.1:%d" (Interop.serverPort s))

    let statusOf (record: SessionRecord) : SessionStatus =
        let key = SessionId.value record.SessionId
        match Map.tryFind key children with
        | Some (child, port) -> Running (port, child.Pid)
        | None ->
            match Map.tryFind key lastExit with
            | Some code -> Exited code
            | None -> NotRunning

    let createSession (sessionId: string) (displayName: string) (token: string) : Result<SessionRecord, string> =
        match SessionId.create sessionId with
        | Error e -> Error e
        | Ok id ->
            let record =
                { SessionId = id
                  DisplayName = if displayName.Trim().Length > 0 then displayName.Trim () else sessionId
                  Token = token
                  CreatedAt = clock ()
                  DataDir = sprintf "sessions/%s" (SessionId.value id) }
            match ManagerState.addSession record state with
            | Error e -> Error e
            | Ok next ->
                // Durable before visible: the registry write precedes any use.
                ManagerStore.save statePath next
                state <- next
                Ok record

    let launch (sessionId: SessionId) : Async<Result<int, string>> =
        async {
            let key = SessionId.value sessionId
            match ManagerState.tryFind sessionId state with
            | None -> return Error (sprintf "unknown session %s" key)
            | Some _ when Map.containsKey key children -> return Error (sprintf "session %s is already running" key)
            | Some record ->
                // Step 24: mint the per-launch secret and grant the capabilities it
                // resolves to — the session scope is established HERE, by the Manager.
                let controlEnv =
                    match options.Grant, controlUrl () with
                    | Some grant, Some url ->
                        let secret = Interop.randomSecret ()
                        secrets <- Map.add secret (grant record.SessionId) secrets
                        [ "YESSION_CONTROL_URL", url; "YESSION_CONTROL_SECRET", secret ], Some secret
                    | _ -> [], None
                let env =
                    [ "YESSION_SESSION", SessionId.value record.SessionId
                      "YESSION_TOKEN", record.Token
                      "YESSION_SESSION_DATA", sprintf "%s/%s" options.DataDir record.DataDir
                      "YESSION_PORT", string (defaultArg options.SessionPort 0)
                      // The child watches its stdin and exits when this Manager dies.
                      "YESSION_PARENT_GUARD", "1" ]
                    @ fst controlEnv
                let revokeSecret () =
                    match snd controlEnv with
                    | Some secret -> secrets <- Map.remove secret secrets
                    | None -> ()
                match! Spawn.launch options.SessionCommand options.SessionArgs env options.LaunchTimeoutMs with
                | Error reason ->
                    revokeSecret ()
                    return Error reason
                | Ok (child, port) ->
                    children <- Map.add key (child, port) children
                    lastExit <- Map.remove key lastExit
                    child.OnExit (fun code ->
                        children <- Map.remove key children
                        // The launch's authority dies with it.
                        revokeSecret ()
                        // A stop's exit is the expected outcome, not a crash to report.
                        if Set.contains key stopping then stopping <- Set.remove key stopping
                        else lastExit <- Map.add key code lastExit)
                    return Ok port
        }

    let stop (sessionId: SessionId) : Async<Result<unit, string>> =
        async {
            let key = SessionId.value sessionId
            match Map.tryFind key children with
            | None -> return Error (sprintf "session %s is not running" key)
            | Some (child, _) ->
                stopping <- Set.add key stopping
                return!
                    Async.FromContinuations (fun (cont, _, _) ->
                        child.OnExit (fun _ -> cont (Ok ()))
                        child.Terminate ()
                        setTimeout options.StopGraceMs (fun () ->
                            if not (child.HasExited ()) then child.Kill ())
                        |> ignore)
        }

    return
        { CreateSession = createSession
          Launch = launch
          Stop = stop
          Sessions = fun () -> state.Sessions |> List.map (fun r -> { Record = r; Status = statusOf r })
          WaitForExit =
            fun sessionId ->
                match Map.tryFind (SessionId.value sessionId) children with
                | None -> async { return () }
                | Some (child, _) ->
                    Async.FromContinuations (fun (cont, _, _) -> child.OnExit (fun _ -> cont ()))
          TryFind =
            fun sessionId ->
                ManagerState.tryFind sessionId state
                |> Option.map (fun r -> { Record = r; Status = statusOf r })
          StopAll =
            fun () ->
                async {
                    for record in state.Sessions do
                        if Map.containsKey (SessionId.value record.SessionId) children then
                            let! _ = stop record.SessionId
                            ()
                    controlServer |> Option.iter (fun s -> s.close ignore)
                } }
  }
