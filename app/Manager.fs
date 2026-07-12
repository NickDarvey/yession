module Yession.Host.Manager

// The Session Manager (Step 10): the authority that launches Session Processes. The
// Manager owns launch — a Session Process never self-starts with host authority — and
// keeps the registry of launched Processes. In Phase 2 the Manager and its Session
// Processes share one local Node runtime (each Process is its own composition root
// listening on its own port); the OS-process split is an adapter concern for later
// phases, and nothing in the authority contract depends on it. See docs/design.md §3.

open Yession.Domain
open Yession.Manager
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
      /// The Manager-owned container ownership registry (observability for tests/ops).
      Containers : Authority.ContainerRegistry
      /// Stop every launched Process.
      Stop : unit -> Async<unit> }

/// Create a Session Manager. Ports are allocated from `basePort` upward; `runAgent`
/// is passed through to each launched Process. When a `ContainerBackend` is given, each
/// launched Process receives environment capabilities already scoped to its session
/// (Step 11) — the Manager owns the ownership registry.
let createWith
    (runAgent: RunAgent option)
    (backend: ContainerBackend option)
    (makeLog: (SessionId -> Yession.SessionProcess.EventLog<SessionEvent>) option)
    (basePort: int)
    : SessionManager =
    let containers = Authority.ContainerRegistry ()
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
                // Launch. The host's listening resolution is the Process's
                // registration back to the Manager. Environment capabilities are
                // granted here — pre-scoped to the launched session.
                let environmentCapabilities =
                    backend |> Option.map (fun b -> Authority.grant containers b request.SessionId)
                let baseLog = makeLog |> Option.map (fun make -> make request.SessionId)
                let! host =
                    Host.startWithCapabilities runAgent environmentCapabilities baseLog request.SessionId request.SessionToken port
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
      Containers = containers
      Registered = fun () -> registry |> Map.toList |> List.map snd
      TryFind = fun sessionId -> Map.tryFind (SessionId.value sessionId) registry
      Stop =
        fun () ->
            async {
                for _, managed in Map.toList registry do
                    do! managed.Host.Stop ()
                registry <- Map.empty
            } }

/// `createWith` without durable storage — the deterministic in-memory default.
let create (runAgent: RunAgent option) (backend: ContainerBackend option) (basePort: int) : SessionManager =
    createWith runAgent backend None basePort
