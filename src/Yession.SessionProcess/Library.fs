namespace Yession.SessionProcess

open Yession.Domain

/// Placeholder that establishes the dependency on the shared domain library. The real
/// Session Process (event log, Yjs document, Elmish loop, agent runtime, WebRTC
/// protocol) is built up across later delivery steps. See docs/plans/00-init.
module Bootstrap =

    /// Smoke helper proving the shared domain vocabulary is reachable from the process.
    let describe (event: SessionEvent) : string =
        match event with
        | SessionCreated _ -> "session-created"
        | PeerJoined _ -> "peer-joined"
        | PeerLeft _ -> "peer-left"
        | MessageSent _ -> "message-sent"
        | AgentTurnStarted _ -> "agent-turn-started"
        | AgentContextBuilt _ -> "agent-context-built"
        | AgentMessageStarted _ -> "agent-message-started"
        | AgentMessageDelta _ -> "agent-message-delta"
        | AgentMessageCompleted _ -> "agent-message-completed"
        | AgentTurnFailed _ -> "agent-turn-failed"
        | AgentTurnInterrupted _ -> "agent-turn-interrupted"
        | EnvironmentNeedIdentified _ -> "environment-need-identified"
        | EnvironmentStartRequested _ -> "environment-start-requested"
        | EnvironmentStarted _ -> "environment-started"
        | EnvironmentStartFailed _ -> "environment-start-failed"
        | EnvironmentStopRequested _ -> "environment-stop-requested"
        | EnvironmentStopped _ -> "environment-stopped"
        | CommandRequested _ -> "command-requested"
        | CommandStarted _ -> "command-started"
        | CommandOutputReceived _ -> "command-output-received"
        | CommandCompleted _ -> "command-completed"
        | SessionEvent.TerminalOpened _ -> "terminal-opened"
        | SessionEvent.TerminalClosed _ -> "terminal-closed"
        | SessionEvent.TerminalLeaseTaken _ -> "terminal-lease-taken"
        | SessionEvent.TerminalLeaseReleased _ -> "terminal-lease-released"
        | SessionEvent.TerminalBlockStarted _ -> "terminal-block-started"
        | SessionEvent.TerminalBlockCompleted _ -> "terminal-block-completed"
        | SessionEvent.TerminalCommandRejected _ -> "terminal-command-rejected"
        | SessionEvent.TerminalIntegrationLost _ -> "terminal-integration-lost"
        | SessionEvent.TerminalIntegrationRestored _ -> "terminal-integration-restored"
        | SessionEvent.TerminalTranscriptTruncated _ -> "terminal-transcript-truncated"
