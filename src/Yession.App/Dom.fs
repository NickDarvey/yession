namespace Yession.App

/// The DOM contract: every `data-*` hook and every observable text/value token the views
/// emit and the tests assert on, defined once. Views (`View`, `ManagerUi`) compose these
/// into markup; the E2E suites and the browser shell's delegation bind to the same names —
/// so a hook is renamed in exactly one place. No behaviour lives here, only the shared
/// vocabulary (docs/design.md §1: the markup is a total function of the model, and the
/// hooks are its stable surface).
module Dom =

    /// `name="value"` — an attribute with a value (e.g. `data-send-draft="draft-ui"`).
    let attr (name: string) (value: string) : string = name + "=\"" + value + "\""

    /// `hook>text<` — a hook sitting immediately before its text content, the shape a
    /// checklist assertion looks for (e.g. `data-catch-up>Catching up<`).
    let hookText (hook: string) (text: string) : string = hook + ">" + text + "<"

    /// The client shell mount element id (`<main id="app">`; the browser mounts Lit here).
    let appId = "app"

    /// The `<meta name>` carrying the serving session id, so the browser can key its local
    /// doc store by session before (and without) any connection.
    let sessionMetaName = "yession-session"

    /// The `<meta name>` carrying the Manager's public origin (Plan 11), so a client whose
    /// session has stopped knows where to ask for it back. Absent — never blank — when
    /// this session has no Manager.
    let managerMetaName = "yession-manager"

    /// The `<meta name>` marking a deployment whose sessions do NOT keep their address
    /// across launches, so browser storage does not survive one. Emitted only in that case:
    /// absence is the good deployment, exactly as with the Manager origin above.
    let ephemeralStorageMetaName = "yession-ephemeral-storage"

    /// The attribute marking the replay player's deferred stylesheet in the head, so the
    /// browser half can find it and turn it on (`Style.deferredHeadTags`, `Replay.mount`).
    /// A hook rather than a selector spelled twice, for the same reason every other one is.
    let playerStylesheetHook = "data-player-css"

    /// `data-*` hooks on the session client shell (`View`) and its browser delegation.
    module Hooks =
        // Header — the collaborative session title and its secondary id.
        let sessionTitle = "data-session-title"
        let sessionId = "data-session-id"
        let cursorPeer = "data-cursor-peer"
        // Sidebar — identity & live sync state.
        /// The transport's exact state token, ALWAYS present and never words on the screen:
        /// a healthy client says nothing about being healthy, so this is the only thing a
        /// test (or the browser suite, waiting for a session to come up) can read it off.
        let connection = "data-connection"
        let displayName = "data-display-name"
        let catchUp = "data-catch-up"
        /// The durable event feed's health (sidebar), carrying a `Text.feed*` token.
        let feed = "data-feed"
        /// A mount of the connection report, carrying the token of whichever leg is down
        /// (`Text.degraded*` or `Text.feed*`); absent entirely when everything is healthy.
        ///
        /// TWO elements wear it — the nav column's, and the bar for where the column cannot be
        /// seen — and they are complementary, so a person only ever sees one. That is a
        /// VISIBILITY rule, which no markup test can settle: both mounts are in the document
        /// at once by design. The browser suite counts the visible ones.
        let degraded = "data-degraded"
        let lastProcessedOffset = "data-last-processed-offset"
        let latestKnownOffset = "data-latest-known-offset"
        /// A roster row for a peer that is here NOW, valued by peer id, and the where-they-are
        /// slot on it — valued by a `Text.at*` field token, with the words for people beside
        /// it. Presence is what makes a collaborator visible from outside the surface they
        /// are in; without it, someone typing a command in another terminal is invisible.
        let peerPresence = "data-peer-presence"
        let peerAt = "data-peer-at"
        let environment = "data-environment"
        // Conversation timeline.
        let conversation = "data-conversation"
        let messageId = "data-message-id"
        let messageAuthor = "data-message-author"
        let messageStatus = "data-message-status"
        let messageBody = "data-message-body"
        /// The reply ref a detached agent reply wears — valued by the id of the message it
        /// answers, so a test reads WHICH message it points at, not merely that it drew one.
        let replyRef = "data-reply-ref"
        /// The landmark rail beside the timeline, and one stroke on it valued by the message
        /// it points at. Beside them, the per-item control that puts a mark there — valued by
        /// the same id, and carrying whether the mark is currently on, so a test can read the
        /// state without reading a class.
        let landmarkRail = "data-landmark-rail"
        let landmark = "data-landmark"
        /// The per-item actions control, valued by the message it acts on, and the menu it
        /// opens. `data-item-marked` rides the BOOKMARK entry rather than the control: what
        /// a test wants to read is which way the entry will go, and that is a property of
        /// the entry.
        let itemActions = "data-item-actions"
        let itemMenu = "data-item-menu"
        let itemBookmark = "data-item-bookmark"
        let itemMarked = "data-item-marked"
        // Agent activity strip.
        let agentStream = "data-agent-stream"
        let agentTurn = "data-agent-turn"
        let interruptTurn = "data-interrupt-turn"
        // Message queue.
        let messageQueue = "data-message-queue"
        let queueId = "data-queue-id"
        let queueAuthor = "data-queue-author"
        let queueOrder = "data-queue-order"
        let queueInput = "data-queue-input"
        let queueUp = "data-queue-up"
        let queueDown = "data-queue-down"
        let queueDelete = "data-queue-delete"
        // Draft composer.
        let draftEditor = "data-draft-editor"
        let draftId = "data-draft-id"
        let draftAuthor = "data-draft-author"
        let draftInput = "data-draft-input"
        let sendDraft = "data-send-draft"
        let discardDraft = "data-discard-draft"
        /// A collapsed draft's summary row, and the button that opens it.
        let draftSummary = "data-draft-summary"
        let expandDraft = "data-draft-expand"
        /// Starts the local peer's own draft, collapsing whoever's was open.
        let newDraft = "data-draft-new"
        /// One per live caret in a draft, so a test can assert who is shown editing it.
        let draftEditor' = "data-draft-editor-peer"
        // Chrome: the sidebar column's collapse/reveal. Its VALUE names the direction —
        // `show` (the header's reopen chevron) or `hide` (the nav head's chevron, and the
        // mobile scrim) — so the browser can hand focus to whichever control replaces the
        // one that was just pressed.
        let navToggle = "data-nav-toggle"
        // Settings, the column's other face: the toggle, the pane, the Claude section, and the
        // agent-presence surfaces (the membership row, and the header's stand-in while the
        // column is off screen). The toggle's VALUE names the control, not just the direction:
        // `open`/`close` are the column's own pivots — exactly one of each in the document, so
        // focus can be handed between them and a test can click one without ambiguity — while
        // `prompt` is any call to action that happens to lead there.
        let settingsToggle = "data-settings-toggle"
        let settingsPanel = "data-settings-panel"
        let claudePanel = "data-claude-panel"
        /// A connected credential that needs signing in again, valued by the scope it is
        /// held under ("mine" | "session") — the same value its `*-connected` row carries,
        /// so a test reads the fault off the same scope it sees the credential on.
        let claudeSignInRequired = "data-claude-signin-required"
        let githubSignInRequired = "data-github-signin-required"
        /// The device code a person has to type into github.com, and the control that puts
        /// it on the clipboard. The code's hook is also the KEY its copy is remembered
        /// under (`ClientModel.Copied`): one name for the box, whichever half is asking.
        let githubUserCode = "data-github-user-code"
        let githubCopyCode = "data-github-copy-code"
        /// The one prompt over the timeline (`Text.signInAgain`), valued by whichever
        /// provider needs it, and the button in it that opens the settings face. Absent
        /// entirely when nothing needs signing in — a surface that is only ever there when
        /// something is wrong cannot be mistaken for chrome.
        let signInRequired = "data-signin-required"
        let signInAgain = "data-signin-again"
        /// The disclosure a notice folds its mechanism into, valued by which notice it
        /// belongs to. ONE hook across every one of them, because it is one move: the
        /// consequence is on the surface, the reason for it is a keypress away.
        let detail = "data-detail"
        /// The model picker (settings): the section, and the control itself. The control's
        /// VALUE is the session's current choice — a model id, or `default` where the
        /// provider is left to choose — so a test reads the state off the same attribute it
        /// clicks.
        let modelPanel = "data-model-panel"
        let modelSelect = "data-model-select"
        let agentPresence = "data-agent-presence"
        let noAgent = "data-no-agent"
        let noAgentConnect = "data-no-agent-connect"
        /// One per repo waiting on somebody. Counted rather than read: what the prompt SAYS
        /// is a design, that there is exactly one per waiting repo is a promise.
        let approvalPrompt = "data-repo-approval"
        let approvalAction = "data-repo-approve"
        // The reconnect offer (Plan 11): shown in place of the connection status word when
        // the session has stopped and this deployment can bring it back.
        let sessionGone = "data-session-gone"
        let sessionReopen = "data-session-reopen"
        // Terminals (Plan 13): the column, its strip of open terminals, the blocks that have
        // run, and the composer that queues the next command. The composer's hooks mirror the
        // message composer's, because the interaction is the same one.
        let terminalPanel = "data-terminal-panel"
        let terminalToggle = "data-terminal-toggle"
        let terminalTab = "data-terminal-tab"
        /// One per peer whose caret is in THAT terminal, on its tab — the strip's share of
        /// the same presence the roster reports.
        let terminalTabPeer = "data-terminal-tab-peer"
        let terminalNew = "data-terminal-new"
        let terminalClose = "data-terminal-close"
        let terminalId = "data-terminal-id"
        /// The scrolling block history — the surface that stays pinned to its newest line.
        let terminalScrollback = "data-terminal-scrollback"
        let terminalBlock = "data-terminal-block"
        let terminalBlockStatus = "data-terminal-block-status"
        let terminalOutput = "data-terminal-output"
        let terminalTruncated = "data-terminal-truncated"
        let terminalInput = "data-terminal-input"
        let terminalSend = "data-terminal-send"
        let terminalDiscard = "data-terminal-discard"
        let terminalDraftAuthor = "data-terminal-draft-author"
        /// One per live caret in a terminal composer slot.
        let terminalDraftEditor = "data-terminal-draft-editor"
        let terminalQueued = "data-terminal-queued"
        let terminalQueuedStatus = "data-terminal-queued-status"
        let terminalQueueDelete = "data-terminal-queue-delete"
        /// The lease bar shown instead of the composer in live mode (Plan 13, stage 2e); its
        /// value is the holder's label, so a test can assert WHO without scraping prose.
        let terminalLease = "data-terminal-lease"
        /// Enter live mode, or steal it. One control, because it is one act.
        let terminalTake = "data-terminal-take"
        let terminalRelease = "data-terminal-release"
        /// The banner shown when a terminal's shell stopped emitting marks (Plan 13, stage
        /// 2f), and the control that types the instrumentation in again.
        let terminalLost = "data-terminal-lost"
        let terminalRearm = "data-terminal-rearm"
        /// Ask the provider for a closed terminal's stream again (Plan 19, step 4).
        let terminalReattach = "data-terminal-reattach"
        /// The replay of a CLOSED terminal (Plan 13, stage 3e): the mount the player attaches
        /// to, the tab that reaches a closed terminal at all, and the banner shown instead
        /// when retention has deleted the recording.
        let terminalClosedTab = "data-terminal-closed-tab"
        let terminalReplayGone = "data-terminal-replay-gone"
        /// Terminal work in the CHAT (Plan 14, stage 1). A chip per block, anchored where the
        /// command started; an item per lease stretch, anchored where it concluded. Both are
        /// buttons — tapping one opens the terminal read-only — so both are keyboard-operable
        /// by construction rather than by a handler bolted onto a div.
        ///
        /// The block chip's VALUE is the block id and the stretch item's is its terminal plus
        /// the transcript line it began at, which is the only handle a stretch has: leases are
        /// not minted with ids, and one terminal can have many stretches.
        let chatBlock = "data-chat-block"
        let chatBlockStatus = "data-chat-block-status"
        let chatStretch = "data-chat-stretch"
        let chatStretchEnd = "data-chat-stretch-end"
        /// A turn's tool calls in the CHAT (Plan 16, part C). A `<details>` per RUN of
        /// consecutive calls from one turn — expandable by construction, so it is keyboard-
        /// operable without a handler — carrying the turn id; and one row inside it per
        /// call, carrying the Process-minted `ToolUseId` that a deep link will address.
        ///
        /// A call that became a block is not here at all: the block chip beside it already
        /// says who ran what and how it went.
        let chatToolRun = "data-chat-tool-run"
        let chatTool = "data-chat-tool"
        /// One agent burst in the CHAT (Plan 20, stage 4): a `<details>` per RUN of
        /// consecutive commands from one turn, carrying the turn id. Its lines are ordinary
        /// block chips, addressable by `chatBlock` exactly as an ungrouped one is — a chip
        /// that moved inside a card is still the same chip, and a test that has to know
        /// whether it was grouped is a test of the grouping, not of the chip.
        let chatTaskCard = "data-chat-task-card"
        /// The call's outcome, in the SAME tokens a block's status uses (`running` / `ok` /
        /// `failed`) rather than a parallel vocabulary meaning the same three things.
        let chatToolStatus = "data-chat-tool-status"
        /// The pane's tab strip (Plan 14, stage 2). One hook for every tab whatever it shows
        /// — a terminal, a block's read-only view, a stretch's replay — because they are one
        /// tablist and a test asserting keyboard order should not have to know which is which.
        /// Its value is `PaneTab.key`.
        let paneTab = "data-pane-tab"
        /// Whether a tab is KEPT — `"true"` or `"false"`, and absent on a tab that cannot be
        /// pinned at all (a closed terminal's preview). State rather than a control: the pin
        /// stopped being a second button beside every tab and became a mark on the one that
        /// has it, toggled by activating the tab you are already on.
        ///
        /// Releasing a tab never ends anything, which is the point the pin inherited from the
        /// close control it replaced: the strip cannot destroy. Killing a terminal is
        /// `terminalClose`, on its row in the list.
        let paneTabPinned = "data-pane-tab-pinned"
        /// The pane's body, carrying the key of whatever it is showing.
        let panePanel = "data-pane-panel"
        /// A block's read-only view: its command line and everything it printed.
        let paneBlock = "data-pane-block"
        /// Where a player mounts (Plan 13, stage 3e; Plan 14, stage 4). ONE hook for all
        /// three kinds of recording — a whole terminal, a block's range, a stretch's — with
        /// the tab's key as its value, because they differ in what they play rather than in
        /// how they are mounted. Two hooks would be two mount paths to keep correct.
        let paneReplay = "data-pane-replay"
        /// A stretch's facts, above its recording.
        let paneStretch = "data-pane-stretch"
        /// From a block, to that command in its terminal's own history (Plan 25, stage 3):
        /// the same text, scrolled to it and marking it. The reader's other question — not
        /// "what did this print", which the block already answers, but "what was going on
        /// around it" — and text answers it, where a player used to.
        let paneShowInTerminal = "data-pane-show-in-terminal"
        /// A block's way between its two reads: the output it printed, and the recording of
        /// it printing. One control saying whichever the reader is not looking at, so the
        /// press that swaps the body keeps the focus it was pressed with. Its VALUE is the
        /// face it will show — `watch` / `output` — the same contract the list toggle keeps.
        let paneWatch = "data-pane-watch"
        /// The live screen of a terminal in live mode (Plan 14, stage 6). Its value is the
        /// terminal's id; the holder's copy is the one that takes keystrokes, and every other
        /// peer's is the same screen read-only.
        let terminalScreen = "data-terminal-screen"
        /// A terminal's one way between its two reads (Plan 14, stage 7; Plan 25, stage 3):
        /// its text — the live screen, or the blocks that ran — and its recording.
        ///
        /// ONE control, in one slot, whatever the terminal is doing. It was four: two ways in
        /// at the top of the scrollback (`↑ replay from the start`, `↑ play the recording`)
        /// and two ways out floating over it (`Back to blocks`, `Jump to live`), each
        /// removing another from the document and each needing focus handed on after it.
        /// A toggle that relabels in place is the same act with none of that, and the words
        /// are the reader's: on a live terminal, watching means going behind its edge and the
        /// way back is `Live`.
        ///
        /// Its VALUE is the face it will show — `watch` / `output` / `live` — so a test can
        /// read the state off the attribute it clicks, and the browser can hand focus to
        /// whichever control replaced the one it just lost.
        let terminalWatch = "data-terminal-watch"
        /// How far behind live the rewound reader is, growing as the terminal keeps
        /// printing under them.
        let terminalBehind = "data-terminal-behind"
        /// The terminal LIST (Plan 20, stage 0): every terminal the session has ever had,
        /// and every verb one of them affords. The toggle carries `list`/`pane` — the face
        /// it will show, so the browser can hand focus to whichever control replaces the one
        /// just pressed, exactly as the nav and settings toggles do.
        let terminalList = "data-terminal-list"
        let terminalListToggle = "data-terminal-list-toggle"
        /// One row, carrying its terminal's id — and the control that shows that terminal,
        /// so a row is keyboard-operable by construction rather than by a handler on a div.
        let terminalListRow = "data-terminal-list-row"
        /// The rewind, on a row. Its own hook because the pane keeps a watch toggle of its own
        /// (`terminalWatch`) for the terminal it is showing, and the two are different
        /// controls in different places — unlike the kill and the attach-again, which the
        /// list is now the ONLY home of (Plan 20, stage 1) and which therefore keep the names
        /// they have always had: `terminalClose`, `terminalReattach`.
        ///
        /// Every row verb is rendered ONLY where `Affordances` says it applies, so a
        /// test asserting one is absent is asserting the fold, not a template's mood.
        let terminalListRewind = "data-terminal-list-rewind"
        /// A closed row whose recording the per-terminal cap ate. The stated gap, where a
        /// play affordance would otherwise be — an audit trail's hole is said, never left to
        /// look like a terminal that printed nothing.
        let terminalListGone = "data-terminal-list-gone"

    /// Observable text/value tokens the session view emits (labels and status words that
    /// tests assert exactly — never free-text message bodies, which are model data).
    module Text =
        // Connection state.
        let disconnected = "Disconnected"
        let connecting = "Connecting"
        let connected = "Connected"
        let reconnecting = "Reconnecting"
        // Catch-up. Said in ONE place — the header — because "everything is fine" is the
        // least actionable thing a screen can carry and it used to be on screen twice at
        // once (the header's status and the sidebar's sync row).
        let catchingUp = "Catching up"
        let upToDate = "Up to date"

        // The landmark rail, and the menu a mark is put on from.
        //
        // "Bookmark" rather than the domain's "landmark": a landmark is what the code calls
        // the thing because some of them arrive without anybody asking, and a bookmark is
        // what a person is doing when they put one there. The surfaces say the second.
        //
        // The entry NAMES which way it goes, unlike the toggle this replaced — a menu entry
        // is read before it is chosen, so it can afford the longer name that a control
        // wearing `aria-pressed` could not.
        let itemActions = "More actions"
        let addBookmark = "Bookmark"
        let removeBookmark = "Remove bookmark"
        let dismissMenu = "Close menu"
        let bookmarks = "Bookmarks"

        /// What a copy control says once it has copied, IN THE BOX that held the value —
        /// the confirmation lands where the eye already is, rather than beside it. A moment
        /// and not a state: whoever set it takes it back (`ClientMsg.CopiedMsg`).
        let copied = "copied"

        /// The word on every notice's disclosure. ONE word across all of them: the move is
        /// the same wherever it appears — what this costs you is on the surface, why it is
        /// happening is one keypress in — and a surface that invented its own word for it
        /// would read as a different kind of control.
        let details = "Details"

        // Where a peer is (presence, in the roster and on a terminal tab). The VALUE of
        // `data-peer-at` is one of these FIELD tokens — stable, one per collaborative field
        // — and the words beside it are for people, so they may name a terminal or a
        // collaborator and are composed rather than fixed.
        let atTitle = "title"
        let atDraft = "draft"
        let atQueued = "queued"
        let atTerminal = "terminal"
        let atTerminalQueued = "terminal-queued"

        let renamingSession = "renaming"
        /// Writing their own message — the plain case, and the one worth the fewest words.
        let writing = "writing"
        let inYourDraft = "in your message"
        let inDraftOf (name: string) : string = "in " + name + "'s message"
        let editingQueued = "in the queue"
        /// In a terminal, named when the terminal is known to this client.
        let inTerminal (title: string) : string = "in " + title
        let atSomeTerminal = "at a terminal"
        // Event-feed health tokens (the HTTP leg that carries history). `IsCatchingUp` says
        // there is more to read; these say whether reading is getting through.
        let feedLive = "live"
        let feedRetrying = "retrying"
        let feedPaused = "paused"
        // Session-leg tokens for the same strip: the transport itself, not its history feed.
        let degradedOffline = "offline"
        let degradedReconnecting = "reconnecting"
        // The reconnect offer's button (Plan 11).
        let reopenSession = "Reopen session"
        /// What reopening costs, on that offer's card. Two, because a deployment that
        /// addresses sessions by port brings one back at a NEW origin, and a browser
        /// partitions storage by origin — the promise the first can make is the one the
        /// second cannot keep. Both say the CONSEQUENCE; `ephemeralAddress` is the
        /// mechanism, and it goes behind the disclosure.
        let reopenPromise = "Your work is saved here and will sync when the session is back."
        let reopenPromiseEphemeral = "Anything written here since it stopped will be lost."
        /// What a credential that stopped working asks for. The panel row says it as a
        /// STATUS, in the caps the other status words use; the prompt over the timeline says
        /// it on a button, in the sentence case the other actions use. One phrase either
        /// way, because it is one thing to do.
        let signInAgain = "Sign in again"
        let signInAgainStatus = "sign in again"
        /// What a dead credential COSTS, on the prompt over the timeline. The button beside
        /// it already says what to do; this says why doing it matters, which is the half a
        /// status word and a button cannot carry between them.
        let signInLost (provider: string) : string = "This session cannot reach " + provider + " on your behalf."
        /// Ask now rather than waiting out the supervised backoff (Plan 20). The wording is
        /// what a person wants of it, not what it does to the loop.
        let retryNow = "Try again"
        /// What every degraded state promises: this is a local-first client, so a lost leg
        /// costs sync, not the ability to work.
        let localFallback = "Your work is saved on this device and will sync when the session is back."
        /// The same promise where it cannot be kept (Plan 13): this deployment addresses
        /// sessions by port, so a session that restarts comes back at a new origin — and a
        /// browser partitions storage by origin, which strands anything written here in the
        /// meantime. Everything already sent is safe; it is on the server.
        let localFallbackEphemeral = "Anything you write while the session is away will be lost."
        /// WHY those two differ, said once and folded away on both surfaces that carry it.
        /// Nobody needs the browser's storage model to understand what it costs them — but
        /// the reader who wonders why a local-first client would lose anything is owed it.
        let ephemeralAddress =
            "This session reopens at a new address, and a browser keeps saved work separately for each address."
        /// The model picker's state token, and its first option, for when nobody has chosen:
        /// the provider decides. Named rather than blank because "no model is set" and
        /// "whatever the provider picks" are the same fact, and only one of them is a
        /// sentence — a picker showing an empty row reads as a control that failed to load.
        let modelDefault = "default"
        let modelDefaultLabel = "Provider's default"
        /// Why this client is keeping no history (Plan 20). The Cache API needs a secure
        /// context; a session reached over plain HTTP at a non-loopback address has none, so
        /// nothing is kept and — without this — nothing says why, which is indistinguishable
        /// from a bug. The remedy is the operator's, and it is one flag, so name it.
        let historyNotKept = "This session's history will not be kept on this device."
        let historyNotKeptWhy =
            "It is served over plain HTTP, and a browser withholds storage of this kind outside a "
            + "secure context. Serving it over HTTPS restores it."
        /// What the composer's keys do, shown in the composer while you are in it. Enter is
        /// the send because that is what every chat surface's Enter is; what it used to do
        /// did not disappear, it split in two — a line break and a paragraph, which Enter
        /// alone could never tell apart.
        let composerKeys = "Enter sends · Shift+Enter line · Alt+Enter paragraph"
        /// What an empty composer says, so that a thin unmarked bar reads as somewhere to
        /// write. Lowercase and wordless of instruction, like every other prompt on the
        /// surface — `composerKeys` above teaches the keys, and this only says what the bar
        /// is. Drawn from a node decoration (`Editor.placeholderPlugin`), never content.
        let composerPlaceholder = "write a message"
        /// What the timeline's pulse means, for a reader who cannot see it pulse (Plan 20).
        let readingHistory = "Reading this session's history"
        /// What stands where history this device does not hold would be (Plan 20). Said only
        /// while nothing is coming to fill it — see `View.chat` — so it is a fact about this
        /// client's own store rather than a complaint about the network.
        let historyMissingLocally = "earlier history is not on this device"
        // Offset placeholder (em dash) when nothing has been read yet.
        let offsetNone = "—"
        // Non-human authors.
        let agent = "agent"
        let sessionProcess = "session-process"
        // Terminal block/queue status tokens (Plan 13).
        let blockRunning = "running"
        let blockOk = "ok"
        let blockFailed = "failed"
        let blockRejected = "rejected"
        /// How a lease stretch ended, on its chat item (Plan 14, stage 1). Four tokens rather
        /// than one, because the question a reader asks afterwards — "did nick finish, get
        /// taken over, drop out, or just wander off?" — has four different answers.
        let stretchReleased = "released"
        let stretchStolen = "stolen"
        let stretchGone = "holder-gone"
        let stretchIdle = "idle"
        /// What the pin mark is called, for anything that cannot see a blue glyph.
        let pinned = "pinned"
        /// What a second activation of the tab you are on will do. A gesture has no control
        /// of its own to be labelled, so it says so from the tab it acts on.
        let pinHint = "Select again to pin this tab"
        let unpinHint = "Select again to unpin this tab"
        /// A queued command that will run as soon as the terminal is free.
        let queuedReady = "ready"
        /// A queued command held because a peer is typing in its terminal (Plan 13, stage
        /// 2e) — it resolves when the person finishes their task, and a queue that said
        /// only *pending* would leave that looking like a stall.
        let queuedAwaitingTerminal = "awaiting-terminal"
        /// What a terminal that stopped marking costs, and why, on that terminal's own band.
        /// The status word says the fault; this says what it does to the queue, which is the
        /// thing a person is about to wonder about.
        let terminalNotMarking = "Queued commands are held until the terminal is re-armed."
        let terminalNotMarkingWhy =
            "The shell stopped reporting when a command starts and finishes, so nothing here can "
            + "tell when one has run."
        /// A queued command held because its terminal's shell stopped marking (Plan 13, stage
        /// 2f). Apart from `queuedAwaitingTerminal` because it resolves differently: one ends
        /// when a person finishes a task, this one when somebody repairs the terminal.
        let queuedAwaitingIntegration = "awaiting-integration"
        let system = "system"
        /// Why a turn nobody asked for exists (Plan 20, stage 2). The token a test reads off
        /// `data-message-woke`; the word beside it on screen is `turnWoke`.
        let wokeCommandFinished = "command-finished"
        /// The vocabulary's other two reasons (Plan 20, stage 5). One token per reason, so a
        /// test asserting WHY a turn exists never has to read the sentence a person reads.
        let wokeStreamEnded = "stream-ended"
        let wokeIntegrationLost = "integration-lost"
        let wokePrChanged = "pr-changed"
        /// What a woken turn wears in the chat, in the slot its siblings — *streaming*,
        /// *interrupted* — already occupy. A word rather than a new glyph: this design says
        /// a message's state in one lowercase word, and a mark nobody can decode without a
        /// tooltip would be a worse semantic than the vocabulary the surface already has.
        let turnWoke = "woke"
        /// The same fact, at length, for the reader who wants it. Never the visible label:
        /// the chat's meta line is three short words wide.
        let turnWokeCommandFinished = "The agent picked this up on its own: a command it left running in the background finished."
        let turnWokeStreamEnded = "The agent picked this up on its own: the stream behind a terminal it was working in has ended."
        let turnWokeIntegrationLost = "The agent picked this up on its own: a terminal it had a command running in stopped reporting, so nothing will say how that command ended."
        let turnWokePrChanged = "The agent picked this up on its own: a pull request watched here changed state."
        /// Stands where the quoted message would be when the ref points past the loaded page —
        /// the reply is real, its cause simply is not on screen yet.
        let replyRefMissing = "earlier message"
        /// The accessible name of the ref, said in full for a reader who does not get the
        /// quote's visual context.
        let replyRefLabel = "In reply to"
        // Conversation item status.
        let complete = "complete"
        let streaming = "streaming"
        let failed = "failed"
        let interrupted = "interrupted"
        // Environment lifecycle.
        let envNotStarted = "not-started"
        let envStarting = "starting"
        let envRunning = "running"
        let envFailed = "failed"
        let envStopped = "stopped"
        // Command output streams.
        let stdout = "stdout"
        let stderr = "stderr"

    /// `data-*` hooks and status tokens on the management UI (`ManagerUi`).
    module Manager =
        let sessions = "data-sessions"
        let session = "data-session"
        let status = "data-status"
        let launch = "data-launch"
        let stop = "data-stop"
        let openLink = "data-open"
        let createSession = "data-create-session"
        /// The row's archive verb, and the archived row's way back. Both carry the session id.
        let archive = "data-archive"
        let unarchive = "data-unarchive"
        /// Which build the MANAGER is running, on the page's own header. There is no
        /// per-session twin any more: the roster row's plumbing line was cut so the summary
        /// could have the column, and a session's build rides the registry stream instead of
        /// this page.
        let managerBuild = "data-manager-build"
        /// The one line a session says about ITSELF, rendered beside its status word. The
        /// Manager stores and shows the string without learning what it means, so this hook
        /// marks the place rather than the content — a test asserts that what the session
        /// said is what the row shows, never what a session ought to say.
        let summary = "data-session-summary"
        /// A filter or sort control. Carries a STABLE name (`show-archived`, `sort`) rather
        /// than the query it links to, so a swap can put focus back on the control that was
        /// pressed even though its href just changed.
        let filter = "data-filter"
        // Process status words shown in a row.
        let statusStopped = "stopped"
        let statusRunning = "running"
        let statusExited = "exited"
        /// Not a process status — an operator's durable decision. It sits in the same cell
        /// because "archived" is the answer a reader wants there, and "stopped" for a
        /// session that can no longer start is true and useless.
        let statusArchived = "archived"
