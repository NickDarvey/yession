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
      /// Whose turn this is: the party whose credentials it runs on (Plan 08 — the agent is
      /// the acting party and has no scope of its own), stated rather than derived.
      ///
      /// It used to be read off `CurrentMessage.Author` by everything that needed it, which
      /// was fine while every turn began with somebody speaking. A woken turn (Plan 20,
      /// stage 2) has nobody speaking and still has authority, so the thing three call sites
      /// actually wanted — WHOSE turn is this — says so itself.
      TurnActor      : ActorRef
      /// What was just said, when a turn began with somebody saying something. `None` for a
      /// turn nothing asked for: the agent was woken because work it started finished, and
      /// there is no message to point at. What moved arrives through `Terminals` either way.
      CurrentMessage : ConversationItem option
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
/// Where a command runs (Plan 15, stage 2). Before named sandboxes this was just "which
/// terminal, or the agent's own"; now "the agent's own" has to say WHICH sandbox's, because
/// a session has several and `execute_command` is still the only door into any of them.
type CommandTarget =
    /// A terminal that already exists — how a follow-up lands in the same shell, with the
    /// same working directory, as the command before it.
    | InTerminal of TerminalId
    /// A named WorkSandbox: the agent's terminal there, opened on first use. This is what
    /// makes a started sandbox usable rather than merely listed.
    | InSandbox of SandboxName

/// `background` (Plan 20, stage 2) asks for the command to be queued and left running: the
/// call answers as soon as there is something to say rather than waiting out the process, and
/// the completion becomes a wake instead of a returned outcome. It changes who WAITS and
/// nothing else — a background command is queued, editable and refusable exactly as every
/// other one is, and it runs through the same one door.
type ExecuteCommand = CommandTarget option -> string -> bool -> Async<Result<TerminalCommandOutcome, string>>

/// Where a COMMAND the agent asked for has got to (Plan 15, stage 3b). The same three shapes
/// `TerminalCommandStatus` has, minus the two that are about a process: a command has no pty
/// to be busy and no deadline of its own.
type CommandStatus =
    /// It ran, and this is what it said. The ordinary answer, and the only one an UNGATED
    /// command can give — which is every command until somebody configures otherwise.
    | CommandRan of string
    /// A human has to approve it. Unbounded in principle, so the tool yields a handle rather
    /// than holding the turn — `TerminalCommandAwaitingApproval`'s reasoning, unchanged.
    | CommandAwaitingApproval
    /// Somebody said no. Comes back to the model as an error rather than a silence, because
    /// a command that vanishes gets retried another way.
    | CommandRefusedBy of by: ActorRef * reason: string option

/// What one gated command answered with.
type CommandOutcome =
    { /// The handle that resumes it — the SAME `QueueId` a terminal command yields, which is
      /// what lets one `check_pending` serve both. `None` when nothing was ever pending: an
      /// ungated command has no entry to resume, and handing out a handle to something that
      /// already finished is an invitation to poll it.
      Handle : QueueId option
      Tool : string
      Summary : string
      Status : CommandStatus }

/// One command, as its gate needs to know it.
type GatedCall =
    { /// Which command — the catalogue value, not a bare name, so a gated call site cannot
      /// name something the settings surface does not render.
      Command : GatedCommand
      /// The arguments, encoded. What lets a process that did NOT propose the act still
      /// carry it out after an approval — the doc holds this, so a restart resumes instead
      /// of refusing. Opaque here: only the command's own dispatch entry reads it.
      Args : string
      /// The arguments as a human should READ them (`add_repo octo/hello`) — what the card
      /// shows and what a refusal records, rendered once here rather than three times
      /// downstream.
      Summary : string
      /// Who is asking. Always the agent today; `yession.yaml` will ask too.
      Author : ActorRef
      /// Whose credential it runs on, when that is not the author's own (Plan 08: the agent
      /// acts, the turn human's credential is used). Recorded on the act, because a restart
      /// has no turn to ask.
      OnBehalfOf : ActorRef option }

