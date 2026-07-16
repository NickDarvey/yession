module Yession.Tests.Acceptance

// Step 09 — the Phase 1 acceptance gate's own checks. The seven required E2E scenarios
// and the model/protocol invariants live in their step suites (Sync/Agent/E2E/Client/
// Domain/SessionProcess — every E2E-N is named in its test title); this file pins the
// remaining acceptance items: the UI checklist, rendered from one representative model,
// and the random peer display name.

open System
open Fable.Pyxpecto
open Yession.Domain
open Yession.App
open Yession.Tests.Support

let private queueId = QueueId.create "queue-ui" |> expect
let private ada = PeerId.create "ada" |> expect

/// A model exercising every UI element at once: connected, mid catch-up, one draft
/// (sendable), one queued message (editable/reorderable/deletable), a completed human
/// message, a streaming agent response, and a running agent turn.
let private representativeModel : ClientModel =
    let turnId = AgentTurnId.create "turn-ui" |> expect
    { Peer = { PeerId = ada; DisplayName = "swift-heron" }
      Connection = Connected
      Synced =
        { Drafts =
            Map.ofList
                [ ada,
                  { Author = ada; Body = Ylmish.Text.ofString "half-typed idea" } ]
          Queue =
            Map.ofList
                [ queueId,
                  { QueueId = queueId; Author = ada; Body = Ylmish.Text.ofString "queued for the agent"; Order = 1.0 } ]
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
      Environment = EnvironmentNotStarted
      Commands = CommandLog.empty }

let private uiChecklistTests =
    testList "UI checklist" [
        testCase "every required Phase 1 UI element renders from the model" <| fun () ->
            let html = Support.render representativeModel
            let required =
                [ "session connection status", Dom.Hooks.connection
                  "connection state value", Dom.hookText Dom.Hooks.connection Dom.Text.connected
                  "peer display name", Dom.hookText Dom.Hooks.displayName "swift-heron"
                  "collaborative draft editor", Dom.Hooks.draftEditor
                  "draft body", "half-typed idea"
                  "send button", Dom.attr Dom.Hooks.sendDraft "ada"
                  "conversation timeline", Dom.Hooks.conversation
                  "sent message in timeline", Dom.hookText Dom.Hooks.messageBody "ship it"
                  "agent streaming response", Dom.attr Dom.Hooks.messageStatus Dom.Text.streaming
                  "agent stream indicator", Dom.Hooks.agentStream
                  "active agent turn", Dom.Hooks.agentTurn
                  "last processed event offset", Dom.hookText Dom.Hooks.lastProcessedOffset "5"
                  "latest known event offset", Dom.hookText Dom.Hooks.latestKnownOffset "7"
                  "catch-up status", Dom.hookText Dom.Hooks.catchUp Dom.Text.catchingUp
                  "environment status (Phase 2)", Dom.Hooks.environment
                  "read-only command log (Phase 2)", Dom.Hooks.commandLog
                  "message queue (Phase 3)", Dom.Hooks.messageQueue
                  "queued message body", "queued for the agent"
                  "queued message editor", Dom.attr Dom.Hooks.queueInput "queue-ui"
                  "queue reorder up", Dom.attr Dom.Hooks.queueUp "queue-ui"
                  "queue reorder down", Dom.attr Dom.Hooks.queueDown "queue-ui"
                  "queue delete", Dom.attr Dom.Hooks.queueDelete "queue-ui" ]
            for label, marker in required do
                Expect.isTrue (html.Contains marker) (sprintf "%s (`%s`) must render" label marker)

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
