namespace Yession.SessionProcess

open System
open Yession.Domain

/// The gate for structured commands (Plan 15, stage 3b; Plan 23).
///
/// Every command passes the CLASSIFIER on its way to the dispatch table: approved, it runs
/// at once; rejected, the refusal is appended to the log — attributed, with the reason — and
/// the model is told REFUSED rather than handed a silence it would retry another way. There
/// is no parking and no verdict register: classification happens at the moment of the call,
/// so the only thing a caller can still be waiting on afterwards is the WORK.
///
/// That wait is bounded by `TerminalCommands.commandTimeout`, the terminal side's process
/// deadline, and yields `CommandRunning` with a handle rather than holding the turn — a
/// six-second clone is work in progress, not a decision anybody was offered.
module CommandGates =

    // The outcome shapes (`CommandStatus`, `CommandOutcome`, `GatedCall`) live in
    // `Yession.Domain.Agent`, beside `TerminalCommandOutcome` and for its reason: they are
    // what a CAPABILITY answers with, and the Domain is where a capability's vocabulary is.

    type CommandGate =
        { /// Run a command through the classifier and, approved, the dispatch table.
          Run : RunGatedCommand
          /// Resume a handle `Run` yielded.
          Read : QueueId -> Async<Result<CommandOutcome, string>>
          /// Whether a handle names a command act rather than a terminal one — the question
          /// the single `check_pending` tool asks before it decides which side to read.
          Knows : QueueId -> bool }

    let unavailable : CommandGate =
        { Run = fun call -> async { return Error ("no gate to run " + call.Tool + " through") }
          Read = fun _ -> async { return Error "this session has no command gate" }
          Knows = fun _ -> false }

    /// `dispatch` is a GETTER: the table is assembled where the capabilities are, which is
    /// after the Host that owns this gate exists.
    let create
        (classifier: Classifier)
        (dispatch: unit -> CommandDispatch)
        (appendAs: ActorRef -> SessionEvent -> Async<unit>)
        (mintQueueId: unit -> QueueId)
        (mintMessageId: unit -> MessageId)
        (now: unit -> DateTimeOffset)
        (onChanged: TerminalCommands.OnChanged)
        : CommandGate =

        // Outcomes, by handle, so a caller can come back for one. An entry is never dropped
        // once it resolves: a resume that answers "no such command" for something that just
        // succeeded is the one answer it must never give.
        let outcomes = System.Collections.Generic.Dictionary<string, CommandOutcome> ()
        // Work in flight, by handle — what `Read` re-waits on. A handle leaves this table
        // the moment its outcome is recorded, so a handle in neither is one nobody minted.
        let inflight = System.Collections.Generic.Dictionary<string, GatedCall> ()

        let record (handle: QueueId) (outcome: CommandOutcome) =
            outcomes.[QueueId.value handle] <- outcome
            inflight.Remove (QueueId.value handle) |> ignore

        let refuse (handle: QueueId) (tool: string) (summary: string) (author: ActorRef) (reason: string option) =
            async {
                do!
                    appendAs
                        ActorRef.System
                        (SessionEvent.CommandRefused
                            { MessageId = mintMessageId ()
                              QueueId = handle
                              Tool = tool
                              Summary = summary
                              Author = author
                              RejectedBy = ActorRef.System
                              Reason = reason })
                record handle { Handle = Some handle; Tool = tool; Summary = summary; Status = CommandRefusedBy (ActorRef.System, reason) }
            }

        /// Wait the work out, then answer. The deadline is measured from the start of the
        /// work and bounds only the work: classification already happened, synchronously
        /// from the caller's point of view, before this wait began.
        ///
        /// The loop wakes on the 100ms tick as well as on log appends, and needs to: a
        /// dispatch that fails records its outcome without appending anything, so the tick
        /// is what guarantees the waiter observes it.
        let rec awaitSettled (call: GatedCall) (handle: QueueId) (startedAt: DateTimeOffset) =
            async {
                match outcomes.TryGetValue (QueueId.value handle) with
                | true, outcome -> return outcome
                | false, _ ->
                    if now () - startedAt >= TerminalCommands.commandTimeout then
                        return { Handle = Some handle; Tool = call.Tool; Summary = call.Summary; Status = CommandRunning }
                    else
                        do! TerminalCommands.nextWake onChanged
                        return! awaitSettled call handle startedAt
            }

        let run (call: GatedCall) =
            async {
                let handle = mintQueueId ()
                match! classifier (Authority.author call.Authority) (CommandAct (call.Tool, call.Args, call.Summary)) with
                | Rejected reason ->
                    do! refuse handle call.Tool call.Summary (Authority.author call.Authority) (Some reason)
                    return Ok { Handle = Some handle; Tool = call.Tool; Summary = call.Summary; Status = CommandRefusedBy (ActorRef.System, Some reason) }
                | Approved ->
                    match Map.tryFind call.Tool (dispatch ()) with
                    // A command the running build does not have: refuse it rather than fail
                    // it, and say which — this is what a rename or a downgrade looks like
                    // from here, and a refusal is the one shape the model will not retry
                    // another way.
                    | None ->
                        let reason = Some (sprintf "this session has no command called '%s'" call.Tool)
                        do! refuse handle call.Tool call.Summary (Authority.author call.Authority) reason
                        return Ok { Handle = Some handle; Tool = call.Tool; Summary = call.Summary; Status = CommandRefusedBy (ActorRef.System, reason) }
                    | Some runIt ->
                        inflight.[QueueId.value handle] <- call
                        Async.StartImmediate (
                            async {
                                let! result = runIt { Args = call.Args; Authority = call.Authority }
                                record
                                    handle
                                    { Handle = Some handle
                                      Tool = call.Tool
                                      Summary = call.Summary
                                      Status =
                                        match result with
                                        | Ok text -> CommandRan text
                                        | Error reason -> CommandRan ("failed: " + reason) }
                            })
                        let! settled = awaitSettled call handle (now ())
                        return Ok settled
            }

        let read (handle: QueueId) =
            async {
                match outcomes.TryGetValue (QueueId.value handle) with
                | true, outcome -> return Ok outcome
                | false, _ ->
                    match inflight.TryGetValue (QueueId.value handle) with
                    // A resume gets the full deadline again, measured from the resume: the
                    // work is the same work, but the caller asked a fresh question.
                    | true, call ->
                        let! settled = awaitSettled call handle (now ())
                        return Ok settled
                    | false, _ -> return Error "no such pending command"
            }

        { Run = run
          Read = read
          Knows =
            fun handle ->
                outcomes.ContainsKey (QueueId.value handle) || inflight.ContainsKey (QueueId.value handle) }
