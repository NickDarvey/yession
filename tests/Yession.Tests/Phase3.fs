module Yession.Tests.Phase3

// Phase 3 (Steps 15–16) verification: the collaborative message queue and the drain.
//
// The named races from docs/plans/01-turn-scheduling.md — delete-vs-accept and
// reorder-vs-accept in both orderings, edit-vs-accept, the crash-window repair, and
// drain liveness/single-flight — are pinned deterministically: peers are real client
// programs on their own Yjs docs, "delivery" is an explicit update application (so a
// peer can stay arbitrarily stale), and the agent is a hold-until-released runner, so
// every interleaving is scripted, not raced.

open System
open Fable.Pyxpecto
open Yjs
open Ylmish
open Yession.Domain
open Yession.SessionProcess
open Yession.Client
open Yession.Host
open Yession.Tests.Support

// An offline peer: a full client program on its own doc, never connected to a channel.
// Updates move only when a test explicitly delivers them.
type private OfflinePeer =
    { Runner : Harness.Runner<ClientModel, Ylmish.Program.Message<ClientModel, ClientMsg>>
      Doc : Y.Doc }

let private offlinePeer (clientId: float) (id: string) (name: string) : OfflinePeer =
    let doc = Y.Doc.Create ()
    // Pin the Yjs clientID so concurrent ties resolve the same way every run.
    doc.clientID <- clientId
    { Runner = Harness.run (App.makeProgram doc (ClientModel.init (peer id name)))
      Doc = doc }

/// Deliver `source`'s full state into `target` (idempotent, order-tolerant — exactly
/// the initial-exchange payload the transport uses).
let private deliver (source: Y.Doc) (target: Y.Doc) : unit =
    Y.applyUpdate (target, Y.encodeStateAsUpdate source)

let private enqueue (p: OfflinePeer) (draftKey: string) (queueKey: string) (text: string) : QueueId =
    let draftId = DraftId.create draftKey |> expect
    let queueId = QueueId.create queueKey |> expect
    p.Runner.Dispatch (user (StartDraftMsg draftId))
    p.Runner.Dispatch (user (editBody draftId (Text.insert 0 text) (p.Runner.Model ())))
    p.Runner.Dispatch (user (SendDraftMsg (draftId, queueId)))
    queueId

/// A `RunAgent` that suspends until the test releases it — the deterministic stand-in
/// for "a turn is running" in every race below. Re-armed per turn.
let private heldAgent () : RunAgent * (unit -> unit) =
    let mutable pending : (AgentRunResult -> unit) option = None
    let runner : RunAgent =
        fun _ _ _ -> Async.FromContinuations (fun (cont, _, _) -> pending <- Some cont)
    let release () =
        match pending with
        | Some cont ->
            pending <- None
            cont (AgentCompleted "held turn done")
        | None -> failwith "no held agent turn to release"
    runner, release

let private sentMessages (log: EventLog<SessionEvent>) : Async<MessageSent list> =
    async {
        let! page = log.Read None Int32.MaxValue
        return page.Events |> List.choose (fun e -> match e.Event with MessageSent m -> Some m | _ -> None)
    }

let private agentEventKinds (log: EventLog<SessionEvent>) : Async<string list> =
    async {
        let! page = log.Read None Int32.MaxValue
        return
            page.Events
            |> List.choose (fun e ->
                match e.Event with
                | AgentTurnStarted _ -> Some "started"
                | AgentMessageCompleted _ -> Some "completed"
                | AgentTurnFailed _ -> Some "failed"
                | _ -> None)
    }

/// Invariant 7: AgentTurnStarted events never overlap — a start only after the
/// previous turn's terminal event.
let private expectSingleFlight (kinds: string list) : unit =
    let mutable running = false
    for kind in kinds do
        match kind with
        | "started" ->
            Expect.isFalse running "no turn starts while another is running (single-flight)"
            running <- true
        | _ -> running <- false

let private hostQueue (h: Host.SessionHost) : Map<QueueId, QueuedMessage> =
    (SyncedStateSync.ofDoc h.Doc |> Result.mapError (sprintf "%A") |> expect).Queue

