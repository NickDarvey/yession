module Yession.Tests.Sync

// Step 05 verification: the Ylmish sync boundary.
//
// - Model tests: decode∘encode preserves `SyncedSessionState`; the doc contains drafts
//   but never the conversation projection ("Ylmish is the sync boundary" — nothing else
//   crosses it).
// - Convergence: two full client programs over two docs converge on one draft, first
//   synced in-memory (deterministic, no IO), then over a real WebRTC data channel with
//   the Session Process relaying `State` frames (E2E-1). `DraftStarted` is appended by
//   the Session Process exactly once.
//
// Event-driven throughout: models are observed via predicate waiters resolved on every
// Elmish `setState` — no sleeps or polling.

open System
open Fable.Pyxpecto
open Yjs
open Ylmish
open Yession.Domain
open Yession.SessionProcess
open Yession.App
open Yession.Host
open Yession.Tests.Support

let private draftId1 = DraftId.create "draft-1" |> expect

let private syncBoth (a: Y.Doc) (b: Y.Doc) =
    Y.applyUpdate (b, Y.encodeStateAsUpdate a)
    Y.applyUpdate (a, Y.encodeStateAsUpdate b)

// -----------------------------------------------------------------------------
// Model tests — the codec through the public surface (program encode + doc decode).
// -----------------------------------------------------------------------------

