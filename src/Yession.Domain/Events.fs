namespace Yession.Domain

open System
open Yession.Domain.Terminals
open Yession.Domain.Agent
open Yession.Domain.Chat
open Yession.Domain.Sandboxes
open Yession.Domain.Tools
open Yession.Domain.Link
open Yession.Domain.Repos

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
    // Command lifecycle, retired. Nothing appends these any more: agent commands became
    // terminal blocks, whose output belongs in the transcript sidecar rather than in the
    // log a second time (`Environment.fs`, `Transcript.fs`). The cases stay because a
    // persisted log written before that change still contains them — deleting them would
    // make an existing session unreadable, not tidy the union.
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
    // A declaration that did NOT become a sandbox (Plan 27). The sibling above announces the
    // starts; until this, only the starts were announced — so a file with a typo in it read
    // on the timeline exactly like a file nobody had written.
    | RepoConfigRefused of RepoConfigRefused
    // The shell profile (Plan 25): where a shell opened in one sandbox starts. One event
    // for set and for clear, because "what does a new terminal do" has one answer at a
    // time — a second verb would let the two disagree about which was last.
    | ShellProfileSet of ShellProfileSet
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

and [<RequireQualifiedAccess>] SessionCreated =
    { SessionId : SessionId }


















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





















