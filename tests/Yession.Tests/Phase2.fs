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

let tests =
    testList "Phase2" [
        launchTests
    ]
