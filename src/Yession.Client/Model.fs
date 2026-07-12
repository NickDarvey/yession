namespace Yession.Client

open Yession.Domain

/// The Browser Client Elmish model and update loop shell. It holds a single typed
/// snapshot of what the client knows: the local peer, connection state, synced
/// collaborative state, the conversation projection, the event-consumer read position,
/// and the agent view state. Later steps fill the draft editor, send flow, event
/// consumption, and agent rendering. See docs/design.md §2.1, §2.3 and
/// docs/plans/00-init/04-*.

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
      Environment   : EnvironmentStatus }

/// Messages that drive the client model. Connection-lifecycle messages are produced by
/// the connection driver (Connection.fs); the suffix avoids clashing with the
/// `ConnectionState` cases and the transport frame DU cases. Draft messages (Step 05)
/// mutate only the synced collaborative state; the Ylmish binding turns those model
/// changes into CRDT deltas.
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
    /// The local user asked to send the draft (Step 06): status moves to `Sending`
    /// while the `SendDraft` command is in flight (the driver issues the command).
    | SendDraftMsg of DraftId
    /// The Session Process accepted the send: the draft is `Sent`.
    | DraftSendAcceptedMsg of DraftId
    /// The Session Process rejected the send: the draft returns to `Active`.
    | DraftSendRejectedMsg of DraftId * reason: string

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
          Environment = EnvironmentNotStarted }

    /// Advance the latest-known offset and recompute the catch-up indicator.
    let private withLatestKnown (latest: EventOffset option) (consumer: EventConsumerState) : EventConsumerState =
        { consumer with
            LatestKnownOffset = latest
            IsCatchingUp = isBehind consumer.LastProcessedOffset latest }

    /// Apply a status transition to a draft, if it exists and `transition` allows it.
    let private withDraftStatus
        (draftId: DraftId)
        (transition: DraftState -> DraftStatus option)
        (model: ClientModel)
        : ClientModel =
        match Map.tryFind draftId model.Synced.Drafts with
        | Some draft ->
            match transition draft with
            | Some status ->
                { model with
                    Synced =
                        { model.Synced with
                            Drafts = Map.add draftId { draft with Status = status } model.Synced.Drafts } }
            | None -> model
        | None -> model

    /// Fold a connection-lifecycle message into the model.
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
                        | AgentMessageCompleted _ | AgentTurnFailed _ -> { agent with ActiveTurn = None }
                        | _ -> agent)
                    model.Agent
            let environment =
                freshEvents
                |> List.fold (fun status e -> EnvironmentStatus.applyEvent status e.Event) model.Environment
            let latestKnown = EventOffset.maxOption model.EventConsumer.LatestKnownOffset highWater
            { model with
                Conversation = conversation
                Agent = agent
                Environment = environment
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
                      Body = Ylmish.Text.empty
                      Status = Active }
                { model with
                    Synced = { model.Synced with Drafts = Map.add draftId draft model.Synced.Drafts } }
        | EditDraftBodyMsg (draftId, body) ->
            match Map.tryFind draftId model.Synced.Drafts with
            | Some draft ->
                { model with
                    Synced =
                        { model.Synced with
                            Drafts = Map.add draftId { draft with Body = body } model.Synced.Drafts } }
            | None -> model
        | SendDraftMsg draftId ->
            withDraftStatus draftId (fun draft -> if draft.Status = Active then Some Sending else None) model
        | DraftSendAcceptedMsg draftId ->
            withDraftStatus draftId (fun _ -> Some Sent) model
        | DraftSendRejectedMsg (draftId, _) ->
            withDraftStatus draftId (fun draft -> if draft.Status = Sending then Some Active else None) model
