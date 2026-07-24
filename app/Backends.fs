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

/// Docker-backed environments through the `Fable.Dockerode` bindings (the Engine API over
/// the local socket — no `docker` CLI). Containers and their workspace volume are named by
/// the session id (always a valid Docker object name; see `SessionId.mint`) and labelled
/// `yession-session=<id>`. The authority layer above is engine-independent, so scoping
/// guarantees do not depend on this adapter.
module DockerBackend =

    module DK = Fable.Dockerode

    [<Emit("setTimeout($0, $1)")>]
    let private setTimeout (cb: unit -> unit) (ms: float) : obj = jsNative

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

    /// Resolve `spec.EnvironmentVariables` to Docker `Env` entries (`KEY=VALUE`) through
    /// the injected, session-scoped resolver (Plan 06: the Manager's store-backed
    /// precedence walk, or the bare process-env fallback), failing on an unresolved
    /// secret reference. This is the ONLY moment a secret value exists on the start
    /// path — it goes straight into the container's env.
    let private resolveEnv (resolveSecret: SecretStore.ResolveSecret) (sessionId: SessionId) (spec: EnvironmentSpec) : Async<Result<string array, string>> =
        let rec walk acc entries =
            async {
                match entries with
                | [] -> return Ok (acc |> List.rev |> List.toArray)
                | (name, PlainValue v) :: rest -> return! walk (sprintf "%s=%s" name v :: acc) rest
                | (name, SecretRef secret) :: rest ->
                    match! resolveSecret sessionId secret with
                    | Error e -> return Error e
                    | Ok value -> return! walk (sprintf "%s=%s" name value :: acc) rest
            }
        walk [] (spec.EnvironmentVariables |> Map.toList)

    /// One `HostConfig.Mounts` entry from a typed mount.
    let private mountObj (sessionId: SessionId) (m: ContainerMount) : obj =
        let source, kind =
            match m.Source with
            | HostPath p -> p, "bind"
            | NamedVolume v -> v, "volume"
            | SessionWorkspace -> SessionId.value sessionId, "volume"
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

    /// Is a Docker daemon reachable? (Gates the integration suite.)
    let daemonAvailable () : Async<bool> =
        async {
            try
                let client = DK.create ()
                do! client.ping () |> Async.AwaitPromise |> Async.Ignore
                return true
            with _ -> return false
        }

    let create (resolveSecret: SecretStore.ResolveSecret) : ContainerBackend =
        let client = DK.create ()

        { Start =
            fun sessionId spec ->
                async {
                    try
                        // Resolve the image: build it from the context, or pull the named image.
                        let! imageResult =
                            async {
                                match spec.Build with
                                | Some build ->
                                    let tag = "yession-build-" + (SessionId.value sessionId).ToLower ()
                                    let src =
                                        readdirSync nodeFs build.ContextPath
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
                        let! envResult = resolveEnv resolveSecret sessionId spec
                        match imageResult, envResult with
                        | Error reason, _ -> return Error reason
                        | _, Error reason -> return Error reason
                        | Ok image, Ok env ->
                            let name = SessionId.value sessionId
                            let workspaceTarget = defaultArg spec.WorkingDirectory "/workspace"
                            // Always attach the session's named workspace volume, unless an
                            // explicit mount already claims the workspace path.
                            let hasWorkspaceMount = spec.Mounts |> List.exists (fun m -> m.Target = workspaceTarget)
                            let workspaceMounts =
                                if hasWorkspaceMount then []
                                else [ createObj [ "Type", box "volume"; "Source", box name; "Target", box workspaceTarget; "ReadOnly", box false ] ]
                            let mounts = workspaceMounts @ (spec.Mounts |> List.map (mountObj sessionId))
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
                                          "HostConfig", box (createObj [ "Mounts", box (List.toArray mounts) ]) ])
                                |> Async.AwaitPromise
                            do! container.start () |> Async.AwaitPromise |> Async.Ignore
                            return Ok container.id
                    with ex -> return Error (sprintf "docker start failed: %s" ex.Message)
                }
          Stop =
            fun containerId ->
                async {
                    try
                        do! client.getContainer(containerId).remove (createObj [ "force", box true ]) |> Async.AwaitPromise |> Async.Ignore
                        return Ok ()
                    with ex -> return Error (sprintf "docker remove failed: %s" ex.Message)
                }
          Execute =
            fun containerId request onChunk ->
                async {
                    try
                        let container = client.getContainer containerId
                        let execOpts =
                            [ "Cmd", box (List.toArray (request.Executable :: request.Arguments))
                              "AttachStdout", box true
                              "AttachStderr", box true
                              "Env", box (request.Environment |> Map.toList |> List.map (fun (k, v) -> sprintf "%s=%s" k v) |> List.toArray) ]
                            @ (match request.WorkingDirectory with Some w -> [ "WorkingDir", box w ] | None -> [])
                            |> createObj
                        let! exec = container.exec execOpts |> Async.AwaitPromise
                        let! stream = exec.start (createObj []) |> Async.AwaitPromise
                        let stdout = DK.createPassThrough ()
                        let stderr = DK.createPassThrough ()
                        client.modem.demuxStream (stream, stdout, stderr)
                        let emit s (d: obj) = onChunk { CommandId = request.CommandId; Stream = s; Text = bufToStr d }
                        stdout.on ("data", emit Stdout) |> ignore
                        stderr.on ("data", emit Stderr) |> ignore
                        // Resolve on stream end, or on the command timeout (whichever first).
                        let! timedOut =
                            Async.FromContinuations(fun (cont, _, _) ->
                                let mutable settled = false
                                let finish v = if not settled then (settled <- true; cont v)
                                match request.Timeout with
                                | Some t -> setTimeout (fun () -> finish true) t.TotalMilliseconds |> ignore
                                | None -> ()
                                stream.on ("end", fun _ -> finish false) |> ignore
                                stream.on ("error", fun _ -> finish false) |> ignore)
                        if timedOut then return CommandTimedOut
                        else
                            let! inspect = exec.inspect () |> Async.AwaitPromise
                            let code = exitCodeOf inspect
                            return (if code = 0 then CommandSucceeded 0 else CommandFailed code)
                    with ex -> return CommandExecutionFailed ex.Message
                }
        }

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