let private codecTests =
    testList "Sync boundary" [
        testCase "decode∘encode preserves the synced session state" <| fun () ->
            let doc = Y.Doc.Create ()
            let p = Harness.run (App.makeProgram doc (ClientModel.init (peer "ada" "Ada")))
            p.Dispatch (user (StartDraftMsg draftId1))
            p.Dispatch (user (editBody draftId1 (Text.insert 0 "hello") (p.Model ())))
            p.Dispatch (user (editBody draftId1 (Text.insert 5 " world") (p.Model ())))

            let decoded = SyncedStateSync.ofDoc doc |> Result.mapError (sprintf "%A") |> expect
            Expect.equal decoded (p.Model ()).Synced "the doc decodes back to exactly the model's synced state"
            Expect.equal (bodyOf draftId1 (p.Model ())) (Some "hello world") "the edited body"

        testCase "an empty doc decodes to the empty synced state (decode-empty = init)" <| fun () ->
            let decoded = SyncedStateSync.ofDoc (Y.Doc.Create ()) |> Result.mapError (sprintf "%A") |> expect
            Expect.equal decoded SyncedSessionState.empty "no drafts, no shared brief"

        testCase "the doc contains drafts but never the conversation projection" <| fun () ->
            let doc = Y.Doc.Create ()
            // A model that already carries conversation history: it is app-only state,
            // mentioned by neither encode nor decode, so it must never reach the doc —
            // and must survive the program's own decode round-trips untouched.
            let messageId = MessageId.create "m-1" |> expect
            let conversation =
                { Items =
                    [ { MessageId = messageId
                        Author = ActorRef.System
                        Body = "secret history"
                        Status = Complete } ]
                  ActiveAgentMessages = Map.empty }
            let initial = { ClientModel.init (peer "ada" "Ada") with Conversation = conversation }
            let p = Harness.run (App.makeProgram doc initial)
            p.Dispatch (user (StartDraftMsg draftId1))
            p.Dispatch (user (editBody draftId1 (Text.insert 0 "draft body") (p.Model ())))

            let drafts : Y.Map<obj> = doc.getMap "drafts"
            Expect.isTrue (drafts.has (DraftId.value draftId1)) "the draft is in the doc"
            Expect.isFalse (doc.share.has "conversation") "no conversation root type in the doc"
            Expect.isFalse ((doc.getMap () : Y.Map<obj>).has "conversation") "no conversation key in the root map"
            Expect.equal (p.Model ()).Conversation conversation "conversation history survives, app-only"

        testCase "enqueueing round-trips through the codec (draft moves into the queue)" <| fun () ->
            let doc = Y.Doc.Create ()
            let p = Harness.run (App.makeProgram doc (ClientModel.init (peer "ada" "Ada")))
            p.Dispatch (user (StartDraftMsg draftId1))
            p.Dispatch (user (editBody draftId1 (Text.insert 0 "queued words") (p.Model ())))
            let queueId = QueueId.create "q-1" |> expect
            p.Dispatch (user (SendDraftMsg (draftId1, queueId)))

            let decoded = SyncedStateSync.ofDoc doc |> Result.mapError (sprintf "%A") |> expect
            Expect.equal decoded (p.Model ()).Synced "the doc decodes back to exactly the model's synced state"
            Expect.isFalse (Map.containsKey draftId1 decoded.Drafts) "the draft left the drafts map"
            let entry = decoded.Queue |> Map.find queueId
            Expect.equal (Text.toString entry.Body) "queued words" "the body moved with it"
            Expect.equal entry.Order 1.0 "first entry lands at order 1"

        testCase "two in-memory clients converge on queue edit, reorder, and delete" <| fun () ->
            let docA = Y.Doc.Create ()
            let docB = Y.Doc.Create ()
            docA.clientID <- 1.0
            docB.clientID <- 2.0
            let pA = Harness.run (App.makeProgram docA (ClientModel.init (peer "ada" "Ada")))
            let pB = Harness.run (App.makeProgram docB (ClientModel.init (peer "grace" "Grace")))
            let q1 = QueueId.create "q-1" |> expect
            let q2 = QueueId.create "q-2" |> expect

            // Ada enqueues two messages.
            for draftKey, text, queueId in [ "d-1", "first", q1; "d-2", "second", q2 ] do
                let draftId = DraftId.create draftKey |> expect
                pA.Dispatch (user (StartDraftMsg draftId))
                pA.Dispatch (user (editBody draftId (Text.insert 0 text) (pA.Model ())))
                pA.Dispatch (user (SendDraftMsg (draftId, queueId)))
            syncBoth docA docB
            Expect.equal (queueView (pB.Model ())) [ "q-1", "first"; "q-2", "second" ] "B sees the queue in order"

            // Concurrent: Grace edits q1's body while Ada moves q2 to the front.
            pB.Dispatch (user (editQueued q1 (Text.insert 5 " draft") (pB.Model ())))
            match QueueOrder.moveUp (pA.Model ()).Synced.Queue q2 with
            | Some order -> pA.Dispatch (user (ReorderQueuedMsg (q2, order)))
            | None -> failwith "q2 should be movable"
            syncBoth docA docB

            Expect.equal (queueView (pA.Model ())) [ "q-2", "second"; "q-1", "first draft" ] "edit and reorder both survive on A"
            Expect.equal (queueView (pB.Model ())) (queueView (pA.Model ())) "replicas converge"

            // Delete propagates and wins over nothing else pending.
            pB.Dispatch (user (DeleteQueuedMsg q2))
            syncBoth docA docB
            Expect.equal (queueView (pA.Model ())) [ "q-1", "first draft" ] "the deleted entry is gone on A"
            Expect.equal (queueView (pB.Model ())) (queueView (pA.Model ())) "replicas converge after delete"

        testCase "two in-memory clients converge on one draft (drafts merge, bodies interleave)" <| fun () ->
            let docA = Y.Doc.Create ()
            let docB = Y.Doc.Create ()
            // Yjs breaks concurrent ties by clientID; pin them so the test is one outcome.
            docA.clientID <- 1.0
            docB.clientID <- 2.0
            let pA = Harness.run (App.makeProgram docA (ClientModel.init (peer "ada" "Ada")))
            let pB = Harness.run (App.makeProgram docB (ClientModel.init (peer "grace" "Grace")))

            pA.Dispatch (user (StartDraftMsg draftId1))
            pA.Dispatch (user (editBody draftId1 (Text.insert 0 "hello") (pA.Model ())))
            syncBoth docA docB
            Expect.equal (bodyOf draftId1 (pB.Model ())) (Some "hello") "B sees A's draft after sync"

            // Concurrent edits to the same body, at disjoint positions.
            pA.Dispatch (user (editBody draftId1 (Text.insert 5 " world") (pA.Model ())))
            pB.Dispatch (user (editBody draftId1 (Text.insert 0 "oh, ") (pB.Model ())))
            syncBoth docA docB

            Expect.equal (bodyOf draftId1 (pA.Model ())) (Some "oh, hello world") "both edits survive on A"
            Expect.equal (bodyOf draftId1 (pB.Model ())) (bodyOf draftId1 (pA.Model ())) "models converge"
    ]

