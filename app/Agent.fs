module Yession.Host.Agent

// The real agent runner: an adapter from the `RunAgent` capability to the Claude Agent
// SDK. The turn's typed capabilities are exposed to the model as MCP tools —
// `execute_command` and `read_terminal_block` (Plan 13, stage 3b) — so a live agent runs
// commands through the session's terminals, where people can see them, exactly like the
// scripted agents in the deterministic suite. Requires ANTHROPIC_API_KEY or CLAUDE_CODE_OAUTH_TOKEN; the
// deterministic tests never call this, and the live smoke test is gated on credentials,
// so verification stays repeatable.

open System
open Fable.Core
open Yession.Domain

type private RunOutcome =
    abstract ok : bool
    abstract body : string
    abstract reason : string
    // Plan 04, Step 28: the `result` message's usage block, surfaced instead of
    // discarded. Zero when the SDK reports no usage; `model` is "" when unknown.
    abstract inputTokens : int
    abstract outputTokens : int
    abstract cacheReadTokens : int
    abstract cacheCreationTokens : int
    abstract model : string

[<Emit("""(async () => {
  try {
    const sdk = await import('@anthropic-ai/claude-agent-sdk')
    const { z } = await import('zod')
    const controller = new AbortController()
    $6(() => controller.abort())
    const yession = sdk.createSdkMcpServer({
      name: 'yession',
      version: '1.0.0',
      tools: [
        sdk.tool(
          'execute_command',
          'Run a shell command in one of this session\'s terminals, where the people in the session can see it, edit it, and — depending on the terminal — approve it before it runs. This is the only way to run anything. Pass `sandbox` to run in a named work sandbox (start_work_sandbox creates one); omit it for the default sandbox, which is where everything runs unless you say otherwise. It waits for the result and returns the exit code and output; if it is waiting on a person, or the terminal is busy, or the command is still going, it says so and returns a handle for read_terminal_block instead of hanging. Read what it returns: every answer states which of those happened.',
          { command: z.string().describe('the shell command line to run, e.g. "npm test -- --watch=false"'), sandbox: z.string().optional().describe('the work sandbox to run in, e.g. "test"; omit for the default one') },
          async (args) => ({ content: [{ type: 'text', text: await $3(args.command, args.sandbox || '') }] })
        ),
        sdk.tool(
          'read_terminal_block',
          'Pick a command back up by the handle execute_command returned — an approval that arrived late, or a long build. Returns the same thing execute_command does.',
          { handle: z.string().describe('the handle from execute_command') },
          async (args) => ({ content: [{ type: 'text', text: await $4(args.handle) }] })
        ),
        sdk.tool(
          'set_secret',
          'Persist a named secret for this session (WRITE-ONLY: no tool can read it back). To USE it, reference its name as an environment variable secret ref when an environment starts — the value is injected there directly and never appears in the conversation.',
          { name: z.string().describe('the secret name, e.g. DEPLOY_TOKEN'), value: z.string().describe('the secret value to store') },
          async (args) => ({ content: [{ type: 'text', text: await $7(args.name, args.value) }] })
        ),
        sdk.tool(
          'list_secrets',
          'List the names and timestamps of this session\'s stored secrets. Never returns values.',
          {},
          async () => ({ content: [{ type: 'text', text: await $8() }] })
        ),
        sdk.tool(
          'delete_secret',
          'Delete one of this session\'s stored secrets by name.',
          { name: z.string().describe('the secret name to delete') },
          async (args) => ({ content: [{ type: 'text', text: await $9(args.name) }] })
        ),
        sdk.tool(
          'add_repo',
          'Clone a GitHub repo into this session\'s shared repos directory (visible to everyone here, and inside the work environment). Takes owner/repo — never a URL — and only repos the session\'s GitHub credential can reach. Read-only bootstrap: to commit or push, use execute_command in a terminal. Already-added repos just report their current state.',
          { repo: z.string().describe('the repo as owner/name, e.g. "octocat/hello-world"') },
          async (args) => ({ content: [{ type: 'text', text: await $11(args.repo) }] })
        ),
        sdk.tool(
          'switch_branch',
          'Switch a repo\'s checkout to a branch (optionally creating it). Local only — never touches the remote. Everyone in the session sees the switch in the timeline.',
          { repo: z.string().describe('owner/name'), branch: z.string().describe('the branch to switch to'), create: z.boolean().optional().describe('create the branch (like switch -c)') },
          async (args) => ({ content: [{ type: 'text', text: await $13(args.repo, args.branch, !!args.create) }] })
        ),
        sdk.tool(
          'fetch_repo',
          'Fetch a repo\'s remote refs (prune, no submodules). Use before switching to a branch that only exists on the remote.',
          { repo: z.string().describe('owner/name') },
          async (args) => ({ content: [{ type: 'text', text: await $14(args.repo) }] })
        ),
        sdk.tool(
          'repo_status',
          'A repo checkout\'s git status (porcelain, with branch header).',
          { repo: z.string().describe('owner/name') },
          async (args) => ({ content: [{ type: 'text', text: await $15(args.repo) }] })
        ),
        sdk.tool(
          'repo_log',
          'The last 30 commits of a repo checkout, one line each.',
          { repo: z.string().describe('owner/name') },
          async (args) => ({ content: [{ type: 'text', text: await $16(args.repo) }] })
        ),
        sdk.tool(
          'repo_diff',
          'The uncommitted diff of a repo checkout (capped; use a terminal for the full thing).',
          { repo: z.string().describe('owner/name') },
          async (args) => ({ content: [{ type: 'text', text: await $17(args.repo) }] })
        ),
        sdk.tool(
          'start_work_sandbox',
          'Make sure a named work sandbox exists for this session, and get it back. Asking twice for the same name with the same forwarding returns the one already running and changes nothing — safe to call every time. Asking for the same name with DIFFERENT forwarding is refused rather than silently recreated, because recreating kills whatever is running inside it: stop_work_sandbox first. `forward` names credentials to put inside the sandbox (currently "github", which is what lets git push work from a terminal there); it uses the credentials of the person whose turn this is, and everyone in the session sees which were forwarded and whose.',
          { name: z.string().describe('the sandbox name, e.g. "default" or "test"'), forward: z.array(z.string()).optional().describe('credential names to forward, e.g. ["github"]') },
          async (args) => ({ content: [{ type: 'text', text: await $18(args.name, args.forward || []) }] })
        ),
        sdk.tool(
          'stop_work_sandbox',
          'Stop a named work sandbox, killing anything running in it. The way to change what a sandbox forwards: stop it, then start it again.',
          { name: z.string().describe('the sandbox name') },
          async (args) => ({ content: [{ type: 'text', text: await $19(args.name) }] })
        ),
        // The session's QUERIES (Plan 15), generated from the registry rather than
        // written out one by one: declaring a query is what puts it in front of the
        // agent, and in front of the humans, with no third place to keep in step.
        // `readOnlyHint` is the MCP spec's own marker — the same annotation that will
        // identify a third-party server's queries — and it is what makes a tool a query
        // rather than a command.
        ...$12.map(q => sdk.tool(
          q.name,
          q.description,
          {},
          async () => ({ content: [{ type: 'text', text: await q.read() }] }),
          { annotations: { readOnlyHint: true, title: q.title } }
        ))
      ]
    })
    const q = sdk.query({
      prompt: $0.prompt,
      options: {
        systemPrompt: $0.system,
        maxTurns: 8,
        settingSources: [],
        includePartialMessages: true,
        mcpServers: { yession },
        // The turn's ONLY tools are the yession ones above. `tools: []` drops every
        // built-in (Bash/Read/Glob/Grep/WebFetch/Agent/Skill) from the model's context;
        // MCP servers ride a separate channel, so `yession`'s tools survive it.
        // `allowedTools` is NOT a restriction — it is the auto-approve list, and on its
        // own it left the read-only built-ins reachable (a session could list the host
        // filesystem). It stays so our tools run without a permission round-trip.
        tools: [],
        allowedTools: ['mcp__yession__execute_command', 'mcp__yession__read_terminal_block', 'mcp__yession__set_secret', 'mcp__yession__list_secrets', 'mcp__yession__delete_secret', 'mcp__yession__add_repo', 'mcp__yession__switch_branch', 'mcp__yession__fetch_repo', 'mcp__yession__repo_status', 'mcp__yession__repo_log', 'mcp__yession__repo_diff', 'mcp__yession__start_work_sandbox', 'mcp__yession__stop_work_sandbox', ...$12.map(q => 'mcp__yession__' + q.name)],
        abortController: controller,
        ...($2 ? { pathToClaudeCodeExecutable: $2 } : {}),
        env: $1,
        spawnClaudeCodeProcess: $10
      }
    })
    let body = ''
    let streamed = ''
    let failed = null
    let inputTokens = 0
    let outputTokens = 0
    let cacheReadTokens = 0
    let cacheCreationTokens = 0
    let model = ''
    for await (const m of q) {
      if (m.type === 'stream_event') {
        const e = m.event
        if (e && e.type === 'content_block_delta' && e.delta && typeof e.delta.text === 'string') {
          $5(e.delta.text)
          streamed += e.delta.text
        }
      } else if (m.type === 'result') {
        // Plan 04, Step 28: read the usage block instead of discarding it.
        const u = m.usage || {}
        inputTokens = u.input_tokens || 0
        outputTokens = u.output_tokens || 0
        cacheReadTokens = u.cache_read_input_tokens || 0
        cacheCreationTokens = u.cache_creation_input_tokens || 0
        if (m.modelUsage) { const ks = Object.keys(m.modelUsage); if (ks.length) model = ks[0] }
        if (m.subtype === 'success') body = (typeof m.result === 'string' && m.result !== '') ? m.result : streamed
        else failed = 'agent run ended: ' + m.subtype
      }
    }
    const usage = { inputTokens, outputTokens, cacheReadTokens, cacheCreationTokens, model }
    return failed ? { ok: false, body: '', reason: failed, ...usage } : { ok: true, body, reason: '', ...usage }
  } catch (err) {
    return { ok: false, body: '', reason: String((err && err.message) || err), inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0, model: '' }
  }
})()""")>]
let private runQuery
    (prompts: {| system: string; prompt: string |})
    (agentEnv: obj)
    (claudePath: string)
    (executeCommand: string -> string -> JS.Promise<string>)
    (readTerminalBlock: string -> JS.Promise<string>)
    (onChunk: string -> unit)
    (registerAbort: (unit -> unit) -> unit)
    (setSecret: string -> string -> JS.Promise<string>)
    (listSecrets: unit -> JS.Promise<string>)
    (deleteSecret: string -> JS.Promise<string>)
    (claudeSpawner: obj)
    (addRepo: string -> JS.Promise<string>)
    /// The registry's queries as tool descriptors — `$12`, where `list_repos` used to be,
    /// which is not a coincidence: the query surface is what replaced it.
    (queries: obj array)
    (switchBranch: string -> string -> bool -> JS.Promise<string>)
    (fetchRepo: string -> JS.Promise<string>)
    (repoStatus: string -> JS.Promise<string>)
    (repoLog: string -> JS.Promise<string>)
    (repoDiff: string -> JS.Promise<string>)
    (startWorkSandbox: string -> string array -> JS.Promise<string>)
    (stopWorkSandbox: string -> JS.Promise<string>)
    : JS.Promise<RunOutcome> =
    jsNative

