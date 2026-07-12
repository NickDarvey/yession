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
open Elmish
open Fable.Pyxpecto
open Yjs
open Ylmish
open Yession.Domain
open Yession.SessionProcess
open Yession.Client
open Yession.Host

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

let private user msg = Ylmish.Program.Message.User msg

/// Run an Elmish program headlessly, exposing the latest model, dispatch, and an
/// event-driven `WaitFor` that resolves the first time the model satisfies a predicate.
module private Harness =

    type Runner<'model, 'msg> =
        { Model : unit -> 'model
          Dispatch : 'msg -> unit
          WaitFor : ('model -> bool) -> Async<unit> }

    let run (program: Program<unit, 'model, 'msg, unit>) : Runner<'model, 'msg> =
        let mutable model = Unchecked.defaultof<'model>
        let mutable dispatch : 'msg -> unit = ignore
        let mutable waiters : (('model -> bool) * (unit -> unit)) list = []
        let setState m d =
            model <- m
            dispatch <- d
            let fire, keep = waiters |> List.partition (fun (predicate, _) -> predicate m)
            waiters <- keep
            fire |> List.iter (fun (_, resume) -> resume ())
        Program.withSetState setState program |> Program.run
        { Model = fun () -> model
          Dispatch = fun msg -> dispatch msg
          WaitFor =
            fun predicate ->
                Async.FromContinuations (fun (cont, _, _) ->
                    if predicate model then cont ()
                    else waiters <- (predicate, fun () -> cont ()) :: waiters) }

let private peer (id: string) (name: string) : PeerState =
    { PeerId = PeerId.create id |> expect; DisplayName = name }

let private draftId1 = DraftId.create "draft-1" |> expect

let private bodyOf (draftId: DraftId) (m: ClientModel) : string option =
    m.Synced.Drafts |> Map.tryFind draftId |> Option.map (fun d -> Text.toString d.Body)

let private editBody (draftId: DraftId) (edit: Text -> Text) (m: ClientModel) : ClientMsg =
    let current = (Map.find draftId m.Synced.Drafts).Body
    EditDraftBodyMsg (draftId, edit current)

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
                        Status = Complete } ] }
            let initial = { ClientModel.init (peer "ada" "Ada") with Conversation = conversation }
            let p = Harness.run (App.makeProgram doc initial)
            p.Dispatch (user (StartDraftMsg draftId1))
            p.Dispatch (user (editBody draftId1 (Text.insert 0 "draft body") (p.Model ())))

            let drafts : Y.Map<obj> = doc.getMap "drafts"
            Expect.isTrue (drafts.has (DraftId.value draftId1)) "the draft is in the doc"
            Expect.isFalse (doc.share.has "conversation") "no conversation root type in the doc"
            Expect.isFalse ((doc.getMap () : Y.Map<obj>).has "conversation") "no conversation key in the root map"
            Expect.equal (p.Model ()).Conversation conversation "conversation history survives, app-only"

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
// E2E-1 — two real WebRTC clients, drafts converge, DraftStarted appended once.
// -----------------------------------------------------------------------------

let private port = 8101
let private token = "sync-e2e-token"
let private sessionId = SessionId.create "sync-e2e-session" |> expect
let private signalUrl = sprintf "http://127.0.0.1:%d/signal" port

let mutable private host : Host.SessionHost option = None

type private Client =
    { Runner : Harness.Runner<ClientModel, Ylmish.Program.Message<ClientModel, ClientMsg>>
      Channel : FrameChannel<string> }

/// Connect one full client: WebRTC channel, its own Yjs doc, the withYlmish program,
/// and the connection driver. Resolves once the model reaches `Connected`.
let private connectClient (id: string) (name: string) : Async<Client> =
    async {
        let! channel = WebRtc.connect signalUrl
        let doc = Y.Doc.Create ()
        let local = peer id name
        let runner = Harness.run (App.makeProgram doc (ClientModel.init local))
        let hello = { PeerId = local.PeerId; DisplayName = name; Token = token }
        Async.StartImmediate (App.connect doc hello (user >> runner.Dispatch) channel)
        do! runner.WaitFor (fun m -> m.Connection = Connected)
        return { Runner = runner; Channel = channel }
    }

let private e2eTests =
    testList "Draft sync E2E" [
        testCaseAsync "start the Session Process host" <|
            async {
                let! h = Host.start sessionId token port
                host <- Some h
            }

        testCaseAsync "two clients collaboratively edit one draft and converge (E2E-1)" <|
            async {
                let! a = connectClient "ada" "Ada"
                let! b = connectClient "grace" "Grace"

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
        e2eTests
    ]
