module Yession.Tests.CommandGates

// The gate for structured commands (Plan 15, stage 3b; Plan 23). Every command passes the
// CLASSIFIER on its way to the dispatch table. What is worth pinning:
//
//   * under the bypass classifier a command is the call and nothing else — no event, no
//     wait, which is what "the shipped behaviour" has to mean;
//   * a classifier's refusal is recorded and attributed, because a decision that vanishes
//     reads as a bug — the same reason `TerminalCommandRejected` exists;
//   * the classifier is asked about the ACT — who proposed it and what it says — because
//     that is the whole interface an AI-driven classifier will have;
//   * the deadline bounds the WORK, and a handle picks up what outlived it.

open System
open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Agent
open Yession.SessionProcess

let private expect result =
    match result with
    | Ok v -> v
    | Error e -> failwithf "invariant: %A" e

let private sessionId = SessionId.create "sess-gates" |> expect
let private ada' = UserRef (UserId.create "ada" |> expect)
let private fixedClock () = DateTimeOffset (2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
let private newLog () : EventLog<SessionEvent> = InMemoryEventLog.create sessionId fixedClock

/// A clock that leaps a minute per look, for the cases that are about a DEADLINE: the yield
/// needs time to have passed, and a fixed clock never gets there.
let private leapingClock () =
    let mutable ticks = 0.0
    fun () ->
        ticks <- ticks + 61.0
        DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds ticks

/// A gate over a classifier, with a dispatch table it can be told about after the fact —
/// the production arrangement, where the table is assembled a layer above the gate.
let private gateWith (classifier: Classifier) (log: EventLog<SessionEvent>) (now: unit -> DateTimeOffset) =
    let mutable n = 0
    let dispatch = ref Map.empty
    let mint (prefix: string) () =
        n <- n + 1
        sprintf "%s-%d" prefix n
    let gate =
        CommandGates.create
            classifier
            (fun () -> dispatch.Value)
            (fun actor event ->
                async {
                    let! _ = log.Append actor event
                    return ()
                })
            (fun () -> QueueId.create (mint "q" ()) |> expect)
            (fun () -> MessageId.create (mint "msg" ()) |> expect)
            now
            // No change feed in these tests: the wait's 100ms tick is what a waiter falls
            // back on, which is also the production guarantee for a dispatch that fails.
            (fun _ -> ignore)
    gate, dispatch

let private eventsOf (log: EventLog<SessionEvent>) : Async<SessionEvent list> =
    async {
        let! page = log.Read None 1000
        return page.Events |> List.map (fun e -> e.Event)
    }

let private call (tool: string) (args: string list) (summary: string) : GatedCall =
    { Tool = tool
      Args = Codec.toString Codec.gatedArgs args
      Summary = summary
      Authority = Authority.agentFor ada' }

/// A dispatch table of one command, recording what it was invoked with.
let private recordingDispatch (tool: string) =
    let seen = ResizeArray<GatedInvocation> ()
    let table : CommandDispatch =
        Map.ofList
            [ tool,
              fun (invocation: GatedInvocation) ->
                async {
                    seen.Add invocation
                    return Ok "done"
                } ]
    table, seen

let private gateTests =
    testList "The command gate" [

        testCaseAsync "under the bypass classifier a command runs, and answers with what it said" <|
            async {
                let log = newLog ()
                let gate, dispatch = gateWith Classifier.approveAll log fixedClock
                let table, seen = recordingDispatch "add_repo"
                dispatch.Value <- table
                let! outcome = gate.Run (call "add_repo" [ "octo/hello" ] "add_repo octo/hello")
                let outcome = expect outcome
                Expect.equal (Seq.length seen) 1 "it ran"
                Expect.equal
                    (Authority.effective (Seq.head seen).Authority)
                    ada'
                    "on the turn actor's credential"
                Expect.equal outcome.Status (CommandRan "done") "and answered with what it said"
                let! events = eventsOf log
                Expect.isEmpty events "and the gate itself recorded nothing"
            }

        testCaseAsync "a classifier's refusal is recorded, attributed, and the command never runs" <|
            async {
                let log = newLog ()
                let refusing : Classifier = fun _ _ -> async { return Rejected "not in this session" }
                let gate, dispatch = gateWith refusing log fixedClock
                let table, seen = recordingDispatch "add_repo"
                dispatch.Value <- table
                let! outcome = gate.Run (call "add_repo" [ "octo/hello" ] "add_repo octo/hello")
                let outcome = expect outcome
                Expect.equal (Seq.length seen) 0 "the dispatch was never invoked"
                Expect.equal
                    outcome.Status
                    (CommandRefusedBy (ActorRef.System, Some "not in this session"))
                    "the model is told REFUSED, with the reason"
                let! events = eventsOf log
                match events with
                | [ SessionEvent.CommandRefused refused ] ->
                    Expect.equal refused.Tool "add_repo" "the record names the tool"
                    Expect.equal refused.Summary "add_repo octo/hello" "and what a person would have read"
                    Expect.equal refused.Author ActorRef.Agent "and whose command it was"
                    Expect.equal refused.RejectedBy ActorRef.System "attributed to the session, not to a person"
                    Expect.equal refused.Reason (Some "not in this session") "with the classifier's reason"
                | other -> failwithf "expected one CommandRefused, got %A" other
            }

        testCaseAsync "the classifier is told who is asking and what the command says" <|
            async {
                let log = newLog ()
                let asked = ResizeArray<ActorRef * ProposedAct> ()
                let recording : Classifier =
                    fun author act ->
                        async {
                            asked.Add (author, act)
                            return Approved
                        }
                let gate, dispatch = gateWith recording log fixedClock
                let table, _ = recordingDispatch "add_repo"
                dispatch.Value <- table
                let! _ = gate.Run (call "add_repo" [ "octo/hello" ] "add_repo octo/hello")
                match List.ofSeq asked with
                | [ author, CommandAct (tool, _, summary) ] ->
                    Expect.equal author ActorRef.Agent "the author, not the credential it borrows"
                    Expect.equal tool "add_repo" "the tool"
                    Expect.equal summary "add_repo octo/hello" "and the summary a person would read"
                | other -> failwithf "expected one CommandAct question, got %A" other
            }

        testCaseAsync "a command this build does not have is refused, and says which" <|
            async {
                let log = newLog ()
                let gate, _ = gateWith Classifier.approveAll log fixedClock
                let! outcome = gate.Run (call "add_repos" [ "octo/hello" ] "add_repos octo/hello")
                let outcome = expect outcome
                match outcome.Status with
                | CommandRefusedBy (ActorRef.System, Some reason) ->
                    Expect.stringContains reason "add_repos" "the refusal names the tool"
                | other -> failwithf "expected a System refusal, got %A" other
                let! events = eventsOf log
                match events with
                | [ SessionEvent.CommandRefused refused ] ->
                    Expect.equal refused.RejectedBy ActorRef.System "recorded, so a rename shows up in the log"
                | other -> failwithf "expected one CommandRefused, got %A" other
            }

        testCaseAsync "a command that outlives its deadline yields a handle rather than holding the turn" <|
            async {
                let log = newLog ()
                let gate, dispatch = gateWith Classifier.approveAll log (leapingClock ())
                let mutable release = false
                dispatch.Value <-
                    Map.ofList
                        [ "add_repo",
                          fun (_: GatedInvocation) ->
                            async {
                                while not release do
                                    do! Async.Sleep 10
                                return Ok "done"
                            } ]
                let! outcome = gate.Run (call "add_repo" [ "octo/hello" ] "add_repo octo/hello")
                let outcome = expect outcome
                Expect.equal outcome.Status CommandRunning "going, not waiting on anybody"
                Expect.isTrue (Option.isSome outcome.Handle) "with the handle that picks it up"
                release <- true
            }

        testCaseAsync "the handle picks up what finished after the yield" <|
            async {
                let log = newLog ()
                let gate, dispatch = gateWith Classifier.approveAll log (leapingClock ())
                let mutable release = false
                dispatch.Value <-
                    Map.ofList
                        [ "add_repo",
                          fun (_: GatedInvocation) ->
                            async {
                                while not release do
                                    do! Async.Sleep 10
                                return Ok "done"
                            } ]
                let! outcome = gate.Run (call "add_repo" [ "octo/hello" ] "add_repo octo/hello")
                let outcome = expect outcome
                let handle =
                    match outcome.Handle with
                    | Some handle -> handle
                    | None -> failwith "expected a handle"
                release <- true
                // The work finishes; the handle resumes to the recorded outcome. A resume
                // may land while the work is still wrapping up, in which case it says so —
                // ask again, exactly as an agent would.
                let rec resume () =
                    async {
                        match! gate.Read handle with
                        | Ok read when read.Status = CommandRunning -> return! resume ()
                        | Ok read -> return read
                        | Error e -> return failwithf "the handle stopped answering: %s" e
                    }
                let! read = resume ()
                Expect.equal read.Status (CommandRan "done") "the same answer the call would have carried"
            }
    ]

let tests = testList "CommandGates" [ gateTests ]
