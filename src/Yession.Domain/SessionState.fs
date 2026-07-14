namespace Yession.Domain

/// Collaborative session state shapes shared by the Session Process and the Browser
/// Client. These are the model shapes only; the Yjs/Ylmish encoding that keeps them in
/// sync lives in Sync.fs. See docs/design.md §2.2 and docs/plans/01-turn-scheduling.md.

type DraftState =
    { DraftId : DraftId
      Author  : PeerId
      Body    : Ylmish.Text }

/// A message waiting for the agent (Phase 3). Queued messages are collaborative state:
/// any peer may edit the body (text merge), reorder (one fractional-index write), or
/// delete — until the Session Process drains the queue, which is the terminal
/// transition into an immutable `MessageSent` event.
type QueuedMessage =
    { QueueId : QueueId
      Author  : PeerId
      /// Concurrent edits interleave and merge, exactly like draft bodies.
      Body    : Ylmish.Text
      /// A fractional index: reorder = one register write, never a structural move
      /// (Yjs's concurrent-move duplication is unrepresentable this way).
      Order   : float }

type SharedBrief = { Body : string }

/// Collaborative state synced via Ylmish.
type SyncedSessionState =
    { Drafts      : Map<DraftId, DraftState>
      Queue       : Map<QueueId, QueuedMessage>
      SharedBrief : SharedBrief option }

module SyncedSessionState =

    /// Nothing synced yet: no drafts, an empty queue, no shared brief.
    let empty : SyncedSessionState = { Drafts = Map.empty; Queue = Map.empty; SharedBrief = None }

/// The queue's total order. `Order` is a float register; ties (possible when two peers
/// mint concurrently) are broken by `QueueId`, so the order is always a total,
/// deterministic function of the queue contents — on every replica.
module QueueOrder =

    /// Queue entries in consumption order: `(Order, QueueId)` ascending.
    let sorted (queue: Map<QueueId, QueuedMessage>) : QueuedMessage list =
        queue
        |> Map.toList
        |> List.map snd
        |> List.sortBy (fun m -> m.Order, QueueId.value m.QueueId)

    /// The order value for a new entry appended at the tail.
    let next (queue: Map<QueueId, QueuedMessage>) : float =
        match sorted queue with
        | [] -> 1.0
        | entries -> (List.last entries).Order + 1.0

    /// An order value strictly between two neighbours (`None` = open end).
    let between (before: float option) (after: float option) : float =
        match before, after with
        | Some a, Some b -> (a + b) / 2.0
        | None, Some b -> b - 1.0
        | Some a, None -> a + 1.0
        | None, None -> 1.0

    /// The order value that moves `id` one position earlier, or `None` if it is not in
    /// the queue or already first. One register write — the UI's "move up".
    let moveUp (queue: Map<QueueId, QueuedMessage>) (id: QueueId) : float option =
        let entries = sorted queue
        match entries |> List.tryFindIndex (fun m -> m.QueueId = id) with
        | Some i when i > 0 ->
            let above = entries.[i - 1].Order
            let aboveAbove = if i >= 2 then Some entries.[i - 2].Order else None
            Some (between aboveAbove (Some above))
        | _ -> None

    /// The order value that moves `id` one position later, or `None` if it is not in
    /// the queue or already last. One register write — the UI's "move down".
    let moveDown (queue: Map<QueueId, QueuedMessage>) (id: QueueId) : float option =
        let entries = sorted queue
        match entries |> List.tryFindIndex (fun m -> m.QueueId = id) with
        | Some i when i < entries.Length - 1 ->
            let below = entries.[i + 1].Order
            let belowBelow = if i + 2 < entries.Length then Some entries.[i + 2].Order else None
            Some (between (Some below) belowBelow)
        | _ -> None
