module Yession.Host.Spawn

// Child-process interop for the Manager (Phase 4, Step 23): spawn a Session Process,
// await its readiness line, observe its exit, stop it. The spawn contract is
// environment variables in and exactly one JSON readiness line out on stdout
// (Plan 02 § Topology); everything else the child prints is
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

[<Emit("(function (chunk) { return typeof chunk === 'string' ? chunk : chunk.toString('utf8') })($0)")>]
let private chunkToString (chunk: obj) : string = jsNative

[<Emit("(function (line) { try { const p = JSON.parse(line); return (p && p.yession === 'ready' && typeof p.port === 'number') ? p.port : null } catch { return null } })($0)")>]
let private parseReadyLine (line: string) : int option = jsNative

/// The child's build, off the same readiness line. Absent from a session bundle older than the
/// field, which must still launch — so this is an option, never a launch precondition. Public
/// only so that back-compat can be asserted directly.
[<Emit("(function (line) { try { const p = JSON.parse(line); return (p && typeof p.version === 'string') ? p.version : null } catch { return null } })($0)")>]
let parseReadyVersion (line: string) : string option = jsNative

/// Refuse a session from a different MAJOR version — the one difference that says their
/// control protocol may genuinely disagree.
///
/// This used to be a warning that launched anyway, which was defensible while the session
/// binary was whatever shipped beside the Manager. It is not defensible once a deployment
/// points `YESSION_SPAWN_BIN` at a floating path (Plan 11), because then a major bump
/// upstream silently pairs two processes that no longer agree, and the symptom surfaces
/// later as something else entirely.
///
/// Refusing is also self-correcting where it matters: no session can start, the running set
/// drains to empty, and an operator whose promotion rule waits for quiescence restarts the
/// Manager on its own. A build that cannot state a release version (`dev`, `test`) is never
/// compared — those are the local and test paths, where the two halves are built together.
/// Both versions explicit, so the rule is testable: `Version.current` is `test` under the
/// suite, whose major is `None`, and a comparison against it could never fire.
let majorSkewBetween (managerVersion: string) (sessionVersion: string option) : string option =
    match sessionVersion with
    | None -> None
    | Some session ->
        match Version.majorOf managerVersion, Version.majorOf session with
        | Some ours, Some theirs when ours <> theirs ->
            Some (
                sprintf
                    "version skew: this manager is %s and the session binary is %s — their control protocol may disagree, so the launch was refused. Restart the manager on the matching build."
                    managerVersion
                    session
            )
        | _ -> None

let majorSkew (sessionVersion: string option) : string option =
    majorSkewBetween Version.current sessionVersion

[<Emit("setTimeout($1, $0)")>]
let private setTimeout (ms: int) (callback: unit -> unit) : obj = jsNative

[<Emit("clearTimeout($0)")>]
let private clearTimeout (handle: obj) : unit = jsNative

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

/// What a launch produced: the handle, the port it serves on, and the BUILD it reported.
///
/// The build was already on the readiness line and already read — to refuse a major skew —
/// and then dropped on the floor. Which meant the one process that knows what every live
/// session is running answered no question about it, and a session that outlived a
/// promotion kept executing an old image with nothing anywhere saying so.
///
/// `None` for a bundle older than the field, which must still launch: what the Manager does
/// not know it says nothing about, rather than inventing a version-shaped placeholder.
type LaunchedSession =
    { Child : RunningChild
      Port : int
      Build : string option }

/// Spawn `command args` with `env` merged over the parent environment, and resolve
/// once the child prints its readiness line (or fails: early exit / timeout). The
/// returned handle observes the child for the rest of its life.
let launch
    (command: string)
    (args: string list)
    (env: (string * string) list)
    (timeoutMs: int)
    : Async<Result<LaunchedSession, string>> =
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
        let settle (result: Result<LaunchedSession, string>) =
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
                    // The version arrives on the readiness line, so this is the first
                    // moment the pairing can be checked — and the last moment before the
                    // Manager starts treating the child as a working session.
                    let build = parseReadyVersion line
                    match majorSkew build with
                    | Some reason ->
                        // Settle first: killing the child fires its exit handler, which
                        // would otherwise settle this launch with "exited before ready" and
                        // bury the actual reason.
                        settle (Error reason)
                        running.Kill ()
                    | None -> settle (Ok { Child = running; Port = port; Build = build })
                | None ->
                    if line.Trim().Length > 0 then printfn "[session %d] %s" child.pid line))
