module Yession.Tests.DevContainer

// This repo's OWN declared work environment, end to end through the product's pieces:
// yession.yaml decoded by the real decoder, the request resolved against the container's
// view of its checkout, the policy assembled by `policyFor`, the container started by the
// real docker backend. Written down after the whole path was proved by hand in a live
// session (the container pivot's final laps), because every invariant here broke at least
// once on the way there and every break was invisible to the cheap tier:
//
//   * the workdir wore a HOST path while the checkout sat under the /repos bind, with an
//     empty workspace volume shadowing it (#405)
//   * `docker exec` died on a nix-built image's symlinked /etc while `docker run` worked,
//     so the container looked up and answered nothing (#400)
//   * git refused the bind-mounted checkout as another user's (#404)
//   * nix could not initialise its store under the hardened capability profile (#401)
//   * a granted named volume read as held and was never mounted (#411)
//
// Two tiers. `Docker` alone buys the fast probes — a pulled image and a few execs, inside
// the release gate. The SELF-HOSTING case (`nix develop --command check` inside the
// container: this suite running itself) is `Docker Dogfood`, in no scheduled tier — run it
// when the container environment story changes:
//
//   check Docker Dogfood
//   gh workflow run verify.yml --ref <branch> -f capabilities="Docker Dogfood"
//
// Fixtures live under $HOME, not the system temp dir, on purpose: a bind mount's source
// must be visible to the DAEMON, and the common macOS arrangement (Colima) shares only
// $HOME into its VM — a fixture under /var/folders binds as an empty directory and every
// case fails saying the checkout is missing, which is the machine's fault, not the code's.

open System
open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Host
open Yession.Tests.Support

let private expect = function Ok v -> v | Error e -> failwithf "%A" e

let private nodeFs : obj = importAll "node:fs"
let private nodeOs : obj = importAll "node:os"
let private childProcess : obj = importAll "node:child_process"

[<Emit("$0.mkdirSync($1, { recursive: true })")>]
let private mkdirp (fs: obj) (path: string) : unit = jsNative

[<Emit("$0.mkdtempSync($1)")>]
let private mkdtempAt (fs: obj) (prefix: string) : string = jsNative

[<Emit("$0.homedir()")>]
let private homedir (os: obj) : string = jsNative

[<Emit("$0.rmSync($1, { recursive: true, force: true })")>]
let private rmrf (fs: obj) (path: string) : unit = jsNative

[<Emit("$0.copyFileSync($1, $2)")>]
let private copyFile (fs: obj) (from: string) (dest: string) : unit = jsNative

[<Emit("$0.execSync($1, { stdio: 'pipe' })")>]
let private execSync (cp: obj) (command: string) : unit = jsNative

module DK = Fable.Dockerode

[<Emit("$0.getVolume($1).remove()")>]
let private removeVolume (client: obj) (name: string) : JS.Promise<unit> = jsNative

let private repoRef = RepoRef.create "trinketworks/yession" |> expect

/// A repos directory holding this repo's checkout at the place the session would put it —
/// under $HOME (see the module comment), removed by the caller.
let private reposDirWith (checkout: string -> unit) : string =
    let root = homedir nodeOs + "/.cache/yession-tests"
    mkdirp nodeFs root
    let reposDir = mkdtempAt nodeFs (root + "/devcontainer-")
    let dir = sprintf "%s/%s" reposDir (RepoRef.relativePath repoRef)
    mkdirp nodeFs dir
    checkout dir
    reposDir

/// The `dev` request exactly as the session builds it: this repo's own file through the
/// real decoder, the workdir resolved against the CONTAINER's view of the checkout
/// (`workCheckoutAt`), and the one thing the session adds — the /repos bind.
let private declaredDev (reposDir: string) : EnvironmentSpec =
    let file = RepoConfig.read reposDir repoRef |> expect |> Option.get
    let decl = file.Sandboxes |> Map.find (SandboxName.create "dev" |> expect)
    let request = SandboxDecl.toRequest (Some (Sandboxes.workCheckoutAt reposDir repoRef)) decl |> expect
    let container =
        match request.Spec.Runtime with
        | Container container -> container
        | Confinement -> failwith "yession.yaml declares no container, and a repo work sandbox is one"
    { request.Spec with
        Runtime =
            Container
                { container with
                    Mounts =
                        container.Mounts
                        @ [ { Source = HostPath reposDir
                              Target = Sandboxes.reposVisibleAt DockerBackend reposDir
                              Mode = ReadWrite } ] } }

