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
      /// What the session's terminals did since the previous turn (Plan 13, stage 3a).
      /// A SEPARATE field, deliberately: a command someone ran is not something someone
      /// said, so folding blocks into `Conversation` would make the chat log a place
      /// where machine output accumulates. The agent needs both; the conversation stays
      /// a conversation.
      Terminals      : TerminalBlockDigest list
      SystemPrompt   : string }

type AgentResponseChunk = { Text : string }

/// Token/cache usage the runner reports for one completed turn (Plan 04, Step 28).
/// Telemetry only — never a durable session fact and never written to the event log.
/// `None` on `AgentCompleted` when the runner reports no usage (scripted runners, or an
/// SDK result with no usage block).
type AgentUsage =
    { InputTokens         : int
      OutputTokens        : int
      CacheReadTokens     : int
      CacheCreationTokens : int
      Model               : string option }

type AgentRunResult =
    | AgentCompleted of body: string * usage: AgentUsage option
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

/// Persist a secret under the session's own scope (Plan 06). WRITE-ONLY from the
/// agent's side: there is no capability that returns a value — a stored secret is used
/// by referencing its name in an environment spec (`SecretRef`), resolved at sandbox
/// spawn straight into the sandbox env, never through the agent loop or the
/// transcript.
type SetSessionSecret = SecretName -> string -> Async<Result<SecretMetadata, string>>

/// List the session's secret METADATA — names and timestamps, never values (the type
/// cannot carry one).
type ListSessionSecrets = unit -> Async<Result<SecretMetadata list, string>>

/// Delete one of the session's secrets; false = it did not exist.
type DeleteSessionSecret = SecretName -> Async<Result<bool, string>>

/// What queueing a terminal command did (Plan 13).
type QueuedTerminalCommand =
    { Terminal : TerminalId
      /// Whether the terminal's approval mode is holding it for a human. The agent is
      /// TOLD this rather than left to infer it from silence: "your command is waiting for
      /// someone to approve it" is the difference between a useful answer and a model
      /// deciding its command failed and trying something else.
      AwaitingApproval : bool }

/// Put a command in a terminal's queue (Plan 13). Named for what it does: the agent does
/// NOT get to run a command in a terminal — it queues one, exactly as a person does, and
/// the terminal's approval mode decides what happens next.
///
/// It returns as soon as the command is queued, and deliberately does not wait for the
/// command to run. Waiting would make an agent turn block on a human pressing Approve,
/// which turns a review gate into a deadlock whenever nobody is looking.
///
/// `TerminalId option`: `None` means "whichever terminal is open", opening one if none is.
type QueueTerminalCommand = TerminalId option -> string -> Async<Result<QueuedTerminalCommand, string>>

/// The typed capabilities an agent turn may use. No raw Docker, no handles, no session
/// ids — everything is already scoped by the Session Process and, beneath it, the
/// Session Manager.
type AgentCapabilities =
    { EnsureEnvironment : EnsureEnvironment
      ExecuteCommand : ExecuteCommand
      SetSecret : SetSessionSecret
      ListSecrets : ListSessionSecrets
      DeleteSecret : DeleteSessionSecret
      /// Queue a command in a terminal (Plan 13), where people can see it, edit it, and —
      /// depending on the terminal's mode — approve it before it runs.
      QueueTerminalCommand : QueueTerminalCommand }

module AgentCapabilities =

    /// A turn with no environment authority at all (Phase 1 behaviour).
    let none : AgentCapabilities =
        { EnsureEnvironment = fun _ -> async { return EnvironmentUnavailable "no environment capability" }
          ExecuteCommand = fun _ _ -> async { return CommandExecutionFailed "no environment capability" }
          SetSecret = fun _ _ -> async { return Error "no secrets capability" }
          ListSecrets = fun () -> async { return Error "no secrets capability" }
          DeleteSecret = fun _ -> async { return Error "no secrets capability" }
          QueueTerminalCommand = fun _ _ -> async { return Error "no terminal capability" } }

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
