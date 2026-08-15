namespace Yession.SessionProcess

open System
open Yession.Domain

/// The approval gate for commands (Plan 15, stage 3b).
///
/// This is the terminal drain with the terminal-shaped parts removed. Both write a pending
/// act into the collaborative doc where every peer can read and refuse it, both ask
/// `ApprovalMode.requiresApproval` whether it must wait, both hand the caller a `QueueId` as
/// the handle, and both record the outcome in the log. What the terminal adds is a serial
/// scheduler — a shell has one working directory and one stdin — and a command has nothing
/// to serialize, so it runs the moment its verdict arrives.
///
/// It is a DRAIN, not a set of waiting continuations, and that is what makes an approval
/// outlive the process that proposed the act. Everything needed to carry a command out is on
/// the act — the tool, its arguments, and whose credential it runs on — so a session that
/// restarted mid-review picks the act up and honours the approval, exactly as the terminal
/// queue has always done with a command line. What the caller holds is only a place to come
/// back for the outcome.
module CommandGates =

    // The outcome shapes (`CommandStatus`, `CommandOutcome`, `GatedCall`) live in
    // `Yession.Domain.Agent`, beside `TerminalCommandOutcome` and for its reason: they are
    // what a CAPABILITY answers with, and the Domain is where a capability's vocabulary is.

    type CommandGate =
        { /// Run a command through its gate. Ungated (the default for every command) this
          /// is the call itself, with no entry written and no event appended — today's
          /// behaviour, unchanged, which is the whole point of the default being `AutoRun`.
          Run : RunGatedCommand
          /// Resume a handle `Run` yielded.
          Read : QueueId -> Async<Result<CommandOutcome, string>>
          /// Whether a handle names a command act rather than a terminal one — the question
          /// the single `check_pending` tool asks before it decides which side to read.
          Knows : QueueId -> bool
          /// Settle every command act whose verdict is in. Wired to the doc's change feed
          /// and called at boot; exposed so the Host can drain once before anything else
          /// reads the session, the way it drains the terminal queue.
          Drain : unit -> unit }

    let unavailable : CommandGate =
        { Run = fun call -> async { return Error ("no gate to run " + call.Command.Tool + " through") }
          Read = fun _ -> async { return Error "this session has no command gate" }
          Knows = fun _ -> false
          Drain = ignore }

    /// Parse the operator's gate configuration: a comma- or space-separated list of tool
    /// names, each of which becomes `ApproveAgent` for its `ForCommand` subject. Empty by
    /// default, so a session nobody configured behaves exactly as it did before this stage.
    ///
    /// A name that is not a gated command is an ERROR, not a name to skip. A typo here reads
    /// as prudence and behaves as a blind spot: the operator believes `add_repo` needs an
    /// approval, and it silently does not — the same failure shape as a test tier that drops
    /// a capability it was asked for and goes green.
    ///
    /// The interim form of what `yession.yaml` will own. It seeds the synced register rather
    /// than being consulted at decision time, which is what lets a human change their mind
    /// mid-session without a restart — and is why there is only ever ONE place the mode is
    /// read from.
    let parseConfiguredGates (raw: string) : Result<GatedCommand list, string> =
        let named =
            raw.Split ([| ','; ' '; ';' |])
            |> Array.map (fun name -> name.Trim ())
            |> Array.filter (fun name -> name <> "")
            |> Array.distinct
            |> Array.sort
            |> List.ofArray
        let unknown = named |> List.filter (fun name -> Option.isNone (GatedCommands.tryFind name))
        if not (List.isEmpty unknown) then
            Error (
                sprintf
                    "these are not gated commands: %s. The ones that can be gated are: %s"
                    (String.concat ", " unknown)
                    (GatedCommands.all |> List.map (fun c -> c.Tool) |> String.concat ", "))
        else Ok (named |> List.choose GatedCommands.tryFind)

    /// A parked act's verdict, as the drain reads it off the doc.
    type private Verdict =
        | Approved of PeerId option
        | Refused of PeerId * string option
        | Undecided

    let private verdictOf (act: PendingAct) (synced: SyncedSessionState) : Verdict =
        // A refusal outranks an approval, exactly as it does in the terminal drain: a
        // policy that would have released the act must not beat a person who said no.
        match act.RejectedBy, act.ApprovedBy with
        | Some by, _ -> Refused (by, act.RejectedReason)
        | None, approved when not (ApprovalMode.requiresApproval (SyncedSessionState.gateOf act.Subject synced) (ActProvenance.author act.Provenance)) ->
            Approved approved
        | None, Some by -> Approved (Some by)
        | None, None -> Undecided

    /// `syncedOf` is read fresh on every observation, so the gate holds no copy of the doc
    /// and a mode changed mid-wait re-decides the act that is waiting.
    ///
    /// `dispatch` is a GETTER: the table is assembled where the capabilities are, which is
    /// after the Host that owns this gate exists.
    let create
        (doc: Yjs.Y.Doc)
        (syncedOf: unit -> Result<SyncedSessionState, Ylmish.Codec.Error list>)
        (dispatch: unit -> CommandDispatch)
        (appendAs: ActorRef -> SessionEvent -> Async<unit>)
        (mintQueueId: unit -> QueueId)
        (mintMessageId: unit -> MessageId)
        (now: unit -> DateTimeOffset)
        (onChanged: TerminalCommands.OnChanged)
        : CommandGate =

        // Outcomes, by handle, so a caller can come back for one. NOT where the act lives —
        // the act lives in the doc, and the drain below settles it from there. An entry is
        // never dropped once it resolves: a resume that answers "no such command" for
        // something that just succeeded is the one answer it must never give.
        let outcomes = System.Collections.Generic.Dictionary<string, CommandOutcome> ()
        // Handles this process is already settling, so a re-entrant drain does not run one
        // act twice. The doc removal is what makes it permanent; this covers the window.
        let settling = System.Collections.Generic.HashSet<string> ()

        let record (handle: QueueId) (outcome: CommandOutcome) =
            outcomes.[QueueId.value handle] <- outcome

        let refuse (handle: QueueId) (tool: string) (summary: string) (author: ActorRef) (by: ActorRef) (reason: string option) =
            async {
                do!
                    appendAs
                        by
                        (SessionEvent.CommandRefused
                            { MessageId = mintMessageId ()
                              QueueId = handle
                              Tool = tool
                              Summary = summary
                              Author = author
                              RejectedBy = by
                              Reason = reason })
                SyncedStateSync.removePending doc [ handle ]
                record handle { Handle = Some handle; Tool = tool; Summary = summary; Status = CommandRefusedBy (by, reason) }
            }

        /// Carry out one act whose verdict is in. Everything it needs comes off the ACT, so
        /// this works for an act proposed by a process that is no longer running — which is
        /// the whole reason `args` and `OnBehalfOf` are on it.
        let settleOne (handle: QueueId) (act: PendingAct) (tool: string) (args: string) (summary: string) (verdict: Verdict) =
            async {
                match verdict with
                | Undecided -> ()
                | Refused (by, reason) -> do! refuse handle tool summary (ActProvenance.author act.Provenance) (PeerRef by) reason
                | Approved approver ->
                    match Map.tryFind tool (dispatch ()) with
                    // A command the running build does not have: refuse it rather than leave
                    // it, and say which — a card that cannot be carried out is worse than a
                    // refusal, and this is what a rename or a downgrade looks like from here.
                    | None ->
                        do!
                            refuse handle tool summary (ActProvenance.author act.Provenance) ActorRef.System
                                (Some (sprintf "this session has no command called '%s'" tool))
                    | Some run ->
                        // The entry goes BEFORE the command runs, for the terminal drain's
                        // reason: the act is consumed the moment its verdict is in, and
                        // leaving the card up while the work happens invites a second
                        // approval of the same thing.
                        SyncedStateSync.removePending doc [ handle ]
                        let! result =
                            run
                                { Args = args
                                  Provenance = act.Provenance |> ActProvenance.approvedBy approver }
                        record
                            handle
                            { Handle = Some handle
                              Tool = tool
                              Summary = summary
                              Status =
                                match result with
                                | Ok text -> CommandRan text
                                | Error reason -> CommandRan ("failed: " + reason) }
            }

        /// Every command act in the doc whose verdict is in, settled. Runs at boot and on
        /// every doc change — the terminal's arrangement exactly, and the reason an approval
        /// takes effect whether or not the turn that proposed it is still alive.
        let drain () =
            match syncedOf () with
            | Error _ -> ()
            | Ok synced ->
                for handle, act in Map.toList synced.Pending do
                    match act.Payload with
                    | CommandLine -> ()
                    | CommandCall (tool, args, summary) ->
                        let key = QueueId.value handle
                        if not (settling.Contains key) then
                            match verdictOf act synced with
                            | Undecided -> ()
                            | verdict ->
                                settling.Add key |> ignore
                                Async.StartImmediate (
                                    async {
                                        try do! settleOne handle act tool args summary verdict
                                        finally settling.Remove key |> ignore
                                    })

        // Wake on every doc change, for the drain's whole life. Unsubscribed never: the gate
        // lives as long as the session does, and an act can be approved at any point in it.
        onChanged drain |> ignore

        /// Wait for a parked act for as long as the terminal waits for an approval, then
        /// yield. Same bound, same reason: a supervised session chains normally, and an
        /// unsupervised one does not hold the turn open.
        let rec awaitSettled (call: GatedCall) (handle: QueueId) (startedAt: DateTimeOffset) =
            async {
                match outcomes.TryGetValue (QueueId.value handle) with
                | true, outcome -> return outcome
                | false, _ when now () - startedAt >= TerminalCommands.approvalGrace ->
                    return
                        { Handle = Some handle
                          Tool = call.Command.Tool
                          Summary = call.Summary
                          Status = CommandAwaitingApproval }
                | false, _ ->
                    do! TerminalCommands.nextWake onChanged
                    return! awaitSettled call handle startedAt
            }

        let run (call: GatedCall) =
            async {
                match syncedOf () with
                | Error _ -> return Error "the session's collaborative state could not be read"
                | Ok synced ->
                    let subject = GatedCommands.subject call.Command
                    let handle = mintQueueId ()
                    // The act is written whether or not it is gated, and that is what makes
                    // the two paths ONE path: an ungated act is written, drained and removed
                    // in the same breath, so nothing about carrying a command out depends on
                    // whether somebody had to say yes to it.
                    SyncedStateSync.enqueueCommandCall
                        doc
                        handle
                        call.Command.Tool
                        call.Args
                        call.Summary
                        call.Provenance
                        (PendingAct.nextOrder subject synced.Pending)
                    drain ()
                    let! settled = awaitSettled call handle (now ())
                    return Ok settled
            }

        let read (handle: QueueId) =
            async {
                match outcomes.TryGetValue (QueueId.value handle) with
                | true, outcome -> return Ok outcome
                | false, _ ->
                    match syncedOf () with
                    | Ok synced ->
                        match Map.tryFind handle synced.Pending with
                        | Some act ->
                            match act.Payload with
                            | CommandCall (tool, _, summary) ->
                                let call =
                                    { Command = GatedCommands.tryFind tool |> Option.defaultValue { Tool = tool; Title = tool }
                                      Args = ""
                                      Summary = summary
                                      Provenance = act.Provenance }
                                let! settled = awaitSettled call handle (now ())
                                return Ok settled
                            | CommandLine -> return Error "that handle names a terminal command"
                        | None -> return Error "no such pending command"
                    | Error _ -> return Error "the session's collaborative state could not be read"
            }

        { Run = run
          Read = read
          // A handle this process settled, or one still waiting in the doc as a command act.
          // The second half is what lets `check_pending` resolve a handle from a session that
          // has since restarted — the act is in the doc, so it is still answerable.
          Knows =
            fun handle ->
                outcomes.ContainsKey (QueueId.value handle)
                || (match syncedOf () with
                    | Ok synced ->
                        match Map.tryFind handle synced.Pending with
                        | Some act -> (match act.Payload with CommandCall _ -> true | CommandLine -> false)
                        | None -> false
                    | Error _ -> false)
          Drain = drain }
