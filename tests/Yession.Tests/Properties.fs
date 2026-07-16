module Yession.Tests.Properties

// Step 18 — the Phase 3 concurrency contract as Hedgehog properties.
//
// Invariants 1–7 from docs/plans/01-turn-scheduling.md, each as one `property {}`
// block over generated N-peer schedules. The system under test is the REAL machinery:
// real client programs (withYlmish) on their own Yjs docs, and the real
// `Scheduler.create` drain over an in-memory event log — no WebRTC, no HTTP. Delivery
// is an explicit op, so peers can stay arbitrarily stale; the agent is a
// hold-until-released runner, so turn lifetimes are part of the schedule.
//
// The oracle is a SHADOW REPLICA: a doc that receives exactly the updates the process
// doc receives (peer payloads applied to it first, the process's own removal
// transactions relayed to it), but runs no scheduler. At every point that can drain,
// the batch the scheduler consumes must equal `QueueDrain.plan` over the shadow's
// decoded state — an independent prediction of snapshot content and order (invariants
// 2 and 5) that never duplicates CRDT logic.
//
// Determinism: the vendored Hedgehog draws from a fixed-seed PRNG and Yjs clientIDs
// are pinned per replica, so every run generates and executes identical schedules.

open Fable.Pyxpecto
open Hedgehog
open Yjs
open Ylmish
open Yession.Domain
open Yession.SessionProcess
open Yession.App
open Yession.Tests.Support

// --- The schedule vocabulary -----------------------------------------------------------

type private ScheduleOp =
    | Enqueue of peer: int
    | EditQueued of peer: int * pick: int
    | Reorder of peer: int * pick: int
    | DeleteQueued of peer: int * pick: int
    | DeliverToProcess of peer: int
    | DeliverToPeer of peer: int
    | TurnCompletes
    | Interrupt of peer: int
    | ProcessRestart

let private genOp : Gen<ScheduleOp> =
    gen {
        let! tag = Gen.int32 (Range.linear 0 11)
        let! p = Gen.int32 (Range.linear 0 1)
        let! pick = Gen.int32 (Range.linear 0 5)
        return
            match tag with
            | 0 | 1 | 2 -> Enqueue p
            | 3 -> EditQueued (p, pick)
            | 4 -> Reorder (p, pick)
            | 5 -> DeleteQueued (p, pick)
            | 6 | 7 -> DeliverToProcess p
            | 8 -> DeliverToPeer p
            | 9 -> TurnCompletes
            | 10 -> Interrupt p
            | _ -> ProcessRestart
    }

let private genSchedule : Gen<ScheduleOp list> =
    Gen.list (Range.linear 0 15) genOp

// --- The executed case: everything the invariants need ---------------------------------

type private DrainRecord =
    { Expected : (string * string) list   // (queueId, body) the shadow oracle predicts
      Actual : (string * string) list }   // (queueId, body) actually appended

type private CaseResult =
    { Events : SessionEvent list
      DrainRecords : DrainRecord list
      /// Event-list lengths at each process restart, for per-life segmentation.
      RestartMarks : int list
      EnqueuedIds : string list
      DeleteOpIds : string list
      FinalProcessQueue : (string * string) list
      FinalPeerQueues : (string * string) list list }

// --- The engine -------------------------------------------------------------------------

let private peerIdOf (i: int) : PeerId = PeerId.create (sprintf "peer-%d" i) |> expect

/// A fixed timestamp (Fable's library lacks DateTimeOffset.UnixEpoch).
let private epoch = System.DateTimeOffset (1970, 1, 1, 0, 0, 0, System.TimeSpan.Zero)

