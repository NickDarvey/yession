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
                    ApprovedBy = None
                    RejectedBy = None
                    RejectedReason = None } ]
          TerminalModes = Map.empty
          TerminalSizes = Map.empty }
      Conversation =
        { Items =
            [ { MessageId = MessageId.create "msg-1" |> expect
                Author = PeerRef ada
                Body = "ship it"
                Status = Complete
                Offset = EventOffset.create 1L |> expect }
              { MessageId = MessageId.create "msg-agent" |> expect
                Author = ActorRef.Agent
                Body = "Sounds go"
                Status = Streaming
                Offset = EventOffset.create 4L |> expect } ]
          ActiveAgentMessages = Map.ofList [ turnId, MessageId.create "msg-agent" |> expect ] }
      // The terminal half of the chat (Plan 14): the fixture's one block, anchored between
      // the two messages — so the checklist renders a chip in the middle of the conversation
      // rather than only at the end, which is the ordering the merge exists for.
      Timeline =
        { TimelineProjection.empty with
            TerminalItems = [ TimelineBlock (EventOffset.create 2L |> expect, terminalId, blockId) ] }
      EventConsumer =
        { LastProcessedOffset = Some (EventOffset.create 5L |> expect)
          LatestKnownOffset = Some (EventOffset.create 7L |> expect)
          IsCatchingUp = true
          // Long enough to be worth saying, so the catch-up status and its offsets are on
          // screen for the checklist. A brief one is deliberately silent (`CatchUpIsSlow`).
          CatchUpIsSlow = true
          Feed = FeedLive }
      Agent = { ActiveTurn = Some turnId }
      Presence = Map.ofList [ bob, { DisplayName = "brave-owl"; Focus = { Field = Title; Pos = { Anchor = "AQI="; Head = "AwQ=" } } } ]
      // The roster names a draft's author even when they are not here: a label, never a peer id.
      Peers = Map.ofList [ ada, "swift-heron"; bob, "brave-owl" ]
      Composer = Unchosen
      Environment = EnvironmentNotStarted
      Terminals =
        { Terminals =
            [ { TerminalId = terminalId
                Title = "build"
                OpenedBy = PeerRef ada
                IsOpen = true
                ClosedReason = None
                Lease = None
                IntegrationLost = false
                Blocks =
                  [ { BlockId = blockId
                      QueueId = None
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
                ReadThrough = 2
                Header = Some { Width = 80; Height = 24; Timestamp = 0L } } ]
      TerminalKeyframes = Map.empty
      TerminalScreens = Map.empty
      PaneTabs = []
      PaneChoice = None
      PaneStartAt = None
      PaneRewound = None
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

/// The same session with bob typing in the terminal (Plan 13, stage 2e) — live mode as every
/// OTHER peer sees it, which is the case the lease bar exists for.
let private leasedTerminalModel : ClientModel =
    { representativeModel with
        Terminals =
            { Terminals =
                representativeModel.Terminals.Terminals
                |> List.map (fun t -> { t with Lease = Some (PeerRef bob) }) }
        // The screen this client composed from the Process's snapshot and the records since
        // (Plan 14, stage 6). Coloured, so the render exercises the ANSI path.
        TerminalScreens = Map.ofList [ terminalId, "\u001b[32mvim ~/notes\u001b[0m" ] }

/// The same terminal, held by THIS peer: the one copy of the screen that takes keystrokes.
let private heldTerminalModel : ClientModel =
    { leasedTerminalModel with
        Terminals =
            { Terminals =
                leasedTerminalModel.Terminals.Terminals
                |> List.map (fun t -> { t with Lease = Some (PeerRef ada) }) } }

/// The same session with the terminal's shell no longer marking (Plan 13, stage 2f). The
/// queued command is a PEER's, so it needs no approval — otherwise the approval hold would
/// be the one reported, which is the correct precedence and not what this case is about.
let private lostIntegrationModel : ClientModel =
    { representativeModel with
        Synced =
            { representativeModel.Synced with
                TerminalQueue =
                    representativeModel.Synced.TerminalQueue
                    |> Map.map (fun _ entry -> { entry with Author = PeerRef ada }) }
        Terminals =
            { Terminals =
                representativeModel.Terminals.Terminals
                |> List.map (fun t -> { t with IntegrationLost = true }) } }

/// The same session after the terminal has closed (Plan 13, stage 3e) — the audit read. The
/// choice is explicit because that is the real flow: the panel LANDS on a live terminal, and
/// a closed one is somewhere you go on purpose.
let private closedTerminalModel : ClientModel =
    { representativeModel with
        Terminals =
            { Terminals =
                representativeModel.Terminals.Terminals
                |> List.map (fun t -> { t with IsOpen = false; ClosedReason = Some "closed by a peer" }) }
        PaneChoice = Some (TerminalTab terminalId) }

/// …and after the per-terminal output cap ate its recording (stage 3d): the blocks survive
/// in the projection, the transcript does not, and the byte count is the only trace of what
/// it held.
let private forgottenTerminalModel : ClientModel =
    { closedTerminalModel with
        Terminals =
            { Terminals =
                closedTerminalModel.Terminals.Terminals |> List.map (fun t -> { t with DroppedBytes = 4096 }) }
        TerminalFeeds = Map.empty }

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
                  // Terminal work in the CHAT (Plan 14): the block that ran has a chip where
                  // it ran, carrying who ran it and how it went — and no output, which is the
                  // whole reason it is a chip.
                  "block chip in the chat", Dom.attr Dom.Hooks.chatBlock "block-ui"
                  "chip carries the block's status", Dom.attr Dom.Hooks.chatBlockStatus Dom.Text.blockOk
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

        testCase "live mode: the lease bar names the holder and offers the steal" <| fun () ->
            let html = Support.render leasedTerminalModel
            let required =
                [ "the lease bar names who holds it", Dom.attr Dom.Hooks.terminalLease (PeerId.value bob)
                  // Any peer may take it, so the control is offered rather than gated —
                  // collaborators are trusted, and the event log is what makes a steal safe.
                  "the steal control", Dom.attr Dom.Hooks.terminalTake (TerminalId.value terminalId)
                  // The queue survives live mode: an entry queued now runs the moment the
                  // terminal comes back, and it says which of the two holds it is under.
                  "the queue is still there", Dom.attr Dom.Hooks.terminalQueued "queue-ui-term" ]
            for label, marker in required do
                Expect.isTrue (html.Contains marker) (sprintf "%s (`%s`) must render" label marker)
            // The command line is gone, not disabled: a box marked "Run" that cannot run
            // anything is the misleading half of live mode.
            Expect.isFalse
                (html.Contains (Dom.attr Dom.Hooks.terminalInput (BodyKey.terminalDraft terminalId ada)))
                "the composer's own command line gives way to the bar"

        testCase "a queued command in a leased terminal says it waits for the TERMINAL" <| fun () ->
            // Not "waiting for approval": one resolves when a person makes a decision, the
            // other when a person finishes a task, and a queue that said only *pending* would
            // leave both looking like a stall.
            let model =
                { leasedTerminalModel with
                    Synced =
                        { leasedTerminalModel.Synced with
                            TerminalQueue =
                                leasedTerminalModel.Synced.TerminalQueue
                                |> Map.map (fun _ entry -> { entry with Author = PeerRef ada }) } }
            let html = Support.render model
            Expect.isTrue
                (html.Contains (Dom.attr Dom.Hooks.terminalQueuedStatus Dom.Text.queuedAwaitingTerminal))
                "the hold names the terminal"
            Expect.isFalse
                (html.Contains (Dom.attr Dom.Hooks.terminalQueuedStatus Dom.Text.queuedAwaitingApproval))
                "and not an approval it does not need"

        testCase "a terminal that stopped marking says so, and offers the repair" <| fun () ->
            // Named, not shown as a stall. The queue really is held, and a surface that only
            // showed "pending" would be indistinguishable from a bug.
            let html = Support.render lostIntegrationModel
            for label, marker in
                [ "the state is named", Dom.attr Dom.Hooks.terminalLost (TerminalId.value terminalId)
                  "with the control that repairs it", Dom.attr Dom.Hooks.terminalRearm (TerminalId.value terminalId)
                  // ...and the held command says WHICH hold it is under: this one ends when
                  // somebody re-arms the terminal, not when a person finishes a task.
                  "the held command names this hold",
                  Dom.attr Dom.Hooks.terminalQueuedStatus Dom.Text.queuedAwaitingIntegration ] do
                Expect.isTrue (html.Contains marker) (sprintf "%s (`%s`) must render" label marker)

        testCase "a closed terminal is reachable, and shows the recording rather than a composer" <| fun () ->
            let html = Support.render closedTerminalModel
            for label, marker in
                [ "a closed terminal has a tab of its own",
                  Dom.attr Dom.Hooks.terminalClosedTab (TerminalId.value terminalId)
                  "and the mount the player attaches to",
                  Dom.attr Dom.Hooks.paneReplay (PaneTab.key (TerminalTab terminalId))
                  // The blocks are still the other half of the read: what ran, beside how it
                  // behaved.
                  "the commands it ran are still listed", Dom.attr Dom.Hooks.terminalBlock "block-ui" ] do
                Expect.isTrue (html.Contains marker) (sprintf "%s (`%s`) must render" label marker)
            // Nothing can be run in a closed terminal, so nothing offers to: a command line
            // that queues into a terminal with no shell behind it is the misleading half.
            Expect.isFalse
                (html.Contains (Dom.attr Dom.Hooks.terminalInput (BodyKey.terminalDraft terminalId ada)))
                "no command line"
            Expect.isFalse
                (html.Contains (Dom.attr "data-terminal-close" (TerminalId.value terminalId)))
                "and no offer to close what is already closed"

        testCase "terminal work sits in the chat WHERE it happened, not at the end" <| fun () ->
            // Plan 14, stage 1. The fixture's block is anchored at offset 2, between the two
            // messages at 1 and 4 — so the merge has to put it there. Appending it after
            // everything said would be the thing the offset exists to prevent.
            let html = Support.render representativeModel
            let indexOf (needle: string) = html.IndexOf needle
            let said = indexOf (Dom.attr Dom.Hooks.messageId "msg-1")
            let ran = indexOf (Dom.attr Dom.Hooks.chatBlock "block-ui")
            let answered = indexOf (Dom.attr Dom.Hooks.messageId "msg-agent")
            Expect.isTrue (said >= 0 && ran >= 0 && answered >= 0) "all three render"
            Expect.isTrue (said < ran && ran < answered) "said, ran, answered — in log order"

        testCase "a chip is one line: who ran what, and how it went — never the output" <| fun () ->
            // Output inline would make the chat noisiest exactly when it is busiest, and
            // would put everything a command printed one glance from everyone in the session
            // rather than one tap. The panel is where output lives.
            let html = Support.render representativeModel
            let chat =
                let start = html.IndexOf Dom.Hooks.conversation
                html.Substring (start, html.IndexOf ("</section>", start) - start)
            Expect.isTrue (chat.Contains (Dom.attr Dom.Hooks.chatBlock "block-ui")) "the chip is there"
            Expect.isTrue (chat.Contains "ls -la") "with the command it ran"
            Expect.isFalse (chat.Contains Dom.Hooks.terminalOutput) "and nothing it printed"
            // The panel is where output lives, and it still does.
            Expect.isTrue (html.Contains Dom.Hooks.terminalOutput) "the terminal panel is unchanged"

        testCase "a concluded lease stretch is its own item, and says how it ended" <| fun () ->
            let stretchModel =
                { representativeModel with
                    Timeline =
                        { representativeModel.Timeline with
                            TerminalItems =
                                representativeModel.Timeline.TerminalItems
                                @ [ TimelineStretch
                                        { Offset = EventOffset.create 3L |> expect
                                          TerminalId = terminalId
                                          Title = "build"
                                          Holder = PeerRef bob
                                          End = LeaseStolen (PeerRef ada)
                                          Range = Some (2, 40)
                                          StartedAt = DateTimeOffset (2026, 8, 8, 0, 0, 0, TimeSpan.Zero)
                                          EndedAt = DateTimeOffset (2026, 8, 8, 0, 2, 0, TimeSpan.Zero) } ] } }
            let html = Support.render stretchModel
            let required =
                [ "the stretch item", Dom.attr Dom.Hooks.chatStretch "term-ui@3"
                  // Four endings, four answers: "did they finish, get taken over, drop out,
                  // or wander off?" is not one question.
                  "how it ended", Dom.attr Dom.Hooks.chatStretchEnd Dom.Text.stretchStolen
                  "who held it", "bob"
                  "where", "build"
                  "for how long", "2m 0s" ]
            for label, marker in required do
                Expect.isTrue (html.Contains marker) (sprintf "%s (`%s`) must render" label marker)

        testCase "live mode shows the SCREEN, and every peer sees the same one" <| fun () ->
            // Plan 14, stage 6. A program is running here, and what it displays is a
            // projection of what it emitted — the block history is block mode's view of a
            // terminal, and it comes back the moment the lease does.
            let watching = Support.render leasedTerminalModel
            Expect.isTrue
                (watching.Contains (Dom.attr Dom.Hooks.terminalScreen (TerminalId.value terminalId)))
                "the screen renders for a peer who is only watching"
            Expect.isTrue (watching.Contains "vim ~/notes") "with what the program drew"
            // ANSI through the same theme tokens a block's output uses — a raw ANSI colour
            // would not clear the contrast floor on this ground.
            Expect.isTrue (watching.Contains "text-term-green") "coloured by the theme, not by the escape"
            Expect.isFalse
                (watching.Contains (Dom.attr Dom.Hooks.terminalBlock (BlockId.value blockId)))
                "and the block history gives way to it"
            // Watching is not a lesser mode; it is the ordinary one. What the holder gets in
            // addition is the keyboard.
            Expect.isFalse (watching.Contains "role=\"application\"") "a watcher's screen takes no keystrokes"
            let held = Support.render heldTerminalModel
            Expect.isTrue (held.Contains "role=\"application\"") "the holder's does"
            Expect.isTrue (held.Contains "tabindex=\"0\"") "and it is a Tab stop, so a keyboard can reach it"

        testCase "a recording the cap ate is a STATED gap, not an empty player" <| fun () ->
            // The one place stage 3d's behaviour reaches the surface. An empty player is
            // indistinguishable from a terminal that printed nothing, and the whole reason
            // the drop is recorded is that a hole in an audit trail must be a stated fact.
            let html = Support.render forgottenTerminalModel
            Expect.isTrue
                (html.Contains (Dom.attr Dom.Hooks.terminalReplayGone (TerminalId.value terminalId)))
                "the gap is named"
            Expect.isFalse
                (html.Contains (Dom.attr Dom.Hooks.paneReplay (PaneTab.key (TerminalTab terminalId))))
                "and no player is mounted over nothing"

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
                  Status = Complete
                  Offset = EventOffset.create 1L |> expect }
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

// Where everyone IS. Presence already drove per-field overlays, but each of those is only
// visible from inside the surface it is about — so the thing pinned here is that a peer is
// findable from OUTSIDE it: the roster names what they are doing, and a terminal's tab shows
// who is in it whether or not that terminal is the one on screen.
let private presenceTests =
    testList "Peer presence" [
        let withBobIn (field: FocusField) =
            { representativeModel with
                Presence =
                    Map.ofList [ bob, { DisplayName = "brave-owl"; Focus = { Field = field; Pos = { Anchor = "AQI="; Head = "AQI=" } } } ] }

        testCase "a peer is in the roster with where they are" <| fun () ->
            let html = Support.render (withBobIn Title)
            Expect.isTrue (html.Contains (Dom.attr Dom.Hooks.peerPresence "bob")) "the peer has a roster row"
            Expect.isTrue
                (html.Contains (Dom.hookText (Dom.attr Dom.Hooks.peerAt Dom.Text.atTitle) Dom.Text.renamingSession))
                "and it says they are renaming the session"

        testCase "a peer writing their own message reads differently from one in yours" <| fun () ->
            let own = Support.render (withBobIn (DraftBody bob))
            Expect.isTrue (own.Contains (Dom.hookText (Dom.attr Dom.Hooks.peerAt Dom.Text.atDraft) Dom.Text.writing)) "their own draft is 'writing'"
            let mine = Support.render (withBobIn (DraftBody ada))
            Expect.isTrue
                (mine.Contains (Dom.hookText (Dom.attr Dom.Hooks.peerAt Dom.Text.atDraft) Dom.Text.inYourDraft))
                "being in the LOCAL peer's draft is said as yours, not as a name"

        testCase "a peer in a terminal is named by the terminal they are in" <| fun () ->
            let html = Support.render (withBobIn (TerminalDraftBody (terminalId, bob)))
            Expect.isTrue
                (html.Contains (Dom.hookText (Dom.attr Dom.Hooks.peerAt Dom.Text.atTerminal) (Dom.Text.inTerminal "build")))
                "the roster names the terminal, not just 'a terminal'"
            Expect.isTrue
                (html.Contains (Dom.attr Dom.Hooks.terminalTabPeer "bob"))
                "and the terminal's own tab carries their mark"

        // A queued command names only its entry; the entry names the terminal. That join is
        // the one thing that could quietly return "somewhere" instead of a name.
        testCase "a peer editing a queued command is still placed in its terminal" <| fun () ->
            let html = Support.render (withBobIn (TerminalQueuedBody terminalQueueId))
            Expect.isTrue
                (html.Contains (Dom.hookText (Dom.attr Dom.Hooks.peerAt Dom.Text.atTerminalQueued) (Dom.Text.inTerminal "build")))
                "the queued command's terminal is resolved through the entry"
            Expect.isTrue (html.Contains (Dom.attr Dom.Hooks.terminalTabPeer "bob")) "and shows on that terminal's tab"

        // Presence is who is here NOW. `Peers` deliberately keeps the departed so a draft's
        // author still has a name — reporting them as present would make the roster a
        // guest book.
        testCase "a peer with no live caret is not reported as being anywhere" <| fun () ->
            let html = Support.render { representativeModel with Presence = Map.empty }
            Expect.isFalse (html.Contains Dom.Hooks.peerPresence) "nobody else is claimed to be here"
            Expect.isTrue (html.Contains "swift-heron") "the local peer's own row is untouched"

        testCase "the local peer never appears as their own collaborator" <| fun () ->
            let html = Support.render (withBobIn Title)
            Expect.isFalse (html.Contains (Dom.attr Dom.Hooks.peerPresence "ada")) "you are 'you', not a peer row"
    ]

// Sync status, said ONCE. Both halves of this used to be wrong at the same time: "up to date"
// appeared in the header AND the sidebar, and the catch-up that replaces it flickered on
// every send (a client is behind its own event for one round trip).
let private syncStatusTests =
    testList "Sync status" [
        let settled =
            { representativeModel with
                EventConsumer =
                    { representativeModel.EventConsumer with
                        LatestKnownOffset = representativeModel.EventConsumer.LastProcessedOffset
                        IsCatchingUp = false
                        CatchUpIsSlow = false } }

        // Case-INSENSITIVE on purpose: the two reports differed in their capitals (one used
        // the shared token, the other a literal), so a case-sensitive count saw one of each
        // and called it fine.
        testCase "'up to date' is said exactly once on the screen" <| fun () ->
            let html = (Support.render settled).ToLowerInvariant ()
            let rec count (from: int) (n: int) =
                match html.IndexOf ("up to date", from) with
                | -1 -> n
                | i -> count (i + 1) (n + 1)
            Expect.equal (count 0 0) 1 "one report of a healthy sync, not one per surface"

        testCase "a brief catch-up says nothing at all" <| fun () ->
            let html = Support.render { representativeModel with EventConsumer = { representativeModel.EventConsumer with CatchUpIsSlow = false } }
            Expect.isFalse (html.Contains Dom.Hooks.catchUp) "the sidebar line stays put"
            Expect.isFalse (html.Contains "catching up") "and the header keeps saying what it was saying"

        testCase "a catch-up worth waiting on is reported, with its progress" <| fun () ->
            let html = Support.render representativeModel
            Expect.isTrue (html.Contains (Dom.hookText Dom.Hooks.catchUp Dom.Text.catchingUp)) "the sidebar names it"
            Expect.isTrue (html.Contains "catching up") "and so does the header"
            Expect.isTrue (html.Contains Dom.Hooks.lastProcessedOffset) "with how far it has got"

        // The flag describes a catch-up that is RUNNING, so it cannot outlive one: a timer
        // that fires just as the page lands must not leave a status nothing can clear.
        testCase "'slow' cannot be claimed once there is nothing left to catch up on" <| fun () ->
            let model = ClientModel.update (CatchUpSlowMsg true) settled
            Expect.isFalse model.EventConsumer.CatchUpIsSlow "a late timer is refused, not stored"
            Expect.isFalse ((Support.render model).Contains Dom.Hooks.catchUp) "and nothing is shown"
    ]

// The chrome's shared vocabulary (`Style.Stroke` and the phrases over it), asserted where it
// is observable: the rendered markup. These are invariants about EVERY control of a kind, so
// they catch the next surface that invents its own field or forgets a focus state — which is
// exactly how the inputs drifted apart in the first place.
let private chromeTests =
    testList "Chrome consistency" [
        /// The `class="…"` of every tag whose name is in `names`.
        let classesOf (names: string list) (html: string) : string list =
            let rec collect (from: int) (acc: string list) =
                let starts =
                    names
                    |> List.choose (fun name ->
                        match html.IndexOf ("<" + name, from) with
                        | -1 -> None
                        | i -> Some i)
                match starts with
                | [] -> List.rev acc
                | starts ->
                    let start = List.min starts
                    let tagEnd = html.IndexOf ('>', start)
                    let tag = html.Substring (start, tagEnd - start)
                    let classes =
                        match tag.IndexOf "class=\"" with
                        | -1 -> ""
                        | i ->
                            let from = i + 7
                            tag.Substring (from, tag.IndexOf ('"', from) - from)
                    collect (tagEnd + 1) (classes :: acc)
            collect 0 []

        let shell = Support.render representativeModel
        let settingsShell = Support.render { representativeModel with Claude = { representativeModel.Claude with Flow = ClaudeAwaitingCode ("https://claude.ai/auth", "mine") } }

        // Every input either wears the ONE field face (a ring that goes blue on focus) or
        // wears nothing at all, because the row around it carries the stroke. What is ruled
        // out is the third thing: a control that invents its own border, or one that draws a
        // box with no focus state.
        testCase "every input draws the one field face, or draws nothing" <| fun () ->
            for classes in classesOf [ "input"; "select"; "textarea" ] (shell + settingsShell) do
                let isField = classes.Contains "focus:border-blue"
                let isBare = classes.Contains "border-0"
                Expect.isTrue (isField || isBare) (sprintf "an input is neither the field face nor bare: %s" classes)

        // Every pressable thing has a visible keyboard focus state (AGENTS.md's UI baseline).
        // The failure this pins is silent by nature: a control with `outline-2` and no
        // `outline` draws nothing, and you only find out with a keyboard.
        testCase "every button and link declares a visible focus ring" <| fun () ->
            for classes in classesOf [ "button"; "a " ] (shell + settingsShell) do
                Expect.isTrue
                    (classes.Contains "focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue")
                    (sprintf "a control has no visible focus ring: %s" classes)

        // Blue is interactive, and focus is the most interactive a thing gets. A field that
        // went green (or anything else) on focus would make the same event mean two things.
        testCase "focus is blue, everywhere" <| fun () ->
            for tone in [ "focus:border-green"; "focus:border-ink"; "focus:border-err"
                          "focus-within:border-green"; "focus-within:border-ink" ] do
                Expect.isFalse ((shell + settingsShell).Contains tone) (sprintf "focus must not be %s" tone)
    ]

let tests =
    testList "Acceptance" [
        uiChecklistTests
        presenceTests
        syncStatusTests
        chromeTests
        reconnectOfferTests
        shellTests
    ]
