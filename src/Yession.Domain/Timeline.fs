namespace Yession.Domain.Chat

open System

open Yession.Domain

/// The chat as a PERSON reads it (Plan 14, stage 1): what was said and what was run, in the
/// order it happened.
///
/// This is a view-level merge, not a fold that competes with the two it merges.
/// `ConversationProjection` stays exactly what it was — it is the agent's context, and the
/// agent already gets block outcomes through `TerminalDigest` — and `TerminalProjection`
/// stays the terminals' own structure. Both are folds of the SAME ordered event log, which
/// is what makes combining them a sort by `EventOffset` rather than a reconciliation of two
/// clocks.
///
/// **The consequence, stated rather than discovered later:** the human's chat and the
/// agent's chat diverge. A person scrolling back sees twelve commands the agent's context
/// does not contain in that form. That is the trade — the digest gives the agent bounded
/// outcomes, the chat gives a person the shape of what happened — but "what is in the
/// conversation" now has two answers depending on who is asking.
///
/// **The unit is the block or the lease stretch, never the terminal.** Blocks exist only in
/// block mode and lease stretches only in live mode, so the two TILE a terminal's timeline:
/// no overlap, no gap, and therefore no "was this a long session?" threshold to tune. A
/// terminal that starts as a shell, becomes a `vim` session and returns to a prompt
/// contributes chips, then one stretch item, then more chips, and each of them is true.

/// One stretch of live mode: from the moment an actor took a terminal's stdin to the moment
/// that lease ended.
///
/// Assembled here rather than projected into `TerminalProjection` because it is not a fact
/// about a terminal's current state — a terminal has ONE lease and it is either held or not.
/// A stretch is a fact about the past, and it is the past that the chat shows.
type TerminalStretch =
    { /// The offset of the event that ENDED the stretch — where the item sits in the chat,
      /// and its identity. Leases are not minted with ids and one terminal has many
      /// stretches, so the anchoring offset is the only handle that is unique by
      /// construction and identical on every replica.
      Offset : EventOffset
      TerminalId : TerminalId
      /// The terminal's title as it stood, so the item can name the place rather than an id.
      Title : string
      /// Who held it.
      Holder : ActorRef
      /// How it ended. The four read differently on purpose — a release is a person
      /// finishing, a steal is another person taking over, a drop is nobody deciding
      /// anything, and an idle reclaim is a person who is still here and has stopped.
      End : TerminalLeaseEnd
      /// The transcript range the stretch produced, when there is one. `None` for a stretch
      /// whose events predate the range being recorded — an empty range and a range that
      /// happens to be empty are the same thing to a reader, and both mean "nothing to
      /// replay" rather than "replay from the top".
      Range : (int * int) option
      /// When the lease was taken and when it ended, from the log's envelope timestamps.
      /// The item says "for how long", and only the envelopes can answer that: the
      /// transcript's clock is per-terminal, relative to when that terminal opened.
      StartedAt : DateTimeOffset
      EndedAt : DateTimeOffset }

module TerminalStretch =

    /// How long the holder had the terminal.
    let duration (stretch: TerminalStretch) : TimeSpan = stretch.EndedAt - stretch.StartedAt

    /// A stable handle for one stretch, for a tab to be keyed by and a test to assert.
    ///
    /// Derived rather than minted, which is fine while it is only a tab key and a test's
    /// handle: both are recomputed from the same projection every time. It stops being fine
    /// the moment a stretch is addressable from outside — a link handed to somebody else turns
    /// "a stretch anchors at the offset it concluded at" into a wire contract nobody wrote
    /// down, and any reconsideration of the anchoring rule silently breaks every link already
    /// sent. The fix is to mint a `StretchId` on `TerminalLeaseReleased` and its
    /// idle/stolen/holder-gone siblings, the way `BlockId` and `ToolUseId` are minted; do that
    /// before deep links, not after.
    let key (stretch: TerminalStretch) : string =
        sprintf "%s@%d" (TerminalId.value stretch.TerminalId) (EventOffset.value stretch.Offset)

