module Yession.Host.ControlClient

// The Session Process side of the control RPC (Phase 4, Step 24): a
// `SessionEnvironmentCapabilities` whose calls go to the Manager's control endpoint,
// authenticated by this launch's secret. Failures are values — a transport error
// degrades to the capability's failed result shapes, never an exception, because the
// Session Process turns them into events.

open Fable.Core
open Yession.Domain
open Yession.Manager

[<Emit("""fetch($0, { method: 'POST', headers: { 'x-yession-control': $1, 'content-type': 'application/json' }, body: $2 })
  .then(r => { if (!r.ok) throw new Error('control rpc failed: ' + r.status); return r.text() })""")>]
let private postJson (url: string) (secret: string) (body: string) : JS.Promise<string> = jsNative

// Stream the NDJSON response: each complete line goes to the callback as it arrives.
[<Emit("""(async () => {
  const res = await fetch($0, { method: 'POST', headers: { 'x-yession-control': $1, 'content-type': 'application/json' }, body: $2 })
  if (!res.ok) throw new Error('control rpc failed: ' + res.status)
  const reader = res.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''
  for (;;) {
    const { done, value } = await reader.read()
    if (done) break
    buffer += decoder.decode(value, { stream: true })
    const parts = buffer.split('\n')
    buffer = parts.pop()
    for (const line of parts) if (line.trim().length > 0) $3(line)
  }
  if (buffer.trim().length > 0) $3(buffer)
})()""")>]
let private postStream (url: string) (secret: string) (body: string) (onLine: string -> unit) : JS.Promise<unit> = jsNative

/// Report the session's display name (its collaborative title) to the Manager, so the
/// session list reflects it. Best-effort: a transport failure is swallowed — a title that
/// fails to reach the list is cosmetic, never a reason to disturb the session.
let nameReporter (baseUrl: string) (secret: string) : string -> Async<unit> =
    fun name ->
        async {
            try
                let! _ =
                    postJson
                        (sprintf "%s/control/name" baseUrl)
                        secret
                        (ControlWire.toString ControlWire.sessionNameReport name)
                    |> Async.AwaitPromise
                return ()
            with _ -> return ()
        }

/// Build the capability record over the Manager's control endpoint.
let capabilities (baseUrl: string) (secret: string) : SessionEnvironmentCapabilities =
    let url (route: string) = sprintf "%s/control/%s" baseUrl route

    { StartContainer =
        fun spec ->
            async {
                try
                    let! text =
                        postJson (url "start") secret (ControlWire.toString ControlWire.environmentSpec spec)
                        |> Async.AwaitPromise
                    match ControlWire.fromString ControlWire.startContainerResult text with
                    | Ok result -> return result
                    | Error e -> return ContainerStartFailed (sprintf "control decode failed: %s" e)
                with e ->
                    return ContainerStartFailed (sprintf "control unreachable: %s" e.Message)
            }
      StopContainer =
        fun handle ->
            async {
                try
                    let! text =
                        postJson (url "stop") secret (ControlWire.toString ControlWire.containerHandle handle)
                        |> Async.AwaitPromise
                    match ControlWire.fromString ControlWire.stopContainerResult text with
                    | Ok result -> return result
                    | Error e -> return ContainerStopFailed (sprintf "control decode failed: %s" e)
                with e ->
                    return ContainerStopFailed (sprintf "control unreachable: %s" e.Message)
            }
      Execute =
        fun handle command onChunk ->
            async {
                let body =
                    ControlWire.toString ControlWire.executeRequest { Handle = handle; Command = command }
                // If the stream ends without a result line, the Manager died mid-command:
                // surface it as an execution failure, never a hang.
                let mutable result = CommandExecutionFailed "control stream ended without a result"
                try
                    do!
                        postStream (url "execute") secret body (fun line ->
                            match ControlWire.fromString ControlWire.executeLine line with
                            | Ok (ControlWire.OutputLine chunk) -> onChunk chunk
                            | Ok (ControlWire.ResultLine r) -> result <- r
                            | Error e -> eprintfn "control stream line decode failed: %s" e)
                        |> Async.AwaitPromise
                    return result
                with e ->
                    return CommandExecutionFailed (sprintf "control unreachable: %s" e.Message)
            } }
