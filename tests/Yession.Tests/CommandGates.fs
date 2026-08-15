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
let private ada' = UserRef (UserId.create "ada" |> expect)
let private fixedClock () = DateTimeOffset (2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
let private newLog () : EventLog<SessionEvent> = InMemoryEventLog.create sessionId fixedClock

/// A monotonic clock, for the one case that is about a DEADLINE rather than a verdict:
/// the yield needs time to have passed, and a fixed clock never gets there.
let private movingClock () =
    let mutable ticks = 0.0
    fun () ->
        ticks <- ticks + 1.0
        DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds ticks

/// A gate over a doc, with a dispatch table it can be told about after the fact — the
/// production arrangement, where the table is assembled a layer above the gate.
let private gateOver (doc: Y.Doc) (log: EventLog<SessionEvent>) (now: unit -> DateTimeOffset) =
    let mutable n = 0
    let dispatch = ref Map.empty
    let mint (prefix: string) () =
        n <- n + 1
        sprintf "%s-%d" prefix n
    let gate =
        CommandGates.create
            doc
            (fun () -> SyncedStateSync.ofDoc doc)
            (fun () -> dispatch.Value)
            (fun actor event ->
                async {
                    let! _ = log.Append actor event
                    return ()
                })
            (fun () -> QueueId.create (mint "q" ()) |> expect)
            (fun () -> MessageId.create (mint "msg" ()) |> expect)
            now
            // No change feed in these tests: the gate drains on `Run` and whenever a case
            // asks it to, which is what a doc update would have done.
            (fun _ -> ignore)
    gate, dispatch

let private eventsOf (log: EventLog<SessionEvent>) : Async<SessionEvent list> =
    async {
        let! page = log.Read None 1000
        return page.Events |> List.map (fun e -> e.Event)
    }

let private call (command: GatedCommand) (args: string list) (summary: string) : GatedCall =
    { Command = command
      Args = Codec.toString Codec.gatedArgs args
      Summary = summary
      Authority = Authority.agentFor ada' }

/// A dispatch table of one command, recording what it was invoked with.
let private recordingDispatch (command: GatedCommand) =
    let seen = ResizeArray<GatedInvocation> ()
    let table : CommandDispatch =
        Map.ofList
            [ command.Tool,
              fun (invocation: GatedInvocation) ->
                async {
                    seen.Add invocation
                    return Ok "done"
                } ]
    table, seen

/// A peer's verdict, landing in the doc the way a merged update would. The gate reads the
/// same registers the terminal drain does, so a test drives them the same way.
[<Fable.Core.Emit("(() => { const e = $0.getMap('pending').get($1); if (e) e.set($2, $3) })()")>]
let private setPendingField (doc: Y.Doc) (id: string) (field: string) (value: string) : unit =
    Fable.Core.Util.jsNative

let private gateTests =
    testList "The command gate" [

        testCaseAsync "an ungated command runs, and answers with what it said" <|
            async {
                let doc = Y.Doc.Create ()
                let log = newLog ()
                let gate, dispatch = gateOver doc log fixedClock
                let table, seen = recordingDispatch GatedCommands.addRepo
                dispatch.Value <- table
                let! outcome = gate.Run (call GatedCommands.addRepo [ "octo/hello" ] "add_repo octo/hello")
                let outcome = expect outcome
                Expect.equal (Seq.length seen) 1 "it ran"
                Expect.equal (Authority.approver (Seq.head seen).Authority) None "nobody had to approve it"
                Expect.equal
                    (Authority.effective (Seq.head seen).Authority)
                    ada'
                    "on the turn actor's credential"
                Expect.equal outcome.Status (CommandRan "done") "and answered with what it said"
                let synced = SyncedStateSync.ofDoc doc |> expect
                Expect.isTrue (Map.isEmpty synced.Pending) "nothing is left waiting"
                let! events = eventsOf log
                Expect.isEmpty events "and the gate itself recorded nothing"
            }

        testCaseAsync "a gated command is visible BEFORE it happens, and does not happen unasked" <|
            async {
                let doc = Y.Doc.Create ()
                let log = newLog ()
                SyncedStateSync.setGate doc (GatedCommands.subject GatedCommands.addRepo) ApproveAgent
                let gate, dispatch = gateOver doc log (movingClock ())
                let table, seen = recordingDispatch GatedCommands.addRepo
                dispatch.Value <- table
                let! outcome = gate.Run (call GatedCommands.addRepo [ "octo/hello" ] "add_repo octo/hello")
                let outcome = expect outcome
                Expect.equal (Seq.length seen) 0 "it has NOT run"
                Expect.equal outcome.Status CommandAwaitingApproval "and says so, rather than failing"
                Expect.isTrue (Option.isSome outcome.Handle) "with a handle to resume it by"
                let synced = SyncedStateSync.ofDoc doc |> expect
                match synced.Pending |> Map.toList with
                | [ (_, act) ] ->
                    Expect.equal act.Subject (ForCommand "add_repo") "the act names the command it is about"
                    Expect.equal
                        act.Payload
                        (CommandCall ("add_repo", Codec.toString Codec.gatedArgs [ "octo/hello" ], "add_repo octo/hello"))
                        "carrying both what a machine runs and what a human reads"
                    Expect.equal (Authority.author act.Authority) ActorRef.Agent "attributed to whoever asked"
                    Expect.equal (Authority.effective act.Authority) ada' "with whose credential it runs on"
                    Expect.equal act.ApprovedBy None "never pre-approved — that would be the agent approving itself"
                | other -> failwithf "expected one pending act, got %A" other
            }

        testCaseAsync "an approval releases it, and reaches the command's own dispatch" <|
            async {
                let doc = Y.Doc.Create ()
                let log = newLog ()
                SyncedStateSync.setGate doc (GatedCommands.subject GatedCommands.addRepo) ApproveAgent
                let gate, dispatch = gateOver doc log (movingClock ())
                let table, seen = recordingDispatch GatedCommands.addRepo
                dispatch.Value <- table
                let! parked = gate.Run (call GatedCommands.addRepo [ "octo/hello" ] "add_repo octo/hello")
                let handle = (expect parked).Handle |> Option.get
                setPendingField doc (QueueId.value handle) "approvedBy" (PeerId.value ada)
                // The drain is what carries it out, so the act completes whether or not
                // anybody is still waiting — a human pressing approve must not need an agent
                // turn to be alive.
                gate.Drain ()
                do! Async.Sleep 150
                Expect.equal (Seq.length seen) 1 "it ran once the verdict was in"
                Expect.equal
                    (Authority.approver (Seq.head seen).Authority)
                    (Some (PeerRef ada))
                    "and the event can name who released it"
                let synced = SyncedStateSync.ofDoc doc |> expect
                Expect.isTrue (Map.isEmpty synced.Pending) "the card goes once the verdict is in"
                let! resumed = gate.Read handle
                match resumed with
                | Ok outcome -> Expect.equal outcome.Status (CommandRan "done") "the handle resumes to the outcome"
                | Error e -> failwithf "expected the handle to resolve, got %s" e
            }

        testCaseAsync "a refusal is recorded, attributed, and told to the model as a refusal" <|
            async {
                let doc = Y.Doc.Create ()
                let log = newLog ()
                SyncedStateSync.setGate doc (GatedCommands.subject GatedCommands.addRepo) ApproveAgent
                let gate, dispatch = gateOver doc log (movingClock ())
                let table, seen = recordingDispatch GatedCommands.addRepo
                dispatch.Value <- table
                let! parked = gate.Run (call GatedCommands.addRepo [ "octo/hello" ] "add_repo octo/hello")
                let handle = (expect parked).Handle |> Option.get
                setPendingField doc (QueueId.value handle) "rejectedBy" (PeerId.value ada)
                setPendingField doc (QueueId.value handle) "rejectedReason" "wrong org"
                gate.Drain ()
                do! Async.Sleep 150
                Expect.equal (Seq.length seen) 0 "the command never ran"
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
                SyncedStateSync.setGate doc (GatedCommands.subject GatedCommands.addRepo) ApproveAgent
                let gate, dispatch = gateOver doc log (movingClock ())
                let table, seen = recordingDispatch GatedCommands.addRepo
                dispatch.Value <- table
                let! parked = gate.Run (call GatedCommands.addRepo [ "octo/hello" ] "add_repo octo/hello")
                let handle = (expect parked).Handle |> Option.get
                setPendingField doc (QueueId.value handle) "approvedBy" (PeerId.value ada)
                setPendingField doc (QueueId.value handle) "rejectedBy" (PeerId.value ada)
                gate.Drain ()
                do! Async.Sleep 150
                Expect.equal (Seq.length seen) 0 "a policy that would have released it does not beat a person who said no"
            }

        // The property this whole shape exists for: an approval given to a process that has
        // since died is still an approval. Everything needed to carry the act out is on the
        // act, so a NEW gate over the same doc honours it.
        testCaseAsync "an approval survives the process that proposed the act" <|
            async {
                let doc = Y.Doc.Create ()
                let log = newLog ()
                SyncedStateSync.setGate doc (GatedCommands.subject GatedCommands.addRepo) ApproveAgent
                let firstGate, firstDispatch = gateOver doc log (movingClock ())
                let firstTable, firstSeen = recordingDispatch GatedCommands.addRepo
                firstDispatch.Value <- firstTable
                let! parked = firstGate.Run (call GatedCommands.addRepo [ "octo/hello" ] "add_repo octo/hello")
                let handle = (expect parked).Handle |> Option.get
                // A human approves while nothing is running.
                setPendingField doc (QueueId.value handle) "approvedBy" (PeerId.value ada)

                // A new process over the same doc — a different gate, a different table.
                let restarted = newLog ()
                let gate, dispatch = gateOver doc restarted (movingClock ())
                let table, seen = recordingDispatch GatedCommands.addRepo
                dispatch.Value <- table
                gate.Drain ()
                do! Async.Sleep 150
                Expect.equal (Seq.length firstSeen) 0 "the dead process ran nothing"
                Expect.equal (Seq.length seen) 1 "the new one honoured the approval"
                let invocation = Seq.head seen
                Expect.equal
                    (Codec.fromString Codec.gatedArgs invocation.Args)
                    (Ok [ "octo/hello" ])
                    "with the arguments off the act, not out of a closure"
                Expect.equal
                    (Authority.effective invocation.Authority)
                    ada'
                    "and the credential owner off the act too"
                Expect.equal
                    (Authority.approver invocation.Authority)
                    (Some (PeerRef ada))
                    "and who released it"
                let synced = SyncedStateSync.ofDoc doc |> expect
                Expect.isTrue (Map.isEmpty synced.Pending) "the card is gone"
            }

        // The build changed under a parked act — a command was renamed or removed. A card
        // nobody can carry out is worse than a refusal, so it is refused with the reason.
        testCaseAsync "an act naming a command this build does not have is refused, not left" <|
            async {
                let doc = Y.Doc.Create ()
                let log = newLog ()
                SyncedStateSync.setGate doc (GatedCommands.subject GatedCommands.addRepo) ApproveAgent
                let gate, dispatch = gateOver doc log (movingClock ())
                dispatch.Value <- Map.empty
                let! parked = gate.Run (call GatedCommands.addRepo [ "octo/hello" ] "add_repo octo/hello")
                let handle = (expect parked).Handle |> Option.get
                setPendingField doc (QueueId.value handle) "approvedBy" (PeerId.value ada)
                gate.Drain ()
                do! Async.Sleep 150
                let! events = eventsOf log
                match events with
                | [ SessionEvent.CommandRefused refusal ] ->
                    Expect.equal refusal.RejectedBy ActorRef.System "attributed to the session, not to a person"
                    Expect.isTrue
                        ((refusal.Reason |> Option.defaultValue "").Contains "add_repo")
                        "and it names the command it could not run"
                | other -> failwithf "expected one refusal, got %A" other
            }

        testCaseAsync "a queued TERMINAL command is not the gate's business" <|
            async {
                // The two share a map on purpose, and the drain must not mistake one for the
                // other: a terminal command is the terminal drain's, and has no dispatch entry.
                let doc = Y.Doc.Create ()
                let log = newLog ()
                let terminal = TerminalId.create "term-a" |> expect
                SyncedStateSync.enqueueTerminalCommand
                    doc (QueueId.create "q-t1" |> expect) terminal (Authority.agentFor ada') 1.0 "git status" false
                let gate, _ = gateOver doc log (movingClock ())
                gate.Drain ()
                do! Async.Sleep 150
                let! events = eventsOf log
                Expect.isEmpty events "nothing recorded"
                let synced = SyncedStateSync.ofDoc doc |> expect
                Expect.equal (Map.count synced.Pending) 1 "and the terminal's entry is still there"
            }
    ]

let private configTests =
    testList "The operator's gate configuration" [

        testCase "an empty configuration gates nothing, which is the default" <| fun () ->
            Expect.equal (CommandGates.parseConfiguredGates "") (Ok []) "empty"
            Expect.equal (CommandGates.parseConfiguredGates "   ") (Ok []) "blank"

        testCase "a list is parsed however somebody plausibly wrote it" <| fun () ->
            Expect.equal
                (CommandGates.parseConfiguredGates "add_repo, start_work_sandbox")
                (Ok [ GatedCommands.addRepo; GatedCommands.startWorkSandbox ])
                "commas and spaces"
            Expect.equal
                (CommandGates.parseConfiguredGates "add_repo add_repo")
                (Ok [ GatedCommands.addRepo ])
                "and naming one twice asks for it once"

        // The failure this refuses is silent and one-directional: an operator believes
        // `add_repo` needs an approval and it does not. Skipping the name would read as
        // prudence and behave as a blind spot.
        testCase "a name that is not a gated command is refused, and the refusal says what is" <| fun () ->
            match CommandGates.parseConfiguredGates "add_repos" with
            | Ok _ -> failwith "a typo must not be accepted"
            | Error reason ->
                Expect.isTrue (reason.Contains "add_repos") "it names what it did not understand"
                Expect.isTrue (reason.Contains "add_repo") "and what it would have"

        testCase "the configuration seeds the register, which is then the only place read" <| fun () ->
            let doc = Y.Doc.Create ()
            match CommandGates.parseConfiguredGates "add_repo" with
            | Error e -> failwith e
            | Ok commands ->
                for command in commands do
                    SyncedStateSync.setGate doc (GatedCommands.subject command) ApproveAgent
            let synced = SyncedStateSync.ofDoc doc |> expect
            Expect.equal (SyncedSessionState.gateOf (ForCommand "add_repo") synced) ApproveAgent "the named one is gated"
            Expect.equal
                (SyncedSessionState.gateOf (ForCommand "switch_branch") synced)
                AutoRun
                "and a command nobody named keeps the default"
    ]

let private catalogueTests =
    testList "The gated-command catalogue" [

        // The catalogue is what makes the three consumers — the gated call sites, the boot
        // configuration and the settings control — read ONE list. A duplicate or an empty
        // name would be a second entry nobody can reach.
        testCase "every command is named once, and says what it is in prose" <| fun () ->
            let tools = GatedCommands.all |> List.map (fun c -> c.Tool)
            Expect.equal (List.distinct tools) tools "no command appears twice"
            for command in GatedCommands.all do
                Expect.notEqual command.Tool "" "a command has a tool name"
                Expect.notEqual command.Title "" "and a title a human can read"
                Expect.notEqual command.Title command.Tool "which is prose, not the tool name repeated"
                Expect.equal (GatedCommands.tryFind command.Tool) (Some command) "and is findable by it"

        testCase "a command nobody has configured reads as its default rather than as absent" <| fun () ->
            for command in GatedCommands.all do
                Expect.equal
                    (SyncedSessionState.gateOf (GatedCommands.subject command) SyncedSessionState.empty)
                    AutoRun
                    (sprintf "%s runs without asking until somebody says otherwise" command.Tool)
    ]

let tests = testList "Command gates (Plan 15, stage 3)" [ gateTests; configTests; catalogueTests ]