/// Some sandboxes disallow the SDK's own vendored executable; `YESSION_CLAUDE_PATH`
/// points the SDK at a system Claude Code install instead. Empty = SDK default.
let private claudePath () = Interop.envOr "YESSION_CLAUDE_PATH" ""

/// The CLI's per-session scratch HOME: it writes `~/.claude` session state, which now
/// lives (and dies) with the session's data directory instead of the real HOME.
let private agentHome () =
    sprintf "%s/agent-home" (Interop.envOr "YESSION_SESSION_DATA" ".yession")

/// Where the CLI process runs (`YESSION_AGENT_SANDBOX`): host or srt, never docker.
/// SessionMain parses this at boot and fails the session on anything else, so by the
/// time a turn runs the value is known good — this reads it back, it does not re-decide.
let private agentBackend () =
    match SandboxBackend.parseAgent (Interop.envOr "YESSION_AGENT_SANDBOX" "host") with
    | Ok backend -> backend
    | Error e -> failwithf "agent sandbox: %s" e

[<Emit("Object.fromEntries($0)")>]
let private toEnvObj (entries: (string * string) array) : obj = jsNative

/// One prompt per turn: the completed conversation as a transcript plus the message to
/// answer. Built from the projection only — draft/Yjs state never appears here.
let private promptOf (context: AgentContextPack) : string =
    let label (author: ActorRef) =
        match author with
        | UserRef u -> UserId.value u
        | PeerRef p -> PeerId.value p
        | ActorRef.Agent -> "agent"
        | ActorRef.SessionProcess -> "session-process"
        | ActorRef.System -> "system"
    let transcript =
        context.Conversation
        |> List.filter (fun item -> item.Status = Complete)
        |> List.map (fun item -> sprintf "%s: %s" (label item.Author) item.Body)
        |> String.concat "\n"
    // The terminal digest is rendered as its own section, never folded into the
    // conversation: the model must be able to tell what someone SAID from what a machine
    // PRINTED, and a block attributed like a chat line invites it to reply to the output.
    let terminals =
        match context.Terminals with
        | [] -> ""
        | blocks ->
            let render (block: TerminalBlockDigest) =
                let outcome =
                    match block.Status with
                    | BlockRunning -> "still running"
                    | BlockFinished (CommandSucceeded code) -> sprintf "exit %d" code
                    | BlockFinished (CommandFailed code) -> sprintf "exit %d" code
                    | BlockFinished (CommandExecutionFailed reason) -> sprintf "could not run: %s" reason
                    | BlockFinished CommandTimedOut -> "timed out"
                    // The agent is told it was refused, and by whom. This is the feedback
                    // the review gate owes whoever it refused: without it a rejected
                    // command is indistinguishable from one that vanished, and the model
                    // reasonably tries again.
                    | BlockRejected (by, Some why) -> sprintf "refused by %s: %s" (label by) why
                    | BlockRejected (by, None) -> sprintf "refused by %s" (label by)
                let approved =
                    match block.ApprovedBy with
                    | Some actor -> sprintf ", approved by %s" (label actor)
                    | None -> ""
                let elided =
                    if block.Elided > 0 then
                        sprintf "[%d earlier characters omitted — the whole output is in the transcript]\n" block.Elided
                    else ""
                sprintf
                    "[%s] %s ran: %s (%s%s)\n%s%s"
                    block.Title
                    (label block.Author)
                    block.Command
                    outcome
                    approved
                    elided
                    block.OutputTail
            sprintf
                "\n\nTerminal activity since your last turn (you did not see this before now):\n%s"
                (blocks |> List.map render |> String.concat "\n\n")
    sprintf
        "Conversation so far:\n%s%s\n\nReply to the latest message from %s:\n%s"
        transcript
        terminals
        (label context.CurrentMessage.Author)
        context.CurrentMessage.Body

