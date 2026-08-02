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

[<Fable.Core.Emit("process.env[$0] = $1")>]
let private setEnv (name: string) (value: string) : unit = Fable.Core.Util.jsNative

[<Fable.Core.Emit("delete process.env[$0]")>]
let private unsetEnv (name: string) : unit = Fable.Core.Util.jsNative

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
                      "YESSION_CONTROL_SECRET", "launch-secret" ]
            let baseline = Sandboxes.hostBaseline ambient
            Expect.equal (Map.tryFind "PATH" baseline) (Some "/usr/bin") "PATH survives"
            Expect.equal (Map.tryFind "HOME" baseline) (Some "/home/u") "HOME survives"
            Expect.equal (Map.tryFind "ANTHROPIC_API_KEY" baseline) None "credentials do not"
            Expect.equal (Map.tryFind "CLAUDE_CODE_OAUTH_TOKEN" baseline) None "no credential survives"
            Expect.equal (Map.tryFind "YESSION_CONTROL_SECRET" baseline) None "the launch secret does not"

        testCase "the agent CLI's env: one credential, scratch HOME, never the raw process env" <| fun () ->
            let ambient =
                Map.ofList
                    [ "PATH", "/usr/bin"
                      "HOME", "/home/u"
                      "HTTPS_PROXY", "http://proxy:3128"
                      "ANTHROPIC_API_KEY", "ambient-key"
                      "CLAUDE_CODE_OAUTH_TOKEN", "ambient-token"
                      "YESSION_CONTROL_SECRET", "launch-secret" ]
            // A resolved per-turn credential displaces BOTH ambient credential vars.
            let resolved = Sandboxes.AgentSandbox.envFor ambient "/data/agent-home" (Some ("CLAUDE_CODE_OAUTH_TOKEN", "turn-token"))
            Expect.equal (Map.tryFind "CLAUDE_CODE_OAUTH_TOKEN" resolved) (Some "turn-token") "the turn's credential is set"
            Expect.equal (Map.tryFind "ANTHROPIC_API_KEY" resolved) None "the ambient key never rides along"
            Expect.equal (Map.tryFind "HOME" resolved) (Some "/data/agent-home") "the CLI gets the scratch HOME"
            Expect.equal (Map.tryFind "HTTPS_PROXY" resolved) (Some "http://proxy:3128") "proxy config passes through"
            Expect.equal (Map.tryFind "YESSION_CONTROL_SECRET" resolved) None "the launch secret never reaches the CLI"
            // The documented ambient last resort passes exactly the two credential vars.
            let ambientRun = Sandboxes.AgentSandbox.envFor ambient "/data/agent-home" None
            Expect.equal (Map.tryFind "ANTHROPIC_API_KEY" ambientRun) (Some "ambient-key") "the ambient key passes when nothing displaces it"
            Expect.equal (Map.tryFind "CLAUDE_CODE_OAUTH_TOKEN" ambientRun) (Some "ambient-token") "so does the ambient token"

        testCase "policy assembly: spec variables win over the baseline; docker takes no baseline" <| fun () ->
            let ambient = Map.ofList [ "PATH", "/usr/bin"; "HOME", "/home/u" ]
            let resolved = Map.ofList [ "HOME", "/workspace-home"; "TOKEN", "t" ]
            let host = Sandboxes.policyFor HostBackend ambient resolved (Some "/ws")
            Expect.equal (Map.tryFind "HOME" host.Env) (Some "/workspace-home") "the spec's variable wins"
            Expect.equal (Map.tryFind "PATH" host.Env) (Some "/usr/bin") "the baseline fills the rest"
            Expect.equal host.WorkingDirectory (Some "/ws") "the workspace is the default cwd"
            let docker = Sandboxes.policyFor DockerBackend ambient resolved None
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
                // A task agent: signals need through the typed capability, twice — the
                // second need must reuse the running environment.
                let taskAgent : RunAgent =
                    fun _ capabilities _signal onChunk ->
                        async {
                            let! first = capabilities.EnsureEnvironment "need to inspect the repository"
                            let! second = capabilities.EnsureEnvironment "and to run the tests"
                            match first, second with
                            | EnvironmentAvailable, EnvironmentAvailable ->
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

let private nodeCommand (id: string) (script: string) : CommandRequest =
    { CommandId = CommandId.create id |> expect
      Executable = "node"
      Arguments = [ "-e"; script ]
      WorkingDirectory = None
      Environment = Map.empty
      Timeout = None }

/// A real host-backend WorkSandbox composition over the given log — exactly what
/// SessionMain wires, minus the control channel (no secret refs here).
let private hostEnvironment (log: Yession.SessionProcess.EventLog<SessionEvent>) (name: string) =
    let createSandbox = Sandboxes.forBackend HostBackend name EnvironmentSpec.defaults |> expect
    let noSecrets = fun (n: SecretName) -> async { return Error (sprintf "no secrets: %s" (SecretName.value n)) }
    Yession.SessionProcess.SessionEnvironment.create
        log
        createSandbox
        (Sandboxes.preparePolicy HostBackend noSecrets None EnvironmentSpec.defaults)
        (Sandboxes.summaryFor HostBackend EnvironmentSpec.defaults)
        (sprintf "env-%s" name)

/// The per-session host sandbox for the in-process Manager compositions below.
let private hostSandboxFor (sessionId: SessionId) : CreateSandbox =
    Sandboxes.forBackend HostBackend (SessionId.value sessionId) EnvironmentSpec.defaults |> expect

// Pure fold + a local child-process integration — cheap tier, no ports.
let private commandFoldTests =
    testList "Command execution (local)" [
        testCase "command output ordering is preserved per command (interleaved commands)" <| fun () ->
            let idA = CommandId.create "cmd-a" |> expect
            let idB = CommandId.create "cmd-b" |> expect
            let events =
                [ CommandRequested { CommandId = idA; Executable = "a"; Arguments = [] }
                  CommandRequested { CommandId = idB; Executable = "b"; Arguments = [] }
                  CommandStarted { CommandId = idA }
                  CommandStarted { CommandId = idB }
                  CommandOutputReceived { CommandId = idA; Stream = Stdout; Text = "a1" }
                  CommandOutputReceived { CommandId = idB; Stream = Stdout; Text = "b1" }
                  CommandOutputReceived { CommandId = idA; Stream = Stderr; Text = "a2" }
                  CommandOutputReceived { CommandId = idA; Stream = Stdout; Text = "a3" }
                  CommandOutputReceived { CommandId = idB; Stream = Stdout; Text = "b2" }
                  CommandCompleted { CommandId = idA; Result = CommandSucceeded 0 }
                  CommandCompleted { CommandId = idB; Result = CommandFailed 2 } ]
            let log = events |> List.fold CommandLog.applyEvent CommandLog.empty
            let entry id = log.Entries |> List.find (fun e -> e.CommandId = id)
            Expect.equal
                ((entry idA).Output)
                [ Stdout, "a1"; Stderr, "a2"; Stdout, "a3" ]
                "command A's output, in order, uncontaminated by B"
            Expect.equal ((entry idB).Output) [ Stdout, "b1"; Stdout, "b2" ] "command B's output, in order"
            Expect.equal ((entry idA).Status) (CommandFinished (CommandSucceeded 0)) "A finished"
            Expect.equal ((entry idB).Status) (CommandFinished (CommandFailed 2)) "B failed with its exit code"
            // Determinism: re-folding the same events yields the same log.
            Expect.equal (events |> List.fold CommandLog.applyEvent CommandLog.empty) log "deterministic fold"

        testCaseAsync "a real command streams its output into the event log (integration)" <|
            async {
                let sessionId = SessionId.create "cmd-int" |> expect
                let log =
                    Yession.SessionProcess.InMemoryEventLog.create sessionId (fun () -> DateTimeOffset.UtcNow)
                let environment = hostEnvironment log "cmd-int"
                let! _ = environment.Ensure None "run a command"

                let! result =
                    environment.Execute (nodeCommand "cmd-real" "console.log('alpha'); console.error('warn'); console.log('beta')") ignore
                Expect.equal result (CommandSucceeded 0) "the command succeeded"

                let! page = log.Read None Int32.MaxValue
                let commandLog =
                    page.Events |> List.fold (fun l e -> CommandLog.applyEvent l e.Event) CommandLog.empty
                let entry = commandLog.Entries |> List.exactlyOne
                Expect.equal entry.Status (CommandFinished (CommandSucceeded 0)) "completed in the log"
                let textOf stream =
                    entry.Output
                    |> List.filter (fun (s, _) -> s = stream)
                    |> List.map snd
                    |> String.concat ""
                Expect.equal (textOf Stdout) "alpha\nbeta\n" "stdout streamed, in order"
                Expect.equal (textOf Stderr) "warn\n" "stderr streamed"

                // Exit codes and lifecycle ordering are events too.
                let! failed = environment.Execute (nodeCommand "cmd-fail" "process.exit(3)") ignore
                Expect.equal failed (CommandFailed 3) "non-zero exit is a value"
                let! after = log.Read None Int32.MaxValue
                let kinds =
                    after.Events
                    |> List.choose (fun e ->
                        match e.Event with
                        | CommandRequested c -> Some (CommandId.value c.CommandId, "requested")
                        | CommandStarted c -> Some (CommandId.value c.CommandId, "started")
                        | CommandCompleted c -> Some (CommandId.value c.CommandId, "completed")
                        | _ -> None)
                    |> List.filter (fun (id, _) -> id = "cmd-fail")
                    |> List.map snd
                Expect.equal kinds [ "requested"; "started"; "completed" ] "the lifecycle, in order"
            }

        testCaseAsync "a command past its timeout maps to CommandTimedOut and kills the process" <|
            async {
                // Timeout is ONE mechanism now — the session races the sandbox handle's
                // Exited — so this covers every backend.
                let recorder = SandboxRecorder ()
                let mutable killed = false
                let neverEnding : CreateSandbox =
                    fun _ ->
                        async {
                            return
                                Ok
                                    { Ref = "never"
                                      Spawn =
                                        fun _ _ ->
                                            async {
                                                return
                                                    Ok
                                                        { WriteStdin = ignore
                                                          CloseStdin = ignore
                                                          Kill = fun () -> killed <- true
                                                          Exited = Async.FromContinuations (fun _ -> ()) }
                                            }
                                      Dispose = fun () -> async { recorder.Disposed <- recorder.Disposed + 1 } }
                        }
                let sessionId = SessionId.create "cmd-timeout" |> expect
                let log =
                    Yession.SessionProcess.InMemoryEventLog.create sessionId (fun () -> DateTimeOffset.UtcNow)
                let environment =
                    Yession.SessionProcess.SessionEnvironment.create log neverEnding preparedEmptyPolicy "scripted" "env-timeout"
                let! _ = environment.Ensure None "hang"
                let! result =
                    environment.Execute
                        { nodeCommand "cmd-hang" "unused" with Timeout = Some (TimeSpan.FromMilliseconds 50.0) }
                        ignore
                Expect.equal result CommandTimedOut "the timeout fires before the command finishes"
                Expect.isTrue killed "the timed-out process is killed"
            }

        testCaseAsync "a host-sandbox command never inherits the session's credentials (leak regression)" <|
            async {
                // The old Manager-side spawn merged `process.env` into every command;
                // this plants a credential there and proves the seam keeps it out.
                setEnv "ANTHROPIC_API_KEY" "planted-credential"
                try
                    let sessionId = SessionId.create "cmd-leak" |> expect
                    let log =
                        Yession.SessionProcess.InMemoryEventLog.create sessionId (fun () -> DateTimeOffset.UtcNow)
                    let environment = hostEnvironment log "cmd-leak"
                    let! _ = environment.Ensure None "leak probe"
                    let mutable output = ""
                    let! result =
                        environment.Execute
                            (nodeCommand "cmd-leak-probe" "console.log('key=' + (process.env.ANTHROPIC_API_KEY || 'absent'))")
                            (fun c -> output <- output + c.Text)
                    Expect.equal result (CommandSucceeded 0) "the probe ran"
                    Expect.isTrue (output.Contains "key=absent") "the planted credential does not reach the command"
                finally
                    unsetEnv "ANTHROPIC_API_KEY"
            }
    ]

let private commandTests =
    testList "Command execution" [
        testCaseAsync "an agent-run command reaches browser clients as a read-only log (E2E-3/E2E-4)" <|
            async {
                // The agent ensures an environment, runs a real command, and answers.
                let devAgent : RunAgent =
                    fun _ capabilities _signal onChunk ->
                        async {
                            let! _ = capabilities.EnsureEnvironment "need to run a command"
                            let! result =
                                capabilities.ExecuteCommand (nodeCommand "cmd-e2e" "console.log('hello from the env')") ignore
                            match result with
                            | CommandSucceeded 0 ->
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
                // command log purely through event pages.
                let! a = connectClient (managed.BootstrapUri + "signal") (managed.Host.MintPeerToken ()) "ada" "Ada"
                let! b = connectClient (managed.BootstrapUri + "signal") (managed.Host.MintPeerToken ()) "grace" "Grace"
                do! compose a a.Hello.PeerId "run the thing"
                a.Connection.SendDraft a.Hello.PeerId

                let sawCommand (model: ClientModel) =
                    model.Commands.Entries
                    |> List.exists (fun e ->
                        e.Status = CommandFinished (CommandSucceeded 0)
                        && (e.Output |> List.exists (fun (_, text) -> text.Contains "hello from the env")))
                do! a.Runner.WaitFor sawCommand
                do! b.Runner.WaitFor sawCommand

                // E2E-3: the lifecycle events are in the log, in order.
                let! page = managed.Host.Log.Read None Int32.MaxValue
                let kinds =
                    page.Events
                    |> List.choose (fun e ->
                        match e.Event with
                        | CommandRequested _ -> Some "requested"
                        | CommandStarted _ -> Some "started"
                        | CommandOutputReceived _ -> Some "output"
                        | CommandCompleted _ -> Some "completed"
                        | _ -> None)
                Expect.equal kinds [ "requested"; "started"; "output"; "completed" ] "Started/OutputReceived/Completed appended"

                // E2E-4: the UI renders the read-only command log from events.
                let html = Support.render (b.Runner.Model ())
                Expect.isTrue (html.Contains Dom.Hooks.commandLog) "the command log section renders"
                Expect.isTrue (html.Contains (Dom.attr Dom.Hooks.commandStatus (Dom.Text.cmdSucceeded 0))) "the command status renders"
                Expect.isTrue (html.Contains "hello from the env") "the streamed output renders"
                Expect.isFalse (html.Contains Dom.Hooks.commandInput) "no input surface exists — read-only by construction"

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
        testCaseAsync "event offsets remain monotonic across message, agent, environment, and command events" <|
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
                          TriggeredByMessageId = MessageId.create "m1" |> expect }
                      EnvironmentNeedIdentified { Reason = "task"; AgentTurnId = None }
                      EnvironmentStarted { EnvironmentId = "env"; ContainerRef = "ctr" }
                      CommandRequested { CommandId = CommandId.create "c1" |> expect; Executable = "node"; Arguments = [] }
                      CommandOutputReceived { CommandId = CommandId.create "c1" |> expect; Stream = Stdout; Text = "x" }
                      CommandCompleted { CommandId = CommandId.create "c1" |> expect; Result = CommandSucceeded 0 } ]
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
        testCaseAsync "a disconnected client catches up on environment and command events (E2E-8)" <|
            async {
                let devAgent : RunAgent =
                    fun _ capabilities _signal onChunk ->
                        async {
                            let! _ = capabilities.EnsureEnvironment "work to do"
                            let! _ = capabilities.ExecuteCommand (nodeCommand "cmd-catchup" "console.log('made progress')") ignore
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
                    && (model.Commands.Entries
                        |> List.exists (fun e ->
                            e.Status = CommandFinished (CommandSucceeded 0)
                            && (e.Output |> List.exists (fun (_, t) -> t.Contains "made progress"))))
                do! a.Runner.WaitFor caughtUp

                // Grace reconnects and catches up on the mixed message + environment +
                // command events by offset.
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
                match! createSandbox (Sandboxes.policyFor DockerBackend Map.empty Map.empty None) with
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
        commandFoldTests
        acceptanceTests
        // Needs ports: everything that binds ports / spawns hosts over real WebRTC.
        Tag.needs "Session Manager launch" [ Tag.Ports; Tag.Native ] (fun () -> launchTests)
        Tag.needs "Lazy environment lifecycle" [ Tag.Ports; Tag.Native ] (fun () -> lazyLifecycleTests)
        Tag.needs "Command execution" [ Tag.Ports; Tag.Native ] (fun () -> commandTests)
        Tag.needs "Phase 2 acceptance E2E" [ Tag.Ports; Tag.Native ] (fun () -> acceptanceE2eTests)
        Tag.needs "Durable event log" [ Tag.Ports; Tag.Native ] (fun () -> persistenceTests)
    ]
