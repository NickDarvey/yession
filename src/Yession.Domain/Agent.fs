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
      /// Which model to run this turn on, when the session has picked one. `None` means
      /// "whatever the provider would have chosen", which is the honest default: no
      /// deployment here knows a provider's current pick, and inventing a fixed id would
      /// pin every session to a model that ages.
      ///
      /// It rides the context pack rather than the runner because it is a fact about THIS
      /// turn, re-read from the collaborative register each time — so a person changing it
      /// mid-session changes the next turn, with nothing to relaunch.
      Model          : ModelId option
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
/// pause, that its command failed and try something else — which is how it routes around
/// whatever was holding it.
type TerminalCommandStatus =
    /// It ran to an outcome. The ordinary answer.
    | TerminalCommandRan of CommandResult
    /// Still going when the deadline fell. A yield, not a cancellation: the block runs on and
    /// the handle resumes it.
    | TerminalCommandRunning
    /// It took the whole screen and is waiting for a keystroke, and the terminal is yours
    /// (Plan 20, stage 6). Its own case because it is the one running block that will never
    /// finish on its own: burning the process deadline on it and then reporting
    /// `TerminalCommandRunning` says "be patient" about a thing that is waiting for the
    /// caller. Returned the moment detection hands the terminal over, deadline or no.
    | TerminalCommandInteractive
    /// The terminal is not free: a peer is typing in it, or another block is running there.
    /// Ends when a person or a process finishes a task.
    | TerminalCommandAwaitingTerminal
    /// The classifier said no (Plan 23). An answer, not an error: it names who and why, so
    /// it is not retried another way.
    | TerminalCommandRefused of by: ActorRef * reason: string option

