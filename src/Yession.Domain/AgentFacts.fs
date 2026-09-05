namespace Yession.Domain.Agent

open Yession.Domain
open Yession.Domain.Prs

/// The facts an agent turn records — why it woke, what context it was given, what it said, and how it ended.
///
/// These sit BELOW `SessionEvent`, because the union names them, while the
/// projections that fold that union sit above it — so Agent spans the event
/// spine rather than living on one side of it.
type AgentTurnStarted =
    { AgentTurnId : AgentTurnId
      /// Why this turn exists — exactly one cause, the durable form of `TurnTrigger`, the
      /// runtime value the scheduler builds a turn from. A turn with no recorded cause is
      /// not a state this event can hold: the two options this replaced could both be `None`
      /// at once — an unauditable turn the type permitted and the emit never meant.
      Cause : TurnCause }

/// The one thing that started a turn: a message someone sent, or an event that fired while
/// nobody was speaking. Exactly the two arms of `TurnTrigger` (`Agent.fs`), kept durable —
/// so the event that records a turn is 1:1 with the value that raised it, rather than a
/// looser re-encoding of it.
///
/// `TriggeredBy` is the audit link from a turn back to what prompted it, and the ref a
/// reader follows to that message. `Woke` carries the reason and nothing else — durable
/// because an agent that acts unprompted must be able to SAY why on every surface that
/// shows what it did, an unexplained turn in a shared session reading as the agent deciding
/// on its own, which is the one thing it must never look like.
and TurnCause =
    | TriggeredBy of MessageId
    | Woke of WakeReason

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
    /// A pull request somebody here watches changed while the agent was not running.
    ///
    /// This is the one reason that is not "whose work finished", and it qualifies for the
    /// reason a roster change does not: a WATCH was started by an attributed party, and the
    /// poll that noticed spent that party's own credential. So the wake has an owner who
    /// asked for exactly this, which is what the paragraph above requires — where a roster
    /// change belongs to `System` and to nobody.
    ///
    /// Attribution, never payload, like the rest: what changed arrives as the timeline note
    /// the transition already folded into, so the reason names only which pull request.
    | PrChanged of PrRef

and AgentContextBuilt =
    { AgentTurnId : AgentTurnId
      MessageCount : int }

and AgentMessageStarted =
    { AgentTurnId : AgentTurnId
      MessageId : MessageId
      /// The message this one follows within the same turn — what the turn said before it
      /// went off to call a tool. `None` for the turn's first message, whose cause is the
      /// turn itself and is on `AgentTurnStarted`.
      ///
      /// The link is also the CLOSE: a message that names an antecedent is the model having
      /// moved on, so the antecedent is complete at what it streamed. Nothing else says so —
      /// `AgentMessageCompleted` is the turn's last word and two state machines read it as
      /// the turn ending, so a mid-turn message cannot borrow it.
      Antecedent : MessageId option }

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
