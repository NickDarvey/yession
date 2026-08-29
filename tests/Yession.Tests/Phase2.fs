module Yession.Tests.Phase2

// Phase 2 verification, step by step:
//
// - Step 10: the Session Manager owns launch — launching registers a Session Process
//   and returns a reachable bootstrap URI; Phase 1 behaviour is preserved under a
//   Manager-launched Process.
//
// Later steps extend this module (scoped capabilities, lazy environments, commands).

open System
open Fable.Core
open Fable.Pyxpecto
open Ylmish
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Agent
open Yession.Domain.Link
open Yession.Domain.Terminals
open Yession.App

[<ImportAll("node:fs")>]
let private nodeFs : obj = jsNative

[<ImportAll("node:os")>]
let private nodeOs : obj = jsNative

[<Emit("$0.mkdtempSync($1.tmpdir() + '/yession-tmp-')")>]
let private mkdtemp (fs: obj) (os: obj) : string = jsNative

/// A directory nothing else in this run writes to.
let private tempDir () : string = mkdtemp nodeFs nodeOs

/// A sandbox whose only behaviour is how its one spawn ends. Enough to drive
/// `SessionEnvironment.verify`, which asks a sandbox exactly one question.
let private sandboxAnswering (answer: (OutputStream * string -> unit) -> Async<Result<SandboxProcessHandle, string>>) : Sandbox =
    { Ref = "fake"
      Spawn = fun _ onChunk -> answer onChunk
      SpawnPty = None
      Dispose = fun () -> async { return () } }

/// A sandbox whose verification program reports the check at `index` as the first failure —
/// which is what the real program prints, so the index this writes and the index `explain`
/// reads have to agree or these tests go red.
let private failingAt (index: int) : Sandbox =
    sandboxAnswering (fun onChunk ->
        async {
            onChunk (Stdout, string index)
            return Ok { WriteStdin = ignore
                        CloseStdin = ignore
                        Kill = ignore
                        Exited = async { return SandboxExited 1 } }
        })

/// A sandbox that cannot start a process.
let private cannotSpawn : Sandbox =
    sandboxAnswering (fun _ -> async { return Error "no shell here" })

/// A sandbox whose verification program finds nothing wrong — the ordinary case, and the
/// only one from which an environment ever ends up running.
let private passesEveryCheck : Sandbox =
    sandboxAnswering (fun _ ->
        async {
            return Ok { WriteStdin = ignore
                        CloseStdin = ignore
                        Kill = ignore
                        Exited = async { return SandboxExited 0 } }
        })
open Yession.Host
open Yession.Tests.Support

let private basePort = 8110

// -----------------------------------------------------------------------------
// Step 10 — Session Manager & launch.
// -----------------------------------------------------------------------------

let mutable private manager : Manager.SessionManager option = None

let private launchTests =
    testList "Session Manager launch" [
        testCaseAsync "launching a session registers a Session Process and returns its bootstrap URI" <|
            async {
                let m = Manager.create None None basePort
                manager <- Some m
                let request : SessionLaunchRequest =
                    { SessionLaunchRequest.SessionId = SessionId.create "managed-1" |> expect }
                let! result = m.StartSession request
                Expect.equal result.SessionId request.SessionId "the launched session"
                Expect.isTrue (result.ProcessId.Length > 0) "a process id is assigned"
                Expect.equal result.LocalBootstrapUri (sprintf "http://127.0.0.1:%d/" basePort) "local bootstrap URI"
                match m.TryFind request.SessionId with
                | Some managed ->
                    Expect.equal managed.ProcessId result.ProcessId "the registration matches the launch result"
                | None -> failwith "the launched Process must be registered with the Manager"
            }

        testCaseAsync "the bootstrap URI is reachable and serves the client shell" <|
            async {
                let m = manager.Value
                let managed = (m.Registered ()) |> List.head
                let! html = Interop.getText managed.BootstrapUri |> Async.AwaitPromise
                Expect.isTrue (html.Contains (Dom.attr "id" Dom.appId)) "the served page is the client shell"
            }

        testCaseAsync "launching the same session twice is rejected" <|
            async {
                let m = manager.Value
                let request : SessionLaunchRequest =
                    { SessionLaunchRequest.SessionId = SessionId.create "managed-1" |> expect }
                let mutable rejected = false
                try
                    let! _ = m.StartSession request
                    ()
                with _ -> rejected <- true
                Expect.isTrue rejected "a session launches at most once"
            }

        testCaseAsync "Phase 1 behaviour is preserved under a Manager-launched Process" <|
            async {
                let m = manager.Value
                let managed = (m.Registered ()) |> List.head
                let signalUrl = managed.BootstrapUri + "signal"
                let! a = connectClient signalUrl (managed.Host.MintPeerToken ()) "ada" "Ada"
                let! b = connectClient signalUrl (managed.Host.MintPeerToken ()) "grace" "Grace"

                do! compose a a.Hello.PeerId "managed hello"
                do! b.Runner.WaitFor (fun _ -> draftBody b a.Hello.PeerId = Some "managed hello")

                a.Connection.SendDraft a.Hello.PeerId
                do! b.Runner.WaitFor (fun model ->
                        model.Conversation.Items |> List.exists (fun i -> i.Body = "managed hello"))

                do! a.Channel.Close ()
                do! b.Channel.Close ()
            }

        testCaseAsync "stop the Manager (and its launched Processes)" <|
            async {
                match manager with
                | Some m -> do! m.Stop ()
                | None -> ()
            }
    ]

// -----------------------------------------------------------------------------
// The sandbox seam — backend parsing and policy assembly are pure and fail
// closed; the host backend's env allowlist is the credential-leak regression
// guard. (The old Step 11 handle-validation suite pinned the deleted
// Manager-side transport and went with it: sandboxes are session-owned, so
// there is no cross-session handle to forge.)
// -----------------------------------------------------------------------------

// A promise that is already rejected when the workflow gets to it — the shape every
// backend produces routinely (a docker 404 for a container that is not there).
[<Fable.Core.Emit("Promise.reject(new Error($0))")>]
let private rejectedPromise (message: string) : JS.Promise<unit> = Fable.Core.Util.jsNative

// Count Node's unhandled-rejection reports. Registering a listener is also what stops
// Node from killing the process over one, so the count is observable rather than fatal.
[<Fable.Core.Emit("(() => { const w = { count: 0 }; const on = () => { w.count++ }; process.on('unhandledRejection', on); w.stop = () => process.off('unhandledRejection', on); return w })()")>]
let private watchUnhandledRejections () : obj = Fable.Core.Util.jsNative

[<Fable.Core.Emit("$0.count")>]
let private unhandledCount (watch: obj) : int = Fable.Core.Util.jsNative

[<Fable.Core.Emit("$0.stop()")>]
let private stopWatching (watch: obj) : unit = Fable.Core.Util.jsNative

let private promiseAwaitTests =
    testList "Awaiting a promise (Node interop)" [
        testCaseAsync "a rejection is handled at the call, so reaching it late is caught, not fatal" <|
            async {
                let watch = watchUnhandledRejections ()
                // Build the await now and run it later. Fable's async trampoline hijacks a
                // workflow onto a `setTimeout` every 2000 steps, so a real await lands here:
                // after Node has already decided whether the rejection was handled. Nothing
                // the workflow does later can undo that verdict, so the handler has to be
                // attached by now.
                let awaiting = Interop.awaitPromise (rejectedPromise "boom")
                do! Async.Sleep 10
                let! caught =
                    async {
                        try
                            do! awaiting
                            return "no error"
                        with ex -> return ex.Message
                    }
                do! Async.Sleep 10
                let unhandled = unhandledCount watch
                stopWatching watch
                Expect.equal caught "boom" "the rejection arrives as a catchable exception"
                Expect.equal unhandled 0 "and Node never reports it unhandled — which would kill the process"
            }
    ]

/// The confinement tools, with the read scope's allow-back as the field a case varies. The
/// tool paths are what a darwin host has (none): nothing below turns on them.
let private toolsWithRuntime (runtime: string list) : Sandboxes.SrtTools =
    { Bwrap = None
      Socat = None
      Ripgrep = None
      Nesting = Sandboxes.StrictNesting
      Runtime = runtime }

/// A config with every Linux tool named — the shape a `startFailure` verdict is read off.
let private namedToolsConfig : Sandboxes.SrtConfig =
    Sandboxes.SrtSandbox.configFor
        { toolsWithRuntime [] with
            Bwrap = Some "/usr/bin/bwrap"
            Socat = Some "/usr/bin/socat"
            Ripgrep = Some "/usr/bin/rg" }
        Support.emptyPolicy