let private raceTests =
    testList "Queue races (drain is the linearization point)" [
        testCaseAsync "delete before the snapshot wins: a deleted entry is never consumed (delete-vs-accept, delete first)" <|
            async {
                let runner, release = heldAgent ()
                let! h = Host.startWith (Some runner) (SessionId.create "race-delete-first" |> expect) "t" 0
                let o = offlinePeer 11.0 "olive" "Olive"

                // m1 drains immediately and its turn HOLDS; m2 accumulates behind it.
                let q1 = enqueue o "d-1" "q-1" "first message"
                deliver o.Doc h.Doc
                let! afterFirst = sentMessages h.Log
                Expect.equal (afterFirst |> List.map (fun m -> m.QueueId)) [ Some q1 ] "m1 consumed, turn held"

                let q2 = enqueue o "d-2" "q-2" "doomed message"
                deliver o.Doc h.Doc
                let! whileHeld = sentMessages h.Log
                Expect.equal (List.length whileHeld) 1 "m2 waits while the turn runs (Cursor default)"

                // The delete reaches the Process before any snapshot could take m2.
                o.Runner.Dispatch (user (DeleteQueuedMsg q2))
                deliver o.Doc h.Doc
                release ()

                let! final = sentMessages h.Log
                Expect.equal (final |> List.map (fun m -> m.QueueId)) [ Some q1 ] "the deleted entry never became an event"
                Expect.isTrue (Map.isEmpty (hostQueue h)) "the process queue is empty"

                // The stale peer converges: entry gone, nothing resurrected.
                deliver h.Doc o.Doc
                Expect.equal (queueView (o.Runner.Model ())) [] "the peer's queue converges to empty"
                do! h.Stop ()
            }

        testCaseAsync "a late delete of an accepted entry is a CRDT no-op (delete-vs-accept, accept first)" <|
            async {
                let! h = Host.start (SessionId.create "race-accept-first" |> expect) "t" 0
                let o = offlinePeer 12.0 "olive" "Olive"

                let q1 = enqueue o "d-1" "q-1" "already accepted"
                deliver o.Doc h.Doc
                let! accepted = sentMessages h.Log
                Expect.equal (accepted |> List.map (fun m -> m.Body)) [ "already accepted" ] "consumed on arrival (idle, no agent)"

                // The peer — which has not yet seen the removal — deletes the entry.
                Expect.equal (queueView (o.Runner.Model ())) [ "q-1", "already accepted" ] "the stale peer still sees it"
                o.Runner.Dispatch (user (DeleteQueuedMsg q1))
                deliver o.Doc h.Doc

                let! final = sentMessages h.Log
                Expect.equal (final |> List.map (fun m -> m.Body)) [ "already accepted" ] "history is untouched — the entry is already an event"
                Expect.isTrue (Map.isEmpty (hostQueue h)) "deleting a removed key merges as a no-op"
                do! h.Stop ()
            }

        testCaseAsync "an edit that reaches the Process before the snapshot is consumed; a late edit is discarded (edit-vs-accept, both orderings)" <|
            async {
                let runner, release = heldAgent ()
                let! h = Host.startWith (Some runner) (SessionId.create "race-edit" |> expect) "t" 0
                let o = offlinePeer 13.0 "olive" "Olive"

                // Hold a turn on m1 so m2 sits in the queue.
                let _q1 = enqueue o "d-1" "q-1" "opening message"
                deliver o.Doc h.Doc

                let q2 = enqueue o "d-2" "q-2" "rough" // will be edited while queued
                deliver o.Doc h.Doc
                // Edit BEFORE the snapshot: reaches the Process while the turn still runs.
                o.Runner.Dispatch (user (editQueued q2 (Text.insert 5 " but improved") (o.Runner.Model ())))
                deliver o.Doc h.Doc
                release ()

                let! afterSecond = sentMessages h.Log
                Expect.equal
                    (afterSecond |> List.map (fun m -> m.Body))
                    [ "opening message"; "rough but improved" ]
                    "the consumed body is the snapshot including the pre-drain edit"

                // Edit AFTER acceptance: the stale peer types into the consumed entry.
                o.Runner.Dispatch (user (editQueued q2 (Text.insert 0 "TOO LATE ") (o.Runner.Model ())))
                deliver o.Doc h.Doc
                release () // second turn (for m2) finishes; drain finds nothing new

                let! final = sentMessages h.Log
                Expect.equal
                    (final |> List.map (fun m -> m.Body))
                    [ "opening message"; "rough but improved" ]
                    "late edits target a removed entry: discarded, never resurrected"
                Expect.isTrue (Map.isEmpty (hostQueue h)) "the process queue is empty"
                deliver h.Doc o.Doc
                Expect.equal (queueView (o.Runner.Model ())) [] "the editor's replica converges to empty"
                do! h.Stop ()
            }

        testCaseAsync "a reorder before the snapshot decides consumption order; the batch is one coalesced turn (reorder-vs-accept, reorder first + liveness + single-flight)" <|
            async {
                let runner, release = heldAgent ()
                let! h = Host.startWith (Some runner) (SessionId.create "race-reorder-first" |> expect) "t" 0
                let o = offlinePeer 14.0 "olive" "Olive"

                let _q1 = enqueue o "d-1" "q-1" "opening message"
                deliver o.Doc h.Doc // turn 1 holds

                let _q2 = enqueue o "d-2" "q-2" "second"
                let q3 = enqueue o "d-3" "q-3" "third"
                deliver o.Doc h.Doc
                // Move q3 above q2 while both wait — one fractional-index write.
                match QueueOrder.moveUp (o.Runner.Model ()).Synced.Queue q3 with
                | Some order -> o.Runner.Dispatch (user (ReorderQueuedMsg (q3, order)))
                | None -> failwith "q3 should be movable"
                deliver o.Doc h.Doc

                release () // turn 1 ends -> drain takes BOTH, in the reordered order
                let! messages = sentMessages h.Log
                Expect.equal
                    (messages |> List.map (fun m -> m.Body))
                    [ "opening message"; "third"; "second" ]
                    "the drain order is the (Order, QueueId) sort at the snapshot"

                release () // the coalesced turn for the batch
                let! kinds = agentEventKinds h.Log
                Expect.equal kinds [ "started"; "completed"; "started"; "completed" ] "one coalesced turn for the whole batch (liveness: nothing left behind)"
                expectSingleFlight kinds
                Expect.isTrue (Map.isEmpty (hostQueue h)) "the queue fully drained"
                do! h.Stop ()
            }

        testCaseAsync "a late reorder of an accepted entry is a no-op (reorder-vs-accept, accept first)" <|
            async {
                let! h = Host.start (SessionId.create "race-reorder-late" |> expect) "t" 0
                let o = offlinePeer 15.0 "olive" "Olive"

                let _q1 = enqueue o "d-1" "q-1" "first"
                let q2 = enqueue o "d-2" "q-2" "second"
                deliver o.Doc h.Doc
                let! accepted = sentMessages h.Log
                Expect.equal (accepted |> List.map (fun m -> m.Body)) [ "first"; "second" ] "both consumed in order"

                // The stale peer drags q2 to the front — but q2 is already history.
                match QueueOrder.moveUp (o.Runner.Model ()).Synced.Queue q2 with
                | Some order -> o.Runner.Dispatch (user (ReorderQueuedMsg (q2, order)))
                | None -> failwith "q2 should be movable on the stale replica"
                deliver o.Doc h.Doc

                let! final = sentMessages h.Log
                Expect.equal (final |> List.map (fun m -> m.Body)) [ "first"; "second" ] "the timeline order never changes after the terminal transition"
                Expect.isTrue (Map.isEmpty (hostQueue h)) "the late register write cannot resurrect the entry"
                do! h.Stop ()
            }
    ]

