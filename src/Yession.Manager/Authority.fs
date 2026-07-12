namespace Yession.Manager

open Yession.Domain

/// The engine that actually runs environments, as a Manager-owned capability. The
/// authority layer (session scoping, ownership, handle validation) is verified
/// independently of any engine; backends are interchangeable (in-memory for
/// deterministic tests, local child processes everywhere, Docker where a daemon runs).
type ContainerBackend =
    { /// Start an environment; returns the backend's container id.
      Start : SessionId -> EnvironmentSpec -> Async<Result<string, string>>
      Stop : string -> Async<Result<unit, string>>
      /// Execute a command in a running container, streaming output chunks in order.
      Execute : string -> CommandRequest -> (CommandOutputChunk -> unit) -> Async<CommandResult> }

/// Manager-side enforcement of session/container ownership (Step 11). Every capability
/// call re-validates the handle against the Manager's registry: the container must
/// exist, belong to the capability's session, and (for exec) be running. A Session
/// Process holds only the resulting pre-scoped functions — no backend, no registry, no
/// way to name another session.
module Authority =

    type internal ContainerRecord =
        { Owner : SessionId
          Running : bool }

    /// The Manager's ownership registry, shared across every session's capabilities.
    type ContainerRegistry () =
        let mutable containers : Map<string, ContainerRecord> = Map.empty
        member internal _.Register (containerId: string) (owner: SessionId) =
            containers <- Map.add containerId { Owner = owner; Running = true } containers
        member internal _.MarkStopped (containerId: string) =
            containers <-
                containers
                |> Map.change containerId (Option.map (fun r -> { r with Running = false }))
        member internal _.TryFind (containerId: string) = Map.tryFind containerId containers
        /// The owner of a container, if it exists (observability for tests/ops).
        member _.OwnerOf (containerId: string) : SessionId option =
            Map.tryFind containerId containers |> Option.map (fun r -> r.Owner)
        /// Is the container currently running?
        member _.IsRunning (containerId: string) : bool =
            match Map.tryFind containerId containers with
            | Some r -> r.Running
            | None -> false

    /// Validate a handle for a capability scoped to `sessionId`: the handle must have
    /// been minted for this session, and the container must exist in the registry and
    /// belong to this session.
    let private validate
        (registry: ContainerRegistry)
        (sessionId: SessionId)
        (handle: ContainerHandle)
        : Result<string, string> =
        let containerId = ContainerHandle.containerId handle
        if ContainerHandle.sessionId handle <> sessionId then
            Error "handle does not belong to this session"
        else
            match registry.TryFind containerId with
            | None -> Error "unknown container"
            | Some record when record.Owner <> sessionId -> Error "container belongs to another session"
            | Some _ -> Ok containerId

    /// Grant a session its environment capabilities: pre-scoped functions closing over
    /// the session id. This is the only way environment authority reaches a Session
    /// Process; there is no ambient engine access anywhere in the Process.
    let grant
        (registry: ContainerRegistry)
        (backend: ContainerBackend)
        (sessionId: SessionId)
        : SessionEnvironmentCapabilities =

        let start (spec: EnvironmentSpec) : Async<StartContainerResult> =
            async {
                match! backend.Start sessionId spec with
                | Ok containerId ->
                    registry.Register containerId sessionId
                    return ContainerStarted (ContainerHandle.create sessionId containerId)
                | Error reason -> return ContainerStartFailed reason
            }

        let stop (handle: ContainerHandle) : Async<StopContainerResult> =
            async {
                match validate registry sessionId handle with
                | Error reason -> return ContainerStopFailed reason
                | Ok containerId ->
                    match! backend.Stop containerId with
                    | Ok () ->
                        registry.MarkStopped containerId
                        return ContainerStopped
                    | Error reason -> return ContainerStopFailed reason
            }

        let execute
            (handle: ContainerHandle)
            (command: CommandRequest)
            (onChunk: CommandOutputChunk -> unit)
            : Async<CommandResult> =
            async {
                match validate registry sessionId handle with
                | Error reason -> return CommandExecutionFailed reason
                | Ok containerId ->
                    if not (registry.IsRunning containerId) then
                        return CommandExecutionFailed "container is not running"
                    else
                        return! backend.Execute containerId command onChunk
            }

        { StartContainer = start
          StopContainer = stop
          Execute = execute }

/// A deterministic backend for tests and dry runs: containers are registry entries,
/// exec is delegated to an injected script. Counts every call so tests can assert the
/// authority layer rejected an operation *before* the backend was reached.
module InMemoryBackend =

    type Recorder () =
        let mutable started = 0
        let mutable stopped = 0
        let mutable executed = 0
        member _.Started = started
        member _.Stopped = stopped
        member _.Executed = executed
        member internal _.RecordStart () = started <- started + 1
        member internal _.RecordStop () = stopped <- stopped + 1
        member internal _.RecordExecute () = executed <- executed + 1

    let create
        (recorder: Recorder)
        (scriptedExec: CommandRequest -> (CommandOutputChunk -> unit) -> Async<CommandResult>)
        : ContainerBackend =
        let mutable nextContainer = 0
        { Start =
            fun sessionId _spec ->
                async {
                    recorder.RecordStart ()
                    let containerId = sprintf "ctr-%s-%d" (SessionId.value sessionId) nextContainer
                    nextContainer <- nextContainer + 1
                    return Ok containerId
                }
          Stop =
            fun _containerId ->
                async {
                    recorder.RecordStop ()
                    return Ok ()
                }
          Execute =
            fun _containerId command onChunk ->
                async {
                    recorder.RecordExecute ()
                    return! scriptedExec command onChunk
                } }
