namespace Yession.SessionProcess

open Yession.Domain
open Yession.Domain.Terminals
open Yession.Domain.Collab
open Yession.Domain.Chat

/// The queue-drain scheduler (Phase 3): the Session Process is the single consumer of
/// the shared message queue, and the drain here is the linearization point of
/// "accepted by the agent". Extracted from the Host composition root so the property
/// harness (Step 18) drives the exact machinery production runs — no HTTP or WebRTC in
/// the loop.
///
/// Single-consumer is structural rather than cooperative: the Manager launches exactly one
/// process per session (`ProcessManager.launch` refuses an already-running one), so there is
/// no second drain to serialize against and none of what follows — exactly-once, snapshot
/// fidelity, total order — is defended by anything in this file. A topology that ran two
/// processes over one session's stores would break all three without a test going red.
module Scheduler =

    /// The scheduler's record of the running agent turn: identity for interrupt
    /// validation, a generation for slot-release checks, and the abort plumbing.
    type private RunningTurn =
        { Generation : int
          TurnId : AgentTurnId
          mutable Aborted : bool
          mutable AbortCallbacks : (unit -> unit) list }

    type SessionScheduler =
        { /// Re-examine the queue now. Call on every doc update observed while idle
          /// (the while-idle trigger: no lost wakeups) — re-entrant calls are cut by
          /// the single-flight guard.
          Drain : unit -> unit
          /// The interrupt authority for the command handler: valid only for the turn
          /// currently running (the interrupt-vs-completion race is decided here).
          RequestInterrupt : PeerId -> AgentTurnId -> Result<unit, string>
          /// The turn currently running, if any.
          RunningTurn : unit -> AgentTurnId option
          /// Re-examine whether the LOG owes the agent a turn nobody asked for (Plan 20,
          /// stage 2) — a background command finished while the agent was not running.
          ///
          /// Called where the answer can change and nowhere else: when a terminal block
          /// completes, once at boot (a completion the previous process never got to act on
          /// is still owed), and at the end of every turn — a completion that lands while the
          /// agent is busy fires this against a taken slot, and would otherwise be a debt
          /// nothing ever collects. Deliberately not on every doc update, which is what a
          /// drain is: the answer lives in the log, a doc update cannot move it, and reading
          /// the whole log on every keystroke somebody types into a draft would be a poll
          /// with a nicer name.
          Wake : unit -> unit }

    /// Create the scheduler for one session. `initialConsumed` seeds the log-anchored
    /// dedup set (every QueueId already named by a `MessageSent` in the durable log —
    /// the restart case); the scheduler maintains it on its own appends thereafter.
    /// `actorFor` resolves a connection to its attribution: the
    /// Manager-verified user behind the peer when one exists, the peer itself otherwise
    /// — drafts and queue entries stay keyed by `PeerId` (connection facts); attribution
    /// is applied here, at the durable-append boundary.
    /// `runAgent` is a THUNK, read at each drain (Plan 08): agent availability is
    /// dynamic — a credential connected mid-session enables turns without a relaunch,
    /// and `None` at drain time means messages append as `MessageSent` with no turn.
    let create
        (sessionId: SessionId)
        (doc: Yjs.Y.Doc)
        (log: EventLog<SessionEvent>)
        (runAgent: unit -> RunAgent option)
        (capabilitiesFor: AgentTurnId -> ActorRef -> AgentCapabilities)
        // Telemetry sink (Plan 04): passed straight to `AgentTurn.run`. Default `ignore`.
        (emitUsage: AgentTurnId -> AgentUsage -> unit)
        (mintTurnId: unit -> AgentTurnId)
        (mintMessageId: unit -> MessageId)
        (actorFor: PeerId -> ActorRef)
        // How a block's output is read back for the agent's terminal digest (Plan 13,
        // stage 3a). Injected rather than reached for, so a session with no transcript
        // storage still runs turns — it simply reports blocks with empty output.
        (readTranscript: ReadTranscript)
        (initialConsumed: Set<string>)
        : SessionScheduler =

        // Which model the next turn runs on, read from the doc each time a turn starts —
        // the same register the picker writes, so changing it takes effect on the next turn
        // with nothing to restart. A doc that will not decode is not a reason to refuse a
        // turn: it reads as no choice, which is the provider's default.
        let selectedModel () : ModelId option =
            match SyncedStateSync.ofDoc doc with
            | Ok synced -> synced.Model
            | Error _ -> None

        let mutable consumed = initialConsumed
        let mutable generation = 0
        let mutable running : RunningTurn option = None
        let mutable drainBusy = false

        let signalFor (turn: RunningTurn) : AgentAbortSignal =
            { IsAborted = fun () -> turn.Aborted
              OnAbort =
                fun callback ->
                    if turn.Aborted then callback ()
                    else turn.AbortCallbacks <- callback :: turn.AbortCallbacks }

        // While a turn runs, sends accumulate in the queue (Cursor's default); when
        // idle, the queue drains: durable append first, doc removal second, then ONE
        // coalesced turn for the whole batch. The snapshot/append/removal block never
        // yields to IO (synchronous log append), so the drain is atomic on the process
        // tick. `running` is the turn slot; an interrupt clears it early and starts
        // the next turn immediately, so the interrupted turn's own return must NOT
        // release its successor's slot — the generation check decides. `drainBusy`
        // guards the pre-turn synchronous section.
        let rec drain () =
            if drainBusy || Option.isSome running then () else
            match SyncedStateSync.ofDoc doc with
            | Error _ -> ()
            | Ok synced when Map.isEmpty synced.Queue -> ()
            | Ok synced ->
                let plan = QueueDrain.plan consumed synced.Queue
                if List.isEmpty plan.Batch then
                    // Everything present is already in the log (a crash between
                    // append and removal): repair the doc, consume nothing twice.
                    SyncedStateSync.removeQueued doc plan.Removals
                else
                    drainBusy <- true
                    Async.StartImmediate (
                        async {
                            // 1. Durable: each consumed entry becomes an immutable
                            //    MessageSent (body snapshotted from this replica,
                            //    QueueId as the exactly-once anchor) BEFORE the doc
                            //    removal — a crash here leaves only a repairable
                            //    leftover, never a lost or doubled message.
                            let mutable lastMessage : MessageSent option = None
                            for entry in plan.Batch do
                                let message =
                                    { MessageId = mintMessageId ()
                                      QueueId = Some entry.QueueId
                                      Author = actorFor entry.Author
                                      Body = SyncedStateSync.queuedBodyMarkdown doc entry.QueueId }
                                let! _ = log.Append (actorFor entry.Author) (MessageSent message)
                                consumed <- Set.add (QueueId.value entry.QueueId) consumed
                                lastMessage <- Some message
                            // 2. Visible: one transaction under the process origin;
                            //    the removal relays to every peer like any update.
                            SyncedStateSync.removeQueued doc plan.Removals
                            // 3. Run one coalesced turn, triggered by the batch tail.
                            match runAgent (), lastMessage with
                            | Some agent, Some trigger ->
                                let trigger = AgentTurn.FromMessage trigger
                                generation <- generation + 1
                                let turn =
                                    { Generation = generation
                                      TurnId = mintTurnId ()
                                      Aborted = false
                                      AbortCallbacks = [] }
                                running <- Some turn
                                drainBusy <- false
                                let! page = log.Read None System.Int32.MaxValue
                                let projection, _ =
                                    ConversationProjection.applyEvents None page.Events ConversationProjection.empty
                                // The terminal digest comes off the SAME page, so the
                                // conversation and the terminals describe one instant.
                                // This page was read before `AgentTurn.run` appends this
                                // turn's `AgentTurnStarted`, which is what makes the
                                // window "since the previous turn" without a cursor.
                                let events = page.Events |> List.map (fun envelope -> envelope.Event)
                                let terminals =
                                    events
                                    |> List.fold TerminalProjection.applyEvent TerminalProjection.empty
                                    |> TerminalDigest.build
                                        (fun id fromSeq toSeq -> readTranscript id fromSeq toSeq |> Transcript.printed)
                                        (TerminalDigest.window events)
                                do! AgentTurn.run log agent (signalFor turn) capabilitiesFor emitUsage (fun () -> turn.TurnId) mintMessageId sessionId projection.Items terminals (selectedModel ()) trigger
                                // Release the slot and re-arm — unless an interrupt
                                // already released it (and possibly started a successor).
                                match running with
                                | Some current when current.Generation = turn.Generation ->
                                    running <- None
                                    drain ()
                                    // A background block may have finished WHILE this turn
                                    // ran, and the wake it fired found the slot taken. The
                                    // debt is in the log, so re-reading it here is what keeps
                                    // a completion from being lost to bad timing — and it
                                    // costs nothing when none is owed. After `drain`, because
                                    // a person who spoke meanwhile outranks it (and, having
                                    // taken the slot, silences this).
                                    wake ()
                                | _ -> ()
                            | _ ->
                                drainBusy <- false
                                // Re-arm: anything enqueued during the appends drains now.
                                drain ()
                        })

        /// Run the turn the log owes, if it owes one. Same shape as the message path's turn
        /// — one page, read BEFORE this turn's `AgentTurnStarted`, so the digest window is
        /// "since the previous turn" without a cursor — and the same slot, so a wake can
        /// never run beside a turn somebody asked for.
        and wake () =
            if drainBusy || Option.isSome running then () else
            match runAgent () with
            | None -> ()
            | Some agent ->
                drainBusy <- true
                Async.StartImmediate (
                    async {
                        let! page = log.Read None System.Int32.MaxValue
                        let events = page.Events |> List.map (fun envelope -> envelope.Event)
                        match AgentWake.pendingReason events with
                        | None -> drainBusy <- false
                        | Some (reason, turnActor) ->
                            generation <- generation + 1
                            let turn =
                                { Generation = generation
                                  TurnId = mintTurnId ()
                                  Aborted = false
                                  AbortCallbacks = [] }
                            running <- Some turn
                            drainBusy <- false
                            let projection, _ =
                                ConversationProjection.applyEvents None page.Events ConversationProjection.empty
                            let terminals =
                                events
                                |> List.fold TerminalProjection.applyEvent TerminalProjection.empty
                                |> TerminalDigest.build
                                    (fun id fromSeq toSeq -> readTranscript id fromSeq toSeq |> Transcript.printed)
                                    (TerminalDigest.window events)
                            do!
                                AgentTurn.run
                                    log
                                    agent
                                    (signalFor turn)
                                    capabilitiesFor
                                    emitUsage
                                    (fun () -> turn.TurnId)
                                    mintMessageId
                                    sessionId
                                    projection.Items
                                    terminals
                                    (selectedModel ())
                                    (AgentTurn.FromWake (reason, turnActor))
                            match running with
                            | Some current when current.Generation = turn.Generation ->
                                running <- None
                                // A person may have said something while the woken turn ran,
                                // and their turn is the one that outranks everything.
                                drain ()
                                // A second background command may have finished while this
                                // woken turn ran. Same re-read as the message path's, and it
                                // terminates: `AgentWake.pending` resets at every
                                // `AgentTurnStarted`, so a wake that consumed the debt leaves
                                // nothing for the next one to find.
                                wake ()
                            | _ -> ()
                    })

        // Terminal event first (durable), then the abort signal, then an immediate
        // drain — queued messages start their turn now. The slot is released BEFORE
        // the callbacks fire: an abort that resumes the turn synchronously must find
        // its generation already retired, or its own completion path would release
        // (and re-drain) a second time.
        let requestInterrupt (peerId: PeerId) (turnId: AgentTurnId) : Result<unit, string> =
            match running with
            | Some turn when turn.TurnId = turnId ->
                turn.Aborted <- true
                let callbacks = turn.AbortCallbacks
                turn.AbortCallbacks <- []
                running <- None
                Async.StartImmediate (
                    async {
                        let! _ =
                            log.Append
                                (actorFor peerId)
                                (AgentTurnInterrupted { AgentTurnId = turnId; RequestedBy = peerId })
                        return ()
                    })
                callbacks |> List.iter (fun callback -> callback ())
                drain ()
                Ok ()
            | _ -> Error "turn already finished"

        { Drain = drain
          RequestInterrupt = requestInterrupt
          RunningTurn = fun () -> running |> Option.map (fun t -> t.TurnId)
          Wake = wake }
