module Yession.Host.Agent

// The real agent runner: an adapter from the `RunAgent` capability to the Claude Agent
// SDK. The turn's typed capabilities reach the model as MCP tools, and WHICH tools those
// are is no longer this file's business — `AgentTools.registry` answers that, and the
// adapter turns whatever it answers into `sdk.tool(...)` calls in a loop (Plan 16, part A).
// Requires ANTHROPIC_API_KEY or CLAUDE_CODE_OAUTH_TOKEN; the deterministic tests never call
// this, and the live smoke test is gated on credentials, so verification stays repeatable.

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

/// What one tool call answered, as JS sees it: the text the model gets, and whether the
/// call HAPPENED. `ok = false` is a protocol failure (no such tool, unreadable arguments),
/// which the SDK is told about as `isError` — a tool that ran and went badly is `ok = true`
/// with text saying so, because that is something the model should read and act on.
type private JsToolAnswer =
    abstract ok : bool
    abstract text : string

[<Emit("""(async function (prompts, agentEnv, claudePath, descriptors, invoke, allowedTools, onChunk, registerAbort, claudeSpawner) {
  try {
    const sdk = await import('@anthropic-ai/claude-agent-sdk')
    const { z } = await import('zod')
    const controller = new AbortController()
    registerAbort(() => controller.abort())
    // JSON Schema in, zod shape out. The SDK's tool builder wants zod; every other
    // boundary a descriptor crosses (MCP's tools/list, an external server, the audit
    // record) speaks JSON Schema — so the conversion belongs here, at the one edge that
    // needs it, rather than making the schema itself SDK-shaped.
    const zodType = (spec) => {
      if (!spec) return z.any()
      if (spec.type === 'string') return z.string()
      if (spec.type === 'boolean') return z.boolean()
      if (spec.type === 'number' || spec.type === 'integer') return z.number()
      if (spec.type === 'array') return z.array(zodType(spec.items))
      return z.any()
    }
    const zodShape = (schema) => {
      const shape = {}
      const props = (schema && schema.properties) || {}
      const required = new Set((schema && schema.required) || [])
      for (const key of Object.keys(props)) {
        const p = props[key] || {}
        let t = zodType(p)
        if (p.description) t = t.describe(p.description)
        if (!required.has(key)) t = t.optional()
        shape[key] = t
      }
      return shape
    }
    // One SDK MCP server per namespace, which is what puts the namespace in the wire name
    // the model sees (mcp__<namespace>__<tool>) without inventing a naming scheme.
    const byNamespace = new Map()
    for (const d of descriptors) {
      let shape = {}
      try { shape = zodShape(JSON.parse(d.schema)) } catch (e) { shape = {} }
      const annotations = {}
      if (d.readOnly) annotations.readOnlyHint = true
      if (d.title) annotations.title = d.title
      const built = sdk.tool(d.name, d.description, shape, async (args) => {
        const answer = await invoke(d.ns, d.name, JSON.stringify(args || {}))
        return { content: [{ type: 'text', text: answer.text }], isError: !answer.ok }
      }, { annotations })
      if (!byNamespace.has(d.ns)) byNamespace.set(d.ns, [])
      byNamespace.get(d.ns).push(built)
    }
    const mcpServers = {}
    for (const entry of byNamespace) {
      mcpServers[entry[0]] = sdk.createSdkMcpServer({ name: entry[0], version: '1.0.0', tools: entry[1] })
    }
    const q = sdk.query({
      prompt: prompts.prompt,
      options: {
        systemPrompt: prompts.system,
        // The session's model choice, and ONLY when it has made one: an absent option is
        // what leaves the pick to the SDK, and passing an empty string instead would be
        // this session inventing a model id of "".
        ...(prompts.model ? { model: prompts.model } : {}),
        // No `maxTurns`: unset is the SDK's no-cap default, the same setting interactive
        // Claude Code runs under. A turn ends when the model is done or somebody
        // interrupts it, never at a step count this file picked.
        settingSources: [],
        includePartialMessages: true,
        mcpServers,
        // The turn's ONLY tools are the registry's. `tools: []` drops every built-in
        // (Bash/Read/Glob/Grep/WebFetch/Agent/Skill) from the model's context; MCP servers
        // ride a separate channel, so the registry's tools survive it. `allowedTools` is
        // NOT a restriction — it is the auto-approve list, and on its own it left the
        // read-only built-ins reachable (a session could list the host filesystem). It
        // stays so our tools run without a permission round-trip, and it is COMPUTED from
        // the same descriptors the servers were built from, so the two cannot drift.
        tools: [],
        allowedTools: allowedTools,
        abortController: controller,
        ...(claudePath ? { pathToClaudeCodeExecutable: claudePath } : {}),
        env: agentEnv,
        spawnClaudeCodeProcess: claudeSpawner
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
          onChunk(e.delta.text)
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
})($0, $1, $2, $3, $4, $5, $6, $7, $8)""")>]
let private runQuery
    (prompts: {| system: string; prompt: string; model: string |})
    (agentEnv: obj)
    (claudePath: string)
    /// The registry's descriptors, flattened for JS. Where eighteen positional callbacks
    /// used to be: one array, and a tool costs nothing here at all.
    (descriptors: obj array)
    (invoke: string -> string -> string -> JS.Promise<JsToolAnswer>)
    (allowedTools: string array)
    (onChunk: string -> unit)
    (registerAbort: (unit -> unit) -> unit)
    (claudeSpawner: obj)
    : JS.Promise<RunOutcome> =
    jsNative

