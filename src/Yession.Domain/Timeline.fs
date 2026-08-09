namespace Yession.Domain

open System

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

module TimelineItem =

    /// Where this sits in the log — the single key everything here is ordered by.
    let offset =
        function
        | TimelineMessage item -> item.Offset
        | TimelineBlock (offset, _, _) -> offset
        | TimelineStretch stretch -> stretch.Offset

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
      Titles : Map<string, string> }

module TimelineProjection =

    let empty : TimelineProjection =
        { TerminalItems = []; OpenLeases = Map.empty; Titles = Map.empty }

    /// A range is `None` unless it holds at least one line. `[from, to)` with `to <= from`
    /// is what a rejected command carries and what a pre-Plan-14 lease event decodes to;
    /// both mean the same thing to a reader, so they get the same answer.
    let private rangeOf (fromSeq: int) (toSeq: int) : (int * int) option =
        if toSeq > fromSeq then Some (fromSeq, toSeq) else None

    let private applyEvent (proj: TimelineProjection) (envelope: EventEnvelope<SessionEvent>) : TimelineProjection =
        let append item = { proj with TerminalItems = proj.TerminalItems @ [ item ] }
        match envelope.Event with
        | SessionEvent.TerminalOpened e ->
            { proj with Titles = Map.add (TerminalId.value e.TerminalId) e.Title proj.Titles }
        | SessionEvent.TerminalBlockStarted e ->
            // Anchored at the START, and it mutates in place as it finishes — so a
            // four-minute build's result lands above messages sent while it ran. The
            // alternative, appearing only on completion, makes long work invisible while it
            // is the only thing happening.
            append (TimelineBlock (envelope.Offset, e.TerminalId, e.BlockId))
        | SessionEvent.TerminalCommandRejected e ->
            // A refusal gets a chip too. "The agent proposed this and a human said no" is
            // the more interesting half of the two, and a rejection that appears nowhere is
            // indistinguishable from a bug.
            append (TimelineBlock (envelope.Offset, e.TerminalId, e.BlockId))
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
