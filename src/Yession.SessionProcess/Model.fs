namespace Yession.SessionProcess

open Yession.Domain

/// The Session Process Elmish model and its sub-states. The model holds a single typed
/// snapshot of the session: synced collaborative state, event-log read position, peers,
/// the conversation projection, and the agent runtime state. See docs/design.md §1
/// "Reactive", §2.2 and docs/plans/00-init/02-*.

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

    /// Advance the model by folding new event envelopes into the conversation projection
    /// and the event-log read position. Idempotent on offset via the shared projection
    /// fold, so overlapping pages do not double-apply.
    let applyEvents (events: EventEnvelope<SessionEvent> list) (model: ProcessModel) : ProcessModel =
        let conversation, highWater =
            ConversationProjection.applyEvents model.EventLog.LatestOffset events model.Conversation
        { model with
            Conversation = conversation
            EventLog = { LatestOffset = highWater } }
