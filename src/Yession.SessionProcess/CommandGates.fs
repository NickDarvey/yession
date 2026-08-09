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
/// What a command does NOT get is restart-durability. A terminal command survives a restart
/// because the doc holds its whole payload — a line of text the drain can run from cold. A
/// structured call's arguments are typed, and the entry holds only what a human was shown,
/// so the thing that runs it is the continuation this process is holding. A restart
/// therefore refuses whatever was still parked (`sweepAtBoot`), visibly and with a reason,
/// rather than leaving a card on everyone's screen that no approval can ever release.
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
          Knows : QueueId -> bool }

    let unavailable : CommandGate =
        { Run = fun call run -> async { let! _ = run None in return Error ("no gate for " + call.Tool) }
          Read = fun _ -> async { return Error "this session has no command gate" }
          Knows = fun _ -> false }

    /// A gate that runs everything immediately: no doc, no modes, no waiting. What a Host
    /// with no collaborative state composes, and what the ungated path degrades to.
    let passthrough : CommandGate =
        { Run =
            fun call run ->
                async {
                    match! run None with
                    | Error reason -> return Error reason
                    | Ok text ->
                        return
                            Ok
                                { Handle = None
                                  Tool = call.Tool
                                  Summary = call.Summary
                                  Status = CommandRan text }
                }
          Read = fun _ -> async { return Error "no such pending command" }
          Knows = fun _ -> false }

    /// Parse the operator's gate configuration: a comma- or space-separated list of tool
    /// names, each of which becomes `ApproveAgent` for its `ForCommand` subject. Empty by
    /// default, so a session nobody configured behaves exactly as it did before this stage.
    ///
    /// The interim form of what `yession.yaml` will own. It seeds the synced register rather
    /// than being consulted at decision time, which is what lets a human change their mind
    /// mid-session without a restart — and is why there is only ever ONE place the mode is
    /// read from.
    let parseConfiguredGates (raw: string) : string list =
        raw.Split ([| ','; ' '; ';' |])
        |> Array.map (fun name -> name.Trim ())
        |> Array.filter (fun name -> name <> "")
        |> Array.distinct
        |> Array.sort
        |> List.ofArray

    /// A parked act's verdict, as the watcher reads it off the doc.
    type private Verdict =
        | Approved of PeerId option
        | Refused of PeerId * string option
        | Undecided
        /// The entry is gone — a peer deleted it, which is a withdrawal rather than a
        /// refusal and is the one outcome that records nothing.
        | Withdrawn

    let private verdictOf (handle: QueueId) (synced: SyncedSessionState) : Verdict =
        match Map.tryFind handle synced.Pending with
        | None -> Withdrawn
        | Some act ->
            // A refusal outranks an approval, exactly as it does in the terminal drain: a
            // policy that would have released the act must not beat a person who said no.
            match act.RejectedBy, act.ApprovedBy with
            | Some by, _ -> Refused (by, act.RejectedReason)
            | None, approved when not (ApprovalMode.requiresApproval (SyncedSessionState.gateOf act.Subject synced) act.Author) ->
                Approved approved
            | None, Some by -> Approved (Some by)
            | None, None -> Undecided

    /// `syncedOf` is read fresh on every observation, so the gate holds no copy of the doc
    /// and a mode changed mid-wait re-decides the act that is waiting.
    let create
        (doc: Yjs.Y.Doc)
        (syncedOf: unit -> Result<SyncedSessionState, Ylmish.Codec.Error list>)
        (appendAs: ActorRef -> SessionEvent -> Async<unit>)
        (mintQueueId: unit -> QueueId)
        (mintMessageId: unit -> MessageId)
        (now: unit -> DateTimeOffset)
        (onChanged: TerminalCommands.OnChanged)
        : CommandGate =

        // The acts this process parked, by handle, each holding its outcome once it has one.
        // An entry is NOT dropped when it resolves: the handle exists so the agent can come
        // back later, and a resume that answers "no such command" for something that just
        // succeeded is the one answer it must never give. In memory on purpose, and the
        // reason `sweepAtBoot` exists: see the module comment.
        let parked = System.Collections.Generic.Dictionary<string, CommandOutcome option ref> ()

        let outcome (call: GatedCall) (handle: QueueId option) (status: CommandStatus) =
            { Handle = handle; Tool = call.Tool; Summary = call.Summary; Status = status }

        let recordRefusal (call: GatedCall) (handle: QueueId) (by: PeerId) (reason: string option) =
            async {
                do!
                    appendAs
                        (PeerRef by)
                        (SessionEvent.CommandRefused
                            { MessageId = mintMessageId ()
                              QueueId = handle
                              Tool = call.Tool
                              Summary = call.Summary
                              Author = call.Author
                              RejectedBy = PeerRef by
                              Reason = reason })
                SyncedStateSync.removePending doc [ handle ]
            }

        /// Watch one parked act to its end, and carry it out. Runs detached from the MCP
        /// call: an approval must take effect whether or not anybody is still waiting on it,
        /// or a human pressing approve would watch nothing happen.
        let rec settle
            (call: GatedCall)
            (handle: QueueId)
            (run: ActorRef option -> Async<Result<string, string>>)
            (slot: CommandOutcome option ref)
            : Async<unit> =
            async {
                match syncedOf () with
                | Error _ ->
                    do! TerminalCommands.nextWake onChanged
                    return! settle call handle run slot
                | Ok synced ->
                    match verdictOf handle synced with
                    | Undecided ->
                        do! TerminalCommands.nextWake onChanged
                        return! settle call handle run slot
                    | Withdrawn ->
                        // Nothing recorded: a peer taking their own proposal back is not a
                        // verdict, and the agent learns it as the act simply not existing.
                        slot.Value <- Some (outcome call (Some handle) (CommandRefusedBy (ActorRef.System, Some "it was withdrawn before anyone decided")))
                    | Refused (by, reason) ->
                        do! recordRefusal call handle by reason
                        slot.Value <- Some (outcome call (Some handle) (CommandRefusedBy (PeerRef by, reason)))
                    | Approved approver ->
                        // The entry goes BEFORE the command runs, for the terminal drain's
                        // reason: the act has been consumed at the moment its verdict is in,
                        // and leaving the card up while the work happens invites a second
                        // approval of the same thing.
                        SyncedStateSync.removePending doc [ handle ]
                        let! result = run (approver |> Option.map PeerRef)
                        slot.Value <-
                            Some (
                                match result with
                                | Ok text -> outcome call (Some handle) (CommandRan text)
                                | Error reason -> outcome call (Some handle) (CommandRan ("failed: " + reason)))
            }

        /// Wait for a parked act for as long as the terminal waits for an approval, then
        /// yield. Same bound, same reason: a supervised session chains normally, and an
        /// unsupervised one does not hold the turn open.
        let rec awaitSettled (handle: QueueId) (slot: CommandOutcome option ref) (startedAt: DateTimeOffset) =
            async {
                match slot.Value with
                | Some outcome -> return outcome
                | None when now () - startedAt >= TerminalCommands.approvalGrace ->
                    return
                        { Handle = Some handle
                          Tool = ""
                          Summary = ""
                          Status = CommandAwaitingApproval }
                | None ->
                    do! TerminalCommands.nextWake onChanged
                    return! awaitSettled handle slot startedAt
            }

        let run (call: GatedCall) (thunk: ActorRef option -> Async<Result<string, string>>) =
            async {
                match syncedOf () with
                | Error _ -> return Error "the session's collaborative state could not be read"
                | Ok synced ->
                    let subject = ForCommand call.Tool
                    if not (ApprovalMode.requiresApproval (SyncedSessionState.gateOf subject synced) call.Author) then
                        // Ungated: the call, and nothing else. No entry, no event, no wait —
                        // today's behaviour for every command that exists.
                        match! thunk None with
                        | Error reason -> return Error reason
                        | Ok text -> return Ok (outcome call None (CommandRan text))
                    else
                        let handle = mintQueueId ()
                        // Visible to every peer the instant this lands, before any waiting,
                        // for the one door's reason: what the agent is about to do is
                        // something people can read and refuse.
                        SyncedStateSync.enqueueCommandCall
                            doc
                            handle
                            call.Tool
                            call.Summary
                            call.Author
                            (PendingAct.nextOrder subject synced.Pending)
                        let slot = ref None
                        parked.[QueueId.value handle] <- slot
                        Async.StartImmediate (settle call handle thunk slot)
                        let! settled = awaitSettled handle slot (now ())
                        return
                            Ok
                                (match settled.Status with
                                 // The yield carries the call's own identity: `awaitSettled`
                                 // knows the handle and the deadline, not what was asked.
                                 | CommandAwaitingApproval -> outcome call (Some handle) CommandAwaitingApproval
                                 | status -> outcome call (Some handle) status)
            }

        let read (handle: QueueId) =
            async {
                match parked.TryGetValue (QueueId.value handle) with
                | false, _ -> return Error "no such pending command"
                | true, slot ->
                    let! settled = awaitSettled handle slot (now ())
                    return Ok settled
            }

        { Run = run
          Read = read
          Knows = fun handle -> parked.ContainsKey (QueueId.value handle) }

    /// Refuse every command act still parked in a doc, attributed to the session itself.
    /// A boot repair, run where the other doc repairs run.
    ///
    /// The alternative is worse than it looks: the continuation that would have carried the
    /// act out died with the previous process, so the card would sit on every screen with an
    /// approve button that can never do anything. Recording a refusal says what actually
    /// happened, and the agent can simply ask again.
    let sweepAtBoot
        (doc: Yjs.Y.Doc)
        (appendAs: ActorRef -> SessionEvent -> Async<unit>)
        (mintMessageId: unit -> MessageId)
        : Async<int> =
        async {
            match SyncedStateSync.ofDoc doc with
            | Error _ -> return 0
            | Ok synced ->
                let stranded =
                    synced.Pending
                    |> Map.toList
                    |> List.choose (fun (handle, act) ->
                        match act.Payload with
                        | CommandCall (tool, summary) -> Some (handle, act, tool, summary)
                        | CommandLine -> None)
                for handle, act, tool, summary in stranded do
                    do!
                        appendAs
                            ActorRef.System
                            (SessionEvent.CommandRefused
                                { MessageId = mintMessageId ()
                                  QueueId = handle
                                  Tool = tool
                                  Summary = summary
                                  Author = act.Author
                                  RejectedBy = ActorRef.System
                                  Reason = Some "the session restarted before anyone decided" })
                if not (List.isEmpty stranded) then
                    SyncedStateSync.removePending doc (stranded |> List.map (fun (handle, _, _, _) -> handle))
                return List.length stranded
        }
