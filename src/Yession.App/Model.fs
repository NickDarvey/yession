namespace Yession.App

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

/// Where the Claude sign-in flow is (Plan 08). `ClaudeAwaitingCode` = the authorize
/// tab is open; completion may land at the Manager's callback (the panel polls status)
/// or arrive as a pasted code.
type ClaudeFlowState =
    | ClaudeIdle
    /// `scope` remembers the sign-in choice ("session" | "mine") the flow began with,
    /// so a pasted-code completion targets the same credential slot.
    | ClaudeAwaitingCode of authorizeUrl: string * scope: string
    | ClaudeBusy
    | ClaudeError of string

/// What the /claude status probe reported: kind label ("oauth"/"static") per sign-in
/// scope, when connected.
type ClaudeStatus =
    { SessionCredential : string option
      MineCredential : string option }

type ClaudeViewState =
    { Status : ClaudeStatus
      Flow : ClaudeFlowState }

/// A remote peer's live caret+selection: the peer's name (for the cursor label) plus its
/// `Focus` — which collaborative field it is in and its position there. Ephemeral presence,
/// delivered over `Presence` frames — never synced through Yjs, never durable. The peer's
/// colour is derived from its id (`PeerColour`), not carried.
type RemotePresence = { DisplayName : string; Focus : Focus }

type ClientModel =
    { Peer          : PeerState
      Connection    : ConnectionState
      /// The serving session's id, learned from `PeerAccepted`; shown as the header's
      /// secondary identifier beside the editable title.
      Session       : SessionId option
      Synced        : SyncedSessionState
      Conversation  : ConversationProjection
      EventConsumer : EventConsumerState
      Agent         : AgentViewState
      /// Other peers' live carets+selections, keyed by peer. Cleared when a peer moves its
      /// caret out of every collaborative field, or disconnects (its `Focus` becomes `None`).
      Presence      : Map<PeerId, RemotePresence>
      /// The session environment's UI status, folded from lifecycle events (Step 12).
      Environment   : EnvironmentStatus
      /// The read-only command log, folded from command events (Step 13).
      Commands      : CommandLog
      /// The Claude connection panel's state (Plan 08), driven by the /claude routes.
      Claude        : ClaudeViewState }

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
    /// Edit the session title (collaborative text, merges like a draft body). A pure CRDT
    /// write; the Session Process reports the settled title to the Manager for the list.
    | EditTitleMsg of Ylmish.Text
    /// A remote peer's cursor moved (or cleared) in the title — ephemeral presence folded
    /// into `Presence`, never into the synced state.
    | RemotePresenceMsg of PresencePayload
    /// Ensure the draft slot keyed by `PeerId` exists (author only). The body is a rich-text
    /// `Y.XmlFragment` anchored by the codec once the slot exists, so the editor has a synced
    /// fragment to bind — the client ensures its own slot so its composer can mount. Editing
    /// is the editor writing that fragment directly (it syncs through the doc); no body message.
    | EnsureDraftMsg of PeerId
    /// Send = enqueue (Phase 3): the owner's draft moves into the shared message queue
    /// under the app-minted `QueueId`, at the tail, and the slot clears. The body fragment's
    /// content is copied draft->queue imperatively at send (shared types can't be re-parented).
    | SendDraftMsg of PeerId * QueueId
    /// Discard the draft in the slot keyed by `PeerId` without sending it.
    | DiscardDraftMsg of PeerId
    /// Reorder a queued message: one fractional-index register write.
    | ReorderQueuedMsg of QueueId * order: float
    /// Delete a queued message. Until consumed, deletion wins: a deleted entry never
    /// becomes an event.
    | DeleteQueuedMsg of QueueId
    /// A fresh /claude status probe result (Plan 08).
    | ClaudeStatusMsg of ClaudeStatus
    /// The Claude sign-in flow moved (begin/busy/error/reset).
    | ClaudeFlowMsg of ClaudeFlowState

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
          Session = None
          Synced = SyncedSessionState.empty
          Conversation = ConversationProjection.empty
          EventConsumer =
            { LastProcessedOffset = None
              LatestKnownOffset = None
              IsCatchingUp = false }
          Agent = { ActiveTurn = None }
          Presence = Map.empty
          Environment = EnvironmentNotStarted
          Commands = CommandLog.empty
          Claude =
            { Status = { SessionCredential = None; MineCredential = None }
              Flow = ClaudeIdle } }

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
                Session = Some accepted.SessionId
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
        | EditTitleMsg title ->
            model |> withSynced { model.Synced with Title = title }
        | RemotePresenceMsg payload ->
            // Never render your own remote caret; a cleared focus removes the peer's entry.
            if payload.PeerId = model.Peer.PeerId then model
            else
                let presence =
                    match payload.Focus with
                    | Some focus ->
                        Map.add payload.PeerId { DisplayName = payload.DisplayName; Focus = focus } model.Presence
                    | None -> Map.remove payload.PeerId model.Presence
                { model with Presence = presence }
        | EnsureDraftMsg peerId ->
            // Materialise the slot keyed by `peerId` (author only) if absent, so the codec
            // anchors its body fragment and the editor can bind. Idempotent.
            if Map.containsKey peerId model.Synced.Drafts then model
            else
                model
                |> withSynced
                    { model.Synced with Drafts = Map.add peerId { Author = peerId } model.Synced.Drafts }
        | SendDraftMsg (peerId, queueId) ->
            // Draft -> queue entry, atomically in one model update (one CRDT transaction):
            // the slot is deleted and the queue key created. Owner-sends: the slot's author
            // is the attributed author; the entry lands at the queue tail. The body fragment's
            // content is carried over imperatively (Browser.sendDraft), not in the model.
            match Map.tryFind peerId model.Synced.Drafts with
            | Some draft when not (Map.containsKey queueId model.Synced.Queue) ->
                let entry =
                    { QueueId = queueId
                      Author = draft.Author
                      Order = QueueOrder.next model.Synced.Queue }
                model
                |> withSynced
                    { model.Synced with
                        Drafts = Map.remove peerId model.Synced.Drafts
                        Queue = Map.add queueId entry model.Synced.Queue }
            | _ -> model
        | DiscardDraftMsg peerId ->
            model |> withSynced { model.Synced with Drafts = Map.remove peerId model.Synced.Drafts }
        | ReorderQueuedMsg (queueId, order) ->
            match Map.tryFind queueId model.Synced.Queue with
            | Some entry ->
                model
                |> withSynced
                    { model.Synced with Queue = Map.add queueId { entry with Order = order } model.Synced.Queue }
            | None -> model
        | DeleteQueuedMsg queueId ->
            model |> withSynced { model.Synced with Queue = Map.remove queueId model.Synced.Queue }
        | ClaudeStatusMsg status ->
            // A connected credential ends an in-flight wait (the callback completed in
            // its own tab); otherwise the flow state is untouched by a mere probe.
            let connected = status.SessionCredential.IsSome || status.MineCredential.IsSome
            let flow =
                match model.Claude.Flow, connected with
                | (ClaudeAwaitingCode _ | ClaudeBusy), true -> ClaudeIdle
                | flow, _ -> flow
            { model with Claude = { Status = status; Flow = flow } }
        | ClaudeFlowMsg flow ->
            { model with Claude = { model.Claude with Flow = flow } }