let private runSchedule (ops: ScheduleOp list) : CaseResult =
    // Peers: real client programs on pinned-clientID docs, never on a channel.
    let mkPeer (i: int) =
        let doc = Y.Doc.Create ()
        doc.clientID <- float (i + 1)
        let runner = Harness.run (App.makeProgram doc (ClientModel.init (peer (sprintf "peer-%d" i) (sprintf "Peer %d" i))))
        doc, runner
    let peers = [| mkPeer 0; mkPeer 1 |]
    let peerDoc i = fst peers.[i]
    let peerRunner i = snd peers.[i]

    // The recorded event stream: the single source for consumed-set, invariants, marks.
    let recorded = ResizeArray<SessionEvent> ()
    let sessionId = SessionId.create "property-session" |> expect
    let baseLog = InMemoryEventLog.create sessionId (fun () -> epoch)
    let log =
        { baseLog with
            Append =
                fun actor event ->
                    async {
                        let! appended = baseLog.Append actor event
                        recorded.Add event
                        return appended
                    } }
    let consumedNow () =
        recorded |> Seq.choose QueueDrain.consumedOf |> Set.ofSeq
    let sentAll () =
        recorded
        |> Seq.choose (fun e ->
            match e with
            | MessageSent m -> Some (m.QueueId |> Option.map QueueId.value |> Option.defaultValue "?", m.Body)
            | _ -> None)
        |> List.ofSeq

    // The hold-until-released agent. Epoch-guarded: a restart abandons the held turn
    // (the process died), so a later release must never resume it.
    let mutable epoch = 0
    let mutable pendingRelease : (unit -> unit) option = None
    let runner : RunAgent =
        fun _ _ signal onChunk ->
            Async.FromContinuations (fun (cont, _, _) ->
                let myEpoch = epoch
                let mutable resumed = false
                let resume result =
                    if not resumed && epoch = myEpoch then
                        resumed <- true
                        cont result
                onChunk { Text = "thinking" }
                pendingRelease <- Some (fun () -> resume (AgentCompleted "turn done"))
                signal.OnAbort (fun () -> resume (AgentFailed "aborted")))

    let mintTurnId =
        let mutable n = 0
        fun () ->
            n <- n + 1
            AgentTurnId.create (sprintf "turn-%d" n) |> expect
    let mintMessageId =
        let mutable n = 0
        fun () ->
            n <- n + 1
            MessageId.create (sprintf "message-%d" n) |> expect

    // The process side: doc + scheduler + the shadow-replica oracle, all rebuilt on
    // ProcessRestart. Restart recovers the persisted doc (Step 19): the sidecar store
    // replays the merged update history, which IS the crashed process's doc state —
    // modelled here by carrying that state into the fresh doc. The log is durable.
    let mutable processDoc = Unchecked.defaultof<Y.Doc>
    let mutable shadowDoc = Unchecked.defaultof<Y.Doc>
    let mutable scheduler = Unchecked.defaultof<Scheduler.SessionScheduler>

    let drainRecords = ResizeArray<DrainRecord> ()
    let restartMarks = ResizeArray<int> ()
    let enqueuedIds = ResizeArray<string> ()
    let deleteOpIds = ResizeArray<string> ()

    /// The oracle's prediction for the next drain, from the shadow replica.
    let expectedBatch () : (string * string) list =
        match SyncedStateSync.ofDoc shadowDoc with
        | Error _ -> []
        | Ok s ->
            (QueueDrain.plan (consumedNow ()) s.Queue).Batch
            |> List.map (fun m -> QueueId.value m.QueueId, Text.toString m.Body)

    /// Run an action that may drain, recording predicted vs actual consumption.
    let checkedDrain (expected: (string * string) list) (action: unit -> unit) =
        let before = List.length (sentAll ())
        action ()
        let actual = sentAll () |> List.skip before
        drainRecords.Add { Expected = expected; Actual = actual }

    let bootProcess (recovered: string option) =
        processDoc <- Y.Doc.Create ()
        processDoc.clientID <- 101.0
        shadowDoc <- Y.Doc.Create ()
        shadowDoc.clientID <- 102.0
        recovered
        |> Option.iter (fun payload ->
            DocSync.applyRemote processDoc payload
            DocSync.applyRemote shadowDoc payload)
        let sched =
            Scheduler.create sessionId processDoc log (Some runner)
                (fun _ -> AgentCapabilities.none) mintTurnId mintMessageId (consumedNow ())
        scheduler <- sched
        let thisProcess = processDoc
        let thisShadow = shadowDoc
        // The process's own writes (drain removals) reach the shadow like any peer's.
        DocSync.onLocalUpdate thisProcess (fun payload ->
            if System.Object.ReferenceEquals (thisProcess, processDoc) then
                DocSync.applyRemote thisShadow payload)
        // The while-idle trigger, exactly as the Host wires it.
        DocSync.onAnyUpdate thisProcess (fun () ->
            if System.Object.ReferenceEquals (thisProcess, processDoc) then sched.Drain ())
        // The boot drain (Step 19): recovered pending entries consume now; recovered
        // consumed-but-not-removed leftovers repair via the log-anchored dedup.
        checkedDrain (expectedBatch ()) sched.Drain
    bootProcess None

    let deliverToProcess (i: int) =
        let payload = DocSync.fullState (peerDoc i)
        // Shadow first: it now holds the process's post-delivery, pre-drain state.
        DocSync.applyRemote shadowDoc payload
        let expected = if (scheduler.RunningTurn ()).IsSome then [] else expectedBatch ()
        checkedDrain expected (fun () -> DocSync.applyRemote processDoc payload)

    let deliverToPeer (i: int) =
        Y.applyUpdate (peerDoc i, Y.encodeStateAsUpdate processDoc)

    let turnCompletes () =
        if (scheduler.RunningTurn ()).IsSome then
            match pendingRelease with
            | Some release ->
                pendingRelease <- None
                checkedDrain (expectedBatch ()) release
            | None -> ()

    let interrupt (i: int) =
        match scheduler.RunningTurn () with
        | Some turnId ->
            let expected = expectedBatch ()
            checkedDrain expected (fun () ->
                scheduler.RequestInterrupt (peerIdOf i) turnId |> ignore)
        | None -> ()

    let mutable enqueueCounter = 0
    let enqueue (i: int) =
        enqueueCounter <- enqueueCounter + 1
        let queueId = QueueId.create (sprintf "queue-%d" enqueueCounter) |> expect
        let r = peerRunner i
        // Peer i writes its own slot (keyed by its peer id) and sends: send clears the
        // slot, so a peer can enqueue repeatedly.
        r.Dispatch (user (setDraft (peerIdOf i) (sprintf "message %d" enqueueCounter)))
        r.Dispatch (user (SendDraftMsg (peerIdOf i, queueId)))
        enqueuedIds.Add (QueueId.value queueId)

    let pickEntry (i: int) (pick: int) : QueueId option =
        let entries = QueueOrder.sorted ((peerRunner i).Model ()).Synced.Queue
        if List.isEmpty entries then None
        else Some (entries.[pick % List.length entries]).QueueId

    let apply (op: ScheduleOp) =
        match op with
        | Enqueue i -> enqueue i
        | EditQueued (i, pick) ->
            match pickEntry i pick with
            | Some q ->
                let r = peerRunner i
                r.Dispatch (user (editQueued q (Text.insert 0 "edit ") (r.Model ())))
            | None -> ()
        | Reorder (i, pick) ->
            match pickEntry i pick with
            | Some q ->
                let r = peerRunner i
                match QueueOrder.moveUp (r.Model ()).Synced.Queue q with
                | Some order -> r.Dispatch (user (ReorderQueuedMsg (q, order)))
                | None -> ()
            | None -> ()
        | DeleteQueued (i, pick) ->
            match pickEntry i pick with
            | Some q ->
                deleteOpIds.Add (QueueId.value q)
                (peerRunner i).Dispatch (user (DeleteQueuedMsg q))
            | None -> ()
        | DeliverToProcess i -> deliverToProcess i
        | DeliverToPeer i -> deliverToPeer i
        | TurnCompletes -> turnCompletes ()
        | Interrupt i -> interrupt i
        | ProcessRestart ->
            // The crashed process's persisted doc — replayed into the next life.
            let persisted = DocSync.fullState processDoc
            epoch <- epoch + 1
            pendingRelease <- None
            restartMarks.Add recorded.Count
            bootProcess (Some persisted)

    ops |> List.iter apply

    // Quiescence: deliver everything both ways and let every turn finish, so the
    // liveness/convergence invariants are evaluated on a settled system.
    for _ in 1 .. 3 do
        for i in 0 .. peers.Length - 1 do
            deliverToProcess i
        let mutable guard = 0
        while (scheduler.RunningTurn ()).IsSome && guard < 1000 do
            turnCompletes ()
            guard <- guard + 1
        for i in 0 .. peers.Length - 1 do
            deliverToPeer i

    let queueViewOfDoc (doc: Y.Doc) =
        match SyncedStateSync.ofDoc doc with
        | Ok s -> QueueOrder.sorted s.Queue |> List.map (fun m -> QueueId.value m.QueueId, Text.toString m.Body)
        | Error e -> failwithf "decode failed at quiescence: %A" e

    { Events = List.ofSeq recorded
      DrainRecords = List.ofSeq drainRecords
      RestartMarks = List.ofSeq restartMarks
      EnqueuedIds = List.ofSeq enqueuedIds
      DeleteOpIds = List.ofSeq deleteOpIds
      FinalProcessQueue = queueViewOfDoc processDoc
      FinalPeerQueues = [ for i in 0 .. peers.Length - 1 -> queueViewOfDoc (peerDoc i) ] }

