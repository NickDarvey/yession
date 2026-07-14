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

let private commandTests =
    testList "Command execution" [
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
                let registry = Authority.ContainerRegistry ()
                let sessionId = SessionId.create "cmd-int" |> expect
                let capabilities = Authority.grant registry (Backends.LocalProcessBackend.create ()) sessionId
                let log =
                    Yession.SessionProcess.InMemoryEventLog.create sessionId (fun () -> DateTimeOffset.UtcNow)
                let environment =
                    Yession.SessionProcess.SessionEnvironment.create log capabilities EnvironmentSpec.localProcess "env-cmd"
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

        testCaseAsync "an agent-run command reaches browser clients as a read-only log (E2E-3/E2E-4)" <|
            async {
                // The agent ensures an environment, runs a real command, and answers.
                let devAgent : RunAgent =
                    fun _ capabilities onChunk ->
                        async {
                            let! _ = capabilities.EnsureEnvironment "need to run a command"
                            let! result =
                                capabilities.ExecuteCommand (nodeCommand "cmd-e2e" "console.log('hello from the env')") ignore
                            match result with
                            | CommandSucceeded 0 ->
                                onChunk { Text = "ran it" }
                                return AgentCompleted "ran it"
                            | other -> return AgentFailed (sprintf "%A" other)
                        }
                let m = Manager.create (Some devAgent) (Some (Backends.LocalProcessBackend.create ())) commandPort
                let! _ =
                    m.StartSession
                        { SessionId = SessionId.create "cmd-e2e-session" |> expect
                          SessionToken = "cmd-token" }
                let managed = (m.Registered ()) |> List.head

                // Two clients: the sender, and a second browser that must see the same
                // command log purely through event pages.
                let! a = connectClient (managed.BootstrapUri + "signal") "cmd-token" "ada" "Ada"
                let! b = connectClient (managed.BootstrapUri + "signal") "cmd-token" "grace" "Grace"
                let draftId = DraftId.create "cmd-draft" |> expect
                a.Runner.Dispatch (user (StartDraftMsg draftId))
                a.Runner.Dispatch (user (editBody draftId (Text.insert 0 "run the thing") (a.Runner.Model ())))
                a.Connection.SendDraft draftId

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
                let html = View.render (b.Runner.Model ())
                Expect.isTrue (html.Contains "data-command-log") "the command log section renders"
                Expect.isTrue (html.Contains "data-command-status=\"succeeded:0\"") "the command status renders"
                Expect.isTrue (html.Contains "hello from the env") "the streamed output renders"
                Expect.isFalse (html.Contains "data-command-input") "no input surface exists — read-only by construction"

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
                          DraftId = None
                          QueueId = None
                          Author = HumanPeer ada
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

        testCaseAsync "a disconnected client catches up on environment and command events (E2E-8)" <|
            async {
                let devAgent : RunAgent =
                    fun _ capabilities onChunk ->
                        async {
                            let! _ = capabilities.EnsureEnvironment "work to do"
                            let! _ = capabilities.ExecuteCommand (nodeCommand "cmd-catchup" "console.log('made progress')") ignore
                            onChunk { Text = "done" }
                            return AgentCompleted "done"
                        }
                let m = Manager.create (Some devAgent) (Some (Backends.LocalProcessBackend.create ())) acceptancePort
                let! _ =
                    m.StartSession
                        { SessionId = SessionId.create "catchup-session" |> expect
                          SessionToken = "catchup-token" }
                let managed = (m.Registered ()) |> List.head
                let signalUrl = managed.BootstrapUri + "signal"

                let! a = connectClient signalUrl "catchup-token" "ada" "Ada"
                let! b = connectClient signalUrl "catchup-token" "grace" "Grace"
                do! b.Runner.WaitFor (fun model -> not model.EventConsumer.IsCatchingUp)

                // Grace leaves; the agent works while she is away.
                do! b.Channel.Close ()
                do! b.Runner.WaitFor (fun model -> model.Connection = Reconnecting)

                let draftId = DraftId.create "catchup-draft" |> expect
                a.Runner.Dispatch (user (StartDraftMsg draftId))
                a.Runner.Dispatch (user (editBody draftId (Text.insert 0 "do the work") (a.Runner.Model ())))
                a.Connection.SendDraft draftId
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

        testCaseAsync "Docker adapter smoke (runs where a daemon exists; reported skipped otherwise)" <|
            async {
                match! Backends.DockerBackend.daemonAvailable () with
                | false ->
                    // No daemon in this environment: the authority layer is verified
                    // engine-independently; this smoke runs wherever Docker exists.
                    ()
                | true ->
                    let registry = Authority.ContainerRegistry ()
                    let sessionId = SessionId.create "docker-smoke" |> expect
                    let capabilities = Authority.grant registry (Backends.DockerBackend.create ()) sessionId
                    match! capabilities.StartContainer EnvironmentSpec.localProcess with
                    | ContainerStartFailed reason -> failwithf "docker start failed: %s" reason
                    | ContainerStarted handle ->
                        let mutable output = ""
                        let! result =
                            capabilities.Execute
                                handle
                                { CommandId = CommandId.create "docker-echo" |> expect
                                  Executable = "echo"
                                  Arguments = [ "hello-from-docker" ]
                                  WorkingDirectory = None
                                  Environment = Map.empty
                                  Timeout = None }
                                (fun c -> output <- output + c.Text)
                        Expect.equal result (CommandSucceeded 0) "docker exec succeeded"
                        Expect.isTrue (output.Contains "hello-from-docker") "docker exec streamed"
                        match! capabilities.StopContainer handle with
                        | ContainerStopped -> ()
                        | ContainerStopFailed reason -> failwithf "docker stop failed: %s" reason
            }
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
                let! _ = m1.StartSession { SessionId = sessionId; SessionToken = "persist-token" }
                let managed1 = (m1.Registered ()) |> List.head
                let! a = connectClient (managed1.BootstrapUri + "signal") "persist-token" "ada" "Ada"
                let draftId = DraftId.create "persist-draft" |> expect
                a.Runner.Dispatch (user (StartDraftMsg draftId))
                a.Runner.Dispatch (user (editBody draftId (Text.insert 0 "remember me") (a.Runner.Model ())))
                a.Connection.SendDraft draftId
                do! a.Runner.WaitFor (fun model ->
                        model.Conversation.Items |> List.exists (fun i -> i.Body = "remember me"))
                let! before = managed1.Host.Log.Read None Int32.MaxValue
                do! a.Channel.Close ()
                do! m1.Stop ()

                // Second life: a fresh Manager + Process over the same file.
                let m2 = Manager.createWith None None (Some makeLog) (persistencePort + 1)
                let! _ = m2.StartSession { SessionId = sessionId; SessionToken = "persist-token" }
                let managed2 = (m2.Registered ()) |> List.head
                let! after = managed2.Host.Log.Read None Int32.MaxValue
                Expect.equal
                    (after.Events |> List.map (fun e -> e.Offset, e.Event))
                    (before.Events |> List.map (fun e -> e.Offset, e.Event))
                    "the reopened log replays the identical history"

                // A reconnecting client catches up on the persisted conversation, and
                // new appends continue the offset sequence.
                let! b = connectClient (managed2.BootstrapUri + "signal") "persist-token" "grace" "Grace"
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
        launchTests
        authorityTests
        lazyLifecycleTests
        commandTests
        acceptanceTests
        persistenceTests
    ]
