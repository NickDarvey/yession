module Yession.Tests.Acceptance

// Step 09 — the Phase 1 acceptance gate's own checks. The seven required E2E scenarios
// and the model/protocol invariants live in their step suites (Sync/Agent/E2E/Client/
// Domain/SessionProcess — every E2E-N is named in its test title); this file pins the
// remaining acceptance items: the UI checklist, rendered from one representative model,
// and the random peer display name.

open System
open Fable.Pyxpecto
open Yession.Domain
open Yession.Client
open Yession.Tests.Support

let private draftId = DraftId.create "draft-ui" |> expect
let private sentDraftId = DraftId.create "draft-ui-sent" |> expect
let private ada = PeerId.create "ada" |> expect

/// A model exercising every UI element at once: connected, mid catch-up, one active
/// draft (sendable) and one sent, a completed human message, a streaming agent
/// response, and a running agent turn.
let private representativeModel : ClientModel =
    let turnId = AgentTurnId.create "turn-ui" |> expect
    { Peer = { PeerId = ada; DisplayName = "swift-heron" }
      Connection = Connected
      Synced =
        { Drafts =
            Map.ofList
                [ draftId,
                  { DraftId = draftId; Author = ada; Body = Ylmish.Text.ofString "half-typed idea"; Status = Active }
                  sentDraftId,
                  { DraftId = sentDraftId; Author = ada; Body = Ylmish.Text.ofString "ship it"; Status = Sent } ]
          SharedBrief = None }
      Conversation =
        { Items =
            [ { MessageId = MessageId.create "msg-1" |> expect
                Author = HumanPeer ada
                Body = "ship it"
                Status = Complete }
              { MessageId = MessageId.create "msg-agent" |> expect
                Author = ActorRef.Agent
                Body = "Sounds go"
                Status = Streaming } ]
          ActiveAgentMessages = Map.ofList [ turnId, MessageId.create "msg-agent" |> expect ] }
      EventConsumer =
        { LastProcessedOffset = Some (EventOffset.create 5L |> expect)
          LatestKnownOffset = Some (EventOffset.create 7L |> expect)
          IsCatchingUp = true }
      Agent = { ActiveTurn = Some turnId }
      Environment = EnvironmentNotStarted }

let private uiChecklistTests =
    testList "UI checklist" [
        testCase "every required Phase 1 UI element renders from the model" <| fun () ->
            let html = View.render representativeModel
            let required =
                [ "session connection status", "data-connection"
                  "connection state value", ">Connected<"
                  "peer display name", "data-display-name>swift-heron<"
                  "collaborative draft editor", "data-draft-editor"
                  "draft body", "half-typed idea"
                  "send button", "data-send-draft=\"draft-ui\""
                  "conversation timeline", "data-conversation"
                  "sent message in timeline", ">ship it<"
                  "agent streaming response", "data-message-status=\"streaming\""
                  "agent stream indicator", "data-agent-stream"
                  "active agent turn", "data-agent-turn"
                  "last processed event offset", "data-last-processed-offset>5<"
                  "latest known event offset", "data-latest-known-offset>7<"
                  "catch-up status", "data-catch-up>Catching up<" ]
            for label, marker in required do
                Expect.isTrue (html.Contains marker) (sprintf "%s (`%s`) must render" label marker)

        testCase "a sent draft renders without a send button" <| fun () ->
            let html = View.render representativeModel
            Expect.isFalse
                (html.Contains (sprintf "data-send-draft=\"%s\"" (DraftId.value sentDraftId)))
                "sent drafts cannot be re-sent from the UI"

        testCase "the random peer display name is human-readable" <| fun () ->
            let rng = Random 1234
            for _ in 1 .. 20 do
                let name = PeerName.random rng
                let parts = name.Split '-'
                Expect.equal parts.Length 2 "adjective-animal shape"
                Expect.isTrue (parts.[0].Length > 0 && parts.[1].Length > 0) "both halves non-empty"
    ]

let tests =
    testList "Acceptance" [
        uiChecklistTests
    ]