// --- The invariants ---------------------------------------------------------------------

let private check (name: string) (assertion: CaseResult -> unit) =
    testCase name <| fun () ->
        Property.check (property {
            let! ops = genSchedule
            let result = runSchedule ops
            assertion result
        })

let private consumedIdsOf (r: CaseResult) : string list =
    r.Events |> List.choose QueueDrain.consumedOf

let tests =
    testList "Properties (invariants 1-7, Hedgehog schedules)" [

        check "1. exactly-once: every QueueId is consumed at most once; at quiescence, unconsumed = deleted" <| fun r ->
            let consumed = consumedIdsOf r
            Expect.equal (List.length consumed) (List.length (List.distinct consumed))
                "no QueueId is ever consumed twice"
            let consumedSet = Set.ofList consumed
            for id in r.EnqueuedIds do
                if not (Set.contains id consumedSet) then
                    Expect.isTrue (List.contains id r.DeleteOpIds)
                        (sprintf "entry %s neither consumed nor deleted after quiescence" id)

        check "2. snapshot fidelity: every consumed body equals the shadow replica's value at that drain" <| fun r ->
            for record in r.DrainRecords do
                Expect.equal (record.Actual |> List.map snd) (record.Expected |> List.map snd)
                    "the drained bodies are exactly the oracle's predicted snapshots"

        check "3. no mutation after terminal: the log folds deterministically and the timeline is exactly the consumed messages" <| fun r ->
            let envelopes =
                r.Events
                |> List.mapi (fun i e ->
                    { EventId = EventId.fresh ()
                      SessionId = SessionId.create "property-session" |> expect
                      Offset = EventOffset.create (int64 i) |> expect
                      Actor = ActorRef.SessionProcess
                      Timestamp = epoch
                      Event = e })
            let first, _ = ConversationProjection.applyEvents None envelopes ConversationProjection.empty
            let second, _ = ConversationProjection.applyEvents None envelopes ConversationProjection.empty
            Expect.equal second first "folding the same log twice yields the identical projection"
            let humanBodies =
                first.Items
                |> List.filter (fun i -> match i.Author with HumanPeer _ -> true | _ -> false)
                |> List.map (fun i -> i.Body)
            let consumedBodies = r.Events |> List.choose (fun e -> match e with MessageSent m -> Some m.Body | _ -> None)
            Expect.equal humanBodies consumedBodies "the timeline is the consumed messages, in log order — nothing else"

        check "4. convergence: after quiescence every replica agrees on the queue" <| fun r ->
            for peerQueue in r.FinalPeerQueues do
                Expect.equal peerQueue r.FinalProcessQueue "peer replica converged to the process queue"

        check "5. total order: every drain batch is the (Order, QueueId) sort of the snapshot" <| fun r ->
            for record in r.DrainRecords do
                Expect.equal record.Actual record.Expected
                    "the drained (id, body) sequence equals the oracle's ordered prediction"

        check "6. liveness: quiescent + idle + everything delivered => the queue is empty" <| fun r ->
            Expect.equal r.FinalProcessQueue [] "no message is left behind after quiescence"

        check "7. single-flight: within each process life, a turn starts only after the previous one's terminal event" <| fun r ->
            let segments =
                let marks = r.RestartMarks @ [ List.length r.Events ]
                let mutable start = 0
                [ for mark in marks do
                    yield r.Events |> List.skip start |> List.take (mark - start)
                    start <- mark ]
            for segment in segments do
                let mutable runningTurn = false
                for e in segment do
                    match e with
                    | AgentTurnStarted _ ->
                        Expect.isFalse runningTurn "AgentTurnStarted while a turn is already running"
                        runningTurn <- true
                    | AgentMessageCompleted _ | AgentTurnFailed _ | AgentTurnInterrupted _ ->
                        runningTurn <- false
                    | _ -> ()
    ]