// -----------------------------------------------------------------------------
// Phase 3 unit tests — the queue's total order, the drain plan's pure decision
// core, the retired send command, and the conversation projection.
// -----------------------------------------------------------------------------

let private queueUnitTests =
    let ada = PeerId.create "ada" |> expect
    let qid (s: string) = QueueId.create s |> expect
    let entry (id: string) (order: float) (body: string) : QueuedMessage =
        { QueueId = qid id; Author = ada; Body = Text.ofString body; Order = order }
    let queueOf (entries: QueuedMessage list) : Map<QueueId, QueuedMessage> =
        entries |> List.map (fun e -> e.QueueId, e) |> Map.ofList

    testList "Queue order and drain plan" [
        testCase "the queue order is (Order, QueueId): total and deterministic under ties" <| fun () ->
            let queue = queueOf [ entry "b" 2.0 "second-by-id"; entry "a" 2.0 "first-by-id"; entry "c" 1.0 "first" ]
            Expect.equal
                (QueueOrder.sorted queue |> List.map (fun m -> QueueId.value m.QueueId))
                [ "c"; "a"; "b" ]
                "order ascending, QueueId breaks ties"

        testCase "enqueue lands at the tail; moveUp/moveDown are one register write between neighbours" <| fun () ->
            let queue = queueOf [ entry "a" 1.0 ""; entry "b" 2.0 ""; entry "c" 3.0 "" ]
            Expect.equal (QueueOrder.next queue) 4.0 "next appends after the tail"
            Expect.equal (QueueOrder.moveUp queue (qid "c")) (Some 1.5) "c moves between a and b"
            Expect.equal (QueueOrder.moveUp queue (qid "a")) None "the head cannot move up"
            Expect.equal (QueueOrder.moveDown queue (qid "a")) (Some 2.5) "a moves between b and c"
            Expect.equal (QueueOrder.moveDown queue (qid "c")) None "the tail cannot move down"
            Expect.equal (QueueOrder.next Map.empty) 1.0 "an empty queue starts at 1"

        testCase "the drain plan consumes in order and dedups against the log-derived consumed set" <| fun () ->
            let queue = queueOf [ entry "q2" 2.0 "two"; entry "q1" 1.0 "one"; entry "q3" 3.0 "three" ]
            // q2 was already consumed (a crash between append and removal left it in
            // the doc): it must be repaired out, never consumed twice.
            let plan = QueueDrain.plan (Set.ofList [ "q2" ]) queue
            Expect.equal
                (plan.Batch |> List.map (fun m -> Text.toString m.Body))
                [ "one"; "three" ]
                "the batch is the snapshot in order, minus consumed"
            Expect.equal
                (plan.Removals |> List.map QueueId.value)
                [ "q1"; "q2"; "q3" ]
                "every snapshot key leaves the doc, including the repair"

        testCase "a deleted entry is simply absent from the snapshot: never consumed" <| fun () ->
            let plan = QueueDrain.plan Set.empty (queueOf [ entry "kept" 1.0 "kept" ])
            Expect.equal (plan.Batch |> List.map (fun m -> QueueId.value m.QueueId)) [ "kept" ] "only present entries consume"

        testCase "MessageSent projects into the conversation" <| fun () ->
            let ada = PeerId.create "ada" |> expect
            let message =
                { MessageId = MessageId.create "msg-1" |> expect
                  DraftId = Some draftId1
                  QueueId = Some (qid "q-1")
                  Author = HumanPeer ada
                  Body = "ship it" }
            let envelope =
                { EventId = EventId.fresh ()
                  SessionId = SessionId.create "send-tests" |> expect
                  Offset = EventOffset.zero
                  Actor = HumanPeer ada
                  Timestamp = DateTimeOffset.UtcNow
                  Event = MessageSent message }
            let projection, _ = ConversationProjection.applyEvents None [ envelope ] ConversationProjection.empty
            Expect.equal
                projection.Items
                [ { MessageId = message.MessageId
                    Author = HumanPeer ada
                    Body = "ship it"
                    Status = Complete } ]
                "the sent message is a complete conversation item"

        testCase "duplicate event pages do not duplicate conversation items" <| fun () ->
            let ada = PeerId.create "ada" |> expect
            let envelope =
                { EventId = EventId.fresh ()
                  SessionId = SessionId.create "send-tests" |> expect
                  Offset = EventOffset.zero
                  Actor = HumanPeer ada
                  Timestamp = DateTimeOffset.UtcNow
                  Event =
                    MessageSent
                        { MessageId = MessageId.create "msg-1" |> expect
                          DraftId = None
                          QueueId = None
                          Author = HumanPeer ada
                          Body = "once only" } }
            let page : EventPage<SessionEvent> =
                { Events = [ envelope ]; LastOffset = Some envelope.Offset; IsEnd = true }
            let model =
                ClientModel.init (peer "ada" "Ada")
                |> ClientModel.update (EventsPageMsg page)
                |> ClientModel.update (EventsPageMsg page)
            Expect.equal
                (model.Conversation.Items |> List.map (fun i -> i.Body))
                [ "once only" ]
                "re-applying an overlapping page adds nothing"
            Expect.equal model.EventConsumer.LastProcessedOffset (Some EventOffset.zero) "progress recorded"
            Expect.isFalse model.EventConsumer.IsCatchingUp "caught up after consuming the page"
    ]