let private sandboxPolicyTests =
    testList "Sandbox policy (pure)" [
        testCase "backend parsing accepts exactly host, srt, and docker — and fails closed" <| fun () ->
            Expect.equal (SandboxBackend.parse "host") (Ok HostBackend) "host"
            Expect.equal (SandboxBackend.parse "srt") (Ok SrtBackend) "srt"
            Expect.equal (SandboxBackend.parse " Docker ") (Ok DockerBackend) "case/space tolerant"
            Expect.isError (SandboxBackend.parse "podman") "an unknown backend is a loud error, never a fallback"
            Expect.isError (SandboxBackend.parse "") "blank is not a choice"
            Expect.equal (SandboxBackend.parseAgent "srt") (Ok SrtBackend) "the agent sandbox accepts srt"
            Expect.isError (SandboxBackend.parseAgent "docker") "docker is a work-sandbox backend only, by design"

        testCase "the host baseline is an allowlist: credentials never pass it" <| fun () ->
            let ambient =
                Map.ofList
                    [ "PATH", "/usr/bin"
                      "HOME", "/home/u"
                      "ANTHROPIC_API_KEY", "super-secret"
                      "CLAUDE_CODE_OAUTH_TOKEN", "also-secret"
                      "YESSION_LAUNCH", "{\"session\":\"s\",\"dataDir\":\"/d\",\"port\":0,\"parentGuard\":true,\"control\":{\"url\":\"http://m\",\"secret\":\"launch-secret\"}}" ]
            let baseline = Sandboxes.hostBaseline ambient
            Expect.equal (Map.tryFind "PATH" baseline) (Some "/usr/bin") "PATH survives"
            Expect.equal (Map.tryFind "HOME" baseline) (Some "/home/u") "HOME survives"
            Expect.equal (Map.tryFind "ANTHROPIC_API_KEY" baseline) None "credentials do not"
            Expect.equal (Map.tryFind "CLAUDE_CODE_OAUTH_TOKEN" baseline) None "no credential survives"
            Expect.equal (Map.tryFind "YESSION_LAUNCH" baseline) None "the launch envelope, which carries the control secret, does not"

        testCase "the agent CLI's env: one credential, scratch HOME, never the raw process env" <| fun () ->
            let ambient =
                Map.ofList
                    [ "PATH", "/usr/bin"
                      "HOME", "/home/u"
                      "HTTPS_PROXY", "http://proxy:3128"
                      "ANTHROPIC_API_KEY", "ambient-key"
                      "CLAUDE_CODE_OAUTH_TOKEN", "ambient-token"
                      "YESSION_LAUNCH", "{\"session\":\"s\",\"dataDir\":\"/d\",\"port\":0,\"parentGuard\":true,\"control\":{\"url\":\"http://m\",\"secret\":\"launch-secret\"}}" ]
            // A resolved per-turn credential displaces BOTH ambient credential vars.
            let resolved = Sandboxes.AgentSandbox.envFor ambient "/data/agent-home" (Some ("CLAUDE_CODE_OAUTH_TOKEN", "turn-token"))
            Expect.equal (Map.tryFind "CLAUDE_CODE_OAUTH_TOKEN" resolved) (Some "turn-token") "the turn's credential is set"
            Expect.equal (Map.tryFind "ANTHROPIC_API_KEY" resolved) None "the ambient key never rides along"
            Expect.equal (Map.tryFind "HOME" resolved) (Some "/data/agent-home") "the CLI gets the scratch HOME"
            Expect.equal (Map.tryFind "HTTPS_PROXY" resolved) (Some "http://proxy:3128") "proxy config passes through"
            Expect.equal (Map.tryFind "YESSION_LAUNCH" resolved) None "the launch envelope never reaches the CLI"
            // The documented ambient last resort passes exactly the two credential vars.
            let ambientRun = Sandboxes.AgentSandbox.envFor ambient "/data/agent-home" None
            Expect.equal (Map.tryFind "ANTHROPIC_API_KEY" ambientRun) (Some "ambient-key") "the ambient key passes when nothing displaces it"
            Expect.equal (Map.tryFind "CLAUDE_CODE_OAUTH_TOKEN" ambientRun) (Some "ambient-token") "so does the ambient token"

        testCase "a sandbox's own process needs a backend that has one" <| fun () ->
            // `cmd` is compose's `command`, and only a container has a `Cmd` to replace. A
            // host or srt sandbox is a confinement around spawns, so the ask is refused
            // rather than ignored — a sandbox that silently did not run what it was asked
            // to run is worse than one that says so.
            // `Confinement` cannot CARRY a command — that is the union's job, and no test can
            // exercise a state the type has no representation for. What is left to check is
            // the question the union cannot settle: whether this backend hosts a container.
            let asContainer =
                { EnvironmentSpec.defaults with
                    Runtime = Container { ContainerSpec.defaults with Command = Some "postgres -c fsync=off" } }
            Expect.isError (Sandboxes.forBackend HostBackend "s" asContainer) "the host backend runs no container"
            Expect.isError (Sandboxes.forBackend SrtBackend "s" asContainer) "nor does srt"
            Expect.isOk (Sandboxes.forBackend DockerBackend "s" asContainer) "docker does"

        testCase "the refusal says what was asked for and what would host it" <| fun () ->
            // A refusal nobody can act on gets worked around instead of fixed.
            let asContainer =
                { EnvironmentSpec.defaults with
                    Runtime = Container { ContainerSpec.defaults with Command = Some "npm start" } }
            match Sandboxes.forBackend HostBackend "s" asContainer with
            | Ok _ -> failwith "expected a refusal"
            | Error e ->
                Expect.isTrue (e.Contains "npm start") "it quotes the command"
                Expect.isTrue (e.Contains "docker") "and names the backend that could run it"

        testCase "a sandbox with no command is unaffected on every backend" <| fun () ->
            // The guard above must not have cost the ordinary sandbox anything.
            Expect.isOk (Sandboxes.forBackend HostBackend "s" EnvironmentSpec.defaults) "host still works"
            // Docker always IS a container: a spec that asked for nothing gets the defaults
            // rather than a second, container-less docker path.
            Expect.isOk (Sandboxes.forBackend DockerBackend "s" EnvironmentSpec.defaults) "so does docker"

        testCase "the srt config denies every read, and the policy's paths are the holes in it" <| fun () ->
            // Denying only the operator's home left everything nobody thought to name —
            // another session's data directory, a checkout this session was never given —
            // readable by every command the agent issues.
            let policy =
                { Support.emptyPolicy with
                    ReadPaths = [ "/opt/tools" ]
                    WritePaths = [ "/data/workspace" ] }
            let config = Sandboxes.SrtSandbox.configFor (toolsWithRuntime [ "/usr" ]) policy
            Expect.equal config.DenyRead [ "/" ] "the denied region is the whole filesystem"
            Expect.equal
                config.AllowRead
                [ "/opt/tools"
                  "/data/workspace"
                  (Sandboxes.SessionLayout.tmpDir ())
                  "/dev/stdout"
                  "/dev/stderr"
                  "/dev/null"
                  "/usr" ]
                "read paths, everything writable (a workspace that cannot be read is no workspace), and the host runtime"

        // Connecting to a unix socket is its own permission, and the file halves do not add
        // up to it. Measured: a sandbox holding the nix daemon socket readable and writable,
        // with `test -S` passing, was refused by nix with "could not connect to any lix
        // socket" — because srt's `network.allowUnixSockets` named nothing.
        testCase "a granted socket reaches srt as a socket, not as two file grants" <| fun () ->
            let leaves = [ Socket "/nix/var/nix/daemon-socket/socket" ]
            match Sandboxes.grantsFrom leaves with
            | Error e -> failwithf "expected a grant, got %s" e
            | Ok (reads, writes, _, sockets, _) ->
                Expect.equal sockets [ "/nix/var/nix/daemon-socket/socket" ] "it is a socket grant"
                // And still the file halves, because talking to one does both and the node
                // has to be reachable before it can be connected to.
                Expect.isTrue (List.contains "/nix/var/nix/daemon-socket/socket" reads) "readable too"
                Expect.isTrue (List.contains "/nix/var/nix/daemon-socket/socket" writes) "and writable"

        // The end of that wire: what srt is actually told. A grant the config never carries
        // is a grant that does not exist, which is the whole fault this fixes.
        testCase "the srt config carries the sockets a sandbox may connect to" <| fun () ->
            let policy = { Support.emptyPolicy with Sockets = [ "/var/run/docker.sock" ] }
            let config = Sandboxes.SrtSandbox.configFor (toolsWithRuntime []) policy
            Expect.equal config.AllowUnixSockets [ "/var/run/docker.sock" ] "it rides through"

        // Nobody named one, so nothing is reachable — srt's own default, stated rather than
        // left to be inferred.
        testCase "a sandbox that names no socket may connect to none" <| fun () ->
            let config = Sandboxes.SrtSandbox.configFor (toolsWithRuntime []) Support.emptyPolicy
            Expect.equal config.AllowUnixSockets [] "no sockets, none allowed"

        // --- what this host can actually express -------------------------------------------

        // The measured split, and the reason the whole layer exists: one declaration is a
        // scoped grant on a Mac and, on Linux, either every unix socket or none.
        testCase "srt scopes a socket by path on macOS and cannot on Linux" <| fun () ->
            Expect.isTrue
                (Sandboxes.limitsFor SrtBackend "darwin" |> HostLimits.can HostDistinction.SocketsByPath)
                "Seatbelt takes a network-outbound rule on the path"
            Expect.isFalse
                (Sandboxes.limitsFor SrtBackend "linux" |> HostLimits.can HostDistinction.SocketsByPath)
                "seccomp-bpf cannot read a socket path out of user-space memory"
            for platform in [ "darwin"; "linux" ] do
                Expect.isTrue
                    (Sandboxes.limitsFor SrtBackend platform |> HostLimits.can HostDistinction.EgressByHost)
                    (sprintf "egress it scopes on both, through its own proxy (%s)" platform)

        // docker's egress is unfiltered, which until now was `AllowedDomains = None` and
        // unsaid anywhere a person could read it.
        testCase "docker binds a socket by path and filters no egress" <| fun () ->
            let docker = Sandboxes.limitsFor DockerBackend "linux"
            Expect.isTrue (HostLimits.can HostDistinction.SocketsByPath docker) "a bind mount is per path"
            Expect.isFalse (HostLimits.can HostDistinction.EgressByHost docker) "and it filters nothing"

        // A granted socket on Linux is COARSENED, not lost: the policy still holds it, says so,
        // and the backend is told to do the wider thing. All three, because any two without
        // the third is a lie — a report about something that did not happen, or a widening
        // nobody was told about.
        testCase "a socket this host cannot scope is still granted, said, and widened" <| fun () ->
            let policy =
                Sandboxes.policyFor
                    // Linux: the host that cannot make this distinction, which is the whole
                    // case. On darwin there is nothing to coarsen and nothing to report.
                    SrtBackend (Sandboxes.limitsFor SrtBackend "linux") Map.empty Map.empty (Some "/ws") None (Some "/ws/home")
                    [ Socket "/run/docker.sock" ]
                    EnvironmentSpec.defaults
                |> expect
            Expect.equal policy.Sockets [ "/run/docker.sock" ] "still granted"
            match policy.Realisation with
            | [ Socket "/run/docker.sock", LeafRealisation.Coarsened got ] ->
                Expect.isTrue (got.Contains "any unix socket") (sprintf "and said, as: %s" got)
            | other -> failwithf "expected one coarsened socket, got %A" other
            let config = Sandboxes.SrtSandbox.configFor (toolsWithRuntime []) policy
            Expect.isTrue config.AllowAllUnixSockets "and the backend does the wider thing it said it would"

        // The common case says nothing. A host that can express the whole selection must not
        // produce a "no degradations" report for somebody to read past.
        testCase "a host that can express the grant reports nothing" <| fun () ->
            let policy =
                Sandboxes.policyFor
                    SrtBackend (Sandboxes.limitsFor SrtBackend "darwin") Map.empty Map.empty (Some "/ws") None (Some "/ws/home")
                    [ Mount { From = "/opt/tools"; At = "/opt/tools"; Mode = ResourceMountMode.Read }
                      Variable ("LANG", "C.UTF-8") ]
                    EnvironmentSpec.defaults
                |> expect
            Expect.equal policy.Realisation [] "nothing to say"
            let config = Sandboxes.SrtSandbox.configFor (toolsWithRuntime []) policy
            Expect.isFalse config.AllowAllUnixSockets "and nothing widened"

        // --- what a sandbox is asked to prove before it is declared started -----------------

        // The order is the whole design. A HOME that cannot be written makes half the list
        // fail, so it goes first and only the first failure is reported — a report naming all
        // twenty buries the one that explains the other nineteen.
        testCase "the checks lead with the two places every toolchain writes" <| fun () ->
            let policy =
                { Support.emptyPolicy with
                    WritePaths = [ "/data/workspace" ]
                    ReadPaths = [ "/opt/tools" ]
                    Env = Map.ofList [ "HOME", "/data/home"; "TMPDIR", "/data/tmp" ] }
            match SandboxVerification.plan policy |> List.map (fun c -> c.What) with
            | first :: second :: rest ->
                Expect.isTrue (first.Contains "/data/home") (sprintf "HOME first, said: %s" first)
                Expect.isTrue (second.Contains "/data/tmp") (sprintf "then TMPDIR, said: %s" second)
                Expect.isTrue (rest |> List.exists (fun w -> w.Contains "/data/workspace"))
                    (sprintf "then what was granted, said: %A" rest)
                Expect.isTrue (rest |> List.exists (fun w -> w.Contains "/opt/tools"))
                    (sprintf "including what was granted read-only, said: %A" rest)
            | other -> failwithf "expected a list leading with HOME and TMPDIR, got %A" other

        // A path that is writable is readable — #320 made that a rule — so checking the read
        // of it says the less useful half of something already asserted, and costs a probe on
        // the path to every sandbox start.
        testCase "a path granted writable is not checked twice" <| fun () ->
            let policy =
                { Support.emptyPolicy with
                    WritePaths = [ "/data/workspace" ]
                    ReadPaths = [ "/data/workspace"; "/opt/tools" ] }
            let mentions = SandboxVerification.plan policy |> List.filter (fun c -> c.What.Contains "/data/workspace")
            Expect.equal (List.length mentions) 1 "once, as a write"

        // Nothing is enforced, so nothing can fail — and a check that always passes reads as
        // coverage while being none.
        testCase "an unconfined sandbox is asked to prove nothing" <| fun () ->
            let policy =
                { Support.emptyPolicy with
                    Filesystem = Unconfined
                    WritePaths = [ "/data/workspace" ]
                    Env = Map.ofList [ "HOME", "/data/home" ] }
            Expect.equal (SandboxVerification.plan policy) [] "no confinement, no claim to check"

        // What a person actually reads. The probe exits with a number and says nothing; the
        // sentence has to come from the plan, and it has to say that the rest went unchecked
        // or a reader assumes everything else passed.
        testCase "the first failure is explained in the words of whoever has to fix it" <| fun () ->
            let policy =
                { Support.emptyPolicy with Env = Map.ofList [ "HOME", "/data/home" ] }
            let checks = SandboxVerification.plan policy
            let said = SandboxVerification.explain checks "0"
            Expect.isTrue (said.Contains "/data/home") (sprintf "names the path, said: %s" said)
            Expect.isTrue (said.Contains "$HOME") (sprintf "and why it matters, said: %s" said)
            Expect.isTrue (said.Contains "Nothing after that was checked")
                (sprintf "and does not let the rest read as passed, said: %s" said)

        // A probe that failed in a way this code cannot place still has to produce a
        // sentence. Silence here would be a sandbox refusing to start for no stated reason,
        // which is the exact fault the whole check exists to remove.
        testCase "a failure it cannot place still says something" <| fun () ->
            let checks = SandboxVerification.plan { Support.emptyPolicy with Env = Map.ofList [ "HOME", "/h" ] }
            let said = SandboxVerification.explain checks ""
            Expect.isTrue (said.Contains "did not say which") (sprintf "said: %s" said)

        // --- and what happens when one of them says no --------------------------------------

        // The contract between the two halves, and the one thing neither half can be tested
        // for alone: the number a probe prints when it fails is the number that names that
        // probe. They are written in different functions, so nothing but this stops them
        // drifting one apart — and a sentence naming the wrong check is worse than none,
        // because somebody will go and fix the path it names.
        testCase "the index a probe prints is the index the sentence reads" <| fun () ->
            let policy =
                { Support.emptyPolicy with
                    WritePaths = [ "/data/one"; "/data/two" ]
                    ReadPaths = [ "/opt/three" ]
                    Env = Map.ofList [ "HOME", "/data/home"; "TMPDIR", "/data/tmp" ] }
            let checks = SandboxVerification.plan policy
            let lines = (SandboxVerification.program checks).Split '\n'
            Expect.equal (Array.length lines) (List.length checks) "one line per check"
            checks
            |> List.iter (fun check ->
                let line = lines |> Array.find (fun l -> l.Contains check.Probe)
                let echoed = line.Substring(line.IndexOf "echo " + 5).Split ';' |> Array.head
                let said = SandboxVerification.explain checks echoed
                Expect.isTrue (said.Contains check.What)
                    (sprintf "index %s should name %s, said: %s" echoed check.What said))

        // The wiring, driven end to end without a sandbox: the checks are turned into a
        // program, the program's answer is turned into a sentence, and the sentence names the
        // check that failed. Pins what the pure tests above cannot — that the index a probe
        // prints and the index `explain` reads are the same index.
        testCaseAsync "a sandbox that fails a check does not start, and says which one" <|
            async {
                let policy =
                    { Support.emptyPolicy with
                        WritePaths = [ "/data/workspace" ]
                        Env = Map.ofList [ "HOME", "/data/home" ] }
                // A shell that fails the FIRST check and no other, which is what a real
                // unwritable HOME does.
                match! Yession.SessionProcess.SessionEnvironment.verify policy (failingAt 0) with
                | Ok () -> failwith "expected the sandbox to be refused"
                | Error reason ->
                    Expect.isTrue (reason.Contains "/data/home")
                        (sprintf "names the check that failed, said: %s" reason)
                    Expect.isFalse (reason.Contains "/data/workspace")
                        (sprintf "and not the ones that never ran, said: %s" reason)
            }

        // A sandbox that cannot run a shell cannot run a command, which is the only thing
        // anybody wants one for. Not a false alarm, and it must not read as a policy fault.
        testCaseAsync "a sandbox that cannot run a command at all is refused in those words" <|
            async {
                let policy = { Support.emptyPolicy with Env = Map.ofList [ "HOME", "/data/home" ] }
                match! Yession.SessionProcess.SessionEnvironment.verify policy cannotSpawn with
                | Ok () -> failwith "expected the sandbox to be refused"
                | Error reason ->
                    Expect.isTrue (reason.Contains "cannot run a command at all")
                        (sprintf "said: %s" reason)
            }

        // The invariant the verb exists for, and the only one: srt reads `CLAUDE_CODE_TMPDIR`
        // off this process at wrap time and bakes it into the child's `TMPDIR`, while the
        // policy decides what the child may write. Those are two readers of one decision, and
        // a sandbox whose `TMPDIR` is not in its write list cannot write a temporary file —
        // which is what was measured before this: `W DENY /tmp/claude` inside a sandbox whose
        // own config named `/tmp/claude`.
        testCase "the temp dir srt is told about is the one the sandbox may write" <| fun () ->
            let dir = tempDir ()
            let prepared = Sandboxes.SessionLayout.prepareTmpDir dir
            let config = Sandboxes.SrtSandbox.configFor (toolsWithRuntime []) Support.emptyPolicy
            Expect.isTrue (List.contains prepared config.AllowWrite)
                (sprintf "the prepared path is writable, allowed: %A" config.AllowWrite)
            Expect.isTrue (List.contains prepared config.AllowRead)
                (sprintf "and readable, since a temp dir that cannot be statted is no temp dir, allowed: %A" config.AllowRead)

        // A sandbox finds what it declared it needs, before anything runs in it. The case
        // this exists for is a toolchain whose FIRST use does something a sandbox cannot —
        // .NET taking a named mutex under /tmp, which NuGet only does when its migration
        // marker is missing (docs/GAPS.md).
        testCase "a sandbox's home is made holding what it asked to find there" <| fun () ->
            let home = tempDir () + "/home"
            let path = HomePath.create ".local/share/NuGet/Migrations/1" |> expect
            Sandboxes.SessionLayout.prepareHome home (Map.ofList [ path, "" ])
            Expect.isTrue
                (Fs.exists (home + "/.local/share/NuGet/Migrations/1"))
                "the file is there, parents and all"

        // A home outlives a restart, so a seed is a starting point rather than a policy:
        // overwriting on every start would silently undo whatever the sandbox has since
        // done with the file.
        testCase "a seed does not overwrite what the sandbox has since written" <| fun () ->
            let home = tempDir () + "/home"
            let path = HomePath.create "tool/state" |> expect
            Sandboxes.SessionLayout.prepareHome home (Map.ofList [ path, "seeded" ])
            Fs.writeTextAtomic (home + "/tool/state") "the sandbox wrote this"
            Sandboxes.SessionLayout.prepareHome home (Map.ofList [ path, "seeded" ])
            Expect.equal (Fs.readText (home + "/tool/state")) "the sandbox wrote this" "the later start left it alone"

        // Canonical, for the reason `OperatorResources` refuses a path that is not: srt
        // canonicalises an allow-list entry and the OS denies the symlink nodes an access
        // traverses, so a temp dir reached through one is granted under a name nothing uses.
        // A macOS data directory under `/var/folders` is exactly that case, unprompted.
        testCase "the temp dir it prepares is the path the kernel will check" <| fun () ->
            let dir = tempDir ()
            let prepared = Sandboxes.SessionLayout.prepareTmpDir dir
            Expect.equal (Fs.canonical prepared) (Some prepared) "it settled on a path that is its own canonical form"

        testCase "the srt config carries the tools, the egress and the temp dir through" <| fun () ->
            let policy = { Support.emptyPolicy with WritePaths = [ "/data/workspace" ]; AllowedDomains = Some [ "api.example.com" ] }
            let config =
                Sandboxes.SrtSandbox.configFor
                    { toolsWithRuntime [] with
                        Bwrap = Some "/usr/bin/bwrap"
                        Socat = Some "/usr/bin/socat"
                        Ripgrep = Some "/usr/bin/rg" }
                    policy
            Expect.isTrue (List.contains "/data/workspace" config.AllowWrite) "the policy's write paths"
            Expect.isTrue (List.contains (Sandboxes.SessionLayout.tmpDir ()) config.AllowWrite) "and the temp dir srt redirects TMPDIR to"
            Expect.equal config.AllowedDomains [ "api.example.com" ] "the egress allowlist rides through"
            Expect.equal config.Bwrap (Some "/usr/bin/bwrap") "the named confinement tool rides through"
            Expect.equal config.Ripgrep (Some "/usr/bin/rg") "and so does the scanner srt will not start without"
            Expect.isFalse config.WeakNesting "the strict profile is what a configured host gets"

        // The rule the case above states in passing — "a workspace that cannot be read is no
        // workspace" — held for the policy's own paths and not for the ones srt adds. The
        // temp dir it redirects TMPDIR to was writable and unreadable at once, so a process
        // could create a file in a directory it could not stat. Derive `AllowRead` from the
        // writable set and this cannot come back; regress the derivation and this goes red.
        testCase "everything a sandbox may write, it may also read" <| fun () ->
            let policy =
                { Support.emptyPolicy with
                    ReadPaths = [ "/opt/tools" ]
                    WritePaths = [ "/data/workspace" ] }
            let config = Sandboxes.SrtSandbox.configFor (toolsWithRuntime [ "/usr" ]) policy
            let unreadable = config.AllowWrite |> List.filter (fun path -> not (List.contains path config.AllowRead))
            Expect.equal unreadable [] "a path that can be written and not read is not a path"

        testCase "a refusal naming a tool this process can execute settled nothing about the host" <| fun () ->
            // srt reports a fork it could not take — no `which` on PATH, a box too busy to
            // hand one out inside a second — as `ripgrep (<path>) not found`. The file is
            // right there, so the host was never the question that got answered.
            Expect.equal
                (Sandboxes.SrtSandbox.startFailure (fun _ -> true) namedToolsConfig)
                Sandboxes.NothingSettled
                "the next sandbox asks again rather than inheriting an answer nobody gave"

        testCase "a refusal naming a tool this process cannot execute is a host that cannot confine" <| fun () ->
            Expect.equal
                (Sandboxes.SrtSandbox.startFailure (fun path -> path <> "/usr/bin/rg") namedToolsConfig)
                Sandboxes.HostCannotConfine
                "that answer does not change while the process lives, so it is the one kept"

        testCase "the sentence for a probe that did not run contradicts srt instead of repeating it" <| fun () ->
            let said =
                Sandboxes.SrtSandbox.probeDidNotRun
                    namedToolsConfig
                    3
                    "Sandbox dependencies not available: ripgrep (/usr/bin/rg) not found"
            Expect.isTrue (said.Contains "/usr/bin/rg") "the path srt called missing is named"
            Expect.isTrue
                (said.Contains "executable in this process")
                "beside the thing that contradicts it, so nobody goes looking for a tool that is there"

        testCase "a policy naming no domains gets no egress, never all of it" <| fun () ->
            let config = Sandboxes.SrtSandbox.configFor (toolsWithRuntime []) Support.emptyPolicy
            Expect.equal config.AllowedDomains [] "srt has no unrestricted mode, so nothing configured means nothing reachable"

        testCase "an install prefix is the tree a runtime was installed AS, never a region above it" <| fun () ->
            // What has to stay readable when reads are scoped: the interpreter, srt's own
            // vendored helper. Nix gives every dependency its own store path, so the store
            // is the unit; an npm install shares one node_modules, so the tree that owns it
            // is; a one-segment answer is a region far larger than what was installed in it.
            Expect.equal
                (Sandboxes.SrtSandbox.installPrefix "/nix/store/abc-nodejs-24/bin/node")
                (Some "/nix/store")
                "under Nix the dependencies are siblings in the store, not children of the prefix"
            Expect.equal
                (Sandboxes.SrtSandbox.installPrefix "/srv/app/node_modules/@anthropic-ai/sandbox-runtime/package.json")
                (Some "/srv/app")
                "an npm install is the tree that owns the node_modules"
            Expect.equal
                (Sandboxes.SrtSandbox.installPrefix "/opt/node22/bin/node")
                (Some "/opt/node22")
                "anywhere else it is the prefix above bin"
            Expect.equal
                (Sandboxes.SrtSandbox.installPrefix "/usr/bin/node")
                None
                "and a one-segment prefix is dropped: /usr is the platform's to name, not an install's"

        // What an operator hands out is a RESOURCE somebody selects, and it arrives through
        // the policy. This list is only the platform's own and the runtimes this process
        // names — it used to fold the operator's `YESSION_SESSION_READ` in as well, which
        // made one variable a ceiling and an unconditional grant at once.
        testCase "the read scope is the platform's and what is running, and nothing an operator hands out" <| fun () ->
            let paths = Sandboxes.SrtSandbox.runtimeReadPaths "linux" [ "/opt/node22/bin/node" ] Map.empty
            Expect.isTrue (List.contains "/usr" paths) "the platform's runtime is there"
            Expect.isTrue (List.contains "/opt/node22" paths) "so is what is already running"

        testCase "the read scope allows /etc by the file, never the directory that holds the secrets" <| fun () ->
            // /etc is the one region in the runtime list that also holds an operator's
            // credentials, so it is named a file at a time. `/etc` itself would re-allow
            // shadow, and every private key a distribution keeps under it.
            Expect.isTrue (List.contains "/etc/ssl" Sandboxes.SrtSandbox.linuxRuntimePaths) "the trust store is named"
            Expect.isFalse (List.contains "/etc" Sandboxes.SrtSandbox.linuxRuntimePaths) "the directory itself is not"

        testCase "an install prefix that is the operator's home is not an allow-back" <| fun () ->
            // `npm i yession` run in a home directory puts the tree at ~/node_modules, whose
            // owning prefix is the home itself — so allowing it back would hand over the
            // whole region this scope exists to deny, and silently.
            let ambient = Map.ofList [ "HOME", "/home/operator" ]
            Expect.isFalse
                (List.contains
                    "/home/operator"
                    (Sandboxes.SrtSandbox.runtimeReadPaths "linux" [ "/home/operator/node_modules/x/package.json" ] ambient))
                "a discovered prefix that is the home is dropped"

        testCase "the read scope's platform list is the platform's, not this box's" <| fun () ->
            Expect.isTrue
                (List.contains "/System" (Sandboxes.SrtSandbox.runtimeReadPaths "darwin" [] Map.empty))
                "a darwin host gets darwin's runtime locations"
            Expect.isFalse
                (List.contains "/System" (Sandboxes.SrtSandbox.runtimeReadPaths "linux" [] Map.empty))
                "and a Linux host does not"

        testCase "the srt config opens .git/config, which a clone cannot avoid writing" <| fun () ->
            let config = Sandboxes.SrtSandbox.configFor (toolsWithRuntime []) Support.emptyPolicy
            Expect.isTrue config.AllowGitConfig "srt's default denies the write every `git clone` makes"

        testCase "an unconfined policy turns srt's filesystem rules off, and only that policy does" <| fun () ->
            let configFor filesystem =
                Sandboxes.SrtSandbox.configFor (toolsWithRuntime []) { Support.emptyPolicy with Filesystem = filesystem }
            Expect.isFalse (configFor Confined).FilesystemDisabled "every ordinary sandbox is confined"
            Expect.isTrue
                (configFor Unconfined).FilesystemDisabled
                "the clone's sandbox is not — a checkout carries names srt refuses to write"

        testCase "the confinement tools: named, blank is absent, and weakening is never a guess" <| fun () ->
            let tools =
                Sandboxes.SrtSandbox.toolsFrom
                    (Map.ofList
                        [ "YESSION_BIN_BWRAP", " /nix/store/x/bin/bwrap "
                          "YESSION_BIN_SOCAT", ""
                          "YESSION_BIN_RIPGREP", "/nix/store/x/bin/rg" ])
                |> expect
            Expect.equal tools.Bwrap (Some "/nix/store/x/bin/bwrap") "a named tool is trimmed and used"
            Expect.equal tools.Ripgrep (Some "/nix/store/x/bin/rg") "every dependency is named, not left to PATH"
            Expect.equal tools.Socat None "a blank one is absent (darwin sets neither), not a path of empty string"
            Expect.equal tools.Nesting Sandboxes.StrictNesting "unconfigured means the strict profile"
            Expect.isTrue
                (List.contains "/usr" tools.Runtime)
                "and the host's own runtime is discovered, not left to a caller to remember"
            Expect.equal
                (Sandboxes.SrtSandbox.toolsFrom (Map.ofList [ "YESSION_NESTED_SANDBOX", "weak" ])
                 |> expect
                 |> fun t -> t.Nesting)
                Sandboxes.WeakNesting
                "an unprivileged container asks for the weaker profile explicitly"
            Expect.isError
                (Sandboxes.SrtSandbox.toolsFrom (Map.ofList [ "YESSION_NESTED_SANDBOX", "off" ]))
                "and anything else is a loud error, not a guess at which way to err"

        testCase "an argv survives the shell srt wraps it in" <| fun () ->
            // srt's Linux/macOS wrapper takes a command STRING, so anything an argv can hold
            // has to come back out the other side of a shell intact.
            Expect.equal
                (Sandboxes.SrtSandbox.commandLine "/bin/echo" [ "two words"; "it's"; "$HOME"; "a;b" ])
                "'/bin/echo' 'two words' 'it'\\''s' '$HOME' 'a;b'"
                "spaces, quotes, expansions and separators are all inert"

        testCase "the agent's egress: a known default, replaceable wholesale" <| fun () ->
            Expect.equal
                (Sandboxes.AgentSandbox.domainsFrom Map.empty)
                Sandboxes.AgentSandbox.defaultDomains
                "unconfigured, the CLI reaches the API and the console it refreshes a credential against"
            Expect.equal
                (Sandboxes.AgentSandbox.domainsFrom (Map.ofList [ "YESSION_SESSION_AGENT_NET", "gateway.internal" ]))
                [ "gateway.internal" ]
                "a deployment that fronts the API elsewhere replaces the list, it does not add to it"

        testCase "policy assembly: spec variables win over the baseline; docker takes no baseline" <| fun () ->
            let ambient = Map.ofList [ "PATH", "/usr/bin"; "HOME", "/home/u" ]
            let resolved = Map.ofList [ "HOME", "/workspace-home"; "TOKEN", "t" ]
            let host =
                Sandboxes.policyFor HostBackend (Sandboxes.limitsFor HostBackend "linux") ambient resolved (Some "/ws") (Some "/repos") (Some "/ws/home") [] EnvironmentSpec.defaults
                |> expect
            Expect.equal (Map.tryFind "HOME" host.Env) (Some "/workspace-home") "the spec's variable wins"
            Expect.equal (Map.tryFind "PATH" host.Env) (Some "/usr/bin") "the baseline fills the rest"
            Expect.equal host.WorkingDirectory (Some "/ws") "the workspace is the default cwd"
            Expect.isTrue
                (List.contains "/repos" host.WritePaths)
                "the repos dir is a write path of its own (Plan 14)"
            let docker = Sandboxes.policyFor DockerBackend (Sandboxes.limitsFor DockerBackend "linux") ambient resolved None None None [] EnvironmentSpec.defaults |> expect
            Expect.equal (Map.tryFind "PATH" docker.Env) None "a docker image supplies its own base env"
            Expect.equal (Map.tryFind "TOKEN" docker.Env) (Some "t") "only the spec's variables inject"

        // A sandbox that inherits a HOME it cannot write is worse off than one with no HOME
        // at all: a tool with none falls back, a tool with one it cannot touch fails. dotnet
        // says so as "The user's home directory could not be determined", which names
        // neither the sandbox nor anything an operator can set.
        testCase "a sandbox has a home of its own, and may write it" <| fun () ->
            let policy =
                Sandboxes.policyFor
                    SrtBackend
                    (Sandboxes.limitsFor SrtBackend "darwin")
                    (Map.ofList [ "HOME", "/Users/operator" ])
                    Map.empty
                    (Some "/data/s/workspace")
                    (Some "/data/s/workspace/repos")
                    (Some "/data/s/home")
                    []
                    EnvironmentSpec.defaults
                |> expect
            Expect.equal (Map.tryFind "HOME" policy.Env) (Some "/data/s/home") "not the operator's, which it is denied"
            Expect.isTrue (List.contains "/data/s/home" policy.WritePaths) "and it may write it, or naming it changed nothing"

        // The layout rule, beside the workspace one it copies: `default` keeps the session's
        // own directory, a named sandbox gets its own — two sandboxes exist precisely so
        // that what happens in one does not happen in the other.
        testCase "each sandbox's home is its own, and the default keeps the session's" <| fun () ->
            let named = SandboxRef.inScope (RepoRef.create "octo/hello" |> expect) (SandboxName.create "dev" |> expect)
            Expect.equal
                (Sandboxes.SessionLayout.homeFor "/data/s" SandboxRef.defaultRef)
                "/data/s/home"
                "the session's own"
            Expect.notEqual
                (Sandboxes.SessionLayout.homeFor "/data/s" named)
                (Sandboxes.SessionLayout.homeFor "/data/s" SandboxRef.defaultRef)
                "and a named sandbox does not share it"

        // The operator's half of the ceiling/grant split. `YESSION_SESSION_READ` was a bound
        // AND an unconditional grant at once, so an operator could not offer a path without
        // forcing it on everything, and a repo asking for one could never obtain it. A
        // resource in the profile's `default` is the grant half, said by name.
        testCase "what the operator granted reaches the sandbox without it asking" <| fun () ->
            let policy =
                Sandboxes.policyFor
                    SrtBackend (Sandboxes.limitsFor SrtBackend "darwin") Map.empty Map.empty (Some "/ws") None (Some "/ws/home")
                    [ Mount { From = "/nix"; At = "/nix"; Mode = ResourceMountMode.Read }
                      Socket "/nix/var/nix/daemon-socket"
                      Endpoint "cache.nixos.org"
                      Variable ("SSL_CERT_FILE", "/nix/ca.crt") ]
                    EnvironmentSpec.defaults
                |> expect
            Expect.isTrue (List.contains "/nix" policy.ReadPaths) "a read mount is readable"
            Expect.isTrue (List.contains "/nix/var/nix/daemon-socket" policy.WritePaths)
                "a socket is written by anything that talks to it, so granting one half grants nothing"
            Expect.equal (Map.tryFind "SSL_CERT_FILE" policy.Env) (Some "/nix/ca.crt") "and a variable arrives"
            Expect.isTrue
                (policy.AllowedDomains |> Option.defaultValue [] |> List.contains "cache.nixos.org")
                "the granted endpoint is reachable though the sandbox asked for nothing"

        // The distinction the whole model turns on: a grant is not bounded by the ceiling,
        // because the ceiling is what a REPO may ask for and this is what the operator
        // already gave. With an empty ceiling and a grant, the grant still holds.
        testCase "a grant is not bounded by the ceiling a repo is held to" <| fun () ->
            let policy =
                Sandboxes.policyFor
                    SrtBackend (Sandboxes.limitsFor SrtBackend "darwin") Map.empty Map.empty (Some "/ws") None (Some "/ws/home")
                    [ Endpoint "cache.nixos.org" ]
                    EnvironmentSpec.defaults
                |> expect
            Expect.equal
                (policy.AllowedDomains)
                (Some [ "cache.nixos.org" ])
                "no operator net ceiling is set, and the grant is reachable anyway"

        // Neither backend here has a union mount, so an overlay would be a resource that
        // READS as "the host's copy stays untouched" and BEHAVES as "write into it".
        testCase "an overlay is refused rather than quietly becoming a write" <| fun () ->
            match
                Sandboxes.policyFor
                    SrtBackend (Sandboxes.limitsFor SrtBackend "darwin") Map.empty Map.empty (Some "/ws") None (Some "/ws/home")
                    [ Mount { From = "/h/.npm"; At = "/h/.npm"; Mode = ResourceMountMode.Overlay } ]
                    EnvironmentSpec.defaults
            with
            | Ok _ -> failwith "expected a refusal"
            | Error e ->
                Expect.isTrue (e.Contains "/h/.npm") (sprintf "the refusal names the path, said: %s" e)
                Expect.isTrue (e.Contains "read or write") (sprintf "and what to do instead, said: %s" e)

        // A toolchain the operator named is the one that answers, so its directory goes in
        // FRONT. This is the first step away from the toolchain being present by accident.
        testCase "a granted executable's directory leads PATH" <| fun () ->
            let policy =
                Sandboxes.policyFor
                    SrtBackend (Sandboxes.limitsFor SrtBackend "darwin") (Map.ofList [ "PATH", "/usr/bin" ]) Map.empty (Some "/ws") None (Some "/ws/home")
                    [ Exec "/nix/store/abc/bin/git" ]
                    EnvironmentSpec.defaults
                |> expect
            Expect.equal
                (Map.tryFind "PATH" policy.Env)
                (Some "/nix/store/abc/bin:/usr/bin")
                "in front of what was inherited, not behind it"
            Expect.isTrue (List.contains "/nix/store/abc/bin/git" policy.ReadPaths) "and the binary is readable"

        // A repo's `workdir` is validated in two places before it gets here — the decoder
        // refuses an absolute path, `toRequest` clamps it at the checkout — and for a while
        // the policy then dropped it, so all that validation guarded a key with no effect.
        // Regress `WorkingDirectory` back to `workspace` and this is the case that goes red.
        testCase "a sandbox that declared where it starts, starts there" <| fun () ->
            let declared =
                { EnvironmentSpec.defaults with
                    WorkingDirectory = Some "/data/s/workspace/repos/octo/hello" }
            let policy =
                Sandboxes.policyFor
                    SrtBackend
                    (Sandboxes.limitsFor SrtBackend "darwin")
                    Map.empty
                    Map.empty
                    (Some "/data/s/workspace")
                    (Some "/data/s/workspace/repos")
                    (Some "/data/s/home")
                    []
                    declared
                |> expect
            Expect.equal
                policy.WorkingDirectory
                (Some "/data/s/workspace/repos/octo/hello")
                "the checkout it asked for, not the workspace around it"

        // The other half of the same change, and the half that could have broken quietly:
        // the workspace is still where it may WRITE. A sandbox that starts in its checkout
        // writing only inside that checkout would be a build that cannot reach its own
        // sibling repos.
        testCase "declaring where a sandbox starts does not narrow where it may write" <| fun () ->
            let declared =
                { EnvironmentSpec.defaults with
                    WorkingDirectory = Some "/data/s/workspace/repos/octo/hello" }
            let policy =
                Sandboxes.policyFor
                    SrtBackend
                    (Sandboxes.limitsFor SrtBackend "darwin")
                    Map.empty
                    Map.empty
                    (Some "/data/s/workspace")
                    (Some "/data/s/workspace/repos")
                    (Some "/data/s/home")
                    []
                    declared
                |> expect
            // Containment rather than equality: what this pins is that declaring a workdir
            // does not take a write path AWAY. The list has since gained the sandbox's own
            // home, and will gain more — an exact match would go red for every addition and
            // say "your redesign is wrong" when it means "something moved".
            Expect.isTrue
                (List.contains "/data/s/workspace" policy.WritePaths)
                "the workspace is still writable"
            Expect.isTrue
                (List.contains "/data/s/workspace/repos" policy.WritePaths)
                "and so is the repos dir"

        // What the change above COSTS, stated so it cannot move without a red test: a
        // relative spawn is resolved against the policy's root, so in a sandbox that
        // declared one, `.` is the checkout. That is the intended reading — a terminal and
        // a one-shot in the same sandbox must agree about where they are — and it is the
        // only thing about existing spawns this change moves.
        testCase "a relative spawn in a declared sandbox resolves against what it declared" <| fun () ->
            Expect.equal
                (SandboxPath.resolvedFrom (Some "/data/s/workspace/repos/octo/hello") (Some "app"))
                (Some "/data/s/workspace/repos/octo/hello/app")
                "relative to the checkout the sandbox declared, not to the workspace"

        // The one place a path in this session's vocabulary becomes an absolute one. Every
        // backend's spawn asks this and nothing else does the arithmetic itself — which is
        // what keeps `repos/octo/hello` meaning the same directory in the timeline, in a
        // repo verb's answer, and in the shell a terminal opens.
        testCase "a spawn's directory is resolved against the sandbox's own root" <| fun () ->
            Expect.equal
                (SandboxPath.resolvedFrom (Some "/data/s/workspace") (Some "repos/octo/hello"))
                (Some "/data/s/workspace/repos/octo/hello")
                "a relative path means what a terminal in this sandbox means by it"
            Expect.equal
                (SandboxPath.resolvedFrom (Some "/data/s/workspace/") (Some "repos/octo/hello"))
                (Some "/data/s/workspace/repos/octo/hello")
                "a trailing slash on the root is the same root"

        testCase "an absolute directory is already an answer, and none at all is the root" <| fun () ->
            Expect.equal
                (SandboxPath.resolvedFrom (Some "/ws") (Some "/repos/octo/hello"))
                (Some "/repos/octo/hello")
                "docker's bind, a named sandbox's shared repos dir — resolved against nothing"
            Expect.equal
                (SandboxPath.resolvedFrom (Some "/ws") None)
                (Some "/ws")
                "a spawn with no opinion runs where the policy puts it"

        // The round trip is the point: what a session HANDS OUT it has to be able to take
        // back, or the two halves drift and a path stops naming a directory.
        testCase "a directory handed out relative resolves back to the one it came from" <| fun () ->
            let root = Some "/data/s/workspace"
            let absolute = "/data/s/workspace/repos/octo/hello"
            Expect.equal
                (SandboxPath.resolvedFrom root (Some (SandboxPath.reachedFrom root absolute)))
                (Some absolute)
                "reachedFrom and resolvedFrom are one fact seen twice"

        testCase "the sandbox's own root round-trips too, as the one path that is always true" <| fun () ->
            // Otherwise the workspace itself is the one directory that can only be said with
            // the operator's home directory in front of it.
            let root = Some "/data/s/workspace"
            Expect.equal (SandboxPath.reachedFrom root "/data/s/workspace") "." "where a terminal already stands"
            Expect.equal (SandboxPath.resolvedFrom root (Some ".")) root "and back"
    ]