/// One outcome, rendered as tool text (Plan 13, stage 3b).
///
/// Every case says WHICH state it is in, and the wording is load-bearing rather than
/// decorative: telling a model "queued" when its command is actually blocked on a person has
/// it conclude, after a silent pause, that the command failed and try something else — which
/// is exactly how a review gate turns into the agent routing around the review.
let private renderOutcome (outcome: TerminalCommandOutcome) : string =
    let where = sprintf "terminal %s" (TerminalId.value outcome.Terminal)
    let handle = QueueId.value outcome.Handle
    let output =
        if outcome.OutputTail = "" then ""
        else if outcome.Elided > 0 then
            sprintf "\n[%d earlier characters omitted]\n%s" outcome.Elided outcome.OutputTail
        else "\n" + outcome.OutputTail
    match outcome.Status with
    | TerminalCommandRan (CommandSucceeded code) -> sprintf "exit code %d in %s%s" code where output
    | TerminalCommandRan (CommandFailed code) -> sprintf "FAILED with exit code %d in %s%s" code where output
    | TerminalCommandRan CommandTimedOut -> sprintf "TIMED OUT in %s%s" where output
    | TerminalCommandRan (CommandExecutionFailed reason) ->
        sprintf "EXECUTION FAILED in %s: %s%s" where reason output
    | TerminalCommandRunning ->
        sprintf
            "STILL RUNNING in %s. It has NOT finished; nothing was cancelled. Call read_terminal_block with handle '%s' to pick it up.%s"
            where handle output
    | TerminalCommandAwaitingApproval ->
        sprintf
            "WAITING FOR A HUMAN TO APPROVE IT in %s. It has not run. Do not wait for output — say what you queued and why. Call read_terminal_block with handle '%s' once they have had a chance to look."
            where handle
    | TerminalCommandAwaitingTerminal ->
        sprintf
            "WAITING FOR %s TO BE FREE — somebody is using it, or another command is running there. It has not run. Call read_terminal_block with handle '%s' later."
            where handle
    | TerminalCommandRefused (by, reason) ->
        let who = ActorRef.token by
        match reason with
        | Some reason -> sprintf "REFUSED by %s in %s: %s. It did not run — do not try to run it another way." who where reason
        | None -> sprintf "REFUSED by %s in %s. It did not run — do not try to run it another way." who where

