module Yession.Host.Spawn

// Child-process interop for the Manager (Phase 4, Step 23): spawn a Session Process,
// await its readiness line, observe its exit, stop it. The spawn contract is
// environment variables in and exactly one JSON readiness line out on stdout
// (docs/plans/02-manager-process.md § Topology); everything else the child prints is
// passed through as logs.

open Fable.Core
open Fable.Core.JsInterop

type [<AllowNullLiteral>] Child =
    abstract pid : int
    abstract kill : string -> bool
    abstract on : string * (obj -> unit) -> Child

[<Import("spawn", "node:child_process")>]
let private spawnRaw : obj = jsNative

// stdin is a pipe on purpose: the child watches it and exits when the Manager dies
// (the kernel closes the pipe even on SIGKILL), so sessions never outlive their
// Manager. stdout is parsed for the readiness line; stderr passes through.
[<Emit("$0($1, $2, { env: { ...process.env, ...Object.fromEntries($3) }, stdio: ['pipe', 'pipe', 'inherit'] })")>]
let private spawnWithEnv (spawn: obj) (command: string) (args: string array) (env: (string * string) array) : Child = jsNative

[<Emit("$0.stdout.on('data', $1)")>]
let private onStdout (child: Child) (handler: obj -> unit) : unit = jsNative

[<Emit("typeof $0 === 'string' ? $0 : $0.toString('utf8')")>]
let private chunkToString (chunk: obj) : string = jsNative

[<Emit("(() => { try { const p = JSON.parse($0); return (p && p.yession === 'ready' && typeof p.port === 'number') ? p.port : null } catch { return null } })()")>]
let private parseReadyLine (line: string) : int option = jsNative

[<Emit("setTimeout($1, $0)")>]
let private setTimeout (ms: int) (callback: unit -> unit) : obj = jsNative

[<Emit("clearTimeout($0)")>]
let private clearTimeout (handle: obj) : unit = jsNative

/// How a launch attempt ended.
type LaunchResult =
    | LaunchReady of port: int
    | LaunchFailed of reason: string

/// A running (or exited) child session process.
type RunningChild =
    { Pid : int
      /// SIGTERM now; the caller escalates if needed.
      Terminate : unit -> unit
      Kill : unit -> unit
      /// Register for the child's exit (fires once, with the exit code if any).
      OnExit : (int option -> unit) -> unit
      /// Has the child exited?
      HasExited : unit -> bool }

/// Spawn `command args` with `env` merged over the parent environment, and resolve
/// once the child prints its readiness line (or fails: early exit / timeout). The
/// returned handle observes the child for the rest of its life.
let launch
    (command: string)
    (args: string list)
    (env: (string * string) list)
    (timeoutMs: int)
    : Async<Result<RunningChild * int, string>> =
    Async.FromContinuations (fun (cont, _, _) ->
        let child = spawnWithEnv spawnRaw command (Array.ofList args) (Array.ofList env)

        let mutable exited : int option option = None // Some code = exited (code option)
        let mutable exitWaiters : (int option -> unit) list = []
        child.on ("exit", fun code ->
            let code = if isNull code then None else Some (unbox<int> code)
            exited <- Some code
            let waiters = exitWaiters
            exitWaiters <- []
            // Registration order: the Manager's bookkeeping (registered at launch)
            // must observe the exit before any stop/wait continuation resumes.
            waiters |> List.rev |> List.iter (fun w -> w code)) |> ignore

        let running =
            { Pid = child.pid
              Terminate = fun () -> child.kill "SIGTERM" |> ignore
              Kill = fun () -> child.kill "SIGKILL" |> ignore
              OnExit =
                fun waiter ->
                    match exited with
                    | Some code -> waiter code
                    | None -> exitWaiters <- waiter :: exitWaiters
              HasExited = fun () -> Option.isSome exited }

        let mutable settled = false
        let settle (result: Result<RunningChild * int, string>) =
            if not settled then
                settled <- true
                cont result

        let timer = setTimeout timeoutMs (fun () ->
            running.Kill ()
            settle (Error (sprintf "session process not ready within %dms" timeoutMs)))

        running.OnExit (fun code ->
            settle (Error (sprintf "session process exited before ready (code %A)" code)))

        // Accumulate stdout and scan complete lines for the readiness JSON; anything
        // else is a log line and passes through.
        let mutable buffer = ""
        onStdout child (fun chunk ->
            buffer <- buffer + chunkToString chunk
            let parts = buffer.Split '\n'
            buffer <- parts.[parts.Length - 1]
            for line in parts.[0 .. parts.Length - 2] do
                match parseReadyLine line with
                | Some port ->
                    clearTimeout timer
                    settle (Ok (running, port))
                | None ->
                    if line.Trim().Length > 0 then printfn "[session %d] %s" child.pid line))