// -----------------------------------------------------------------------------
// E2E-1 — two real WebRTC clients, drafts converge, DraftStarted appended once.
// E2E-2/E2E-3 — sending appends one snapshotted MessageSent visible to both
// clients; later edits never mutate it.
// -----------------------------------------------------------------------------

let private port = 8101
let private token = "sync-e2e-token"
let private sessionId = SessionId.create "sync-e2e-session" |> expect
let private signalUrl = sprintf "http://127.0.0.1:%d/signal" port

let mutable private host : Host.SessionHost option = None

let private connect = connectClient signalUrl token
let private reconnect = reconnectClient signalUrl

let private e2eTests =
    testList "Draft sync E2E" [
        testCaseAsync "start the Session Process host" <|
            async {
                let! h = Host.start sessionId token port
                host <- Some h
            }

        testCaseAsync "two clients collaboratively edit one draft and converge (E2E-1)" <|
            async {
                let! a = connect "ada" "Ada"
                let! b = connect "grace" "Grace"

                // Ada starts the draft (app-minted id) and seeds the body.
                a.Runner.Dispatch (user (StartDraftMsg draftId1))
                a.Runner.Dispatch (user (editBody draftId1 (Text.insert 0 "hello") (a.Runner.Model ())))
                do! b.Runner.WaitFor (fun m -> bodyOf draftId1 m = Some "hello")

                // Concurrent edits: both dispatched in the same tick, so each is based on
                // "hello"; positions are disjoint, so the merge is one deterministic string.
                a.Runner.Dispatch (user (editBody draftId1 (Text.insert 5 " world") (a.Runner.Model ())))
                b.Runner.Dispatch (user (editBody draftId1 (Text.insert 0 "oh, ") (b.Runner.Model ())))

                do! a.Runner.WaitFor (fun m -> bodyOf draftId1 m = Some "oh, hello world")
                do! b.Runner.WaitFor (fun m -> bodyOf draftId1 m = Some "oh, hello world")

                // The durable fact: the Session Process appended DraftStarted exactly once.
                let h = host.Value
                let! page = h.Log.Read None Int32.MaxValue
                let draftStarts =
                    page.Events
                    |> List.choose (fun e ->
                        match e.Event with
                        | DraftStarted d -> Some (d, e.Actor)
                        | _ -> None)
                let ada = PeerId.create "ada" |> expect
                Expect.equal
                    draftStarts
                    [ { DraftId = draftId1; StartedBy = ada }, HumanPeer ada ]
                    "one DraftStarted, attributed to the starting peer"

                do! a.Channel.Close ()
                do! b.Channel.Close ()
            }

        testCaseAsync "sending enqueues, the Process drains exactly one snapshotted MessageSent, and the entry leaves the queue on both clients (E2E-2/E2E-3)" <|
            async {
                let sendDraftId = DraftId.create "draft-2" |> expect
                let! a = connect "ada" "Ada"
                let! b = connect "grace" "Grace"

                // Ada drafts "ship it" and both replicas converge on it.
                a.Runner.Dispatch (user (StartDraftMsg sendDraftId))
                a.Runner.Dispatch (user (editBody sendDraftId (Text.insert 0 "ship it") (a.Runner.Model ())))
                do! b.Runner.WaitFor (fun m -> bodyOf sendDraftId m = Some "ship it")

                // Send = enqueue: the draft becomes a queue entry (pure CRDT write); the
                // idle Process drains it into the timeline, and the entry leaves the
                // queue on every replica.
                a.Connection.SendDraft sendDraftId
                let settled (m: ClientModel) =
                    Map.isEmpty m.Synced.Queue
                    && not (Map.containsKey sendDraftId m.Synced.Drafts)
                    && (m.Conversation.Items |> List.map (fun i -> i.Body)) = [ "ship it" ]
                do! a.Runner.WaitFor settled
                do! b.Runner.WaitFor settled

                // E2E-2: exactly one MessageSent — body snapshotted at consumption,
                // attributed to the sender, and anchored to its queue entry.
                let h = host.Value
                let messagesIn (events: EventEnvelope<SessionEvent> list) =
                    events
                    |> List.choose (fun e ->
                        match e.Event with
                        | MessageSent m -> Some m
                        | _ -> None)
                let! page = h.Log.Read None Int32.MaxValue
                match messagesIn page.Events with
                | [ message ] ->
                    Expect.equal message.Body "ship it" "the body is the consumption-time snapshot"
                    Expect.equal message.Author (HumanPeer (PeerId.create "ada" |> expect)) "authored by the sender"
                    Expect.isTrue message.QueueId.IsSome "anchored to its queue entry (the dedup key)"
                | other -> failwithf "expected exactly one MessageSent, got %A" other

                // E2E-3: the terminal transition is immutable — the event log holds the
                // one snapshot (the edit/delete-vs-accept races are pinned in Phase3.fs).
                let! after = h.Log.Read None Int32.MaxValue
                match messagesIn after.Events with
                | [ message ] -> Expect.equal message.Body "ship it" "the sent message never mutates"
                | other -> failwithf "expected the one immutable MessageSent, got %A" other

                do! a.Channel.Close ()
                do! b.Channel.Close ()
            }

        testCaseAsync "a disconnected client catches up by offset on reconnect; the timeline renders only projected events (E2E-4/E2E-7)" <|
            async {
                let! a = connect "ada" "Ada"
                let! b = connect "grace" "Grace"

                // Both consume the log so far (it already holds "ship it" from E2E-2).
                let caughtUp (m: ClientModel) =
                    not m.EventConsumer.IsCatchingUp
                    && (m.Conversation.Items |> List.exists (fun i -> i.Body = "ship it"))
                do! a.Runner.WaitFor caughtUp
                do! b.Runner.WaitFor caughtUp

                // Grace disconnects; the session continues without her.
                do! b.Channel.Close ()
                do! b.Runner.WaitFor (fun m -> m.Connection = Reconnecting)

                let missedId = DraftId.create "draft-3" |> expect
                a.Runner.Dispatch (user (StartDraftMsg missedId))
                a.Runner.Dispatch (user (editBody missedId (Text.insert 0 "while you were away") (a.Runner.Model ())))
                a.Connection.SendDraft missedId
                do! a.Runner.WaitFor (fun m ->
                        m.Conversation.Items |> List.exists (fun i -> i.Body = "while you were away"))

                // Grace reconnects and catches up from her processed offset (E2E-4);
                // the page size of 2 forces the catch-up across multiple reads.
                let! b = reconnect b
                do! b.Runner.WaitFor (fun m ->
                        not m.EventConsumer.IsCatchingUp
                        && (m.Conversation.Items |> List.map (fun i -> i.Body)) = [ "ship it"; "while you were away" ])

                // E2E-7: unsent draft content renders in the draft editor, never in the
                // timeline — the conversation comes from the projection alone.
                let unsentId = DraftId.create "draft-unsent" |> expect
                a.Runner.Dispatch (user (StartDraftMsg unsentId))
                a.Runner.Dispatch (user (editBody unsentId (Text.insert 0 "UNSENT thought") (a.Runner.Model ())))
                do! b.Runner.WaitFor (fun m -> bodyOf unsentId m = Some "UNSENT thought")
                let html = Support.render (b.Runner.Model ())
                // A section's markup runs from its data marker to its closing tag (no
                // section nests another), so these slices are exact wherever the layout
                // places the section in the document.
                let sectionAt (marker: string) =
                    let start = html.IndexOf marker
                    html.Substring (start, html.IndexOf ("</section>", start) - start)
                let timeline = sectionAt Dom.Hooks.conversation
                let editor = sectionAt Dom.Hooks.draftEditor
                Expect.isTrue (timeline.Contains "while you were away") "the sent message is in the timeline"
                Expect.isFalse (timeline.Contains "UNSENT") "unsent draft edits never appear in the timeline"
                Expect.isTrue (editor.Contains "UNSENT thought") "the live draft renders in the draft editor"

                do! a.Channel.Close ()
                do! b.Channel.Close ()
            }

        testCaseAsync "clients are read-only event consumers: spoofed frames never append (E2E-6)" <|
            async {
                let mallory = PeerId.create "mallory" |> expect
                let! channel = WebRtc.connect signalUrl
                do! channel.Send (Control (PeerHello { PeerId = mallory; DisplayName = "Mallory"; Token = token }))
                let rec awaitAccepted () =
                    async {
                        match! channel.Receive () with
                        | Some (Control (PeerAccepted _)) -> return ()
                        | Some _ -> return! awaitAccepted ()
                        | None -> return failwith "channel closed before accept"
                    }
                do! awaitAccepted ()

                // Forge a MessageSent inside an EventsPage plus an availability hint.
                // No frame appends: the Session Process drains both without effect.
                let forged =
                    { EventId = EventId.fresh ()
                      SessionId = sessionId
                      Offset = EventOffset.create 999L |> expect
                      Actor = HumanPeer mallory
                      Timestamp = DateTimeOffset.UtcNow
                      Event =
                        MessageSent
                            { MessageId = MessageId.create "forged" |> expect
                              DraftId = None
                              QueueId = None
                              Author = HumanPeer mallory
                              Body = "forged message" } }
                do! channel.Send (
                        EventLog (
                            EventsPage (
                                RequestId.fresh (),
                                { Events = [ forged ]; LastOffset = Some forged.Offset; IsEnd = true })))
                do! channel.Send (EventLog (EventsAvailable forged.Offset))

                // A real read after the spoofed frames (ordered channel => they were
                // already processed) shows the log untouched by them.
                let requestId = RequestId.fresh ()
                do! channel.Send (EventLog (ReadEventsAfter (requestId, None, 1000)))
                let rec awaitPage () =
                    async {
                        match! channel.Receive () with
                        | Some (EventLog (EventsPage (r, page))) when r = requestId -> return page
                        | Some _ -> return! awaitPage ()
                        | None -> return failwith "channel closed before the events page"
                    }
                let! page = awaitPage ()
                let forgedInLog =
                    page.Events
                    |> List.exists (fun e ->
                        match e.Event with
                        | MessageSent m -> m.Body = "forged message"
                        | _ -> false)
                Expect.isFalse forgedInLog "no spoofed event reaches the log"
                do! channel.Close ()
            }

        testCaseAsync "stop the Session Process host" <|
            async {
                match host with
                | Some h -> do! h.Stop ()
                | None -> ()
            }
    ]

let tests =
    testList "Sync" [
        codecTests
        queueUnitTests
        Tag.verify e2eTests
    ]