/// The `execute_command` tool body (Plan 13, stage 3b): the agent's ONE execution path,
/// and — since Plan 15, stage 2 — the only door into a NAMED sandbox as well as the
/// default one. An empty sandbox argument means the default, which is what the tool's
/// optional parameter degrades to.
let private executeFor (capabilities: AgentCapabilities) : string -> string -> JS.Promise<string> =
    fun command sandbox ->
        async {
            let target =
                if sandbox = "" then Ok None
                else SandboxName.create sandbox |> Result.map (InSandbox >> Some)
            match target with
            | Error e -> return sprintf "not a sandbox name: %s" e
            | Ok target ->
                match! capabilities.ExecuteCommand target command with
                | Ok outcome -> return renderOutcome outcome
                | Error reason -> return sprintf "could not run the command: %s" reason
        }
        |> Async.StartAsPromise

/// The `read_terminal_block` tool body: resume a handle the tool above yielded.
let private readBlockFor (capabilities: AgentCapabilities) : string -> JS.Promise<string> =
    fun handle ->
        async {
            match QueueId.create handle with
            | Error e -> return sprintf "not a command handle: %s" e
            | Ok handle ->
                match! capabilities.ReadTerminalBlock handle with
                | Ok outcome -> return renderOutcome outcome
                | Error reason -> return sprintf "could not read that command: %s" reason
        }
        |> Async.StartAsPromise

