module Yession.Tests.CommandGates

// The approval gate for commands (Plan 15, stage 3b). What is worth pinning is what makes
// this ONE mechanism with the terminal's rather than a second one beside it:
//
//   * an ungated command is the call and nothing else — no entry, no event, no wait,
//     which is what "today's behaviour, unchanged" has to mean;
//   * a gated one is VISIBLE before it happens, and refusable by anybody who can see it;
//   * a refusal is recorded and attributed, because a decision that vanishes reads as a
//     bug — the same reason `TerminalCommandRejected` exists;
//   * an approval reaches the command's OWN event, so the approver stays attached to the
//     act they released.

open System
open Fable.Pyxpecto
open Yjs
open Yession.Domain
open Yession.SessionProcess

let private expect result =
    match result with
    | Ok v -> v
    | Error e -> failwithf "invariant: %A" e

let private sessionId = SessionId.create "sess-gates" |> expect
let private ada = PeerId.create "ada" |> expect
let private fixedClock () = DateTimeOffset (2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
let private newLog () : EventLog<SessionEvent> = InMemoryEventLog.create sessionId fixedClock

/// A monotonic clock, for the one case that is about a DEADLINE rather than a verdict:
/// the yield needs time to have passed, and a fixed clock never gets there.
let private movingClock () =
    let mutable ticks = 0.0
    fun () ->
        ticks <- ticks + 1.0
        DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds ticks

let private gateOver (doc: Y.Doc) (log: EventLog<SessionEvent>) (now: unit -> DateTimeOffset) =
    let mutable n = 0
    let mint (prefix: string) () =
        n <- n + 1
        sprintf "%s-%d" prefix n
    CommandGates.create
        doc
        (fun () -> SyncedStateSync.ofDoc doc)
        (fun actor event ->
            async {
                let! _ = log.Append actor event
                return ()
            })
        (fun () -> QueueId.create (mint "q" ()) |> expect)
        (fun () -> MessageId.create (mint "msg" ()) |> expect)
        now
        // No change feed in these tests: the wait's 100ms tick is the floor, and every
        // case here either resolves before it waits or is about the deadline.
        (fun _ -> ignore)

let private eventsOf (log: EventLog<SessionEvent>) : Async<SessionEvent list> =
    async {
        let! page = log.Read None 1000
        return page.Events |> List.map (fun e -> e.Event)
    }

let private call (tool: string) (summary: string) : GatedCall =
    { Tool = tool; Summary = summary; Author = ActorRef.Agent }

/// A peer's verdict, landing in the doc the way a merged update would. The gate reads the
/// same registers the terminal drain does, so a test drives them the same way.
[<Fable.Core.Emit("(() => { const e = $0.getMap('pending').get($1); if (e) e.set($2, $3) })()")>]
let private setPendingField (doc: Y.Doc) (id: string) (field: string) (value: string) : unit =
    Fable.Core.Util.jsNative

let private gateTests =
    testList "The command gate" [

        testCaseAsync "an ungated command is the call and nothing else" <|
            async {
                let doc = Y.Doc.Create ()
                let log = newLog ()
                let gate = gateOver doc log fixedClock
                let mutable ran = 0
                let! outcome =
                    gate.Run (call "add_repo" "add_repo octo/hello") (fun approvedBy ->
                        async {
                            ran <- ran + 1
                            Expect.equal approvedBy None "nobody had to approve it"
                            return Ok "added octo/hello"
                        })
                let outcome = expect outcome
                Expect.equal ran 1 "it ran"
                Expect.equal outcome.Status (CommandRan "added octo/hello") "and answered with what it said"
                // No handle, because there is nothing to resume — offering one for a thing
                // that already finished is an invitation to poll it.
                Expect.equal outcome.Handle None "no handle"
                let synced = SyncedStateSync.ofDoc doc |> expect
                Expect.isTrue (Map.isEmpty synced.Pending) "nothing was ever pending"
                let! events = eventsOf log
                Expect.isEmpty events "and nothing was recorded by the gate itself"
            }

        testCaseAsync "a gated command is visible BEFORE it happens, and does not happen unasked" <|
            async {
                let doc = Y.Doc.Create ()
                let log = newLog ()
                SyncedStateSync.setGate doc (ForCommand "add_repo") ApproveAgent
                let gate = gateOver doc log (movingClock ())
                let mutable ran = 0
                let! outcome =
                    gate.Run (call "add_repo" "add_repo octo/hello") (fun _ ->
                        async {
                            ran <- ran + 1
                            return Ok "added"
                        })
                let outcome = expect outcome
                Expect.equal ran 0 "it has NOT run"
                Expect.equal outcome.Status CommandAwaitingApproval "and says so, rather than failing"
                Expect.isTrue (Option.isSome outcome.Handle) "with a handle to resume it by"
                let synced = SyncedStateSync.ofDoc doc |> expect
                match synced.Pending |> Map.toList with
                | [ (_, act) ] ->
                    Expect.equal act.Subject (ForCommand "add_repo") "the act names the command it is about"
                    Expect.equal
                        act.Payload
                        (CommandCall ("add_repo", "add_repo octo/hello"))
                        "and carries what a human should read"
                    Expect.equal act.Author ActorRef.Agent "attributed to whoever asked"
                    Expect.equal act.ApprovedBy None "never pre-approved — that would be the agent approving itself"
                | other -> failwithf "expected one pending act, got %A" other
            }

        testCaseAsync "an approval releases it, and reaches the command's own event" <|
            async {
                let doc = Y.Doc.Create ()
                let log = newLog ()
                SyncedStateSync.setGate doc (ForCommand "add_repo") ApproveAgent
                let gate = gateOver doc log (movingClock ())
                let mutable approver = None
                let! parked =
                    gate.Run (call "add_repo" "add_repo octo/hello") (fun approvedBy ->
                        async {
                            approver <- approvedBy
                            return Ok "added octo/hello"
                        })
                let handle = (expect parked).Handle |> Option.get
                setPendingField doc (QueueId.value handle) "approvedBy" (PeerId.value ada)
                // The watcher is detached, so the act completes whether or not anybody is
                // still waiting on it — which is the whole point: a human pressing approve
                // must not need an agent turn to be alive.
                do! Async.Sleep 250
                Expect.equal approver (Some (PeerRef ada)) "the command's own event can name who approved it"
                let synced = SyncedStateSync.ofDoc doc |> expect
                Expect.isTrue (Map.isEmpty synced.Pending) "and the card is gone once the verdict is in"
                let! resumed = gate.Read handle
                match resumed with
                | Ok outcome -> Expect.equal outcome.Status (CommandRan "added octo/hello") "the handle resumes to the outcome"
                | Error e -> failwithf "expected the handle to resolve, got %s" e
            }

        testCaseAsync "a refusal is recorded, attributed, and told to the model as a refusal" <|
            async {
                let doc = Y.Doc.Create ()
                let log = newLog ()
                SyncedStateSync.setGate doc (ForCommand "add_repo") ApproveAgent
                let gate = gateOver doc log (movingClock ())
                let mutable ran = 0
                let! parked =
                    gate.Run (call "add_repo" "add_repo octo/hello") (fun _ ->
                        async {
                            ran <- ran + 1
                            return Ok "added"
                        })
                let handle = (expect parked).Handle |> Option.get
                setPendingField doc (QueueId.value handle) "rejectedBy" (PeerId.value ada)
                setPendingField doc (QueueId.value handle) "rejectedReason" "wrong org"
                do! Async.Sleep 250
                Expect.equal ran 0 "the command never ran"
                let! events = eventsOf log
                match events with
                | [ SessionEvent.CommandRefused refusal ] ->
                    Expect.equal refusal.Tool "add_repo" "the tool"
                    Expect.equal refusal.Summary "add_repo octo/hello" "what was on the screen, not a re-rendering of it"
                    Expect.equal refusal.RejectedBy (PeerRef ada) "who said no"
                    Expect.equal refusal.Reason (Some "wrong org") "and why"
                    Expect.equal refusal.Author ActorRef.Agent "with who had asked"
                | other -> failwithf "expected one refusal, got %A" other
                let! resumed = gate.Read handle
                match resumed with
                | Ok outcome ->
                    Expect.equal
                        outcome.Status
                        (CommandRefusedBy (PeerRef ada, Some "wrong org"))
                        "and the model is told a decision, not a malfunction"
                | Error e -> failwithf "expected the handle to resolve, got %s" e
            }

        testCaseAsync "a refusal outranks an approval, exactly as it does in the terminal drain" <|
            async {
                let doc = Y.Doc.Create ()
                let log = newLog ()
                SyncedStateSync.setGate doc (ForCommand "add_repo") ApproveAgent
                let gate = gateOver doc log (movingClock ())
                let mutable ran = 0
                let! parked =
                    gate.Run (call "add_repo" "add_repo octo/hello") (fun _ ->
                        async {
                            ran <- ran + 1
                            return Ok "added"
                        })
                let handle = (expect parked).Handle |> Option.get
                setPendingField doc (QueueId.value handle) "approvedBy" (PeerId.value ada)
                setPendingField doc (QueueId.value handle) "rejectedBy" (PeerId.value ada)
                do! Async.Sleep 250
                Expect.equal ran 0 "a policy that would have released it does not beat a person who said no"
            }

        testCaseAsync "a command left parked by a dead process is refused at boot, not left hanging" <|
            async {
                let doc = Y.Doc.Create ()
                let log = newLog ()
                SyncedStateSync.setGate doc (ForCommand "add_repo") ApproveAgent
                let gate = gateOver doc log (movingClock ())
                let! _ = gate.Run (call "add_repo" "add_repo octo/hello") (fun _ -> async { return Ok "added" })
                // A new process over the same doc: the continuation that would have carried
                // the act out is gone, so an approve button on that card could never do
                // anything.
                let recovered = newLog ()
                let mutable n = 0
                let! swept =
                    CommandGates.sweepAtBoot
                        doc
                        (fun actor event ->
                            async {
                                let! _ = recovered.Append actor event
                                return ()
                            })
                        (fun () ->
                            n <- n + 1
                            MessageId.create (sprintf "boot-%d" n) |> expect)
                Expect.equal swept 1 "the stranded act is dealt with"
                let synced = SyncedStateSync.ofDoc doc |> expect
                Expect.isTrue (Map.isEmpty synced.Pending) "the card goes"
                let! events = eventsOf recovered
                match events with
                | [ SessionEvent.CommandRefused refusal ] ->
                    Expect.equal refusal.RejectedBy ActorRef.System "attributed to the session, not to a person"
                    Expect.equal
                        refusal.Reason
                        (Some "the session restarted before anyone decided")
                        "and it says what actually happened"
                | other -> failwithf "expected one refusal, got %A" other
            }

        testCaseAsync "a queued TERMINAL command is not the gate's business" <|
            async {
                // The two share a map on purpose, and the sweep must not mistake one for the
                // other: a terminal command survives a restart, because the doc holds its
                // whole payload.
                let doc = Y.Doc.Create ()
                let log = newLog ()
                let terminal = TerminalId.create "term-a" |> expect
                SyncedStateSync.enqueueTerminalCommand doc (QueueId.create "q-t1" |> expect) terminal ActorRef.Agent 1.0 "git status"
                let! swept =
                    CommandGates.sweepAtBoot
                        doc
                        (fun actor event ->
                            async {
                                let! _ = log.Append actor event
                                return ()
                            })
                        (fun () -> MessageId.create "boot-1" |> expect)
                Expect.equal swept 0 "nothing swept"
                let synced = SyncedStateSync.ofDoc doc |> expect
                Expect.equal (Map.count synced.Pending) 1 "and the terminal's entry is still there"
            }
    ]

let private configTests =
    testList "The operator's gate configuration" [

        testCase "an empty configuration gates nothing, which is the default" <| fun () ->
            Expect.isEmpty (CommandGates.parseConfiguredGates "") "empty"
            Expect.isEmpty (CommandGates.parseConfiguredGates "   ") "blank"

        testCase "a list is parsed however somebody plausibly wrote it" <| fun () ->
            Expect.equal
                (CommandGates.parseConfiguredGates "add_repo, start_work_sandbox")
                [ "add_repo"; "start_work_sandbox" ]
                "commas and spaces"
            Expect.equal
                (CommandGates.parseConfiguredGates "add_repo add_repo")
                [ "add_repo" ]
                "and naming one twice asks for it once"

        testCase "the configuration seeds the register, which is then the only place read" <| fun () ->
            let doc = Y.Doc.Create ()
            for tool in CommandGates.parseConfiguredGates "add_repo" do
                SyncedStateSync.setGate doc (ForCommand tool) ApproveAgent
            let synced = SyncedStateSync.ofDoc doc |> expect
            Expect.equal (SyncedSessionState.gateOf (ForCommand "add_repo") synced) ApproveAgent "the named one is gated"
            Expect.equal
                (SyncedSessionState.gateOf (ForCommand "switch_branch") synced)
                AutoRun
                "and a command nobody named keeps the default"
    ]

let tests = testList "Command gates (Plan 15, stage 3)" [ gateTests; configTests ]
