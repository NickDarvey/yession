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
    | TerminalLeaseTaken of TerminalLeaseTaken
    | TerminalLeaseReleased of TerminalLeaseReleased
    | TerminalBlockStarted of TerminalBlockStarted
    | TerminalBlockCompleted of TerminalBlockCompleted
    | TerminalCommandRejected of TerminalCommandRejected
    | TerminalIntegrationLost of TerminalIntegrationLost
    | TerminalIntegrationRestored of TerminalIntegrationRestored
    | TerminalTranscriptTruncated of TerminalTranscriptTruncated
    // Repos (Plan 14): durable facts about the session's repos directory — who brought
    // which repo in, removed it, or moved its checkout to another branch. The agent's
    // verbs and the settings panel are two interfaces over one function, and these
    // events are that function's record; they also project into the conversation
    // timeline (each carries the Process-minted MessageId its timeline note folds
    // under), so humans and the agent's context both see the history.
    | RepoAdded of RepoAdded
    | RepoRemoved of RepoRemoved
    | RepoBranchSwitched of RepoBranchSwitched
    // Named WorkSandboxes (Plan 15, stage 2). A session used to have exactly one
    // environment, whose lifecycle is the `Environment*` events above — those stay, and
    // stay the record of what the SANDBOX did. These record what a PARTY asked for: an
    // act, attributed, with its own MessageId so it reads in the timeline beside the repo
    // notes it is a sibling of.
    | WorkSandboxStarted of WorkSandboxStarted
    | WorkSandboxStopped of WorkSandboxStopped
    // The approval gate's refusal (Plan 15, stage 3). Only the refusal: an approval is
    // recorded on the event of the command it released.
    | CommandRefused of CommandRefused
    // Tool use (Plan 16, part C): every call the agent makes, recorded. Two events rather
    // than one, and for the same reason a block has two — a call that takes four minutes
    // must hold its place in the chat WHILE it is the only thing happening, so the start
    // anchors it and the finish moves what it says without touching where it sits.
    //
    // These fold into the TIMELINE and deliberately not into `ConversationProjection`: the
    // agent made the call and already has the result in its own transcript, so feeding it
    // back would double-feed the model. That is the same rule terminals follow, and the
    // opposite of the one repos follow — the question is not "did the agent do it" but
    // "does a future turn need to be told?".
    | ToolUseStarted of ToolUseStarted
    | ToolUseFinished of ToolUseFinished
    // The MCP servers this session was given (Plan 17). With no attach step there is no
    // human act to record — but the SESSION still gains and loses whole namespaces of
    // tools while it runs, and a turn that suddenly has four serial tools it did not have
    // before needs to know why. So the question part C asked applies: not "did the agent
    // do it" but "does a future turn need to be told?".
    | McpServerAvailable of McpServerNoted
    | McpServerUnavailable of McpServerNoted

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
      /// What was said to start this turn. `None` for a turn nobody asked for (Plan 20,
      /// stage 2) — an option rather than a minted stand-in, because an id that names no
      /// message is a fact invented to fill a field.
      TriggeredByMessageId : MessageId option
      /// Why this turn exists when nobody spoke (Plan 20, stage 2). `None` is the ordinary
      /// turn, whose trigger is the message above.
      ///
      /// Durable because an agent that acts unprompted must be able to SAY why on every
      /// surface that shows what it did — an unexplained turn in a shared session reads as
      /// the agent deciding on its own, which is the one thing it must never look like.
      Woke : WakeReason option }

