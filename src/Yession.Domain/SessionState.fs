namespace Yession.Domain

/// Collaborative session state shapes shared by the Session Process and the Browser
/// Client. These are the model shapes only; the Yjs/Ylmish encoding that keeps them in
/// sync lives in Sync.fs. See docs/design.md §2.2 and Plan 01.

/// A client's work-in-progress draft: the WIP tail, not yet queued. Keyed by its
/// `Author` in `SyncedSessionState.Drafts`, so each client owns at most one — the cap is
/// structural, not a runtime check. The body is a rich-text `Y.XmlFragment` (a ProseMirror
/// doc) held as a top-level doc root keyed by `BodyKey.draft` (RichText.fs), NOT in the model
/// and NOT in the synced-state tree — so this record carries only the slot's identity. Concurrent
/// edits merge in the fragment CRDT, so "collaborate" is co-editing someone's slot and "write
/// your own" is yours.
type DraftState =
    { Author  : PeerId
      /// The queue key this draft becomes when sent, minted by the author when the slot is
      /// published. ANY co-editor may send a draft, and because every sender writes this same
      /// key, two concurrent sends are one map entry that merges rather than two duplicate
      /// messages — the double-send race is unrepresentable instead of merely avoided by policy.
      /// A fresh draft gets a fresh key: the slot is created anew each time, so a sent key is
      /// never reused while its queue entry may still be waiting.
      QueueId : QueueId }

/// A message waiting for the agent (Phase 3). Queued messages are collaborative state:
/// any peer may edit the rich body (the `Y.XmlFragment` merges), reorder (one
/// fractional-index write), or delete — until the Session Process drains the queue, which
/// snapshots the body to Markdown in an immutable `MessageSent`.
type QueuedMessage =
    { QueueId : QueueId
      Author  : PeerId
      /// A fractional index: reorder = one register write, never a structural move
      /// (Yjs's concurrent-move duplication is unrepresentable this way).
      Order   : float }

/// A collaborative command line waiting to be sent to a terminal (Plan 13): the terminal
/// composer's draft, and the exact shape `DraftState` has, for the exact reason. One slot
/// per (terminal, author), so everyone sees everyone typing and any peer may co-edit a
/// slot — including the agent's, which is what makes reviewing its command the same act as
/// reading its message.
///
/// The command TEXT is not here. It is a top-level `Y.Text` root keyed by
/// `BodyKey.terminalDraft` (the same sibling-root arrangement rich bodies use, and for the
/// same reason), so this record carries only the slot's identity. Plain `Y.Text` rather
/// than a rich body because a command is characters, not prose: round-tripping `ls *.fs`
/// through Markdown would escape the glob.
type TerminalDraft =
    { Terminal : TerminalId
      Author   : PeerId
      /// The queue key this draft becomes when sent, minted by its author when the slot is
      /// published — so two peers sending it concurrently write ONE entry, exactly as in
      /// the message queue.
      QueueId  : QueueId }

/// A command line queued to run in a terminal, whose text is a `Y.Text` root keyed by
/// `BodyKey.terminalQueued`. Collaborative until the drain consumes it: any peer may
/// reorder or delete it, and edit its text in place — reading what is about to happen and
/// fixing it first is the point of the queue being visible. The one kind of act that
/// queues (Plan 23): a structured command classifies and dispatches synchronously, so
/// nothing else parks here any more.
type PendingAct =
    { QueueId  : QueueId
      /// The terminal this command runs in.
      Terminal : TerminalId
      /// Who proposed the act, and whose authority it would run on (Plan 20). Neither is
      /// changed by an edit, because "who asked for this" is not editable — and the pair is
      /// one value so that an agent-proposed act without an owner cannot be written.
      Authority : Authority
      /// A fractional index within its terminal's queue — one register write to reorder.
      Order    : float
      /// Whether the author asked for this to run WITHOUT holding their turn open (Plan 20,
      /// stage 2). Only an agent sets it — a person's composer never waits on anything —
      /// and it rides the queue entry because the drain is what reads the doc and mints the
      /// block that records it.
      Background : bool
      /// How wide the author's terminal was when they asked for this, if they had one.
      ///
      /// The size rides the ACT rather than sitting in a register beside the terminal, and
      /// that is the whole of the policy. A shared register has to answer "whose viewport
      /// wins", and every answer is wrong somewhere: smallest-wins is tmux's worst
      /// inheritance (see `TerminalSize`), and any other pick makes the width depend on who
      /// happened to be connected. A command has one author, and the output belongs to them.
      ///
      /// It matters more here than in live mode because block geometry is not ephemeral.
      /// Resize a live screen and the program redraws; a block that ran at 200 columns is
      /// 200-column text in the transcript for ever — for every later reader, for the agent's
      /// digest, for replay.
      ///
      /// `None` is an act with no viewport to speak for: the agent's commands, and a person
      /// whose terminals column is shut. It makes no claim, so the terminal keeps the width it
      /// had — which is the last human's, or the 80x24 it opened at. No constant to defend.
      Size : TerminalSize option }