/// What one `execute_command` answered with (Plan 13, stage 3b).
type TerminalCommandOutcome =
    { Terminal : TerminalId
      /// The handle that resumes this command, and it is the QUEUE entry's id rather than the
      /// block's — deliberately, because a block does not exist until the command runs, so a
      /// block-id handle could not be returned by the case that most needs one
      /// (`AwaitingTerminal`). The queue id names the REQUEST, which exists from the moment
      /// it is visible to everyone.
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
/// execution path, so the classifier that gates this gates everything the agent runs.
///
/// `command` is a shell command LINE, not an executable plus argv. A terminal block is a line
/// a human reads in a queue and may edit before it runs, and an argv array is not that; the
/// quoting burden moves to the side that knows what it meant.
///
/// `TerminalId option`: `None` means the session's agent terminal, opened on first use.
///
/// It waits out the command timeout and yields a handle rather than blocking a turn on the
/// work. See `TerminalCommandWait` for the policy.
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

/// One request to run a command (Plan 20). A record rather than positional arguments, because
/// this is the one door and every capability the agent gains arrives here — `background` came
/// in at stage 2, and a bare `None "npm test" false` at the call site was already the shape
/// nobody can read.
type CommandRequest =
    { /// The shell command line.
      Command : string
      /// Where to run it. `None` is the agent's own terminal in the default sandbox.
      Target : CommandTarget option
      /// Queue it and leave it running (Plan 20, stage 2): the call answers as soon as there
      /// is something to say rather than waiting out the process, and the completion becomes a
      /// wake instead of a returned outcome. It changes who WAITS and nothing else — a
      /// background command is queued, editable and refusable exactly as every other one is,
      /// and it runs through the same one door.
      Background : bool }

module CommandRequest =

    /// The plain case: a command, waited for, in the default sandbox's agent terminal.
    let ofCommand (command: string) : CommandRequest =
        { Command = command; Target = None; Background = false }

type ExecuteCommand = CommandRequest -> Async<Result<TerminalCommandOutcome, string>>

/// Where a COMMAND the agent asked for has got to (Plan 15, stage 3b; Plan 23). The shapes
/// `TerminalCommandStatus` has, minus the ones that are about a process: a command has no
/// pty to be busy and no screen to take.
type CommandStatus =
    /// It ran, and this is what it said. The ordinary answer.
    | CommandRan of string
    /// STILL GOING when the deadline fell. `TerminalCommandRunning`'s counterpart: the
    /// deadline bounds waiting on the WORK, and an unfinished command is a yield, not a
    /// cancellation — the command runs on and the handle resumes it.
    | CommandRunning
    /// Somebody said no. Comes back to the model as an error rather than a silence, because
    /// a command that vanishes gets retried another way.
    | CommandRefusedBy of by: ActorRef * reason: string option

/// What one gated command answered with.
type CommandOutcome =
    { /// The handle that resumes it — the SAME `QueueId` a terminal command yields, which is
      /// what lets one `check_pending` serve both.
      Handle : QueueId option
      Tool : string
      Summary : string
      Status : CommandStatus }

/// One command, as its gate needs to know it.
type GatedCall =
    { /// Which command, by its MCP tool name: what the model calls, what the classifier
      /// reads, and what a refusal records. The dispatch table and every call site live in
      /// one file (`app/Commands.fs`), so the name is agreed where it is used.
      Tool : string
      /// The arguments, encoded. Opaque here: only the command's own dispatch entry reads it.
      Args : string
      /// The arguments as a human should READ them (`add_repo octo/hello`) — what the audit
      /// shows and what a refusal records, rendered once here rather than three times
      /// downstream.
      Summary : string
      /// Who is asking, and on whose authority (Plan 08: the agent acts, the turn human's
      /// credential is used). One value, so a call cannot be built with an author and no
      /// authority.
      Authority : Authority }

/// A command being carried out, as its dispatch entry sees it.
type GatedInvocation =
    { Args : string
      /// The acting parties. `Authority.effective` is what a dispatch entry asks for whose
      /// credential to run on.
      Authority : Authority }

/// How a command is actually carried out, by tool name. Built where the capabilities are
/// composed; read by the gate.
type CommandDispatch = Map<string, GatedInvocation -> Async<Result<string, string>>>

/// Run a command through its gate (Plan 15, stage 3b; Plan 23: the gate is the classifier).
/// A capability rather than a detail of the MCP adapter, so the declarative executor gets
/// the same gate the agent does instead of a second path around it.
type RunGatedCommand = GatedCall -> Async<Result<CommandOutcome, string>>

/// What a handle resolved to. One tool, two shapes — the alternative being an agent that has
/// to know, before it asks, which kind of thing it is waiting on.
type PendingOutcome =
    | PendingTerminal of TerminalCommandOutcome
    | PendingCommand of CommandOutcome

/// Resume a handle `ExecuteCommand` or a gated command yielded (Plan 13, stage 3b; Plan 15,
/// stage 3b): a long build, a held terminal, a program waiting on a keystroke. One verb,
/// because the handle type is one type.
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
/// `execute_command` and the classifier that gates it.
type WriteTerminal = TerminalId -> string -> Async<Result<string, string>>

/// A window of what a terminal has said, and where in its recording that window sits.
///
/// It carries its own bounds because a reader that cannot say WHERE it read cannot read on.
/// The tail answers "what is it saying now"; everything else worth asking — what did this say
/// before I arrived, what did it say between these two lines — is the same question asked
/// from a different line, and `Through` is what makes asking it possible.
type TerminalTail =
    { Text : string
      /// Characters left out of the START of this window. Non-zero only on a TAIL, which is
      /// bounded by characters so it fits a context window and says what it dropped. A PAGE
      /// is bounded by where it STOPPED and says so with `Through` instead — a page that
      /// elided its own middle would hand back a cursor skipping lines nobody was told about.
      ///
      /// Stated rather than silently dropped, for `TerminalCommandOutcome`'s reason: a model
      /// that cannot tell a short output from a truncated one will confidently describe the
      /// wrong thing.
      Elided : int
      /// The first transcript line this window covers.
      From : int
      /// One past the last line it covers. Pass it back as `from` to read on.
      Through : int
      /// One past the last line the terminal has. `Through = Length` means this window
      /// reaches the live edge, and is the only way a reader can tell that it does.
      Length : int }

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
///
/// `from` is a line a previous read handed back, or `None` for the tail. Reading an hour back
/// is not more of a claim on the device than reading the last line — it is the same read from
/// a different place, which is why it is an argument rather than a second verb.
type ReadTerminal = TerminalId -> int option -> Async<Result<TerminalTail, string>>

/// One terminal, as the AGENT is told about it (Plan 20, stage 3). What a person reads off a
/// row in the list, in the shape a model reads — the same facts, because they are looking at
/// the same thing.
type TerminalSummary =
    { Terminal : TerminalId
      /// What it is FOR. The agent names its own; a person's carries whatever they opened it
      /// as.
      Name : string
      /// Which WorkSandbox its shell lives in. `None` for a stream somebody else produces.
      Sandbox : SandboxName option
      /// Whether the agent opened it. Not a permission — it is what the agent needs in order
      /// to know which of these are its own to close.
      Mine : bool
      /// Whether a command is running in it right now.
      Busy : bool }

/// Open a terminal for the agent's own use (Plan 20, stage 3): the same verb a person has,
/// over the same terminals. `name` is what it is for, and becomes the title everyone reads.
///
/// Refuses, with the limit named, when the sandbox already has as many as the agent may hold.
/// A refusal rather than a queue: the agent is the only party who can decide which of its own
/// terminals is finished with, so the answer has to reach it where it can act on it.
type OpenTerminal = string -> SandboxName option -> Async<Result<TerminalId, string>>

/// Close one of the agent's own terminals. Refuses on a terminal a PERSON opened: a human
/// typing in their shell is not the agent's to end, and the list gives them the same verb over
/// the agent's if they want it.
type CloseTerminal = TerminalId -> Async<Result<unit, string>>

/// Every terminal this session has, so the agent knows what it can use rather than guessing
/// from whichever blocks happened to land in its last digest.
type ListTerminals = unit -> Async<Result<TerminalSummary list, string>>

/// The read-only repo verbs (Plan 14): clone-and-orient, NO mutation of history and NO
/// push — everything irreversible goes through `ExecuteCommand` in the WorkSandbox,
/// where the classifier and the transcript already are. Git runs confined beside the
/// agent (the agent backend's sandbox family), and the clone URL is constructed from
/// the validated `owner/repo`, so no verb can name an arbitrary remote.

/// Clone a repo into the session's repos directory (a no-op returning the current state
/// when it is already there). The checkout is visible to every peer and to the
/// WorkSandbox from the moment it lands.
/// Answers with a `CommandOutcome` rather than the listing, because every MUTATING command
/// goes through its gate (Plan 15, stage 3b) and a gate has more than one answer.
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
      /// The agent's ONLY execution path — that is what makes the classifier's gate real.
      ExecuteCommand : ExecuteCommand
      /// Resume a handle `ExecuteCommand` yielded.
      CheckPending : CheckPending
      /// Type into a terminal that has no blocks to run a command in (Plan 19), under the
      /// same lease a person types under.
      WriteTerminal : WriteTerminal
      /// Read what such a terminal has said. Takes no lease: a reader is not a writer.
      ReadTerminal : ReadTerminal
      /// The terminal verbs a person already has (Plan 20, stage 3), so the agent can hold
      /// several things open at once and say what each is for. One implementation, two
      /// surfaces: these and the list's buttons reach the same manager.
      OpenTerminal : OpenTerminal
      CloseTerminal : CloseTerminal
      ListTerminals : ListTerminals
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
        { ExecuteCommand = fun _ -> async { return Error "no terminal capability" }
          CheckPending = fun _ -> async { return Error "no terminal capability" }
          WriteTerminal = fun _ _ -> async { return Error "no terminal capability" }
          ReadTerminal = fun _ _ -> async { return Error "no terminal capability" }
          OpenTerminal = fun _ _ -> async { return Error "no terminal capability" }
          CloseTerminal = fun _ -> async { return Error "no terminal capability" }
          ListTerminals = fun () -> async { return Error "no terminal capability" }
          RunGated =
            fun call ->
                async { return Error (sprintf "no gate to run %s through in this session" call.Tool) }
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
    ///
    /// **Where a terminal-shaped reason finds its owner** (Plan 20, stage 5). A stream ending
    /// and an integration going missing are facts about a TERMINAL, and `TerminalOpened`
    /// records only who asked for it — which for the agent's own terminals is the agent, an
    /// actor with no credential of its own. So those reasons take the owner of the most recent
    /// agent-authored block in that terminal: the turn that last did work there is the turn
    /// this concerns. A terminal the agent has never run anything in wakes nothing, which is
    /// the same safe direction as an unreadable owner and, here, also the honest answer — a
    /// source ending under a terminal the agent never used is not the agent's news.
    ///
    /// **Precedence, when several reasons are owed at once.** They resolve to ONE turn, whose
    /// attribution is the most consequential of them; the rest are inside that turn's digest
    /// window regardless, exactly as several completions already coalesce. The order is how
    /// much each changes what the agent should do next: an integration loss means its queue is
    /// HELD and nothing further will arrive; a stream ending means a source it was reading is
    /// gone; a completion is ordinary news.
    let private rank =
        function
        | IntegrationLost _ -> 0
        | StreamEnded _ -> 1
        | CommandFinished -> 2

    /// Why a turn is owed and who it would run as, or `None` when nothing is.
    let pendingReason (events: SessionEvent list) : (WakeReason * ActorRef) option =
        // Owed reasons are collected rather than short-circuited, because precedence is across
        // KINDS and the highest-ranked one can arrive last.
        let better (candidate: WakeReason * ActorRef) (best: (WakeReason * ActorRef) option) =
            match best with
            // Strictly better, so within one rank the FIRST owed still wins — the rest
            // coalesce into it, as they always have.
            | Some (kept, _) when rank (fst candidate) >= rank kept -> best
            | _ -> Some candidate
        // Which terminals take their bytes from a stream somebody else produces (Plan 16,
        // part D) — the only ones whose CLOSE is a source ending rather than a shell being
        // shut. A sandbox shell closing is somebody deciding, usually the agent itself
        // through `close_terminal`, and waking an agent to tell it what it just did would be
        // a loop with a delay in it.
        let attached =
            events
            |> List.choose (function
                | SessionEvent.TerminalOpened e when Option.isNone e.Sandbox -> Some (TerminalId.value e.TerminalId)
                | _ -> None)
            |> Set.ofList
        events
        |> List.fold
            (fun (background: Map<string, ActorRef>, lastAgent: Map<string, ActorRef>, owed) event ->
                match event with
                // A new turn takes everything before it: whatever those blocks did, that
                // turn's digest reported it. `lastAgent` is NOT reset — it is not a debt, it
                // is who the agent has been in that terminal, and that outlives the turn.
                | AgentTurnStarted _ -> Map.empty, lastAgent, None
                | SessionEvent.TerminalBlockStarted b ->
                    let lastAgent =
                        match Authority.onBehalfOf b.Authority with
                        | Some owner -> Map.add (TerminalId.value b.TerminalId) owner lastAgent
                        | None -> lastAgent
                    let background =
                        match b.Background, Authority.onBehalfOf b.Authority with
                        | true, Some owner -> Map.add (BlockId.value b.BlockId) owner background
                        | _ -> background
                    background, lastAgent, owed
                | SessionEvent.TerminalBlockCompleted b ->
                    let owed =
                        match Map.tryFind (BlockId.value b.BlockId) background with
                        | Some owner -> better (CommandFinished, owner) owed
                        | None -> owed
                    background, lastAgent, owed
                // Only a terminal the agent has worked in, and only the loss — the RESTORE
                // needs no turn, because a queue that started moving again says so by moving.
                | SessionEvent.TerminalIntegrationLost e ->
                    let owed =
                        match Map.tryFind (TerminalId.value e.TerminalId) lastAgent with
                        | Some owner -> better (IntegrationLost e.TerminalId, owner) owed
                        | None -> owed
                    background, lastAgent, owed
                | SessionEvent.TerminalClosed e when Set.contains (TerminalId.value e.TerminalId) attached ->
                    let owed =
                        match Map.tryFind (TerminalId.value e.TerminalId) lastAgent with
                        | Some owner -> better (StreamEnded e.TerminalId, owner) owed
                        | None -> owed
                    background, lastAgent, owed
                | _ -> background, lastAgent, owed)
            (Map.empty, Map.empty, None)
        |> fun (_, _, owed) -> owed

    /// The actor a woken turn would run AS, for the readers that do not need the reason.
    let pending (events: SessionEvent list) : ActorRef option = pendingReason events |> Option.map snd

    /// Whether a turn is owed at all. `pending` says who; this says whether, for the readers
    /// that only need the question answered.
    let due (events: SessionEvent list) : bool = pendingReason events |> Option.isSome
