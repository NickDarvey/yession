namespace Yession.SessionProcess

open Yession.Domain

/// The session's lazily-started environment (Step 12). One environment per session:
/// nothing starts at session creation; a signalled need (usually from the agent) starts
/// a container through the scoped capability; a stopped environment is restarted by the
/// next need under the same environment id. Every transition is appended as an event —
/// the Session Process is the only writer.
module SessionEnvironment =

    type SessionEnvironment =
        { /// Make sure an environment is available, recording the identified need. The
          /// turn id (when the need comes from an agent turn) is recorded on the event.
          Ensure : AgentTurnId option -> string -> Async<EnsureEnvironmentResult>
          /// Run a command in the running environment, streaming output into the event
          /// log (Step 13). The whole lifecycle is events; per-command output ordering
          /// is preserved.
          Execute : CommandRequest -> Async<CommandResult>
          /// Stop the environment if it is running (recorded as events).
          Stop : unit -> Async<unit>
          /// The running container's handle, if any.
          CurrentHandle : unit -> ContainerHandle option }

    /// A session with no environment capability: needs are recorded as unavailable
    /// without any container authority existing in the Process.
    let unavailable : SessionEnvironment =
        { Ensure = fun _ _ -> async { return EnvironmentUnavailable "this session has no environment capability" }
          Execute = fun _ -> async { return CommandExecutionFailed "this session has no environment capability" }
          Stop = fun () -> async { return () }
          CurrentHandle = fun () -> None }

    let create
        (log: EventLog<SessionEvent>)
        (capabilities: SessionEnvironmentCapabilities)
        (spec: EnvironmentSpec)
        (environmentId: string)
        : SessionEnvironment =

        let mutable running : ContainerHandle option = None

        let append event =
            async {
                let! _ = log.Append ActorRef.SessionProcess event
                return ()
            }

        let ensure (agentTurnId: AgentTurnId option) (reason: string) : Async<EnsureEnvironmentResult> =
            async {
                do! append (EnvironmentNeedIdentified { Reason = reason; AgentTurnId = agentTurnId })
                match running with
                | Some _ ->
                    // Already running: the environment is preserved across needs.
                    return EnvironmentAvailable
                | None ->
                    do! append (EnvironmentStartRequested
                                    { EnvironmentId = environmentId
                                      SpecSummary = EnvironmentSpec.summary spec })
                    match! capabilities.StartContainer spec with
                    | ContainerStarted handle ->
                        running <- Some handle
                        do! append (EnvironmentStarted
                                        { EnvironmentId = environmentId
                                          ContainerRef = ContainerHandle.containerId handle })
                        return EnvironmentAvailable
                    | ContainerStartFailed reason ->
                        do! append (EnvironmentStartFailed { EnvironmentId = environmentId; Reason = reason })
                        return EnvironmentUnavailable reason
            }

        let execute (request: CommandRequest) : Async<CommandResult> =
            async {
                do! append (CommandRequested
                                { CommandId = request.CommandId
                                  Executable = request.Executable
                                  Arguments = request.Arguments })
                match running with
                | None ->
                    let result = CommandExecutionFailed "no running environment"
                    do! append (CommandCompleted { CommandId = request.CommandId; Result = result })
                    return result
                | Some handle ->
                    do! append (CommandStarted { CommandId = request.CommandId })
                    let onChunk (chunk: CommandOutputChunk) =
                        Async.StartImmediate (
                            append (CommandOutputReceived
                                        { CommandId = chunk.CommandId
                                          Stream = chunk.Stream
                                          Text = chunk.Text }))
                    let! result = capabilities.Execute handle request onChunk
                    do! append (CommandCompleted { CommandId = request.CommandId; Result = result })
                    return result
            }

        let stop () : Async<unit> =
            async {
                match running with
                | None -> return ()
                | Some handle ->
                    do! append (EnvironmentStopRequested { EnvironmentId = environmentId })
                    match! capabilities.StopContainer handle with
                    | ContainerStopped ->
                        running <- None
                        do! append (EnvironmentStopped { EnvironmentId = environmentId })
                    | ContainerStopFailed _ ->
                        // The container may still be running; keep the handle.
                        return ()
            }

        { Ensure = ensure
          Execute = execute
          Stop = stop
          CurrentHandle = fun () -> running }
