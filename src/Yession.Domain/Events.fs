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
    // A message consumed off the queue: the body snapshotted at drain time by the Session
    // Process. Immutable — later edits never touch it. Drafts themselves are ephemeral WIP
    // in the synced state and are never durable facts (only their send is).
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
    // An explicit interrupt (Phase 3, Step 17): the turn's terminal event when a peer
    // cancels it. The partial response streamed so far is kept.
    | AgentTurnInterrupted of AgentTurnInterrupted
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
    // Terminals (Plan 13): durable FACTS about a terminal — never its raw output, which
    // lives in the per-terminal transcript sidecar (`Transcript.fs`). A terminal that
    // printed a gigabyte adds four events here, not a gigabyte, so the log every client
    // folds stays the size of what happened rather than the size of what was printed.
    // The block events bracket the transcript range they produced (`FromSeq`/`ToSeq`),
    // which is how "who ran this, and which bytes are its output" is answerable from the
    // log and the transcript together.
    | TerminalOpened of TerminalOpened
    | TerminalClosed of TerminalClosed
    | TerminalBlockStarted of TerminalBlockStarted
    | TerminalBlockCompleted of TerminalBlockCompleted
    | TerminalCommandRejected of TerminalCommandRejected
    | TerminalTranscriptTruncated of TerminalTranscriptTruncated

and SessionCreated =
    { SessionId : SessionId }

and PeerJoined =
    { PeerId : PeerId
      DisplayName : string
      /// The Manager-verified user behind this connection, when the authentication
      /// strategy attributed one. None = unattributed access (trust-localhost) —
      /// the connection is identified only by its peer.
      User : UserId option }

and PeerLeft =
    { PeerId : PeerId }

and MessageSent =
    { MessageId : MessageId
      /// The queue entry this message was consumed from (Phase 3): the durable link
      /// from doc-world to event-world, and the drain's exactly-once dedup key.
      /// `None` for messages that predate the queue.
      QueueId : QueueId option
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

and AgentTurnInterrupted =
    { AgentTurnId : AgentTurnId
      RequestedBy : PeerId }

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

and TerminalOpened =
    { TerminalId : TerminalId
      /// Who asked for it. A terminal is opened by a peer or by the agent, and which one
      /// decides nothing about how it behaves — it is attribution, for the audit.
      OpenedBy : ActorRef
      /// A human label, so a session with four terminals is navigable. Never unique.
      Title : string }

and TerminalClosed =
    { TerminalId : TerminalId
      Reason : string }

and TerminalBlockStarted =
    { TerminalId : TerminalId
      BlockId : BlockId
      /// The queue entry this block was drained from, when it came through the composer.
      /// `None` for a block the Session Process ran on its own behalf.
      QueueId : QueueId option
      /// Who wrote the command — not who approved it (that is `ApprovedBy`) and not who
      /// happened to press send.
      Author : ActorRef
      /// The approver, when the terminal's mode required one. `None` = ran unapproved,
      /// which is a fact worth recording rather than an absence worth inferring.
      ApprovedBy : ActorRef option
      /// The command line, snapshotted from the collaborative draft at drain time and
      /// immutable thereafter — exactly as `MessageSent` snapshots a message body.
      Command : string
      /// The transcript line index at which this block's output begins.
      FromSeq : int }

/// A queued command a peer refused (Plan 13, stage 2a). The other half of the approval
/// gate: a log that records every yes and no no is the weaker thing wearing the stronger
/// thing's face, and "the agent proposed this and a human said no" is the more interesting
/// half of the two.
///
/// Deliberately NOT a `SessionCommand`. A command frame from a peer that drops mid-flight
/// is lost, and the log stays the Session Process's alone to write — so a peer writes
/// `RejectedBy` on the doc entry and the drain, which is already the queue's single
/// consumer, observes it and appends this.
and TerminalCommandRejected =
    { TerminalId : TerminalId
      QueueId : QueueId
      /// Minted here, exactly as `TerminalBlockStarted` mints one, rather than derived by
      /// each client's fold from the `QueueId`. A `BlockId` names a proposed command and
      /// its outcome, not a process — so a refusal has one, and a handle that is
      /// addressable later does not depend on a derivation rule living nowhere in the data.
      BlockId : BlockId
      /// Whose command it was. Usually the agent's; that is the point of recording this.
      Author : ActorRef
      RejectedBy : ActorRef
      /// The command line, snapshotted because the doc entry is deleted immediately after.
      /// A record saying *something* was rejected is not a record.
      Command : string
      Reason : string option }

and TerminalBlockCompleted =
    { TerminalId : TerminalId
      BlockId : BlockId
      Result : CommandResult
      /// The transcript line index one past this block's last output line.
      ToSeq : int }

and TerminalTranscriptTruncated =
    { TerminalId : TerminalId
      BlockId : BlockId option
      /// Output this terminal produced and the transcript did NOT keep. Recorded so a
      /// gap in an audit trail is a stated fact, never a silent one.
      DroppedBytes : int }
