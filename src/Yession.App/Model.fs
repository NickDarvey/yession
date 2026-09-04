namespace Yession.App

open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Agent
open Yession.Domain.Link
open Yession.Domain.Terminals
open Yession.Domain.Collab
open Yession.Domain.Tools
open Yession.Domain.Chat
open Yession.Domain.Prs

/// The Browser Client Elmish model and update loop shell. It holds a single typed
/// snapshot of what the client knows: the local peer, connection state, synced
/// collaborative state, the conversation projection, the event-consumer read position,
/// and the agent view state. See docs/design.md §2.1, §2.3.

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
/// HTTP by cursor — immutable ranges kept in the client's own store — rather than the data
/// channel. Deliberately separate from `ConnectionState`: either leg can be down while the
/// other works, and neither takes the client with it. Collaborative state is CRDT state in a
/// local doc, so a dead feed costs history, not the ability to read, write, or send
/// (docs/design.md §1, local-first).
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
      Feed                : FeedHealth
      /// Where this client's KEPT history resumes, when the boot replay could not walk to
      /// it (Plan 20): everything between `LastProcessedOffset` and this offset is not on
      /// this device. `None` is the ordinary state — nothing kept, or everything kept in
      /// one unbroken run.
      ///
      /// A fact about the STORE, never about the feed: it is settled before a single read
      /// leaves this client, so it can neither prove nor deny that history is arriving. The
      /// feed repairs it — a read resumes at the cursor, which the replay parked at exactly
      /// the offset the fill has to start from — which is why any page off the network
      /// clears it. Reported as feed health, it flashed a red "history paused" over every
      /// cold open with an out-of-order store, moments before the first page fixed it.
      MissingBefore       : EventOffset option }

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

/// One connected credential as a panel row reads it.
///
/// `SignInRequired` carries the REASON rather than a flag, because a row that says only
/// "broken" sends somebody to guess. "the refresh token has expired" and "github rejected
/// this credential" lead a person to the same button but tell them different things about
/// why they are pressing it. `None` means nothing has established otherwise — not a promise
/// that it works, which is a thing no side of this can honestly make about a static token.
type ConnectionView =
    { Kind : string
      SignInRequired : string option }

/// What the /claude status probe reported, per sign-in scope, when connected.
type ClaudeStatus =
    { SessionCredential : ConnectionView option
      MineCredential : ConnectionView option
      /// Who the "all my sessions" scope would belong to here: `"user"` (this signed-in
      /// human alone) or `"local"` (the whole deployment — everyone who can reach this
      /// Manager, under `--auth localhost`). The panel must not promise "mine" for a
      /// credential everybody shares. `None` until the first probe answers.
      Owner : string option
      /// Whether THIS session currently has an agent at all (any connected credential
      /// or the host's ambient one). `None` until the first probe answers — the
      /// "no agent" prompt must never flash before the client actually knows.
      AgentAvailable : bool option }

[<RequireQualifiedAccess>]
type ClaudeViewState =
    { Status : ClaudeStatus
      Flow : ClaudeFlowState }

/// Where the GitHub sign-in flow is (Plan 14). Device flow: the panel shows a user
/// code, the human approves it on github.com in their own tab, and the browser polls
/// the session (which polls GitHub) until the grant lands.
type GitHubFlowState =
    | GitHubIdle
    /// The code is on screen. `scope` remembers the sign-in choice ("session" |
    /// "mine"); `interval` is GitHub's polling pace in seconds, which `slow_down`
    /// replies may widen mid-flow.
    | GitHubAwaitingApproval of userCode: string * verificationUri: string * scope: string * interval: int
    | GitHubBusy
    | GitHubError of string

/// What the /github status probe reported, per sign-in scope, when connected.
type GitHubStatus =
    { SessionCredential : ConnectionView option
      MineCredential : ConnectionView option }

[<RequireQualifiedAccess>]
type GitHubViewState =
    { Status : GitHubStatus
      Flow : GitHubFlowState }

/// What the picker knows about the models it can offer. Three states and no fourth,
/// because a picker has exactly three honest things to say: I have not looked yet, here is
/// the list, or here is why there is no list. A single `AgentModel list` could not tell the
/// first from a provider that genuinely offers nothing, and the difference is what decides
/// whether a person waits or goes and connects an account.
type ModelCatalogueState =
    /// Nothing has been asked for yet, or an answer is in flight.
    | ModelsUnknown
    | ModelsLoaded of AgentModel list
    /// The lookup answered, and what it said was why it could not.
    | ModelsUnavailable of reason: string

/// The generated read surface's state (Plan 15), folded from the `/queries` stream.
///
/// There is no `Busy` and no `Error` here, and their absence is the design rather than an
/// omission: this surface has no actions, so nothing can be in flight, and a query that
/// cannot be answered simply keeps its last known value rather than blanking. Whatever
/// went wrong went wrong for the AGENT, which is where it is actionable.
type QueriesViewState =
    { /// What this session declares. Empty until the stream's opening frame — a client
      /// renders the sections it is told about, never a list it was compiled with.
      Declared : QueryDef list
      /// The latest value per query name. Absent = not answered yet.
      Values : Map<string, QueryValue> }

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
    | TerminalTab of TerminalId
    /// One block, read-only: the command and what it printed. Opened by tapping its chip in
    /// the chat.
    | BlockTab of TerminalId * BlockId
    /// One stretch of live mode. Opened from its chat item.
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

    /// Whether this tab is a LIVE terminal's, given the terminals as they stand (Plan 20,
    /// stage 1) — what the strip may keep.
    ///
    /// The strip holds the working set, and a recording is not one: a closed terminal leaves
    /// the strip and stays in the list, which is where every terminal the session has ever
    /// had now lives. A pin on a recording is a person keeping something to READ, which is
    /// what a block or stretch tab is, and those stay however their terminal ends.
    let isLive (terminals: Projection) =
        function
        | TerminalTab id ->
            Projection.tryFind id terminals |> Option.map (fun t -> t.IsOpen) |> Option.defaultValue false
        | BlockTab _ | StretchTab _ -> true

/// Which read of a tab the pane is showing (Plan 25, stage 2): the reader's POSITION — which
/// tab, and for a terminal where in its history — and their FIDELITY — the text of it, or the
/// recording — as ONE fact.
///
/// They were four fields that had to agree (`PaneChoice`, `PanePlaying`, `PaneRewound`,
/// `TerminalList`), and every message cleared the subset its author had in mind. That is how a
/// chip tapped over the terminal list retitled the pane and showed nothing (the list flag
/// survived a choice that meant to replace it), and how the list's own rewind verb undid
/// itself (two messages whose clear-sets cancelled). Here every transition states the whole
/// next mode, so a half-cleared state is not a bug to find, it is a value that cannot be
/// written.
///
/// What is NOT here is what the reader did not choose. A stretch is always its recording and a
/// closed terminal with nothing but a recording plays without being asked — both are rules
/// about the terminal, folded where the terminal is, and `playsRecording` is the one place the
/// reader's choice and those rules meet.
type TabMode =
    /// A tab's text read: a terminal's scrollback, a block's output, a stretch's facts.
    | Reading of PaneTab
    /// A tab's recording, because the reader asked for it.
    | Watching of PaneTab
    /// A terminal's recording, entered FROM one of its blocks, and starting at that command.
    ///
    /// The block's IDENTITY rather than the transcript line it starts at: the line, and the
    /// time the player needs, are derived from the projection when the recording is assembled
    /// (`paneReplay`), so a hint cannot go stale against blocks that arrived after it.
    | WatchingFrom of TerminalId * BlockId
    /// A LIVE terminal watched from behind its edge — the DVR — carrying the transcript length
    /// the rewind pinned. A pin exists only in this case, which is what makes "pinned to a
    /// block's recording" unwritable rather than merely unwritten.
    | WatchingBehind of TerminalId * pin: int
    /// A terminal's TEXT, positioned at one of its commands — "show in terminal" (Plan 25,
    /// stage 3). The answer to "what was going on around this", which is a question about
    /// POSITION and wants more text, not a player: the same scrollback, scrolled to the
    /// command and marking it.
    | ReadingAt of TerminalId * BlockId