/// The secrets tool bodies (Plan 06): the typed WRITE-ONLY capabilities rendered as
/// tool text. Values never flow back — a set/delete confirms, a list names.
let private setSecretFor (capabilities: AgentCapabilities) : string -> string -> JS.Promise<string> =
    fun name value ->
        async {
            match SecretName.create name with
            | Error e -> return sprintf "invalid secret name: %s" e
            | Ok secretName ->
                match! capabilities.SetSecret secretName value with
                | Ok metadata -> return sprintf "stored secret '%s' (updated %s)" (SecretName.value metadata.Id.Name) (metadata.UpdatedAt.ToString "o")
                | Error e -> return sprintf "could not store secret: %s" e
        }
        |> Async.StartAsPromise

let private listSecretsFor (capabilities: AgentCapabilities) : unit -> JS.Promise<string> =
    fun () ->
        async {
            match! capabilities.ListSecrets () with
            | Error e -> return sprintf "could not list secrets: %s" e
            | Ok [] -> return "no secrets stored for this session"
            | Ok listed ->
                return
                    listed
                    |> List.map (fun m -> sprintf "%s (updated %s)" (SecretName.value m.Id.Name) (m.UpdatedAt.ToString "o"))
                    |> String.concat "\n"
        }
        |> Async.StartAsPromise