/// One thing in the chat, in the order it happened.
///
/// A block is carried by ID rather than by value, and that is the whole reason a chip
/// "mutates in place" for free: the timeline holds WHERE it goes, `TerminalProjection` holds
/// what it currently says, and a block that starts running and later exits 1 moves the
/// second without touching the first.
type TimelineItem =
    /// Something someone said.
    | TimelineMessage of ConversationItem
    /// A command someone ran, or one someone refused, at the offset it STARTED. Carried by
    /// id and resolved against `TerminalProjection`, which is why a chip mutates in place for
    /// free: the timeline holds where it goes, the projection holds what it currently says.
    | TimelineBlock of EventOffset * TerminalId * BlockId
    /// A stretch of live mode, at the offset it concluded.
    | TimelineStretch of TerminalStretch
    /// A tool the agent called, at the offset it STARTED (Plan 16, part C). It takes the
    /// block's anchoring rule rather than the stretch's, for the block's reason: a call that
    /// takes four minutes must be visible while it is the only thing happening.
    ///
    /// Carried by id, like a block, and resolved against `ToolUses` below — so the outcome
    /// arriving later moves what the chip says without moving where it sits.
    | TimelineToolUse of EventOffset * ToolUseId

module TimelineItem =

    /// Where this sits in the log — the single key everything here is ordered by.
    let offset =
        function
        | TimelineMessage item -> item.Offset
        | TimelineBlock (offset, _, _) -> offset
        | TimelineStretch stretch -> stretch.Offset
        | TimelineToolUse (offset, _) -> offset

/// What a task card says about one of its commands — coarser than `TerminalBlockStatus`, and
/// coarser on purpose. A card is read at a glance to answer three questions: is anything
/// wrong, is anything still going, is it finished. The exact exit code is on the line itself
/// and in the block behind it; a card that counted `exit 2` separately from `timed out` would
/// be a longer summary saying less.
///
/// A refusal counts as a failure. It is red on every other surface for the same reason: a
/// command the agent proposed and did not get to run is the thing a person scanning for
/// trouble is scanning for.
type TaskState =
    | TaskFailed
    | TaskRunning
    | TaskDone

/// A burst counted by state — the card's summary line, as a value.
type TaskTally =
    { Commands : int
      Failed : int
      Running : int
      Done : int }

