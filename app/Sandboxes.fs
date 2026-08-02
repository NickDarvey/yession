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
/// The domains a sandbox may reach, as configured. `None` is unrestricted, which is what
/// the unconfining backends are and all srt can honestly report for them; srt itself has
/// no unrestricted mode, so a confined sandbox always carries a list — `YESSION_SANDBOX_DOMAINS`
/// (comma- or space-separated), and an empty one where the operator named none.
let egressFor (backend: SandboxBackend) (ambient: Map<string, string>) : string list option =
    match backend with
    | HostBackend
    | DockerBackend -> None
    | SrtBackend ->
        ambient
        |> Map.tryFind "YESSION_SANDBOX_DOMAINS"
        |> Option.defaultValue ""
        |> fun raw -> raw.Split ([| ','; ' '; '\t'; '\n' |])
        |> Array.map (fun domain -> domain.Trim ())
        |> Array.filter (fun domain -> domain <> "")
        |> List.ofArray
        |> Some

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
      AllowedDomains = egressFor backend ambient
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

// --- Child processes: the one place this module spawns anything --------------------------

/// `node:child_process` behind a handle-shaped surface. Both unconfined backends run
/// their processes through here — the host backend spawns the command itself, the srt
/// backend spawns the argv srt wrapped it in — so process-group kill, stream wiring and
/// exit reporting have one implementation and behave identically under both.
module private Children =

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

    /// A live registry of the children a sandbox spawned, so `Dispose` can take down
    /// whatever is still running when the session's environment stops.
    type Registry () =
        let mutable live : (int * (unit -> unit)) list = []
        let mutable next = 0

        member _.Spawn
            (executable: string, arguments: string list, cwd: string, env: Map<string, string>)
            (onChunk: OutputStream * string -> unit)
            : SandboxProcessHandle =
            let child = spawnChild childProcess executable (List.toArray arguments) cwd (Map.toArray env)
            let id = next
            next <- next + 1
            live <- (id, fun () -> killTree child) :: live
            let forget () = live <- live |> List.filter (fun (other, _) -> other <> id)
            let ended = OneShot<SandboxRun> ()
            onStdout child (fun text -> onChunk (Stdout, text))
            onStderr child (fun text -> onChunk (Stderr, text))
            onError child (fun reason -> forget (); ended.Settle (SandboxRunFailed reason))
            onClose child (fun code -> forget (); ended.Settle (SandboxExited code))
            { WriteStdin = writeStdin child
              CloseStdin = fun () -> endStdin child
              Kill = fun () -> killTree child
              Exited = ended.Await }

        member _.KillAll () =
            live |> List.iter (fun (_, kill) -> kill ())
            live <- []

// --- Host: explicitly unsandboxed --------------------------------------------------------

/// Plain child processes of the Session Process. No confinement — the documented
/// default for now — but the env discipline still holds: the child sees exactly the
/// policy env plus the request's, never the parent's.
module HostSandbox =

    let create () : CreateSandbox =
        fun policy ->
            async {
                policy.WorkingDirectory |> Option.iter Fs.ensureDir
                let children = Children.Registry ()
                let spawn (exec: SandboxExec) (onChunk: OutputStream * string -> unit) =
                    async {
                        let env = mergeEnv policy.Env exec.Env
                        let cwd =
                            exec.WorkingDirectory
                            |> Option.orElse policy.WorkingDirectory
                            |> Option.defaultValue ""
                        return Ok (children.Spawn (exec.Executable, exec.Arguments, cwd, env) onChunk)
                    }
                return
                    Ok
                        { Ref = "host"
                          Spawn = spawn
                          Dispose = fun () -> async { children.KillAll () } }
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
                |> Interop.awaitPromise
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
                                    |> Interop.awaitPromise
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
                                            do! client.getImage(image).inspect () |> Interop.awaitPromise |> Async.Ignore
                                            return true
                                        with _ -> return false
                                    }
                                if present then return Ok image
                                else
                                    let! stream = client.pull image |> Interop.awaitPromise
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
                        do! client.createVolume (createObj [ "Name", box name; "Labels", box (createObj [ "yession-session", box name ]) ]) |> Interop.awaitPromise |> Async.Ignore
                        // Clear a same-named crash leftover so `createContainer` can reuse the name.
                        try do! client.getContainer(name).remove (createObj [ "force", box true ]) |> Interop.awaitPromise |> Async.Ignore
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
                            |> Interop.awaitPromise
                        do! container.start () |> Interop.awaitPromise |> Async.Ignore

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
                                    let! started = container.exec execOpts |> Interop.awaitPromise
                                    // Hijack the connection so stdin rides the same socket the
                                    // output is demuxed from.
                                    let! stream = started.start (createObj [ "hijack", box true; "stdin", box true ]) |> Interop.awaitPromise
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
                                                    let! inspect = started.inspect () |> Interop.awaitPromise
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
                                                do! client.getContainer(container.id).remove (createObj [ "force", box true ]) |> Interop.awaitPromise |> Async.Ignore
                                            with ex ->
                                                eprintfn "[sandbox %s] docker remove failed: %s" name ex.Message
                                        } }
                with ex -> return Error (sprintf "docker sandbox failed: %s" ex.Message)
            }

