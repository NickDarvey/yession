namespace Yession.App

open Yession.Domain

/// The Browser Client Elmish model and update loop shell. It holds a single typed
/// snapshot of what the client knows: the local peer, connection state, synced
/// collaborative state, the conversation projection, the event-consumer read position,
/// and the agent view state. See docs/design.md §2.1, §2.3 and docs/plans/00-init/04-*.

type ConnectionState =
    /// Not connected and not trying. Carries WHY whenever the client knows — a rejected
    /// token, a session that never answered — because a bare "disconnected" is the same
    /// dead end the event feed used to be: a true statement that helps nobody.
    | Disconnected of reason: string option
    | Connecting
    | Connected
    | Reconnecting

type PeerState = { PeerId : PeerId; DisplayName : string }

/// The durable event feed's health — the leg that carries HISTORY, which in the browser is
/// HTTP (immutable chunks) rather than the data channel. Deliberately separate from
/// `ConnectionState`: either leg can be down while the other works, and neither takes the
/// client with it. Collaborative state is CRDT state in a local doc, so a dead feed costs
/// history, not the ability to read, write, or send (docs/design.md §1, local-first).
type FeedHealth =
    /// The last read succeeded — history is current.
    | FeedLive
    /// A read failed and the resilience policy is still trying. `attempt` is how many have
    /// failed so far; `reason` is the fault, for the degraded banner.
    | FeedRetrying of attempt: int * reason: string
    /// The policy gave up. History is whatever is already local, and stays that way until
    /// the next availability hint or reconnect re-arms the read — editing keeps working.
    | FeedStalled of reason: string

/// How far the client has consumed the event log versus what it knows exists.
type EventConsumerState =
    { LastProcessedOffset : EventOffset option
      LatestKnownOffset   : EventOffset option
      IsCatchingUp        : bool
      /// Whether reads are getting through at all. `IsCatchingUp` says there is more to
      /// read; this says whether reading is possible — the distinction the old design had
      /// no way to express, because a failed fetch was reported as an empty final page.
      Feed                : FeedHealth }

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
      MineCredential : string option
      /// Whether THIS session currently has an agent at all (any connected credential
      /// or the host's ambient one). `None` until the first probe answers — the
      /// "no agent" prompt must never flash before the client actually knows.
      AgentAvailable : bool option }

type ClaudeViewState =
    { Status : ClaudeStatus
      Flow : ClaudeFlowState }

/// Which draft the composer has open. `Unchosen` is the state a fresh client is in, and the only
/// one where the DEFAULT applies (join the draft already in flight rather than start a rival) —
/// once someone picks, the pick stands, so "new message" is not undone by a peer starting to type.
type ComposerChoice =
    | Unchosen
    | Own
    | Joined of PeerId

/// A remote peer's live caret+selection: the peer's name (for the cursor label) plus its
/// `Focus` — which collaborative field it is in and its position there. Ephemeral presence,
/// delivered over `Presence` frames — never synced through Yjs, never durable. The peer's
/// colour is derived from its id (`PeerColour`), not carried.
type RemotePresence = { DisplayName : string; Focus : Focus }

