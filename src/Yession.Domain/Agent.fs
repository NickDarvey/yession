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

/// Where a command the agent asked for has got to (Plan 13, stage 3b).
///
/// Every case NAMES its own state, and that is the point rather than a nicety. Telling a
/// model "queued" when it is actually blocked on a person has it conclude, after a silent
/// pause, that its command failed and try something else — which is how a review gate turns
/// into the agent routing around the review.
type TerminalCommandStatus =
    /// It ran to an outcome. The ordinary answer.
    | TerminalCommandRan of CommandResult
    /// Still going when the deadline fell. A yield, not a cancellation: the block runs on and
    /// the handle resumes it.
    | TerminalCommandRunning
    /// A human has to approve it. Unbounded in principle — they may be asleep — so the tool
    /// returns rather than waits, and the turn says what it queued and why.
    | TerminalCommandAwaitingApproval
    /// The terminal is not free: a peer is typing in it, or another block is running there.
    /// Distinct from awaiting approval because it resolves differently — one ends when a
    /// person makes a decision, the other when a person or a process finishes a task.
    | TerminalCommandAwaitingTerminal
    /// Somebody said no. The other half of the approval gate, and the more interesting half.
    | TerminalCommandRefused of by: ActorRef * reason: string option

/// What one `execute_command` answered with (Plan 13, stage 3b).
type TerminalCommandOutcome =
    { Terminal : TerminalId
      /// The handle that resumes this command, and it is the QUEUE entry's id rather than the
      /// block's — deliberately, because a block does not exist until the command runs, so a
      /// block-id handle could not be returned by the two cases that most need one
      /// (`AwaitingApproval`, `AwaitingTerminal`). The queue id names the REQUEST, which
      /// exists from the moment it is visible to everyone.
      Handle : QueueId
      /// The block, once there is one. `None` while the command is still only a request.
      Block : BlockId option
      Status : TerminalCommandStatus
      /// The tail of what it printed, capped. The transcript keeps all of it, and the block's
      /// range travels with the handle, so nothing here is the only copy.
      OutputTail : string
      /// Characters the tail leaves out. Stated rather than silently elided: a model that
      /// cannot tell a short output from a truncated one will confidently describe the wrong
      /// thing.
      Elided : int }

/// Run a command for the agent (Plan 13, stage 3b). ONE door: the agent has no private
/// execution path, so a terminal set to require approval actually holds it.
///
/// `command` is a shell command LINE, not an executable plus argv. A terminal block is a line
/// a human reads in a queue and may edit before approving, and an argv array is not that; the
/// quoting burden moves to the side that knows what it meant.
///
/// `TerminalId option`: `None` means the session's agent terminal, opened on first use.
///
/// It waits, bounded twice over — a short grace for an approval, then the command timeout for
/// the process — and yields a handle rather than blocking a turn on a human. See
/// `TerminalCommandWait` for the policy.
type ExecuteCommand = TerminalId option -> string -> Async<Result<TerminalCommandOutcome, string>>

/// Resume a handle `ExecuteCommand` yielded (Plan 13, stage 3b): an approval that arrived
/// late, a long build. Returns the same shape, so the agent learns one thing rather than two.
type ReadTerminalBlock = QueueId -> Async<Result<TerminalCommandOutcome, string>>

/// The read-only repo verbs (Plan 14): clone-and-orient, NO mutation of history and NO
/// push — everything irreversible goes through `ExecuteCommand` in the WorkSandbox,
/// where the approval gate and the transcript already are. Git runs confined beside the
/// agent (the agent backend's sandbox family), and the clone URL is constructed from
/// the validated `owner/repo`, so no verb can name an arbitrary remote.

/// Clone a repo into the session's repos directory (a no-op returning the current state
/// when it is already there). The checkout is visible to every peer and to the
/// WorkSandbox from the moment it lands.
type AddRepo = RepoRef -> Async<Result<RepoListing, string>>

/// Switch a repo's checkout to a branch, optionally creating it. Local ref movement
/// only — never touches the remote.
type SwitchRepoBranch = RepoRef -> string -> bool -> Async<Result<RepoListing, string>>

/// Fetch a repo's remote refs (prune, no submodules). The one network verb besides the
/// clone itself; runs on the same per-invocation credential.
type FetchRepo = RepoRef -> Async<Result<string, string>>

/// A read-only look at a checkout — status, log, or diff — rendered as text, capped.
type InspectRepo = RepoRef -> Async<Result<string, string>>

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

/// Answer one of the session's registered queries (Plan 15). The agent reaches the SAME
/// registry the humans' settings surface streams from — that is the whole point of a
/// query being a declaration rather than a tool body: one declaration, two audiences, no
/// chance of the two being told different things.
type ReadQuery = QueryName -> Async<Result<QueryValue, string>>

/// The typed capabilities an agent turn may use. No raw Docker, no handles, no session
/// ids — everything is already scoped by the Session Process and, beneath it, the
/// Session Manager.
///
/// `EnsureEnvironment` retired with stage 3b: it existed to start the environment lazily
/// before a command, and opening a terminal already does that — so it had nothing left to do.
/// Its `reason` argument survives as the agent terminal's TITLE, which is a better answer to
/// "what is that terminal for" than the tool ever gave.
type AgentCapabilities =
    { /// Run a command where the people in this session can see it (Plan 13, stage 3b).
      /// The agent's ONLY execution path — that is what makes the approval gate real.
      ExecuteCommand : ExecuteCommand
      /// Resume a handle `ExecuteCommand` yielded.
      ReadTerminalBlock : ReadTerminalBlock
      SetSecret : SetSessionSecret
      ListSecrets : ListSessionSecrets
      DeleteSecret : DeleteSessionSecret
      // The repo verbs (Plan 14): read-only bootstrap — clone and orient. Commit/push
      // stay behind ExecuteCommand, which is what keeps the one-door invariant intact.
      AddRepo : AddRepo
      SwitchRepoBranch : SwitchRepoBranch
      FetchRepo : FetchRepo
      RepoStatus : InspectRepo
      RepoLog : InspectRepo
      RepoDiff : InspectRepo
      /// The session's read-only queries (Plan 15), declared once and surfaced to the
      /// agent as generated MCP tools. Data rather than a thunk: the runner needs the
      /// declarations to BUILD the tools, before any of them is called. `list_repos` used
      /// to sit above as its own capability and is now the `repos` query — one place, and
      /// the humans see the same answer without asking.
      Queries : QueryDef list
      ReadQuery : ReadQuery }

module AgentCapabilities =

    /// A turn with no environment authority at all (Phase 1 behaviour).
    let none : AgentCapabilities =
        { ExecuteCommand = fun _ _ -> async { return Error "no terminal capability" }
          ReadTerminalBlock = fun _ -> async { return Error "no terminal capability" }
          SetSecret = fun _ _ -> async { return Error "no secrets capability" }
          ListSecrets = fun () -> async { return Error "no secrets capability" }
          DeleteSecret = fun _ -> async { return Error "no secrets capability" }
          AddRepo = fun _ -> async { return Error "no repos capability" }
          SwitchRepoBranch = fun _ _ _ -> async { return Error "no repos capability" }
          FetchRepo = fun _ -> async { return Error "no repos capability" }
          RepoStatus = fun _ -> async { return Error "no repos capability" }
          RepoLog = fun _ -> async { return Error "no repos capability" }
          RepoDiff = fun _ -> async { return Error "no repos capability" }
          Queries = []
          ReadQuery = fun _ -> async { return Error "no query capability" } }

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
