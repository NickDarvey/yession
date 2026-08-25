namespace Yession.Domain.Chat

open Yession.Domain

/// The facts a conversation records — a message drained from the queue and sent, and a command the gate refused.
///
/// These sit BELOW `SessionEvent`, because the union names them, while the
/// projections that fold that union sit above it — so Chat spans the event
/// spine rather than living on one side of it.
type MessageSent =
    { MessageId : MessageId
      /// The queue entry this message was consumed from (Phase 3): the durable link
      /// from doc-world to event-world, and the drain's exactly-once dedup key.
      /// `None` for messages that predate the queue.
      QueueId : QueueId option
      Author : ActorRef
      Body : string }
/// A command refused at its gate (Plan 15, stage 3; Plan 23: the gate is the classifier).
/// The mirror of `TerminalCommandRejected`, and it exists for that event's reason: a refusal
/// that simply vanishes is indistinguishable from a bug — to anyone reading the record and
/// to the model, which will otherwise try the same thing another way.

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