module TabMode =

    /// Which tab this mode is about. The three terminal-shaped cases are all that terminal's
    /// own tab; they differ in what is shown there, which is the point of the split.
    let tab =
        function
        | Reading tab
        | Watching tab -> tab
        | WatchingFrom (terminal, _)
        | WatchingBehind (terminal, _)
        | ReadingAt (terminal, _) -> TerminalTab terminal

    /// Whether this mode is a recording rather than a text read — the reader's half of
    /// `ClientModel.playsRecording`.
    let watches =
        function
        | Reading _ | ReadingAt _ -> false
        | Watching _ | WatchingFrom _ | WatchingBehind _ -> true

    /// The command a terminal's text read is positioned at, if it is positioned at one — what
    /// the reveal scrolls to.
    let anchor =
        function
        | ReadingAt (terminal, blockId) -> Some (terminal, blockId)
        | Reading _ | Watching _ | WatchingFrom _ | WatchingBehind _ -> None

    /// The OTHER read of the same thing — what the one watch/read toggle dispatches.
    ///
    /// Position is navigation and fidelity is a mode, so flipping the mode never moves the
    /// reader: a watch entered at a command comes back to that command's text, and the way
    /// back out is the same control in the same slot. That is what makes the toggle keep its
    /// focus, and what retired the four differently-named exits that used to leave the
    /// document behind them.
    ///
    /// Total, because a mode with only one read never renders the toggle: a stretch IS its
    /// recording, and so is a closed terminal that ran nothing (`ReplayIsTheRead`). Those
    /// rows are unreachable and still stated, because a partial function here would be a
    /// crash waiting for the surface to change its mind.
    let toggled =
        function
        | Reading tab -> Watching tab
        | Watching tab -> Reading tab
        // The anchor survives the flip, in both directions. This pair IS the step-out the
        // old "play whole terminal" reached for: the position was already the command, so
        // watching from it needs no hint riding a message, and coming back lands where the
        // reader was rather than at the top of a scrollback.
        | ReadingAt (terminal, blockId) -> WatchingFrom (terminal, blockId)
        | WatchingFrom (terminal, blockId) -> ReadingAt (terminal, blockId)
        // "Live". A pin is a fact about watching from behind an edge, so it dies with the
        // watch rather than being carried into a read that has no use for it.
        | WatchingBehind (terminal, _) -> Reading (TerminalTab terminal)

/// The pane's one face (Plan 25, stage 2): a tab, or the census of every terminal.
type PaneMode =
    | OnTab of TabMode
    /// The terminal list. A DESTINATION rather than a mask over one — which is what it was as
    /// a boolean, and why a chip could open a tab nobody could see.
    ///
    /// It remembers the mode it covered so that glancing at the list and coming back resumes
    /// the read, a DVR pin included. That is the one thing masking did right, and the only
    /// reason this carries anything at all.
    | OnList of resume: TabMode option

module PaneMode =

    /// The tab-mode showing, if one is: `None` while the list is up.
    let onTab =
        function
        | OnTab mode -> Some mode
        | OnList _ -> None

    /// The tab-mode this face is ABOUT — the one showing, or the one the list is covering.
    /// What the pane's furniture (the strip, the header, the composer) reads, because those
    /// answer "which terminal am I working with" rather than "what is on screen".
    let subject =
        function
        | OnTab mode -> Some mode
        | OnList resume -> resume

