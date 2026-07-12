module Yession.Host.Backends

// Container backends for the Session Manager (Steps 11–13). `LocalProcessBackend` runs
// commands as local child processes of the Manager — the engine verifiable in every
// environment, so command streaming has real, repeatable coverage. A Docker adapter
// rides the same `ContainerBackend` seam and is exercised where a daemon exists.

open Fable.Core
open Fable.Core.JsInterop
open Yession.Domain
open Yession.Manager

type private SpawnOutcome =
    abstract kind : string
    abstract code : int
    abstract reason : string

[<Emit("""new Promise((resolve) => {
  let settled = false
  const finish = (r) => { if (!settled) { settled = true; resolve(r) } }
  try {
    import('node:child_process').then(({ spawn }) => {
      const child = spawn($0, $1, { cwd: $2 || undefined, env: { ...process.env, ...$3 } })
      let timer = null
      if ($4 > 0) timer = setTimeout(() => { try { child.kill('SIGKILL') } catch {} ; finish({ kind: 'timeout', code: -1, reason: '' }) }, $4)
      child.stdout.on('data', (d) => $5(String(d)))
      child.stderr.on('data', (d) => $6(String(d)))
      child.on('error', (e) => { if (timer) clearTimeout(timer); finish({ kind: 'error', code: -1, reason: String((e && e.message) || e) }) })
      child.on('close', (code) => { if (timer) clearTimeout(timer); finish({ kind: 'exit', code: code == null ? -1 : code, reason: '' }) })
    }, (e) => finish({ kind: 'error', code: -1, reason: String((e && e.message) || e) }))
  } catch (e) { finish({ kind: 'error', code: -1, reason: String((e && e.message) || e) }) }
})""")>]
let private spawnRun
    (executable: string)
    (args: string array)
    (cwd: string)
    (env: obj)
    (timeoutMs: float)
    (onStdout: string -> unit)
    (onStderr: string -> unit)
    : JS.Promise<SpawnOutcome> =
    jsNative

/// Commands as local child processes. "Containers" are logical session workspaces (no
/// OS-level isolation — acceptable for local-first development; the Docker adapter
/// provides isolation where available). The authority layer above this backend is
/// engine-independent.
module LocalProcessBackend =

    let create () : ContainerBackend =
        let mutable nextContainer = 0
        { Start =
            fun sessionId _spec ->
                async {
                    let containerId = sprintf "local-%s-%d" (SessionId.value sessionId) nextContainer
                    nextContainer <- nextContainer + 1
                    return Ok containerId
                }
          Stop = fun _ -> async { return Ok () }
          Execute =
            fun _containerId request onChunk ->
                async {
                    let envObj =
                        request.Environment
                        |> Map.toList
                        |> List.map (fun (k, v) -> k, box v)
                        |> createObj
                    let timeoutMs =
                        match request.Timeout with
                        | Some t -> t.TotalMilliseconds
                        | None -> 0.0
                    let chunk stream text =
                        onChunk { CommandId = request.CommandId; Stream = stream; Text = text }
                    let! outcome =
                        spawnRun
                            request.Executable
                            (List.toArray request.Arguments)
                            (defaultArg request.WorkingDirectory "")
                            envObj
                            timeoutMs
                            (chunk Stdout)
                            (chunk Stderr)
                        |> Async.AwaitPromise
                    return
                        match outcome.kind with
                        | "exit" when outcome.code = 0 -> CommandSucceeded 0
                        | "exit" -> CommandFailed outcome.code
                        | "timeout" -> CommandTimedOut
                        | _ -> CommandExecutionFailed outcome.reason
                }
        }
