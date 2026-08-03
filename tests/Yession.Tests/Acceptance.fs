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
/// The key the fixture's draft will become when someone sends it — distinct from the entry
/// already queued, because a draft's key is one the queue does not hold yet.
let private draftQueueId = QueueId.create "queue-ui-draft" |> expect
let private ada = PeerId.create "ada" |> expect
let private bob = PeerId.create "bob" |> expect
let private sessionId = SessionId.create "demo-session" |> expect
let private terminalId = TerminalId.create "term-ui" |> expect
let private blockId = BlockId.create "block-ui" |> expect
let private terminalDraftQueueId = QueueId.create "queue-ui-term-draft" |> expect
let private terminalQueueId = QueueId.create "queue-ui-term" |> expect

/// A model exercising every UI element at once: connected, mid catch-up, one draft
/// (sendable), one queued message (editable/reorderable/deletable), a completed human
/// message, a streaming agent response, and a running agent turn.
let private representativeModel : ClientModel =
    let turnId = AgentTurnId.create "turn-ui" |> expect
    { Peer = { PeerId = ada; DisplayName = "swift-heron" }
      Connection = Connected
      Session = Some sessionId
      // Connected, so the reconnect offer (Plan 11) is not showing — but the origin is
      // present, which is the interesting case: the offer must be gated on the CONNECTION,
      // not merely on whether a Manager is known.
      Manager = Some "http://127.0.0.1:8321"
      // Path-mounted unless a case says otherwise: the address survives a restart.
      EphemeralStorage = false
      Synced =
        // Draft/queue bodies are rich-text `Y.XmlFragment`s mounted by the browser editor,
        // not fields on the model — so the SSR fixture carries only the slot's identity; the
        // checklist below asserts the mount *hosts* render (`data-rich-body`/`data-*-input`),
        // and the body-content rendering is a browser concern covered by the editor E2E.
        { Drafts = Map.ofList [ ada, { Author = ada; QueueId = draftQueueId } ]
          Queue = Map.ofList [ queueId, { QueueId = queueId; Author = ada; Order = 1.0 } ]
          Title = Ylmish.Text.ofString "planning the launch"
          SharedBrief = None
          // A terminal composer slot, and one queued command the AGENT wrote — which under
          // the default mode is the interesting case: it is the entry the approval gate
          // holds, so the panel must render it as waiting rather than as ready.
          TerminalDrafts =
            Map.ofList [ (terminalId, ada), { Terminal = terminalId; Author = ada; QueueId = terminalDraftQueueId } ]
          TerminalQueue =
            Map.ofList
                [ terminalQueueId,
                  { QueueId = terminalQueueId
                    Terminal = terminalId
                    Author = ActorRef.Agent
                    Order = 1.0
                    ApprovedBy = None } ]
          TerminalModes = Map.empty }
      Conversation =
        { Items =
            [ { MessageId = MessageId.create "msg-1" |> expect
                Author = PeerRef ada
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
          IsCatchingUp = true
          Feed = FeedLive }
      Agent = { ActiveTurn = Some turnId }
      Presence = Map.ofList [ bob, { DisplayName = "brave-owl"; Focus = { Field = Title; Pos = { Anchor = "AQI="; Head = "AwQ=" } } } ]
      // The roster names a draft's author even when they are not here: a label, never a peer id.
      Peers = Map.ofList [ ada, "swift-heron"; bob, "brave-owl" ]
      Composer = Unchosen
      Environment = EnvironmentNotStarted
      Commands = CommandLog.empty
      Terminals =
        { Terminals =
            [ { TerminalId = terminalId
                Title = "build"
                OpenedBy = PeerRef ada
                IsOpen = true
                ClosedReason = None
                Blocks =
                  [ { BlockId = blockId
                      Author = PeerRef ada
                      ApprovedBy = None
                      Command = "ls -la"
                      FromSeq = 0
                      ToSeq = Some 2
                      Status = BlockFinished (CommandSucceeded 0) } ]
                DroppedBytes = 0 } ] }
      // The transcript this client has: one coloured line, so the SSR render exercises the
      // ANSI path rather than only the plain one.
      TerminalFeeds =
        Map.ofList
            [ terminalId,
              { Records =
                  Map.ofList
                      [ 0, { At = 0.0; Kind = TranscriptInput; Data = "ls -la\n" }
                        1, { At = 0.1; Kind = TranscriptOutput; Data = "\u001b[32mtotal 0\u001b[0m\n" } ]
                KnownLength = 2
                ReadThrough = 2 } ]
      TerminalChoice = None
      TerminalsOpen = true
      Claude =
        { Status = { SessionCredential = None; MineCredential = None; AgentAvailable = Some false }
          Flow = ClaudeIdle } }

/// The composer when a PEER is the one writing: their draft is what you are in, yours (if any)
/// is a summary you can open, and "new message" is the way out of collaborating.
let private joinedComposerModel : ClientModel =
    { representativeModel with
        Synced =
            { representativeModel.Synced with
                Drafts =
                    Map.ofList
                        [ ada, { Author = ada; QueueId = draftQueueId }
                          bob, { Author = bob; QueueId = QueueId.create "queue-ui-bob" |> expect } ] }
        // Bob is writing, and his caret is in his own draft — the activity the summary shows.
        Presence =
            Map.ofList
                [ bob,
                  { DisplayName = "brave-owl"
                    Focus = { Field = DraftBody bob; Pos = { Anchor = "AQI="; Head = "AQI=" } } } ]
        Composer = Joined bob }

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
                  "queue delete", Dom.attr Dom.Hooks.queueDelete "queue-ui"
                  // Terminals (Plan 13): the panel, the terminal it is showing, the block
                  // that ran with its exit status, and the composer that queues the next
                  // command. The queued entry is the AGENT's, so it must render as waiting
                  // for an approval — that state is the whole point of the surface.
                  "terminals panel", Dom.Hooks.terminalPanel
                  "terminal tab", Dom.attr Dom.Hooks.terminalTab "term-ui"
                  "new terminal", Dom.Hooks.terminalNew
                  "terminal block", Dom.attr Dom.Hooks.terminalBlock "block-ui"
                  "terminal block status", Dom.attr Dom.Hooks.terminalBlockStatus Dom.Text.blockOk
                  "terminal block command", "ls -la"
                  "terminal output", Dom.Hooks.terminalOutput
                  // The output is ANSI-coloured, and the colour is a THEME token — a raw
                  // ANSI colour would not clear the contrast floor on this ground.
                  "terminal output colour is a theme token", "text-term-green"
                  "terminal output text", "total 0"
                  "queued terminal command", Dom.attr Dom.Hooks.terminalQueued "queue-ui-term"
                  "queued command awaits approval", Dom.attr Dom.Hooks.terminalQueuedStatus Dom.Text.queuedAwaitingApproval
                  "approve button", Dom.attr Dom.Hooks.terminalApprove "queue-ui-term"
                  "terminal composer input", Dom.attr Dom.Hooks.terminalInput "term-draft:term-ui:ada"
                  "terminal approval mode", Dom.attr Dom.Hooks.terminalMode "approve-agent"
                  // Settings + agent presence (Plan 08 pass): the model has no agent, so
                  // the sidebar row says absent, the prompt strip renders with its
                  // connect call-to-action, and the drawer holds the Claude panel.
                  "settings drawer toggle", Dom.Hooks.settingsToggle
                  "settings drawer panel", Dom.Hooks.settingsPanel
                  "claude panel in settings", Dom.Hooks.claudePanel
                  "agent presence row (absent)", Dom.attr Dom.Hooks.agentPresence "absent"
                  "no-agent prompt strip", Dom.Hooks.noAgent
                  "no-agent connect call-to-action", Dom.Hooks.noAgentConnect ]
            for label, marker in required do
                Expect.isTrue (html.Contains marker) (sprintf "%s (`%s`) must render" label marker)

        testCase "the random peer display name is human-readable" <| fun () ->
            let rng = Random 1234
            for _ in 1 .. 20 do
                let name = PeerName.random rng
                let parts = name.Split '-'
                Expect.equal parts.Length 2 "adjective-animal shape"
                Expect.isTrue (parts.[0].Length > 0 && parts.[1].Length > 0) "both halves non-empty"

        testCase "joining a peer's draft: theirs is the composer, yours is a summary, and either can be sent" <| fun () ->
            let html = Support.render joinedComposerModel
            let required =
                [ "the joined draft is the composer", Dom.attr Dom.Hooks.draftInput "bob"
                  "its author is named, not numbered", "brave-owl"
                  "anyone may send the draft they are in", Dom.attr Dom.Hooks.sendDraft "bob"
                  "your own draft collapses to a summary", Dom.attr Dom.Hooks.draftSummary "ada"
                  "a summary opens on click", Dom.attr Dom.Hooks.expandDraft "ada"
                  "the summary carries the body, clamped", Dom.attr "data-rich-body" (BodyKey.draft ada)
                  "who is editing it right now", Dom.attr Dom.Hooks.draftEditor' (PeerId.value bob)
                  "the way out of collaborating", Dom.Hooks.newDraft ]
            for label, needle in required do
                Expect.isTrue (html.Contains needle) (sprintf "%s (%s)" label needle)
            // Destruction stays the author's: you cannot discard a draft you merely joined.
            Expect.isFalse (html.Contains Dom.Hooks.discardDraft) "no discard on someone else's draft"
            // And there is exactly ONE editable body host: the composer, not the summaries.
            Expect.equal
                (html.Split "data-rich-readonly=\"false\"" |> Array.length |> (fun n -> n - 1))
                2
                "one editable draft body, plus the queued message's own editor"

        // The agent's absence is a call to action, and a call to action repeated three times
        // is wallpaper: it used to be a sidebar row, a strip over the composer, AND the
        // settings copy, all at once. It now lives where the session's members are listed.
        testCase "the agent's absence is asked for exactly once, where the session's members are" <| fun () ->
            let html = Support.render representativeModel // no agent in this model
            let occurrences (needle: string) = (html.Split needle |> Array.length) - 1
            Expect.equal (occurrences Dom.Hooks.noAgentConnect) 1 "one connect call-to-action, not several"
            // It sits in the membership section, which the shell renders before the timeline.
            Expect.isTrue
                (html.IndexOf Dom.Hooks.noAgentConnect < html.IndexOf Dom.Hooks.conversation)
                "the prompt is in the sidebar's membership section, not over the composer"
            // A session WITH an agent asks for nothing at all.
            let connected =
                { representativeModel with
                    Claude =
                        { representativeModel.Claude with
                            Status = { representativeModel.Claude.Status with AgentAvailable = Some true } } }
            let connectedHtml = Support.render connected
            Expect.isFalse (connectedHtml.Contains Dom.Hooks.noAgent) "nothing asks for a connection once there is one"

        // The timeline renders a sent body's Markdown as formatted rich text — the read-only
        // mirror of the composer — through the very SSR path the browser also runs. This pins
        // it in the cheap tier; the real-browser two-peer flow (Browser.fs) covers it live.
        testCase "a sent markdown body renders as formatted rich text in the timeline" <| fun () ->
            let richItem : ConversationItem =
                { MessageId = MessageId.create "msg-rich" |> expect
                  Author = PeerRef ada
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

// The offer to bring a stopped session back (Plan 11). It replaces the connection status
// word, so the thing to pin is WHEN it appears — a button with nowhere to go, or one shown
// over a session that is merely reconnecting, are both worse than the plain status.
let private reconnectOfferTests =
    testList "The reconnect offer" [
        let stopped (manager: string option) (session: SessionId option) =
            { representativeModel with
                Connection = Disconnected (Some "the session did not answer")
                Manager = manager
                Session = session }

        testCase "a settled disconnection with a manager and a session offers a way back" <| fun () ->
            let html = Support.render (stopped (Some "http://127.0.0.1:8321") (Some sessionId))
            Expect.isTrue (html.Contains Dom.Hooks.sessionGone) "the card renders"
            Expect.isTrue (html.Contains Dom.Text.reopenSession) "with its button"
            Expect.isTrue
                (html.Contains "http://127.0.0.1:8321/sessions/demo-session/open")
                "pointing at the manager's open route for THIS session"

        // Replaces, never accompanies: the status word and a button to fix it would be
        // saying the same thing twice.
        testCase "the offer replaces the connection status word" <| fun () ->
            let html = Support.render (stopped (Some "http://127.0.0.1:8321") (Some sessionId))
            // `data-connection-reason` has `data-connection` as a prefix, so this one
            // assertion covers both the status word and its separate reason line.
            Expect.isFalse (html.Contains Dom.Hooks.connection) "neither the status word nor its reason line"
            // The reason itself is not lost — it moves into the card's copy.
            Expect.isTrue (html.Contains "the session did not answer") "the reason still reaches the reader"

        // The three ways the offer must decline to render, each of which would otherwise be
        // a button that cannot work.
        testCase "no manager origin means no offer, just the status" <| fun () ->
            let html = Support.render (stopped None (Some sessionId))
            Expect.isFalse (html.Contains Dom.Hooks.sessionGone) "nothing to ask, so nothing offered"
            Expect.isTrue (html.Contains Dom.Hooks.connection) "the ordinary status still renders"

        testCase "no session id means no offer" <| fun () ->
            let html = Support.render (stopped (Some "http://127.0.0.1:8321") None)
            Expect.isFalse (html.Contains Dom.Hooks.sessionGone) "nothing to ask FOR"
            Expect.isTrue (html.Contains Dom.Hooks.connection) "the ordinary status still renders"

        testCase "a session that is merely reconnecting is not offered a reopen" <| fun () ->
            let html =
                Support.render
                    { representativeModel with
                        Connection = Reconnecting
                        Manager = Some "http://127.0.0.1:8321" }
            Expect.isFalse (html.Contains Dom.Hooks.sessionGone) "reconnecting is not stopped"

        // Plan 13: the card promises what the deployment can actually deliver. Nothing
        // asserted this sentence before, which is how it came to claim work was safe on a
        // deployment that strands it.
        testCase "a stable-address deployment promises the work comes back" <| fun () ->
            let html = Support.render (stopped (Some "http://127.0.0.1:8321") (Some sessionId))
            Expect.isTrue (html.Contains "saved here and syncs") "the promise is kept where it can be"
            Expect.isFalse (html.Contains "new address") "and no warning where none is due"

        testCase "an ephemeral-address deployment warns instead of promising" <| fun () ->
            let html =
                Support.render
                    { stopped (Some "http://127.0.0.1:8321") (Some sessionId) with EphemeralStorage = true }
            Expect.isTrue (html.Contains "reopens at a new address") "it says what reopening costs"
            Expect.isFalse (html.Contains "saved here and syncs") "and never both"

        // The banner and the card both render for a settled disconnection. The card is the
        // more specific message, so the banner must not restate the promise underneath it.
        testCase "the degraded banner does not repeat the promise while the offer shows" <| fun () ->
            let html = Support.render (stopped (Some "http://127.0.0.1:8321") (Some sessionId))
            Expect.equal
                ((html.Split "saved here and syncs" |> Array.length) - 1)
                1
                "stated once, by the card"
            Expect.isFalse (html.Contains Dom.Text.localFallback) "the banner's own promise stays out of it"

        testCase "a connected session shows no offer" <| fun () ->
            Expect.isFalse
                ((Support.render representativeModel).Contains Dom.Hooks.sessionGone)
                "connected, with a manager known — still nothing to offer"
    ]

// The bootstrap shell itself (`Ssr.page`), which had no Node-tier coverage. What matters
// here is the manager meta tag's ABSENCE rule: the client's offer is gated on the value
// being present, so a shell that emitted an empty one would turn a structural guarantee
// into a string check nobody wrote.
let private shellTests =
    testList "Bootstrap shell" [
        // Digests the shell merely carries into its asset URLs; this suite is about the
        // manager meta tag, so any pair does.
        let assets : AssetDigests = { Bundle = "testbundle01"; Css = "testcss0001" }

        let pageWith (managerOrigin: string option) (ephemeralStorage: bool) =
            Yession.Host.Ssr.page sessionId "" managerOrigin ephemeralStorage assets representativeModel

        let page (managerOrigin: string option) = pageWith managerOrigin false

        testCase "a manager origin is emitted as its meta tag" <| fun () ->
            Expect.isTrue
                ((page (Some "http://127.0.0.1:8321")).Contains
                    """<meta name="yession-manager" content="http://127.0.0.1:8321">""")
                "the origin rides the shell"

        testCase "no manager origin emits no tag at all — not an empty one" <| fun () ->
            let html = page None
            Expect.isFalse (html.Contains Dom.managerMetaName) "the tag is absent, so the client reads None"

        testCase "the session id is always there, so a client knows what it is before connecting" <| fun () ->
            Expect.isTrue
                ((page None).Contains (sprintf """<meta name="%s" content="demo-session">""" Dom.sessionMetaName))
                "session identity does not depend on having a manager"

        // The origin is operator-configured rather than user input, but it lands in an
        // attribute, and one escaper for every attribute is the rule.
        testCase "an origin containing a quote is escaped, not injected" <| fun () ->
            let html = page (Some "http://x\"onload=alert(1)")
            Expect.isFalse (html.Contains "\"onload=alert(1)") "the quote must not close the attribute"
            Expect.isTrue (html.Contains "&quot;onload=alert(1)") "it is escaped in place"

        // Plan 13. The tag says the one thing worth saying, and only when it is true, so a
        // path-mounted shell carries nothing about storage at all.
        testCase "an ephemeral-storage deployment marks its shell" <| fun () ->
            Expect.isTrue
                ((pageWith None true).Contains (sprintf """<meta name="%s" content="1">""" Dom.ephemeralStorageMetaName))
                "a deployment whose sessions move must say so"

        testCase "a stable-address deployment says nothing about storage" <| fun () ->
            Expect.isFalse
                ((pageWith None false).Contains Dom.ephemeralStorageMetaName)
                "absence is the good case, so the client reads false"
    ]

let tests =
    testList "Acceptance" [
        uiChecklistTests
        reconnectOfferTests
        shellTests
    ]