/// Some sandboxes disallow the SDK's own vendored executable; `YESSION_CLAUDE_PATH`
/// points the SDK at a system Claude Code install instead. Empty = SDK default.
let private claudePath () = Interop.envOr "YESSION_CLAUDE_PATH" ""

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
                let elided =
                    if block.Elided > 0 then
                        sprintf "[%d earlier characters omitted — the whole output is in the transcript]\n" block.Elided
                    else ""
                sprintf
                    "[%s] %s ran: %s (%s)\n%s%s"
                    block.Title
                    (label block.Author)
                    block.Command
                    outcome
                    elided
                    block.OutputTail
            sprintf
                "\n\nTerminal activity since your last turn (you did not see this before now):\n%s"
                (blocks |> List.map render |> String.concat "\n\n")
    match context.CurrentMessage with
    | Some message ->
        sprintf
            "Conversation so far:\n%s%s\n\nReply to the latest message from %s:\n%s"
            transcript
            terminals
            (label message.Author)
            message.Body
    // A turn nobody asked for (Plan 20, stage 2): work this agent started finished while it
    // was not running. There is no message to reply to, and inventing one — "the system says
    // your build finished" — would put words in somebody's mouth on a shared transcript. It
    // is told what it is, and the terminal activity above is what it acts on.
    | None ->
        sprintf
            "Conversation so far:\n%s%s\n\nNobody has said anything new. You are running because work you started in the background finished — the terminal activity above is that work. Carry on with it, and say what it means for what you were doing."
            transcript
            terminals

/// The registry's descriptors, as plain objects the Emit block can walk. The only place
/// the two representations meet, and it is a projection — nothing is decided here.
let private descriptorsOf (registry: ToolRegistry) : obj array =
    registry.Tools
    |> List.map (fun descriptor ->
        box
            {| ns = descriptor.Namespace
               name = descriptor.Name
               description = descriptor.Description
               schema = descriptor.InputSchema
               readOnly = descriptor.ReadOnly
               title = Option.defaultValue "" descriptor.Title |})
    |> Array.ofList

/// The one dispatch, as a promise the Emit block can await. Every tool — in-process today,
/// proxied tomorrow — arrives here, which is what makes a single audit seam possible.
let private invokeOf (registry: ToolRegistry) : string -> string -> string -> JS.Promise<JsToolAnswer> =
    fun ns name args ->
        async {
            match! registry.Invoke { Namespace = ns; Name = name; Arguments = args } with
            | Ok answer -> return unbox<JsToolAnswer> {| ok = true; text = answer.Text |}
            | Error reason -> return unbox<JsToolAnswer> {| ok = false; text = reason |}
        }
        |> Async.StartAsPromise

/// Every tool ONE turn can reach, assembled once: the session's own registry, plus a
/// namespace per MCP server it was given (Plan 17), wrapped in the audit seam (Plan 16,
/// part C) over the merged whole — applying it per server would let a provider added later
/// arrive with its own logging, or with none.
///
/// It lives here rather than inside the runner below because "what can this turn call, and
/// what happens when it does" is a question worth answering without a model in the loop: a
/// harness that drives a tool call drives THIS, so the thing it exercises is the thing a
/// turn exercises rather than a second assembly that resembles it.
///
/// A merge refusal can only be a BUG — resolution already made the names unique — so it is
/// reported and the turn proceeds on the session's own tools rather than failing. A turn
/// that cannot reach a printer is a smaller problem than a turn that will not run.
let registryFor (capabilities: AgentCapabilities) : ToolRegistry =
    let own = AgentTools.registry capabilities
    let merged =
        match ToolRegistry.mergeAll (own :: capabilities.ForeignTools) with
        | Ok registry -> registry
        | Error reason ->
            eprintfn "mcp: two registries claim one namespace (%s); using the session's own tools" reason
            own
    merged |> ToolUseLog.wrap capabilities.RecordToolUse

/// The Claude Agent SDK–backed `RunAgent`, over this session's data directory (the CLI's
/// scratch HOME hangs off it) and parameterized by the turn's credential:
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
let runWith (dataDir: string) (credential: (string * string) option) : RunAgent =
    fun context capabilities signal onChunk ->
        async {
            let home = Sandboxes.SessionLayout.agentHome dataDir
            Fs.ensureDir home
            let ambient = Sandboxes.ambientEnv ()
            let env = Sandboxes.AgentSandbox.envFor ambient home credential
            // What this turn can call, assembled where every driver of a tool call assembles
            // it — the registry, then the audit, in that order and only once.
            let registry = registryFor capabilities
            let! outcome =
                runQuery
                    {| system = context.SystemPrompt
                       prompt = promptOf context
                       // "" = no choice, which is the provider's own default. The turn
                       // carries the choice rather than the runner holding one, so a
                       // person changing it changes the next turn and nothing else.
                       model = context.Model |> Option.map ModelId.value |> Option.defaultValue "" |}
                    (toEnvObj (Map.toArray env))
                    (claudePath ())
                    (descriptorsOf registry)
                    (invokeOf registry)
                    (ToolRegistry.allowedTools registry |> Array.ofList)
                    (fun text -> onChunk { Text = text })
                    signal.OnAbort
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

/// The ambient-credential runner over a given data directory (existing call sites and the
/// env fallback). The data dir is where the CLI's scratch HOME goes, so a caller that has no
/// launch of its own passes `Launch.unlaunched.DataDir` and says so by doing it.
let run (dataDir: string) : RunAgent = runWith dataDir None
