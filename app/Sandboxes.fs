module Yession.Host.Sandboxes

// The sandbox backends behind the `CreateSandbox` seam. Every decision — backend
// choice, environment assembly, policy construction — is a pure F# function here,
// unit-testable in the cheap tier; the `[<Emit>]` blocks below stay dumb (spawn, wire
// streams, kill) and never decide anything.
//
// The host backend passes the policy env to the child VERBATIM: no backend ever merges
// `process.env` into a spawned command again — that merge (the old Manager-side
// LocalProcessBackend) is exactly how `ANTHROPIC_API_KEY` leaked into every
// agent-issued command.

open Fable.Core
open Fable.Core.JsInterop
open Yession.Domain

// --- Pure policy assembly ----------------------------------------------------------------

/// The variables a host-backend command may inherit from the Session Process's own
/// environment: what a shell needs to resolve and run programs, and nothing that
/// authenticates anything. An allowlist, so a variable is shared by decision, never by
/// default.
let hostBaselineNames : string list =
    [ "PATH"; "HOME"; "TMPDIR"; "TMP"; "TEMP"; "LANG"; "LC_ALL"; "LC_CTYPE"; "TZ"; "USER"; "LOGNAME"; "SHELL" ]

/// Filter an ambient environment down to the host baseline.
let hostBaseline (ambient: Map<string, string>) : Map<string, string> =
    ambient |> Map.filter (fun name _ -> List.contains name hostBaselineNames)

/// Right-biased merge: `overrides` win over `baseline`.
let mergeEnv (baseline: Map<string, string>) (overrides: Map<string, string>) : Map<string, string> =
    overrides |> Map.fold (fun acc key value -> Map.add key value acc) baseline

/// Resolve the spec's environment variables: plain values verbatim, secret references
/// through the injected resolver. Called fresh at every sandbox (re)creation — the
/// resolved plaintext goes into the policy env and nowhere else.
let resolveVariables
    (resolveSecret: SecretName -> Async<Result<string, string>>)
    (variables: Map<string, EnvironmentVariableRef>)
    : Async<Result<Map<string, string>, string>> =
    let rec walk acc entries =
        async {
            match entries with
            | [] -> return Ok (Map.ofList (List.rev acc))
            | (name, PlainValue value) :: rest -> return! walk ((name, value) :: acc) rest
            | (name, SecretRef secret) :: rest ->
                match! resolveSecret secret with
                | Error e -> return Error (sprintf "%s: %s" (SecretName.value secret) e)
                | Ok value -> return! walk ((name, value) :: acc) rest
        }
    walk [] (Map.toList variables)

/// Assemble a sandbox policy for the configured backend. Host (and srt) sandboxes get
/// the baseline allowlist under the spec's variables; a docker image supplies its own
/// base environment, so only the spec's variables inject there.
let policyFor
    (backend: SandboxBackend)
    (ambient: Map<string, string>)
    (resolved: Map<string, string>)
    (workspace: string option)
    : SandboxPolicy =
    let env =
        match backend with
        | HostBackend
        | SrtBackend -> mergeEnv (hostBaseline ambient) resolved
        | DockerBackend -> resolved
    { ReadPaths = []
      WritePaths = workspace |> Option.toList
      // Egress restriction arrives with the srt backend; host and docker are
      // unrestricted today.
      AllowedDomains = None
      Env = env
      WorkingDirectory = workspace }

/// A one-line description of the backend + spec for the start-requested event.
let summaryFor (backend: SandboxBackend) (spec: EnvironmentSpec) : string =
    match backend, spec.Image with
    | DockerBackend, Some image ->
        sprintf "docker:%s%s" image.Name (image.Tag |> Option.map ((+) ":") |> Option.defaultValue "")
    | _ -> SandboxBackend.describe backend

[<Emit("Object.entries(process.env).filter(([, v]) => typeof v === 'string')")>]
let private ambientEntries () : (string * string) array = jsNative

/// The Session Process's own environment, as data (the input `policyFor` filters).
let ambientEnv () : Map<string, string> = ambientEntries () |> Map.ofArray

