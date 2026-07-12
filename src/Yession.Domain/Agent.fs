namespace Yession.Domain

/// The agent-turn capability vocabulary (Step 08). The Session Process runs an agent
/// turn against the *projection-derived* conversation — never Yjs/draft state — and the
/// response comes back as streamed chunks plus a final result, which the Process turns
/// into events. The runner is a capability so the real Claude Agent SDK adapter and a
/// deterministic scripted runner are interchangeable (docs/design.md §1 "Capabilities
/// are scoped, not ambient", "Verification is automated end-to-end").

/// Everything the agent is given for one turn. Phase 1: no tools, no environment.
type AgentContextPack =
    { SessionId      : SessionId
      Conversation   : ConversationItem list
      CurrentMessage : ConversationItem
      SystemPrompt   : string }

type AgentResponseChunk = { Text : string }

type AgentRunResult =
    | AgentCompleted of body: string
    | AgentFailed of reason: string

type EnsureEnvironmentResult =
    | EnvironmentAvailable
    | EnvironmentUnavailable of reason: string

/// Ask the Session Process to make sure an environment exists for the session (Step 12).
/// Lazy by design: calling this is the agent *signalling need*; a conversational answer
/// never calls it, so a one-shot never starts a container.
type EnsureEnvironment = string -> Async<EnsureEnvironmentResult>

/// Run a command in the session's environment (Step 13). Typed and pre-scoped — the
/// agent never sees container handles or engines.
type ExecuteCommand = CommandRequest -> Async<CommandResult>

/// The typed capabilities an agent turn may use. No raw Docker, no handles, no session
/// ids — everything is already scoped by the Session Process and, beneath it, the
/// Session Manager.
type AgentCapabilities =
    { EnsureEnvironment : EnsureEnvironment
      ExecuteCommand : ExecuteCommand }

module AgentCapabilities =

    /// A turn with no environment authority at all (Phase 1 behaviour).
    let none : AgentCapabilities =
        { EnsureEnvironment = fun _ -> async { return EnvironmentUnavailable "no environment capability" }
          ExecuteCommand = fun _ -> async { return CommandExecutionFailed "no environment capability" } }

/// Run one agent turn: `onChunk` is invoked with each streamed chunk in order, and the
/// async resolves with the final result once the stream ends. Implementations must not
/// throw for agent-level errors — failures are values (`AgentFailed`), because the
/// Session Process represents them as events, not exceptions.
type RunAgent = AgentContextPack -> AgentCapabilities -> (AgentResponseChunk -> unit) -> Async<AgentRunResult>