/// A command being carried out, as its dispatch entry sees it. Everything comes off the
/// pending act rather than out of a closure, which is what makes an approval survive the
/// process that proposed it.
type GatedInvocation =
    { Args : string
      OnBehalfOf : ActorRef option
      /// Who released it, when a gate held it. Goes onto the command's own event.
      ApprovedBy : ActorRef option }

/// How a command is actually carried out, by tool name. Built where the capabilities are
/// composed; read by the gate, including at boot for acts it never proposed.
type CommandDispatch = Map<string, GatedInvocation -> Async<Result<string, string>>>

/// Run a command through its approval gate (Plan 15, stage 3b). A capability rather than a
/// detail of the MCP adapter, so the declarative executor gets the same gate the agent does
/// instead of a second path around it.
/// No thunk: the gate looks the command up in the `CommandDispatch` and runs it from the
/// pending act's own fields. A closure would tie the act to the process that proposed it,
/// which is precisely the restart that used to lose it.
type RunGatedCommand = GatedCall -> Async<Result<CommandOutcome, string>>

/// What a handle resolved to. One tool, two shapes — the alternative being an agent that has
/// to know, before it asks, which kind of thing it is waiting on.
type PendingOutcome =
    | PendingTerminal of TerminalCommandOutcome
    | PendingCommand of CommandOutcome

/// Resume a handle `ExecuteCommand` or a gated command yielded (Plan 13, stage 3b; Plan 15,
/// stage 3b): an approval that arrived late, a long build. One verb, because the handle type
/// is one type.
type CheckPending = QueueId -> Async<Result<PendingOutcome, string>>

/// Type into a terminal whose source cannot be instrumented (Plan 19).
///
/// The agent's second hand, and it exists to keep the FIRST rule intact rather than to add a
/// second one. A device stream has no prompt to bootstrap, so it has no blocks — which means
/// `execute_command` has nothing to do there, and an agent that needs to talk to the thing on
/// the other end would otherwise have to use the provider's own write tool. That is a second
/// door onto one device, past the lease that arbitrates who is typing.
///
/// So this one takes the lease, exactly as a person does: the agent shows up in the holder
/// field a human shows up in, a human can steal it back mid-sentence, and every byte is in
/// the transcript everyone reads. Refused on an instrumented terminal, where the answer is
/// `execute_command` and the approval gate that comes with it.
type WriteTerminal = TerminalId -> string -> Async<Result<string, string>>

/// The tail of what a terminal has said, and how much of it was left out.
///
/// `Elided` is stated rather than silently dropped, for `TerminalCommandOutcome`'s reason: a
/// model that cannot tell a short output from a truncated one will confidently describe the
/// wrong thing.
type TerminalTail = { Text : string; Elided : int }

/// Read what a terminal with no blocks has said (Plan 19).
///
/// `write_terminal`'s other half, and it exists because the two halves are not symmetrical.
/// A WRITE has to be arbitrated — the lease is the whole point — but a read is not a second
/// writer, so this takes nothing and blocks nobody: a person typing keeps the terminal while
/// the agent reads over their shoulder, which is what sharing a device means.
///
/// Refused on an instrumented terminal, where a command IS a block: what a block printed
/// comes back from `execute_command`, and every block since the last turn is already in the
/// context pack. A second way to read the same bytes would be a second answer to one
/// question.
type ReadTerminal = TerminalId -> Async<Result<TerminalTail, string>>

/// The read-only repo verbs (Plan 14): clone-and-orient, NO mutation of history and NO
/// push — everything irreversible goes through `ExecuteCommand` in the WorkSandbox,
/// where the approval gate and the transcript already are. Git runs confined beside the
/// agent (the agent backend's sandbox family), and the clone URL is constructed from
/// the validated `owner/repo`, so no verb can name an arbitrary remote.