/// Start the declared container through the production composition, with `granted` as what
/// the operator's profile came to (empty = a host offering nothing, so the file's `wants:`
/// selects nothing — the portable cold path).
let private startDev (granted: ResourceLeaf list) (spec: EnvironmentSpec) : Async<Sandbox> =
    async {
        let name = SessionId.value (SessionId.mint ())
        let createSandbox = Sandboxes.forBackend DockerBackend name spec |> expect
        let resolve = fun secret -> async { return Error (sprintf "no secrets here for '%s'" (SecretName.value secret)) }
        match! Sandboxes.preparePolicy DockerBackend resolve None None None (fun _ _ -> Ok granted) spec () with
        | Error reason -> return failwithf "policy refused: %s" reason
        | Ok policy ->
            match! createSandbox policy with
            | Error reason -> return failwithf "container failed to start: %s" reason
            | Ok sandbox -> return sandbox
    }

/// A container's "started" is not its "ready": its start command may still be materialising
/// /etc when the first exec lands (the race the product retries through — #402). The same
/// brief patience here, so a case's red means its invariant and not the race.
let private awaitReady (sandbox: Sandbox) : Async<unit> =
    let rec go attempts =
        async {
            let! run, _, _ = runInSandbox sandbox "sh" [ "-c"; "true" ] Map.empty None
            match run with
            | SandboxExited 0 -> return ()
            | outcome when attempts <= 1 -> return failwithf "the container never became execable: %A" outcome
            | _ ->
                do! Async.Sleep 500
                return! go (attempts - 1)
        }
    go 20