// -----------------------------------------------------------------------------
// Step 12 — lazy environment lifecycle: one-shots start nothing; a signalled
// need starts (or restarts) the session's one environment, all as events.
// -----------------------------------------------------------------------------

let private lazyEnvironmentPort = 8115

let private environmentEventsOf (log: Yession.SessionProcess.EventLog<SessionEvent>) =
    async {
        let! page = log.Read None Int32.MaxValue
        return
            page.Events
            |> List.choose (fun e ->
                match e.Event with
                | EnvironmentNeedIdentified _ -> Some "need"
                | EnvironmentStartRequested _ -> Some "start-requested"
                | EnvironmentStarted _ -> Some "started"
                | EnvironmentStartFailed _ -> Some "start-failed"
                | EnvironmentStopRequested _ -> Some "stop-requested"
                | EnvironmentStopped _ -> Some "stopped"
                | _ -> None)
    }

// Pure fold — cheap tier, runs everywhere.
let private environmentProjectionTests =
    testList "Environment projection" [
        testCase "environment events project deterministically into UI state" <| fun () ->
            let step status event = EnvironmentStatus.applyEvent status event
            let s0 = EnvironmentNotStarted
            let s1 = step s0 (EnvironmentNeedIdentified { Reason = "task"; AgentTurnId = None })
            Expect.equal s1 EnvironmentNotStarted "a need alone changes nothing"
            let s2 = step s1 (EnvironmentStartRequested { EnvironmentId = "env-1"; SpecSummary = "local-process" })
            Expect.equal s2 EnvironmentStarting "start requested"
            let s3 = step s2 (EnvironmentStarted { EnvironmentId = "env-1"; ContainerRef = "ctr-1" })
            Expect.equal s3 (EnvironmentRunning "ctr-1") "running"
            let s4 = step s3 (EnvironmentStopped { EnvironmentId = "env-1" })
            Expect.equal s4 EnvironmentDown "stopped"
            let s5 = step s2 (EnvironmentStartFailed { EnvironmentId = "env-1"; Reason = "no image" })
            Expect.equal s5 (EnvironmentFailed "no image") "failure surfaces"
    ]

