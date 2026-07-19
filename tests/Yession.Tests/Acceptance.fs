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
let private bob = PeerId.create "bob" |> expect
let private sessionId = SessionId.create "demo-session" |> expect

/// A model exercising every UI element at once: connected, mid catch-up, one draft
/// (sendable), one queued message (editable/reorderable/deletable), a completed human
/// message, a streaming agent response, and a running agent turn.
let private representativeModel : ClientModel =
    let turnId = AgentTurnId.create "turn-ui" |> expect
    { Peer = { PeerId = ada; DisplayName = "swift-heron" }
      Connection = Connected
      Session = Some sessionId
      Synced =
        // Draft/queue bodies are rich-text `Y.XmlFragment`s mounted by the browser editor,
        // not fields on the model — so the SSR fixture carries only the slot's identity; the
        // checklist below asserts the mount *hosts* render (`data-rich-body`/`data-*-input`),
        // and the body-content rendering is a browser concern covered by the editor E2E.
        { Drafts = Map.ofList [ ada, { Author = ada } ]
          Queue = Map.ofList [ queueId, { QueueId = queueId; Author = ada; Order = 1.0 } ]
          Title = Ylmish.Text.ofString "planning the launch"
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
      Presence = Map.ofList [ bob, { DisplayName = "brave-owl"; Focus = { Field = Title; Pos = { Anchor = "AQI="; Head = "AwQ=" } } } ]
      Environment = EnvironmentNotStarted
      Commands = CommandLog.empty }

let private uiChecklistTests =
    testList "UI checklist" [
        testCase "every required Phase 1 UI element renders from the model" <| fun () ->
            let html = Support.render representativeModel
            let required =
                [ "session connection status", Dom.Hooks.connection
                  "connection state value", Dom.hookText Dom.Hooks.connection Dom.Text.connected
                  "editable session title", Dom.Hooks.sessionTitle
                  "title body", "planning the launch"
                  "session id secondary identifier", Dom.hookText Dom.Hooks.sessionId "demo-session"
                  "remote collaborator cursor", Dom.attr Dom.Hooks.cursorPeer (PeerId.value bob)
                  "remote cursor peer label", "brave-owl"
                  "peer display name", Dom.hookText Dom.Hooks.displayName "swift-heron"
                  "collaborative draft editor", Dom.Hooks.draftEditor
                  "draft editor mount host", Dom.attr Dom.Hooks.draftInput "ada"
                  "send button", Dom.attr Dom.Hooks.sendDraft "ada"
                  "conversation timeline", Dom.Hooks.conversation
                  // The sent body is rendered as formatted rich text (a paragraph here), not
                  // raw markdown text sitting directly under the hook.
                  "sent message in timeline", ">ship it</p>"
                  "agent streaming response", Dom.attr Dom.Hooks.messageStatus Dom.Text.streaming
                  "agent stream indicator", Dom.Hooks.agentStream
                  "active agent turn", Dom.Hooks.agentTurn
                  "last processed event offset", Dom.hookText Dom.Hooks.lastProcessedOffset "5"
                  "latest known event offset", Dom.hookText Dom.Hooks.latestKnownOffset "7"
                  "catch-up status", Dom.hookText Dom.Hooks.catchUp Dom.Text.catchingUp
                  "environment status (Phase 2)", Dom.Hooks.environment
                  "read-only command log (Phase 2)", Dom.Hooks.commandLog
                  "message queue (Phase 3)", Dom.Hooks.messageQueue
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

        // The timeline renders a sent body's Markdown as formatted rich text — the read-only
        // mirror of the composer — through the very SSR path the browser also runs. This pins
        // it in the cheap tier; the real-browser two-peer flow (Browser.fs) covers it live.
        testCase "a sent markdown body renders as formatted rich text in the timeline" <| fun () ->
            let richItem : ConversationItem =
                { MessageId = MessageId.create "msg-rich" |> expect
                  Author = HumanPeer ada
                  Body = "# Heading one\n\nText with **bold** and `code`.\n\n- item one\n- item two"
                  Status = Complete }
            let model =
                { representativeModel with
                    Conversation = { representativeModel.Conversation with Items = [ richItem ] } }
            let html = Support.render model
            let timeline =
                let start = html.IndexOf Dom.Hooks.conversation
                html.Substring (start, html.IndexOf ("</section>", start) - start)
            // Block/inline structure comes through as semantic elements…
            for label, marker in
                [ "heading text", ">Heading one</h1>"
                  "bold mark", ">bold</strong>"
                  "inline code", ">code</code>"
                  "bullet list", "<ul"
                  "list item", "<li"
                  "list item text", ">item one</p>" ] do
                Expect.isTrue (timeline.Contains marker) (sprintf "%s (`%s`) must render formatted" label marker)
            // …and the Markdown syntax itself is transformed away, never left as literal source.
            Expect.isFalse (timeline.Contains "# Heading one") "the heading '#' is not literal text"
            Expect.isFalse (timeline.Contains "**bold**") "the bold '**' is not literal text"
    ]

let tests =
    testList "Acceptance" [
        uiChecklistTests
    ]
