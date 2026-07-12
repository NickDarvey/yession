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
                let m = Manager.create None basePort
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

let tests =
    testList "Phase2" [
        launchTests
        authorityTests
    ]