// --- srt: OS-level confinement -----------------------------------------------------------

/// How strongly a Linux srt sandbox nests. The strict profile mounts a fresh `/proc` and
/// drops every capability, which costs a NESTED user namespace — and an unprivileged
/// container (this repo's own dev container, and any deployment that runs a session
/// inside one) is not allowed to create one. `Weak` is srt's documented answer there:
/// the host's `/proc` stays visible and caps are not dropped. It is a real reduction in
/// confinement, so it is a configured choice, never a fallback the runtime picks when
/// the strict profile fails.
type SandboxNesting =
    | StrictNesting
    | WeakNesting

/// How this host must be driven to confine: the tools srt shells out to, named rather
/// than looked up on PATH (srt treats a named path as a directive and reports it missing;
/// a PATH lookup would silently take someone else's build), and how far the nesting can
/// go. Both paths are `None` on macOS, where Seatbelt ships with the OS and needs neither.
type SrtTools =
    { Bwrap : string option
      Socat : string option
      /// srt scans for the files it must deny outright (keys, shell rc files, git hooks)
      /// with ripgrep. It is as much a dependency as bubblewrap — a sandbox does not start
      /// without one — and naming it is what stops a host's incidental `rg` from deciding
      /// how a session confines.
      Ripgrep : string option
      Nesting : SandboxNesting }

/// The srt runtime configuration a policy becomes. Assembled as data so the mapping is
/// checkable without a sandbox: what is denied, what is re-allowed, and where egress may
/// go are decisions, and decisions belong in F#.
type SrtConfig =
    { /// Regions read access is removed from, before `AllowRead` opens holes in them.
      DenyRead : string list
      AllowRead : string list
      AllowWrite : string list
      /// The egress allowlist. Empty means no egress: srt has no "unrestricted", so a
      /// policy that names no domains gets none.
      AllowedDomains : string list
      Bwrap : string option
      Socat : string option
      Ripgrep : string option
      WeakNesting : bool }

