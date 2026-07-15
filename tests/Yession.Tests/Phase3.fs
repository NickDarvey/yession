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
open Yession.App
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
        fun _ _ _ _ -> Async.FromContinuations (fun (cont, _, _) -> pending <- Some cont)
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
                | AgentTurnInterrupted _ -> Some "interrupted"
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

let private interruptTests =
    testList "Interrupt (Step 17)" [
        testCaseAsync "interrupt keeps the partial response, is the turn's terminal event, and drains the waiting queue immediately" <|
            async {
                // A runner that streams one chunk, then suspends until aborted or
                // released — the deterministic shape of a long-running live turn.
                let mutable pending : (AgentRunResult -> unit) option = None
                let runner : RunAgent =
                    fun _ _ signal onChunk ->
                        Async.FromContinuations (fun (cont, _, _) ->
                            onChunk { Text = "partial thoughts" }
                            let mutable resumed = false
                            let resume result =
                                if not resumed then
                                    resumed <- true
                                    cont result
                            pending <- Some resume
                            // A well-behaved runner returns promptly once aborted; the
                            // orchestrator must discard this result — the Interrupted
                            // event is already the terminal fact.
                            signal.OnAbort (fun () -> resume (AgentFailed "aborted mid-flight")))
                let release () =
                    match pending with
                    | Some resume ->
                        pending <- None
                        resume (AgentCompleted "second turn done")
                    | None -> failwith "no held turn to release"

                let! h = Host.startWith (Some runner) (SessionId.create "interrupt-e2e" |> expect) "interrupt-token" 0
                let signalUrl = sprintf "http://127.0.0.1:%d/signal" h.Port
                let! a = connectClient signalUrl "interrupt-token" "ada" "Ada"

                // First send: the turn starts and streams its partial response.
                let d1 = DraftId.create "d-1" |> expect
                a.Runner.Dispatch (user (StartDraftMsg d1))
                a.Runner.Dispatch (user (editBody d1 (Text.insert 0 "go research this") (a.Runner.Model ())))
                a.Connection.SendDraft d1
                do! a.Runner.WaitFor (fun m ->
                        m.Agent.ActiveTurn.IsSome
                        && (m.Conversation.Items |> List.exists (fun i -> i.Status = Streaming && i.Body = "partial thoughts")))
                let firstTurn = (a.Runner.Model ()).Agent.ActiveTurn.Value

                // A second message queues behind the running turn (Cursor default);
                // the ordered channel guarantees it reaches the Process before the
                // interrupt that follows.
                let d2 = DraftId.create "d-2" |> expect
                a.Runner.Dispatch (user (StartDraftMsg d2))
                a.Runner.Dispatch (user (editBody d2 (Text.insert 0 "queued behind") (a.Runner.Model ())))
                a.Connection.SendDraft d2

                // Interrupt: partial body kept (Interrupted status), and the queued
                // message drains immediately into a NEW turn.
                a.Connection.InterruptTurn firstTurn
                do! a.Runner.WaitFor (fun m ->
                        (m.Conversation.Items
                         |> List.exists (fun i -> i.Status = ConversationItemStatus.Interrupted && i.Body = "partial thoughts"))
                        && (m.Conversation.Items |> List.exists (fun i -> i.Body = "queued behind"))
                        && (match m.Agent.ActiveTurn with Some t -> t <> firstTurn | None -> false))

                release () // the successor turn completes normally
                do! a.Runner.WaitFor (fun m ->
                        m.Agent.ActiveTurn = None
                        && (m.Conversation.Items |> List.exists (fun i -> i.Body = "second turn done")))

                // The event stream: the interrupt is terminal, single-flight holds,
                // and the aborted runner's failure result was discarded.
                let! kinds = agentEventKinds h.Log
                Expect.equal
                    kinds
                    [ "started"; "interrupted"; "started"; "completed" ]
                    "interrupt terminates the first turn; the drain starts exactly one successor"
                expectSingleFlight kinds

                do! a.Channel.Close ()
                do! h.Stop ()
            }

        testCaseAsync "interrupting a turn that already finished is rejected (interrupt-vs-completion race)" <|
            async {
                let scripted : RunAgent = fun _ _ _ _ -> async { return AgentCompleted "instant" }
                let! h = Host.startWith (Some scripted) (SessionId.create "interrupt-late" |> expect) "late-token" 0
                let signalUrl = sprintf "http://127.0.0.1:%d/signal" h.Port
                let! a = connectClient signalUrl "late-token" "ada" "Ada"

                let d1 = DraftId.create "d-1" |> expect
                a.Runner.Dispatch (user (StartDraftMsg d1))
                a.Runner.Dispatch (user (editBody d1 (Text.insert 0 "quick one") (a.Runner.Model ())))
                a.Connection.SendDraft d1
                do! a.Runner.WaitFor (fun m ->
                        (m.Conversation.Items |> List.exists (fun i -> i.Body = "instant"))
                        && m.Agent.ActiveTurn = None)
                let! page = h.Log.Read None Int32.MaxValue
                let turnId =
                    page.Events
                    |> List.pick (fun e -> match e.Event with AgentTurnStarted t -> Some t.AgentTurnId | _ -> None)

                // A raw channel so the command RESPONSE is observable.
                let! channel = WebRtc.connect signalUrl
                do! channel.Send (
                        Control (PeerHello { PeerId = PeerId.create "raw" |> expect; DisplayName = "Raw"; Token = "late-token" }))
                let rec awaitAccepted () =
                    async {
                        match! channel.Receive () with
                        | Some (Control (PeerAccepted _)) -> return ()
                        | Some _ -> return! awaitAccepted ()
                        | None -> return failwith "channel closed before accept"
                    }
                do! awaitAccepted ()
                let requestId = RequestId.fresh ()
                do! channel.Send (Command (Request (requestId, InterruptAgentTurn turnId)))
                let rec awaitResponse () =
                    async {
                        match! channel.Receive () with
                        | Some (Command (Response (rid, result))) when rid = requestId -> return result
                        | Some _ -> return! awaitResponse ()
                        | None -> return failwith "channel closed before the response"
                    }
                match! awaitResponse () with
                | CommandRejected reason -> Expect.isTrue (reason.Contains "finished") "rejected as already finished"
                | other -> failwithf "expected a rejection, got %A" other

                let! kinds = agentEventKinds h.Log
                Expect.equal kinds [ "started"; "completed" ] "no stray Interrupted event — history unchanged"
                do! channel.Close ()
                do! a.Channel.Close ()
                do! h.Stop ()
            }
    ]

