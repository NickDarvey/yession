namespace Yession.Domain.Terminals

open Yession.Domain

/// The facts a terminal records — opening and closing, who holds its stdin, the blocks it ran, and what its transcript could not keep.
///
/// These sit BELOW `SessionEvent`, because the union names them, while the
/// projections that fold that union sit above it — so Terminals spans the event
/// spine rather than living on one side of it.
type TerminalOpened =
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
      Sandbox : SandboxRef option
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
      /// The three parties behind the command: who wrote it, whose credential it ran on when
      /// that was not their own, and who released it when the terminal's mode required an
      /// approval. One value rather than three fields, because they are one question — and
      /// because the answer to its middle third went missing here once (Plan 20).
      ///
      /// The owner matters beyond the audit: a WOKEN turn has no triggering message to
      /// resolve its authority from, and the log is the only thing it can read. Absent means
      /// no turn can be woken by this block — an unresolvable owner runs on NOTHING rather
      /// than on somebody else's credential.
      Authority : Authority
      /// The command line, snapshotted from the collaborative draft at drain time and
      /// immutable thereafter — exactly as `MessageSent` snapshots a message body.
      Command : string
      /// The transcript line index at which this block's output begins.
      FromSeq : int
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