module TaskCard =

    /// How one block reads on a card.
    let stateOf =
        function
        | BlockRunning -> TaskRunning
        | BlockFinished (CommandSucceeded _) -> TaskDone
        | BlockFinished _ -> TaskFailed
        | BlockRejected _ -> TaskFailed

    /// What the summary line counts. Blocks whose status this client cannot resolve yet are
    /// simply not passed in: a card at a page boundary says less rather than guessing.
    let tally (states: TaskState list) : TaskTally =
        let count state = states |> List.filter ((=) state) |> List.length
        { Commands = List.length states
          Failed = count TaskFailed
          Running = count TaskRunning
          Done = count TaskDone }

    /// The order a card's lines are read in: what went wrong, then what is still going, then
    /// what is done. Red is what a person scans for, and a burst of twenty commands with one
    /// failure buried at line fourteen makes them scan for it.
    ///
    /// Chronological WITHIN each group, so the only thing a finishing command changes is
    /// which group it is in — the line itself never jumps a place inside one.
    let ordered (lines: ('line * TaskState) list) : ('line * TaskState) list =
        let inState state = lines |> List.filter (fun (_, s) -> s = state)
        inState TaskFailed @ inState TaskRunning @ inState TaskDone

/// One tool call, as the chat currently knows it. `Outcome = None` is a call still running:
/// it already holds its place, and says so, rather than appearing out of nowhere when it
/// finishes.
type ToolUse =
    { ToolUseId : ToolUseId
      /// The turn that made the call — what lets a chatty turn group into one line.
      AgentTurnId : AgentTurnId
      Namespace : string
      Name : string
      /// As recorded: secrets already gone, `None` for a tool whose schema we did not write.
      Arguments : string option
      Outcome : ToolOutcome option
      Block : BlockId option }

module ToolUse =

    /// Where a reader is sent, and what a link carries: `<namespace>/<name>` names the tool
    /// and the minted id names the call.
    let label (use': ToolUse) : string = sprintf "%s/%s" use'.Namespace use'.Name

/// One DRAWN row of the chat. A row is usually one item; a run of consecutive tool calls
/// from one turn is one row holding several, so the chat costs a line per turn rather than
/// a line per call. A burst of commands from one turn groups the same way, into a task card.
type TimelineRow =
    | RowItem of TimelineItem
    | RowToolRun of AgentTurnId * TimelineItem list
    /// One agent burst: consecutive blocks the same turn started, across whichever of the
    /// agent's terminals they ran in (Plan 20, stage 4). Never one block — a card around a
    /// single chip is a disclosure over nothing — and never a human's, because grouping is
    /// for work nobody is hand-driving.
    | RowTaskCard of AgentTurnId * TimelineItem list

module TimelineRow =

    let offset =
        function
        | RowItem item -> TimelineItem.offset item
        | RowToolRun (_, items)
        | RowTaskCard (_, items) ->
            items |> List.tryHead |> Option.map TimelineItem.offset |> Option.defaultValue EventOffset.zero

/// The terminal side of the timeline, folded from the log. Conversation items are not
/// folded again here — they are merged in by `items`, from the projection that already has
/// them.
type TimelineProjection =
    { /// Terminal-derived items in the order they were anchored, newest last.
      TerminalItems : TimelineItem list
      /// Leases currently open, by terminal — projection-internal bookkeeping, so a release
      /// can be paired with the take it ends. A take with no release yet is NOT an item: a
      /// stretch appears when it concluded, which is the whole difference between it and a
      /// chip.
      OpenLeases : Map<string, ActorRef * int * DateTimeOffset>
      /// Each terminal's title as `TerminalOpened` gave it, so a stretch can name the place
      /// without the merge having to carry a second projection in.
      Titles : Map<string, string>
      /// What each tool call currently says, by its minted id. Held HERE rather than in a
      /// projection of its own because this is already the chat's fold and the two would
      /// have to be applied in lockstep anyway — `OpenLeases` is the same kind of
      /// bookkeeping.
      ToolUses : Map<string, ToolUse>
      /// The turn the fold is inside, from the last `AgentTurnStarted` it saw.
      /// Projection-internal bookkeeping, exactly like `OpenLeases`: it exists so a block
      /// can be attributed to the turn that started it.
      CurrentTurn : AgentTurnId option
      /// Which turn started each of the AGENT's blocks — the grouping key a task card is
      /// built on (Plan 20, stage 4).
      ///
      /// Recorded at the block's START rather than joined from `ToolUseFinished`'s `Block`,
      /// which names the same pair. The tool call finishes when the COMMAND does, so that
      /// join arrives too late to group a running block — and a card whose lines appear only
      /// once they are done is a card that is empty for exactly as long as it matters.
      BlockTurns : Map<string, AgentTurnId> }

module TimelineProjection =

    let empty : TimelineProjection =
        { TerminalItems = []
          OpenLeases = Map.empty
          Titles = Map.empty
          ToolUses = Map.empty
          CurrentTurn = None
          BlockTurns = Map.empty }

    /// The turn that started this block, when the agent started it. `None` for a human's
    /// command, for one the Session Process ran on its own behalf, and for any block whose
    /// start this fold has not seen — all three mean the same thing to a card: not mine.
    let blockTurn (id: BlockId) (proj: TimelineProjection) : AgentTurnId option =
        Map.tryFind (BlockId.value id) proj.BlockTurns

    /// What one tool-use item currently says.
    let toolUse (id: ToolUseId) (proj: TimelineProjection) : ToolUse option =
        Map.tryFind (ToolUseId.value id) proj.ToolUses

    /// Does this call draw a chip of its own?
    ///
    /// Not when it became a block: the block chip already says who ran what and how it went,
    /// and a second item beside it would be two renderings of one fact, free to disagree.
    /// The RECORD still exists — the audit wants every call — it simply does not draw twice.
    let drawsChip (id: ToolUseId) (proj: TimelineProjection) : bool =
        match toolUse id proj with
        | Some use' -> Option.isNone use'.Block
        | None -> false

    /// A range is `None` unless it holds at least one line. `[from, to)` with `to <= from`
    /// is what a rejected command carries and what a pre-Plan-14 lease event decodes to;
    /// both mean the same thing to a reader, so they get the same answer.
    let private rangeOf (fromSeq: int) (toSeq: int) : (int * int) option =
        if toSeq > fromSeq then Some (fromSeq, toSeq) else None

    let private applyEvent (proj: TimelineProjection) (envelope: EventEnvelope<SessionEvent>) : TimelineProjection =
        let append item = { proj with TerminalItems = proj.TerminalItems @ [ item ] }
        // Attribute a block to the turn running when it started, and only when the AGENT
        // wrote it. A person's command typed while a turn happens to be open is theirs — the
        // clock says "during", and only the authority says "whose".
        let attributed (author: ActorRef) (id: BlockId) (proj: TimelineProjection) =
            match author, proj.CurrentTurn with
            | ActorRef.Agent, Some turn -> { proj with BlockTurns = Map.add (BlockId.value id) turn proj.BlockTurns }
            | _ -> proj
        match envelope.Event with
        | SessionEvent.TerminalOpened e ->
            { proj with Titles = Map.add (TerminalId.value e.TerminalId) e.Title proj.Titles }
        | SessionEvent.AgentTurnStarted e -> { proj with CurrentTurn = Some e.AgentTurnId }
        | SessionEvent.TerminalBlockStarted e ->
            // Anchored at the START, and it mutates in place as it finishes — so a
            // four-minute build's result lands above messages sent while it ran. The
            // alternative, appearing only on completion, makes long work invisible while it
            // is the only thing happening.
            append (TimelineBlock (envelope.Offset, e.TerminalId, e.BlockId))
            |> attributed (Authority.author e.Authority) e.BlockId
        | SessionEvent.TerminalCommandRejected e ->
            // A refusal gets a chip too. "The agent proposed this and a human said no" is
            // the more interesting half of the two, and a rejection that appears nowhere is
            // indistinguishable from a bug.
            append (TimelineBlock (envelope.Offset, e.TerminalId, e.BlockId))
            |> attributed e.Author e.BlockId
        | SessionEvent.TerminalLeaseTaken e ->
            { proj with
                OpenLeases =
                    Map.add (TerminalId.value e.TerminalId) (e.By, e.FromSeq, envelope.Timestamp) proj.OpenLeases }
        | SessionEvent.TerminalLeaseReleased e ->
            let key = TerminalId.value e.TerminalId
            match Map.tryFind key proj.OpenLeases with
            // Only the holder this release names. A steal is two events — the old lease
            // ending and the new one starting — and this is the same guard the terminal fold
            // applies, for the same reason: a release naming someone who no longer holds it
            // is stale, and acting on it would close the stretch the take beside it opened.
            | Some (holder, fromSeq, startedAt) when holder = e.Was ->
                let stretch =
                    { Offset = envelope.Offset
                      TerminalId = e.TerminalId
                      Title = Map.tryFind key proj.Titles |> Option.defaultValue (TerminalId.value e.TerminalId)
                      Holder = holder
                      End = e.Reason
                      Range = rangeOf fromSeq e.ToSeq
                      StartedAt = startedAt
                      EndedAt = envelope.Timestamp }
                { proj with
                    TerminalItems = proj.TerminalItems @ [ TimelineStretch stretch ]
                    OpenLeases = Map.remove key proj.OpenLeases }
            | _ -> proj
        | SessionEvent.ToolUseStarted e ->
            let use' =
                { ToolUseId = e.ToolUseId
                  AgentTurnId = e.AgentTurnId
                  Namespace = e.Namespace
                  Name = e.Name
                  Arguments = e.Arguments
                  Outcome = None
                  Block = None }
            { proj with
                TerminalItems = proj.TerminalItems @ [ TimelineToolUse (envelope.Offset, e.ToolUseId) ]
                ToolUses = Map.add (ToolUseId.value e.ToolUseId) use' proj.ToolUses }
        | SessionEvent.ToolUseFinished e ->
            // No new item: the call already has its place. Only what it SAYS changes.
            match Map.tryFind (ToolUseId.value e.ToolUseId) proj.ToolUses with
            | Some use' ->
                { proj with
                    ToolUses =
                        Map.add
                            (ToolUseId.value e.ToolUseId)
                            { use' with Outcome = Some e.Outcome; Block = e.Block }
                            proj.ToolUses }
            | None -> proj
        | SessionEvent.TerminalClosed e ->
            // The lease goes with the terminal, and WITHOUT a release event — that is the
            // Process's rule, so the stretch has to be closed here or it would never appear.
            // Read as `LeaseHolderGone`: nobody decided anything, the terminal went away.
            let key = TerminalId.value e.TerminalId
            match Map.tryFind key proj.OpenLeases with
            | Some (holder, fromSeq, startedAt) ->
                let stretch =
                    { Offset = envelope.Offset
                      TerminalId = e.TerminalId
                      Title = Map.tryFind key proj.Titles |> Option.defaultValue (TerminalId.value e.TerminalId)
                      Holder = holder
                      End = LeaseHolderGone
                      // A terminal that closed under a live holder has no recorded end, and
                      // inventing one from the transcript's current length would be a guess
                      // this fold cannot check. Nothing to replay is the honest answer.
                      Range = None
                      StartedAt = startedAt
                      EndedAt = envelope.Timestamp }
                { proj with
                    TerminalItems = proj.TerminalItems @ [ TimelineStretch stretch ]
                    OpenLeases = Map.remove key proj.OpenLeases }
            | None -> proj
        | _ -> proj

    /// Fold ordered event envelopes in. Offset-gated exactly as the other projections are,
    /// so re-applying an overlapping page is idempotent.
    let applyEvents
        (appliedThrough: EventOffset option)
        (events: EventEnvelope<SessionEvent> list)
        (projection: TimelineProjection)
        : TimelineProjection * EventOffset option =
        events
        |> List.fold
            (fun (proj, highWater) envelope ->
                let beyondApplied =
                    match highWater with
                    | Some o -> EventOffset.value envelope.Offset > EventOffset.value o
                    | None -> true
                if beyondApplied then applyEvent proj envelope, Some envelope.Offset
                else proj, highWater)
            (projection, appliedThrough)

    /// The chat, in order: conversation items and terminal items merged by offset.
    ///
    /// A stable sort on the offset alone. Two items can never share one — the log assigns a
    /// distinct offset per event and every item here is anchored at exactly one event — so
    /// there is no tie to break and no second key to invent.
    let items (conversation: ConversationProjection) (proj: TimelineProjection) : TimelineItem list =
        let said = conversation.Items |> List.map TimelineMessage
        said @ proj.TerminalItems
        |> List.sortBy (TimelineItem.offset >> EventOffset.value)

    /// The chat as it is DRAWN, which is not quite the chat as it happened.
    ///
    /// Three rules apply here rather than in the fold, because all three are about rendering
    /// and the fold is about facts: a call that became a block is dropped (its block already
    /// draws), consecutive calls from ONE turn collapse into a run, and consecutive BLOCKS
    /// from one turn collapse into a task card. The events already carry the turn, and a
    /// chatty turn should cost one line rather than twenty — tool use is the first item a
    /// single turn can emit a dozen of, and an agent working across several terminals is the
    /// second.
    ///
    /// Both groupings take the same shape and stop at the same boundary: only CONSECUTIVE
    /// items group, so anything said in the middle splits the row. That is not a shared
    /// implementation detail, it is the rule — a card that swallowed the message between two
    /// commands would tell a reader the wrong story about the order.
    let rows (conversation: ConversationProjection) (proj: TimelineProjection) : TimelineRow list =
        let turnOf item =
            match item with
            | TimelineToolUse (_, id) -> toolUse id proj |> Option.map (fun u -> u.AgentTurnId)
            | _ -> None
        let burstOf item =
            match item with
            | TimelineBlock (_, _, id) -> blockTurn id proj
            | _ -> None
        items conversation proj
        |> List.filter (fun item ->
            match item with
            | TimelineToolUse (_, id) -> drawsChip id proj
            | _ -> true)
        |> List.fold
            (fun rows item ->
                match turnOf item, burstOf item, rows with
                | Some turn, _, RowToolRun (previous, earlier) :: rest when previous = turn ->
                    RowToolRun (turn, earlier @ [ item ]) :: rest
                | Some turn, _, _ -> RowToolRun (turn, [ item ]) :: rows
                | _, Some turn, RowTaskCard (previous, earlier) :: rest when previous = turn ->
                    RowTaskCard (turn, earlier @ [ item ]) :: rest
                // The card forms on the SECOND command, not the first: one command from a
                // turn is a chip, and wrapping it in a disclosure would hide the only thing
                // the row has to say behind a click.
                | _, Some turn, RowItem (TimelineBlock _ as first) :: rest when burstOf first = Some turn ->
                    RowTaskCard (turn, [ first; item ]) :: rest
                | _, _, _ -> RowItem item :: rows)
            []
        |> List.rev
