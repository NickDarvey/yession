namespace Yession.SessionProcess

open Yession.Domain
open Yession.Domain.Chat

/// The Session Process Elmish model and its sub-states. The model holds a single typed
/// snapshot of the session: synced collaborative state, event-log read position, peers,
/// the conversation projection, and the agent runtime state. See docs/design.md §1
/// "Reactive", §2.2.

/// The model's view of the event log: how far the projection has consumed.
type EventLogState = { LatestOffset : EventOffset option }

type PeerConnectionState =
    { PeerId         : PeerId
      DisplayName    : string
      LastSeenOffset : EventOffset option }

type AgentRuntimeState =
    | Idle
    | Running of AgentTurnId
    | Failed  of AgentTurnId * string

type ProcessModel =
    { SessionId    : SessionId
      Synced       : SyncedSessionState
      EventLog     : EventLogState
      Peers        : Map<PeerId, PeerConnectionState>
      Conversation : ConversationProjection
      Agent        : AgentRuntimeState }

module ProcessModel =

    /// The model for a freshly created session: nothing synced, nothing consumed, idle.
    let initial (sessionId: SessionId) : ProcessModel =
        { SessionId = sessionId
          Synced = SyncedSessionState.empty
          EventLog = { LatestOffset = None }
          Peers = Map.empty
          Conversation = ConversationProjection.empty
          Agent = Idle }

    /// Fold one event into the agent runtime state (Step 08): a started turn is
    /// `Running` until its message completes (`Idle`) or the turn fails (`Failed`).
    let private applyAgentEvent (state: AgentRuntimeState) (event: SessionEvent) : AgentRuntimeState =
        match event with
        | SessionEvent.AgentTurnStarted a -> Running a.AgentTurnId
        | SessionEvent.AgentMessageCompleted _ -> Idle
        | SessionEvent.AgentTurnInterrupted _ -> Idle
        | SessionEvent.AgentTurnFailed a -> Failed (a.AgentTurnId, a.Reason)
        | _ -> state

    /// Advance the model by folding new event envelopes into the conversation projection,
    /// the agent runtime state, and the event-log read position. Idempotent on offset via
    /// the shared projection fold, so overlapping pages do not double-apply.
    let applyEvents (events: EventEnvelope<SessionEvent> list) (model: ProcessModel) : ProcessModel =
        let conversation, highWater =
            ConversationProjection.applyEvents model.EventLog.LatestOffset events model.Conversation
        let agent =
            let appliedThrough = model.EventLog.LatestOffset |> Option.map EventOffset.value
            events
            |> List.filter (fun e ->
                match appliedThrough with
                | Some n -> EventOffset.value e.Offset > n
                | None -> true)
            |> List.fold (fun state e -> applyAgentEvent state e.Event) model.Agent
        { model with
            Conversation = conversation
            Agent = agent
            EventLog = { LatestOffset = highWater } }
