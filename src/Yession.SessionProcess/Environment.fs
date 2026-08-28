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
          CurrentRef : unit -> string option
          /// Where the RUNNING sandbox holds something other than what its resources named,
          /// one line each, empty when this host gave the whole of it.
          ///
          /// Read off the policy the sandbox was built from, and kept beside the sandbox for
          /// the same reason `verify` takes that policy: a backend that quietly widened
          /// something would otherwise be asked to report on itself. Empty while nothing is
          /// running, because a sandbox that does not exist holds nothing — never the last
          /// one's answer.
          Realisation : unit -> string list }

    /// A session with no environment: needs are recorded as unavailable without any
    /// sandbox existing in the Process.
    let unavailable : SessionEnvironment =
        { Ensure = fun _ _ -> async { return EnvironmentUnavailable "this session has no environment" }
          Spawn = fun _ _ -> async { return Error "this session has no environment" }
          SpawnPty = fun _ _ _ _ -> async { return Error "this session has no environment" }
          Stop = fun () -> async { return () }
          CurrentRef = fun () -> None
          Realisation = fun () -> [] }

    /// Run this sandbox's own checks in it, once, before anybody else can reach it.
    ///
    /// The policy the sandbox was BUILT from is what gets checked, which is why this
    /// takes the prepared policy rather than asking the sandbox what it thinks it holds:
    /// a backend that quietly dropped something would otherwise be asked to grade its own
    /// work.
    ///
    /// A sandbox that cannot run `/bin/sh` at all fails here too, and says so. That is
    /// not a false alarm — a sandbox that cannot run a shell cannot run a command, which
    /// is the only thing anybody wants one for.
    let verify (policy: SandboxPolicy) (sandbox: Sandbox) : Async<Result<unit, string>> =
        async {
            match SandboxVerification.plan policy with
            | [] -> return Ok ()
            | checks ->
                let said = System.Text.StringBuilder ()
                let exec =
                    { Executable = "/bin/sh"
                      Arguments = [ "-c"; SandboxVerification.program checks ]
                      Env = Map.empty
                      WorkingDirectory = None }
                match! sandbox.Spawn exec (fun (_, chunk) -> said.Append chunk |> ignore) with
                | Error reason ->
                    return Error (sprintf "this sandbox cannot run a command at all: %s" reason)
                | Ok handle ->
                    match! handle.Exited with
                    | SandboxExited 0 -> return Ok ()
                    | SandboxExited _ ->
                        return Error (SandboxVerification.explain checks (said.ToString ()))
                    | SandboxRunFailed reason ->
                        return Error (sprintf "this sandbox cannot run a command at all: %s" reason)
        }

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

        // The sandbox and what this host made of its grant, together in ONE cell. Two
        // mutables would be two things a stop has to remember to clear, and the one that got
        // forgotten would be a closed sandbox still answering for what it used to hold.
        let mutable running : (Sandbox * string list) option = None

        let append event =
            async {
                let! _ = log.Append ActorRef.SessionProcess event
                return ()
            }


        /// What the log already says became of this environment — a failure reason when the
        /// last thing recorded about it was a start that failed, `None` when it started,
        /// stopped, or was never attempted.
        ///
        /// Read from the log rather than remembered in a field, and that is the whole point:
        /// `WorkSandboxes` builds a FRESH environment for every start it attempts and keeps
        /// only the ones that came up, so a failed attempt's object — and any memory it could
        /// have held — is discarded before the next attempt asks. The log is the only thing
        /// that survives it. It is also what makes a process restart quiet rather than
        /// re-announcing everything, which is the same rule `SessionMain` computes the MCP
        /// declaration delta by.
        ///
        /// Only the events carrying an environment id count, which excludes the need itself:
        /// a need is somebody ASKING, not something that became of the environment.
        let lastFailure () : Async<string option> =
            async {
                let! page = log.Read None Int32.MaxValue
                return
                    page.Events
                    |> List.choose (fun e ->
                        match e.Event with
                        | EnvironmentStarted s when s.EnvironmentId = environmentId -> Some None
                        | EnvironmentStopped s when s.EnvironmentId = environmentId -> Some None
                        | EnvironmentStartFailed f when f.EnvironmentId = environmentId -> Some (Some f.Reason)
                        | _ -> None)
                    |> List.tryLast
                    |> Option.flatten
            }

        let ensure (agentTurnId: AgentTurnId option) (reason: string) : Async<EnsureEnvironmentResult> =
            async {
                let need () = append (EnvironmentNeedIdentified { Reason = reason; AgentTurnId = agentTurnId })
                match running with
                | Some _ ->
                    // Already running: the environment is preserved across needs, and the
                    // need is still recorded. That record is the point — an environment
                    // being REUSED rather than started a second time is only visible
                    // because the second need is in the log beside the first start.
                    do! need ()
                    return EnvironmentAvailable
                | None ->
                    // Attempted before anything is recorded, so that a repeat of a failure
                    // the log already ends with can be left unsaid. The successful sequence
                    // is unchanged — need, start-requested, started, in that order — because
                    // what moved is when they are written, not what they say or their order.
                    // The policy is carried out of this block beside the sandbox, because
                    // what gets verified is what the sandbox was BUILT from.
                    let! prepared =
                        async {
                            match! preparePolicy () with
                            | Error reason -> return Error reason
                            | Ok policy ->
                                match! createSandbox policy with
                                | Error reason -> return Error reason
                                | Ok sandbox -> return Ok (policy, sandbox)
                        }
                    let requested () =
                        append (EnvironmentStartRequested
                                    { EnvironmentId = environmentId
                                      SpecSummary = specSummary })
                    // Verified BEFORE it is declared started, and here rather than inside
                    // each backend's `create`: three backends would need three copies of
                    // this, free to disagree about what a working sandbox is. A failure
                    // takes the existing refusal path unchanged — `EnvironmentStartFailed`
                    // carries the sentence, the no-repeat rule above still applies, and
                    // `RepoSandboxes` already turns it into a refusal a person can read.
                    let! prepared =
                        async {
                            match prepared with
                            | Error reason -> return Error reason
                            | Ok (policy, sandbox) ->
                                match! verify policy sandbox with
                                | Ok () ->
                                    return Ok (sandbox, policy.Realisation |> List.map RealisedClosure.describeDifference)
                                | Error reason ->
                                    // Disposed rather than left running. A sandbox that
                                    // failed its own checks is not a sandbox anybody should
                                    // be able to reach, and `running` is never set for it.
                                    do! sandbox.Dispose ()
                                    return Error reason
                        }
                    match prepared with
                    | Ok (sandbox, realisation) ->
                        running <- Some (sandbox, realisation)
                        do! need ()
                        do! requested ()
                        do! append (EnvironmentStarted
                                        { EnvironmentId = environmentId
                                          ContainerRef = sandbox.Ref })
                        return EnvironmentAvailable
                    | Error reason ->
                        // A refusal the log already ends with is not news. Without this, an
                        // ask that cannot start records three events EVERY time it is made —
                        // and the fold that asks re-runs after every repo verb, so a
                        // declaration over the operator's ceiling grew the log without bound
                        // while changing nothing about the session.
                        //
                        // The attempt itself still happens, which is what keeps this from
                        // becoming a cache of refusals: a credential signed in mid-session, a
                        // daemon that came back, an operator who widened the ceiling and
                        // restarted — each of those changes the outcome, and a changed
                        // outcome is recorded the first time it differs.
                        match! lastFailure () with
                        | Some said when said = reason -> ()
                        | _ ->
                            do! need ()
                            do! requested ()
                            do! append (EnvironmentStartFailed { EnvironmentId = environmentId; Reason = reason })
                        return EnvironmentUnavailable reason
            }

        let stop () : Async<unit> =
            async {
                match running with
                | None -> return ()
                | Some (sandbox, _) ->
                    do! append (EnvironmentStopRequested { EnvironmentId = environmentId })
                    do! sandbox.Dispose ()
                    running <- None
                    do! append (EnvironmentStopped { EnvironmentId = environmentId })
            }

        let spawn (exec: SandboxExec) (onChunk: OutputStream * string -> unit) =
            async {
                match running with
                | None -> return Error "no running environment"
                | Some (sandbox, _) -> return! sandbox.Spawn exec onChunk
            }

        let spawnPty (exec: SandboxExec) (cols: int) (rows: int) (onOutput: string -> unit) =
            async {
                match running with
                | None -> return Error "no running environment"
                | Some (sandbox, _) ->
                    match sandbox.SpawnPty with
                    | None -> return Error "this backend cannot open a pseudo-terminal"
                    | Some spawn -> return! spawn exec cols rows onOutput
            }

        { Ensure = ensure
          Spawn = spawn
          SpawnPty = spawnPty
          Stop = stop
          CurrentRef = fun () -> running |> Option.map (fun (sandbox, _) -> sandbox.Ref)
          Realisation = fun () -> running |> Option.map snd |> Option.defaultValue [] }