/// OS-level confinement via `@anthropic-ai/sandbox-runtime` (bubblewrap on Linux,
/// Seatbelt on macOS): a wrapped spawn, no container, and egress that is ENFORCED —
/// the network namespace is unshared, so the only route out is srt's filtering proxy.
module SrtSandbox =

    /// srt redirects the sandbox's `TMPDIR` here, so it has to be writable and to exist.
    /// (`CLAUDE_CODE_TMPDIR` overrides the location, but it is read from the host process
    /// env once, not per sandbox — one path for the process is the honest shape.)
    let tmpDir = "/tmp/claude"

    /// Quote one argument for the shell srt wraps the command in. srt's Linux and macOS
    /// wrappers take a COMMAND STRING (the profile ends in `<shell> -c <wrapped>`), so an
    /// argv has to survive a shell round-trip: single-quote everything, and close/escape/
    /// reopen around any embedded quote. Nothing else is interpreted inside single quotes.
    let quoteArg (arg: string) : string =
        "'" + arg.Replace ("'", "'\\''") + "'"

    let commandLine (executable: string) (arguments: string list) : string =
        executable :: arguments |> List.map quoteArg |> String.concat " "

    /// A policy as an srt configuration.
    ///
    /// Reads are deny-then-allow: srt starts readable everywhere (the sandbox still needs
    /// its interpreter, its libraries, the store they live in), so confinement means
    /// denying the region that holds the operator's secrets — the invoking user's home —
    /// and re-allowing what the policy names. Anything the sandbox may WRITE it may also
    /// read; a workspace under the denied home would otherwise be write-only.
    ///
    /// Writes are allow-only: exactly the policy's paths, plus the temp directory srt
    /// points the sandbox at and the standard streams a process expects to be able to
    /// write.
    let configFor (tools: SrtTools) (home: string option) (policy: SandboxPolicy) : SrtConfig =
        let distinct (paths: string list) = paths |> List.distinct
        { DenyRead = home |> Option.toList
          AllowRead = distinct (policy.ReadPaths @ policy.WritePaths)
          AllowWrite = distinct (policy.WritePaths @ [ tmpDir; "/dev/stdout"; "/dev/stderr"; "/dev/null" ])
          AllowedDomains = policy.AllowedDomains |> Option.defaultValue []
          Bwrap = tools.Bwrap
          Socat = tools.Socat
          Ripgrep = tools.Ripgrep
          WeakNesting = (tools.Nesting = WeakNesting) }

    /// How this host confines, as configured. A blank tool path is an absent one: the dev
    /// shell and the installable set these per platform, and on macOS they are empty.
    /// The nesting is parsed fail-closed — an unrecognised value is a loud error rather
    /// than a guess at which way the operator meant to err.
    let toolsFrom (ambient: Map<string, string>) : Result<SrtTools, string> =
        let named name =
            ambient
            |> Map.tryFind name
            |> Option.map (fun value -> value.Trim ())
            |> Option.filter (fun value -> value <> "")
        let nesting =
            match ambient |> Map.tryFind "YESSION_SANDBOX_NESTED" |> Option.defaultValue "strict" with
            | value ->
                match value.Trim().ToLowerInvariant () with
                | ""
                | "strict" -> Ok StrictNesting
                | "weak" -> Ok WeakNesting
                | other ->
                    Error (sprintf "unknown sandbox nesting '%s' (expected strict, or weak for an unprivileged container)" other)
        nesting
        |> Result.map (fun nesting ->
            { Bwrap = named "YESSION_BWRAP_PATH"
              Socat = named "YESSION_SOCAT_PATH"
              Ripgrep = named "YESSION_RIPGREP_PATH"
              Nesting = nesting })

    [<Emit("({ network: { allowedDomains: $0, deniedDomains: [], strictAllowlist: true }, filesystem: { denyRead: $1, allowRead: $2, allowWrite: $3, denyWrite: [] }, ...($4 ? { bwrapPath: $4 } : {}), ...($5 ? { socatPath: $5 } : {}), ...($6 ? { ripgrep: { command: $6 } } : {}), ...($7 ? { enableWeakerNestedSandbox: true } : {}) })")>]
    let private configObject
        (allowedDomains: string array)
        (denyRead: string array)
        (allowRead: string array)
        (allowWrite: string array)
        (bwrap: string)
        (socat: string)
        (ripgrep: string)
        (weakNesting: bool)
        : obj = jsNative

    let private toJs (config: SrtConfig) : obj =
        configObject
            (List.toArray config.AllowedDomains)
            (List.toArray config.DenyRead)
            (List.toArray config.AllowRead)
            (List.toArray config.AllowWrite)
            (config.Bwrap |> Option.defaultValue "")
            (config.Socat |> Option.defaultValue "")
            (config.Ripgrep |> Option.defaultValue "")
            config.WeakNesting

    // The package is loaded on demand: it pulls a proxy stack and a TLS library, and a
    // session on the host backend must not pay for either. Dynamic `import` (not
    // `createRequire`) because it is ESM-only.
    [<Emit("import('@anthropic-ai/sandbox-runtime')")>]
    let private importSrt () : JS.Promise<obj> = jsNative

    [<Emit("$0.SandboxManager.isSupportedPlatform()")>]
    let private supportedPlatform (srt: obj) : bool = jsNative

    [<Emit("$0.SandboxManager.initialize($1)")>]
    let private initialize (srt: obj) (config: obj) : JS.Promise<unit> = jsNative

    [<Emit("$0.SandboxManager.checkDependenciesAsync()")>]
    let private checkDependencies (srt: obj) : JS.Promise<obj> = jsNative

    [<Emit("(($0.errors ?? []).join('; '))")>]
    let private dependencyErrors (check: obj) : string = jsNative

    [<Emit("$0.SandboxManager.wrapWithSandboxArgv($1, undefined, $2, undefined, $3 || undefined)")>]
    let private wrapArgv (srt: obj) (command: string) (custom: obj) (cwd: string) : JS.Promise<obj> = jsNative

    [<Emit("$0.argv")>]
    let private argvOf (wrapped: obj) : string array = jsNative

    [<Emit("$0.SandboxManager.updateConfig({ ...$0.SandboxManager.getConfig(), network: { ...$0.SandboxManager.getConfig().network, allowedDomains: $1 } })")>]
    let private widenAllowlist (srt: obj) (allowedDomains: string array) : unit = jsNative

    // srt's manager is a PROCESS-WIDE singleton: one filtering proxy pair, one egress
    // allowlist, initialized once. Filesystem policy is per-spawn (it rides `customConfig`
    // into the bwrap profile), so two sandboxes in a session confine their files exactly;
    // their egress allowlists, though, can only be the union — see docs/GAPS.md.
    let mutable private starting : JS.Promise<obj> option = None
    let mutable private allowed : Set<string> = Set.empty

    /// Reset the memoized process-wide manager. For tests that drive a fresh session in
    /// the same process — production initializes once and keeps it until the process ends.
    let forgetManager () =
        starting <- None
        allowed <- Set.empty

    let private managerFor (config: SrtConfig) : Async<obj> =
        match starting with
        | Some promise ->
            async {
                let! srt = Interop.awaitPromise promise
                let union = Set.union allowed (Set.ofList config.AllowedDomains)
                if union <> allowed then
                    allowed <- union
                    widenAllowlist srt (Set.toArray union)
                return srt
            }
        | None ->
            allowed <- Set.ofList config.AllowedDomains
            // Started once, here, and memoized as its promise — including a start that
            // FAILED, so every later sandbox reports the same reason instead of retrying
            // an initialization the host cannot support.
            let promise =
                Async.StartAsPromise (
                    async {
                        let! srt = Interop.awaitPromise (importSrt ())
                        if not (supportedPlatform srt) then
                            return failwith "this platform has no srt sandbox"
                        do! Interop.awaitPromise (initialize srt (toJs config))
                        let! check = Interop.awaitPromise (checkDependencies srt)
                        match dependencyErrors check with
                        | "" -> return srt
                        | errors -> return failwith errors
                    })
            starting <- Some promise
            Interop.awaitPromise promise

    let create (tools: SrtTools) (home: string option) : CreateSandbox =
        fun policy ->
            async {
                try
                    policy.WorkingDirectory |> Option.iter Fs.ensureDir
                    Fs.ensureDir tmpDir
                    let config = configFor tools home policy
                    let! srt = managerFor config
                    let children = Children.Registry ()
                    let spawn (exec: SandboxExec) (onChunk: OutputStream * string -> unit) =
                        async {
                            try
                                let env = mergeEnv policy.Env exec.Env
                                let cwd =
                                    exec.WorkingDirectory
                                    |> Option.orElse policy.WorkingDirectory
                                    |> Option.defaultValue ""
                                // This sandbox's own filesystem policy rides every spawn:
                                // the manager was initialized by whichever sandbox came
                                // first, and `customConfig` is what makes the profile this
                                // one's rather than that one's.
                                let! wrapped =
                                    Interop.awaitPromise (wrapArgv srt (commandLine exec.Executable exec.Arguments) (toJs config) cwd)
                                match List.ofArray (argvOf wrapped) with
                                | [] -> return Error "srt returned an empty argv"
                                | executable :: arguments ->
                                    return Ok (children.Spawn (executable, arguments, cwd, env) onChunk)
                            with ex -> return Error ex.Message
                        }
                    return
                        Ok
                            { Ref = "srt"
                              Spawn = spawn
                              // The manager stays up: it is process-wide, and a sibling
                              // sandbox may still be running under it. Its proxies die
                              // with the Session Process, which is the lifetime they are
                              // scoped to anyway.
                              Dispose = fun () -> async { children.KillAll () } }
                with ex -> return Error (sprintf "srt sandbox failed: %s" ex.Message)
            }

    /// Wrap one command under a policy, yielding the argv that runs it confined. The
    /// agent CLI's spawner needs exactly this and nothing else around it: it is handed a
    /// command by the SDK and has to produce a confined process from it.
    let wrapperFor (tools: SrtTools) (home: string option) (policy: SandboxPolicy) : string -> string list -> string -> Async<string list> =
        let config = configFor tools home policy
        fun executable arguments cwd ->
            async {
                let! srt = managerFor config
                let! wrapped = Interop.awaitPromise (wrapArgv srt (commandLine executable arguments) (toJs config) cwd)
                match List.ofArray (argvOf wrapped) with
                | [] -> return failwith "srt returned an empty argv"
                | argv -> return argv
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

    /// Where the confined CLI may reach. The agent's egress needs are known — the API it
    /// talks to, and the console it refreshes an OAuth credential against — so unlike the
    /// work sandbox's, this allowlist has a real default. `YESSION_AGENT_DOMAINS` replaces
    /// it wholesale for a deployment that fronts the API somewhere else.
    let defaultDomains : string list =
        [ "api.anthropic.com"; "console.anthropic.com"; "claude.ai" ]

    let domainsFrom (ambient: Map<string, string>) : string list =
        match ambient |> Map.tryFind "YESSION_AGENT_DOMAINS" with
        | None -> defaultDomains
        | Some raw ->
            raw.Split ([| ','; ' '; '\t'; '\n' |])
            |> Array.map (fun domain -> domain.Trim ())
            |> Array.filter (fun domain -> domain <> "")
            |> List.ofArray

    /// The agent's own sandbox policy: it reads and writes its scratch HOME and nothing
    /// else of the operator's, and reaches only the API hosts above. `WorkingDirectory`
    /// is left to the SDK — the CLI is spawned wherever the session runs.
    let policyFor (ambient: Map<string, string>) (home: string) (env: Map<string, string>) : SandboxPolicy =
        { ReadPaths = [ home ]
          WritePaths = [ home ]
          AllowedDomains = Some (domainsFrom ambient)
          Env = env
          WorkingDirectory = None }

    let private childProcess : obj = importAll "node:child_process"
    let private nodeStream : obj = importAll "node:stream"
    let private nodeEvents : obj = importAll "node:events"

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

    // The srt spawner has one problem the host spawner does not: the SDK's seam is
    // SYNCHRONOUS (`options => process`) and srt's wrap is asynchronous (it resolves the
    // profile and waits on the proxy). So the process the SDK gets back is a stand-in
    // whose streams are already live — writes queue in `stdin`, reads wait on `stdout` —
    // and the real child is joined to them the moment the wrap resolves. Nothing here
    // decides anything: it buffers, pipes, forwards the two events the interface has, and
    // remembers a kill that arrived before there was anything to kill.
    [<Emit("""((cp, stream, events, wrap) => (options) => {
  const emitter = new events.EventEmitter()
  const stdin = new stream.PassThrough()
  const stdout = new stream.PassThrough()
  const stderr = new stream.PassThrough()
  const state = { child: null, killed: false, exitCode: null, pending: null }
  const killTree = (child, signal) => {
    try { process.kill(-child.pid, signal) } catch { try { child.kill(signal) } catch {} }
  }
  wrap(options.command, options.args, options.cwd || '')
    .then((argv) => {
      const child = cp.spawn(argv[0], argv.slice(1), { cwd: options.cwd || undefined, env: options.env, stdio: ['pipe', 'pipe', 'pipe'], detached: true })
      state.child = child
      stdin.pipe(child.stdin)
      child.stdout.pipe(stdout)
      child.stderr.pipe(stderr)
      child.on('exit', (code, signal) => { state.exitCode = code; emitter.emit('exit', code, signal) })
      child.on('error', (error) => emitter.emit('error', error))
      if (state.pending) killTree(child, state.pending)
    })
    .catch((error) => emitter.emit('error', error instanceof Error ? error : new Error(String(error))))
  const proxy = {
    stdin, stdout, stderr,
    get killed() { return state.killed },
    get exitCode() { return state.exitCode },
    kill(signal) {
      const chosen = signal || 'SIGTERM'
      state.killed = true
      if (state.child) killTree(state.child, chosen)
      else state.pending = chosen
      return true
    },
    on: (event, listener) => { emitter.on(event, listener) },
    once: (event, listener) => { emitter.once(event, listener) },
    off: (event, listener) => { emitter.off(event, listener) },
  }
  if (options.signal) {
    const abort = () => proxy.kill('SIGKILL')
    if (options.signal.aborted) abort()
    else options.signal.addEventListener('abort', abort, { once: true })
  }
  return proxy
})($0, $1, $2, $3)""")>]
    let private srtSpawnerOver (cp: obj) (stream: obj) (events: obj) (wrap: System.Func<string, string array, string, JS.Promise<string array>>) : obj = jsNative

    /// The srt-backend agent spawner: the same seam, with the CLI coming up inside the
    /// sandbox `wrap` describes.
    let srtClaudeSpawner (wrap: string -> string list -> string -> Async<string list>) : obj =
        srtSpawnerOver
            childProcess
            nodeStream
            nodeEvents
            (System.Func<_, _, _, _> (fun executable arguments cwd ->
                Async.StartAsPromise (
                    async {
                        let! argv = wrap executable (List.ofArray arguments) cwd
                        return List.toArray argv
                    })))

    /// The spawner for the configured agent backend. Docker is not one: `parseAgent`
    /// refused it at boot.
    let claudeSpawnerFor (backend: SandboxBackend) (ambient: Map<string, string>) (home: string) (env: Map<string, string>) : obj =
        match backend with
        | SrtBackend ->
            // The tools parse fail-closed, and SessionMain has already had this value
            // accepted at boot — a bad one cannot first appear here, mid-turn.
            match SrtSandbox.toolsFrom ambient with
            | Error reason -> failwithf "agent sandbox: %s" reason
            | Ok tools ->
                let policy = policyFor ambient home env
                srtClaudeSpawner (SrtSandbox.wrapperFor tools (Map.tryFind "HOME" ambient) policy)
        | HostBackend
        | DockerBackend -> hostClaudeSpawner ()

// --- Backend selection --------------------------------------------------------------------

/// The session's `CreateSandbox` for its configured backend. `Error` fails the session
/// at boot — fail closed, never a silent fallback to a weaker backend.
let forBackend (backend: SandboxBackend) (name: string) (spec: EnvironmentSpec) : Result<CreateSandbox, string> =
    let ambient = ambientEnv ()
    match backend with
    | HostBackend -> Ok (HostSandbox.create ())
    | DockerBackend -> Ok (DockerSandbox.create name spec)
    | SrtBackend ->
        SrtSandbox.toolsFrom ambient
        |> Result.map (fun tools -> SrtSandbox.create tools (Map.tryFind "HOME" ambient))