let tests =
    Tag.needs "The declared dev container" [ Tag.Docker ] (fun () ->
        testList "the dev container this repo declares" [

            // One container, several invariants probed — but the SETUP is the shared part
            // and each probe asserts one thing, so they stay separate cases over one
            // fixture would cost a pull per case; a compromise is documented rather than
            // silent: the probes run in one case each against a per-case container, and
            // the image pull is paid once by the daemon's own cache.

            testCaseAsync "it starts in this repo's checkout, reached through the /repos bind" (async {
                let reposDir = reposDirWith (fun dir -> copyFile nodeFs "yession.yaml" (dir + "/yession.yaml"))
                let! sandbox = startDev [] (declaredDev reposDir)
                do! awaitReady sandbox
                // An exec answering AT ALL is itself the Docker 29 /etc invariant: on a
                // nix-built image every exec died while the container ran happily.
                let! run, out, _ = runInSandbox sandbox "sh" [ "-c"; "pwd && ls yession.yaml" ] Map.empty None
                Expect.equal run (SandboxExited 0) "the exec ran"
                Expect.isTrue (out.Contains "/repos/trinketworks/yession") "it stands in the checkout, container view"
                Expect.isTrue (out.Contains "yession.yaml") "and the checkout's content is really there (an empty listing here usually means the daemon cannot see the fixture — see the module comment)"
                do! sandbox.Dispose ()
                rmrf nodeFs reposDir
            })

            testCaseAsync "the backend's git trust reaches the container" (async {
                let reposDir = reposDirWith (fun dir -> copyFile nodeFs "yession.yaml" (dir + "/yession.yaml"))
                let! sandbox = startDev [] (declaredDev reposDir)
                do! awaitReady sandbox
                // Presence, not behaviour: the base image carries no git, and pulling one
                // just for this probe would make the case about the network. The BEHAVIOUR
                // — git actually reading the uid-mismatched checkout — is what the
                // Dogfood run proves, whose suite reads devenv.lock through real git.
                let! run, out, _ = runInSandbox sandbox "sh" [ "-c"; "printenv GIT_CONFIG_KEY_0 GIT_CONFIG_VALUE_0" ] Map.empty None
                Expect.equal run (SandboxExited 0) "the trio is set"
                Expect.isTrue (out.Contains "safe.directory") "the key"
                Expect.isTrue (out.Contains "*") "for every path the session mounted"
                do! sandbox.Dispose ()
                rmrf nodeFs reposDir
            })

            testCaseAsync "nix initialises and writes its store under the hardened profile" (async {
                let reposDir = reposDirWith (fun dir -> copyFile nodeFs "yession.yaml" (dir + "/yession.yaml"))
                let! sandbox = startDev [] (declaredDev reposDir)
                do! awaitReady sandbox
                // Offline on purpose: a store ADD proves single-user init and store writes
                // under CapDrop (the multi-user chown was the fault) without making the
                // case about the network. Substitution is the Dogfood run's business.
                let! run, _, err = runInSandbox sandbox "sh" [ "-c"; "echo probe > /tmp/probe && nix store add-file /tmp/probe" ] Map.empty None
                Expect.equal run (SandboxExited 0) (sprintf "the store accepted a write (stderr: %s)" err)
                do! sandbox.Dispose ()
                rmrf nodeFs reposDir
            })

            testCaseAsync "a granted store volume mounts at /nix, seeded from the image" (async {
                let reposDir = reposDirWith (fun dir -> copyFile nodeFs "yession.yaml" (dir + "/yession.yaml"))
                // A unique name per run: volumes are host-global and persistent — the
                // property under test — so a fixed name would couple runs to each other.
                let volume = sprintf "yession-test-%s" (SessionId.value (SessionId.mint ())) |> fun s -> s.ToLowerInvariant ()
                let! sandbox = startDev [ Volume (volume, "/nix") ] (declaredDev reposDir)
                do! awaitReady sandbox
                let! run, out, _ =
                    runInSandbox sandbox "sh"
                        [ "-c"; "grep -c ' /nix ' /proc/mounts && ls /nix/store | wc -l" ] Map.empty None
                Expect.equal run (SandboxExited 0) "the probes ran"
                match out.Trim().Split '\n' |> Array.toList |> List.map (fun s -> s.Trim ()) with
                | [ mounts; paths ] ->
                    Expect.equal mounts "1" "/nix is a mount, not the image's own directory"
                    Expect.isTrue (int paths > 0) "and dockerd seeded the empty volume from the image, so nix still runs"
                | other -> failwithf "expected two counts, got %A" other
                do! sandbox.Dispose ()
                do! removeVolume (DK.create ()) volume |> Interop.awaitPromise
                rmrf nodeFs reposDir
            })
        ])

/// The self-hosting run: this repo's whole suite, inside the very container its file
/// declares — `nix develop` assembling the devshell from the checkout and `check` running
/// what you are reading. The one case that proves the ENVIRONMENT rather than any seam of
/// it, and the reason it is consent-gated: cold, it substitutes the devshell closure
/// before a single test runs.
let dogfood =
    Tag.needs "The dev container, self-hosting" [ Tag.Docker; Tag.Dogfood ] (fun () ->
        testList "the dev container runs this repo's own suite" [

            testCaseAsync "nix develop --command check passes inside the declared container" (async {
                // A real clone of HEAD, not a copy of the working tree: `nix develop`
                // evaluates the flake from git, and a checkout is what the session would
                // have put there. Uncommitted changes are deliberately not smuggled in —
                // this proves the tree as committed, which is what anything downstream gets.
                let reposDir =
                    reposDirWith (fun dir -> execSync childProcess (sprintf "git clone --quiet . %s" dir))
                let! sandbox = startDev [] (declaredDev reposDir)
                do! awaitReady sandbox
                let! run, out, err =
                    runInSandbox sandbox "nix" [ "develop"; "--impure"; "--command"; "check" ] Map.empty None
                // The exit code IS the tally: check exits non-zero on any failure or
                // error. The output check on top only proves the suite RAN rather than
                // something exiting 0 without ever reaching it.
                Expect.equal run (SandboxExited 0) (sprintf "check failed inside the container; tail of stderr: %s" (err.Substring (max 0 (err.Length - 2000))))
                Expect.isTrue (out.Contains "tests run") "the tally printed, so the suite really ran"
                do! sandbox.Dispose ()
                rmrf nodeFs reposDir
            })
        ])