/// Clone a repo into the session's repos directory (a no-op returning the current state
/// when it is already there). The checkout is visible to every peer and to the
/// WorkSandbox from the moment it lands.
/// Answers with a `CommandOutcome` rather than the listing, because every MUTATING command
/// goes through its approval gate (Plan 15, stage 3b) and a gate has three answers, not one.
/// The rendering that used to live in the MCP adapter moved into the thunk the gate wraps —
/// which is where it has to be anyway, since what the gate carries is what the agent reads.
type AddRepo = RepoRef -> Async<Result<CommandOutcome, string>>

/// Switch a repo's checkout to a branch, optionally creating it. Local ref movement
/// only — never touches the remote.
type SwitchRepoBranch = RepoRef -> string -> bool -> Async<Result<CommandOutcome, string>>

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

/// Start (or get) one of the session's named WorkSandboxes (Plan 15, stage 2). ENSURE
/// semantics: the same name with the same forwarding hands back the one already running
/// and records nothing, so folding a declarative file into these commands at every boot
/// converges instead of accumulating. The same name with DIFFERENT forwarding is refused,
/// naming the difference — recreating would kill whatever is running inside it.
///
/// `forward` is a list of credential NAMES. Each resolves for the turn human (Plan 08
/// precedence) into that sandbox's environment; the value goes nowhere else, and the
/// event records which names and whose, never what.
type StartWorkSandbox = SandboxName -> string list -> Async<Result<CommandOutcome, string>>

/// Stop one, taking whatever is running in it down. The way to change a sandbox's
/// forwarding, and stated as such wherever the change is refused.
type StopWorkSandbox = SandboxName -> Async<Result<CommandOutcome, string>>

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
      CheckPending : CheckPending
      /// Type into a terminal that has no blocks to run a command in (Plan 19), under the
      /// same lease a person types under.
      WriteTerminal : WriteTerminal
      /// Read what such a terminal has said. Takes no lease: a reader is not a writer.
      ReadTerminal : ReadTerminal
      RunGated : RunGatedCommand
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
      ReadQuery : ReadQuery
      // Named WorkSandboxes (Plan 15, stage 2). Commands, so agent-only: a human asks,
      // and reads the act-line. `execute_command` is still the one door into a sandbox —
      // these decide which sandboxes exist, not what runs in them.
      StartWorkSandbox : StartWorkSandbox
      StopWorkSandbox : StopWorkSandbox
      /// Where every tool call this turn makes is recorded (Plan 16, part C). ONE seam,
      /// bound to the turn by whoever built these capabilities, and wrapped around the
      /// WHOLE registry rather than around each tool — so a provider added later cannot
      /// arrive with its own logging, or with none.
      ///
      /// It is a capability like the rest for the usual reason: a turn that must not be
      /// recorded is given a log that records nothing, rather than a flag somebody has to
      /// remember to check.
      RecordToolUse : ToolUseLog
      /// The tools of the MCP servers this session was given (Plan 17), one registry per
      /// connected server, ready to merge with the session's own.
      ///
      /// A LIST rather than a merged registry, because the merge belongs where the audit
      /// wrap does — around the whole, once. And a value rather than a thunk for the reason
      /// the whole record is resolved per turn: the turn holds a SNAPSHOT, so a set change
      /// mid-turn lands on the next turn and the model's tool list never shifts underneath
      /// it. A server that is down contributes nothing here and fails nothing.
      ForeignTools : ToolRegistry list }

module AgentCapabilities =

    /// A turn with no environment authority at all (Phase 1 behaviour).
    let none : AgentCapabilities =
        { ExecuteCommand = fun _ _ _ -> async { return Error "no terminal capability" }
          CheckPending = fun _ -> async { return Error "no terminal capability" }
          WriteTerminal = fun _ _ -> async { return Error "no terminal capability" }
          ReadTerminal = fun _ -> async { return Error "no terminal capability" }
          // A denial that still RUNS the command: the gate is a wrapper, and a session with
          // no collaborative state to park an act in must not lose the act.
          RunGated =
            fun call ->
                async {
                    return
                        Error (sprintf "no gate to run %s through in this session" call.Command.Tool)
                }
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
          ReadQuery = fun _ -> async { return Error "no query capability" }
          StartWorkSandbox = fun _ _ -> async { return Error "no sandbox capability" }
          StopWorkSandbox = fun _ -> async { return Error "no sandbox capability" }
          RecordToolUse = ToolUseLog.none
          ForeignTools = [] }

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

