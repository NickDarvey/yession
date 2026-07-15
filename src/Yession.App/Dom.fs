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

    /// `data-*` hooks on the session client shell (`View`) and its browser delegation.
    module Hooks =
        // Sidebar — identity & live sync state.
        let connection = "data-connection"
        let displayName = "data-display-name"
        let catchUp = "data-catch-up"
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
        let startDraft = "data-start-draft"
        // Chrome: the sidebar drawer toggle (lives on the shell root, outside `#app`).
        let navToggle = "data-nav-toggle"

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
        // Offset placeholder (em dash) when nothing has been read yet.
        let offsetNone = "—"
        // Non-human authors.
        let agent = "agent"
        let sessionProcess = "session-process"
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
