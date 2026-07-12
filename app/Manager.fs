module Yession.Host.Manager

// The Session Manager (Step 10): the authority that launches Session Processes. The
// Manager owns launch — a Session Process never self-starts with host authority — and
// keeps the registry of launched Processes. In Phase 2 the Manager and its Session
// Processes share one local Node runtime (each Process is its own composition root
// listening on its own port); the OS-process split is an adapter concern for later
// phases, and nothing in the authority contract depends on it. See docs/design.md §3.

open Yession.Domain
open Yession.SessionProcess

/// A Session Process the Manager has launched and registered.
type ManagedSession =
    { SessionId : SessionId
      ProcessId : string
      Port : int
      BootstrapUri : string
      Host : Host.SessionHost }

type SessionManager =
    { /// Launch a Session Process for a new session and return its bootstrap URI once
      /// the Process has registered (i.e. is listening). Rejects duplicate sessions.
      StartSession : StartSession
      /// The Manager's registry of launched Processes.
      Registered : unit -> ManagedSession list
      /// Look up one registration.
      TryFind : SessionId -> ManagedSession option
      /// Stop every launched Process.
      Stop : unit -> Async<unit> }

/// Create a Session Manager. Ports are allocated from `basePort` upward; `runAgent`
/// is passed through to each launched Process (the agent capability set grows in
/// Steps 11–13).
let create (runAgent: RunAgent option) (basePort: int) : SessionManager =
    let mutable nextPort = basePort
    let mutable nextProcessNumber = 0
    let mutable registry : Map<string, ManagedSession> = Map.empty

    let startSession (request: SessionLaunchRequest) : Async<SessionLaunchResult> =
        async {
            let key = SessionId.value request.SessionId
            if Map.containsKey key registry then
                return failwithf "session %s is already launched" key
            else
                let port = nextPort
                nextPort <- nextPort + 1
                let processId = sprintf "session-process-%d" nextProcessNumber
                nextProcessNumber <- nextProcessNumber + 1
                // Launch. `Host.startWith` resolves once the Process is listening —
                // that resolution is the Process's registration back to the Manager.
                let! host = Host.startWith runAgent request.SessionId request.SessionToken port
                let bootstrapUri = sprintf "http://127.0.0.1:%d/" port
                let managed =
                    { SessionId = request.SessionId
                      ProcessId = processId
                      Port = port
                      BootstrapUri = bootstrapUri
                      Host = host }
                registry <- Map.add key managed registry
                return
                    { SessionId = request.SessionId
                      ProcessId = processId
                      LocalBootstrapUri = bootstrapUri }
        }

    { StartSession = startSession
      Registered = fun () -> registry |> Map.toList |> List.map snd
      TryFind = fun sessionId -> Map.tryFind (SessionId.value sessionId) registry
      Stop =
        fun () ->
            async {
                for _, managed in Map.toList registry do
                    do! managed.Host.Stop ()
                registry <- Map.empty
            } }