// What the environment RECORDS, as opposed to what it does. Pure: an in-memory log and a
// `CreateSandbox` that answers without touching the machine, so this runs in the cheap tier
// rather than beside the lifecycle suites that genuinely need a host.
/// A policy with one thing to prove, so `verify` actually spawns instead of returning `Ok`
/// on an empty check list. A HOME is the first check the plan makes.
let private policyWithAHome : unit -> Async<Result<SandboxPolicy, string>> =
    fun () -> async { return Ok { Support.emptyPolicy with Env = Map.ofList [ "HOME", "/data/home" ] } }

/// The same policy, built on a host that could not scope the socket it was asked for — one
/// coarsening, which is the shape every degradation a STARTED sandbox can have takes (a
/// withheld leaf refuses the policy before a sandbox is ever built).
let private policyOnACoarserHost : unit -> Async<Result<SandboxPolicy, string>> =
    fun () ->
        async {
            return
                Ok
                    { Support.emptyPolicy with
                        Env = Map.ofList [ "HOME", "/data/home" ]
                        Realisation =
                            [ Socket "/run/docker.sock",
                              LeafRealisation.Coarsened "any unix socket on this host" ] }
        }

let private environmentRecordingTests =
    testList "What a start attempt records" [

        // --- a sandbox that fails its own checks -------------------------------------------

        // The wiring #333 added, seen from the outside: a sandbox that cannot prove it holds
        // what it was granted must reach a caller as a REFUSAL, in the same shape as a
        // backend that could not create one at all. Tested here and not only at `verify`,
        // because what a caller sees is decided by `ensure` — and `RepoSandboxes` turns
        // exactly this into the sentence a person reads.
        testCaseAsync "a sandbox that fails a start-up check is refused, in the words of the check" <|
            async {
                let sessionId = SessionId.create "unverified-1" |> expect
                let log =
                    Yession.SessionProcess.InMemoryEventLog.create sessionId (fun () -> DateTimeOffset.UtcNow)
                let creating : CreateSandbox = fun _ -> async { return Ok (failingAt 0) }
                let environment =
                    Yession.SessionProcess.SessionEnvironment.create
                        log creating policyWithAHome "scripted" "env-unverified-1"

                match! environment.Ensure None "the fold asked" with
                | EnvironmentAvailable -> failwith "a sandbox that failed its checks must not be available"
                | EnvironmentUnavailable reason ->
                    Expect.isTrue (reason.Contains "/data/home")
                        (sprintf "and says which check, said: %s" reason)

                let! events = environmentEventsOf log
                Expect.equal events [ "need"; "start-requested"; "start-failed" ]
                    "recorded as a start that failed, which is what it is"
            }

        // --- what a sandbox says it holds --------------------------------------------------

        // The report comes off the policy the sandbox was BUILT from, and comes out of the
        // environment rather than being recomputed by whoever reports it. Same reason
        // `verify` takes that policy: a backend asked to describe its own confinement is a
        // backend grading its own work.
        testCaseAsync "a running environment says where this host could not give what was asked" <|
            async {
                let sessionId = SessionId.create "realised-1" |> expect
                let log =
                    Yession.SessionProcess.InMemoryEventLog.create sessionId (fun () -> DateTimeOffset.UtcNow)
                let creating : CreateSandbox = fun _ -> async { return Ok passesEveryCheck }
                let environment =
                    Yession.SessionProcess.SessionEnvironment.create
                        log creating policyOnACoarserHost "scripted" "env-realised-1"

                let! outcome = environment.Ensure None "the fold asked"
                Expect.equal outcome EnvironmentAvailable "a coarsening is a sandbox that works differently, not one that fails"
                match environment.Realisation () with
                | [ said ] ->
                    Expect.isTrue (said.Contains "/run/docker.sock") (sprintf "the grant is named, said: %s" said)
                    Expect.isTrue (said.Contains "any unix socket") (sprintf "and what it became, said: %s" said)
                | other -> failwithf "expected one line, got %A" other
            }

        // Before anything runs there is nothing to report, and this is the half that keeps
        // the panel honest: a column read off an environment that had not started would
        // otherwise describe a sandbox that does not exist.
        testCaseAsync "an environment that has not started holds nothing" <|
            async {
                let sessionId = SessionId.create "realised-2" |> expect
                let log =
                    Yession.SessionProcess.InMemoryEventLog.create sessionId (fun () -> DateTimeOffset.UtcNow)
                let creating : CreateSandbox = fun _ -> async { return Ok passesEveryCheck }
                let environment =
                    Yession.SessionProcess.SessionEnvironment.create
                        log creating policyOnACoarserHost "scripted" "env-realised-2"
                Expect.equal (environment.Realisation ()) [] "nothing runs, nothing is claimed"
            }

        // And it stops claiming it when the sandbox goes. The sandbox and what it holds live
        // in one cell for exactly this: two would be two things a stop has to clear, and the
        // one that got forgotten would be a closed sandbox still answering for a widening.
        testCaseAsync "a stopped environment stops saying what it held" <|
            async {
                let sessionId = SessionId.create "realised-3" |> expect
                let log =
                    Yession.SessionProcess.InMemoryEventLog.create sessionId (fun () -> DateTimeOffset.UtcNow)
                let creating : CreateSandbox = fun _ -> async { return Ok passesEveryCheck }
                let environment =
                    Yession.SessionProcess.SessionEnvironment.create
                        log creating policyOnACoarserHost "scripted" "env-realised-3"
                let! _ = environment.Ensure None "the fold asked"
                do! environment.Stop ()
                Expect.equal (environment.Realisation ()) [] "the sandbox is gone, and so is what it held"
            }

        // `running` is never set for it, so nothing can reach a sandbox that failed its own
        // checks. The observable form of that: the next ask ATTEMPTS again rather than
        // handing back the one that failed — a sandbox kept in `running` would be reused
        // forever and never re-created when the fault cleared.
        testCaseAsync "a sandbox that failed its checks is never handed to anybody" <|
            async {
                let sessionId = SessionId.create "unverified-2" |> expect
                let log =
                    Yession.SessionProcess.InMemoryEventLog.create sessionId (fun () -> DateTimeOffset.UtcNow)
                let mutable created = 0
                let creating : CreateSandbox =
                    fun _ ->
                        async {
                            created <- created + 1
                            return Ok (failingAt 0)
                        }
                let environment =
                    Yession.SessionProcess.SessionEnvironment.create
                        log creating policyWithAHome "scripted" "env-unverified-2"

                let! _ = environment.Ensure None "the fold asked"
                let! _ = environment.Ensure None "the fold asked again"
                Expect.equal created 2 "it was built again rather than the failed one reused"

                // And the refusal is not re-announced, because #307's rule applies to this
                // refusal like any other — the fold re-runs after every repo verb.
                let! events = environmentEventsOf log
                Expect.equal events [ "need"; "start-requested"; "start-failed" ]
                    "said once, however many times it is asked"
            }

        // Disposed, not leaked. A sandbox is a real process tree under srt and a container
        // under docker; one abandoned per failed start, on a fold that re-runs after every
        // repo verb, is an unbounded leak of the most expensive thing here.
        testCaseAsync "a sandbox that failed its checks is disposed" <|
            async {
                let sessionId = SessionId.create "unverified-3" |> expect
                let log =
                    Yession.SessionProcess.InMemoryEventLog.create sessionId (fun () -> DateTimeOffset.UtcNow)
                let mutable disposed = 0
                let creating : CreateSandbox =
                    fun _ ->
                        async {
                            return Ok { failingAt 0 with Dispose = fun () -> async { disposed <- disposed + 1 } }
                        }
                let environment =
                    Yession.SessionProcess.SessionEnvironment.create
                        log creating policyWithAHome "scripted" "env-unverified-3"

                let! _ = environment.Ensure None "the fold asked"
                Expect.equal disposed 1 "the sandbox it could not vouch for was torn down"
            }

        // The fold that asks for a repo's sandboxes re-runs after every repo verb, so a
        // declaration the operator's ceiling refuses used to record three events every time
        // anyone touched a repo — identical reasons, unbounded, about a session in which
        // nothing had changed. Regress the suppression and this is the case that goes red.
        testCaseAsync "a refusal the log already ends with is not recorded a second time" <|
            async {
                let sessionId = SessionId.create "refused-1" |> expect
                let log =
                    Yession.SessionProcess.InMemoryEventLog.create sessionId (fun () -> DateTimeOffset.UtcNow)
                let refusing : CreateSandbox = fun _ -> async { return Error "the ceiling is closed" }
                let environment =
                    Yession.SessionProcess.SessionEnvironment.create
                        log refusing preparedEmptyPolicy "scripted" "env-refused-1"

                let! first = environment.Ensure None "the fold asked"
                Expect.equal first (EnvironmentUnavailable "the ceiling is closed") "it cannot start"
                let! afterFirst = environmentEventsOf log
                Expect.equal
                    afterFirst
                    [ "need"; "start-requested"; "start-failed" ]
                    "the first refusal is news, and says the whole story"

                let! second = environment.Ensure None "the fold asked again"
                Expect.equal second (EnvironmentUnavailable "the ceiling is closed") "still cannot start"
                let! afterSecond = environmentEventsOf log
                Expect.equal afterSecond afterFirst "and the second changed nothing, so it said nothing"
            }

        // The other half, and the half that keeps the suppression from becoming a cache of
        // refusals: what is compared is the REASON, so an outcome that moved is recorded the
        // first time it differs. This is what a credential signed in mid-session, or a daemon
        // that came back, looks like from here.
        testCaseAsync "a refusal that differs from the last one is recorded" <|
            async {
                let sessionId = SessionId.create "refused-2" |> expect
                let log =
                    Yession.SessionProcess.InMemoryEventLog.create sessionId (fun () -> DateTimeOffset.UtcNow)
                let mutable said = "the ceiling is closed"
                let refusing : CreateSandbox = fun _ -> async { return Error said }
                let environment =
                    Yession.SessionProcess.SessionEnvironment.create
                        log refusing preparedEmptyPolicy "scripted" "env-refused-2"

                let! _ = environment.Ensure None "the fold asked"
                said <- "no credential to forward"
                let! _ = environment.Ensure None "the fold asked again"

                let! events = environmentEventsOf log
                Expect.equal
                    events
                    [ "need"; "start-requested"; "start-failed"
                      "need"; "start-requested"; "start-failed" ]
                    "a different reason is a different thing to say"
            }

        // A refusal is only stale while it is the last word. Once the environment comes up,
        // the log no longer ends with that failure — so a later one is news again, and that
        // is why the comparison is against the last OUTCOME rather than against every
        // failure the log has ever held.
        testCaseAsync "a failure after the environment has started is recorded again" <|
            async {
                let recorder = SandboxRecorder ()
                let sessionId = SessionId.create "refused-3" |> expect
                let log =
                    Yession.SessionProcess.InMemoryEventLog.create sessionId (fun () -> DateTimeOffset.UtcNow)
                let mutable up = false
                let flaky : CreateSandbox =
                    fun policy ->
                        if up then scriptedSandbox recorder echoSandboxScript policy
                        else async { return Error "not yet" }
                let environment =
                    Yession.SessionProcess.SessionEnvironment.create
                        log flaky preparedEmptyPolicy "scripted" "env-refused-3"

                let! _ = environment.Ensure None "too early"
                up <- true
                let! _ = environment.Ensure None "now"
                do! environment.Stop ()
                up <- false
                let! _ = environment.Ensure None "and down again"

                let! events = environmentEventsOf log
                Expect.equal
                    events
                    [ "need"; "start-requested"; "start-failed"
                      "need"; "start-requested"; "started"
                      "stop-requested"; "stopped"
                      "need"; "start-requested"; "start-failed" ]
                    "the start in between makes the second failure news"
            }
    ]