/// Why a turn exists when nobody spoke (Plan 20, stage 2).
///
/// Attribution, never payload: the substance of what happened arrives through the same door
/// every turn's does — `TerminalBlockDigest` for terminal work, the tool roster for a roster
/// change — so a wake carries the REASON and nothing else. A reason that carried results
/// would be a second channel into the agent's context, free to disagree with the first.
and WakeReason =
    /// A command the agent asked to run in the background finished while it was not running.
    | CommandFinished

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
      Title : string
      /// Which of the session's WorkSandboxes it runs in (Plan 15, stage 2). Named on the
      /// OPEN event because it is fixed for the terminal's life, and because a replayed
      /// log has to be able to bring the terminal back up in the same sandbox it was in.
      /// A log written before named sandboxes decodes to `default`, which is where those
      /// terminals were.
      ///
      /// `None` means the terminal runs in NO sandbox: its bytes come from a stream
      /// somebody else produces (Plan 16, part D). Optional rather than defaulted, because
      /// saying an attached serial port is in `default` would be inventing a fact — and the
      /// two consumers both need the difference: the panel says where a terminal is, and
      /// the block runner picks an environment by it.
      Sandbox : SandboxName option
      /// Can this terminal's stream be asked for again (Plan 19, step 4)?
      ///
      /// The provider's claim about its own tool, recorded here because a person meets this
      /// question at the WORST moment to go looking for the answer: the stream has ended,
      /// the terminal is closed, and what they want to know is whether there is a way back.
      /// False for a shell, which has no provider to ask, and for a log written before this
      /// field existed.
      Renewable : bool }

and TerminalClosed =
    { TerminalId : TerminalId
      Reason : string }

/// A peer took the terminal's stdin (Plan 13, stage 2e) — live mode entered, or STOLEN from
/// whoever held it before. One event for both, because they are the same fact: from this
/// moment these keystrokes are that peer's. Collaborators are trusted, so a steal needs no
/// permission; what it needs is to be on the record, which is this.
and TerminalLeaseTaken =
    { TerminalId : TerminalId
      By : ActorRef
      /// The transcript line index at which this stretch of live mode begins (Plan 14,
      /// stage 1). A block records the range it produced and a lease stretch did not, so
      /// an interactive stretch had no replay bounds at all — and nothing can derive them
      /// afterwards, because only the Process knows where the transcript stood when the
      /// lease changed hands.
      FromSeq : int }

/// The terminal is back in block mode. Appended when the holder releases it, when a peer
/// steals it (the previous holder's lease ends), and when the holder's CONNECTION drops —
/// a lease held by someone who is gone is the one hold nobody should have to clear by hand.
and TerminalLeaseReleased =
    { TerminalId : TerminalId
      /// Who held it. Kept because the interesting question afterwards is whose keystrokes
      /// the bracketed transcript range belongs to, and an empty release cannot answer it.
      Was : ActorRef
      Reason : TerminalLeaseEnd
      /// One past the last transcript line of the stretch that just ended — the other half
      /// of the range `TerminalLeaseTaken.FromSeq` opened.
      ToSeq : int }

/// Why a lease ended. Distinguished because they read differently in a log: a release is a
/// person finishing, a steal is another person taking over, a drop is nobody deciding
/// anything at all, and an idle reclaim is a person who is still here and has stopped.
and TerminalLeaseEnd =
    | LeaseReleased
    | LeaseStolen of by: ActorRef
    | LeaseHolderGone
    /// Reclaimed by the idle timeout (Plan 13, stage 3c): the holder is still connected and
    /// simply stopped typing while something was queued behind them. Its own case because the
    /// question a reader asks afterwards — "did nick finish, drop out, or just wander off?" —
    /// has three different answers, and answering it as `LeaseReleased` would say the holder
    /// decided something they did not.
    | LeaseIdle

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
      FromSeq : int
      /// Whose credential the command ran on, when that is not the author's own (Plan 20,
      /// stage 2) — the queue entry's `OnBehalfOf`, carried onto the block.
      ///
      /// Here because a WOKEN turn has no triggering message to resolve its authority from,
      /// and the log is the only thing it can read. Absent means no turn can be woken by
      /// this block: an unresolvable owner runs on NOTHING rather than on somebody else's
      /// credential, which is the direction every other reader of this field already takes.
      OnBehalfOf : ActorRef option
      /// Whether the agent asked for this one to run in the BACKGROUND (Plan 20, stage 2):
      /// it did not hold the turn open, and its completion is something the agent wants to
      /// be told about.
      ///
      /// On the block rather than only in the queue entry it came from, because that is what
      /// makes "is a wake due" a pure fold over the log: the doc's entry is gone the moment
      /// the block starts, and a scheduling decision that depended on it would be a decision
      /// a restart could not re-derive.
      Background : bool }

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

