module Yession.Tests.DockerIntegration

// Docker backend integration suite, reworked over the sandbox seam. Drives the REAL
// dockerode-backed sandbox — create/spawn/dispose, and the spec fields the backend
// interprets (env vars via the policy, working dir, mounts + the named workspace volume,
// build spec, secret refs resolved at spawn). The whole suite sits under
// `Tag.needs [Docker]` (Node + verify tier), which is the ONLY daemon gate: a run that
// did not ask for Docker reports one skip instead of running empty, so the cheap tier
// never reaches these. `verify` ASKS FOR Docker, and asking for a capability requires it
// — an unreachable daemon fails the gate rather than dropping to a green skip.

open System
open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Access
open Yession.Host
open Yession.Tests.Support

// --- Node helpers (host-side fs/os and process env, for HostPath mounts + secret store) --

let private nodeFs : obj = importAll "node:fs"
let private nodeOs : obj = importAll "node:os"

[<Emit("$0.mkdtempSync($1.tmpdir() + '/yession-')")>]
let private mkdtemp (fs: obj) (os: obj) : string = jsNative

// World-writable, because the hardened container (CapDrop ALL) has no
// CAP_DAC_OVERRIDE: its root cannot write into a 0700 host dir owned by another
// uid. The mount-mode test asserts rw-vs-ro mount semantics, not DAC override.
[<Emit("$0.chmodSync($1, 0o777)")>]
let private makeWorldWritable (fs: obj) (path: string) : unit = jsNative

[<Emit("$0.readFileSync($1, 'utf8')")>]
let private readFile (fs: obj) (path: string) : string = jsNative

// --- Fixtures / helpers ------------------------------------------------------------------

/// Reshape the container inside a spec. The runtime union nests what used to be flat, and
/// these cases are all about the container, so they say so once here.
let private withContainer (f: ContainerSpec -> ContainerSpec) (spec: EnvironmentSpec) : EnvironmentSpec =
    { spec with
        Runtime =
            match spec.Runtime with
            | Container c -> Container (f c)
            | Confinement -> Container (f ContainerSpec.defaults) }

let private alpineSpec =
    { EnvironmentSpec.defaults with Runtime = Container { ContainerSpec.defaults with Image = Some { Name = "alpine"; Tag = Some "3" } } }

/// The env fallback the old Manager-side injection ended on: the test process's env.
let private envSecrets : SecretName -> Async<Result<string, string>> =
    fun n -> SecretStore.SecretResolution.processEnv (SessionId.mint ()) n

/// Create a docker sandbox for a fresh minted name: the production composition
/// (`preparePolicy` → `forBackend`), so the spec's secret refs resolve exactly as a
/// session's would.
let private start (resolve: SecretName -> Async<Result<string, string>>) (spec: EnvironmentSpec) : Async<Result<string * Sandbox, string>> =
    async {
        let name = SessionId.value (SessionId.mint ())
        let createSandbox = Sandboxes.forBackend DockerBackend name spec |> expect
        match! Sandboxes.preparePolicy DockerBackend resolve None None None (fun _ -> Ok []) spec () with
        | Error reason -> return Error reason
        | Ok policy ->
            let! created = createSandbox policy
            return created |> Result.map (fun sandbox -> name, sandbox)
    }

let private startOrFail (spec: EnvironmentSpec) : Async<string * Sandbox> =
    async {
        match! start envSecrets spec with
        | Error reason -> return failwithf "docker sandbox failed: %s" reason
        | Ok started -> return started
    }

let private label (name: string) = sprintf "yession-session=%s" name

// --- The suite ---------------------------------------------------------------------------

