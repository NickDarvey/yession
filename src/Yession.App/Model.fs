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
      /// Whether catch-up has lasted long enough to be worth SAYING. Sending a message puts
      /// the client behind its own event for a round trip, so `IsCatchingUp` is true for a
      /// few dozen milliseconds every time anyone sends anything — and a status that flips
      /// to "catching up" and back on every send is a flicker, not information. The truth
      /// stays in `IsCatchingUp` (the read loop reads it); this is what the UI reports.
      ///
      /// Set by a timer the client arms when catch-up begins and disarms when it ends, so
      /// the threshold is one number in one place (`Browser.catchUpQuietMs`). It can only
      /// ever be true WHILE catching up — the reducer enforces that, so a timer that fires
      /// just after the page landed is harmless rather than a stuck indicator.
      CatchUpIsSlow       : bool
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

/// One terminal's live transcript as this client has it (Plan 13). Records are keyed by
/// their sequence number, which makes application idempotent by construction: the same
/// record arriving twice — once as a live frame, once inside a fetched chunk — is one map
/// entry, exactly as an event at a known offset folds once. That is the whole reason the
/// live leg and the history leg can be different transports and still agree.
type TerminalFeed =
    { Records : Map<int, TranscriptRecord>
      /// The transcript length this client believes the session has, from availability
      /// hints and from the records it has seen. Drives catch-up the way
      /// `LatestKnownOffset` drives the event feed.
      KnownLength : int
      /// How far a contiguous prefix has been read. Fetching resumes here.
      ReadThrough : int
      /// The transcript's own header, once chunk 0 has been fetched (Plan 13, stage 3e).
      /// The replay rebuilds a `.cast` from these records, and the recorded width and height
      /// are what make it come out the shape the terminal actually was.
      Header : TranscriptHeader option }

module TerminalFeed =

    let empty : TerminalFeed = { Records = Map.empty; KnownLength = 0; ReadThrough = 0; Header = None }

    /// Fold one record in. Out-of-order and duplicate records are both fine — the map key
    /// is the sequence number.
    let withRecord (seq: int) (record: TranscriptRecord) (feed: TerminalFeed) : TerminalFeed =
        { feed with
            Records = Map.add seq record feed.Records
            KnownLength = max feed.KnownLength (seq + 1) }

    /// The records in `[fromSeq, toSeq)`, in order — one block's output.
    let slice (fromSeq: int) (toSeq: int) (feed: TerminalFeed) : TranscriptRecord list =
        feed.Records
        |> Map.toList
        |> List.filter (fun (seq, _) -> seq >= fromSeq && seq < toSeq)
        |> List.map snd

    /// The output text of a range: the `o`/`e` records concatenated. Input and resize
    /// records are excluded — a replay shows what was typed, a block's OUTPUT does not.
    let outputText (fromSeq: int) (toSeq: int) (feed: TerminalFeed) : string =
        slice fromSeq toSeq feed
        |> List.filter (fun r -> r.Kind = TranscriptOutput || r.Kind = TranscriptStderr)
        |> List.map (fun r -> r.Data)
        |> String.concat ""

/// One thing the side pane can show (Plan 14, stage 2).
///
/// The pane stops being "the terminal panel" and becomes a tab strip over three kinds of
/// thing. That is a genuine model change rather than a rename: the selection used to be a
/// `TerminalId option`, and a block's read-only view is not a terminal — one terminal can
/// contribute a hundred tabs.
type PaneTab =
    /// A terminal itself: its composer while it is open, its recording once it is closed.
    /// Every terminal the session has ever had is always in the strip, so these are never
    /// "opened" and never closed — they are the strip's furniture.
    | TerminalTab of TerminalId
    /// One block, read-only: the command and what it printed. Opened by tapping its chip in
    /// the chat, and closeable.
    | BlockTab of TerminalId * BlockId
    /// One stretch of live mode. Opened from its chat item, and closeable.
    | StretchTab of TerminalStretch

