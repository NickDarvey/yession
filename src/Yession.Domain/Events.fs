namespace Yession.Domain

open System

/// The generic envelope wrapping every persisted event. The wire format is a boundary
/// concern handled in Serialization.fs; this is the in-memory domain shape.
/// See docs/design.md §6.
type EventEnvelope<'event> =
    { EventId   : EventId
      SessionId : SessionId
      Offset    : EventOffset
      Actor     : ActorRef
      Timestamp : DateTimeOffset
      Event     : 'event }

/// Command output streams and results (Step 13). Defined with the events because the
/// command lifecycle is recorded as events; the request/execution shapes live with the
/// capability surface in Environment.fs.
type OutputStream =
    | Stdout
    | Stderr

type CommandResult =
    | CommandSucceeded of exitCode: int
    | CommandFailed of exitCode: int
    | CommandTimedOut
    | CommandExecutionFailed of reason: string

/// The single, append-only session event type. New cases are added per delivery step;
/// foundations define only `SessionCreated`.
type SessionEvent =
    | SessionCreated of SessionCreated
    // Control/presence facts appended by the Session Process on connect/disconnect (Step 03).
    | PeerJoined of PeerJoined
    | PeerLeft of PeerLeft
    // The durable fact that a draft began (Step 05). The draft's *content* lives in the
    // synced collaborative state (Yjs), never in the event log.
    | DraftStarted of DraftStarted
    // A draft sent: the body snapshotted at send time by the Session Process (Step 06).
    // Immutable in Phase 1 — later draft edits never touch it.
    | MessageSent of MessageSent
    // Agent turn lifecycle (Step 08): the agent's response is represented entirely as
    // events — streamed deltas project as a Streaming conversation item; completion or
    // failure flips it. Appended only by the Session Process.
    | AgentTurnStarted of AgentTurnStarted
    | AgentContextBuilt of AgentContextBuilt
    | AgentMessageStarted of AgentMessageStarted
    | AgentMessageDelta of AgentMessageDelta
    | AgentMessageCompleted of AgentMessageCompleted
    | AgentTurnFailed of AgentTurnFailed
    // Environment lifecycle (Step 12): environments start lazily — a need is identified
    // (usually by the agent), then the Session Process starts one through its scoped
    // capability. Every transition is a durable fact.
    | EnvironmentNeedIdentified of EnvironmentNeedIdentified
    | EnvironmentStartRequested of EnvironmentStartRequested
    | EnvironmentStarted of EnvironmentStarted
    | EnvironmentStartFailed of EnvironmentStartFailed
    | EnvironmentStopRequested of EnvironmentStopRequested
    | EnvironmentStopped of EnvironmentStopped
    // Command lifecycle (Step 13): commands run in the session environment through the
    // scoped capability; output streams into the log so the command log is event-derived
    // and read-only everywhere.
    | CommandRequested of CommandRequested
    | CommandStarted of CommandStarted
    | CommandOutputReceived of CommandOutputReceived
    | CommandCompleted of CommandCompleted

and SessionCreated =
    { SessionId : SessionId }

and PeerJoined =
    { PeerId : PeerId
      DisplayName : string }

and PeerLeft =
    { PeerId : PeerId }

and DraftStarted =
    { DraftId : DraftId
      StartedBy : PeerId }

and MessageSent =
    { MessageId : MessageId
      /// The draft the message came from; `None` once direct (draftless) sends exist.
      DraftId : DraftId option
      Author : ActorRef
      Body : string }

and AgentTurnStarted =
    { AgentTurnId : AgentTurnId
      TriggeredByMessageId : MessageId }

and AgentContextBuilt =
    { AgentTurnId : AgentTurnId
      MessageCount : int }

and AgentMessageStarted =
    { AgentTurnId : AgentTurnId
      MessageId : MessageId }

and AgentMessageDelta =
    { AgentTurnId : AgentTurnId
      MessageId : MessageId
      Delta : string }

and AgentMessageCompleted =
    { AgentTurnId : AgentTurnId
      MessageId : MessageId
      Body : string }

and AgentTurnFailed =
    { AgentTurnId : AgentTurnId
      Reason : string }

and EnvironmentNeedIdentified =
    { Reason : string
      AgentTurnId : AgentTurnId option }

and EnvironmentStartRequested =
    { EnvironmentId : string
      SpecSummary : string }

and EnvironmentStarted =
    { EnvironmentId : string
      ContainerRef : string }

and EnvironmentStartFailed =
    { EnvironmentId : string
      Reason : string }

and EnvironmentStopRequested =
    { EnvironmentId : string }

and EnvironmentStopped =
    { EnvironmentId : string }

and CommandRequested =
    { CommandId : CommandId
      Executable : string
      Arguments : string list }

and CommandStarted =
    { CommandId : CommandId }

and CommandOutputReceived =
    { CommandId : CommandId
      Stream : OutputStream
      Text : string }

and CommandCompleted =
    { CommandId : CommandId
      Result : CommandResult }
