module Yession.Host.Agent

// The real agent runner: an adapter from the `RunAgent` capability to the Claude Agent
// SDK. Phase 1 turns are pure conversation — no tools, no environment access, one turn.
// Requires ANTHROPIC_API_KEY in the process environment; the deterministic test suite
// never calls this (it injects scripted runners), and the live smoke test is gated on
// the key's presence, so verification stays repeatable.

open Fable.Core
open Yession.Domain

type private RunOutcome =
    abstract ok : bool
    abstract body : string
    abstract reason : string

[<Emit("""(async () => {
  try {
    const sdk = await import('@anthropic-ai/claude-agent-sdk')
    const q = sdk.query({
      prompt: $1,
      options: {
        systemPrompt: $0,
        allowedTools: [],
        maxTurns: 1,
        settingSources: [],
        includePartialMessages: true
      }
    })
    let body = ''
    let streamed = ''
    let failed = null
    for await (const m of q) {
      if (m.type === 'stream_event') {
        const e = m.event
        if (e && e.type === 'content_block_delta' && e.delta && typeof e.delta.text === 'string') {
          $2(e.delta.text)
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
let private runQuery (systemPrompt: string) (prompt: string) (onChunk: string -> unit) : JS.Promise<RunOutcome> =
    jsNative

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

/// The Claude Agent SDK–backed `RunAgent`. Streams text deltas as chunks; failures are
/// values, never exceptions.
let run : RunAgent =
    fun context onChunk ->
        async {
            let! outcome =
                runQuery context.SystemPrompt (promptOf context) (fun text -> onChunk { Text = text })
                |> Async.AwaitPromise
            return if outcome.ok then AgentCompleted outcome.body else AgentFailed outcome.reason
        }
