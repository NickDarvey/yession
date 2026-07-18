module Yession.Tests.Sync

// Step 05 verification: the Ylmish sync boundary.
//
// - Model tests: decode∘encode preserves `SyncedSessionState`; the doc contains drafts
//   but never the conversation projection ("Ylmish is the sync boundary" — nothing else
//   crosses it).
// - Convergence: two full client programs over two docs converge on one draft, first
//   synced in-memory (deterministic, no IO), then over a real WebRTC data channel with
//   the Session Process relaying `State` frames (E2E-1). Drafts are keyed by author (one
//   per client); collaboration is co-editing a peer's slot.
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

// The draft slot key is its author; "ada" is the local peer in the single-client tests
// and the slot owner in the collaboration tests.
let private ada = PeerId.create "ada" |> expect

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
            let registry = BodyRegistry doc
            let p = Harness.run (App.makeProgram doc (ClientModel.init (peer "ada" "Ada")))
            // A draft slot whose rich body is a top-level fragment root (not a model field, and
            // not in the decoded tree). So equality below is over the slot's identity; the body
            // is asserted separately, through the registry.
            Body.author registry p ada "hello world"

            let decoded = SyncedStateSync.ofDoc doc |> Result.mapError (sprintf "%A") |> expect
            Expect.equal decoded (p.Model ()).Synced "the doc decodes back to exactly the model's synced state"
            Expect.equal (Body.draft registry ada) (Some "hello world") "the body fragment holds the composed markdown"

        testCase "an empty doc decodes to the empty synced state (decode-empty = init)" <| fun () ->
            let decoded = SyncedStateSync.ofDoc (Y.Doc.Create ()) |> Result.mapError (sprintf "%A") |> expect
            Expect.equal decoded SyncedSessionState.empty "no drafts, no shared brief"

        testCase "the doc contains drafts but never the conversation projection" <| fun () ->
            let doc = Y.Doc.Create ()
            let registry = BodyRegistry doc
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
            Body.author registry p ada "draft body"

            let drafts : Y.Map<obj> = doc.getMap "drafts"
            Expect.isTrue (drafts.has (PeerId.value ada)) "the draft is in the doc"
            Expect.isFalse (doc.share.has "conversation") "no conversation root type in the doc"
            Expect.isFalse ((doc.getMap () : Y.Map<obj>).has "conversation") "no conversation key in the root map"
            Expect.equal (p.Model ()).Conversation conversation "conversation history survives, app-only"

        testCase "enqueueing round-trips through the codec (draft moves into the queue)" <| fun () ->
            let doc = Y.Doc.Create ()
            let registry = BodyRegistry doc
            let p = Harness.run (App.makeProgram doc (ClientModel.init (peer "ada" "Ada")))
            Body.author registry p ada "queued words"
            let queueId = QueueId.create "q-1" |> expect
            Body.send registry p ada queueId

            let decoded = SyncedStateSync.ofDoc doc |> Result.mapError (sprintf "%A") |> expect
            Expect.equal decoded (p.Model ()).Synced "the doc decodes back to exactly the model's synced state"
            Expect.isFalse (Map.containsKey ada decoded.Drafts) "the draft left the drafts map"
            let entry = decoded.Queue |> Map.find queueId
            Expect.equal entry.Order 1.0 "first entry lands at order 1"
            Expect.equal (Body.queued doc queueId) "queued words" "the body was copied draft->queue on send"

        testCase "two in-memory clients converge on queue reorder and delete" <| fun () ->
            let docA = Y.Doc.Create ()
            let docB = Y.Doc.Create ()
            docA.clientID <- 1.0
            docB.clientID <- 2.0
            let regA = BodyRegistry docA
            let regB = BodyRegistry docB
            let pA = Harness.run (App.makeProgram docA (ClientModel.init (peer "ada" "Ada")))
            let pB = Harness.run (App.makeProgram docB (ClientModel.init (peer "grace" "Grace")))
            let q1 = QueueId.create "q-1" |> expect
            let q2 = QueueId.create "q-2" |> expect
            let queueIds (p: Body.Runner) =
                QueueOrder.sorted (p.Model ()).Synced.Queue |> List.map (fun m -> QueueId.value m.QueueId)

            // Ada enqueues two messages by sending twice: each send clears her one slot.
            for text, queueId in [ "first", q1; "second", q2 ] do
                Body.author regA pA ada text
                Body.send regA pA ada queueId
            syncBoth docA docB
            Expect.equal (queueIds pB) [ "q-1"; "q-2" ] "B sees the queue in order"

            // Ada moves q2 to the front; the reorder converges on both replicas. (Concurrent
            // body co-editing is the fragment CRDT's own cheap test — not re-proven here.)
            match QueueOrder.moveUp (pA.Model ()).Synced.Queue q2 with
            | Some order -> pA.Dispatch (user (ReorderQueuedMsg (q2, order)))
            | None -> failwith "q2 should be movable"
            syncBoth docA docB
            Expect.equal (queueIds pA) [ "q-2"; "q-1" ] "the reorder is visible on A"
            Expect.equal (queueIds pB) (queueIds pA) "replicas converge"

            // Delete propagates and wins over nothing else pending.
            pB.Dispatch (user (DeleteQueuedMsg q2))
            syncBoth docA docB
            Expect.equal (queueIds pA) [ "q-1" ] "the deleted entry is gone on A"
            Expect.equal (queueIds pB) (queueIds pA) "replicas converge after delete"

        testCase "the collaborative title round-trips through the codec" <| fun () ->
            let doc = Y.Doc.Create ()
            let registry = BodyRegistry doc
            let p = Harness.run (App.makeProgram doc (ClientModel.init (peer "ada" "Ada")))
            p.Dispatch (user (EditTitleMsg (Text.insert 0 "Launch plan" (p.Model ()).Synced.Title)))

            let decoded = SyncedStateSync.ofDoc doc |> Result.mapError (sprintf "%A") |> expect
            Expect.equal decoded (p.Model ()).Synced "the doc decodes back to exactly the model's synced state"
            Expect.equal (Text.toString decoded.Title) "Launch plan" "the title crossed the boundary"
            Expect.isTrue (doc.share.has "title") "the title anchors to a named text root"
    ]