let private lazyLifecycleTests =
    testList "Lazy environment lifecycle" [
        testCaseAsync "a conversational one-shot does not start an environment (E2E-1)" <|
            async {
                let recorder = SandboxRecorder ()
                // A conversational agent: answers from context, never signals need.
                let conversational : RunAgent =
                    fun _ _ _signal onChunk ->
                        async {
                            onChunk { Text = "just an answer" }
                            return AgentCompleted ("just an answer", None)
                        }
                let m = Manager.create (Some conversational) (Some (fun _ -> scriptedSandbox recorder echoSandboxScript)) lazyEnvironmentPort
                let! _ =
                    m.StartSession
                        { SessionLaunchRequest.SessionId = SessionId.create "lazy-1" |> expect }
                let managed = (m.Registered ()) |> List.head

                let! a = connectClient (managed.BootstrapUri + "signal") (managed.Host.MintPeerToken ()) "ada" "Ada"
                do! compose a a.Hello.PeerId "what is a monad?"
                a.Connection.SendDraft a.Hello.PeerId
                do! a.Runner.WaitFor (fun model ->
                        model.Conversation.Items |> List.exists (fun i -> i.Body = "just an answer"))

                Expect.equal recorder.Created 0 "no sandbox created for a one-shot"
                let! envEvents = environmentEventsOf managed.Host.Log
                Expect.isEmpty envEvents "no environment events for a one-shot"

                do! a.Channel.Close ()
                do! m.Stop ()
            }

        testCaseAsync "a development task identifies need and starts the environment (E2E-2)" <|
            async {
                let recorder = SandboxRecorder ()
                // A task agent that runs a command. `ensure_environment` retired with stage
                // 3b — opening a terminal IS the need, and running a command opens the
                // agent's — so the need now arrives through the one door that is left. It
                // runs twice to pin the other half: the agent terminal is opened once and
                // reused, so a second command is not a second need.
                let taskAgent : RunAgent =
                    fun _ capabilities _signal onChunk ->
                        async {
                            let! first = capabilities.Terminals.Execute (CommandRequest.ofCommand "true")
                            let! second = capabilities.Terminals.Execute (CommandRequest.ofCommand "true")
                            match first, second with
                            | Ok _, Ok _ ->
                                onChunk { Text = "environment is up" }
                                return AgentCompleted ("environment is up", None)
                            | other -> return AgentFailed (sprintf "%A" other, None)
                        }
                let m = Manager.create (Some taskAgent) (Some (fun _ -> scriptedSandbox recorder echoSandboxScript)) (lazyEnvironmentPort + 1)
                let! _ =
                    m.StartSession
                        { SessionLaunchRequest.SessionId = SessionId.create "lazy-2" |> expect }
                let managed = (m.Registered ()) |> List.head

                let! a = connectClient (managed.BootstrapUri + "signal") (managed.Host.MintPeerToken ()) "ada" "Ada"
                do! compose a a.Hello.PeerId "please run the tests"
                a.Connection.SendDraft a.Hello.PeerId
                do! a.Runner.WaitFor (fun model ->
                        model.Conversation.Items |> List.exists (fun i -> i.Body = "environment is up")
                        && (match model.Environment with EnvironmentRunning _ -> true | _ -> false))

                // A SECOND need, from a different actor and against a different terminal:
                // Ada opens her own. This is the half E2E-2 is really about — a need arriving
                // while an environment is already running must reuse it rather than start a
                // second one — and it is a stronger case than the agent's own second command,
                // which reuses a terminal it already has and is therefore no need at all.
                a.Connection.OpenTerminal "ada's"
                do! a.Runner.WaitFor (fun model ->
                        (Projection.openTerminals model.Terminals |> List.length) = 2)

                Expect.equal recorder.Created 1 "exactly one sandbox created across two needs"
                let! envEvents = environmentEventsOf managed.Host.Log
                Expect.equal
                    envEvents
                    [ "need"; "start-requested"; "started"; "need" ]
                    "need -> start -> started, then the second need reuses the environment"

                // The client's UI reflects the running environment from events alone.
                let html = Support.render (a.Runner.Model ())
                Expect.isTrue (html.Contains (Dom.attr Dom.Hooks.environment Dom.Text.envRunning)) "the environment status renders"

                do! a.Channel.Close ()
                do! m.Stop ()
            }

        testCaseAsync "a stopped environment is restarted by the next need, under the same id (E2E-7)" <|
            async {
                let recorder = SandboxRecorder ()
                let sessionId = SessionId.create "lazy-3" |> expect
                let log =
                    Yession.SessionProcess.InMemoryEventLog.create sessionId (fun () -> DateTimeOffset.UtcNow)
                let environment =
                    Yession.SessionProcess.SessionEnvironment.create
                        log (scriptedSandbox recorder echoSandboxScript) preparedEmptyPolicy "scripted" "env-lazy-3"

                let! first = environment.Ensure None "initial task"
                Expect.equal first EnvironmentAvailable "first ensure starts"
                do! environment.Stop ()
                Expect.equal (environment.CurrentRef ()) None "stopped"
                Expect.equal recorder.Disposed 1 "the stop disposed the sandbox"
                let! second = environment.Ensure None "back for more"
                Expect.equal second EnvironmentAvailable "the next need restarts"
                Expect.equal recorder.Created 2 "two sandbox creations across the stop"

                let! envEvents = environmentEventsOf log
                Expect.equal
                    envEvents
                    [ "need"; "start-requested"; "started"; "stop-requested"; "stopped"; "need"; "start-requested"; "started" ]
                    "the full lifecycle is events, environment id preserved"
                let! page = log.Read None Int32.MaxValue
                let ids =
                    page.Events
                    |> List.choose (fun e ->
                        match e.Event with
                        | EnvironmentStartRequested p -> Some p.EnvironmentId
                        | EnvironmentStarted p -> Some p.EnvironmentId
                        | EnvironmentStopRequested p -> Some p.EnvironmentId
                        | EnvironmentStopped p -> Some p.EnvironmentId
                        | _ -> None)
                    |> List.distinct
                Expect.equal ids [ "env-lazy-3" ] "one environment identity across restart"
            }
    ]

