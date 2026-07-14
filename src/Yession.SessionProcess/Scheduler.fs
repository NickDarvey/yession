namespace Yession.SessionProcess

open Yession.Domain

/// The queue-drain scheduler (Phase 3): the Session Process is the single consumer of
/// the shared message queue, and the drain here is the linearization point of
/// "accepted by the agent". Extracted from the Host composition root so the property
/// harness (Step 18) drives the exact machinery production runs — no HTTP or WebRTC in
/// the loop. See docs/plans/01-turn-scheduling.md.
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
          RunningTurn : unit -> AgentTurnId option }

    /// Create the scheduler for one session. `initialConsumed` seeds the log-anchored
    /// dedup set (every QueueId already named by a `MessageSent` in the durable log —
    /// the restart case); the scheduler maintains it on its own appends thereafter.
    let create
        (sessionId: SessionId)
        (doc: Yjs.Y.Doc)
        (log: EventLog<SessionEvent>)
        (runAgent: RunAgent option)
        (capabilitiesFor: AgentTurnId -> AgentCapabilities)
        (mintTurnId: unit -> AgentTurnId)
        (mintMessageId: unit -> MessageId)
        (initialConsumed: Set<string>)
        : SessionScheduler =

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
                                      DraftId = None
                                      QueueId = Some entry.QueueId
                                      Author = HumanPeer entry.Author
                                      Body = Ylmish.Text.toString entry.Body }
                                let! _ = log.Append (HumanPeer entry.Author) (MessageSent message)
                                consumed <- Set.add (QueueId.value entry.QueueId) consumed
                                lastMessage <- Some message
                            // 2. Visible: one transaction under the process origin;
                            //    the removal relays to every peer like any update.
                            SyncedStateSync.removeQueued doc plan.Removals
                            // 3. Run one coalesced turn, triggered by the batch tail.
                            match runAgent, lastMessage with
                            | Some agent, Some trigger ->
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
                                do! AgentTurn.run log agent (signalFor turn) capabilitiesFor (fun () -> turn.TurnId) mintMessageId sessionId projection.Items trigger
                                // Release the slot and re-arm — unless an interrupt
                                // already released it (and possibly started a successor).
                                match running with
                                | Some current when current.Generation = turn.Generation ->
                                    running <- None
                                    drain ()
                                | _ -> ()
                            | _ ->
                                drainBusy <- false
                                // Re-arm: anything enqueued during the appends drains now.
                                drain ()
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
                                (HumanPeer peerId)
                                (AgentTurnInterrupted { AgentTurnId = turnId; RequestedBy = peerId })
                        return ()
                    })
                callbacks |> List.iter (fun callback -> callback ())
                drain ()
                Ok ()
            | _ -> Error "turn already finished"

        { Drain = drain
          RequestInterrupt = requestInterrupt
          RunningTurn = fun () -> running |> Option.map (fun t -> t.TurnId) }