/// The shell stopped emitting marks (Plan 13, stage 2f). `exec sh`, or an image whose shell
/// drops into another, replaces the process we instrumented while the pty stays open — so
/// `Exited` never fires and the marks simply stop.
///
/// Durable rather than runtime-only state, for the same reason `TerminalTranscriptTruncated`
/// is: this is a GAP in what the record can say. From here the Process cannot tell when a
/// command started or finished, so "we no longer know when this finished" is a fact about the
/// audit trail and belongs in it — not merely on a screen somebody may not be looking at.
and TerminalIntegrationLost =
    { TerminalId : TerminalId
      /// The block that was open when it happened, if one was. It stays open — its `ToSeq`
      /// and exit code are exactly what was lost — and naming it here is what lets a reader
      /// tell an unbounded block from a running one.
      BlockId : BlockId option }

/// Marking is back (Plan 13, stage 2f): a peer used the re-arm control and the shell that is
/// actually there now answered our instrumentation.
and TerminalIntegrationRestored =
    { TerminalId : TerminalId }

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

and RepoAdded =
    { /// The timeline note's identity, minted by the Process at append time — which is
      /// what lets the conversation projection fold this without inventing ids.
      MessageId : MessageId
      Repo : RepoRef
      /// The branch the clone landed on (the remote's default).
      Branch : string
      /// Who brought it in — the panel's human or the agent. Carried on the payload
      /// because the projection reads events, not envelopes, and "who added this repo"
      /// is the fact the shared-trust disclosure hangs off.
      Actor : ActorRef
      /// Who approved it, when the subject's gate required an approval (Plan 15, stage 3).
      /// `None` is the ordinary case and means nobody had to — exactly as it does on
      /// `TerminalBlockStarted`, which is the same field for the same reason: the approver
      /// belongs on the thing approved, not on an event beside it.
      ApprovedBy : ActorRef option }

and RepoRemoved =
    { MessageId : MessageId
      Repo : RepoRef
      Actor : ActorRef
      /// Who approved it, when the subject's gate required an approval (Plan 15, stage 3).
      /// `None` is the ordinary case and means nobody had to — exactly as it does on
      /// `TerminalBlockStarted`, which is the same field for the same reason: the approver
      /// belongs on the thing approved, not on an event beside it.
      ApprovedBy : ActorRef option }

and RepoBranchSwitched =
    { MessageId : MessageId
      Repo : RepoRef
      Branch : string
      /// True when the switch created the branch (`-b`), false when it checked out an
      /// existing one — one event, because it is one act at the panel and one verb.
      Created : bool
      Actor : ActorRef
      /// Who approved it, when the subject's gate required an approval (Plan 15, stage 3).
      /// `None` is the ordinary case and means nobody had to — exactly as it does on
      /// `TerminalBlockStarted`, which is the same field for the same reason: the approver
      /// belongs on the thing approved, not on an event beside it.
      ApprovedBy : ActorRef option }

and WorkSandboxStarted =
    { MessageId : MessageId
      Sandbox : SandboxName
      /// Which backend it came up on, so the record says what confinement it actually
      /// got rather than what the operator configured at some point.
      Backend : string
      /// The credential NAMES forwarded into it — never a value, and never a token
      /// shape that could be mistaken for one. Forwarding is a fact about the sandbox
      /// that outlives the turn that asked for it, so the log has to carry it; what the
      /// credential IS belongs only in the sandbox's env.
      Forwarded : string list
      /// Whose credentials were forwarded. Distinct from `Actor` on purpose: for an
      /// agent-issued start the AGENT is the acting party while the credentials are the
      /// turn human's (Plan 08 — no borrowing, and the agent has no scope of its own).
      /// `None` when nothing was forwarded, because then nobody's were.
      CredentialOwner : ActorRef option
      Actor : ActorRef
      /// Who approved it, when the subject's gate required an approval (Plan 15, stage 3).
      /// `None` is the ordinary case and means nobody had to — exactly as it does on
      /// `TerminalBlockStarted`, which is the same field for the same reason: the approver
      /// belongs on the thing approved, not on an event beside it.
      ApprovedBy : ActorRef option }

