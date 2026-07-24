module Yession.Tests.DockerIntegration

// Docker backend integration suite (docs/plans/03). Drives the REAL dockerode-backed
// engine through the session-scoped capability — start/exec/stop, and the spec fields the
// backend now interprets (env vars, working dir, timeout, mounts + the named workspace
// volume, build spec, secret refs). Every case runs behind `Docker.gate`, so it executes
// where a daemon exists (the CI verify runner) and reports a skip otherwise — except under
// YESSION_REQUIRE_DOCKER, where a missing daemon fails the gate. The whole suite declares
// `Tag.needs [Docker]` (Node + verify tier), so the cheap tier never reaches it.

open System
open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto
open Yession.Domain
open Yession.Manager
open Yession.Host
open Yession.Tests.Support

// --- Node helpers (host-side fs/os and process env, for HostPath mounts + secret store) --

let private nodeFs : obj = importAll "node:fs"
let private nodeOs : obj = importAll "node:os"

[<Emit("$0.mkdtempSync($1.tmpdir() + '/yession-')")>]
let private mkdtemp (fs: obj) (os: obj) : string = jsNative

[<Emit("$0.readFileSync($1, 'utf8')")>]
let private readFile (fs: obj) (path: string) : string = jsNative

[<Emit("process.env[$0] = $1")>]
let private setEnv (name: string) (value: string) : unit = jsNative

[<Emit("delete process.env[$0]")>]
let private unsetEnv (name: string) : unit = jsNative

// --- Fixtures / helpers ------------------------------------------------------------------

let private alpineSpec =
    { EnvironmentSpec.localProcess with
        Kind = Docker
        Image = Some { Name = "alpine"; Tag = Some "3" } }

let private req id exe args env wd timeout : CommandRequest =
    { CommandId = CommandId.create id |> expect
      Executable = exe
      Arguments = args
      WorkingDirectory = wd
      Environment = env
      Timeout = timeout }

/// Run a command, returning its result and the accumulated stdout / stderr text.
let private run (caps: SessionEnvironmentCapabilities) (handle: ContainerHandle) (r: CommandRequest) : Async<CommandResult * string * string> =
    async {
        let out = System.Text.StringBuilder()
        let err = System.Text.StringBuilder()
        let! result =
            caps.Execute handle r (fun c ->
                match c.Stream with
                | Stdout -> out.Append c.Text |> ignore
                | Stderr -> err.Append c.Text |> ignore)
        return result, out.ToString(), err.ToString()
    }

/// Start a container for a fresh minted session, returning its id, capability, and handle.
let private start (spec: EnvironmentSpec) : Async<SessionId * SessionEnvironmentCapabilities * ContainerHandle> =
    async {
        let registry = Authority.ContainerRegistry ()
        let sessionId = SessionId.mint ()
        let caps = Authority.grant registry (Backends.DockerBackend.create SecretStore.SecretResolution.processEnv) sessionId
        match! caps.StartContainer spec with
        | ContainerStartFailed r -> return failwithf "container start failed: %s" r
        | ContainerStarted handle -> return sessionId, caps, handle
    }

let private stop (caps: SessionEnvironmentCapabilities) (handle: ContainerHandle) : Async<unit> =
    caps.StopContainer handle |> Async.Ignore

let private label (sessionId: SessionId) = sprintf "yession-session=%s" (SessionId.value sessionId)

// --- The suite ---------------------------------------------------------------------------

