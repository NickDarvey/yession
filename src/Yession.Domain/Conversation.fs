namespace Yession.Domain

/// The conversation is a *projection* of the event log — never read from Yjs/draft state.
/// The projection type and its fold live in the shared Domain library because both the
/// Session Process and (later) the Browser Client derive the conversation the same way.
/// See docs/design.md §1 "Reactive", §2.2 and docs/plans/00-init/02-*.

type ConversationItemStatus =
    | Complete
    | Streaming
    | Failed

type ConversationItem =
    { MessageId : MessageId
      Author    : ActorRef
      Body      : string
      Status    : ConversationItemStatus }

type ConversationProjection = { Items : ConversationItem list }

module ConversationProjection =

    let empty : ConversationProjection = { Items = [] }

    /// The conversation items contributed by a single event. The match is total over
    /// `SessionEvent`, so adding a case (MessageSent in Step 06, Agent* in Step 08)
    /// forces this projection to account for it.
    let private itemsFrom (envelope: EventEnvelope<SessionEvent>) : ConversationItem list =
        match envelope.Event with
        | SessionCreated _ -> [] // session lifecycle, not a conversation item
        | PeerJoined _ -> []     // presence, not a conversation item
        | PeerLeft _ -> []       // presence, not a conversation item

    /// Fold ordered event envelopes into a conversation projection.
    ///
    /// `appliedThrough` is the highest offset already folded in; events at or below it are
    /// skipped, so re-applying overlapping pages is idempotent on offset. Returns the
    /// updated projection together with the new high-water offset.
    ///
    /// The signature deliberately takes only events — never synced/draft state — so the
    /// conversation can never depend on collaborative editing state.
    let applyEvents
        (appliedThrough: EventOffset option)
        (events: EventEnvelope<SessionEvent> list)
        (projection: ConversationProjection)
        : ConversationProjection * EventOffset option =
        events
        |> List.fold
            (fun (proj, highWater) envelope ->
                let beyondApplied =
                    match highWater with
                    | Some o -> EventOffset.value envelope.Offset > EventOffset.value o
                    | None -> true
                if beyondApplied then
                    { proj with Items = proj.Items @ itemsFrom envelope }, Some envelope.Offset
                else
                    proj, highWater)
            (projection, appliedThrough)
