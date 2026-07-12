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
        | DraftStarted _ -> "draft-started"
        | MessageSent _ -> "message-sent"
        | AgentTurnStarted _ -> "agent-turn-started"
        | AgentContextBuilt _ -> "agent-context-built"
        | AgentMessageStarted _ -> "agent-message-started"
        | AgentMessageDelta _ -> "agent-message-delta"
        | AgentMessageCompleted _ -> "agent-message-completed"
        | AgentTurnFailed _ -> "agent-turn-failed"