// -----------------------------------------------------------------------------
// Step 13 — command execution: streamed into events, rendered read-only.
// -----------------------------------------------------------------------------

let private commandPort = 8120

/// A real host-backend WorkSandbox composition over the given log — exactly what
/// SessionMain wires, minus the control channel (no secret refs here).
let private hostEnvironment (log: Yession.SessionProcess.EventLog<SessionEvent>) (name: string) =
    let createSandbox = Sandboxes.forBackend HostBackend name EnvironmentSpec.defaults |> expect
    let noSecrets = fun (n: SecretName) -> async { return Error (sprintf "no secrets: %s" (SecretName.value n)) }
    Yession.SessionProcess.SessionEnvironment.create
        log
        createSandbox
        (Sandboxes.preparePolicy HostBackend noSecrets None None None (fun _ -> Ok []) EnvironmentSpec.defaults)
        (Sandboxes.summaryFor HostBackend EnvironmentSpec.defaults)
        (sprintf "env-%s" name)

/// The per-session host sandbox for the in-process Manager compositions below.
let private hostSandboxFor (sessionId: SessionId) : CreateSandbox =
    Sandboxes.forBackend HostBackend (SessionId.value sessionId) EnvironmentSpec.defaults |> expect

// The properties of the retired `Execute` path that OUTLIVE it (Plan 13, stage 3b).
//
// Step 13's command log — `CommandRequested/Started/OutputReceived/Completed` folded into a
// read-only sidebar — retired with the merged tool: nothing feeds it, because the agent's
// commands are terminal blocks now and a block's bytes belong in its transcript rather than
// in the event log a second time. Its fold and its streaming test went with it, covered where
// the behaviour moved (`Terminals`, `PtyIntegration`).
//
// The COMMAND TIMEOUT went too, and deliberately rather than by omission: a block is owned by
// the session, not by the turn that asked for it, so a deadline is now a YIELD — the tool
// returns `Running` with a handle and the command runs on — where the old one killed the
// process. `TerminalCommandWait` is where that is pinned.
//
// What must not be lost is the SECURITY property, which is about the sandbox seam rather than
// about any tool on top of it.

