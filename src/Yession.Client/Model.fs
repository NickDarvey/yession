namespace Yession.Client

open Yession.Domain

/// The Browser Client Elmish model and update loop shell. It holds a single typed
/// snapshot of what the client knows: the local peer, connection state, synced
/// collaborative state, the conversation projection, the event-consumer read position,
/// and the agent view state. See docs/design.md §2.1, §2.3 and docs/plans/00-init/04-*.

type ConnectionState =
    | Disconnected
    | Connecting
    | Connected
    | Reconnecting

type PeerState = { PeerId : PeerId; DisplayName : string }

/// How far the client has consumed the event log versus what it knows exists.
type EventConsumerState =
    { LastProcessedOffset : EventOffset option
      LatestKnownOffset   : EventOffset option
      IsCatchingUp        : bool }

type AgentViewState = { ActiveTurn : AgentTurnId option }

type ClientModel =
    { Peer          : PeerState
      Connection    : ConnectionState
      Synced        : SyncedSessionState
      Conversation  : ConversationProjection
      EventConsumer : EventConsumerState
      Agent         : AgentViewState
      /// The session environment's UI status, folded from lifecycle events (Step 12).
      Environment   : EnvironmentStatus
      /// The read-only command log, folded from command events (Step 13).
      Commands      : CommandLog }

/// Messages that drive the client model. Connection-lifecycle messages are produced by
/// the connection driver (Connection.fs); the suffix avoids clashing with the
/// `ConnectionState` cases and the transport frame DU cases. Draft and queue messages
/// mutate only the synced collaborative state; the Ylmish binding turns those model
/// changes into CRDT deltas — sending needs no command round-trip (Phase 3).
type ClientMsg =
    | ConnectingMsg
    | ConnectedMsg of PeerAcceptedPayload
    | RejectedMsg of reason: string
    | EventsAvailableMsg of latestOffset: EventOffset
    /// A read-only event page from the Session Process (Step 07): the conversation is
    /// built by folding pages through the shared projection; offsets track progress.
    | EventsPageMsg of EventPage<SessionEvent>
    | DisconnectedMsg
    /// Start a draft under an app-minted id (unique keys make concurrent — even
    /// offline — creation safe; see docs/design.md §1 "Ylmish is the sync boundary").
    | StartDraftMsg of DraftId
    /// Replace a draft's body with an edited `Text` (carrying the edit intent).
    | EditDraftBodyMsg of DraftId * Ylmish.Text
    /// Send = enqueue (Phase 3): the draft moves into the shared message queue under
    /// the app-minted `QueueId`, at the tail. A pure CRDT write; the Session Process
    /// consumes the queue when the agent is idle.
    | SendDraftMsg of DraftId * QueueId
    /// Edit a queued message's body (any peer may, until the agent takes it).
    | EditQueuedBodyMsg of QueueId * Ylmish.Text
    /// Reorder a queued message: one fractional-index register write.
    | ReorderQueuedMsg of QueueId * order: float
    /// Delete a queued message. Until consumed, deletion wins: a deleted entry never
    /// becomes an event.
    | DeleteQueuedMsg of QueueId

