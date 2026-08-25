namespace Yession.Domain.Tools

open Yession.Domain

/// The facts a tool use records — a call started, how it ended, and whether an MCP server was reachable.
///
/// These sit BELOW `SessionEvent`, because the union names them, while the
/// projections that fold that union sit above it — so Tools spans the event
/// spine rather than living on one side of it.

/// Whether the CALL happened, which is not whether it went well. A command that exits 1
/// succeeded as a tool call — the model is meant to read that and choose differently.
/// `ToolCallFailed` means the call never reached a tool at all: no such tool, or arguments
/// that could not be read.

type ToolOutcome =
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