let private crashRepairTests =
    testList "Crash-window repair (log-anchored exactly-once)" [
        testCaseAsync "a consumed entry re-synced by a stale peer after restart is repaired out, never consumed twice" <|
            async {
                let dir = "tests/Yession.Tests/out/.data"
                let path = sprintf "%s/phase3-dedup-%d.events.jsonl" dir (int (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) % 100000)
                let sessionId = SessionId.create "phase3-dedup" |> expect
                let openLog () = EventStore.openLog path sessionId (fun () -> DateTimeOffset.UtcNow)

                // First life: the entry is consumed (appended durably, removed from the
                // process doc). The peer never receives the removal.
                let! h1 = Host.startWithCapabilities None None (Some (openLog ())) sessionId "t" 0
                let o = offlinePeer 16.0 "olive" "Olive"
                let q1 = enqueue o "d-1" "q-1" "exactly once"
                deliver o.Doc h1.Doc
                let! firstLife = sentMessages h1.Log
                Expect.equal (firstLife |> List.map (fun m -> m.QueueId)) [ Some q1 ] "consumed in the first life"
                do! h1.Stop ()

                // Second life: a fresh process doc (doc persistence arrives in Step 19),
                // the SAME durable log. The stale peer re-syncs the consumed entry —
                // exactly the crash-between-append-and-removal shape.
                let! h2 = Host.startWithCapabilities None None (Some (openLog ())) sessionId "t" 0
                deliver o.Doc h2.Doc

                let! secondLife = sentMessages h2.Log
                Expect.equal (secondLife |> List.map (fun m -> m.QueueId)) [ Some q1 ] "the log still holds exactly one MessageSent — dedup is log-anchored"
                Expect.isTrue (Map.isEmpty (hostQueue h2)) "the leftover entry is repaired out of the doc"

                deliver h2.Doc o.Doc
                Expect.equal (queueView (o.Runner.Model ())) [] "the stale peer converges to the repaired state"
                do! h2.Stop ()
            }
    ]

let tests =
    testList "Phase3" [
        raceTests
        crashRepairTests
    ]
