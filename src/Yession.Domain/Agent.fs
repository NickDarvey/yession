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
/// agent never sees container handles or engines. Output streams through the chunk
/// callback (it is also recorded as events by the Session Process).
type ExecuteCommand = CommandRequest -> (CommandOutputChunk -> unit) -> Async<CommandResult>

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
          ExecuteCommand = fun _ _ -> async { return CommandExecutionFailed "no environment capability" } }

/// The abort seam (Phase 3, Step 17): how an interrupt reaches a running turn. The
/// Session Process owns the signal; the runner observes it — poll `IsAborted` at
/// yield points and/or register `OnAbort` to cancel promptly (e.g. an SDK
/// AbortController). Once aborted, the turn's result is ignored: the terminal fact is
/// the `AgentTurnInterrupted` event the Process already appended.
type AgentAbortSignal =
    { IsAborted : unit -> bool
      /// Register a callback fired when the turn is interrupted; fired immediately if
      /// it already was.
      OnAbort : (unit -> unit) -> unit }

module AgentAbortSignal =

    /// A signal that never aborts — for turns nothing can interrupt (tests, one-shots).
    let none : AgentAbortSignal =
        { IsAborted = (fun () -> false)
          OnAbort = ignore }

/// Run one agent turn: `onChunk` is invoked with each streamed chunk in order, and the
/// async resolves with the final result once the stream ends. Implementations must not
/// throw for agent-level errors — failures are values (`AgentFailed`), because the
/// Session Process represents them as events, not exceptions. The abort signal may end
/// the turn early; a well-behaved runner returns promptly once it fires.
type RunAgent = AgentContextPack -> AgentCapabilities -> AgentAbortSignal -> (AgentResponseChunk -> unit) -> Async<AgentRunResult>