/// What a pane tab's player should be handed (Plan 14, stage 4) — a whole recording, or a
/// range of one, plus the things the stock player already knows how to do with it.
type PaneReplay =
    { /// The `.cast` text, ready to mount — chapters included, as `"m"` events written into
      /// the recording (`TranscriptReplay.castWithMarkers`) rather than handed to the player
      /// beside it. The player compresses idle time in the EVENTS it loads and would leave a
      /// marker list on the uncompressed clock; in the file, a chapter moves with the
      /// records around it.
      Cast : string
      /// Where to start playing — how a watch entered from a command lands on that command
      /// in full context, without slicing anything. In the recording's own clock: the player
      /// maps it onto the compressed one itself.
      StartAt : float option
      /// The time whose frame becomes the still shown before anyone presses play.
      ///
      /// Fed by replaying events while `time < poster`, so a poster asking for the frame at
      /// time T shows the one BEFORE it. Every poster here means "the screen as it stood
      /// after that record", so each is nudged past its record rather than landing on it.
      Poster : float option
      /// Set when this cast is a LIVE terminal watched from behind its edge (Plan 14,
      /// stage 7): it ends where the rewind pinned it, not where the terminal is, so
      /// playing past its end means the reader has caught up — the mount answers by
      /// jumping back to live rather than stopping on a stale frame.
      BehindLive : TerminalId option }

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
      /// Whether this client can keep the history it is given (Plan 20). The store is the
      /// Cache API, which needs a secure context — loopback and every `https://` mount are
      /// one, a session reached over plain HTTP at a LAN address is not.
      ///
      /// Defaults to TRUE, and the browser says otherwise: the server renders this shell too,
      /// and a default of false would have every server-rendered page announce a missing store
      /// before the client that knows has had a chance to look.
      CanKeepHistory : bool
      /// Whether this client has finished reading the history it already had (Plan 20).
      ///
      /// It exists because an empty timeline had two opposite meanings wearing one mark: the
      /// idle caret says *nothing was ever said here*, and it was also what a client showed
      /// while it had not yet looked. After the local store landed, the second is the common
      /// case on a cold open — so the caret was telling most people the opposite of the truth.
      ///
      /// One flag rather than a `Pending | Restoring | Restored`: the view asks one question,
      /// "has this client looked yet", and a state nothing distinguishes is a state nobody
      /// can act on. Starts FALSE, including on the server-rendered shell, because at first
      /// paint no client has looked.
      HistoryRead : bool
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
      Terminals     : Projection
      /// Each terminal's transcript as this client has it. Separate from the projection
      /// because it arrives on a different leg: facts fold from the event log, bytes
      /// stream from the transcript.
      TerminalFeeds : Map<TerminalId, TerminalFeed>
      /// Keyframes this client has fetched, keyed by terminal and the transcript line each
      /// paints (Plan 14, stage 3). One per range this client has opened, not one per block
      /// the session ever ran: they are fetched on demand, and a range is only opened by
      /// somebody choosing to read it.
      TerminalKeyframes : Map<TerminalId * int, TranscriptKeyframe>
      /// The live SCREEN of each terminal, as this client has composed it (Plan 14, stage
      /// 6): the serialized output of an emulator fed the Process's snapshot and every
      /// record since.
      ///
      /// A screen, not a stream — a terminal in live mode is running a program that moves
      /// the cursor, and what it DISPLAYS is a projection of what it emitted. The transcript
      /// stays the record; this is the view.
      TerminalScreens : Map<TerminalId, string>
      /// How big this client's own view of each terminal is, in CHARACTER CELLS: the box the
      /// output is laid into, measured from the rendered page rather than assumed.
      ///
      /// What it is FOR is the command about to be queued (`PendingAct.Size`). A block that
      /// ran at eighty columns is eighty-column text in the transcript for ever, so the width
      /// a command runs at is worth as much as the command — and until this, block mode had
      /// no width at all: every terminal was 80x24 for its whole life.
      ///
      /// LOCAL, never synced. A shared register of everyone's viewport has to answer "whose
      /// wins", and that is the question the size riding the ACT exists to avoid. What leaves
      /// this client is a claim about one command, or a resize on a lease this peer holds —
      /// neither of them a fact about the room.
      ///
      /// A terminal the pane is not showing keeps its last measurement rather than losing it.
      /// There is nothing on screen to measure, and no measurement is not the same as a
      /// measurement of nothing: the width this reader last had is the truer answer, and the
      /// only one they could have meant.
      TerminalViewports : Map<TerminalId, Size>
      /// What this client PINNED to the strip, in pin order (Plan 20, stage 1).
      ///
      /// The strip used to be a census — every terminal the session ever had, for ever,
      /// because it was the only door to a recording. The list is that door now, so the
      /// strip can be what a person is actually working with: their pins, and whatever they
      /// are looking at.
      ///
      /// A LIST rather than a set, because pin order is what a reader's tabs sit in and a
      /// set would re-order them on any change. LOCAL to this client, never synced: pinning
      /// is reading, not collaborating.
      Pins          : PaneTab list
      /// What the pane is SHOWING: which tab, which read of it, or the census (Plan 25,
      /// stage 2). `None` = nothing chosen yet, resolved to a default by `selectedPane`.
      ///
      /// One field rather than the four this replaces, because the four had to agree and
      /// nothing made them: see `PaneMode`. Its tab is also the PREVIEW slot (Plan 20, stage
      /// 1) — a tab that is shown and not pinned is transient, and showing anything else
      /// replaces it. There is no second field for that: a pinned tab and a previewed one
      /// differ by whether `Pins` names it, which is the only fact there is.
      Pane          : PaneMode option
      /// Whether the terminals panel is open. View state, never synced: two people in one
      /// session may reasonably want different columns on screen.
      TerminalsOpen : bool
      /// Which timeline item has its actions menu open, if any. View state for the same
      /// reason the column above is: a menu one person opened is not a thing anybody else
      /// is looking at.
      ///
      /// ONE field rather than a set, and that is the invariant: a second menu cannot be
      /// open, because opening one is writing this. Two open menus would be two popovers
      /// over one column with one Escape between them.
      ItemMenu      : MessageId option
      /// What this client has just put on the clipboard, named by the hook of the box it
      /// came out of (`Dom.Hooks.githubUserCode` and whatever joins it). View state, local
      /// and transient for the same reason the menu above is: copying is one person's act
      /// on one machine, and nobody else is looking at their clipboard.
      ///
      /// ONE slot, so the confirmation cannot be showing on two boxes at once — and `None`
      /// again a moment later, put back by whoever set it (the browser's `Copy`), because
      /// what it says is "just now" and nothing else in the model expires on its own.
      Copied        : string option
      /// The Claude connection panel's state (Plan 08), driven by the /claude routes.
      Claude        : ClaudeViewState
      /// The GitHub connection panel's state (Plan 14), driven by the /github routes.
      GitHub        : GitHubViewState
      /// What the picker has to choose from, fetched from /models. View state and NOT
      /// synced: the catalogue is the same for everybody, so syncing it would be a second
      /// copy of a fact the session already holds — the CHOICE is what collaborates, and
      /// that lives in `Synced.Model`.
      Models        : ModelCatalogueState
      /// The generated read surface (Plan 15), driven by the /queries stream.
      Queries       : QueriesViewState
      /// Repos whose sensitive capability set is waiting on somebody here (Plan 27).
      ///
      /// Folded from the same events the Process gates on, so what a person is asked and
      /// what a sandbox is waiting for are two readings of one log rather than two answers.
      Approvals     : RepoApprovals.Pending }

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
    /// A page this client had already been given and kept (Plan 20): replayed out of its own
    /// store at boot, before any network read and without a session.
    ///
    /// Folded through exactly the same projection as `EventsPageMsg` — the events are the
    /// same events — and differing in one thing, which is the reason it is a separate case:
    /// it says NOTHING about the feed. A page off the network proves the feed works; a page
    /// off the local store proves only that this client kept it, and an offline client
    /// reporting a live history feed would be lying about the one leg that is down.
    | LocalHistoryMsg of EventPage<SessionEvent>
    /// The boot replay could not walk all the way through what this client kept (Plan 20):
    /// the carried offset is where the kept history resumes, and everything between the read
    /// cursor and it is not on this device.
    ///
    /// Its own message rather than a feed fault, because it is not one: no read has been
    /// attempted when it is dispatched, and the next one repairs it. See
    /// `EventConsumerState.MissingBefore`.
    | LocalHistoryGapMsg of EventOffset
    /// The client has finished reading what it already had (Plan 20) — whether that was a
    /// full conversation, or nothing at all because it keeps nothing. Either way it has now
    /// LOOKED, which is what the timeline needs to know before it can claim a session is
    /// empty.
    | HistoryReadMsg
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
    /// A fresh /github status probe result (Plan 14).
    | GitHubStatusMsg of GitHubStatus
    /// The GitHub sign-in flow moved (begin/awaiting/busy/error/reset).
    | GitHubFlowMsg of GitHubFlowState
    /// What /models answered: the catalogue, or why there isn't one.
    | ModelCatalogueMsg of ModelCatalogueState
    /// Pick the model this session's turns run on — `None` hands the choice back to the
    /// provider. One register, written like a gate: the reducer sets it and the Ylmish
    /// binding carries it to every peer.
    | SetModelMsg of ModelId option
    /// Mark this message for the rail, or take its mark off — one message, because there is
    /// one act. Which way it goes is `Landmarks.toggle`'s to decide, from the item and the
    /// verdicts already recorded: a message carrying the desired state would be a message
    /// whose sender had to know what an unmarked-by-nature act defaults to.
    | ToggleLandmarkMsg of MessageId
    /// One frame off the multiplexed query stream (Plan 15) — the declarations, or one
    /// query's current value. ONE message for the whole read surface, however many
    /// queries there are: a message per query would be a message per FUTURE query too.
    | QueryFrameMsg of QueryFrame
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
    /// The live screen, recomposed (Plan 14, stage 6). Dispatched by the platform half,
    /// which owns the emulator: a screen is a projection an emulator maintains, and the
    /// reducer is pure.
    | TerminalScreenMsg of TerminalId * screen: string
    /// This client's own view of a terminal was measured, and it had moved (Plan 13, stage
    /// 2b). Dispatched by the platform half, which is the only half that can measure a box —
    /// and it measures on the edges a render loop cannot see, a splitter dragged or a window
    /// turned, because those change the box without changing anything the model holds.
    | TerminalViewportMsg of TerminalId * Size
    /// Show something in the pane, in a stated read of it (Plan 25, stage 2).
    ///
    /// ONE message for every way in — a chat chip, a tab in the strip, a row in the list, the
    /// watch toggle, catching back up to live — because each of them is the same act: name
    /// the mode the pane is in next. The six messages this replaces each cleared a different
    /// subset of four fields, which is what let a chip open a tab the list was still covering
    /// and let the list's rewind cancel itself.
    ///
    /// A tab shown and not pinned is the PREVIEW slot (Plan 20, stage 1): showing anything
    /// else replaces it, so a person reading twenty chips ends with one tab, not twenty.
    | ShowInPaneMsg of TabMode
    /// Keep this tab, or stop keeping it (Plan 20, stage 1). Unpinning is not closing:
    /// unpinning a terminal leaves it running and leaves its row in the list, and the one
    /// verb that ends a terminal lives on that row.
    | TogglePinMsg of PaneTab
    /// Rewind a LIVE terminal (Plan 14, stage 7): watch what it has recorded so far, from a
    /// transcript length pinned NOW while the terminal keeps running.
    ///
    /// Its own message rather than a `ShowInPaneMsg (WatchingBehind …)` a caller composes,
    /// because the pin is read off the feed at the moment of the rewind — a caller that had
    /// to look it up first could look it up wrong, or forget, and the rule belongs with the
    /// state it governs.
    | RewindTerminalMsg of TerminalId
    /// Open or close the terminals column.
    | ToggleTerminalsMsg
    /// Open this item's actions menu, or shut it if it is the one already open. A toggle
    /// rather than an open, because the control that sends it is the same control either
    /// way — pressing the ellipsis a second time has to put the menu away.
    | ToggleItemMenuMsg of MessageId
    /// Shut whatever menu is open. Everything that dismisses one sends this: Escape, a
    /// press outside it, and choosing something from it.
    | CloseItemMenuMsg
    /// Something was copied to the clipboard (`Some` the hook of the box it came from), or
    /// the moment for saying so has passed (`None`).
    ///
    /// The clipboard write itself is the browser's — a permission the page may be refused —
    /// so this is dispatched only where the write SUCCEEDED. A confirmation the reducer
    /// could set on its own would be a claim about a clipboard nothing here has read.
    | CopiedMsg of string option
    /// Show the terminal list, or go back to the read it covered (Plan 20, stage 0).
    | ToggleTerminalListMsg
    /// Ensure the composer slot for (terminal, author) exists, carrying the queue key it
    /// becomes when sent. The author's own call, exactly as for a message draft.
    | EnsureTerminalDraftMsg of TerminalId * PeerId * QueueId
    /// Send = enqueue: the slot's command moves into the terminal's queue at the tail
    /// under the key the slot has carried since publication, and the slot clears.
    | SendTerminalDraftMsg of TerminalId * PeerId
    /// Drop a composer slot without sending it.
    | DiscardTerminalDraftMsg of TerminalId * PeerId
    /// Delete a queued command. Until consumed, deletion wins.
    | DeletePendingMsg of QueueId
    /// Reorder a queued command within its terminal: one fractional-index register write.
    | ReorderPendingMsg of QueueId * order: float

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
          CanKeepHistory = true
          HistoryRead = false
          Synced = SyncedSessionState.empty
          Conversation = ConversationProjection.empty
          Approvals = RepoApprovals.empty
          Timeline = TimelineProjection.empty
          EventConsumer =
            { LastProcessedOffset = None
              LatestKnownOffset = None
              IsCatchingUp = false
              CatchUpIsSlow = false
              // Nothing has failed yet; the first read decides.
              Feed = FeedLive
              // Nothing has been looked at yet; the replay decides.
              MissingBefore = None }
          Agent = { ActiveTurn = None }
          Presence = Map.empty
          Peers = Map.empty
          Composer = Unchosen
          Environment = EnvironmentNotStarted
          Terminals = Projection.empty
          TerminalFeeds = Map.empty
          TerminalKeyframes = Map.empty
          TerminalScreens = Map.empty
          TerminalViewports = Map.empty
          Pins = []
          Pane = None
          TerminalsOpen = false
          ItemMenu = None
          Copied = None
          Claude =
            { Status = { SessionCredential = None; MineCredential = None; Owner = None; AgentAvailable = None }
              Flow = ClaudeIdle }
          GitHub =
            { Status = { SessionCredential = None; MineCredential = None }
              Flow = GitHubIdle }
          Models = ModelsUnknown
          Queries = { Declared = []; Values = Map.empty } }

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

    /// Whether a draft carries anything a send would take.
    ///
    /// The SLOT is the answer, not a second measurement: `DraftSlot` publishes a peer's slot
    /// exactly while their body has content and retracts it the moment it empties, so this is
    /// the same fact the send path already acts on. Serializing the body again here would cost
    /// a Markdown pass per render and, worse, give the composer a way to disagree with the rule
    /// about whether there is anything to send.
    let draftHasContent (peer: PeerId) (model: ClientModel) : bool =
        Map.containsKey peer model.Synced.Drafts

    /// Who is editing this draft right now, by their live caret (never the local peer — you are
    /// not your own collaborator). Names come from presence, which is where a live caret's name
    /// already travels.
    let editorsOf (peer: PeerId) (model: ClientModel) : (PeerId * string) list =
        model.Presence
        |> Map.toList
        |> List.filter (fun (_, presence) -> presence.Focus.Field = DraftBody peer)
        |> List.map (fun (editor, presence) -> editor, presence.DisplayName)

    /// Whether this client is keeping a tab.
    let isPinned (tab: PaneTab) (model: ClientModel) : bool =
        model.Pins |> List.exists (fun pinned -> PaneTab.key pinned = PaneTab.key tab)

    /// The tab strip, in the order it renders (Plan 20, stage 1): the pins that are still
    /// live, in pin order, then whatever is being previewed.
    ///
    /// The preview is at the END and never in the middle, so a person reading one recording
    /// after another watches one tab change rather than their pins shuffling under them.
    /// A closed terminal is not here at all — its row in the list is where its recording is
    /// read now, which is what lets the strip stop being a census.
    let rec paneTabs (model: ClientModel) : PaneTab list =
        // No filter here: a pin on a terminal that has closed is dropped where the close is
        // FOLDED, so the strip is simply the pins. Filtering again at render would be a
        // second mechanism for one fact, free to disagree with the first.
        //
        // The preview is the RESOLVED selection rather than the stored choice, because a
        // client that has pinned nothing still shows a terminal — whatever `selectedPane`
        // fell back to — and a strip that omitted it would be a tablist with no tab for the
        // panel it is sitting above.
        let previewed =
            match selectedPane model with
            | Some chosen when not (isPinned chosen model) -> [ chosen ]
            | _ -> []
        model.Pins @ previewed

    /// Which tab the pane shows: the stored choice while what it names still exists, else the
    /// first pinned live terminal, else the first open one. Resolved rather than stored, for
    /// the same reason `composerTarget` is: a choice that outlives what it pointed at is a
    /// blank pane nobody asked for. The default lands somewhere you can type.
    ///
    /// A choice naming a CLOSED terminal survives, and that is not an oversight: it is how
    /// the list opens a recording. What it must not survive is naming a terminal the session
    /// does not have.
    and selectedPane (model: ClientModel) : PaneTab option =
        let exists (tab: PaneTab) =
            match tab with
            | TerminalTab id -> Projection.tryFind id model.Terminals |> Option.isSome
            | BlockTab _ | StretchTab _ -> true
        // The mode's SUBJECT rather than only what is on screen: while the list is up it is
        // the read the list covers, so the strip, the header and the composer keep answering
        // "which terminal am I working with" instead of going blank behind the census.
        match model.Pane |> Option.bind PaneMode.subject |> Option.map TabMode.tab with
        | Some chosen when exists chosen -> Some chosen
        | _ ->
            let pinnedTerminal = model.Pins |> List.tryPick (function TerminalTab _ as tab -> Some tab | _ -> None)
            match pinnedTerminal with
            | Some tab -> Some tab
            | None ->
                Projection.openTerminals model.Terminals
                |> List.map (fun t -> TerminalTab t.TerminalId)
                |> List.tryHead

    /// Whether the pane is showing the census rather than a tab (Plan 20, stage 0; Plan 25,
    /// stage 2). A face the pane is IN, not a flag over the one it is in — which is why
    /// nothing else has to remember to clear it.
    let showsList (model: ClientModel) : bool =
        match model.Pane with
        | Some (OnList _) -> true
        | Some (OnTab _) | None -> false

    /// The command the pane's text read is positioned at (Plan 25, stage 3) — what the
    /// browser scrolls into view once the render that put it on screen has happened.
    let paneAnchor (model: ClientModel) : (TerminalId * BlockId) option =
        model.Pane |> Option.bind PaneMode.onTab |> Option.bind TabMode.anchor

    /// Which terminal the pane is about — the selected tab's, whichever kind it is. A block
    /// tab and a stretch tab still belong to a terminal, which is what the composer, the
    /// presence marks and the transcript reads are keyed by.
    let selectedTerminal (model: ClientModel) : TerminalId option =
        selectedPane model |> Option.map PaneTab.terminal

    /// The transcript length this client's rewind pinned, while the terminal is still LIVE
    /// (Plan 14, stage 7). Resolved rather than read raw, for the same reason `selectedPane`
    /// is: a pin that outlives its live edge — the terminal closed while somebody sat behind
    /// it — is not a rewind any more, it is simply the recording, and the closed-terminal
    /// replay already shows that in full.
    let rewoundTo (terminal: TerminalId) (model: ClientModel) : int option =
        model.Pane
        |> Option.bind PaneMode.subject
        |> Option.bind (function WatchingBehind (id, pin) -> Some (id, pin) | _ -> None)
        |> Option.filter (fun (id, _) -> id = terminal)
        |> Option.filter (fun _ ->
            Projection.tryFind terminal model.Terminals
            |> Option.map (fun view -> view.IsOpen)
            |> Option.defaultValue false)
        |> Option.map snd

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
    let private rangedCastFrom
        (header: TranscriptHeader)
        (feed: TerminalFeed)
        (model: ClientModel)
        (terminal: TerminalId)
        (fromSeq: int)
        (toSeq: int)
        : string =
        TranscriptReplay.range
            header
            (Map.tryFind (terminal, fromSeq) model.TerminalKeyframes)
            fromSeq
            toSeq
            (feed.Records |> Map.toList)

    let rangedCast (terminal: TerminalId) (fromSeq: int) (toSeq: int) (model: ClientModel) : string option =
        let feed = model.TerminalFeeds |> Map.tryFind terminal |> Option.defaultValue TerminalFeed.empty
        feed.Header |> Option.map (fun header -> rangedCastFrom header feed model terminal fromSeq toSeq)

    /// The transcript range a block's recording covers, when it has one.
    ///
    /// A block's range is only a RANGE once it has an end. While it runs, its recording grows
    /// on every record, and a player rebuilt on each one would thrash through a streaming
    /// build; a refused command never ran at all. Both are cases the surface reports rather
    /// than plays.
    ///
    /// One rule, two callers: what a player is handed (`paneReplay`) and whether a player is
    /// OFFERED (`playable`) are the same question asked at two moments, and a surface that
    /// offered a control the builder then refused would be a button that does nothing.
    let private blockRange (terminal: TerminalId) (blockId: BlockId) (model: ClientModel) : (int * int) option =
        Projection.tryFind terminal model.Terminals
        |> Option.bind (fun view -> view.Blocks |> List.tryFind (fun b -> b.BlockId = blockId))
        |> Option.filter (fun block -> match block.Status with BlockRejected _ -> false | _ -> true)
        |> Option.bind (fun block -> block.ToSeq |> Option.map (fun toSeq -> block.FromSeq, toSeq))

    /// How far past a record's own time a poster asking for THAT record's screen must sit.
    ///
    /// The player paints a poster by replaying events while `time < poster`, so a poster at
    /// a record's exact time stops just short of it and shows the screen as it stood BEFORE
    /// that record — one frame early, in the still whose whole job is to be the last thing
    /// that happened. Smaller than any interval a recording can distinguish, so it can never
    /// reach into the next record.
    let private posterNudge = 0.001

    /// What a tab's player should be handed (Plan 14, stage 4).
    ///
    /// Assembled here rather than in the browser entry because every part of it is a
    /// function of the model, and a value the cheap tier can assert on is worth more than
    /// one only a real player can.
    let paneReplay (tab: PaneTab) (model: ClientModel) : PaneReplay option =
        let feed = model.TerminalFeeds |> Map.tryFind (PaneTab.terminal tab) |> Option.defaultValue TerminalFeed.empty
        /// The recording's own clock at a transcript line — what a marker, a poster and a
        /// start position are all measured in. `None` for a line this client has not read.
        let timeOf (seq: int) = feed.Records |> Map.tryFind seq |> Option.map (fun r -> r.At)
        match feed.Header with
        // The header is transcript line 0; without it the geometry is a guess, and a
        // recording replayed under the wrong one rewraps every line in it.
        | None -> None
        | Some header ->
            match tab with
            | BlockTab (terminal, blockId) ->
                blockRange terminal blockId model
                |> Option.map (fun (fromSeq, toSeq) ->
                    { Cast = rangedCastFrom header feed model terminal fromSeq toSeq
                      StartAt = None
                      Poster = None
                      BehindLive = None })
            | StretchTab stretch ->
                match stretch.Range with
                // Nothing to replay, and the surface says so rather than mounting a player
                // over an empty recording — which is indistinguishable from a quiet session.
                | None -> None
                | Some (fromSeq, toSeq) ->
                    let origin = timeOf fromSeq |> Option.defaultValue 0.0
                    Some
                        { Cast = rangedCastFrom header feed model stretch.TerminalId fromSeq toSeq
                          StartAt = None
                          // A still of the FINAL screen, so the item has a face before anyone
                          // presses play. It costs nothing extra: the player builds it by
                          // replaying internally to that time.
                          Poster = timeOf (toSeq - 1) |> Option.map (fun at -> at - origin + posterNudge)
                          BehindLive = None }
            | TerminalTab terminal ->
                // A REWOUND live terminal plays what it has recorded so far, up to the
                // length pinned when the rewind began. Everything else about it is the
                // whole-terminal recording, which is the point: rewinding live TV and
                // replaying a finished session are the same mechanism with a moving end.
                let pin = rewoundTo terminal model
                let markers =
                    Projection.tryFind terminal model.Terminals
                    |> Option.map (fun view ->
                        view.Blocks
                        |> List.choose (fun block ->
                            // A block whose first line this client has not read has no time
                            // to mark, and a marker at a guessed one would point at the
                            // wrong command.
                            timeOf block.FromSeq |> Option.map (fun at -> at, block.Command)))
                    |> Option.defaultValue []
                let records =
                    match pin with
                    | Some length -> feed.Records |> Map.toList |> List.filter (fun (seq, _) -> seq < length)
                    | None -> feed.Records |> Map.toList
                // A rewind lands AT the pinned edge, not at the recording's start: "rewind"
                // on an hour-old terminal must not mean "restart from the beginning". The
                // still is the screen as it stood at the pin — visually the live screen the
                // reader just left — and the scrub bar is how they go back from there.
                let pinnedEdge =
                    pin |> Option.bind (fun _ -> records |> List.tryLast |> Option.map (fun (_, r) -> r.At))
                Some
                    { Cast = TranscriptReplay.castWithMarkers header records markers
                      StartAt =
                        match pinnedEdge with
                        | Some at -> Some at
                        | None ->
                            // A watch entered from one of this terminal's blocks starts at
                            // that command. The block's line is looked up HERE rather than
                            // carried in the mode, so a hint cannot disagree with the blocks
                            // the projection actually has.
                            model.Pane
                            |> Option.bind PaneMode.subject
                            |> Option.bind (function
                                | WatchingFrom (id, blockId) when id = terminal -> Some blockId
                                | _ -> None)
                            |> Option.bind (fun blockId ->
                                Projection.tryFind terminal model.Terminals
                                |> Option.bind (fun view -> view.Blocks |> List.tryFind (fun b -> b.BlockId = blockId)))
                            |> Option.bind (fun block -> timeOf block.FromSeq)
                      Poster = pinnedEdge |> Option.map (fun at -> at + posterNudge)
                      BehindLive = pin |> Option.map (fun _ -> terminal) }

    /// The keyframe a tab's replay needs and this client does not have (Plan 14, stage 4).
    ///
    /// Fetched on demand rather than streamed: a keyframe is read by somebody opening a
    /// recording, not by everybody watching one grow, and there is one per block in a
    /// session that may have run thousands.
    let missingKeyframe (tab: PaneTab) (model: ClientModel) : (TerminalId * int) option =
        let wanted =
            match tab with
            | BlockTab (terminal, blockId) ->
                Projection.tryFind terminal model.Terminals
                |> Option.bind (fun view -> view.Blocks |> List.tryFind (fun b -> b.BlockId = blockId))
                // A refused command has an empty range and never ran, so there is no screen
                // it started from and nothing to fetch.
                |> Option.filter (fun block -> block.Status <> BlockRunning && (match block.Status with BlockRejected _ -> false | _ -> true))
                |> Option.map (fun block -> terminal, block.FromSeq)
            | StretchTab stretch -> stretch.Range |> Option.map (fun (fromSeq, _) -> stretch.TerminalId, fromSeq)
            // A whole recording starts at the start; the header is its keyframe.
            | TerminalTab _ -> None
        wanted |> Option.filter (fun key -> not (Map.containsKey key model.TerminalKeyframes))

    /// Whether this client is watching a terminal behind its live edge (Plan 14, stage 7).
    let isRewound (terminal: TerminalId) (model: ClientModel) : bool =
        rewoundTo terminal model |> Option.isSome

    /// How much recording has accrued past this client's pin, in the recording's own clock
    /// (seconds). `None` when not rewound; `Some 0.0` while nothing new has arrived. What
    /// lets the surface say HOW FAR behind the reader is, which a bare "behind live" cannot.
    let behindLive (terminal: TerminalId) (model: ClientModel) : float option =
        rewoundTo terminal model
        |> Option.map (fun pin ->
            let feed = model.TerminalFeeds |> Map.tryFind terminal |> Option.defaultValue TerminalFeed.empty
            let latestBefore limit =
                feed.Records |> Map.fold (fun acc seq r -> if seq < limit then max acc r.At else acc) 0.0
            max 0.0 (latestBefore System.Int32.MaxValue - latestBefore pin))

    /// The live screen of a terminal, when this client has composed one.
    let terminalScreen (terminal: TerminalId) (model: ClientModel) : string option =
        model.TerminalScreens |> Map.tryFind terminal

    /// A terminal's feed, empty when nothing has arrived for it yet.
    let terminalFeed (terminal: TerminalId) (model: ClientModel) : TerminalFeed =
        model.TerminalFeeds |> Map.tryFind terminal |> Option.defaultValue TerminalFeed.empty

    /// Whether this client holds anything of a terminal's recording (Plan 20, stage 0) — the
    /// one client-local input `Affordances.ofView` takes.
    ///
    /// Either signal counts, because they are the same fact reaching this client two ways: a
    /// LIVE terminal's length arrives as a catch-up hint before any chunk is fetched, and a
    /// CLOSED one's records arrive as chunks with no live hint behind them. Asking only one
    /// would offer the rewind on a terminal whose records had not been fetched, or refuse the
    /// replay on a recording sitting in the feed.
    let hasRecording (terminal: TerminalId) (model: ClientModel) : bool =
        let feed = terminalFeed terminal model
        feed.KnownLength > 0 || not (Map.isEmpty feed.Records)

    /// What a terminal's row offers this reader.
    let affordances (view: TerminalView) (model: ClientModel) : Affordances =
        Affordances.ofView (hasRecording view.TerminalId model) view

    /// Whether this tab has a recording to play at all — what decides whether a surface
    /// OFFERS one. Cheap on purpose: map lookups and a list find, no cast built, so a view
    /// can ask it on every render without assembling a recording nobody watches.
    ///
    /// The header gates every case because it is transcript line 0: without it the geometry
    /// is a guess, and a recording replayed under the wrong one rewraps every line in it.
    let playable (tab: PaneTab) (model: ClientModel) : bool =
        (terminalFeed (PaneTab.terminal tab) model).Header |> Option.isSome
        && match tab with
           // A whole terminal's recording starts at the start, and the header is its
           // keyframe: there is nothing else to resolve.
           | TerminalTab _ -> true
           | BlockTab (terminal, blockId) -> blockRange terminal blockId model |> Option.isSome
           // A stretch with no recorded bounds is a gap in the record, which the surface
           // states rather than playing an empty player over.
           | StretchTab stretch -> Option.isSome stretch.Range

    /// Whether the pane shows this tab as its RECORDING rather than as its text.
    ///
    /// The two reads of one history: what a terminal PRINTED, which the client can render as
    /// text from the same transcript bytes, and how it BEHAVED, which only a player can show.
    /// Showing both at once made the second redundant wherever the first said everything — a
    /// command and its result, with a player of the same two lines beneath — so text is the
    /// read and the recording is a destination you go to.
    ///
    /// Except where there is no text read to go back to, and then the recording is not a
    /// destination, it is the surface. That rule is a fact about the terminal, so it lives
    /// with the terminal (`ReplayIsTheRead`) and this only asks it.
    ///
    /// Every player in the pane is here, the DVR included: rewinding a live terminal is this
    /// same swap with a moving end (`RewindTerminalMsg` asks for the recording like any other
    /// way in), and a second condition beside this one is how two surfaces that mount the
    /// same player start disagreeing about when.
    ///
    /// Not gated on `playable`: the mode is what the READER asked for, and a recording whose
    /// header has not arrived yet is a mount that fills in on the render after it does. The
    /// two questions are separate on purpose — `playable` decides what to OFFER, this decides
    /// what is SHOWN, and a control is only ever offered where the first says yes.
    let playsRecording (tab: PaneTab) (model: ClientModel) : bool =
        let chosen =
            model.Pane
            |> Option.bind PaneMode.subject
            |> Option.exists (fun mode -> TabMode.watches mode && PaneTab.key (TabMode.tab mode) = PaneTab.key tab)
        match tab with
        // A stretch IS a stretch of recording: somebody held the keyboard, and what they did
        // is bytes rather than commands. There are no blocks to read instead, so it plays
        // wherever there is anything to play — and where there is not, the surface says so in
        // words rather than mounting a player over a gap.
        | StretchTab _ -> playable tab model
        // A block's output is the cheaper read of the same bytes, so a block plays only
        // where its reader said so.
        | BlockTab _ -> chosen
        | TerminalTab id ->
            chosen
            || (Projection.tryFind id model.Terminals
                |> Option.exists (fun view -> (affordances view model).ReplayIsTheRead))

    /// The terminal list, in the order it renders (Plan 20, stage 0): the OPEN terminals in
    /// open order, then the closed ones most recently opened first.
    ///
    /// Two orders because the two halves answer different questions. The open half is the
    /// working set and mirrors the strip exactly — two surfaces listing the same live
    /// terminals in two orders would be a difference a reader has to hold in their head. The
    /// closed half is history, and history reads newest first.
    ///
    /// Ordered by OPEN order rather than by last activity, which the projection cannot
    /// answer: a `TerminalView` carries no clock, and inventing one from block ranges would
    /// make the list's order a function of how much a terminal printed.
    let terminalRows (model: ClientModel) : TerminalView list =
        let opened, closed = model.Terminals.Terminals |> List.partition (fun t -> t.IsOpen)
        opened @ List.rev closed

    /// A terminal's queued commands in run order.
    let terminalQueue (terminal: TerminalId) (model: ClientModel) : PendingAct list =
        TerminalQueueOrder.sortedFor terminal model.Synced.Pending

    /// Every act waiting on a verdict, in a stable total order (Plan 15, stage 3c): by
    /// subject, then by the subject's own order, then by id. What the chat column shows,
    /// and it deliberately includes the TERMINAL ones — approving a command the agent is
    /// about to run is the same act as reading what it is about to say, so it belongs where
    /// the reading happens rather than only inside a panel you may not have open.
    let pendingActs (model: ClientModel) : PendingAct list =
        model.Synced.Pending
        |> Map.toList
        |> List.map snd
        |> List.sortBy (fun act -> TerminalId.value act.Terminal, act.Order, QueueId.value act.QueueId)

    /// The composer slots published in a terminal, in stable order — every peer mid-command
    /// there, the local peer included.
    let terminalDrafts (terminal: TerminalId) (model: ClientModel) : PeerId list =
        model.Synced.TerminalDrafts
        |> Map.toList
        |> List.filter (fun ((t, _), _) -> t = terminal)
        |> List.map (fun ((_, author), _) -> author)
        |> List.sortBy PeerId.value

    /// Whether a queued command is held because a peer is typing in its terminal (Plan 13,
    /// stage 2e). Named because it resolves when a person finishes a task, and a queue that
    /// said only *pending* would leave that looking like a stall.
    let awaitsTerminal (entry: PendingAct) (model: ClientModel) : bool =
        Projection.tryFind entry.Terminal model.Terminals
        |> Option.bind (fun view -> view.Lease)
        |> Option.isSome

    /// Whether a queued command is held because its terminal's shell stopped emitting marks
    /// (Plan 13, stage 2f) rather than because a peer is typing there. Apart again for the
    /// same reason: they resolve differently — one when a person finishes, this one when
    /// somebody repairs the terminal.
    let awaitsIntegration (entry: PendingAct) (model: ClientModel) : bool =
        Projection.tryFind entry.Terminal model.Terminals
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
    /// Every connected credential that needs a person to sign in again, as
    /// `(provider, reason)` — Claude's before GitHub's, and each panel's shared scope before
    /// its session-only one, so the order on screen never depends on a map's iteration.
    ///
    /// A DERIVATION rather than a field, so the surfaces that report this — the panel rows,
    /// the roster, the prompt over the timeline — cannot disagree about whether anything is
    /// wrong. The view stays a total function of the model, and the cheap tier can ask this
    /// question without rendering anything.
    let signInRequired (model: ClientModel) : (string * string) list =
        let needing (provider: string) (credential: ConnectionView option) =
            match credential with
            | Some view -> view.SignInRequired |> Option.map (fun reason -> provider, reason)
            | None -> None
        [ needing "claude" model.Claude.Status.MineCredential
          needing "claude" model.Claude.Status.SessionCredential
          needing "github" model.GitHub.Status.MineCredential
          needing "github" model.GitHub.Status.SessionCredential ]
        |> List.choose id

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
            model.Synced.Pending |> Map.tryFind queueId |> Option.map (fun act -> act.Terminal)
        | Title | DraftBody _ | QueueBody _ -> None

    /// Who is in a given terminal right now — whether writing a new command or editing a
    /// queued one, because from the strip they are the same fact: someone is in there.
    let peersInTerminal (terminal: TerminalId) (model: ClientModel) : (PeerId * string) list =
        presentPeers model
        |> List.filter (fun (_, _, field) -> terminalOfFocus field model = Some terminal)
        |> List.map (fun (peer, name, _) -> peer, name)

    /// A peer's display name: your own connection's, else the roster's, else the peer's own
    /// live presence, else the raw id (an id is a last resort, not a label — `PEER-129755065`
    /// is not a person).
    ///
    /// YOUR OWN name comes first and from your own connection, because those two can disagree
    /// and only one of them is what the rest of the screen is showing. The roster is folded
    /// from the durable `PeerJoined` log, while `Peer.DisplayName` is what THIS connection was
    /// assigned — so a peer that rejoins under a new name has an old one still in the log, and
    /// a client reading the roster for itself would put a name in the chat that the sidebar's
    /// "you" row contradicts. Which is the exact defect resolving names was meant to end.
    let nameOf (peer: PeerId) (model: ClientModel) : string =
        if peer = model.Peer.PeerId && model.Peer.DisplayName <> "" then model.Peer.DisplayName
        else
            match Map.tryFind peer model.Peers with
            | Some name -> name
            | None ->
                match Map.tryFind peer model.Presence with
                | Some presence when presence.DisplayName <> "" -> presence.DisplayName
                | _ -> PeerId.value peer

    /// What a stroke on the rail is called, for a reader who cannot see where it points.
    ///
    /// An act note's headline is already a short sentence and arrives whole. A message is not:
    /// it is somebody's markdown, and a rail whose every stroke announced a paragraph would be
    /// a rail nobody could tab through. So a message is named by its FIRST line, cut at a
    /// length a person can hear in one breath — which is also how a person recognises their
    /// own message in a list.
    ///
    /// An ellipsis marks the cut, because a sentence that simply stops reads as a sentence
    /// that was garbled rather than one that was shortened.
    let landmarkLabel (item: ConversationItem) : string =
        let firstLine =
            match item.Body.IndexOf '\n' with
            | -1 -> item.Body.Trim ()
            | n -> (item.Body.Substring (0, n)).Trim ()
        if firstLine.Length <= 72 then firstLine
        else (firstLine.Substring (0, 71)).TrimEnd () + "…"

    /// The marked items, oldest first — the conversation's own order, which is the order the
    /// strokes are rendered in and the order the rail reads them back in.
    ///
    /// No position here, and that is a deliberate subtraction rather than an omission. The
    /// rail used to space its strokes by RANK on a log scale, which made it an index of the
    /// marks: readable in isolation, and beside a column of per-item controls sharing the same
    /// margin, two rows of near-identical dashes with nothing saying which was which. A stroke
    /// now stands where its MESSAGE stands, which no model can know — a pixel is a measurement
    /// of a laid-out page, and this list is the same on a phone and a desk. `Rail.place` is the
    /// arithmetic and `RailSync` is what measures its inputs.
    let landmarks (model: ClientModel) : ConversationItem list =
        Landmarks.over model.Synced.Landmarks model.Conversation.Items

    /// What this session's pull-request watches currently stand at, read off the
    /// `pull_requests` query — the only shape a browser has them in, since the query stream
    /// is what delivers them.
    ///
    /// Here rather than at either surface because there are now two: the header strip and
    /// the tab title. Two readers of one set of rows that parsed them separately would be
    /// two readers that can disagree about the same session in the same window — which is
    /// the fault `PrStatus` exists to prevent, one layer up from where it prevents it.
    ///
    /// Every standing, live or not. The callers want different halves — a summary counts
    /// only what is still owed, the tab title's tick is specifically about one that is NOT
    /// owed any more — and `PrStatus.live` is how each says which.
    let prStandings (model: ClientModel) : (string * string) list =
        let cell (row: (string * QueryCell) list) (key: string) =
            row |> List.tryFind (fun (k, _) -> k = key) |> Option.map snd
        let text row key =
            match cell row key with
            | Some (CellStatus (said, _)) -> Some said
            | Some (CellText said) -> Some said
            | _ -> None
        // The TONE and never the sentence: a health line's words are the provider's and
        // change with it, while the tone is this repository's own verdict vocabulary.
        let readable row =
            match cell row PrStatus.Columns.status with
            | Some (CellStatus (_, ToneBad)) -> false
            | _ -> true
        match model.Queries.Values |> Map.tryFind PrStatus.Columns.query with
        | Some (RowsOf rows) ->
            rows
            |> List.choose (fun row ->
                match text row PrStatus.Columns.pr with
                | Some named ->
                    PrStatus.standing (PrStatus.labelOf named) (text row PrStatus.Columns.state) (readable row)
                | None -> None)
        | _ -> []

    /// What a pull-request watch puts in front of the tab name, for somebody who is not
    /// looking at this tab at all — the only surface that reaches them there.
    ///
    /// Two signals and deliberately not three. A red suite is the AGENT's to fix and it is
    /// already fixing it, so interrupting a person with one trains them to ignore the mark
    /// by the time it means something. What reaches them is the two facts nobody else is
    /// acting on:
    ///
    /// - `⚠` — a live watch stalled, or one nobody can read any more. Both mean the same
    ///   thing to the person waiting: this pull request has stopped being on its way in and
    ///   no machine is going to notice.
    /// - `✓` — something merged and the watch is still there. Stop waiting; the watch going
    ///   away is what clears it.
    ///
    /// The warning wins, because a tab can only say one thing and the one that needs a
    /// person is the one that has stopped moving.
    let private tabSignal (model: ClientModel) : string =
        let standings = prStandings model
        // No `live` filter on these two: a watch that stalled or cannot be read is by
        // construction still owed, so asking would be asking a question with one answer.
        let stopped =
            standings |> List.exists (fun (_, word) -> word = "stalled" || word = PrStatus.unreachable)
        if stopped then "⚠ "
        elif standings |> List.exists (fun (_, word) -> word = "merged") then "✓ "
        else ""

    /// What the browser tab is called: the session's own title, falling back to its id, and
    /// always saying which product it belongs to. Every session shell served the constant
    /// "Yession", so a person with three of them open had three identical tabs and no way to
    /// tell which was which without visiting each.
    ///
    /// A pure projection rather than something the composition root assembles, because every
    /// part of it is a decision — what wins, what a blank title falls back to, how the two
    /// are joined — and a decision inside `setState` is one no cheap test can reach. The
    /// browser only applies the answer.
    ///
    /// The id fallback is the honest one here, unlike `nameOf` where an id is a last resort:
    /// this is the tab for a session, and its id is what the header shows beside the title
    /// until somebody names it.
    let tabTitle (model: ClientModel) : string =
        let named = (Ylmish.Text.toString model.Synced.Title).Trim ()
        let subject =
            if named <> "" then Some named
            else model.Session |> Option.map SessionId.value
        let signal = tabSignal model
        match subject with
        | Some subject -> sprintf "%s%s — yession" signal subject
        | None -> signal + "yession"

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
        // One fold, reached by two messages. The events are the same events and the
        // projection is the same projection; what differs is what arriving PROVED, and that
        // is `Feed`, decided below rather than in here.
        | EventsPageMsg page | LocalHistoryMsg page ->
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
                |> List.fold (fun proj e -> Projection.applyEvent proj e.Event) model.Terminals
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
            // The pins move with the terminals (Plan 20, stage 1), in the step that folds
            // the events rather than at render: a terminal I opened is one I asked for and
            // is therefore in my hands, and a terminal that has closed has nothing left to
            // keep. Doing it here means the strip is simply the pins — one rule, one place,
            // and no filter at render free to disagree with it.
            //
            // "I opened it" is `PeerRef` against this client's own peer, which is how every
            // other surface here decides whose something is (the lease bar, the queue's
            // authorship). A session whose actors are Manager-verified users attributes them
            // as `UserRef`, and this does not pin those — stated rather than papered over,
            // because inventing a second identity rule for one convenience is how two
            // answers to "is this mine" start disagreeing.
            let pins =
                let mine = ActorRef.PeerRef model.Peer.PeerId
                let opened =
                    freshEvents
                    |> List.choose (fun e ->
                        match e.Event with
                        | SessionEvent.TerminalOpened t when t.OpenedBy = mine -> Some (TerminalTab t.TerminalId)
                        | _ -> None)
                (model.Pins @ opened)
                |> List.filter (fun tab -> PaneTab.isLive terminals tab)
                |> List.distinctBy PaneTab.key
            let latestKnown = EventOffset.maxOption model.EventConsumer.LatestKnownOffset highWater
            { model with
                Conversation = conversation
                Approvals = RepoApprovals.apply model.Approvals (freshEvents |> List.map (fun e -> e.Event))
                Timeline = timeline
                Agent = agent
                Environment = environment
                Terminals = terminals
                Pins = pins
                Peers = peers
                EventConsumer =
                    { LastProcessedOffset = highWater
                      LatestKnownOffset = latestKnown
                      IsCatchingUp = isBehind highWater latestKnown
                      // A catch-up that has finished was never slow, whatever the timer
                      // was about to say.
                      CatchUpIsSlow =
                        isBehind highWater latestKnown && model.EventConsumer.CatchUpIsSlow
                      // A page off the NETWORK is proof the feed works, so recovery from a
                      // stall needs no separate signal. A page off the local store proves
                      // only that this client kept it — the feed is whatever it already
                      // was, which offline is exactly the truth the strip is showing.
                      Feed =
                        match msg with
                        | EventsPageMsg _ -> FeedLive
                        | _ -> model.EventConsumer.Feed
                      // A page off the NETWORK resumes at the cursor and runs unbroken from
                      // it, so whatever the store was missing before it is being filled —
                      // what is left to arrive is ordinary catch-up, which `IsCatchingUp`
                      // already says. A page off the local store is what found the hole in
                      // the first place and cannot have repaired it.
                      MissingBefore =
                        match msg with
                        | EventsPageMsg _ -> None
                        | _ -> model.EventConsumer.MissingBefore } }
        | LocalHistoryGapMsg resumesAt ->
            { model with EventConsumer = { model.EventConsumer with MissingBefore = Some resumesAt } }
        | HistoryReadMsg -> { model with HistoryRead = true }
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
            // imperatively (`Client.connect`'s SendDraft), not in the model.
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
        | GitHubStatusMsg status ->
            // A connected credential ends an in-flight wait (the poll completed, or the
            // grant landed from another tab); otherwise a mere probe leaves the flow be.
            let connected = status.SessionCredential.IsSome || status.MineCredential.IsSome
            let flow =
                match model.GitHub.Flow, connected with
                | (GitHubAwaitingApproval _ | GitHubBusy), true -> GitHubIdle
                | flow, _ -> flow
            { model with GitHub = { Status = status; Flow = flow } }
        | GitHubFlowMsg flow ->
            { model with GitHub = { model.GitHub with Flow = flow } }
        | QueryFrameMsg (QueriesDeclared defs) ->
            // The declarations REPLACE rather than merge: a reconnect re-declares, and a
            // query the session has dropped must leave the surface with it.
            { model with Queries = { model.Queries with Declared = defs } }
        | QueryFrameMsg (QueryValued (name, value)) ->
            { model with
                Queries =
                    { model.Queries with Values = Map.add (QueryName.value name) value model.Queries.Values } }
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
        | TerminalScreenMsg (terminal, screen) ->
            { model with TerminalScreens = Map.add terminal screen model.TerminalScreens }
        | TerminalViewportMsg (terminal, size) ->
            // A box that is not in the document yet, or has just left it, measures as zero
            // cells — and a zero-column terminal is not a narrow one, it is a broken one. The
            // refusal is here, with the state, so that no route to it can put one in front of
            // a command: the reducer is the only way in, and it says no.
            if Size.isValid size then
                { model with TerminalViewports = Map.add terminal size model.TerminalViewports }
            else model
        | ShowInPaneMsg mode ->
            // The WHOLE next face, stated by every way in. Nothing here clears a subset and
            // hopes the rest was already right: the list cannot survive a choice that
            // replaces it, and a pin or a start hint cannot outlive the mode that carried it.
            { model with Pane = Some (OnTab mode); TerminalsOpen = true }
        | RewindTerminalMsg terminal ->
            // The length is pinned NOW rather than followed. A recording that grew under a
            // reader would move the scrub bar out from under them, which is the one thing
            // rewinding exists to avoid.
            let length = (model.TerminalFeeds |> Map.tryFind terminal |> Option.defaultValue TerminalFeed.empty).KnownLength
            // Rewinding IS asking to watch, said the same way as every other way in, with the
            // pin as the extra fact rather than a second kind of watching. What that buys: a
            // terminal that CLOSES under a rewound reader keeps playing rather than dropping
            // them back into its blocks, because the pin was the only part of their state
            // that died with the live edge (`rewoundTo` resolves it against `IsOpen`).
            //
            // What a pin gives up is following the tail while behind it: the recording under the
            // reader is fixed until they catch up. The alternative was a custom player source
            // driving history, tail and seek itself — the stock player's file source is static
            // and its live sources do not seek backwards — and it was not needed, because the
            // client already holds every record and mounting the ordinary whole-terminal cast
            // over `[0, pin)` replays through the same player a finished terminal uses. If
            // following-while-behind is ever wanted, that custom source is where it goes; it is
            // not something this reducer can grow.
            { model with Pane = Some (OnTab (WatchingBehind (terminal, length))); TerminalsOpen = true }
        | TogglePinMsg tab ->
            let key = PaneTab.key tab
            // Unpinning leaves what is SHOWN alone: it stays on screen, now as the preview.
            // Pressing unpin should say "stop keeping this", never "take it away from me
            // while I am looking at it".
            if isPinned tab model then
                { model with Pins = model.Pins |> List.filter (fun pinned -> PaneTab.key pinned <> key) }
            else { model with Pins = model.Pins @ [ tab ] }
        | ToggleTerminalsMsg ->
            { model with TerminalsOpen = not model.TerminalsOpen }
        | ToggleItemMenuMsg messageId ->
            // Opening one is writing the field, so opening a second shuts the first without
            // anybody arranging it. That is the whole reason this is one slot and not a set.
            let next = if model.ItemMenu = Some messageId then None else Some messageId
            { model with ItemMenu = next }
        | CloseItemMenuMsg -> { model with ItemMenu = None }
        | CopiedMsg copied -> { model with Copied = copied }
        | ToggleTerminalListMsg ->
            // Going to the list KEEPS the read it covers, so coming back resumes it — a
            // rewind included, which is the one thing the boolean did right. The column comes
            // with it: reaching the list from a shut column is exactly the case where a
            // person is looking for a terminal they cannot see.
            let next =
                match model.Pane with
                | Some (OnList resume) -> resume |> Option.map OnTab
                | Some (OnTab mode) -> Some (OnList (Some mode))
                | None -> Some (OnList None)
            { model with Pane = next; TerminalsOpen = true }
        | EnsureTerminalDraftMsg (terminal, author, queueId) ->
            // Typing in a terminal pins it, for the person typing (Plan 20, stage 1). The
            // rule that makes the agent's terminals safe to leave unpinned: watching one and
            // joining one are a keystroke apart, and the moment you take a seat at it, it is
            // in your strip. Applied before the idempotence check below, because a slot that
            // already exists is somebody coming BACK to a terminal — which is the same claim.
            let model =
                if author = model.Peer.PeerId && not (isPinned (TerminalTab terminal) model) then
                    { model with Pins = model.Pins @ [ TerminalTab terminal ] }
                else model
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
            // the same transaction (`Client.connect`'s SendTerminalDraft) — shared types
            // cannot be re-parented.
            match Map.tryFind (terminal, author) model.Synced.TerminalDrafts with
            | Some draft when not (Map.containsKey draft.QueueId model.Synced.Pending) ->
                let entry =
                    { QueueId = draft.QueueId
                      Terminal = terminal
                      Order = TerminalQueueOrder.nextFor terminal model.Synced.Pending
                      // The author is the PEER who wrote it. Attribution to a verified user
                      // happens at the durable append, where the Session Process knows the
                      // binding — the doc only ever knows connections.
                      //
                      // `ofAuthor`, so it runs as its own author: a terminal command is a
                      // shell line in a sandbox, not a call against somebody's credential —
                      // and a person's act cannot accidentally carry one.
                      Authority = Authority.ofAuthor (PeerRef author)
                      // A person's composer never waits on a command, so there is nothing
                      // for a background flag to spare them (Plan 20, stage 2).
                      Background = false
                      // The width of the box this author is looking at, so the output is laid
                      // out for the screen it will be read on. Absent when nothing has been
                      // measured — a terminals column that has never been opened — which is a
                      // claim of nothing rather than a guess at eighty.
                      Size = Map.tryFind terminal model.TerminalViewports }
                model
                |> withSynced
                    { model.Synced with
                        TerminalDrafts = Map.remove (terminal, author) model.Synced.TerminalDrafts
                        Pending = Map.add draft.QueueId entry model.Synced.Pending }
            | _ -> model
        | DiscardTerminalDraftMsg (terminal, author) ->
            model
            |> withSynced
                { model.Synced with TerminalDrafts = Map.remove (terminal, author) model.Synced.TerminalDrafts }
        | DeletePendingMsg queueId ->
            model |> withSynced { model.Synced with Pending = Map.remove queueId model.Synced.Pending }
        | ReorderPendingMsg (queueId, order) ->
            match Map.tryFind queueId model.Synced.Pending with
            | Some entry ->
                model
                |> withSynced
                    { model.Synced with
                        Pending = Map.add queueId { entry with Order = order } model.Synced.Pending }
            | None -> model
        | ModelCatalogueMsg catalogue -> { model with Models = catalogue }
        | SetModelMsg choice -> model |> withSynced { model.Synced with Model = choice }
        // An id this client's window does not hold is a page boundary, not a bug — and
        // there is nothing to toggle, because what the mark would default to is on the item.
        | ToggleLandmarkMsg messageId ->
            // The menu shuts either way. It is the surface the mark is chosen from, and one
            // left standing over an act it has already performed is a menu asking to be
            // pressed again — including when the item was not found, where leaving it open
            // would be a menu offering something that cannot happen.
            let model = { model with ItemMenu = None }
            match model.Conversation.Items |> List.tryFind (fun item -> item.MessageId = messageId) with
            | Some item ->
                model
                |> withSynced
                    { model.Synced with Landmarks = Landmarks.toggle item model.Synced.Landmarks }
            | None -> model