// The case below plants a credential in the process env, and the suite is ONE process — so
// how it hands the environment back is load-bearing for everything compiled after it. It
// once handed it back by DELETING the name, which silently disarmed the whole LiveAgent
// tier: those sessions started with no credential, `SessionMain` answers that by starting no
// agent at all, and a turn just never got a reply. `Support.withEnv` is the fix; these three
// are what make its red mean something.
[<Fable.Core.Emit("(process.env[$0] ?? null)")>]
let private envRaw (name: string) : string option = Fable.Core.Util.jsNative

let private testEnvTests =
    testList "The test environment (take and give back)" [
        testCaseAsync "a variable that was there is put back with the value it had" <|
            async {
                do! Support.withEnv [ "YESSION_ENV_PROBE", Some "before" ] (fun () -> async { () })
                do!
                    Support.withEnv [ "YESSION_ENV_PROBE", Some "before" ] (fun () -> async {
                        do! Support.withEnv [ "YESSION_ENV_PROBE", Some "during" ] (fun () -> async { () })
                        Expect.equal (envRaw "YESSION_ENV_PROBE") (Some "before") "the outer value survives the inner take"
                    })
            }

        testCaseAsync "a variable that was absent is absent again — never left blank or planted" <|
            async {
                do!
                    Support.withEnv [ "YESSION_ENV_ABSENT", None ] (fun () -> async {
                        do! Support.withEnv [ "YESSION_ENV_ABSENT", Some "planted" ] (fun () -> async { () })
                        Expect.equal (envRaw "YESSION_ENV_ABSENT") None "absence is a value, and it is restored"
                    })
            }

        testCaseAsync "a body that throws still gives the environment back" <|
            async {
                do!
                    Support.withEnv [ "YESSION_ENV_PROBE", Some "before" ] (fun () -> async {
                        let! outcome =
                            Support.withEnv [ "YESSION_ENV_PROBE", Some "during" ] (fun () -> async {
                                return failwith "the body blew up"
                            })
                            |> Async.Catch
                        match outcome with
                        | Choice1Of2 _ -> failwith "expected the body's exception to propagate"
                        | Choice2Of2 _ -> ()
                        Expect.equal (envRaw "YESSION_ENV_PROBE") (Some "before") "restored on the exceptional path too"
                    })
            }
    ]

// The other half of the same story. A case's deadline is spent out of the whole Node run's
// budget, and a case that asks for more than the run can afford does not fail as itself — it
// reaches its deadline, the runner kills the process, and every other suite dies unnamed with
// it. That cost two red release runs to work out. `settledWithin` refuses at the CALL instead,
// which is the only moment the two numbers are both in view.
let private testWaitTests =
    testList "The test harness's waits (a deadline the run can afford)" [
        testCaseAsync "a deadline at or beyond the run's whole budget is refused, naming both numbers" <|
            async {
                do!
                    Support.withEnv [ "YESSION_TEST_BUDGET_MS", Some "1000" ] (fun () -> async {
                        let! outcome = Support.settledWithin 2_000 (fun () -> false) |> Async.Catch
                        match outcome with
                        | Choice1Of2 _ -> failwith "expected an unaffordable deadline to be refused"
                        | Choice2Of2 e ->
                            Expect.isTrue (e.Message.Contains "2000" && e.Message.Contains "1000")
                                "the refusal names what was asked for and what the run has"
                    })
            }

        testCaseAsync "a deadline inside the budget is simply waited out" <|
            async {
                do!
                    Support.withEnv [ "YESSION_TEST_BUDGET_MS", Some "10000" ] (fun () -> async {
                        let! held = Support.settledWithin 1_000 (fun () -> true)
                        Expect.isTrue held "the condition already held, so the wait answered true"
                    })
            }

        testCaseAsync "with no budget declared, a deadline is not second-guessed" <|
            async {
                // A bundle run by hand (`node tests/…/out/Main.js`) spends nothing, so there is
                // nothing to refuse against — and refusing there would break the one way to run
                // the suite without `check`.
                do!
                    Support.withEnv [ "YESSION_TEST_BUDGET_MS", None ] (fun () -> async {
                        let! held = Support.settledWithin 999_999 (fun () -> true)
                        Expect.isTrue held "no budget, no refusal"
                    })
            }
    ]

let private commandFoldTests =
    testList "Command execution (local)" [
        testCaseAsync "a host-sandbox command never inherits the session's credentials (leak regression)" <|
            async {
                // The old Manager-side spawn merged `process.env` into every command; this
                // plants a credential there and proves the seam keeps it out. Driven through
                // `Spawn`, which is what the terminal drain uses and therefore what every
                // agent command now goes through.
                // `withEnv`, not set-then-delete: this ran with the REAL credential in CI, and
                // deleting it on the way out left every later suite without one.
                return!
                    Support.withEnv [ "ANTHROPIC_API_KEY", Some "planted-credential" ] (fun () -> async {
                        let sessionId = SessionId.create "cmd-leak" |> expect
                        let log =
                            Yession.SessionProcess.InMemoryEventLog.create sessionId (fun () -> DateTimeOffset.UtcNow)
                        let environment = hostEnvironment log "cmd-leak"
                        let! _ = environment.Ensure None "leak probe"
                        let mutable output = ""
                        let! spawned =
                            environment.Spawn
                                { Executable = "node"
                                  Arguments =
                                    [ "-e"; "console.log('key=' + (process.env.ANTHROPIC_API_KEY || 'absent'))" ]
                                  Env = Map.empty
                                  WorkingDirectory = None }
                                (fun (_, text) -> output <- output + text)
                        match spawned with
                        | Error e -> failwith e
                        | Ok handle ->
                            let! ended = handle.Exited
                            Expect.equal ended (SandboxExited 0) "the probe ran"
                            Expect.isTrue
                                (output.Contains "key=absent")
                                "the planted credential does not reach the command"
                    })
            }
    ]

let private commandTests =
    testList "Command execution" [
        testCaseAsync "an agent-run command reaches browser clients as a terminal block (E2E-3/E2E-4)" <|
            async {
                // The agent runs a real command through its ONE door, and the answer comes
                // back INSIDE the turn — which is what stage 3b bought: the old
                // `queue_terminal_command` returned before anything had happened, so an agent
                // that needed an answer took the ungated path instead.
                let devAgent : RunAgent =
                    fun _ capabilities _signal onChunk ->
                        async {
                            match! capabilities.Terminals.Execute (CommandRequest.ofCommand "echo hello from the env") with
                            | Ok outcome when outcome.Status = TerminalCommandRan (CommandSucceeded 0) ->
                                onChunk { Text = "ran it" }
                                return AgentCompleted ("ran it", None)
                            | other -> return AgentFailed (sprintf "%A" other, None)
                        }
                let m = Manager.create (Some devAgent) (Some hostSandboxFor) commandPort
                let! _ =
                    m.StartSession
                        { SessionLaunchRequest.SessionId = SessionId.create "cmd-e2e-session" |> expect }
                let managed = (m.Registered ()) |> List.head

                // Two clients: the sender, and a second browser that must see the same
                // block purely through event pages.
                let! a = connectClient (managed.BootstrapUri + "signal") (managed.Host.MintPeerToken ()) "ada" "Ada"
                let! b = connectClient (managed.BootstrapUri + "signal") (managed.Host.MintPeerToken ()) "grace" "Grace"
                do! compose a a.Hello.PeerId "run the thing"
                a.Connection.SendDraft a.Hello.PeerId

                let sawCommand (model: ClientModel) =
                    model.Terminals.Terminals
                    |> List.exists (fun t ->
                        t.Blocks
                        |> List.exists (fun b ->
                            b.Command.Contains "hello from the env"
                            && b.Status = BlockFinished (CommandSucceeded 0)))
                do! a.Runner.WaitFor sawCommand
                do! b.Runner.WaitFor sawCommand

                // E2E-3: the lifecycle events are in the log, in order.
                let! page = managed.Host.Log.Read None Int32.MaxValue
                let kinds =
                    page.Events
                    |> List.choose (fun e ->
                        match e.Event with
                        | SessionEvent.TerminalOpened _ -> Some "terminal"
                        | SessionEvent.TerminalBlockStarted _ -> Some "started"
                        | SessionEvent.TerminalBlockCompleted _ -> Some "completed"
                        | _ -> None)
                Expect.equal kinds [ "terminal"; "started"; "completed" ] "the block lifecycle is appended, in order"

                // E2E-4: the UI renders the block from events. The read-only command log it
                // used to render retired with the merged tool (Plan 13, stage 3b) — a terminal
                // block IS the read-only record now, and it is the one people can also act on.
                let html = Support.render (b.Runner.Model ())
                Expect.isTrue (html.Contains Dom.Hooks.terminalBlock) "the block renders"
                Expect.isTrue
                    (html.Contains (Dom.attr Dom.Hooks.terminalBlockStatus Dom.Text.blockOk))
                    "with its exit status"
                Expect.isTrue (html.Contains "hello from the env") "and the command that ran"

                do! a.Channel.Close ()
                do! b.Channel.Close ()
                do! m.Stop ()
            }
    ]

