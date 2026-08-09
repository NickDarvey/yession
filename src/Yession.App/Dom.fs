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

    /// `data-*` hooks on the session client shell (`View`) and its browser delegation.
    module Hooks =
        // Header — the collaborative session title and its secondary id.
        let sessionTitle = "data-session-title"
        let sessionId = "data-session-id"
        let cursorPeer = "data-cursor-peer"
        // Sidebar — identity & live sync state.
        let connection = "data-connection"
        let displayName = "data-display-name"
        let catchUp = "data-catch-up"
        /// Why the client is not connected, when it knows; absent otherwise.
        let connectionReason = "data-connection-reason"
        /// The durable event feed's health (sidebar), carrying a `Text.feed*` token.
        let feed = "data-feed"
        /// The one degradation strip over the timeline, carrying the token of whichever leg
        /// is down (`Text.degraded*` or `Text.feed*`); absent when everything is healthy.
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
        /// The empty timeline's caret anchor: present ONLY while nothing has happened yet, so
        /// its absence is as meaningful as its presence — a session with a conversation in it
        /// has its own anchor, and two would be one too many.
        let conversationIdle = "data-conversation-idle"
        let messageId = "data-message-id"
        let messageAuthor = "data-message-author"
        let messageStatus = "data-message-status"
        let messageBody = "data-message-body"
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
        let agentPresence = "data-agent-presence"
        let noAgent = "data-no-agent"
        let noAgentConnect = "data-no-agent-connect"
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
        let terminalMode = "data-terminal-mode"
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
        let terminalApprove = "data-terminal-approve"
        let terminalUnapprove = "data-terminal-unapprove"
        let terminalReject = "data-terminal-reject"
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
        /// The pane's tab strip (Plan 14, stage 2). One hook for every tab whatever it shows
        /// — a terminal, a block's read-only view, a stretch's replay — because they are one
        /// tablist and a test asserting keyboard order should not have to know which is which.
        /// Its value is `PaneTab.key`.
        let paneTab = "data-pane-tab"
        /// The close control on a tab a person opened. Terminal tabs have none: the strip
        /// lists every terminal the session has, and "close" there already means something
        /// else (`terminalClose`).
        let paneTabClose = "data-pane-tab-close"
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
        /// The step-out from a block's sliced view to the whole terminal's recording.
        let panePlayWhole = "data-pane-play-whole"
        /// The live screen of a terminal in live mode (Plan 14, stage 6). Its value is the
        /// terminal's id; the holder's copy is the one that takes keystrokes, and every other
        /// peer's is the same screen read-only.
        let terminalScreen = "data-terminal-screen"
        /// The DVR (Plan 14, stage 7): step back through what a LIVE terminal has recorded
        /// so far, and catch back up to its edge. Offered on any live terminal, whichever
        /// mode it is in — both are one growing byte stream, and a rule that offered it for
        /// an interactive session and not for a running build would be a special case to
        /// explain rather than a feature.
        let terminalRewind = "data-terminal-rewind"
        let terminalLive = "data-terminal-live"
        /// How far behind live the rewound reader is, growing as the terminal keeps
        /// printing under them.
        let terminalBehind = "data-terminal-behind"

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
        /// What every degraded state promises: this is a local-first client, so a lost leg
        /// costs sync, not the ability to work.
        let localFallback = "You can keep writing — everything is saved locally and syncs when the session is back."
        /// The same promise where it cannot be kept (Plan 13): this deployment addresses
        /// sessions by port, so a session that restarts comes back at a new origin — and a
        /// browser partitions storage by origin, which strands anything written here in the
        /// meantime. Everything already sent is safe; it is on the server.
        let localFallbackEphemeral =
            "You can keep writing, but this session reopens at a new address — anything written here while it is away will not come back with it."
        /// What the composer's keys do, shown in the composer while you are in it. Enter is
        /// the send because that is what every chat surface's Enter is; what it used to do
        /// did not disappear, it split in two — a line break and a paragraph, which Enter
        /// alone could never tell apart.
        let composerKeys = "Enter sends · Shift+Enter line · Alt+Enter paragraph"
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
        /// A queued command whose terminal's mode demands an approval it has not got.
        let queuedAwaitingApproval = "awaiting-approval"
        /// A queued command that will run as soon as the terminal is free.
        let queuedReady = "ready"
        /// A queued command held because a peer is typing in its terminal (Plan 13, stage
        /// 2e). Distinct from `queuedAwaitingApproval` on purpose: one resolves when a person
        /// makes a decision, the other when a person finishes a task, and a queue that said
        /// only *pending* would leave both looking like a stall.
        let queuedAwaitingTerminal = "awaiting-terminal"
        /// A queued command held because its terminal's shell stopped marking (Plan 13, stage
        /// 2f). Apart from `queuedAwaitingTerminal` because it resolves differently: one ends
        /// when a person finishes a task, this one when somebody repairs the terminal.
        let queuedAwaitingIntegration = "awaiting-integration"
        let system = "system"
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
        // Process status words shown in a row.
        let statusStopped = "stopped"
        let statusRunning = "running"
        let statusExited = "exited"