/// The per-(re)creation policy thunk `SessionEnvironment.create` consumes: resolve the
/// spec's secret references fresh, then assemble the policy.
let preparePolicy
    (backend: SandboxBackend)
    (resolveSecret: SecretName -> Async<Result<string, string>>)
    (workspace: string option)
    (spec: EnvironmentSpec)
    : unit -> Async<Result<SandboxPolicy, string>> =
    fun () ->
        async {
            match! resolveVariables resolveSecret spec.EnvironmentVariables with
            | Error e -> return Error e
            | Ok resolved -> return Ok (policyFor backend (ambientEnv ()) resolved workspace)
        }

// --- A buffered one-shot: settle once, deliver to every (even late) awaiter --------------

type private OneShot<'a> () =
    let mutable outcome : 'a option = None
    let mutable waiters : ('a -> unit) list = []
    member _.Settle (value: 'a) =
        match outcome with
        | Some _ -> ()
        | None ->
            outcome <- Some value
            let pending = waiters
            waiters <- []
            pending |> List.iter (fun w -> w value)
    member _.Await : Async<'a> =
        Async.FromContinuations (fun (cont, _, _) ->
            match outcome with
            | Some value -> cont value
            | None -> waiters <- cont :: waiters)

// --- Host: explicitly unsandboxed --------------------------------------------------------

/// Plain child processes of the Session Process. No confinement — the documented
/// default for now — but the env discipline still holds: the child sees exactly the
/// policy env plus the request's, never the parent's.
module HostSandbox =

    let private childProcess : obj = importAll "node:child_process"

    // `detached: true` makes the child its own process group leader, so `Kill` can take
    // the whole tree with one signal to `-pid`.
    [<Emit("$0.spawn($1, $2, { cwd: $3 || undefined, env: Object.fromEntries($4), stdio: ['pipe', 'pipe', 'pipe'], detached: true })")>]
    let private spawnChild (cp: obj) (executable: string) (args: string array) (cwd: string) (env: (string * string) array) : obj = jsNative

    [<Emit("$0.stdout.on('data', (d) => $1(String(d)))")>]
    let private onStdout (child: obj) (handler: string -> unit) : unit = jsNative

    [<Emit("$0.stderr.on('data', (d) => $1(String(d)))")>]
    let private onStderr (child: obj) (handler: string -> unit) : unit = jsNative

    [<Emit("$0.on('error', (e) => $1(String((e && e.message) || e)))")>]
    let private onError (child: obj) (handler: string -> unit) : unit = jsNative

    [<Emit("$0.on('close', (c) => $1(c == null ? -1 : c))")>]
    let private onClose (child: obj) (handler: int -> unit) : unit = jsNative

    [<Emit("(() => { try { $0.stdin.write($1) } catch {} })()")>]
    let private writeStdin (child: obj) (text: string) : unit = jsNative

    [<Emit("(() => { try { $0.stdin.end() } catch {} })()")>]
    let private endStdin (child: obj) : unit = jsNative

    [<Emit("(() => { try { process.kill(-$0.pid, 'SIGKILL') } catch { try { $0.kill('SIGKILL') } catch {} } })()")>]
    let private killTree (child: obj) : unit = jsNative

    let create () : CreateSandbox =
        fun policy ->
            async {
                policy.WorkingDirectory |> Option.iter Fs.ensureDir
                // Live children, so Dispose takes down anything still running when the
                // session's environment stops.
                let mutable live : (int * (unit -> unit)) list = []
                let mutable nextChild = 0
                let spawn (exec: SandboxExec) (onChunk: OutputStream * string -> unit) =
                    async {
                        let env = mergeEnv policy.Env exec.Env
                        let cwd =
                            exec.WorkingDirectory
                            |> Option.orElse policy.WorkingDirectory
                            |> Option.defaultValue ""
                        let child =
                            spawnChild childProcess exec.Executable (List.toArray exec.Arguments) cwd (Map.toArray env)
                        let childId = nextChild
                        nextChild <- nextChild + 1
                        live <- (childId, fun () -> killTree child) :: live
                        let ended = OneShot<SandboxRun> ()
                        onStdout child (fun text -> onChunk (Stdout, text))
                        onStderr child (fun text -> onChunk (Stderr, text))
                        onError child (fun reason ->
                            live <- live |> List.filter (fun (id, _) -> id <> childId)
                            ended.Settle (SandboxRunFailed reason))
                        onClose child (fun code ->
                            live <- live |> List.filter (fun (id, _) -> id <> childId)
                            ended.Settle (SandboxExited code))
                        return
                            Ok
                                { WriteStdin = writeStdin child
                                  CloseStdin = fun () -> endStdin child
                                  Kill = fun () -> killTree child
                                  Exited = ended.Await }
                    }
                return
                    Ok
                        { Ref = "host"
                          Spawn = spawn
                          Dispose =
                            fun () ->
                                async {
                                    live |> List.iter (fun (_, kill) -> kill ())
                                    live <- []
                                } }
            }

// --- Docker: a full isolated userland ----------------------------------------------------

/// Docker-backed sandboxes through the `Fable.Dockerode` bindings (the Engine API over
/// the local socket — no `docker` CLI). The container and its workspace volume are
/// named by the sandbox name (the session id — always a valid Docker object name) and
/// labelled `yession-session=<name>`, so the existing cleanup sweep keeps working.
module DockerSandbox =

    module DK = Fable.Dockerode

    [<Emit("$0 == null")>]
    let private isNil (o: obj) : bool = jsNative

    [<Emit("$0.toString('utf8')")>]
    let private bufToStr (b: obj) : string = jsNative

    [<Emit("($0.ExitCode == null ? -1 : $0.ExitCode)")>]
    let private exitCodeOf (inspect: obj) : int = jsNative

    let private nodeFs : obj = importAll "node:fs"

    [<Emit("$0.readdirSync($1)")>]
    let private readdirSync (fs: obj) (dir: string) : string array = jsNative

    /// Resolve `spec.Image` (default `alpine:3`) to a `name:tag` string.
    let private imageRef (spec: EnvironmentSpec) : string =
        match spec.Image with
        | Some i -> i.Name + (i.Tag |> Option.map ((+) ":") |> Option.defaultValue "")
        | None -> "alpine:3"

    /// One `HostConfig.Mounts` entry from a typed mount.
    let private mountObj (name: string) (m: ContainerMount) : obj =
        let source, kind =
            match m.Source with
            | HostPath p -> p, "bind"
            | NamedVolume v -> v, "volume"
            | SessionWorkspace -> name, "volume"
        createObj
            [ "Type", box kind
              "Source", box source
              "Target", box m.Target
              "ReadOnly", box (m.Mode = ReadOnly) ]

    /// Drain a build/pull progress stream; resolves when Docker signals completion.
    let private drainProgress (client: DK.Docker) (stream: DK.Stream) : Async<Result<unit, string>> =
        Async.FromContinuations(fun (cont, _, _) ->
            client.modem.followProgress (stream, fun err _ ->
                if isNil err then cont (Ok ()) else cont (Error (bufToStr err))))

    /// Count containers (running or not) carrying a `yession-session` label value — lets
    /// tests assert a stopped session leaves nothing behind.
    let countByLabel (label: string) : Async<int> =
        async {
            let client = DK.create ()
            let! arr =
                client.listContainers (createObj [ "all", box true; "filters", box (createObj [ "label", box [| label |] ]) ])
                |> Async.AwaitPromise
            return arr.Length
        }

    let create (name: string) (spec: EnvironmentSpec) : CreateSandbox =
        fun policy ->
            async {
                try
                    let client = DK.create ()
                    // Resolve the image: build it from the context, or pull the named image.
                    let! imageResult =
                        async {
                            match spec.Build with
                            | Some build ->
                                let tag = "yession-build-" + name.ToLower ()
                                let src = readdirSync nodeFs build.ContextPath
                                let opts =
                                    [ "t", box tag ]
                                    @ (match build.DockerfilePath with Some d -> [ "dockerfile", box d ] | None -> [])
                                    |> createObj
                                let! stream =
                                    client.buildImage (createObj [ "context", box build.ContextPath; "src", box src ], opts)
                                    |> Async.AwaitPromise
                                let! drained = drainProgress client stream
                                return drained |> Result.map (fun () -> tag)
                            | None ->
                                let image = imageRef spec
                                // Pull only when the image is absent locally — matches
                                // `docker run`, and avoids re-hitting the registry (and its
                                // transient failures) on every container start.
                                let! present =
                                    async {
                                        try
                                            do! client.getImage(image).inspect () |> Async.AwaitPromise |> Async.Ignore
                                            return true
                                        with _ -> return false
                                    }
                                if present then return Ok image
                                else
                                    let! stream = client.pull image |> Async.AwaitPromise
                                    let! drained = drainProgress client stream
                                    return drained |> Result.map (fun () -> image)
                        }
                    match imageResult with
                    | Error reason -> return Error reason
                    | Ok image ->
                        let workspaceTarget =
                            policy.WorkingDirectory
                            |> Option.orElse spec.WorkingDirectory
                            |> Option.defaultValue "/workspace"
                        // Always attach the sandbox's named workspace volume, unless an
                        // explicit mount already claims the workspace path.
                        let hasWorkspaceMount = spec.Mounts |> List.exists (fun m -> m.Target = workspaceTarget)
                        let workspaceMounts =
                            if hasWorkspaceMount then []
                            else [ createObj [ "Type", box "volume"; "Source", box name; "Target", box workspaceTarget; "ReadOnly", box false ] ]
                        let mounts = workspaceMounts @ (spec.Mounts |> List.map (mountObj name))
                        let env =
                            policy.Env |> Map.toList |> List.map (fun (k, v) -> sprintf "%s=%s" k v) |> List.toArray
                        // The named volume persists across container restarts by design;
                        // the label lets cleanup find it (see the workflow teardown).
                        do! client.createVolume (createObj [ "Name", box name; "Labels", box (createObj [ "yession-session", box name ]) ]) |> Async.AwaitPromise |> Async.Ignore
                        // Clear a same-named crash leftover so `createContainer` can reuse the name.
                        try do! client.getContainer(name).remove (createObj [ "force", box true ]) |> Async.AwaitPromise |> Async.Ignore
                        with _ -> ()
                        let! container =
                            client.createContainer (
                                createObj
                                    [ "name", box name
                                      "Image", box image
                                      "Labels", box (createObj [ "yession-session", box name ])
                                      "Env", box env
                                      "WorkingDir", box workspaceTarget
                                      "Cmd", box [| "tail"; "-f"; "/dev/null" |]
                                      "HostConfig",
                                      box (
                                          createObj
                                              [ "Mounts", box (List.toArray mounts)
                                                // The workload is agent-issued commands; nothing
                                                // it runs legitimately needs a capability or a
                                                // privilege escalation.
                                                "CapDrop", box [| "ALL" |]
                                                "SecurityOpt", box [| "no-new-privileges" |] ]) ])
                            |> Async.AwaitPromise
                        do! container.start () |> Async.AwaitPromise |> Async.Ignore

                        let spawn (exec: SandboxExec) (onChunk: OutputStream * string -> unit) =
                            async {
                                try
                                    let execOpts =
                                        [ "Cmd", box (List.toArray (exec.Executable :: exec.Arguments))
                                          "AttachStdin", box true
                                          "AttachStdout", box true
                                          "AttachStderr", box true
                                          "Env", box (exec.Env |> Map.toList |> List.map (fun (k, v) -> sprintf "%s=%s" k v) |> List.toArray) ]
                                        @ (match exec.WorkingDirectory with Some w -> [ "WorkingDir", box w ] | None -> [])
                                        |> createObj
                                    let! started = container.exec execOpts |> Async.AwaitPromise
                                    // Hijack the connection so stdin rides the same socket the
                                    // output is demuxed from.
                                    let! stream = started.start (createObj [ "hijack", box true; "stdin", box true ]) |> Async.AwaitPromise
                                    let stdout = DK.createPassThrough ()
                                    let stderr = DK.createPassThrough ()
                                    client.modem.demuxStream (stream, stdout, stderr)
                                    stdout.on ("data", fun d -> onChunk (Stdout, bufToStr d)) |> ignore
                                    stderr.on ("data", fun d -> onChunk (Stderr, bufToStr d)) |> ignore
                                    let ended = OneShot<SandboxRun> ()
                                    let finish () =
                                        Async.StartImmediate (
                                            async {
                                                try
                                                    let! inspect = started.inspect () |> Async.AwaitPromise
                                                    ended.Settle (SandboxExited (exitCodeOf inspect))
                                                with ex -> ended.Settle (SandboxRunFailed ex.Message)
                                            })
                                    stream.on ("end", fun _ -> finish ()) |> ignore
                                    stream.on ("error", fun e -> ended.Settle (SandboxRunFailed (string e))) |> ignore
                                    return
                                        Ok
                                            { WriteStdin = fun text -> stream.write (box text) |> ignore
                                              CloseStdin = fun () -> stream.``end`` ()
                                              // The Engine API cannot signal an exec's process;
                                              // closing our side of the stream is the most a
                                              // caller's Kill can do (the container itself dies
                                              // with Dispose).
                                              Kill = fun () -> stream.``end`` ()
                                              Exited = ended.Await }
                                with ex -> return Error ex.Message
                            }
                        return
                            Ok
                                { Ref = container.id
                                  Spawn = spawn
                                  Dispose =
                                    fun () ->
                                        async {
                                            try
                                                do! client.getContainer(container.id).remove (createObj [ "force", box true ]) |> Async.AwaitPromise |> Async.Ignore
                                            with ex ->
                                                eprintfn "[sandbox %s] docker remove failed: %s" name ex.Message
                                        } }
                with ex -> return Error (sprintf "docker sandbox failed: %s" ex.Message)
            }

// --- The AgentSandbox: where the agent CLI process runs ----------------------------------

/// Extra names the agent CLI may inherit beyond the host baseline: outbound-proxy
/// configuration, without which a proxied deployment's CLI cannot reach the API.
let agentPassthroughNames : string list =
    [ "HTTP_PROXY"; "HTTPS_PROXY"; "NO_PROXY"; "http_proxy"; "https_proxy"; "no_proxy"
      "NODE_EXTRA_CA_CERTS"; "SSL_CERT_FILE" ]

/// The agent CLI's confinement (PR 2 of the sandboxing plan): the SDK stays in-process;
/// the CLI it spawns goes through the `spawnClaudeCodeProcess` seam with a policy env.
/// Host tier here; the srt wrap arrives with the srt backend.
module AgentSandbox =

    /// The spawned CLI's WHOLE environment: the host baseline + proxy passthrough, a
    /// per-session scratch HOME (the CLI writes `~/.claude` state there), and exactly
    /// one credential — the turn's resolved credential displaces both ambient
    /// credential variables by construction (the allowlist never admits them), and the
    /// documented ambient last resort passes exactly those two through.
    let envFor (ambient: Map<string, string>) (home: string) (credential: (string * string) option) : Map<string, string> =
        let baseline =
            ambient
            |> Map.filter (fun name _ ->
                List.contains name hostBaselineNames || List.contains name agentPassthroughNames)
        let credentials =
            match credential with
            | Some (name, value) -> Map.ofList [ name, value ]
            | None ->
                [ "ANTHROPIC_API_KEY"; "CLAUDE_CODE_OAUTH_TOKEN" ]
                |> List.choose (fun name -> ambient |> Map.tryFind name |> Option.map (fun value -> name, value))
                |> Map.ofList
        mergeEnv baseline (Map.add "HOME" home credentials)

    let private childProcess : obj = importAll "node:child_process"

    // The SDK's `spawnClaudeCodeProcess` seam. The env arriving in `options.env` IS the
    // policy env (it flows from the query's `env` option), so the spawner passes it
    // verbatim. `detached: true` makes the CLI a process-group leader, and the kill on
    // `options.signal` takes the whole tree — that signal is the SDK's FORWARDED one,
    // firing only after its stdin-EOF + grace window, so the force-kill never pre-empts
    // the CLI's graceful shutdown.
    [<Emit("""((cp) => (options) => {
  const child = cp.spawn(options.command, options.args, { cwd: options.cwd || undefined, env: options.env, stdio: ['pipe', 'pipe', 'pipe'], detached: true })
  const killTree = () => { try { process.kill(-child.pid, 'SIGKILL') } catch { try { child.kill('SIGKILL') } catch {} } }
  if (options.signal) {
    if (options.signal.aborted) killTree()
    else options.signal.addEventListener('abort', killTree, { once: true })
  }
  return child
})($0)""")>]
    let private hostSpawnerOver (cp: obj) : obj = jsNative

    /// The host-backend agent spawner, handed to the SDK as `spawnClaudeCodeProcess`.
    let hostClaudeSpawner () : obj = hostSpawnerOver childProcess

// --- Backend selection --------------------------------------------------------------------

/// The session's `CreateSandbox` for its configured backend. `Error` fails the session
/// at boot — fail closed, never a silent fallback to a weaker backend.
let forBackend (backend: SandboxBackend) (name: string) (spec: EnvironmentSpec) : Result<CreateSandbox, string> =
    match backend with
    | HostBackend -> Ok (HostSandbox.create ())
    | DockerBackend -> Ok (DockerSandbox.create name spec)
    | SrtBackend -> Error "the srt sandbox backend is not implemented yet — set host or docker"
