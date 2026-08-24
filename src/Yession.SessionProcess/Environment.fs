namespace Yession.SessionProcess

open System
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Agent

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
          /// Spawn a process in the running environment. The caller records what happened
          /// in its own terms: a block's output belongs in its terminal's transcript and its
          /// lifecycle in the block events, so there is no command-event lifecycle here —
          /// routing output through one would write every printed byte into the event log a
          /// second time. (Step 13's `Execute`, which did, retired with the merged tool in
          /// Plan 13 stage 3b: nothing fed the command log any more.)
          /// The environment is still the one gate — a spawn with nothing running is an
          /// error.
          Spawn : SandboxExec -> (OutputStream * string -> unit) -> Async<Result<SandboxProcessHandle, string>>
          /// Spawn on a pseudo-terminal, when the running backend has one (Plan 13, stage
          /// 2d). `Error` rather than an option, because whether a pty is available is a
          /// property of the RUNNING sandbox and there may not be one yet — a caller has to
          /// handle "no environment" regardless, and folding "this backend has no pty" into
          /// the same shape keeps it from needing two ways to be told no.
          SpawnPty : SandboxExec -> int -> int -> (string -> unit) -> Async<Result<PtyHandle, string>>
          /// Stop the environment if it is running (recorded as events).
          Stop : unit -> Async<unit>
          /// The running sandbox's backend reference, if any.
          CurrentRef : unit -> string option }

    /// A session with no environment: needs are recorded as unavailable without any
    /// sandbox existing in the Process.
    let unavailable : SessionEnvironment =
        { Ensure = fun _ _ -> async { return EnvironmentUnavailable "this session has no environment" }
          Spawn = fun _ _ -> async { return Error "this session has no environment" }
          SpawnPty = fun _ _ _ _ -> async { return Error "this session has no environment" }
          Stop = fun () -> async { return () }
          CurrentRef = fun () -> None }

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

        let spawnPty (exec: SandboxExec) (cols: int) (rows: int) (onOutput: string -> unit) =
            async {
                match running with
                | None -> return Error "no running environment"
                | Some sandbox ->
                    match sandbox.SpawnPty with
                    | None -> return Error "this backend cannot open a pseudo-terminal"
                    | Some spawn -> return! spawn exec cols rows onOutput
            }

        { Ensure = ensure
          Spawn = spawn
          SpawnPty = spawnPty
          Stop = stop
          CurrentRef = fun () -> running |> Option.map (fun s -> s.Ref) }
