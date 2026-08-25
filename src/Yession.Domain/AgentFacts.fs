namespace Yession.Domain.Agent

open Yession.Domain

/// The facts an agent turn records — why it woke, what context it was given, what it said, and how it ended.
///
/// These sit BELOW `SessionEvent`, because the union names them, while the
/// projections that fold that union sit above it — so Agent spans the event
/// spine rather than living on one side of it.
type AgentTurnStarted =
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
/// every turn's does — `BlockDigest` for terminal work, the tool roster for a roster
/// change — so a wake carries the REASON and nothing else. A reason that carried results
/// would be a second channel into the agent's context, free to disagree with the first.
///
/// A roster change is deliberately NOT one of these. Every reason here answers "whose work
/// finished" and takes its actor from the party who queued it — which is what makes a woken
/// turn a turn with credentials. A tool list changing queues nothing and belongs to nobody;
/// the session records it as `ActorRef.System`, and `System` is not a credential the agent can
/// call tools on. Borrowing the last turn's actor instead would make it the first wake whose
/// actor did not queue the work — the agent calling a newly-appeared tool on somebody's
/// credentials for something they never asked for. And the next turn rebuilds its roster
/// regardless, so the wake would buy promptness, not correctness.

and WakeReason =
    /// A command the agent asked to run in the background finished while it was not running.
    | CommandFinished
    /// A terminal whose bytes came from a stream somebody else produces (Plan 16, part D)
    /// closed. The agent was reading a source that is now gone, and no tool call it made is
    /// still open to tell it so.
    | StreamEnded of TerminalId
    /// A terminal's shell stopped answering our marks (Plan 13, stage 2f) while the agent had
    /// a block open there. This is the one wake that reports the agent being STUCK rather than
    /// something finishing: from here the Process cannot tell when that block ended, so the
    /// queue behind it is held and nothing else will arrive to say why.
    | IntegrationLost of TerminalId

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

and [<RequireQualifiedAccess>] AgentTurnFailed =
    { AgentTurnId : AgentTurnId
      Reason : string }

and AgentTurnInterrupted =
    { AgentTurnId : AgentTurnId
      RequestedBy : PeerId }