// --- Step 19: process doc persistence (sidecar doc JSONL) -------------------------------

[<Fable.Core.ImportAll("node:fs")>]
let private nodeFs : obj = Fable.Core.Util.jsNative

[<Fable.Core.Emit("$0.appendFileSync($1, $2)")>]
let private appendFileSync (fs: obj) (path: string) (text: string) : unit = Fable.Core.Util.jsNative

[<Fable.Core.Emit("$0.readFileSync($1, 'utf8')")>]
let private readFileSync (fs: obj) (path: string) : string = Fable.Core.Util.jsNative

let private docPersistenceTests =
    let freshPaths (name: string) =
        let dir = "tests/Yession.Tests/out/.data"
        let stamp = int (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) % 1000000
        sprintf "%s/%s-%d.events.jsonl" dir name stamp, sprintf "%s/%s-%d.doc.jsonl" dir name stamp

    testList "Process doc persistence (Step 19)" [
        testCaseAsync "a restart replays the persisted doc: pending entries drain exactly once at boot; drafts survive and are not re-announced" <|
            async {
                let logPath, docPath = freshPaths "phase3-docstore"
                let sessionId = SessionId.create "phase3-docstore" |> expect
                let openLog () = EventStore.openLog logPath sessionId (fun () -> DateTimeOffset.UtcNow)

                // First life: a held turn consumes e1; e2 stays pending; a plain draft
                // is typed but never sent.
                let runner1, _release1 = heldAgent ()
                let! h1 = Host.startFull (Some runner1) None (Some (openLog ())) (Some (DocStore.openStore docPath)) sessionId "t" 0
                let o = offlinePeer 21.0 "olive" "Olive"
                let q1 = enqueue o "d-1" "q-1" "consumed before crash"
                deliver o.Doc h1.Doc
                let q2 = enqueue o "d-2" "q-2" "pending at crash"
                deliver o.Doc h1.Doc
                let keptDraft = DraftId.create "d-keep" |> expect
                o.Runner.Dispatch (user (StartDraftMsg keptDraft))
                o.Runner.Dispatch (user (editBody keptDraft (Text.insert 0 "durable words") (o.Runner.Model ())))
                deliver o.Doc h1.Doc
                let! firstLife = sentMessages h1.Log
                Expect.equal (firstLife |> List.map (fun m -> m.QueueId)) [ Some q1 ] "only e1 consumed; e2 waits behind the held turn"
                do! h1.Stop () // crash: the held turn never finishes

                // Second life: replay doc + log. The boot drain consumes e2 exactly
                // once; the draft is intact and not announced again.
                let runner2, release2 = heldAgent ()
                let! h2 = Host.startFull (Some runner2) None (Some (openLog ())) (Some (DocStore.openStore docPath)) sessionId "t" 0
                let! secondLife = sentMessages h2.Log
                Expect.equal
                    (secondLife |> List.map (fun m -> m.QueueId, m.Body))
                    [ Some q1, "consumed before crash"; Some q2, "pending at crash" ]
                    "the pending entry survived the restart and drained exactly once at boot"
                Expect.isTrue (Map.isEmpty (hostQueue h2)) "the queue is empty after the boot drain"
                release2 ()

                let synced = SyncedStateSync.ofDoc h2.Doc |> Result.mapError (sprintf "%A") |> expect
                Expect.equal
                    (synced.Drafts |> Map.tryFind keptDraft |> Option.map (fun d -> Text.toString d.Body))
                    (Some "durable words")
                    "the unsent draft survived the restart"
                // A later doc update triggers the draft scan; the replayed draft must
                // not produce a second DraftStarted (knownDrafts seeds from the log).
                o.Runner.Dispatch (user (editBody keptDraft (Text.insert 13 "!") (o.Runner.Model ())))
                deliver o.Doc h2.Doc
                let! page = h2.Log.Read None Int32.MaxValue
                let announcements =
                    page.Events
                    |> List.filter (fun e ->
                        match e.Event with
                        | DraftStarted d -> d.DraftId = keptDraft
                        | _ -> false)
                Expect.equal (List.length announcements) 1 "DraftStarted appended exactly once across both lives"
                do! h2.Stop ()

                // Compaction: the second open collapsed the history to one snapshot
                // line (plus any updates appended after boot).
                let lineCount =
                    (readFileSync nodeFs docPath).Split '\n'
                    |> Array.filter (fun l -> l.Trim().Length > 0)
                    |> Array.length
                Expect.isTrue (lineCount <= 3) (sprintf "the store is compacted at open (found %d lines)" lineCount)
            }

        testCaseAsync "a torn final line in the doc store is dropped; the acknowledged state is intact" <|
            async {
                let logPath, docPath = freshPaths "phase3-torn"
                let sessionId = SessionId.create "phase3-torn" |> expect
                let openLog () = EventStore.openLog logPath sessionId (fun () -> DateTimeOffset.UtcNow)

                let! h1 = Host.startFull None None (Some (openLog ())) (Some (DocStore.openStore docPath)) sessionId "t" 0
                let o = offlinePeer 22.0 "olive" "Olive"
                let draftId = DraftId.create "d-1" |> expect
                o.Runner.Dispatch (user (StartDraftMsg draftId))
                o.Runner.Dispatch (user (editBody draftId (Text.insert 0 "acknowledged") (o.Runner.Model ())))
                deliver o.Doc h1.Doc
                do! h1.Stop ()

                // A crash tore the final append: an unparseable half-line, no newline.
                appendFileSync nodeFs docPath "////////"

                let! h2 = Host.startFull None None (Some (openLog ())) (Some (DocStore.openStore docPath)) sessionId "t" 0
                let synced = SyncedStateSync.ofDoc h2.Doc |> Result.mapError (sprintf "%A") |> expect
                Expect.equal
                    (synced.Drafts |> Map.tryFind draftId |> Option.map (fun d -> Text.toString d.Body))
                    (Some "acknowledged")
                    "every acknowledged update survived; only the torn tail was dropped"
                do! h2.Stop ()
            }

        testCaseAsync "real corruption (a torn line that is NOT the unacknowledged tail) fails loudly" <|
            async {
                let logPath, docPath = freshPaths "phase3-corrupt"
                let sessionId = SessionId.create "phase3-corrupt" |> expect
                let openLog () = EventStore.openLog logPath sessionId (fun () -> DateTimeOffset.UtcNow)

                let! h1 = Host.startFull None None (Some (openLog ())) (Some (DocStore.openStore docPath)) sessionId "t" 0
                do! h1.Stop ()
                // A garbage line WITH a trailing newline claims to be acknowledged:
                // that is corruption, and it must never be silently dropped.
                appendFileSync nodeFs docPath "////////\n"

                let mutable failedLoudly = false
                try
                    let store = DocStore.openStore docPath
                    store.ReplayInto (Y.Doc.Create ())
                with _ -> failedLoudly <- true
                Expect.isTrue failedLoudly "a corrupt acknowledged line fails the open"
            }
    ]

let tests =
    testList "Phase3" [
        raceTests
        crashRepairTests
        docPersistenceTests
        // Interrupt runs over real WebRTC clients: verify tier.
        Tag.verify interruptTests
    ]
