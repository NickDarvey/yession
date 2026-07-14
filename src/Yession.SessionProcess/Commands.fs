namespace Yession.SessionProcess

open Yession.Domain

/// Handling of `SessionCommand` requests on the Session Process. Commands are how
/// clients ask for durable facts the CRDT cannot express; anything expressible as
/// collaborative state is NOT a command. As of Phase 3 both draft creation and sending
/// are pure CRDT writes, so every remaining command decodes to a rejection — the module
/// stays because the protocol surface (and Step 17's interrupt) lives here.
module SessionCommands =

    /// Handle one command from an accepted peer.
    let handle
        (_peerId: PeerId)
        (command: SessionCommand)
        : Async<SessionCommandResult> =
        async {
            match command with
            | StartDraft ->
                // Drafts are created in the shared synced state (Step 05), not by command.
                return CommandRejected "drafts are started in the shared session state"
            | SendDraft _ ->
                // Retired in Phase 3: sending enqueues via the shared session state; the
                // Session Process consumes the queue (docs/plans/01-turn-scheduling.md).
                return CommandRejected "superseded: sending enqueues via the shared session state"
        }

/// The queue drain's pure decision core (Phase 3, Step 16). The Session Process is the
/// queue's single consumer; `plan` computes, from a snapshot of its replica plus the
/// log-derived set of already-consumed entries, exactly what one drain does: which
/// entries become `MessageSent` events (in which order) and which doc keys to remove.
module QueueDrain =

    type DrainPlan =
        { /// Entries to consume, in `(Order, QueueId)` order — each becomes one
          /// `MessageSent` with the body snapshotted from this plan.
          Batch : QueuedMessage list
          /// Every snapshot key leaves the doc: the batch, plus entries already named
          /// by a `MessageSent` (a crash between append and removal left them behind —
          /// repaired here rather than consumed twice).
          Removals : QueueId list }

    let plan (consumed: Set<string>) (queue: Map<QueueId, QueuedMessage>) : DrainPlan =
        let snapshot = QueueOrder.sorted queue
        { Batch = snapshot |> List.filter (fun m -> not (Set.contains (QueueId.value m.QueueId) consumed))
          Removals = snapshot |> List.map (fun m -> m.QueueId) }

    /// The consumed-set contribution of one event: drains dedup against every
    /// `MessageSent` that names a queue entry.
    let consumedOf (event: SessionEvent) : string option =
        match event with
        | MessageSent m -> m.QueueId |> Option.map QueueId.value
        | _ -> None
