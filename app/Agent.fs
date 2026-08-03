module Yession.Host.Agent

// The real agent runner: an adapter from the `RunAgent` capability to the Claude Agent
// SDK. The turn's typed capabilities are exposed to the model as MCP tools —
// `ensure_environment`, `execute_command` and `queue_terminal_command` — so a live agent
// can lazily start the
// session environment and run commands, exactly like the scripted agents in the
// deterministic suite. Requires ANTHROPIC_API_KEY or CLAUDE_CODE_OAUTH_TOKEN; the
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
          'ensure_environment',
          'Make sure this session has a running environment for command execution. Call it before execute_command, with the reason you need one. Do NOT call it for purely conversational answers.',
          { reason: z.string().describe('why an environment is needed') },
          async (args) => ({ content: [{ type: 'text', text: await $3(args.reason) }] })
        ),
        sdk.tool(
          'execute_command',
          'Run a command in the session environment. Returns the exit result followed by the streamed output.',
          { executable: z.string(), arguments: z.array(z.string()).describe('argv, one element per argument') },
          async (args) => ({ content: [{ type: 'text', text: await $4(args.executable, args.arguments) }] })
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
          'queue_terminal_command',
          'Put a shell command in a terminal queue, where the people in this session can read it, edit it, and (depending on the terminal) approve it before it runs. Returns as soon as it is queued — it does NOT wait for the command to run or return its output. Prefer this over execute_command whenever a human should see what you are about to run.',
          { command: z.string().describe('the shell command line to queue') },
          async (args) => ({ content: [{ type: 'text', text: await $10(args.command) }] })
        )
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
        // The turn's ONLY tools are the six above. `tools: []` drops every built-in
        // (Bash/Read/Glob/Grep/WebFetch/Agent/Skill) from the model's context; MCP
        // servers ride a separate channel, so `yession`'s tools survive it.
        // `allowedTools` is NOT a restriction — it is the auto-approve list, and on its
        // own it left the read-only built-ins reachable (a session could list the host
        // filesystem). It stays so our tools run without a permission round-trip.
        tools: [],
        allowedTools: ['mcp__yession__ensure_environment', 'mcp__yession__execute_command', 'mcp__yession__set_secret', 'mcp__yession__list_secrets', 'mcp__yession__delete_secret', 'mcp__yession__queue_terminal_command'],
        abortController: controller,
        ...($2 ? { pathToClaudeCodeExecutable: $2 } : {}),
        env: $1,
        spawnClaudeCodeProcess: $11
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
    (ensure: string -> JS.Promise<string>)
    (executeCommand: string -> string array -> JS.Promise<string>)
    (onChunk: string -> unit)
    (registerAbort: (unit -> unit) -> unit)
    (setSecret: string -> string -> JS.Promise<string>)
    (listSecrets: unit -> JS.Promise<string>)
    (deleteSecret: string -> JS.Promise<string>)
    (queueTerminalCommand: string -> JS.Promise<string>)
    (claudeSpawner: obj)
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
    sprintf
        "Conversation so far:\n%s\n\nReply to the latest message from %s:\n%s"
        transcript
        (label context.CurrentMessage.Author)
        context.CurrentMessage.Body

/// The `ensure_environment` tool body: the typed capability, rendered as tool text.
let private ensureFor (capabilities: AgentCapabilities) : string -> JS.Promise<string> =
    fun reason ->
        async {
            match! capabilities.EnsureEnvironment reason with
            | EnvironmentAvailable -> return "environment available"
            | EnvironmentUnavailable r -> return sprintf "environment unavailable: %s" r
        }
        |> Async.StartAsPromise

/// The `execute_command` tool body: runs through the typed capability, returning the
/// exit result plus the streamed output so the model can reason about it. The same
/// output is recorded as events by the Session Process.
let private executeFor (capabilities: AgentCapabilities) : string -> string array -> JS.Promise<string> =
    fun executable arguments ->
        async {
            let commandId =
                match CommandId.create (string (Guid.NewGuid ())) with
                | Ok id -> id
                | Error e -> failwithf "command id invariant violated: %s" e
            let request =
                { CommandId = commandId
                  Executable = executable
                  Arguments = List.ofArray arguments
                  WorkingDirectory = None
                  Environment = Map.empty
                  Timeout = Some (TimeSpan.FromSeconds 120.0) }
            let mutable output = ""
            let! result =
                capabilities.ExecuteCommand request (fun chunk ->
                    let prefix = match chunk.Stream with Stdout -> "" | Stderr -> "[stderr] "
                    output <- output + prefix + chunk.Text)
            let summary =
                match result with
                | CommandSucceeded code -> sprintf "exit code %d" code
                | CommandFailed code -> sprintf "FAILED with exit code %d" code
                | CommandTimedOut -> "TIMED OUT"
                | CommandExecutionFailed reason -> sprintf "EXECUTION FAILED: %s" reason
            return sprintf "%s\n%s" summary output
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

/// The `queue_terminal_command` tool body (Plan 13). It reports the queue's OUTCOME, not
/// the command's: the command has not run yet, and telling a model "queued" when it is
/// actually waiting for a human would have it conclude, after a silent pause, that its
/// command failed and try something else.
let private queueTerminalCommandFor (capabilities: AgentCapabilities) : string -> JS.Promise<string> =
    fun command ->
        async {
            match! capabilities.QueueTerminalCommand None command with
            | Ok queued when queued.AwaitingApproval ->
                return
                    sprintf
                        "queued in terminal %s, WAITING FOR A HUMAN TO APPROVE IT. It has not run. Do not wait for output — say what you queued and why, and let them approve it."
                        (TerminalId.value queued.Terminal)
            | Ok queued ->
                return
                    sprintf
                        "queued in terminal %s and will run shortly. It has not run yet, so no output is available in this turn."
                        (TerminalId.value queued.Terminal)
            | Error reason -> return sprintf "could not queue the command: %s" reason
        }
        |> Async.StartAsPromise

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
                    (ensureFor capabilities)
                    (executeFor capabilities)
                    (fun text -> onChunk { Text = text })
                    signal.OnAbort
                    (setSecretFor capabilities)
                    (listSecretsFor capabilities)
                    (deleteSecretFor capabilities)
                    (queueTerminalCommandFor capabilities)
                    (Sandboxes.AgentSandbox.claudeSpawnerFor (agentBackend ()) ambient home env)
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