/// Whether the agent is owed a turn nobody asked for (Plan 20, stage 2).
///
/// The wake is a MAILBOX ITEM, not a callback: work finishing does not reach into a running
/// turn, it makes a turn due, and the scheduler that already drains one queue reads this the
/// same way it reads that one. Which is the only shape that composes with the rest of this
/// design — a callback would have to exist somewhere while no turn does, and would die with
/// the process that held it.
///
/// It carries NO payload, and that is what makes it cheap: an agent turn's context is built
/// from a page read before the turn appends its own `AgentTurnStarted`, so
/// `TerminalDigest.window` already reports every block that started or completed since the
/// previous turn. The wake decides WHEN a turn runs; the digest is what it then reads.
module AgentWake =

    /// Due iff some BACKGROUND block completed at or after the last `AgentTurnStarted`.
    ///
    /// The digest's own window trick, applied to scheduling: resetting on every
    /// `AgentTurnStarted` in the page leaves exactly what moved since the previous one, so no
    /// cursor is stored anywhere and a process that died between the completion and the turn
    /// re-derives the same pending wake from the log at boot. Restart-safety for free, rather
    /// than as a mechanism.
    ///
    /// Coalescing is free for the same reason: five commands finishing over two minutes make
    /// ONE wake, and everything that landed before the turn starts is inside that turn's
    /// digest window. A wake is idempotent by construction, which is what makes it safe to
    /// compute on every drain rather than deliver exactly once.
    ///
    /// A block that completed and was never `Background` does not wake anything: somebody was
    /// already waiting on it, and their tool call is what carries the outcome back.
    /// The actor a woken turn would run AS, or `None` when nothing is owed.
    ///
    /// One fold answering both halves, because they are one question: a turn that is due but
    /// has nobody to run as is not due. Every turn resolves its repo credential, its sandbox
    /// credential and its Claude account from whoever it is FOR, and a woken turn has no
    /// triggering message to read that from — so it carries the actor of the turn that queued
    /// the work, which the block recorded as `OnBehalfOf`. That invents no authority: it
    /// continues the one the queuing turn already had.
    ///
    /// A background block with no recorded owner therefore wakes nothing. That is the same
    /// safe direction an unreadable owner already takes elsewhere — run on nothing rather
    /// than on somebody else's credential — and the alternative, picking whoever spoke most
    /// recently, would run one person's work as another.
    let pending (events: SessionEvent list) : ActorRef option =
        events
        |> List.fold
            (fun (background: Map<string, ActorRef>, woken: ActorRef option) event ->
                match event with
                // A new turn takes everything before it: whatever those blocks did, that
                // turn's digest reported it.
                | AgentTurnStarted _ -> Map.empty, None
                | SessionEvent.TerminalBlockStarted b when b.Background ->
                    match b.OnBehalfOf with
                    | Some owner -> Map.add (BlockId.value b.BlockId) owner background, woken
                    | None -> background, woken
                | SessionEvent.TerminalBlockCompleted b ->
                    // The FIRST owed turn wins, and the rest coalesce into it: they are all
                    // reported by the digest of whichever turn runs, so a second wake would
                    // be a second turn reading the same window.
                    let owner = Map.tryFind (BlockId.value b.BlockId) background
                    background, (match woken with Some _ -> woken | None -> owner)
                | _ -> background, woken)
            (Map.empty, None)
        |> snd

    /// Whether a turn is owed at all. `pending` says who; this says whether, for the readers
    /// that only need the question answered.
    let due (events: SessionEvent list) : bool = pending events |> Option.isSome