// -----------------------------------------------------------------------------
// Step 14 — acceptance-gate additions: mixed-event offsets, mixed-event catch-up
// (E2E-8), and the Docker adapter smoke (gated on daemon availability).
// -----------------------------------------------------------------------------

let private acceptancePort = 8125

let private acceptanceTests =
    testList "Phase 2 acceptance" [
        testCaseAsync "event offsets remain monotonic across message, agent, environment, and terminal events" <|
            async {
                let sessionId = SessionId.create "mixed-offsets" |> expect
                let log = Yession.SessionProcess.InMemoryEventLog.create sessionId (fun () -> DateTimeOffset.UtcNow)
                let ada = PeerId.create "ada" |> expect
                let mixed : SessionEvent list =
                    [ MessageSent
                        { MessageId = MessageId.create "m1" |> expect
                          QueueId = None
                          Author = PeerRef ada
                          Body = "hi" }
                      AgentTurnStarted
                        { AgentTurnId = AgentTurnId.create "t1" |> expect
                          TriggeredByMessageId = Some (MessageId.create "m1" |> expect); Woke = None }
                      EnvironmentNeedIdentified { Reason = "task"; AgentTurnId = None }
                      EnvironmentStarted { EnvironmentId = "env"; ContainerRef = "ctr" }
                      SessionEvent.TerminalOpened
                        { TerminalId = TerminalId.create "t1" |> expect; OpenedBy = ActorRef.Agent; Title = (TerminalTitle.fromProse "agent"); Sandbox = Some SandboxRef.defaultRef; Renewable = false }
                      SessionEvent.TerminalBlockStarted
                        { TerminalId = TerminalId.create "t1" |> expect
                          BlockId = BlockId.create "b1" |> expect
                          QueueId = None
                          Authority = Authority.agentFor (PeerRef ada)
                          Command = "true"
                          FromSeq = 0
                          Background = false }
                      SessionEvent.TerminalBlockCompleted
                        { TerminalId = TerminalId.create "t1" |> expect
                          BlockId = BlockId.create "b1" |> expect
                          Result = CommandSucceeded 0
                          ToSeq = 1 } ]
                for event in mixed do
                    let! _ = log.Append ActorRef.SessionProcess event
                    ()
                let! page = log.Read None Int32.MaxValue
                let offsets = page.Events |> List.map (fun e -> EventOffset.value e.Offset)
                Expect.equal offsets [ 0L .. int64 (List.length mixed - 1) ] "offsets are dense and monotonic across event kinds"
            }
    ]

let private acceptanceE2eTests =
    testList "Phase 2 acceptance E2E" [
        testCaseAsync "a disconnected client catches up on environment and terminal events (E2E-8)" <|
            async {
                let devAgent : RunAgent =
                    fun _ capabilities _signal onChunk ->
                        async {
                            let! _ = capabilities.Terminals.Execute (CommandRequest.ofCommand "echo made progress")
                            onChunk { Text = "done" }
                            return AgentCompleted ("done", None)
                        }
                let m = Manager.create (Some devAgent) (Some hostSandboxFor) acceptancePort
                let! _ =
                    m.StartSession
                        { SessionLaunchRequest.SessionId = SessionId.create "catchup-session" |> expect }
                let managed = (m.Registered ()) |> List.head
                let signalUrl = managed.BootstrapUri + "signal"

                let! a = connectClient signalUrl (managed.Host.MintPeerToken ()) "ada" "Ada"
                let! b = connectClient signalUrl (managed.Host.MintPeerToken ()) "grace" "Grace"
                do! b.Runner.WaitFor (fun model -> not model.EventConsumer.IsCatchingUp)

                // Grace leaves; the agent works while she is away.
                do! b.Channel.Close ()
                do! b.Runner.WaitFor (fun model -> model.Connection = Reconnecting)

                do! compose a a.Hello.PeerId "do the work"
                a.Connection.SendDraft a.Hello.PeerId
                let caughtUp (model: ClientModel) =
                    (model.Conversation.Items |> List.exists (fun i -> i.Body = "done"))
                    && (match model.Environment with EnvironmentRunning _ -> true | _ -> false)
                    // The agent's command is a terminal BLOCK now, not a command-log entry:
                    // that retirement is the point of stage 3b, and the catch-up property is
                    // unchanged — a client that was away folds the block from the log by
                    // offset exactly as it folded the command entry.
                    && (model.Terminals.Terminals
                        |> List.exists (fun t ->
                            t.Blocks
                            |> List.exists (fun b ->
                                b.Command.Contains "made progress"
                                && b.Status = BlockFinished (CommandSucceeded 0))))
                do! a.Runner.WaitFor caughtUp

                // Grace reconnects and catches up on the mixed message + environment +
                // terminal events by offset.
                let! b = reconnectClient signalUrl b
                do! b.Runner.WaitFor caughtUp

                do! a.Channel.Close ()
                do! b.Channel.Close ()
                do! m.Stop ()
            }

        // The one case in this suite that needs a real daemon, so it carries the `Docker` tag
        // itself rather than the suite (everything above is cheap-tier). Absent a daemon the
        // run drops that capability and this reports one skip. Richer coverage lives in the
        // DockerIntegration suite.
        Tag.needs "Docker adapter smoke" [ Tag.Docker ] (fun () ->
            testCaseAsync "real container create/spawn/dispose" (async {
                let name = SessionId.value (SessionId.mint ())
                let spec = { EnvironmentSpec.defaults with Runtime = Container { ContainerSpec.defaults with Image = Some { Name = "alpine"; Tag = Some "3" } } }
                let createSandbox = Sandboxes.forBackend DockerBackend name spec |> expect
                match! createSandbox ((Sandboxes.policyFor DockerBackend (Sandboxes.limitsFor DockerBackend "linux") Map.empty Map.empty None None None [] EnvironmentSpec.defaults |> expect)) with
                | Error reason -> failwithf "docker sandbox failed: %s" reason
                | Ok sandbox ->
                    let! run, out, _ = runInSandbox sandbox "echo" [ "hello-from-docker" ] Map.empty None
                    Expect.equal run (SandboxExited 0) "docker exec succeeded"
                    Expect.isTrue (out.Contains "hello-from-docker") "docker exec streamed"
                    do! sandbox.Dispose ()
                    let! remaining = Sandboxes.DockerSandbox.countByLabel (sprintf "yession-session=%s" name)
                    Expect.equal remaining 0 "dispose removed the container"
            }))
    ]

// -----------------------------------------------------------------------------
// Durable event log: history survives a Session Process restart.
// -----------------------------------------------------------------------------

let private persistencePort = 8130

let private persistenceTests =
    testList "Durable event log" [
        testCaseAsync "a restarted session keeps its history and continues its offsets" <|
            async {
                let dir = "tests/Yession.Tests/out/.data"
                let path = sprintf "%s/persist-%d.events.jsonl" dir (int (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) % 100000)
                let sessionId = SessionId.create "persist-session" |> expect
                let makeLog (id: SessionId) = EventStore.openLog path id (fun () -> DateTimeOffset.UtcNow)

                // First life: a client drafts and sends a message.
                let m1 = Manager.createWith None None (Some makeLog) persistencePort
                let! _ = m1.StartSession { SessionLaunchRequest.SessionId = sessionId }
                let managed1 = (m1.Registered ()) |> List.head
                let! a = connectClient (managed1.BootstrapUri + "signal") (managed1.Host.MintPeerToken ()) "ada" "Ada"
                do! compose a a.Hello.PeerId "remember me"
                a.Connection.SendDraft a.Hello.PeerId
                do! a.Runner.WaitFor (fun model ->
                        model.Conversation.Items |> List.exists (fun i -> i.Body = "remember me"))
                // Snapshot AFTER the host has fully observed the disconnect: with the
                // deterministic teardown the peer's departure reliably lands as a
                // PeerLeft event, and the session-end signal fires only after the peer
                // pump (which appends it) completes — so `before` is the settled
                // first-life history, PeerLeft included.
                let ended = managed1.Host.WaitForNextSessionEnd ()
                do! a.Channel.Close ()
                do! ended
                let! before = managed1.Host.Log.Read None Int32.MaxValue
                do! m1.Stop ()

                // Second life: a fresh Manager + Process over the same file.
                let m2 = Manager.createWith None None (Some makeLog) (persistencePort + 1)
                let! _ = m2.StartSession { SessionLaunchRequest.SessionId = sessionId }
                let managed2 = (m2.Registered ()) |> List.head
                let! after = managed2.Host.Log.Read None Int32.MaxValue
                Expect.equal
                    (after.Events |> List.map (fun e -> e.Offset, e.Event))
                    (before.Events |> List.map (fun e -> e.Offset, e.Event))
                    "the reopened log replays the identical history"

                // A reconnecting client catches up on the persisted conversation, and
                // new appends continue the offset sequence.
                let! b = connectClient (managed2.BootstrapUri + "signal") (managed2.Host.MintPeerToken ()) "grace" "Grace"
                do! b.Runner.WaitFor (fun model ->
                        (model.Conversation.Items |> List.exists (fun i -> i.Body = "remember me"))
                        && not model.EventConsumer.IsCatchingUp)
                let! page = managed2.Host.Log.Read None Int32.MaxValue
                let offsets = page.Events |> List.map (fun e -> EventOffset.value e.Offset)
                Expect.equal offsets [ 0L .. int64 (List.length page.Events - 1) ] "offsets continue densely across the restart"

                do! b.Channel.Close ()
                do! m2.Stop ()
            }
    ]

let tests =
    testList "Phase2" [
        // Cheap tier: pure policy/parse, folds, host-sandbox child-process integration.
        promiseAwaitTests
        sandboxPolicyTests
        environmentProjectionTests
        environmentRecordingTests
        testEnvTests
        testWaitTests
        commandFoldTests
        acceptanceTests
        // Needs ports: everything that binds ports / spawns hosts over real WebRTC.
        Tag.needs "Session Manager launch" [ Tag.Ports; Tag.Native ] (fun () -> launchTests)
        Tag.needs "Lazy environment lifecycle" [ Tag.Ports; Tag.Native ] (fun () -> lazyLifecycleTests)
        Tag.needs "Command execution" [ Tag.Ports; Tag.Native ] (fun () -> commandTests)
        Tag.needs "Phase 2 acceptance E2E" [ Tag.Ports; Tag.Native ] (fun () -> acceptanceE2eTests)
        Tag.needs "Durable event log" [ Tag.Ports; Tag.Native ] (fun () -> persistenceTests)
    ]