let tests =
    Tag.needs "Docker integration" [ Tag.Docker ] (fun () ->
        testList "Docker integration" [

            // -- Tier 2: the current engine ------------------------------------------------

            testCaseAsync "lifecycle: create, stream stdout, dispose, container is gone" (async {
                let! name, sandbox = startOrFail alpineSpec
                let! run, out, _ = runInSandbox sandbox "echo" [ "streamed-out" ] Map.empty None
                Expect.equal run (SandboxExited 0) "exit 0"
                Expect.isTrue (out.Contains "streamed-out") "stdout streamed to the caller"
                do! sandbox.Dispose ()
                let! remaining = Sandboxes.DockerSandbox.countByLabel (label name)
                Expect.equal remaining 0 "the sandbox's container is removed on dispose"
            })

            testCaseAsync "a non-zero command exit maps to its exit code" (async {
                let! _, sandbox = startOrFail alpineSpec
                let! run, _, _ = runInSandbox sandbox "sh" [ "-c"; "exit 7" ] Map.empty None
                Expect.equal run (SandboxExited 7) "the container exit code propagates"
                do! sandbox.Dispose ()
            })

            testCaseAsync "stderr output is routed to the stderr stream" (async {
                let! _, sandbox = startOrFail alpineSpec
                let! run, out, err = runInSandbox sandbox "sh" [ "-c"; "echo oops 1>&2" ] Map.empty None
                Expect.equal run (SandboxExited 0) "exit 0"
                Expect.isTrue (err.Contains "oops") "text on stderr arrives as a Stderr chunk"
                Expect.isFalse (out.Contains "oops") "and not on stdout"
                do! sandbox.Dispose ()
            })

            testCaseAsync "two sandboxes get separately labelled containers" (async {
                let! a, sandboxA = startOrFail alpineSpec
                let! b, sandboxB = startOrFail alpineSpec
                let! countA = Sandboxes.DockerSandbox.countByLabel (label a)
                let! countB = Sandboxes.DockerSandbox.countByLabel (label b)
                Expect.equal countA 1 "sandbox A has exactly its own container"
                Expect.equal countB 1 "sandbox B has exactly its own container"
                do! sandboxA.Dispose ()
                do! sandboxB.Dispose ()
            })

            // -- Tier 3: the typed spec fields ---------------------------------------------

            testCaseAsync "env-var refs (PlainValue) reach the container env" (async {
                let spec = { alpineSpec with EnvironmentVariables = Map.ofList [ "GREETING", PlainValue "hello-env" ] }
                let! _, sandbox = startOrFail spec
                let! _, specEnv, _ = runInSandbox sandbox "printenv" [ "GREETING" ] Map.empty None
                Expect.isTrue (specEnv.Contains "hello-env") "spec env var is set in the container"
                // Per-command env from the request too.
                let! _, cmdEnv, _ = runInSandbox sandbox "printenv" [ "PER_CMD" ] (Map.ofList [ "PER_CMD", "cmd-env" ]) None
                Expect.isTrue (cmdEnv.Contains "cmd-env") "request env var is set for the exec"
                do! sandbox.Dispose ()
            })

            testCaseAsync "working directory: spec default and per-command override" (async {
                let spec = { alpineSpec with WorkingDirectory = Some "/tmp" }
                let! _, sandbox = startOrFail spec
                let! _, wd, _ = runInSandbox sandbox "pwd" [] Map.empty None
                Expect.isTrue (wd.Contains "/tmp") "commands run in the spec working directory"
                let! _, wdOverride, _ = runInSandbox sandbox "pwd" [] Map.empty (Some "/")
                Expect.isTrue (wdOverride.Trim() = "/") "the request working directory overrides it"
                do! sandbox.Dispose ()
            })

            testCaseAsync "the sandbox workspace named volume persists across a recreation" (async {
                let! name, first = startOrFail alpineSpec
                let! w, _, _ = runInSandbox first "sh" [ "-c"; "echo persisted > /workspace/marker" ] Map.empty None
                Expect.equal w (SandboxExited 0) "write into the workspace succeeds"
                do! first.Dispose ()
                // A fresh sandbox under the SAME name re-attaches the same volume.
                let createSandbox = Sandboxes.forBackend DockerBackend name alpineSpec |> expect
                match! createSandbox ((Sandboxes.policyFor DockerBackend Map.empty Map.empty None None None [] EnvironmentSpec.defaults |> expect)) with
                | Error reason -> failwithf "recreate failed: %s" reason
                | Ok second ->
                    let! r2, out, _ = runInSandbox second "cat" [ "/workspace/marker" ] Map.empty None
                    Expect.equal r2 (SandboxExited 0) "read after recreation succeeds"
                    Expect.isTrue (out.Contains "persisted") "the named volume kept the file across recreation"
                    do! second.Dispose ()
            })

            testCaseAsync "HostPath mounts honour read-write and read-only" (async {
                let dir = mkdtemp nodeFs nodeOs
                makeWorldWritable nodeFs dir
                // Read-write: the container writes, the host reads it back.
                let specRW = alpineSpec |> withContainer (fun c -> { c with Mounts = [ { Source = HostPath dir; Target = "/host"; Mode = ReadWrite } ] })
                let! _, sandboxRW = startOrFail specRW
                let! rw, _, _ = runInSandbox sandboxRW "sh" [ "-c"; "echo hostbound > /host/f" ] Map.empty None
                Expect.equal rw (SandboxExited 0) "read-write mount accepts writes"
                do! sandboxRW.Dispose ()
                Expect.isTrue ((readFile nodeFs (dir + "/f")).Contains "hostbound") "the host sees the container's write"
                // Read-only: the same path rejects writes.
                let specRO = alpineSpec |> withContainer (fun c -> { c with Mounts = [ { Source = HostPath dir; Target = "/host"; Mode = ReadOnly } ] })
                let! _, sandboxRO = startOrFail specRO
                let! ro, _, _ = runInSandbox sandboxRO "sh" [ "-c"; "echo nope > /host/f2" ] Map.empty None
                Expect.isFalse (ro = SandboxExited 0) "read-only mount rejects writes"
                do! sandboxRO.Dispose ()
            })

            testCaseAsync "build spec: an image is built from a context and run" (async {
                let spec =
                    alpineSpec
                    |> withContainer (fun c ->
                        { c with
                            Image = None
                            Build = Some { ContextPath = "tests/fixtures/docker"; DockerfilePath = None } })
                let! _, sandbox = startOrFail spec
                let! run, out, _ = runInSandbox sandbox "cat" [ "/marker" ] Map.empty None
                Expect.equal run (SandboxExited 0) "the built image runs"
                Expect.isTrue (out.Contains "built-from-context") "the Dockerfile RUN baked the marker"
                do! sandbox.Dispose ()
            })

            testCaseAsync "secret refs resolve at spawn; missing secrets fail creation" (async {
                do! Support.withEnv [ "YESSION_TEST_SECRET", Some "s3cr3t-value" ] (fun () -> async {
                    let resolvedSpec =
                        { alpineSpec with EnvironmentVariables = Map.ofList [ "TOKEN", SecretRef (SecretName.create "YESSION_TEST_SECRET" |> expect) ] }
                    let! _, sandbox =
                        async {
                            match! start envSecrets resolvedSpec with
                            | Error reason -> return failwithf "docker sandbox failed: %s" reason
                            | Ok started -> return started
                        }
                    let! _, out, _ = runInSandbox sandbox "printenv" [ "TOKEN" ] Map.empty None
                    Expect.isTrue (out.Contains "s3cr3t-value") "the referenced secret reaches the container env"
                    do! sandbox.Dispose ()
                })

                // An unresolved secret ref fails creation with a legible reason — before any
                // container exists. `None` is the arrangement here (the name must be ABSENT),
                // and `withEnv` still puts back whatever this box had.
                do! Support.withEnv [ "YESSION_MISSING_SECRET", None ] (fun () -> async {
                    let missingSpec =
                        { alpineSpec with EnvironmentVariables = Map.ofList [ "TOKEN", SecretRef (SecretName.create "YESSION_MISSING_SECRET" |> expect) ] }
                    match! start envSecrets missingSpec with
                    | Error reason -> Expect.isTrue (reason.Contains "YESSION_MISSING_SECRET") "the failure names the missing secret"
                    | Ok _ -> failwith "creation should fail when a secret ref cannot be resolved"
                })
            })

            testCaseAsync "the Manager's secret store feeds resolve-at-spawn, gated by the ABAC walk (Plan 06)" (async {
                // A store seeded with a session-scoped secret, a user-scoped secret,
                // and a shadowed name that also exists in the process env — resolved
                // through the SAME walk the control route serves, then injected at
                // sandbox spawn.
                let! opened = SecretStore.openStore None (KeyStore.random ())
                let store = expect (opened |> Result.mapError SecretStore.OpenError.describe)
                let sessionId = SessionId.mint ()
                let alice = UserId.create "alice" |> expect
                let secretName n = SecretName.create n |> expect
                let! _ = store.Set { Scope = SessionScope sessionId; Name = secretName "SESSION_TOKEN" } "session-held"
                let! _ = store.Set { Scope = UserScope alice; Name = secretName "USER_TOKEN" } "user-held"
                do! Support.withEnv [ "SESSION_TOKEN", Some "env-shadowed" ] (fun () -> async {
                    let walk = SecretStore.SecretResolution.compose (fun _ _ _ -> ()) store (fun _ -> Set.singleton alice) (fun _ -> Set.empty) (fun _ -> false) SecretStore.SecretResolution.processEnv
                    let spec =
                        { alpineSpec with
                            EnvironmentVariables =
                                Map.ofList
                                    [ "SESSION_TOKEN", SecretRef (secretName "SESSION_TOKEN")
                                      "USER_TOKEN", SecretRef (secretName "USER_TOKEN") ] }
                    match! start (fun n -> walk sessionId n) spec with
                    | Error reason -> failwithf "docker sandbox failed: %s" reason
                    | Ok (_, sandbox) ->
                        let! _, out, _ = runInSandbox sandbox "printenv" [] Map.empty None
                        Expect.isTrue (out.Contains "SESSION_TOKEN=session-held") "the store's session secret shadows the env fallback"
                        Expect.isTrue (out.Contains "USER_TOKEN=user-held") "a bound user's secret injects"
                        do! sandbox.Dispose ()
                })

                // Without the user binding, the user-scoped secret is unreachable.
                let unbound = SecretStore.SecretResolution.compose (fun _ _ _ -> ()) store (fun _ -> Set.empty) (fun _ -> Set.empty) (fun _ -> false) SecretStore.SecretResolution.processEnv
                let session2 = SessionId.mint ()
                let spec2 = { alpineSpec with EnvironmentVariables = Map.ofList [ "USER_TOKEN", SecretRef (secretName "USER_TOKEN") ] }
                match! start (fun n -> unbound session2 n) spec2 with
                | Error reason -> Expect.isTrue (reason.Contains "USER_TOKEN") "refused: no bound user, and no env fallback"
                | Ok _ -> failwith "an unbound session must not receive a user's secret"
            })
        ])