and WorkSandboxStopped =
    { MessageId : MessageId
      Sandbox : SandboxName
      Actor : ActorRef
      /// Who approved it, when the subject's gate required an approval (Plan 15, stage 3).
      /// `None` is the ordinary case and means nobody had to — exactly as it does on
      /// `TerminalBlockStarted`, which is the same field for the same reason: the approver
      /// belongs on the thing approved, not on an event beside it.
      ApprovedBy : ActorRef option }

/// A command a human refused at its gate (Plan 15, stage 3). The mirror of
/// `TerminalCommandRejected`, and it exists for that event's reason: a refusal that simply
/// vanishes is indistinguishable from a bug — to the person who pressed the button and to
/// the model, which will otherwise try the same thing another way.
///
/// There is no `CommandApproved` beside it. An approval rides the event the command itself
/// appends (`ApprovedBy`), where it stays attached to the act it released; a separate event
/// would spend two records on one act and detach the approver from what they approved.
and CommandRefused =
    { MessageId : MessageId
      /// The pending act's id — the handle the agent was given, so the refusal it reads
      /// back joins the request it made.
      QueueId : QueueId
      /// The MCP tool name, which is both what the model called and what the gate was
      /// configured against.
      Tool : string
      /// The arguments as they were shown to the person who refused them. Rendered, not
      /// raw: what the log should record is what was on the screen.
      Summary : string
      /// Who proposed it. Always the agent today (commands are agent-only), and carried
      /// anyway because `yession.yaml` will propose them too.
      Author : ActorRef
      RejectedBy : ActorRef
      Reason : string option }

/// Whether the CALL happened, which is not whether it went well. A command that exits 1
/// succeeded as a tool call — the model is meant to read that and choose differently.
/// `ToolCallFailed` means the call never reached a tool at all: no such tool, or arguments
/// that could not be read.
and ToolOutcome =
    | ToolCallOk
    | ToolCallFailed of reason: string

and ToolUseStarted =
    { /// Minted by the Process, so the chip has a handle a link can carry.
      ToolUseId : ToolUseId
      /// The turn that made the call — what lets a chatty turn be grouped into one line
      /// instead of twenty.
      AgentTurnId : AgentTurnId
      /// Where the call went. `yession` for the session's own verbs; a provider's name for
      /// anything a session was given.
      Namespace : string
      Name : string
      /// The arguments AS RECORDED. Fields the schema marks `writeOnly` never reach here —
      /// they are dropped as the record is built, so there is no write path to get wrong.
      /// `None` for a tool whose schema we did not write: we cannot trust a foreign schema
      /// to mark its own secrets, so nothing of a foreign call's arguments is recorded.
      Arguments : string option }

/// A server entering or leaving what this session may reach.
///
/// `ActorRef.System` on the way in, always, because nobody in the session did it —
/// attributing it to the agent, or to whoever happens to be connected, would be inventing
/// an actor. The name alone, not the url: the timeline is what a human reads, and where a
/// server lives is the `mcp_servers` query's business.
and McpServerNoted =
    { MessageId : MessageId
      Name : McpServerName }

and ToolUseFinished =
    { ToolUseId : ToolUseId
      Outcome : ToolOutcome
      /// The block the call became, when it became one. Set means the block's own chip
      /// already says who ran what and how it went, so this draws nothing beside it — two
      /// renderings of one fact are free to disagree.
      Block : BlockId option }
