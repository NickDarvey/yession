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
open Yession.Client
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
                let request =
                    { SessionId = SessionId.create "managed-1" |> expect
                      SessionToken = "managed-1-token" }
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
                Expect.isTrue (html.Contains "<main id=\"app\"") "the served page is the client shell"
            }

        testCaseAsync "launching the same session twice is rejected" <|
            async {
                let m = manager.Value
                let request =
                    { SessionId = SessionId.create "managed-1" |> expect
                      SessionToken = "managed-1-token" }
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
                let! a = connectClient signalUrl "managed-1-token" "ada" "Ada"
                let! b = connectClient signalUrl "managed-1-token" "grace" "Grace"

                let draftId = DraftId.create "managed-draft" |> expect
                a.Runner.Dispatch (user (StartDraftMsg draftId))
                a.Runner.Dispatch (user (editBody draftId (Text.insert 0 "managed hello") (a.Runner.Model ())))
                do! b.Runner.WaitFor (fun model -> bodyOf draftId model = Some "managed hello")

                a.Connection.SendDraft draftId
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
// Step 11 — scoped environment capability: ownership + handle validation.
// -----------------------------------------------------------------------------

open Yession.Manager

let private echoExec : CommandRequest -> (CommandOutputChunk -> unit) -> Async<CommandResult> =
    fun command onChunk ->
        async {
            onChunk { CommandId = command.CommandId; Stream = Stdout; Text = "ok" }
            return CommandSucceeded 0
        }

let private commandRequest (id: string) : CommandRequest =
    { CommandId = CommandId.create id |> expect
      Executable = "echo"
      Arguments = [ "ok" ]
      WorkingDirectory = None
      Environment = Map.empty
      Timeout = None }

let private authorityTests =
    let sessionA = SessionId.create "session-a" |> expect
    let sessionB = SessionId.create "session-b" |> expect

    testList "Scoped environment capability" [
        testCaseAsync "StartContainer creates a session-owned container" <|
            async {
                let registry = Authority.ContainerRegistry ()
                let recorder = InMemoryBackend.Recorder ()
                let capsA = Authority.grant registry (InMemoryBackend.create recorder echoExec) sessionA
                match! capsA.StartContainer EnvironmentSpec.localProcess with
                | ContainerStarted handle ->
                    Expect.equal (registry.OwnerOf (ContainerHandle.containerId handle)) (Some sessionA)
                        "the container is owned by the starting session"
                    Expect.equal recorder.Started 1 "the backend started exactly one container"
                | ContainerStartFailed reason -> failwithf "start failed: %s" reason
            }

        testCaseAsync "Exec runs through a valid scoped handle, streaming output" <|
            async {
                let registry = Authority.ContainerRegistry ()
                let recorder = InMemoryBackend.Recorder ()
                let capsA = Authority.grant registry (InMemoryBackend.create recorder echoExec) sessionA
                let! started = capsA.StartContainer EnvironmentSpec.localProcess
                let handle = match started with ContainerStarted h -> h | r -> failwithf "start failed: %A" r
                let mutable chunks = []
                let! result = capsA.Execute handle (commandRequest "cmd-1") (fun c -> chunks <- c :: chunks)
                Expect.equal result (CommandSucceeded 0) "the command ran"
                Expect.equal (chunks |> List.map (fun c -> c.Text)) [ "ok" ] "output streamed"
                Expect.equal recorder.Executed 1 "the backend executed once"
            }

        testCaseAsync "a Process cannot exec in another session's container (E2E authority)" <|
            async {
                let registry = Authority.ContainerRegistry ()
                let recorder = InMemoryBackend.Recorder ()
                let backend = InMemoryBackend.create recorder echoExec
                let capsA = Authority.grant registry backend sessionA
                let capsB = Authority.grant registry backend sessionB
                let! started = capsA.StartContainer EnvironmentSpec.localProcess
                let handleA = match started with ContainerStarted h -> h | r -> failwithf "start failed: %A" r

                // Session B tries with A's handle verbatim, and with a re-minted handle
                // naming its own session but A's container. Both must be rejected
                // before the backend is reached.
                let! withStolenHandle = capsB.Execute handleA (commandRequest "cmd-2") ignore
                match withStolenHandle with
                | CommandExecutionFailed _ -> ()
                | other -> failwithf "expected rejection, got %A" other

                let reminted = ContainerHandle.create sessionB (ContainerHandle.containerId handleA)
                let! withRemintedHandle = capsB.Execute reminted (commandRequest "cmd-3") ignore
                match withRemintedHandle with
                | CommandExecutionFailed _ -> ()
                | other -> failwithf "expected rejection, got %A" other

                Expect.equal recorder.Executed 0 "the backend was never reached"
            }

        testCaseAsync "a Process cannot exec with a forged handle (E2E authority)" <|
            async {
                let registry = Authority.ContainerRegistry ()
                let recorder = InMemoryBackend.Recorder ()
                let capsA = Authority.grant registry (InMemoryBackend.create recorder echoExec) sessionA
                let forged = ContainerHandle.create sessionA "no-such-container"
                let! result = capsA.Execute forged (commandRequest "cmd-4") ignore
                match result with
                | CommandExecutionFailed _ -> ()
                | other -> failwithf "expected rejection, got %A" other
                Expect.equal recorder.Executed 0 "the backend was never reached"
            }

        testCaseAsync "Stop validates ownership too, and a stopped container cannot exec" <|
            async {
                let registry = Authority.ContainerRegistry ()
                let recorder = InMemoryBackend.Recorder ()
                let backend = InMemoryBackend.create recorder echoExec
                let capsA = Authority.grant registry backend sessionA
                let capsB = Authority.grant registry backend sessionB
                let! started = capsA.StartContainer EnvironmentSpec.localProcess
                let handleA = match started with ContainerStarted h -> h | r -> failwithf "start failed: %A" r

                match! capsB.StopContainer handleA with
                | ContainerStopFailed _ -> ()
                | other -> failwithf "expected stop rejection, got %A" other
                Expect.equal recorder.Stopped 0 "the backend never stopped anything for B"

                match! capsA.StopContainer handleA with
                | ContainerStopped -> ()
                | other -> failwithf "expected stop, got %A" other

                let! afterStop = capsA.Execute handleA (commandRequest "cmd-5") ignore
                match afterStop with
                | CommandExecutionFailed reason ->
                    Expect.isTrue (reason.Contains "not running") "rejected because stopped"
                | other -> failwithf "expected rejection, got %A" other
            }
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

let private lazyLifecycleTests =
    testList "Lazy environment lifecycle" [
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

        testCaseAsync "a conversational one-shot does not start an environment (E2E-1)" <|
            async {
                let recorder = InMemoryBackend.Recorder ()
                let backend = InMemoryBackend.create recorder echoExec
                // A conversational agent: answers from context, never signals need.
                let conversational : RunAgent =
                    fun _ _ onChunk ->
                        async {
                            onChunk { Text = "just an answer" }
                            return AgentCompleted "just an answer"
                        }
                let m = Manager.create (Some conversational) (Some backend) lazyEnvironmentPort
                let! _ =
                    m.StartSession
                        { SessionId = SessionId.create "lazy-1" |> expect
                          SessionToken = "lazy-token" }
                let managed = (m.Registered ()) |> List.head

                let! a = connectClient (managed.BootstrapUri + "signal") "lazy-token" "ada" "Ada"
                let draftId = DraftId.create "oneshot" |> expect
                a.Runner.Dispatch (user (StartDraftMsg draftId))
                a.Runner.Dispatch (user (editBody draftId (Text.insert 0 "what is a monad?") (a.Runner.Model ())))
                a.Connection.SendDraft draftId
                do! a.Runner.WaitFor (fun model ->
                        model.Conversation.Items |> List.exists (fun i -> i.Body = "just an answer"))

                Expect.equal recorder.Started 0 "no container started for a one-shot"
                let! envEvents = environmentEventsOf managed.Host.Log
                Expect.isEmpty envEvents "no environment events for a one-shot"

                do! a.Channel.Close ()
                do! m.Stop ()
            }

        testCaseAsync "a development task identifies need and starts the environment (E2E-2)" <|
            async {
                let recorder = InMemoryBackend.Recorder ()
                let backend = InMemoryBackend.create recorder echoExec
                // A task agent: signals need through the typed capability, twice — the
                // second need must reuse the running environment.
                let taskAgent : RunAgent =
                    fun _ capabilities onChunk ->
                        async {
                            let! first = capabilities.EnsureEnvironment "need to inspect the repository"
                            let! second = capabilities.EnsureEnvironment "and to run the tests"
                            match first, second with
                            | EnvironmentAvailable, EnvironmentAvailable ->
                                onChunk { Text = "environment is up" }
                                return AgentCompleted "environment is up"
                            | other -> return AgentFailed (sprintf "%A" other)
                        }
                let m = Manager.create (Some taskAgent) (Some backend) (lazyEnvironmentPort + 1)
                let! _ =
                    m.StartSession
                        { SessionId = SessionId.create "lazy-2" |> expect
                          SessionToken = "lazy-token" }
                let managed = (m.Registered ()) |> List.head

                let! a = connectClient (managed.BootstrapUri + "signal") "lazy-token" "ada" "Ada"
                let draftId = DraftId.create "devtask" |> expect
                a.Runner.Dispatch (user (StartDraftMsg draftId))
                a.Runner.Dispatch (user (editBody draftId (Text.insert 0 "please run the tests") (a.Runner.Model ())))
                a.Connection.SendDraft draftId
                do! a.Runner.WaitFor (fun model ->
                        model.Conversation.Items |> List.exists (fun i -> i.Body = "environment is up")
                        && (match model.Environment with EnvironmentRunning _ -> true | _ -> false))

                Expect.equal recorder.Started 1 "exactly one container started across two needs"
                let! envEvents = environmentEventsOf managed.Host.Log
                Expect.equal
                    envEvents
                    [ "need"; "start-requested"; "started"; "need" ]
                    "need -> start -> started, then the second need reuses the environment"

                // The client's UI reflects the running environment from events alone.
                let html = View.render (a.Runner.Model ())
                Expect.isTrue (html.Contains "data-environment=\"running\"") "the environment status renders"

                do! a.Channel.Close ()
                do! m.Stop ()
            }

        testCaseAsync "a stopped environment is restarted by the next need, under the same id (E2E-7)" <|
            async {
                let recorder = InMemoryBackend.Recorder ()
                let backend = InMemoryBackend.create recorder echoExec
                let registry = Authority.ContainerRegistry ()
                let sessionId = SessionId.create "lazy-3" |> expect
                let capabilities = Authority.grant registry backend sessionId
                let log =
                    Yession.SessionProcess.InMemoryEventLog.create sessionId (fun () -> DateTimeOffset.UtcNow)
                let environment =
                    Yession.SessionProcess.SessionEnvironment.create log capabilities EnvironmentSpec.localProcess "env-lazy-3"

                let! first = environment.Ensure None "initial task"
                Expect.equal first EnvironmentAvailable "first ensure starts"
                do! environment.Stop ()
                Expect.equal (environment.CurrentHandle ()) None "stopped"
                let! second = environment.Ensure None "back for more"
                Expect.equal second EnvironmentAvailable "the next need restarts"
                Expect.equal recorder.Started 2 "two starts across the stop"

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

let tests =
    testList "Phase2" [
        launchTests
        authorityTests
        lazyLifecycleTests
    ]
