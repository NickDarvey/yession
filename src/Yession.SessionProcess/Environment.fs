namespace Yession.SessionProcess

open System
open Yession.Domain

/// The session's lazily-started WorkSandbox (Step 12, reworked over the sandbox seam).
/// One environment per session: nothing starts at session creation; a signalled need
/// (usually from the agent) creates a sandbox through the injected `CreateSandbox`; a
/// stopped environment is recreated by the next need under the same environment id.
/// Every transition is appended as an event — the Session Process is the only writer,
/// and the event flow is the pinned observable protocol.
module SessionEnvironment =

    type SessionEnvironment =
        { /// Make sure an environment is available, recording the identified need. The
          /// turn id (when the need comes from an agent turn) is recorded on the event.
          Ensure : AgentTurnId option -> string -> Async<EnsureEnvironmentResult>
          /// Run a command in the running environment, streaming output into the event
          /// log and to the caller (Step 13). The whole lifecycle is events; per-command
          /// output ordering is preserved.
          Execute : CommandRequest -> (CommandOutputChunk -> unit) -> Async<CommandResult>
          /// Spawn a process in the running environment WITHOUT the command-event
          /// lifecycle — the caller records what happened in its own terms.
          ///
          /// Terminals (Plan 13) need this: a block's output belongs in that terminal's
          /// transcript, and its lifecycle in the block events, so routing it through
          /// `Execute` would write every printed byte into the event log a second time.
          /// The environment is still the one gate — a spawn with nothing running is an
          /// error here exactly as it is there.
          Spawn : SandboxExec -> (OutputStream * string -> unit) -> Async<Result<SandboxProcessHandle, string>>
          /// Stop the environment if it is running (recorded as events).
          Stop : unit -> Async<unit>
          /// The running sandbox's backend reference, if any.
          CurrentRef : unit -> string option }

    /// A session with no environment: needs are recorded as unavailable without any
    /// sandbox existing in the Process.
    let unavailable : SessionEnvironment =
        { Ensure = fun _ _ -> async { return EnvironmentUnavailable "this session has no environment" }
          Execute = fun _ _ -> async { return CommandExecutionFailed "this session has no environment" }
          Spawn = fun _ _ -> async { return Error "this session has no environment" }
          Stop = fun () -> async { return () }
          CurrentRef = fun () -> None }

    /// Race a process's end against its command timeout. `None` = the timeout fired
    /// first. Single-settle by construction; safe on the Node loop and under the CLR
    /// type-check alike.
    let private raceTimeout (timeout: TimeSpan option) (work: Async<SandboxRun>) : Async<SandboxRun option> =
        match timeout with
        | None ->
            async {
                let! run = work
                return Some run
            }
        | Some window ->
            Async.FromContinuations (fun (cont, _, _) ->
                let mutable settled = false
                let finish value =
                    if not settled then
                        settled <- true
                        cont value
                Async.StartImmediate (
                    async {
                        let! run = work
                        finish (Some run)
                    })
                Async.StartImmediate (
                    async {
                        do! Async.Sleep (int window.TotalMilliseconds)
                        finish None
                    }))

    let create
        (log: EventLog<SessionEvent>)
        (createSandbox: CreateSandbox)
        // Assembled fresh at every (re)creation: this is where `SecretRef` env vars
        // resolve — at sandbox spawn, and nowhere else.
        (preparePolicy: unit -> Async<Result<SandboxPolicy, string>>)
        // A one-line description of the backend + spec for the start-requested event.
        (specSummary: string)
        (environmentId: string)
        : SessionEnvironment =

        let mutable running : Sandbox option = None

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
                                      SpecSummary = specSummary })
                    let! prepared =
                        async {
                            match! preparePolicy () with
                            | Error reason -> return Error reason
                            | Ok policy -> return! createSandbox policy
                        }
                    match prepared with
                    | Ok sandbox ->
                        running <- Some sandbox
                        do! append (EnvironmentStarted
                                        { EnvironmentId = environmentId
                                          ContainerRef = sandbox.Ref })
                        return EnvironmentAvailable
                    | Error reason ->
                        do! append (EnvironmentStartFailed { EnvironmentId = environmentId; Reason = reason })
                        return EnvironmentUnavailable reason
            }

        let execute (request: CommandRequest) (onCallerChunk: CommandOutputChunk -> unit) : Async<CommandResult> =
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
                | Some sandbox ->
                    do! append (CommandStarted { CommandId = request.CommandId })
                    let onChunk (stream: OutputStream, text: string) =
                        let chunk : CommandOutputChunk = { CommandId = request.CommandId; Stream = stream; Text = text }
                        onCallerChunk chunk
                        Async.StartImmediate (
                            append (CommandOutputReceived
                                        { CommandId = chunk.CommandId
                                          Stream = chunk.Stream
                                          Text = chunk.Text }))
                    let! spawned =
                        sandbox.Spawn
                            { Executable = request.Executable
                              Arguments = request.Arguments
                              Env = request.Environment
                              WorkingDirectory = request.WorkingDirectory }
                            onChunk
                    let! result =
                        async {
                            match spawned with
                            | Error reason -> return CommandExecutionFailed reason
                            | Ok handle ->
                                match! raceTimeout request.Timeout handle.Exited with
                                | None ->
                                    handle.Kill ()
                                    return CommandTimedOut
                                | Some (SandboxExited 0) -> return CommandSucceeded 0
                                | Some (SandboxExited code) -> return CommandFailed code
                                | Some (SandboxRunFailed reason) -> return CommandExecutionFailed reason
                        }
                    do! append (CommandCompleted { CommandId = request.CommandId; Result = result })
                    return result
            }

        let stop () : Async<unit> =
            async {
                match running with
                | None -> return ()
                | Some sandbox ->
                    do! append (EnvironmentStopRequested { EnvironmentId = environmentId })
                    do! sandbox.Dispose ()
                    running <- None
                    do! append (EnvironmentStopped { EnvironmentId = environmentId })
            }

        let spawn (exec: SandboxExec) (onChunk: OutputStream * string -> unit) =
            async {
                match running with
                | None -> return Error "no running environment"
                | Some sandbox -> return! sandbox.Spawn exec onChunk
            }

        { Ensure = ensure
          Execute = execute
          Spawn = spawn
          Stop = stop
          CurrentRef = fun () -> running |> Option.map (fun s -> s.Ref) }
