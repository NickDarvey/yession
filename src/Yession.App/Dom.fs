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
        let environment = "data-environment"
        let commandLog = "data-command-log"
        let commandId = "data-command-id"
        let commandStatus = "data-command-status"
        let stream = "data-stream"
        // Deliberately never emitted — the command log is read-only. Named so the
        // "no input surface" invariant is asserted against a constant, not a literal.
        let commandInput = "data-command-input"
        // Conversation timeline.
        let conversation = "data-conversation"
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

    /// Observable text/value tokens the session view emits (labels and status words that
    /// tests assert exactly — never free-text message bodies, which are model data).
    module Text =
        // Connection state.
        let disconnected = "Disconnected"
        let connecting = "Connecting"
        let connected = "Connected"
        let reconnecting = "Reconnecting"
        // Catch-up.
        let catchingUp = "Catching up"
        let upToDate = "Up to date"
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
        /// A queued command whose terminal's mode demands an approval it has not got.
        let queuedAwaitingApproval = "awaiting-approval"
        /// A queued command that will run as soon as the terminal is free.
        let queuedReady = "ready"
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
        // Command result status tokens.
        let cmdPending = "pending"
        let cmdRunning = "running"
        let cmdTimedOut = "timed-out"
        let cmdExecutionFailed = "execution-failed"
        let cmdSucceeded (code: int) : string = "succeeded:" + string code
        let cmdFailed (code: int) : string = "failed:" + string code
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
