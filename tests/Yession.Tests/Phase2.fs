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
open Yession.App
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

        testCase "egress: only a confined backend carries an allowlist, and it is opt-in" <| fun () ->
            let ambient = Map.ofList [ "YESSION_WORK_DOMAINS", "api.example.com, cdn.example.com" ]
            Expect.equal (Sandboxes.egressFor HostBackend ambient) None "an unconfined backend is unrestricted"
            Expect.equal (Sandboxes.egressFor DockerBackend ambient) None "so is docker"
            Expect.equal
                (Sandboxes.egressFor SrtBackend ambient)
                (Some [ "api.example.com"; "cdn.example.com" ])
                "srt carries exactly the configured domains"
            Expect.equal
                (Sandboxes.egressFor SrtBackend Map.empty)
                (Some [])
                "and none where none were configured — srt has no unrestricted mode, so it fails closed"

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
                [ "/opt/tools"; "/data/workspace"; "/usr" ]
                "read paths, everything writable (a workspace that cannot be read is no workspace), and the host runtime"

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
            Expect.isTrue (List.contains Sandboxes.SrtSandbox.tmpDir config.AllowWrite) "and the temp dir srt redirects TMPDIR to"
            Expect.equal config.AllowedDomains [ "api.example.com" ] "the egress allowlist rides through"
            Expect.equal config.Bwrap (Some "/usr/bin/bwrap") "the named confinement tool rides through"
            Expect.equal config.Ripgrep (Some "/usr/bin/rg") "and so does the scanner srt will not start without"
            Expect.isFalse config.WeakNesting "the strict profile is what a configured host gets"

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

        testCase "the read scope's allow-back adds the operator's paths, it does not replace the platform's" <| fun () ->
            // The opposite of YESSION_*_DOMAINS, and deliberately: a deployment naming its
            // unusual toolchain still needs libc.
            let paths =
                Sandboxes.SrtSandbox.runtimeReadPaths
                    "linux"
                    [ "/opt/node22/bin/node" ]
                    (Map.ofList [ "YESSION_SANDBOX_READ_PATHS", "/srv/toolchain, /srv/sdk" ])
            Expect.isTrue (List.contains "/usr" paths) "the platform's runtime is still there"
            Expect.isTrue (List.contains "/opt/node22" paths) "so is what is already running"
            Expect.isTrue (List.contains "/srv/toolchain" paths) "and the operator's additions"
            Expect.isTrue (List.contains "/srv/sdk" paths) "all of them"

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
            Expect.isTrue
                (List.contains
                    "/home/operator"
                    (Sandboxes.SrtSandbox.runtimeReadPaths
                        "linux"
                        []
                        (Map.add "YESSION_SANDBOX_READ_PATHS" "/home/operator" ambient)))
                "an operator naming it explicitly means it"

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
                        [ "YESSION_BWRAP_PATH", " /nix/store/x/bin/bwrap "
                          "YESSION_SOCAT_PATH", ""
                          "YESSION_RIPGREP_PATH", "/nix/store/x/bin/rg" ])
                |> expect
            Expect.equal tools.Bwrap (Some "/nix/store/x/bin/bwrap") "a named tool is trimmed and used"
            Expect.equal tools.Ripgrep (Some "/nix/store/x/bin/rg") "every dependency is named, not left to PATH"
            Expect.equal tools.Socat None "a blank one is absent (darwin sets neither), not a path of empty string"
            Expect.equal tools.Nesting Sandboxes.StrictNesting "unconfigured means the strict profile"
            Expect.isTrue
                (List.contains "/usr" tools.Runtime)
                "and the host's own runtime is discovered, not left to a caller to remember"
            Expect.equal
                (Sandboxes.SrtSandbox.toolsFrom (Map.ofList [ "YESSION_SANDBOX_NESTED", "weak" ])
                 |> expect
                 |> fun t -> t.Nesting)
                Sandboxes.WeakNesting
                "an unprivileged container asks for the weaker profile explicitly"
            Expect.isError
                (Sandboxes.SrtSandbox.toolsFrom (Map.ofList [ "YESSION_SANDBOX_NESTED", "off" ]))
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
                (Sandboxes.AgentSandbox.domainsFrom (Map.ofList [ "YESSION_AGENT_DOMAINS", "gateway.internal" ]))
                [ "gateway.internal" ]
                "a deployment that fronts the API elsewhere replaces the list, it does not add to it"

        testCase "policy assembly: spec variables win over the baseline; docker takes no baseline" <| fun () ->
            let ambient = Map.ofList [ "PATH", "/usr/bin"; "HOME", "/home/u" ]
            let resolved = Map.ofList [ "HOME", "/workspace-home"; "TOKEN", "t" ]
            let host = Sandboxes.policyFor HostBackend ambient resolved (Some "/ws") (Some "/repos")
            Expect.equal (Map.tryFind "HOME" host.Env) (Some "/workspace-home") "the spec's variable wins"
            Expect.equal (Map.tryFind "PATH" host.Env) (Some "/usr/bin") "the baseline fills the rest"
            Expect.equal host.WorkingDirectory (Some "/ws") "the workspace is the default cwd"
            Expect.equal host.WritePaths [ "/ws"; "/repos" ] "the repos dir is a write path of its own (Plan 14)"
            let docker = Sandboxes.policyFor DockerBackend ambient resolved None None
            Expect.equal (Map.tryFind "PATH" docker.Env) None "a docker image supplies its own base env"
            Expect.equal (Map.tryFind "TOKEN" docker.Env) (Some "t") "only the spec's variables inject"
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
                            let! first = capabilities.ExecuteCommand (CommandRequest.ofCommand "true")
                            let! second = capabilities.ExecuteCommand (CommandRequest.ofCommand "true")
                            match first, second with
                            | Ok _, Ok _ ->
                                onChunk { Text = "environment is up" }
                                return AgentCompleted ("environment is up", None)
                            | other -> return AgentFailed (sprintf "%A" other)
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
                        (TerminalProjection.openTerminals model.Terminals |> List.length) = 2)

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
        (Sandboxes.preparePolicy HostBackend noSecrets None None EnvironmentSpec.defaults)
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
                            match! capabilities.ExecuteCommand (CommandRequest.ofCommand "echo hello from the env") with
                            | Ok outcome when outcome.Status = TerminalCommandRan (CommandSucceeded 0) ->
                                onChunk { Text = "ran it" }
                                return AgentCompleted ("ran it", None)
                            | other -> return AgentFailed (sprintf "%A" other)
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
                        { TerminalId = TerminalId.create "t1" |> expect; OpenedBy = ActorRef.Agent; Title = "agent"; Sandbox = Some SandboxName.defaultName; Renewable = false }
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
                            let! _ = capabilities.ExecuteCommand (CommandRequest.ofCommand "echo made progress")
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
                let spec = { EnvironmentSpec.defaults with Image = Some { Name = "alpine"; Tag = Some "3" } }
                let createSandbox = Sandboxes.forBackend DockerBackend name spec |> expect
                match! createSandbox (Sandboxes.policyFor DockerBackend Map.empty Map.empty None None) with
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