type ClientModel =
    { Peer          : PeerState
      Connection    : ConnectionState
      /// The serving session's id: seeded from the shell (so it is known before — and
      /// without — any connection) and re-learned from `PeerAccepted`. Shown as the
      /// header's secondary identifier beside the editable title, and it names the session
      /// the reconnect offer asks the Manager for, which is a moment at which no
      /// `PeerAccepted` has happened by definition.
      Session       : SessionId option
      /// The Manager's public origin as the SHELL was told it (Plan 11): where to ask for
      /// this session back once it has stopped. `None` when the shell carried none — a
      /// Manager-less session — and then there is nothing to offer.
      ///
      /// Static for the life of the page. Never a message, never folded: it is a fact
      /// about the deployment that served this document, not part of the session's state.
      Manager       : string option
      Synced        : SyncedSessionState
      Conversation  : ConversationProjection
      EventConsumer : EventConsumerState
      Agent         : AgentViewState
      /// Other peers' live carets+selections, keyed by peer. Cleared when a peer moves its
      /// caret out of every collaborative field, or disconnects (its `Focus` becomes `None`).
      Presence      : Map<PeerId, RemotePresence>
      /// Every peer this session has seen, with the display name it joined under — folded from
      /// the durable log (`PeerJoined`/`PeerLeft`), so it survives a reload and names a draft's
      /// author even while that author is away. Presence is who is here NOW; this is who is who.
      Peers         : Map<PeerId, string>
      /// Which draft this client has OPEN in the composer, of the at-most-one that can be. App
      /// state, never synced: two people in one session may each have a different draft open.
      Composer      : ComposerChoice
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
    /// The transport could not be opened at all — the session never answered, after the
    /// connect policy spent its retries. Distinct from `RejectedMsg` (which is the session
    /// refusing a peer it did hear from) because the remedy differs: wait, versus re-auth.
    | ConnectFailedMsg of reason: string
    | EventsAvailableMsg of latestOffset: EventOffset
    /// A read-only event page from the Session Process (Step 07): the conversation is
    /// built by folding pages through the shared projection; offsets track progress.
    | EventsPageMsg of EventPage<SessionEvent>
    /// The event feed's health changed: a read failed and is being retried (reported by the
    /// resilience policy composed with the transport), or it failed for good (reported by
    /// the read loop, which is the one place that knows a read is over). A successful page
    /// needs no message — `EventsPageMsg` itself proves the feed is live.
    | EventFeedMsg of FeedHealth
    | DisconnectedMsg
    /// Edit the session title (collaborative text, merges like a draft body). A pure CRDT
    /// write; the Session Process reports the settled title to the Manager for the list.
    | EditTitleMsg of Ylmish.Text
    /// A remote peer's cursor moved (or cleared) in the title — ephemeral presence folded
    /// into `Presence`, never into the synced state.
    | RemotePresenceMsg of PresencePayload
    /// Ensure the draft slot keyed by `PeerId` exists (author only), carrying the queue key it
    /// will become when anyone sends it. The body is a rich-text `Y.XmlFragment` anchored by the
    /// codec once the slot exists, so the editor has a synced fragment to bind — the client
    /// ensures its own slot so its composer can mount. Editing is the editor writing that
    /// fragment directly (it syncs through the doc); no body message.
    | EnsureDraftMsg of PeerId * QueueId
    /// Send = enqueue (Phase 3): the draft in this slot moves into the shared message queue at
    /// the tail under the key the slot has carried since it was published, and the slot clears.
    /// ANY co-editor may send; the same key from every sender is what makes concurrent sends one
    /// entry. The body fragment's content is copied draft->queue imperatively at send (shared
    /// types can't be re-parented).
    | SendDraftMsg of PeerId
    /// Discard the draft in the slot keyed by `PeerId` without sending it. The author's call:
    /// a co-editor collapses a draft, it does not destroy one.
    | DiscardDraftMsg of PeerId
    /// Open this peer's draft in the composer, collapsing whatever was open. Local view state.
    | ExpandDraftMsg of PeerId
    /// Open the local peer's own composer (the "new message" path), collapsing anyone else's.
    | StartDraftMsg
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
          Connection = Disconnected None
          Session = None
          Manager = None
          Synced = SyncedSessionState.empty
          Conversation = ConversationProjection.empty
          EventConsumer =
            { LastProcessedOffset = None
              LatestKnownOffset = None
              IsCatchingUp = false
              // Nothing has failed yet; the first read decides.
              Feed = FeedLive }
          Agent = { ActiveTurn = None }
          Presence = Map.empty
          Peers = Map.empty
          Composer = Unchosen
          Environment = EnvironmentNotStarted
          Commands = CommandLog.empty
          Claude =
            { Status = { SessionCredential = None; MineCredential = None; AgentAvailable = None }
              Flow = ClaudeIdle } }

    /// Advance the latest-known offset and recompute the catch-up indicator.
    let private withLatestKnown (latest: EventOffset option) (consumer: EventConsumerState) : EventConsumerState =
        { consumer with
            LatestKnownOffset = latest
            IsCatchingUp = isBehind consumer.LastProcessedOffset latest }

    let private withSynced (synced: SyncedSessionState) (model: ClientModel) : ClientModel =
        { model with Synced = synced }

    /// Whose draft the composer is showing — the resolved answer to `ComposerChoice`, and the
    /// only place the "join what is already being written" default lives.
    ///
    /// A choice is honoured while that draft still exists (a sent or discarded one falls back).
    /// Unchosen prefers your own draft if you have one, then the drafts already in flight in a
    /// stable order, and finally your own empty composer — so a session where someone is midway
    /// through a message opens on THEIR words, not on a second blank box beside them.
    let composerTarget (model: ClientModel) : PeerId =
        let mine = model.Peer.PeerId
        let others =
            model.Synced.Drafts
            |> Map.toList
            |> List.map fst
            |> List.filter (fun peer -> peer <> mine)
        match model.Composer with
        | Own -> mine
        | Joined peer when Map.containsKey peer model.Synced.Drafts -> peer
        | Joined _
        | Unchosen ->
            if Map.containsKey mine model.Synced.Drafts then mine
            else
                match others with
                | first :: _ -> first
                | [] -> mine

    /// The drafts NOT in the composer, in stable order: the collapsed summaries.
    let collapsedDrafts (model: ClientModel) : PeerId list =
        let target = composerTarget model
        model.Synced.Drafts |> Map.toList |> List.map fst |> List.filter (fun peer -> peer <> target)

    /// Who is editing this draft right now, by their live caret (never the local peer — you are
    /// not your own collaborator). Names come from presence, which is where a live caret's name
    /// already travels.
    let editorsOf (peer: PeerId) (model: ClientModel) : (PeerId * string) list =
        model.Presence
        |> Map.toList
        |> List.filter (fun (_, presence) -> presence.Focus.Field = DraftBody peer)
        |> List.map (fun (editor, presence) -> editor, presence.DisplayName)

    /// A peer's display name: the roster's, else the peer's own live presence, else the raw id
    /// (an id is a last resort, not a label — `PEER-129755065` is not a person).
    let nameOf (peer: PeerId) (model: ClientModel) : string =
        match Map.tryFind peer model.Peers with
        | Some name -> name
        | None ->
            match Map.tryFind peer model.Presence with
            | Some presence when presence.DisplayName <> "" -> presence.DisplayName
            | _ -> PeerId.value peer

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
        | RejectedMsg reason ->
            { model with Connection = Disconnected (Some reason) }
        | ConnectFailedMsg reason ->
            { model with Connection = Disconnected (Some reason) }
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
            // The roster keeps departed peers: a draft's author may have left while their words
            // are still in the composer, and "who wrote this" must still have an answer.
            let peers =
                freshEvents
                |> List.fold
                    (fun roster e ->
                        match e.Event with
                        | PeerJoined joined when joined.DisplayName.Trim () <> "" ->
                            Map.add joined.PeerId joined.DisplayName roster
                        | _ -> roster)
                    model.Peers
            let latestKnown = EventOffset.maxOption model.EventConsumer.LatestKnownOffset highWater
            { model with
                Conversation = conversation
                Agent = agent
                Environment = environment
                Commands = commands
                Peers = peers
                EventConsumer =
                    { LastProcessedOffset = highWater
                      LatestKnownOffset = latestKnown
                      IsCatchingUp = isBehind highWater latestKnown
                      // A page arrived, so the feed is live by construction — recovery from
                      // a stall needs no separate signal.
                      Feed = FeedLive } }
        | EventFeedMsg health ->
            { model with EventConsumer = { model.EventConsumer with Feed = health } }
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
        | EnsureDraftMsg (peerId, queueId) ->
            // Materialise the slot keyed by `peerId` (author only) if absent, so the codec
            // anchors its body fragment and the editor can bind. Idempotent — and the queue key
            // of an existing slot is never re-minted, because every co-editor's send depends on
            // it staying the one the draft was published with.
            if Map.containsKey peerId model.Synced.Drafts then model
            else
                model
                |> withSynced
                    { model.Synced with
                        Drafts = Map.add peerId { Author = peerId; QueueId = queueId } model.Synced.Drafts }
        | SendDraftMsg peerId ->
            // Draft -> queue entry, atomically in one model update (one CRDT transaction):
            // the slot is deleted and its queue key created. The entry is attributed to the
            // slot's AUTHOR, not to whoever pressed send — the sender committed it, the author
            // wrote it — and it lands at the queue tail. Two peers sending this draft
            // concurrently write the same key, so the replicas merge to one entry instead of
            // queueing the message twice. The body fragment's content is carried over
            // imperatively (`App.connect`'s SendDraft), not in the model.
            match Map.tryFind peerId model.Synced.Drafts with
            | Some draft when not (Map.containsKey draft.QueueId model.Synced.Queue) ->
                let entry =
                    { QueueId = draft.QueueId
                      Author = draft.Author
                      Order = QueueOrder.next model.Synced.Queue }
                model
                |> withSynced
                    { model.Synced with
                        Drafts = Map.remove peerId model.Synced.Drafts
                        Queue = Map.add draft.QueueId entry model.Synced.Queue }
            | _ -> model
        | DiscardDraftMsg peerId ->
            model |> withSynced { model.Synced with Drafts = Map.remove peerId model.Synced.Drafts }
        | ExpandDraftMsg peerId ->
            // One draft is open at a time, so opening one IS collapsing the other.
            { model with Composer = if peerId = model.Peer.PeerId then Own else Joined peerId }
        | StartDraftMsg ->
            { model with Composer = Own }
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