module PaneTab =

    /// A tab's identity, and the value its DOM hook carries. Prefixed per kind because a
    /// block id and a terminal id are drawn from the same alphabet and a collision would
    /// silently select the wrong tab.
    let key =
        function
        | TerminalTab id -> "terminal:" + TerminalId.value id
        | BlockTab (id, blockId) -> "block:" + TerminalId.value id + ":" + BlockId.value blockId
        | StretchTab stretch -> "stretch:" + TerminalStretch.key stretch

    /// Which terminal this tab is about — what the strip groups by and what a replay reads.
    let terminal =
        function
        | TerminalTab id -> id
        | BlockTab (id, _) -> id
        | StretchTab stretch -> stretch.TerminalId

    /// Whether this tab is one a person opened and can close. Terminal tabs are not: the
    /// strip lists every terminal the session has, and a "close" on one of those already
    /// means something else entirely (closing the terminal).
    let isClosable =
        function
        | TerminalTab _ -> false
        | BlockTab _ | StretchTab _ -> true

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
      /// Whether this deployment's sessions change address between launches (Plan 12).
      /// When true the browser's storage does not survive a restart, because it is
      /// partitioned by origin and the origin carries the port — so the client's
      /// local-first promise has to be qualified wherever it is made.
      ///
      /// Static for the life of the page, like `Manager`: a fact about the deployment that
      /// served this document, never a message and never folded.
      EphemeralStorage : bool
      Synced        : SyncedSessionState
      Conversation  : ConversationProjection
      /// The terminal half of the chat (Plan 14, stage 1): block chips and lease-stretch
      /// items, with the offset each is anchored at. Merged with `Conversation` at render
      /// time by `TimelineProjection.items` — a view-level fold, so the projection that
      /// builds the agent's context is untouched.
      Timeline      : TimelineProjection
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
      /// Terminals, folded from terminal events (Plan 13) — the panel's structure.
      Terminals     : TerminalProjection
      /// Each terminal's transcript as this client has it. Separate from the projection
      /// because it arrives on a different leg: facts fold from the event log, bytes
      /// stream from the transcript.
      TerminalFeeds : Map<TerminalId, TerminalFeed>
      /// Keyframes this client has fetched, keyed by terminal and the transcript line each
      /// paints (Plan 14, stage 3). One per range this client has opened, not one per block
      /// the session ever ran: they are fetched on demand, and a range is only opened by
      /// somebody choosing to read it.
      TerminalKeyframes : Map<TerminalId * int, TranscriptKeyframe>
      /// The read-only tabs this client opened from the chat, oldest first (Plan 14, stage
      /// 2). Terminal tabs are NOT here: every terminal the session has is always in the
      /// strip, and these are the ones a person added by tapping a chip.
      ///
      /// LOCAL to this client, never synced. Opening a recording to read it must not move
      /// anyone else's screen — that is reading, not collaborating. Presence still shows who
      /// is IN a terminal, because that is about the shared thing.
      PaneTabs      : PaneTab list
      /// Which tab the pane is showing. `None` = the first open terminal, resolved by
      /// `selectedPane`.
      PaneChoice    : PaneTab option
      /// Whether the terminals panel is open. View state, never synced: two people in one
      /// session may reasonably want different columns on screen.
      TerminalsOpen : bool
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
    /// Catch-up has been running long enough to be worth saying (or has stopped being).
    /// The client arms a timer when catch-up begins; this is what the timer reports, and
    /// it is the ONLY thing that lights the "catching up" status — see
    /// `EventConsumerState.CatchUpIsSlow` for why the truth alone is too noisy to show.
    | CatchUpSlowMsg of bool
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
    // --- Terminals (Plan 13) ---------------------------------------------------------
    /// One transcript record arrived — live over the data channel, or from a fetched
    /// history chunk. Both routes carry the sequence number, so both fold the same way.
    | TerminalRecordMsg of TerminalId * seq: int * TranscriptRecord
    /// A terminal's transcript is this long. A hint that triggers a read, never data.
    | TerminalAvailableMsg of TerminalId * length: int
    /// A contiguous prefix of a terminal's transcript has been read through this seq.
    | TerminalReadThroughMsg of TerminalId * seq: int
    /// The transcript's header, from the chunk that carried line 0 (Plan 13, stage 3e).
    | TerminalHeaderMsg of TerminalId * TranscriptHeader
    /// A keyframe arrived for a terminal (Plan 14, stage 3): the screen a ranged replay
    /// starts from. Fetched when a tab needs one, never streamed — a keyframe is read by
    /// somebody opening a recording, not by everybody watching one grow.
    | TerminalKeyframeMsg of TerminalId * TranscriptKeyframe
    /// Show this terminal in the pane.
    | SelectTerminalMsg of TerminalId
    /// Bring an already-open tab forward (Plan 14, stage 2).
    | SelectPaneTabMsg of PaneTab
    /// Open a read-only tab from the chat — a block's view or a stretch's replay — and show
    /// it. Idempotent on the tab's key.
    | OpenPaneTabMsg of PaneTab
    /// Close one. Only tabs a person opened can be closed; a terminal's own tab is the
    /// strip's furniture, and "close" on one of those already means closing the terminal.
    | ClosePaneTabMsg of PaneTab
    /// Open or close the terminals column.
    | ToggleTerminalsMsg
    /// Ensure the composer slot for (terminal, author) exists, carrying the queue key it
    /// becomes when sent. The author's own call, exactly as for a message draft.
    | EnsureTerminalDraftMsg of TerminalId * PeerId * QueueId
    /// Send = enqueue: the slot's command moves into the terminal's queue at the tail
    /// under the key the slot has carried since publication, and the slot clears.
    | SendTerminalDraftMsg of TerminalId * PeerId
    /// Drop a composer slot without sending it.
    | DiscardTerminalDraftMsg of TerminalId * PeerId
    /// Approve a queued command, by the peer approving it. The drain runs it on the next
    /// pass; until then it is still editable, which is the point.
    | ApproveTerminalQueuedMsg of QueueId * PeerId
    /// Withdraw an approval — the mirror of granting one, so a mis-click is undoable for
    /// as long as the command has not been consumed.
    | UnapproveTerminalQueuedMsg of QueueId
    /// Refuse a queued command, by the peer refusing it. Not a deletion: the drain observes
    /// the refusal, records who said no and why, and only then removes the entry — so the
    /// log that captures every yes captures the noes too.
    | RejectTerminalQueuedMsg of QueueId * PeerId * reason: string option
    /// Delete a queued command. Until consumed, deletion wins.
    | DeleteTerminalQueuedMsg of QueueId
    /// Reorder a queued command within its terminal: one fractional-index register write.
    | ReorderTerminalQueuedMsg of QueueId * order: float
    /// Set a terminal's approval mode.
    | SetTerminalModeMsg of TerminalId * TerminalApprovalMode

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
          EphemeralStorage = false
          Synced = SyncedSessionState.empty
          Conversation = ConversationProjection.empty
          Timeline = TimelineProjection.empty
          EventConsumer =
            { LastProcessedOffset = None
              LatestKnownOffset = None
              IsCatchingUp = false
              CatchUpIsSlow = false
              // Nothing has failed yet; the first read decides.
              Feed = FeedLive }
          Agent = { ActiveTurn = None }
          Presence = Map.empty
          Peers = Map.empty
          Composer = Unchosen
          Environment = EnvironmentNotStarted
          Terminals = TerminalProjection.empty
          TerminalFeeds = Map.empty
          TerminalKeyframes = Map.empty
          PaneTabs = []
          PaneChoice = None
          TerminalsOpen = false
          Claude =
            { Status = { SessionCredential = None; MineCredential = None; AgentAvailable = None }
              Flow = ClaudeIdle } }

    /// Advance the latest-known offset and recompute the catch-up indicator. "Slow" is a
    /// property of a catch-up that is STILL RUNNING, so it dies with the catch-up it
    /// described — the timer that set it never has to be raced.
    let private withLatestKnown (latest: EventOffset option) (consumer: EventConsumerState) : EventConsumerState =
        let catchingUp = isBehind consumer.LastProcessedOffset latest
        { consumer with
            LatestKnownOffset = latest
            IsCatchingUp = catchingUp
            CatchUpIsSlow = catchingUp && consumer.CatchUpIsSlow }

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

    /// The tab strip, in the order it renders: every terminal the session has ever had,
    /// then the read-only tabs this client opened from the chat.
    ///
    /// Closed terminals stay in the strip (Plan 13, stage 3e) — a closed terminal is where
    /// its recording is read, so dropping it the moment its shell exits would put the audit
    /// out of reach exactly when it starts to matter.
    let paneTabs (model: ClientModel) : PaneTab list =
        (model.Terminals.Terminals |> List.map (fun t -> TerminalTab t.TerminalId)) @ model.PaneTabs

    /// Which tab the pane shows: the stored choice while it is still in the strip, else the
    /// first OPEN terminal. Resolved rather than stored, for the same reason `composerTarget`
    /// is: a choice that outlives what it pointed at is a blank pane nobody asked for. The
    /// default lands somewhere you can type.
    let selectedPane (model: ClientModel) : PaneTab option =
        let known = paneTabs model |> List.map PaneTab.key
        match model.PaneChoice with
        | Some chosen when List.contains (PaneTab.key chosen) known -> Some chosen
        | _ ->
            TerminalProjection.openTerminals model.Terminals
            |> List.map (fun t -> TerminalTab t.TerminalId)
            |> List.tryHead

    /// Which terminal the pane is about — the selected tab's, whichever kind it is. A block
    /// tab and a stretch tab still belong to a terminal, which is what the composer, the
    /// presence marks and the transcript reads are keyed by.
    let selectedTerminal (model: ClientModel) : TerminalId option =
        selectedPane model |> Option.map PaneTab.terminal

    /// The `.cast` for one range of a terminal's recording — a block's output, or a stretch
    /// of live mode — from the records and the keyframe this client has (Plan 14, stage 3).
    ///
    /// `None` when the header has not arrived: the header is transcript line 0, so a client
    /// that has not read the first chunk cannot say how big the screen is, and a recording
    /// under a guessed geometry rewraps every line in it.
    ///
    /// A MISSING keyframe is not `None`. The range still rebases and still plays; it is then
    /// the naive slice, approximately right for command output and wrong wherever the screen
    /// carried state in. Refusing to play a recording we do have would be the worse answer,
    /// and the surface says which one it is showing.
    let rangedCast (terminal: TerminalId) (fromSeq: int) (toSeq: int) (model: ClientModel) : string option =
        let feed = model.TerminalFeeds |> Map.tryFind terminal |> Option.defaultValue TerminalFeed.empty
        feed.Header
        |> Option.map (fun header ->
            TranscriptReplay.range
                header
                (Map.tryFind (terminal, fromSeq) model.TerminalKeyframes)
                fromSeq
                toSeq
                (feed.Records |> Map.toList))

    /// A terminal's feed, empty when nothing has arrived for it yet.
    let terminalFeed (terminal: TerminalId) (model: ClientModel) : TerminalFeed =
        model.TerminalFeeds |> Map.tryFind terminal |> Option.defaultValue TerminalFeed.empty

    /// A terminal's queued commands in run order.
    let terminalQueue (terminal: TerminalId) (model: ClientModel) : TerminalQueued list =
        TerminalQueueOrder.sortedFor terminal model.Synced.TerminalQueue

    /// The composer slots published in a terminal, in stable order — every peer mid-command
    /// there, the local peer included.
    let terminalDrafts (terminal: TerminalId) (model: ClientModel) : PeerId list =
        model.Synced.TerminalDrafts
        |> Map.toList
        |> List.filter (fun ((t, _), _) -> t = terminal)
        |> List.map (fun ((_, author), _) -> author)
        |> List.sortBy PeerId.value

    /// Whether a queued command is waiting on an approval it has not got — the one question
    /// the queue's UI asks, answered by the same function the drain asks
    /// (`TerminalApprovalMode.requiresApproval`), so a badge can never disagree with what
    /// the Session Process will actually do.
    let awaitsApproval (entry: TerminalQueued) (model: ClientModel) : bool =
        TerminalApprovalMode.requiresApproval (SyncedSessionState.modeOf entry.Terminal model.Synced) entry.Author
        && Option.isNone entry.ApprovedBy

    /// Whether a queued command is held because a peer is typing in its terminal (Plan 13,
    /// stage 2e) rather than because it needs an approval. Reported apart because they resolve
    /// differently — one when a person makes a decision, the other when a person finishes a
    /// task — and a queue that said only *pending* would leave both looking like a stall.
    let awaitsTerminal (entry: TerminalQueued) (model: ClientModel) : bool =
        TerminalProjection.tryFind entry.Terminal model.Terminals
        |> Option.bind (fun view -> view.Lease)
        |> Option.isSome

    /// Whether a queued command is held because its terminal's shell stopped emitting marks
    /// (Plan 13, stage 2f) rather than because a peer is typing there. Apart again for the
    /// same reason: they resolve differently — one when a person finishes, this one when
    /// somebody repairs the terminal.
    let awaitsIntegration (entry: TerminalQueued) (model: ClientModel) : bool =
        TerminalProjection.tryFind entry.Terminal model.Terminals
        |> Option.map (fun view -> view.IntegrationLost)
        |> Option.defaultValue false

    /// Who is editing a terminal composer right now, by their live caret.
    let terminalEditorsOf (terminal: TerminalId) (author: PeerId) (model: ClientModel) : (PeerId * string) list =
        model.Presence
        |> Map.toList
        |> List.filter (fun (_, presence) -> presence.Focus.Field = TerminalDraftBody (terminal, author))
        |> List.map (fun (editor, presence) -> editor, presence.DisplayName)

    // --- Where everyone is ------------------------------------------------------------------
    // Presence already drove the per-field overlays (a caret in a body, a dot on a draft), but
    // each of those is only visible from INSIDE the surface it is about — so a collaborator
    // typing a command in a terminal you are not looking at, or renaming the session while you
    // read the timeline, was invisible. These three answer "where is everyone" from the model,
    // and the roster and the terminal strip render it.
    //
    // A peer appears exactly while its caret is in a collaborative field, because that is
    // precisely what the session knows: presence clears when the caret leaves (`Focus = None`)
    // and when the peer goes. Listing everyone who has EVER joined (`Peers`, which deliberately
    // keeps the departed so a draft's author still has a name) would report people who left
    // days ago as being in the room.

    /// Every peer that is somewhere collaborative right now, with its name and where —
    /// never the local peer, who is not their own collaborator. Ordered by name so the
    /// roster does not reshuffle when a map's internal order changes.
    let presentPeers (model: ClientModel) : (PeerId * string * FocusField) list =
        model.Presence
        |> Map.toList
        |> List.filter (fun (peer, _) -> peer <> model.Peer.PeerId)
        |> List.map (fun (peer, presence) -> peer, presence.DisplayName, presence.Focus.Field)
        |> List.sortBy (fun (peer, name, _) -> name, PeerId.value peer)

    /// The terminal a focus is in, when it is in one. A composer slot names its terminal
    /// directly; a queued command names only its entry, and the entry names the terminal —
    /// so this is the one place that join lives.
    let terminalOfFocus (field: FocusField) (model: ClientModel) : TerminalId option =
        match field with
        | TerminalDraftBody (terminal, _) -> Some terminal
        | TerminalQueuedBody queueId ->
            model.Synced.TerminalQueue |> Map.tryFind queueId |> Option.map (fun entry -> entry.Terminal)
        | Title | DraftBody _ | QueueBody _ -> None

    /// Who is in a given terminal right now — whether writing a new command or editing a
    /// queued one, because from the strip they are the same fact: someone is in there.
    let peersInTerminal (terminal: TerminalId) (model: ClientModel) : (PeerId * string) list =
        presentPeers model
        |> List.filter (fun (_, _, field) -> terminalOfFocus field model = Some terminal)
        |> List.map (fun (peer, name, _) -> peer, name)

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
            let terminals =
                freshEvents
                |> List.fold (fun proj e -> TerminalProjection.applyEvent proj e.Event) model.Terminals
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
            // The terminal half of the chat, gated on the same offset as the conversation —
            // one page, two folds, merged only at render.
            let timeline, _ =
                TimelineProjection.applyEvents
                    model.EventConsumer.LastProcessedOffset
                    page.Events
                    model.Timeline
            let latestKnown = EventOffset.maxOption model.EventConsumer.LatestKnownOffset highWater
            { model with
                Conversation = conversation
                Timeline = timeline
                Agent = agent
                Environment = environment
                Terminals = terminals
                Peers = peers
                EventConsumer =
                    { LastProcessedOffset = highWater
                      LatestKnownOffset = latestKnown
                      IsCatchingUp = isBehind highWater latestKnown
                      // A catch-up that has finished was never slow, whatever the timer
                      // was about to say.
                      CatchUpIsSlow =
                        isBehind highWater latestKnown && model.EventConsumer.CatchUpIsSlow
                      // A page arrived, so the feed is live by construction — recovery from
                      // a stall needs no separate signal.
                      Feed = FeedLive } }
        | EventFeedMsg health ->
            { model with EventConsumer = { model.EventConsumer with Feed = health } }
        | CatchUpSlowMsg slow ->
            // Gated on still being behind, so a timer that fires just after the page landed
            // cannot light an indicator with nothing left to report.
            { model with
                EventConsumer =
                    { model.EventConsumer with
                        CatchUpIsSlow = slow && model.EventConsumer.IsCatchingUp } }
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
        | TerminalRecordMsg (terminal, seq, record) ->
            let feed = terminalFeed terminal model |> TerminalFeed.withRecord seq record
            { model with TerminalFeeds = Map.add terminal feed model.TerminalFeeds }
        | TerminalAvailableMsg (terminal, length) ->
            let feed = terminalFeed terminal model
            { model with
                TerminalFeeds =
                    Map.add terminal { feed with KnownLength = max feed.KnownLength length } model.TerminalFeeds }
        | TerminalHeaderMsg (terminal, header) ->
            let feed = terminalFeed terminal model
            { model with TerminalFeeds = Map.add terminal { feed with Header = Some header } model.TerminalFeeds }
        | TerminalReadThroughMsg (terminal, seq) ->
            let feed = terminalFeed terminal model
            { model with
                TerminalFeeds =
                    Map.add
                        terminal
                        { feed with ReadThrough = max feed.ReadThrough seq; KnownLength = max feed.KnownLength seq }
                        model.TerminalFeeds }
        | TerminalKeyframeMsg (terminal, keyframe) ->
            { model with TerminalKeyframes = Map.add (terminal, keyframe.Seq) keyframe model.TerminalKeyframes }
        | SelectTerminalMsg terminal ->
            { model with PaneChoice = Some (TerminalTab terminal); TerminalsOpen = true }
        | SelectPaneTabMsg tab ->
            { model with PaneChoice = Some tab; TerminalsOpen = true }
        | OpenPaneTabMsg tab ->
            // Idempotent on the tab's key: tapping the same chip twice brings its tab
            // forward rather than opening a second one that says exactly the same thing.
            let already = model.PaneTabs |> List.exists (fun t -> PaneTab.key t = PaneTab.key tab)
            { model with
                PaneTabs = if already then model.PaneTabs else model.PaneTabs @ [ tab ]
                PaneChoice = Some tab
                TerminalsOpen = true }
        | ClosePaneTabMsg tab ->
            // The selection falls back through `selectedPane` — a choice naming a tab that
            // is no longer in the strip resolves to the first open terminal, which is the
            // same rule a closed terminal already relies on. Clearing it here as well would
            // be a second mechanism for one fact.
            { model with PaneTabs = model.PaneTabs |> List.filter (fun t -> PaneTab.key t <> PaneTab.key tab) }
        | ToggleTerminalsMsg ->
            { model with TerminalsOpen = not model.TerminalsOpen }
        | EnsureTerminalDraftMsg (terminal, author, queueId) ->
            // Idempotent, and the queue key of an existing slot is never re-minted: every
            // co-editor's send depends on it staying the one the slot was published with.
            if Map.containsKey (terminal, author) model.Synced.TerminalDrafts then model
            else
                model
                |> withSynced
                    { model.Synced with
                        TerminalDrafts =
                            Map.add
                                (terminal, author)
                                { Terminal = terminal; Author = author; QueueId = queueId }
                                model.Synced.TerminalDrafts }
        | SendTerminalDraftMsg (terminal, author) ->
            // Slot -> queue entry in one model update (one CRDT transaction), attributed to
            // the slot's AUTHOR rather than to whoever pressed send. Two peers sending the
            // same slot write the same key, so the replicas merge to one entry instead of
            // running the command twice. The command TEXT is carried over imperatively in
            // the same transaction (`App.connect`'s SendTerminalDraft) — shared types
            // cannot be re-parented.
            match Map.tryFind (terminal, author) model.Synced.TerminalDrafts with
            | Some draft when not (Map.containsKey draft.QueueId model.Synced.TerminalQueue) ->
                let entry =
                    { QueueId = draft.QueueId
                      Terminal = terminal
                      // The author is the PEER who wrote it. Attribution to a verified user
                      // happens at the durable append, where the Session Process knows the
                      // binding — the doc only ever knows connections.
                      Author = PeerRef author
                      Order = TerminalQueueOrder.nextFor terminal model.Synced.TerminalQueue
                      ApprovedBy = None
                      RejectedBy = None
                      RejectedReason = None }
                model
                |> withSynced
                    { model.Synced with
                        TerminalDrafts = Map.remove (terminal, author) model.Synced.TerminalDrafts
                        TerminalQueue = Map.add draft.QueueId entry model.Synced.TerminalQueue }
            | _ -> model
        | DiscardTerminalDraftMsg (terminal, author) ->
            model
            |> withSynced
                { model.Synced with TerminalDrafts = Map.remove (terminal, author) model.Synced.TerminalDrafts }
        | ApproveTerminalQueuedMsg (queueId, approver) ->
            match Map.tryFind queueId model.Synced.TerminalQueue with
            | Some entry ->
                model
                |> withSynced
                    { model.Synced with
                        TerminalQueue =
                            Map.add queueId { entry with ApprovedBy = Some approver } model.Synced.TerminalQueue }
            | None -> model
        | UnapproveTerminalQueuedMsg queueId ->
            match Map.tryFind queueId model.Synced.TerminalQueue with
            | Some entry ->
                model
                |> withSynced
                    { model.Synced with
                        TerminalQueue = Map.add queueId { entry with ApprovedBy = None } model.Synced.TerminalQueue }
            | None -> model
        | RejectTerminalQueuedMsg (queueId, rejector, reason) ->
            match Map.tryFind queueId model.Synced.TerminalQueue with
            | Some entry ->
                model
                |> withSynced
                    { model.Synced with
                        TerminalQueue =
                            Map.add
                                queueId
                                { entry with RejectedBy = Some rejector; RejectedReason = reason }
                                model.Synced.TerminalQueue }
            | None -> model
        | DeleteTerminalQueuedMsg queueId ->
            model |> withSynced { model.Synced with TerminalQueue = Map.remove queueId model.Synced.TerminalQueue }
        | ReorderTerminalQueuedMsg (queueId, order) ->
            match Map.tryFind queueId model.Synced.TerminalQueue with
            | Some entry ->
                model
                |> withSynced
                    { model.Synced with
                        TerminalQueue = Map.add queueId { entry with Order = order } model.Synced.TerminalQueue }
            | None -> model
        | SetTerminalModeMsg (terminal, mode) ->
            model |> withSynced { model.Synced with TerminalModes = Map.add terminal mode model.Synced.TerminalModes }