module ClientModel =

    /// Is `latest` strictly ahead of `processed` (i.e. there is more to consume)?
    let private isBehind (processed: EventOffset option) (latest: EventOffset option) : bool =
        match latest with
        | None -> false
        | Some latest ->
            match processed with
            | None -> true
            | Some processed -> EventOffset.value latest > EventOffset.value processed

    /// The model for a freshly loaded client: disconnected, nothing consumed, idle.
    let init (peer: PeerState) : ClientModel =
        { Peer = peer
          Connection = Disconnected
          Synced = SyncedSessionState.empty
          Conversation = ConversationProjection.empty
          EventConsumer =
            { LastProcessedOffset = None
              LatestKnownOffset = None
              IsCatchingUp = false }
          Agent = { ActiveTurn = None }
          Environment = EnvironmentNotStarted
          Commands = CommandLog.empty }

    /// Advance the latest-known offset and recompute the catch-up indicator.
    let private withLatestKnown (latest: EventOffset option) (consumer: EventConsumerState) : EventConsumerState =
        { consumer with
            LatestKnownOffset = latest
            IsCatchingUp = isBehind consumer.LastProcessedOffset latest }

    let private withSynced (synced: SyncedSessionState) (model: ClientModel) : ClientModel =
        { model with Synced = synced }

    /// Fold a message into the model.
    let update (msg: ClientMsg) (model: ClientModel) : ClientModel =
        match msg with
        | ConnectingMsg ->
            { model with Connection = Connecting }
        | ConnectedMsg accepted ->
            { model with
                Connection = Connected
                Peer = { model.Peer with DisplayName = accepted.AssignedDisplayName }
                EventConsumer = withLatestKnown accepted.LatestOffset model.EventConsumer }
        | RejectedMsg _ ->
            { model with Connection = Disconnected }
        | EventsAvailableMsg latest ->
            { model with EventConsumer = withLatestKnown (Some latest) model.EventConsumer }
        | EventsPageMsg page ->
            // The offset-gated projection fold makes overlapping/duplicate pages
            // idempotent: events at or below the processed offset are skipped.
            let conversation, highWater =
                ConversationProjection.applyEvents
                    model.EventConsumer.LastProcessedOffset
                    page.Events
                    model.Conversation
            let freshEvents =
                let appliedThrough = model.EventConsumer.LastProcessedOffset |> Option.map EventOffset.value
                page.Events
                |> List.filter (fun e ->
                    match appliedThrough with
                    | Some n -> EventOffset.value e.Offset > n
                    | None -> true)
            let agent =
                freshEvents
                |> List.fold
                    (fun (agent: AgentViewState) e ->
                        match e.Event with
                        | AgentTurnStarted a -> { agent with ActiveTurn = Some a.AgentTurnId }
                        | AgentMessageCompleted _ | AgentTurnFailed _ | AgentTurnInterrupted _ ->
                            { agent with ActiveTurn = None }
                        | _ -> agent)
                    model.Agent
            let environment =
                freshEvents
                |> List.fold (fun status e -> EnvironmentStatus.applyEvent status e.Event) model.Environment
            let commands =
                freshEvents
                |> List.fold (fun log e -> CommandLog.applyEvent log e.Event) model.Commands
            let latestKnown = EventOffset.maxOption model.EventConsumer.LatestKnownOffset highWater
            { model with
                Conversation = conversation
                Agent = agent
                Environment = environment
                Commands = commands
                EventConsumer =
                    { LastProcessedOffset = highWater
                      LatestKnownOffset = latestKnown
                      IsCatchingUp = isBehind highWater latestKnown } }
        | DisconnectedMsg ->
            { model with Connection = Reconnecting }
        | StartDraftMsg draftId ->
            // Starting an already-present draft is a no-op: ids are app-minted and unique.
            if Map.containsKey draftId model.Synced.Drafts then
                model
            else
                let draft =
                    { DraftId = draftId
                      Author = model.Peer.PeerId
                      Body = Ylmish.Text.empty }
                model |> withSynced { model.Synced with Drafts = Map.add draftId draft model.Synced.Drafts }
        | EditDraftBodyMsg (draftId, body) ->
            match Map.tryFind draftId model.Synced.Drafts with
            | Some draft ->
                model
                |> withSynced
                    { model.Synced with Drafts = Map.add draftId { draft with Body = body } model.Synced.Drafts }
            | None -> model
        | SendDraftMsg (draftId, queueId) ->
            // Draft -> queue entry, atomically in one model update (one CRDT
            // transaction): the draft key is deleted and the queue key created. The
            // sender is the attributed author; the entry lands at the queue tail.
            match Map.tryFind draftId model.Synced.Drafts with
            | Some draft when not (Map.containsKey queueId model.Synced.Queue) ->
                let entry =
                    { QueueId = queueId
                      Author = model.Peer.PeerId
                      Body = draft.Body
                      Order = QueueOrder.next model.Synced.Queue }
                model
                |> withSynced
                    { model.Synced with
                        Drafts = Map.remove draftId model.Synced.Drafts
                        Queue = Map.add queueId entry model.Synced.Queue }
            | _ -> model
        | EditQueuedBodyMsg (queueId, body) ->
            match Map.tryFind queueId model.Synced.Queue with
            | Some entry ->
                model
                |> withSynced
                    { model.Synced with Queue = Map.add queueId { entry with Body = body } model.Synced.Queue }
            | None -> model
        | ReorderQueuedMsg (queueId, order) ->
            match Map.tryFind queueId model.Synced.Queue with
            | Some entry ->
                model
                |> withSynced
                    { model.Synced with Queue = Map.add queueId { entry with Order = order } model.Synced.Queue }
            | None -> model
        | DeleteQueuedMsg queueId ->
            model |> withSynced { model.Synced with Queue = Map.remove queueId model.Synced.Queue }