// -----------------------------------------------------------------------------
// Phase 3 unit tests — the queue's total order, the drain plan's pure decision
// core, the retired send command, and the conversation projection.
// -----------------------------------------------------------------------------

let private queueUnitTests =
    let ada = PeerId.create "ada" |> expect
    let qid (s: string) = QueueId.create s |> expect
    // The body is a fragment read from the doc at drain time, not a field on the entry, so
    // these order/drain-plan units carry only identity, author, and order.
    let entry (id: string) (order: float) : QueuedMessage =
        { QueueId = qid id; Author = ada; Order = order }
    let queueOf (entries: QueuedMessage list) : Map<QueueId, QueuedMessage> =
        entries |> List.map (fun e -> e.QueueId, e) |> Map.ofList

    testList "Queue order and drain plan" [
        testCase "the queue order is (Order, QueueId): total and deterministic under ties" <| fun () ->
            let queue = queueOf [ entry "b" 2.0; entry "a" 2.0; entry "c" 1.0 ]
            Expect.equal
                (QueueOrder.sorted queue |> List.map (fun m -> QueueId.value m.QueueId))
                [ "c"; "a"; "b" ]
                "order ascending, QueueId breaks ties"

        testCase "enqueue lands at the tail; moveUp/moveDown are one register write between neighbours" <| fun () ->
            let queue = queueOf [ entry "a" 1.0; entry "b" 2.0; entry "c" 3.0 ]
            Expect.equal (QueueOrder.next queue) 4.0 "next appends after the tail"
            Expect.equal (QueueOrder.moveUp queue (qid "c")) (Some 1.5) "c moves between a and b"
            Expect.equal (QueueOrder.moveUp queue (qid "a")) None "the head cannot move up"
            Expect.equal (QueueOrder.moveDown queue (qid "a")) (Some 2.5) "a moves between b and c"
            Expect.equal (QueueOrder.moveDown queue (qid "c")) None "the tail cannot move down"
            Expect.equal (QueueOrder.next Map.empty) 1.0 "an empty queue starts at 1"

        testCase "the drain plan consumes in order and dedups against the log-derived consumed set" <| fun () ->
            let queue = queueOf [ entry "q2" 2.0; entry "q1" 1.0; entry "q3" 3.0 ]
            // q2 was already consumed (a crash between append and removal left it in
            // the doc): it must be repaired out, never consumed twice.
            let plan = QueueDrain.plan (Set.ofList [ "q2" ]) queue
            Expect.equal
                (plan.Batch |> List.map (fun m -> QueueId.value m.QueueId))
                [ "q1"; "q3" ]
                "the batch is the snapshot in order, minus consumed"
            Expect.equal
                (plan.Removals |> List.map QueueId.value)
                [ "q1"; "q2"; "q3" ]
                "every snapshot key leaves the doc, including the repair"

        testCase "a deleted entry is simply absent from the snapshot: never consumed" <| fun () ->
            let plan = QueueDrain.plan Set.empty (queueOf [ entry "kept" 1.0 ])
            Expect.equal (plan.Batch |> List.map (fun m -> QueueId.value m.QueueId)) [ "kept" ] "only present entries consume"

        testCase "MessageSent projects into the conversation" <| fun () ->
            let ada = PeerId.create "ada" |> expect
            let message =
                { MessageId = MessageId.create "msg-1" |> expect
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
// E2E-1 — two real WebRTC clients collaborate on one draft slot and converge.
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

                // Ada composes her own draft (keyed by her peer id); the rich body fragment
                // syncs to Grace over the real WebRTC transport + the Process's State relay.
                do! compose a ada "hello from ada"
                do! b.Runner.WaitFor (fun _ -> draftBody b ada = Some "hello from ada")

                // Grace joins Ada's slot and rewrites the shared body; both replicas converge
                // on it. (Character-level interleave merge is the fragment CRDT's cheap test.)
                do! compose b ada "hello from grace"
                do! a.Runner.WaitFor (fun _ -> draftBody a ada = Some "hello from grace")
                do! b.Runner.WaitFor (fun _ -> draftBody b ada = Some "hello from grace")

                do! a.Channel.Close ()
                do! b.Channel.Close ()
            }

        testCaseAsync "sending enqueues, the Process drains exactly one snapshotted MessageSent, and the entry leaves the queue on both clients (E2E-2/E2E-3)" <|
            async {
                let! a = connect "ada" "Ada"
                let! b = connect "grace" "Grace"

                // Ada drafts "ship it" and both replicas converge on it.
                do! compose a ada "ship it"
                do! b.Runner.WaitFor (fun _ -> draftBody b ada = Some "ship it")

                // Send = enqueue: the draft becomes a queue entry (pure CRDT write); the
                // idle Process drains it into the timeline, and the entry leaves the
                // queue on every replica.
                a.Connection.SendDraft ada
                let settled (m: ClientModel) =
                    Map.isEmpty m.Synced.Queue
                    && not (Map.containsKey ada m.Synced.Drafts)
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

                do! compose a ada "while you were away"
                a.Connection.SendDraft ada
                do! a.Runner.WaitFor (fun m ->
                        m.Conversation.Items |> List.exists (fun i -> i.Body = "while you were away"))

                // Grace reconnects and catches up from her processed offset (E2E-4);
                // the page size of 2 forces the catch-up across multiple reads.
                let! b = reconnect b
                do! b.Runner.WaitFor (fun m ->
                        not m.EventConsumer.IsCatchingUp
                        && (m.Conversation.Items |> List.map (fun i -> i.Body)) = [ "ship it"; "while you were away" ])

                // E2E-7: unsent draft content lives in the draft, never in the timeline —
                // the conversation comes from the projection alone.
                do! compose a ada "UNSENT thought"
                do! b.Runner.WaitFor (fun _ -> draftBody b ada = Some "UNSENT thought")
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
                // The rich body is mounted by the browser editor, so its text is not in the SSR
                // string; the draft editor renders ada's mount host, and the fragment — not the
                // timeline — holds the unsent content.
                Expect.isTrue (editor.Contains (Dom.attr Dom.Hooks.draftInput (PeerId.value ada)))
                    "the draft editor renders the unsent draft's mount host"
                Expect.equal (draftBody b ada) (Some "UNSENT thought")
                    "the unsent content lives in the draft body fragment, not the timeline"

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

// -----------------------------------------------------------------------------
// Pure client-model reducers for the editable title and cursor presence.
// -----------------------------------------------------------------------------

let private titlePresenceTests =
    let base' = ClientModel.init (peer "ada" "Ada")
    let bob = PeerId.create "bob" |> expect
    testList "Title and presence (client model)" [
        testCase "ConnectedMsg records the session id as the secondary identifier" <| fun () ->
            let accepted =
                { SessionId = SessionId.create "demo-session" |> expect
                  AssignedDisplayName = "swift-heron"
                  LatestOffset = None }
            let next = ClientModel.update (ConnectedMsg accepted) base'
            Expect.equal next.Session (Some (SessionId.create "demo-session" |> expect)) "the session id is learned from PeerAccepted"

        testCase "RemotePresenceMsg adds, updates, and clears a peer's cursor" <| fun () ->
            let added = ClientModel.update (RemotePresenceMsg { PeerId = bob; DisplayName = "brave-owl"; TitleCursor = Some 3 }) base'
            Expect.equal (Map.tryFind bob added.Presence) (Some { DisplayName = "brave-owl"; Index = 3 }) "the peer's caret is recorded"
            let moved = ClientModel.update (RemotePresenceMsg { PeerId = bob; DisplayName = "brave-owl"; TitleCursor = Some 7 }) added
            Expect.equal (Map.tryFind bob moved.Presence |> Option.map (fun c -> c.Index)) (Some 7) "the caret moves"
            let cleared = ClientModel.update (RemotePresenceMsg { PeerId = bob; DisplayName = ""; TitleCursor = None }) moved
            Expect.isFalse (Map.containsKey bob cleared.Presence) "a cleared cursor removes the peer"

        testCase "RemotePresenceMsg ignores the local peer's own cursor" <| fun () ->
            let next = ClientModel.update (RemotePresenceMsg { PeerId = base'.Peer.PeerId; DisplayName = "Ada"; TitleCursor = Some 2 }) base'
            Expect.isFalse (Map.containsKey base'.Peer.PeerId next.Presence) "you never render your own remote caret"
    ]

let tests =
    testList "Sync" [
        codecTests
        queueUnitTests
        titlePresenceTests
        Tag.needs "Draft sync E2E" [ Tag.Ports; Tag.Native ] (fun () -> e2eTests)
    ]