/// The name of the top-level `Y.XmlFragment` root that holds a draft/queue body. Stable across
/// peers so every replica's `BodyRegistry` and editor bind to the same fragment (root types
/// merge by name, so there is no creation race).
module BodyKey =
    let draft (author: PeerId) : string = "draft:" + PeerId.value author
    let queued (id: QueueId) : string = "queue:" + QueueId.value id

    /// A terminal composer's `Y.Text` root. Keyed by both ids because the slot is per
    /// author PER TERMINAL — one person may be mid-command in two terminals at once.
    let terminalDraft (terminal: TerminalId) (author: PeerId) : string =
        "term-draft:" + TerminalId.value terminal + ":" + PeerId.value author

    /// A queued terminal command's `Y.Text` root. Keyed by the queue id alone: the entry
    /// already names its terminal, and the key must not change when the text is edited.
    let terminalQueued (id: QueueId) : string = "term-queue:" + QueueId.value id

type SharedBrief = { Body : string }

/// Collaborative state synced via Ylmish.
[<RequireQualifiedAccess>]
type SyncedSessionState =
    { /// Keyed by author, and that key is the invariant: one draft per client is
      /// unrepresentable-otherwise, and two peers drafting across a partition cannot collide
      /// because their keys differ. An ownerless "session draft", if one is ever wanted, is a
      /// second optional slot beside `SharedBrief` — never a widened key here, which would
      /// trade the structural cap for a runtime check and a reconciliation nobody has written.
      Drafts      : Map<PeerId, DraftState>
      Queue       : Map<QueueId, QueuedMessage>
      /// The session's human-given title: collaborative text, so concurrent edits
      /// interleave and merge exactly like a draft body. Empty until first named.
      Title       : Ylmish.Text
      SharedBrief : SharedBrief option
      /// Terminal composer slots, keyed by (terminal, author) — one per person per
      /// terminal, structurally, exactly as `Drafts` caps a person at one message draft.
      TerminalDrafts : Map<TerminalId * PeerId, TerminalDraft>
      /// Every act waiting on a verdict, across every subject. One flat map rather than a
      /// map per terminal or per command: an entry names its own subject, and a flat keyed
      /// map is what makes concurrent creation safe (different keys never conflict)
      /// regardless of which surface each peer was looking at.
      Pending : Map<QueueId, PendingAct>
      /// Which model the agent's turns run on. Collaborative because it is a property of
      /// the SESSION, not of whoever happened to open the picker, so everybody sees the
      /// same answer and sees it change.
      ///
      /// `None` — the absence of the register — is "the provider's own default". The
      /// default is the absence again, so a session nobody has configured carries nothing
      /// restating what the provider already decides.
      Model       : ModelId option }

module SyncedSessionState =

    /// Nothing synced yet: no drafts, an empty queue, an unnamed title, no shared brief,
    /// no terminal composers.
    let empty : SyncedSessionState =
        { Drafts = Map.empty
          Queue = Map.empty
          Title = Ylmish.Text.empty
          SharedBrief = None
          TerminalDrafts = Map.empty
          Pending = Map.empty
          Model = None }

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

/// The terminal queue's order, per terminal. The same total, deterministic
/// `(Order, QueueId)` rule the message queue uses — restated over the terminal entry
/// rather than abstracted over both, because the one thing these two queues must never
/// share is a code path that could reorder one when asked to reorder the other. The
/// fractional-index arithmetic itself IS shared (`QueueOrder.between`): that part is about
/// floats, not about queues.
module TerminalQueueOrder =

    /// One terminal's entries in consumption order.
    let sortedFor (terminal: TerminalId) (queue: Map<QueueId, PendingAct>) : PendingAct list =
        queue
        |> Map.toList
        |> List.map snd
        |> List.filter (fun e -> e.Terminal = terminal)
        |> List.sortBy (fun e -> e.Order, QueueId.value e.QueueId)

    /// The order value for a new entry appended at the tail of a terminal's queue. Unique
    /// and ascending within a terminal, which is what keeps the CARD LIST stable for
    /// everybody as well as ordering the drain.
    let nextFor (terminal: TerminalId) (queue: Map<QueueId, PendingAct>) : float =
        match sortedFor terminal queue with
        | [] -> 1.0
        | acts -> (acts |> List.map (fun act -> act.Order) |> List.max) + 1.0

    /// The order value that moves `id` one position earlier within its own terminal.
    let moveUp (queue: Map<QueueId, PendingAct>) (id: QueueId) : float option =
        match Map.tryFind id queue |> Option.map (fun act -> act.Terminal) with
        | None -> None
        | Some terminal ->
            let entries = sortedFor terminal queue
            match entries |> List.tryFindIndex (fun e -> e.QueueId = id) with
            | Some i when i > 0 ->
                let above = entries.[i - 1].Order
                let aboveAbove = if i >= 2 then Some entries.[i - 2].Order else None
                Some (QueueOrder.between aboveAbove (Some above))
            | _ -> None

    /// The order value that moves `id` one position later within its own terminal.
    let moveDown (queue: Map<QueueId, PendingAct>) (id: QueueId) : float option =
        match Map.tryFind id queue |> Option.map (fun act -> act.Terminal) with
        | None -> None
        | Some terminal ->
            let entries = sortedFor terminal queue
            match entries |> List.tryFindIndex (fun e -> e.QueueId = id) with
            | Some i when i < entries.Length - 1 ->
                let below = entries.[i + 1].Order
                let belowBelow = if i + 2 < entries.Length then Some entries.[i + 2].Order else None
                Some (QueueOrder.between (Some below) belowBelow)
            | _ -> None