let private deleteSecretFor (capabilities: AgentCapabilities) : string -> JS.Promise<string> =
    fun name ->
        async {
            match SecretName.create name with
            | Error e -> return sprintf "invalid secret name: %s" e
            | Ok secretName ->
                match! capabilities.DeleteSecret secretName with
                | Ok true -> return sprintf "deleted secret '%s'" name
                | Ok false -> return sprintf "no secret named '%s'" name
                | Error e -> return sprintf "could not delete secret: %s" e
        }
        |> Async.StartAsPromise

/// The repo tool bodies (Plan 14): the typed read-only verbs rendered as tool text.
/// Every body parses the raw `owner/repo` at this boundary, so the capabilities behind
/// it only ever see a validated `RepoRef`.
let private withRepo (raw: string) (inner: RepoRef -> Async<string>) : JS.Promise<string> =
    async {
        match RepoRef.create raw with
        | Error e -> return sprintf "not a repo name: %s" e
        | Ok repo -> return! inner repo
    }
    |> Async.StartAsPromise

let private addRepoFor (capabilities: AgentCapabilities) : string -> JS.Promise<string> =
    fun raw ->
        withRepo raw (fun repo ->
            async {
                match! capabilities.AddRepo repo with
                | Ok listing -> return sprintf "added %s — the checkout is shared with everyone in this session and visible in the work environment" (RepoListing.describe listing)
                | Error e -> return sprintf "could not add the repo: %s" e
            })

/// The session's queries as SDK tool descriptors (Plan 15). One per registered query, in
/// registration order, each answering with the same text the settings surface renders as
/// a table — the model and the humans are looking at one value, not two renderings that
/// drift.
let private queryToolsFor (capabilities: AgentCapabilities) : obj array =
    capabilities.Queries
    |> List.map (fun def ->
        box
            {| name = QueryName.value def.Name
               title = def.Title
               description = QueryDef.toolDescription def
               read =
                 fun () ->
                     async {
                         match! capabilities.ReadQuery def.Name with
                         | Ok value -> return QueryValue.describe def.Shape value
                         | Error e ->
                             return sprintf "could not read %s: %s" (QueryName.value def.Name) e
                     }
                     |> Async.StartAsPromise |})
    |> Array.ofList

let private switchBranchFor (capabilities: AgentCapabilities) : string -> string -> bool -> JS.Promise<string> =
    fun raw branch create ->
        withRepo raw (fun repo ->
            async {
                match! capabilities.SwitchRepoBranch repo branch create with
                | Ok listing -> return sprintf "now on %s" (RepoListing.describe listing)
                | Error e -> return sprintf "could not switch branch: %s" e
            })

let private fetchRepoFor (capabilities: AgentCapabilities) : string -> JS.Promise<string> =
    fun raw ->
        withRepo raw (fun repo ->
            async {
                match! capabilities.FetchRepo repo with
                | Ok said -> return said
                | Error e -> return sprintf "could not fetch: %s" e
            })

let private inspectRepoFor (inspect: AgentCapabilities -> InspectRepo) (capabilities: AgentCapabilities) : string -> JS.Promise<string> =
    fun raw ->
        withRepo raw (fun repo ->
            async {
                match! inspect capabilities repo with
                | Ok said -> return said
                | Error e -> return sprintf "could not inspect the repo: %s" e
            })

