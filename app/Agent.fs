module Yession.Host.Agent

// The real agent runner: an adapter from the `RunAgent` capability to the Claude Agent
// SDK. The turn's typed capabilities are exposed to the model as MCP tools —
// `ensure_environment` and `execute_command` — so a live agent can lazily start the
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

[<Emit("""(async () => {
  try {
    const sdk = await import('@anthropic-ai/claude-agent-sdk')
    const { z } = await import('zod')
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
        )
      ]
    })
    const q = sdk.query({
      prompt: $1,
      options: {
        systemPrompt: $0,
        maxTurns: 8,
        settingSources: [],
        includePartialMessages: true,
        mcpServers: { yession },
        allowedTools: ['mcp__yession__ensure_environment', 'mcp__yession__execute_command'],
        ...($2 ? { pathToClaudeCodeExecutable: $2 } : {})
      }
    })
    let body = ''
    let streamed = ''
    let failed = null
    for await (const m of q) {
      if (m.type === 'stream_event') {
        const e = m.event
        if (e && e.type === 'content_block_delta' && e.delta && typeof e.delta.text === 'string') {
          $5(e.delta.text)
          streamed += e.delta.text
        }
      } else if (m.type === 'result') {
        if (m.subtype === 'success') body = (typeof m.result === 'string' && m.result !== '') ? m.result : streamed
        else failed = 'agent run ended: ' + m.subtype
      }
    }
    return failed ? { ok: false, body: '', reason: failed } : { ok: true, body, reason: '' }
  } catch (err) {
    return { ok: false, body: '', reason: String((err && err.message) || err) }
  }
})()""")>]
let private runQuery
    (systemPrompt: string)
    (prompt: string)
    (claudePath: string)
    (ensure: string -> JS.Promise<string>)
    (executeCommand: string -> string array -> JS.Promise<string>)
    (onChunk: string -> unit)
    : JS.Promise<RunOutcome> =
    jsNative

/// Some sandboxes disallow the SDK's own vendored executable; `YESSION_CLAUDE_PATH`
/// points the SDK at a system Claude Code install instead. Empty = SDK default.
let private claudePath () = Interop.envOr "YESSION_CLAUDE_PATH" ""

/// One prompt per turn: the completed conversation as a transcript plus the message to
/// answer. Built from the projection only — draft/Yjs state never appears here.
let private promptOf (context: AgentContextPack) : string =
    let label (author: ActorRef) =
        match author with
        | HumanPeer p -> PeerId.value p
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

/// The Claude Agent SDK–backed `RunAgent`. Streams text deltas as chunks; the typed
/// capabilities surface as MCP tools; failures are values, never exceptions.
let run : RunAgent =
    fun context capabilities onChunk ->
        async {
            let! outcome =
                runQuery
                    context.SystemPrompt
                    (promptOf context)
                    (claudePath ())
                    (ensureFor capabilities)
                    (executeFor capabilities)
                    (fun text -> onChunk { Text = text })
                |> Async.AwaitPromise
            return if outcome.ok then AgentCompleted outcome.body else AgentFailed outcome.reason
        }