let tests =
    Tag.needs "Docker integration" [ Tag.Docker ] (fun () ->
        testList "Docker integration" [

            // -- Tier 2: the current engine ------------------------------------------------

            Docker.gate "lifecycle: start, stream stdout, stop, container is gone" (fun () ->
                async {
                    let! sid, caps, handle = start alpineSpec
                    let! result, out, _ = run caps handle (req "c-echo" "echo" [ "streamed-out" ] Map.empty None None)
                    Expect.equal result (CommandSucceeded 0) "exit 0"
                    Expect.isTrue (out.Contains "streamed-out") "stdout streamed to the caller"
                    do! stop caps handle
                    let! remaining = Backends.DockerBackend.countByLabel (label sid)
                    Expect.equal remaining 0 "the session's container is removed on stop"
                })

            Docker.gate "a non-zero command exit maps to CommandFailed" (fun () ->
                async {
                    let! _, caps, handle = start alpineSpec
                    let! result, _, _ = run caps handle (req "c-fail" "sh" [ "-c"; "exit 7" ] Map.empty None None)
                    Expect.equal result (CommandFailed 7) "the container exit code propagates"
                    do! stop caps handle
                })

            Docker.gate "stderr output is routed to the stderr stream" (fun () ->
                async {
                    let! _, caps, handle = start alpineSpec
                    let! result, out, err = run caps handle (req "c-err" "sh" [ "-c"; "echo oops 1>&2" ] Map.empty None None)
                    Expect.equal result (CommandSucceeded 0) "exit 0"
                    Expect.isTrue (err.Contains "oops") "text on stderr arrives as a Stderr chunk"
                    Expect.isFalse (out.Contains "oops") "and not on stdout"
                    do! stop caps handle
                })

            Docker.gate "two sessions get separately labelled containers" (fun () ->
                async {
                    let! a, capsA, hA = start alpineSpec
                    let! b, capsB, hB = start alpineSpec
                    let! countA = Backends.DockerBackend.countByLabel (label a)
                    let! countB = Backends.DockerBackend.countByLabel (label b)
                    Expect.equal countA 1 "session A has exactly its own container"
                    Expect.equal countB 1 "session B has exactly its own container"
                    do! stop capsA hA
                    do! stop capsB hB
                })

            // -- Tier 3: the typed spec fields ---------------------------------------------

            Docker.gate "env-var refs (PlainValue) reach the container env" (fun () ->
                async {
                    let spec = { alpineSpec with EnvironmentVariables = Map.ofList [ "GREETING", PlainValue "hello-env" ] }
                    let! _, caps, handle = start spec
                    let! _, specEnv, _ = run caps handle (req "e-spec" "printenv" [ "GREETING" ] Map.empty None None)
                    Expect.isTrue (specEnv.Contains "hello-env") "spec env var is set in the container"
                    // Per-command env from the request too.
                    let! _, cmdEnv, _ = run caps handle (req "e-cmd" "printenv" [ "PER_CMD" ] (Map.ofList [ "PER_CMD", "cmd-env" ]) None None)
                    Expect.isTrue (cmdEnv.Contains "cmd-env") "request env var is set for the exec"
                    do! stop caps handle
                })

            Docker.gate "working directory: spec default and per-command override" (fun () ->
                async {
                    let spec = { alpineSpec with WorkingDirectory = Some "/tmp" }
                    let! _, caps, handle = start spec
                    let! _, wd, _ = run caps handle (req "w-spec" "pwd" [] Map.empty None None)
                    Expect.isTrue (wd.Contains "/tmp") "commands run in the spec working directory"
                    let! _, wdOverride, _ = run caps handle (req "w-cmd" "pwd" [] Map.empty (Some "/") None)
                    Expect.isTrue (wdOverride.Trim() = "/") "the request working directory overrides it"
                    do! stop caps handle
                })

            Docker.gate "a command past its timeout maps to CommandTimedOut" (fun () ->
                async {
                    let! _, caps, handle = start alpineSpec
                    let! result, _, _ = run caps handle (req "t-sleep" "sleep" [ "5" ] Map.empty None (Some (TimeSpan.FromMilliseconds 500.0)))
                    Expect.equal result CommandTimedOut "the timeout fires before the command finishes"
                    do! stop caps handle
                })

            Docker.gate "the session workspace named volume persists across a restart" (fun () ->
                async {
                    let! _, caps, h1 = start alpineSpec
                    let! w, _, _ = run caps h1 (req "v-write" "sh" [ "-c"; "echo persisted > /workspace/marker" ] Map.empty None None)
                    Expect.equal w (CommandSucceeded 0) "write into the workspace succeeds"
                    do! stop caps h1
                    // A fresh container on the SAME session id re-attaches the same volume.
                    match! caps.StartContainer alpineSpec with
                    | ContainerStartFailed r -> failwithf "restart failed: %s" r
                    | ContainerStarted h2 ->
                        let! r2, out, _ = run caps h2 (req "v-read" "cat" [ "/workspace/marker" ] Map.empty None None)
                        Expect.equal r2 (CommandSucceeded 0) "read after restart succeeds"
                        Expect.isTrue (out.Contains "persisted") "the named volume kept the file across restart"
                        do! stop caps h2
                })

            Docker.gate "HostPath mounts honour read-write and read-only" (fun () ->
                async {
                    let dir = mkdtemp nodeFs nodeOs
                    // Read-write: the container writes, the host reads it back.
                    let specRW = { alpineSpec with Mounts = [ { Source = HostPath dir; Target = "/host"; Mode = ReadWrite } ] }
                    let! _, capsRW, hRW = start specRW
                    let! rw, _, _ = run capsRW hRW (req "m-rw" "sh" [ "-c"; "echo hostbound > /host/f" ] Map.empty None None)
                    Expect.equal rw (CommandSucceeded 0) "read-write mount accepts writes"
                    do! stop capsRW hRW
                    Expect.isTrue ((readFile nodeFs (dir + "/f")).Contains "hostbound") "the host sees the container's write"
                    // Read-only: the same path rejects writes.
                    let specRO = { alpineSpec with Mounts = [ { Source = HostPath dir; Target = "/host"; Mode = ReadOnly } ] }
                    let! _, capsRO, hRO = start specRO
                    let! ro, _, _ = run capsRO hRO (req "m-ro" "sh" [ "-c"; "echo nope > /host/f2" ] Map.empty None None)
                    Expect.isFalse (ro = CommandSucceeded 0) "read-only mount rejects writes"
                    do! stop capsRO hRO
                })

            Docker.gate "build spec: an image is built from a context and run" (fun () ->
                async {
                    let spec =
                        { alpineSpec with
                            Image = None
                            Build = Some { ContextPath = "tests/fixtures/docker"; DockerfilePath = None } }
                    let! _, caps, handle = start spec
                    let! result, out, _ = run caps handle (req "b-cat" "cat" [ "/marker" ] Map.empty None None)
                    Expect.equal result (CommandSucceeded 0) "the built image runs"
                    Expect.isTrue (out.Contains "built-from-context") "the Dockerfile RUN baked the marker"
                    do! stop caps handle
                })

            Docker.gate "secret refs resolve from the store; missing secrets fail start" (fun () ->
                async {
                    setEnv "YESSION_TEST_SECRET" "s3cr3t-value"
                    let resolvedSpec =
                        { alpineSpec with EnvironmentVariables = Map.ofList [ "TOKEN", SecretRef (SecretName.create "YESSION_TEST_SECRET" |> expect) ] }
                    let! _, caps, handle = start resolvedSpec
                    let! _, out, _ = run caps handle (req "s-print" "printenv" [ "TOKEN" ] Map.empty None None)
                    Expect.isTrue (out.Contains "s3cr3t-value") "the referenced secret reaches the container env"
                    do! stop caps handle
                    unsetEnv "YESSION_TEST_SECRET"

                    // An unresolved secret ref fails start with a legible reason.
                    unsetEnv "YESSION_MISSING_SECRET"
                    let registry = Authority.ContainerRegistry ()
                    let missingCaps = Authority.grant registry (Backends.DockerBackend.create SecretStore.SecretResolution.processEnv) (SessionId.mint ())
                    let missingSpec =
                        { alpineSpec with EnvironmentVariables = Map.ofList [ "TOKEN", SecretRef (SecretName.create "YESSION_MISSING_SECRET" |> expect) ] }
                    match! missingCaps.StartContainer missingSpec with
                    | ContainerStartFailed reason -> Expect.isTrue (reason.Contains "YESSION_MISSING_SECRET") "the failure names the missing secret"
                    | ContainerStarted _ -> failwith "start should fail when a secret ref cannot be resolved"
                })

            Docker.gate "the Manager's secret store injects into the container env, gated by the ABAC walk (Plan 06)" (fun () ->
                async {
                    // A store seeded with a session-scoped secret, a user-scoped secret,
                    // and a shadowed name that also exists in the process env.
                    let! opened = SecretStore.openStore None (KeyStore.random ())
                    let store = expect opened
                    let sessionId = SessionId.mint ()
                    let alice = UserSubject.create "alice" |> expect
                    let secretName n = SecretName.create n |> expect
                    let! _ = store.Set { Scope = SessionScope sessionId; Name = secretName "SESSION_TOKEN" } "session-held"
                    let! _ = store.Set { Scope = UserScope alice; Name = secretName "USER_TOKEN" } "user-held"
                    setEnv "SESSION_TOKEN" "env-shadowed"
                    let resolve = SecretStore.SecretResolution.compose store (fun _ -> Set.singleton alice) SecretStore.SecretResolution.processEnv
                    let registry = Authority.ContainerRegistry ()
                    let caps = Authority.grant registry (Backends.DockerBackend.create resolve) sessionId
                    let spec =
                        { alpineSpec with
                            EnvironmentVariables =
                                Map.ofList
                                    [ "SESSION_TOKEN", SecretRef (secretName "SESSION_TOKEN")
                                      "USER_TOKEN", SecretRef (secretName "USER_TOKEN") ] }
                    match! caps.StartContainer spec with
                    | ContainerStartFailed r -> failwithf "container start failed: %s" r
                    | ContainerStarted handle ->
                        let! _, out, _ = run caps handle (req "s-env" "printenv" [] Map.empty None None)
                        Expect.isTrue (out.Contains "SESSION_TOKEN=session-held") "the store's session secret shadows the env fallback"
                        Expect.isTrue (out.Contains "USER_TOKEN=user-held") "a bound user's secret injects"
                        do! stop caps handle
                    unsetEnv "SESSION_TOKEN"

                    // Without the user binding, the user-scoped secret is unreachable.
                    let unbound = SecretStore.SecretResolution.compose store (fun _ -> Set.empty) SecretStore.SecretResolution.processEnv
                    let registry2 = Authority.ContainerRegistry ()
                    let session2 = SessionId.mint ()
                    let caps2 = Authority.grant registry2 (Backends.DockerBackend.create unbound) session2
                    let spec2 = { alpineSpec with EnvironmentVariables = Map.ofList [ "USER_TOKEN", SecretRef (secretName "USER_TOKEN") ] }
                    match! caps2.StartContainer spec2 with
                    | ContainerStartFailed reason -> Expect.isTrue (reason.Contains "USER_TOKEN") "refused: no bound user, and no env fallback"
                    | ContainerStarted _ -> failwith "an unbound session must not receive a user's secret"
                })
        ])