/// The named-WorkSandbox tool bodies (Plan 15, stage 2). Both parse the raw name at this
/// boundary, so the capability behind them only ever sees a validated `SandboxName`.
let private withSandbox (raw: string) (inner: SandboxName -> Async<string>) : JS.Promise<string> =
    async {
        match SandboxName.create raw with
        | Error e -> return sprintf "not a sandbox name: %s" e
        | Ok name -> return! inner name
    }
    |> Async.StartAsPromise

let private startWorkSandboxFor (capabilities: AgentCapabilities) : string -> string array -> JS.Promise<string> =
    fun raw forward ->
        withSandbox raw (fun name ->
            async {
                match! capabilities.StartWorkSandbox name (List.ofArray forward) with
                | Ok said -> return said
                | Error e -> return sprintf "could not start the sandbox: %s" e
            })

let private stopWorkSandboxFor (capabilities: AgentCapabilities) : string -> JS.Promise<string> =
    fun raw ->
        withSandbox raw (fun name ->
            async {
                match! capabilities.StopWorkSandbox name with
                | Ok said -> return said
                | Error e -> return sprintf "could not stop the sandbox: %s" e
            })

/// The Claude Agent SDK–backed `RunAgent`, parameterized by the turn's credential:
/// `None` = the ambient credential variables pass through (the documented last resort
/// — how CI's LiveAgent tier feeds the agent); `Some (envVar, value)` = the spawned
/// CLI runs with exactly that credential, both ambient credential variables displaced.
/// Either way the CLI's environment is the AgentSandbox policy env — allowlisted
/// baseline + per-session scratch HOME — never the raw process env, and the CLI
/// process itself comes up through the `spawnClaudeCodeProcess` seam. Streams text
/// deltas as chunks; the typed capabilities surface as MCP tools; failures are values,
/// never exceptions. The abort signal maps onto the SDK's AbortController, so an
/// interrupt cancels the live query promptly (the returned failure is then discarded
/// by the orchestrator); the spawner's own kill fires only on the SDK's forwarded
/// signal, after the graceful stdin-EOF window.
let runWith (credential: (string * string) option) : RunAgent =
    fun context capabilities signal onChunk ->
        async {
            let home = agentHome ()
            Fs.ensureDir home
            let ambient = Sandboxes.ambientEnv ()
            let env = Sandboxes.AgentSandbox.envFor ambient home credential
            let! outcome =
                runQuery
                    {| system = context.SystemPrompt; prompt = promptOf context |}
                    (toEnvObj (Map.toArray env))
                    (claudePath ())
                    (executeFor capabilities)
                    (readBlockFor capabilities)
                    (fun text -> onChunk { Text = text })
                    signal.OnAbort
                    (setSecretFor capabilities)
                    (listSecretsFor capabilities)
                    (deleteSecretFor capabilities)
                    (Sandboxes.AgentSandbox.claudeSpawnerFor (agentBackend ()) ambient home env)
                    (addRepoFor capabilities)
                    (queryToolsFor capabilities)
                    (switchBranchFor capabilities)
                    (fetchRepoFor capabilities)
                    (inspectRepoFor (fun c -> c.RepoStatus) capabilities)
                    (inspectRepoFor (fun c -> c.RepoLog) capabilities)
                    (inspectRepoFor (fun c -> c.RepoDiff) capabilities)
                    (startWorkSandboxFor capabilities)
                    (stopWorkSandboxFor capabilities)
                |> Interop.awaitPromise
            let usage =
                { InputTokens = outcome.inputTokens
                  OutputTokens = outcome.outputTokens
                  CacheReadTokens = outcome.cacheReadTokens
                  CacheCreationTokens = outcome.cacheCreationTokens
                  Model = if System.String.IsNullOrEmpty outcome.model then None else Some outcome.model }
            return if outcome.ok then AgentCompleted (outcome.body, Some usage) else AgentFailed outcome.reason
        }

/// The ambient-credential runner (existing call sites and the env fallback).
let run : RunAgent = runWith None
