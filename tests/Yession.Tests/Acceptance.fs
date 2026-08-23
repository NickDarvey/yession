module Yession.Tests.Acceptance

// Step 09 — the Phase 1 acceptance gate's own checks. The seven required E2E scenarios
// and the model/protocol invariants live in their step suites (Sync/Agent/E2E/Client/
// Domain/SessionProcess — every E2E-N is named in its test title); this file pins the
// remaining acceptance items: the UI checklist, rendered from one representative model,
// and the random peer display name.
//
// The `(E2E-N)` suffixes are the phase acceptance gates' scenario numbers, and they are
// numbered PER PHASE — Phase 1's E2E-1 is the two-client draft, Phase 2's is the
// conversational one-shot. The gate documents that held both lists are gone; the titles are
// the index now, so a scenario's number means nothing without the phase beside it.

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
/// The model this representative session has picked, and the catalogue it picked from.
/// Ids and labels a provider would give, so the picker renders the case that matters —
/// something is chosen, and the list it came from is on screen with it.
let private pickedModel = ModelId.create "example-model-large" |> expect
let private offeredModels =
    [ AgentModel.create pickedModel "Example Large"
      AgentModel.create (ModelId.create "example-model-small" |> expect) "Example Small" ]

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
      // A representative client is one that can keep what it is given; the case that cannot
      // is the exception, and says so where it matters.
      CanKeepHistory = true
      // A representative client has been to look; the timeline it renders is the session's,
      // not a placeholder for one it has not read.
      HistoryRead = true
      Synced =
        // Draft/queue bodies are rich-text `Y.XmlFragment`s mounted by the browser editor,
        // not fields on the model — so the SSR fixture carries only the slot's identity; the
        // checklist below asserts the mount *hosts* render (`data-rich-body`/`data-*-input`),
        // and the body-content rendering is a browser concern covered by the editor E2E.
        { Drafts = Map.ofList [ ada, { Author = ada; QueueId = draftQueueId } ]
          Queue = Map.ofList [ queueId, { QueueId = queueId; Author = ada; Order = 1.0 } ]
          Title = Ylmish.Text.ofString "planning the launch"
          SharedBrief = None
          // A terminal composer slot, and one queued command the AGENT wrote.
          TerminalDrafts =
            Map.ofList [ (terminalId, ada), { Terminal = terminalId; Author = ada; QueueId = terminalDraftQueueId } ]
          Pending =
            Map.ofList
                [ terminalQueueId,
                  { QueueId = terminalQueueId
                    Terminal = terminalId
                    // What the product actually writes for an agent command: the agent acts,
                    // on the turn human's authority. There is no other agent-shaped way to
                    // build one.
                    Authority = Authority.agentFor (PeerRef ada)
                    Order = 1.0
                    Background = false } ]
          Model = Some pickedModel
          TerminalSizes = Map.empty }
      Conversation =
        { Items =
            [ { MessageId = MessageId.create "msg-1" |> expect
                Author = PeerRef ada
                Body = "ship it"
                Status = Complete
                Kind = ConversationItemKind.Message
                Offset = EventOffset.create 1L |> expect
                Woke = None }
              { MessageId = MessageId.create "msg-agent" |> expect
                Author = ActorRef.Agent
                Body = "Sounds go"
                Status = Streaming
                Kind = ConversationItemKind.Message
                Offset = EventOffset.create 4L |> expect
                Woke = None } ]
          ActiveAgentMessages = Map.ofList [ turnId, MessageId.create "msg-agent" |> expect ]
          WokenTurn = None }
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
          Feed = FeedLive
          MissingBefore = None }
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
                Sandbox = Some SandboxName.defaultName
                Renewable = false
                IsOpen = true
                ClosedReason = None
                Lease = None
                IntegrationLost = false
                Blocks =
                  [ { BlockId = blockId
                      QueueId = None
                      Authority = Authority.ofAuthor (PeerRef ada)
                      Command = "ls -la"
                      Background = false
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
      Pins = []
      Pane = None
      TerminalsOpen = true
      // The pane shows a TAB by default; the list is what the cases below turn on.
      Claude =
        { Status = { SessionCredential = None; MineCredential = None; Owner = None; AgentAvailable = Some false }
          Flow = ClaudeIdle }
      GitHub =
        { Status = { SessionCredential = None; MineCredential = None }
          Flow = GitHubIdle }
      Models = ModelsLoaded offeredModels
      // The generated read surface (Plan 15), with all three shapes declared at once, so
      // the acceptance render exercises the ONE renderer every future query goes through
      // rather than the one shape today's queries happen to use.
      Queries =
        { Declared =
            [ { Name = QueryName.create "repos" |> expect
                Title = "repos"
                Description = "the session's checkouts"
                Shape =
                  Rows
                      [ QueryColumn.create "repo" "repo"
                        QueryColumn.create "branch" "branch"
                        QueryColumn.create "dirty" "uncommitted changes" ] }
              { Name = QueryName.create "work_environment" |> expect
                Title = "work environment"
                Description = "where commands run"
                Shape = Fields [ QueryColumn.create "backend" "backend"; QueryColumn.create "state" "state" ] }
              // Declared but not yet answered: the surface must render a section for a
              // query whose first value has not arrived, or a slow query is an empty gap
              // rather than a thing that is loading.
              { Name = QueryName.create "leases" |> expect
                Title = "leases"
                Description = "devices leased to this session"
                Shape = Value } ]
          Values =
            Map.ofList
                [ "repos",
                  RowsOf
                      [ [ "repo", CellText "octo/hello"
                          "branch", CellText "main"
                          "dirty", CellFlag true ] ]
                  "work_environment",
                  FieldsOf [ "backend", CellText "srt"; "state", CellText "running" ] ] } }

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
/// queued command is a PEER's.
let private lostIntegrationModel : ClientModel =
    { representativeModel with
        Synced =
            { representativeModel.Synced with
                Pending =
                    representativeModel.Synced.Pending
                    |> Map.map (fun _ entry -> { entry with Authority = Authority.ofAuthor (PeerRef ada) }) }
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
        Pane = Some (OnTab (Reading (TerminalTab terminalId))) }

/// A closed terminal whose bytes came from a provider that said its stream can be asked for
/// again (Plan 19, step 4) — the one case where a closed terminal has a way back.
let private renewableTerminalModel : ClientModel =
    { closedTerminalModel with
        Terminals =
            { Terminals =
                closedTerminalModel.Terminals.Terminals
                |> List.map (fun t -> { t with Sandbox = None; Renewable = true }) } }

/// …and after the per-terminal output cap ate its recording (stage 3d): the blocks survive
/// in the projection, the transcript does not, and the byte count is the only trace of what
/// it held.
let private forgottenTerminalModel : ClientModel =
    { closedTerminalModel with
        Terminals =
            { Terminals =
                closedTerminalModel.Terminals.Terminals |> List.map (fun t -> { t with DroppedBytes = 4096 }) }
        TerminalFeeds = Map.empty }

/// The buttons in a rendered page that a screen reader would announce as nothing but
/// "button": no text between the tags once markup is stripped, and no `aria-label` /
/// `aria-labelledby` on the tag. Returns their open tags, so a failure names the offender.
///
/// Hand-scanned rather than matched with a `Regex`, because this file runs on BOTH runtimes
/// and string indexing is the one thing that behaves identically on each.
let private namelessButtons (html: string) : string list =
    let visibleText (inner: string) =
        // What is left after every tag and comment: an inline SVG contributes nothing, which
        // is exactly the case this test exists to catch. Angle brackets nest (a self-closing
        // `<path/>` opens and closes; so does a `<!--lit-part-->` marker), so depth-count
        // rather than assume one level.
        let mutable depth = 0
        let kept = System.Text.StringBuilder ()
        for ch in inner do
            if ch = '<' then depth <- depth + 1
            elif ch = '>' then depth <- max 0 (depth - 1)
            elif depth = 0 then kept.Append ch |> ignore
        kept.ToString().Trim ()
    let rec scan (from: int) (found: string list) =
        let start = html.IndexOf ("<button", from)
        if start < 0 then List.rev found
        else
            let openEnd = html.IndexOf (">", start)
            let closeAt = html.IndexOf ("</button>", start)
            if openEnd < 0 || closeAt < 0 || closeAt < openEnd then List.rev found
            else
                let tag = html.Substring (start, openEnd - start)
                let named =
                    tag.Contains "aria-label=" || tag.Contains "aria-labelledby="
                    || visibleText (html.Substring (openEnd + 1, closeAt - openEnd - 1)) <> ""
                scan (closeAt + 9) (if named then found else tag :: found)
    scan 0 []

let private uiChecklistTests =
    testList "UI checklist" [
        // Pinned ONCE, over the whole shell, rather than remembered at each control: the
        // verbs at the edge of a field are glyphs now (run, send, discard, the strip's `+`),
        // which is the right shape for a verb you meet mid-sentence and the wrong shape for
        // anyone who cannot see it. A label that is a picture is not a label, and the failure
        // is silent — the control still works, still takes focus, and still announces itself
        // as "button", saying nothing about which one.
        testCase "no control is announced as nothing but \"button\"" <| fun () ->
            let offenders =
                [ representativeModel; joinedComposerModel; leasedTerminalModel; closedTerminalModel ]
                |> List.collect (Support.render >> namelessButtons)
            Expect.equal
                offenders
                []
                (sprintf "every button needs a name, from its text or aria-label — these have neither: %s"
                    (String.concat " | " offenders))

        testCase "every required Phase 1 UI element renders from the model" <| fun () ->
            let html = Support.render representativeModel
            let required =
                [ "session connection status", Dom.Hooks.connection
                  "connection state value", Dom.attr Dom.Hooks.connection Dom.Text.connected
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
                  // command.
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
                  "terminal composer input", Dom.attr Dom.Hooks.terminalInput "term-draft:term-ui:ada"
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
                  // The model picker, showing the session's choice: the CONTROL's own
                  // attribute, so what a person reads and what the register holds are one
                  // thing rather than two that could disagree.
                  "model picker", Dom.attr Dom.Hooks.modelSelect (ModelId.value pickedModel)
                  "agent presence row (absent)", Dom.attr Dom.Hooks.agentPresence "absent"
                  "no-agent prompt strip", Dom.Hooks.noAgent
                  "no-agent connect call-to-action", Dom.Hooks.noAgentConnect ]
            for label, marker in required do
                Expect.isTrue (html.Contains marker) (sprintf "%s (`%s`) must render" label marker)

        // --- a credential that stopped working ------------------------------------------
        // Three surfaces, one fact. Each is asserted where it renders rather than against
        // the whole page, because a whole-page render contains every surface at once and a
        // bare `Contains` would be satisfied by whichever one happened to be right.

        /// The representative client, with one connection needing a sign-in.
        let private' (provider: string) (reason: string) =
            let needing = Some { Kind = "static"; SignInRequired = Some reason }
            match provider with
            | "claude" ->
                { representativeModel with
                    Claude =
                        { representativeModel.Claude with
                            Status =
                                { representativeModel.Claude.Status with
                                    MineCredential = needing
                                    AgentAvailable = Some true } } }
            | _ ->
                { representativeModel with
                    GitHub = { representativeModel.GitHub with Status = { representativeModel.GitHub.Status with MineCredential = needing } } }

        // The one derivation every surface reads, so they cannot disagree about whether
        // anything is wrong. Ordered, not a map's iteration: what the prompt names first
        // must not change between renders of an unchanged model.
        testCase "what needs signing in is derived once, in a settled order" <| fun () ->
            let needing reason = Some { Kind = "static"; SignInRequired = Some reason }
            let both =
                { representativeModel with
                    Claude =
                        { representativeModel.Claude with
                            Status = { representativeModel.Claude.Status with MineCredential = needing "claude said no" } }
                    GitHub =
                        { representativeModel.GitHub with
                            Status = { representativeModel.GitHub.Status with MineCredential = needing "github said no" } } }
            Expect.equal
                (ClientModel.signInRequired both)
                [ "claude", "claude said no"; "github", "github said no" ]
                "both, and always in this order"
            Expect.isEmpty
                (ClientModel.signInRequired representativeModel)
                "and nothing at all when every connection is fine"

        testCase "a panel row for a credential that stopped working says so, and says why" <| fun () ->
            let html = Support.render (private' "github" "github rejected this credential")
            Expect.isTrue
                (html.Contains (Dom.attr Dom.Hooks.githubSignInRequired "mine"))
                "the row carries the fault on the scope it is held under"
            Expect.isTrue
                (html.Contains "github rejected this credential")
                "with the provider's own words, which a person could not have guessed"

        testCase "a healthy credential's row says nothing about signing in" <| fun () ->
            let healthy =
                { representativeModel with
                    GitHub =
                        { representativeModel.GitHub with
                            Status =
                                { representativeModel.GitHub.Status with
                                    MineCredential = Some { Kind = "oauth"; SignInRequired = None } } } }
            let html = Support.render healthy
            Expect.isTrue (html.Contains "data-github-connected=\"mine\"") "it is still shown as connected"
            Expect.isFalse (html.Contains Dom.Hooks.githubSignInRequired) "and nothing asks for a sign-in"
            Expect.isFalse (html.Contains Dom.Hooks.signInRequired) "so no prompt over the timeline either"

        // The roster used to read "ready" in green over a Claude credential the next turn
        // would fail on, because the agent gate asks whether a credential is STORED and not
        // whether it still works.
        testCase "the agent's row follows the credential's health, not merely its presence" <| fun () ->
            let html = Support.render (private' "claude" "the refresh token has expired")
            let row = html.Substring (html.IndexOf (Dom.attr Dom.Hooks.agentPresence "live"))
            let row = row.Substring (0, min 400 row.Length)
            Expect.isTrue (row.Contains Dom.Text.signInAgainStatus) "the agent's own row says what is needed"
            Expect.isFalse (row.Contains ">ready<") "and no longer claims to be ready"

        testCase "a credential that stopped working is offered a way to fix it, over the timeline" <| fun () ->
            let html = Support.render (private' "github" "github rejected this credential")
            Expect.isTrue
                (html.Contains (Dom.attr Dom.Hooks.signInRequired "github"))
                "the prompt names which connection needs it"
            Expect.isTrue (html.Contains Dom.Hooks.signInAgain) "and carries the button that leads to the fix"
            Expect.isTrue (html.Contains Dom.Text.signInAgain) "in words"
            // Over the timeline, not under it: this is a notice, and a notice below what it
            // is about is one somebody scrolls past.
            Expect.isTrue
                (html.IndexOf Dom.Hooks.signInRequired < html.IndexOf Dom.Hooks.conversation)
                "it sits above the conversation"

        // The rule `Style.fs`'s noAgent block and the case below it already encode: a call to
        // action repeated is wallpaper. The panel row and the roster row say the same fact as
        // STATUSES, and exactly one button in the document offers the remedy.
        testCase "the way to fix it is offered exactly once" <| fun () ->
            let html = Support.render (private' "claude" "the refresh token has expired")
            let occurrences (needle: string) = (html.Split needle |> Array.length) - 1
            Expect.equal (occurrences Dom.Hooks.signInAgain) 1 "one button, not one per surface that mentions it"

        // Two dead credentials are still one instruction, and the panel it opens shows both.
        testCase "two credentials needing a sign-in are still one prompt" <| fun () ->
            let needing reason = Some { Kind = "static"; SignInRequired = Some reason }
            let both =
                { representativeModel with
                    Claude =
                        { representativeModel.Claude with
                            Status = { representativeModel.Claude.Status with MineCredential = needing "claude said no" } }
                    GitHub =
                        { representativeModel.GitHub with
                            Status = { representativeModel.GitHub.Status with MineCredential = needing "github said no" } } }
            let html = Support.render both
            let occurrences (needle: string) = (html.Split needle |> Array.length) - 1
            Expect.equal (occurrences Dom.Hooks.signInRequired) 1 "one prompt"
            Expect.equal (occurrences Dom.Hooks.signInAgain) 1 "one button"

        // Signing in runs against the session. Offering it to somebody who cannot reach the
        // session is offering a button that cannot work — and the degraded strip already owns
        // that moment, which is the one-strip-at-a-time promise it has always made.
        testCase "nothing is offered while the session cannot be reached" <| fun () ->
            let offline =
                { private' "github" "github rejected this credential" with
                    Connection = Disconnected (Some "transport closed") }
            let html = Support.render offline
            Expect.isFalse (html.Contains Dom.Hooks.signInRequired) "no sign-in prompt while offline"
            Expect.isTrue
                (html.Contains (Dom.attr Dom.Hooks.degraded Dom.Text.degradedOffline))
                "the degraded strip is what speaks for that moment"
            // The panel row still says it. A status is true whether or not it can be acted on
            // right now; only the OFFER is withheld.
            Expect.isTrue
                (html.Contains (Dom.attr Dom.Hooks.githubSignInRequired "mine"))
                "and the panel still reports the credential honestly"

        testCase "an empty timeline says whether it has looked, and the caret means one thing again" <| fun () ->
            // Two opposite facts used to wear the same mark. The idle caret says "nothing was
            // ever said here"; it was also what a client showed BEFORE it had read anything,
            // which after the local store is the ordinary cold open. So the caret was telling
            // most people the opposite of the truth, and neither state could be told from the
            // other on screen.
            let empty =
                { representativeModel with
                    Conversation = ConversationProjection.empty
                    Timeline = TimelineProjection.empty }
            let looking = Support.render { empty with HistoryRead = false }
            Expect.isTrue
                (looking.Contains "data-history-loading")
                "a client that has not looked yet says it is reading, rather than claiming emptiness"
            Expect.isTrue
                (looking.Contains Dom.Text.readingHistory)
                "and says it in words too, since the pulse alone reaches nobody who cannot see it"
            let looked = Support.render { empty with HistoryRead = true }
            Expect.isFalse
                (looked.Contains "data-history-loading")
                "a client that has looked and found nothing is not still reading"
            // The other half, and the reason this is worth pinning: a session that HAS messages
            // never shows either, whether or not the client has finished looking.
            let full = Support.render { representativeModel with HistoryRead = false }
            Expect.isFalse
                (full.Contains "data-history-loading")
                "a timeline with messages in it is not an empty one"

        testCase "a client that cannot keep history says so; one that can says nothing" <| fun () ->
            // The availability invariant, not the wording: a client whose context denies it a
            // store keeps no history, and the alternative to saying so is a session that
            // quietly stops remembering with nothing on screen to explain it. The note is
            // ABSENT for every ordinary client, which is the half that keeps it meaningful.
            let ordinary = Support.render representativeModel
            Expect.isFalse
                (ordinary.Contains "data-history-store")
                "a client that keeps history has nothing to explain"
            let denied = Support.render { representativeModel with CanKeepHistory = false }
            Expect.isTrue
                (denied.Contains "data-history-store")
                "a client that cannot keep history says which capability is missing"
            Expect.isTrue
                (denied.Contains "HTTPS")
                "and names the remedy, which is the operator's and is one flag"

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
            // A queue that said only *pending* would leave the hold looking like a stall;
            // this one resolves when a person finishes a task, and says so.
            let model =
                { leasedTerminalModel with
                    Synced =
                        { leasedTerminalModel.Synced with
                            Pending =
                                leasedTerminalModel.Synced.Pending
                                |> Map.map (fun _ entry -> { entry with Authority = Authority.ofAuthor (PeerRef ada) }) } }
            let html = Support.render model
            Expect.isTrue
                (html.Contains (Dom.attr Dom.Hooks.terminalQueuedStatus Dom.Text.queuedAwaitingTerminal))
                "the hold names the terminal"

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

        testCase "a closed terminal is reachable, and shows what it ran rather than a composer" <| fun () ->
            let html = Support.render closedTerminalModel
            for label, marker in
                [ "a closed terminal has a tab of its own",
                  Dom.attr Dom.Hooks.terminalClosedTab (TerminalId.value terminalId)
                  // What it RAN is the read; how it behaved is a recording one press away.
                  // Both at once put a player of the same two lines under every command and
                  // its result.
                  "the commands it ran are listed", Dom.attr Dom.Hooks.terminalBlock "block-ui"
                  "and the way to its recording is offered",
                  Dom.attr Dom.Hooks.terminalWatch "watch" ] do
                Expect.isTrue (html.Contains marker) (sprintf "%s (`%s`) must render" label marker)
            // Nothing can be run in a closed terminal, so nothing offers to: a command line
            // that queues into a terminal with no shell behind it is the misleading half.
            Expect.isFalse
                (html.Contains (Dom.attr Dom.Hooks.terminalInput (BodyKey.terminalDraft terminalId ada)))
                "no command line"
            // The kill and the attach-again used to be asserted here, against the STRIP.
            // They live on the terminal's row in the list now (Plan 20, stage 1), and the
            // list's own cases pin both halves of each. Re-asserting their absence from a
            // strip that offers no verbs at all would be a test that cannot fail.

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
                  Kind = ConversationItemKind.Message
                  Offset = EventOffset.create 1L |> expect
                  Woke = None }
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

        // A repo note (Plan 14) is an ACT in the timeline, not a message: it renders as
        // the quiet attributed line, never as an avatar'd message article.
        testCase "a repo note renders as an attributed act-line, not a message" <| fun () ->
            let note : ConversationItem =
                { MessageId = MessageId.create "msg-repo-note" |> expect
                  Author = PeerRef ada
                  Body = "added repo octo/hello (branch main)"
                  Status = Complete
                  Kind = ConversationItemKind.ActNote
                  Offset = EventOffset.create 1L |> expect
                  Woke = None }
            let model =
                { representativeModel with
                    Conversation = { representativeModel.Conversation with Items = [ note ] } }
            let html = Support.render model
            Expect.isTrue (html.Contains "data-act-note") "the note hook renders"
            Expect.isTrue (html.Contains "added repo octo/hello (branch main)") "the act reads as its sentence"
            let noteStart = html.IndexOf "data-act-note"
            let article = html.Substring (html.LastIndexOf ("<article", noteStart), 300)
            Expect.isFalse (article.Contains "data-message-body") "no message body — it is not something someone said"

        // A turn nobody asked for (Plan 20, stage 2). The agent may now speak with nobody
        // having spoken to it, and on a shared surface that reads as the agent deciding
        // things on its own unless the item itself says otherwise. What is pinned is that
        // the mark is ON the woken item and on nothing else — not what it looks like.
        testCase "what a woken turn said is attributed as a turn nobody asked for" <| fun () ->
            let asked = MessageId.create "msg-asked" |> expect
            let woken = MessageId.create "msg-woken" |> expect
            let agentItem (messageId: MessageId) (woke: WakeReason option) : ConversationItem =
                { MessageId = messageId
                  Author = ActorRef.Agent
                  Body = "the build finished"
                  Status = Complete
                  Kind = ConversationItemKind.Message
                  Offset = EventOffset.create 1L |> expect
                  Woke = woke }
            let model =
                { representativeModel with
                    Conversation =
                        { representativeModel.Conversation with
                            Items = [ agentItem asked None; agentItem woken (Some CommandFinished) ] } }
            let html = Support.render model
            // Scoped to each article, because a whole-page render contains both and a bare
            // `Contains` would pass with the mark on the wrong one.
            let article (messageId: MessageId) =
                let start = html.IndexOf (sprintf "data-message-id=\"%s\"" (MessageId.value messageId))
                Expect.isTrue (start > 0) "the message renders"
                html.Substring (start, html.IndexOf ("</article>", start) - start)
            Expect.isTrue
                ((article woken).Contains Dom.Text.wokeCommandFinished)
                "the woken turn's message says why it exists"
            Expect.isFalse
                ((article asked).Contains "data-message-woke")
                "and a turn somebody asked for says nothing — there is nothing to explain"

        // The generated read surface (Plan 15). One renderer draws every query, so this
        // pins the RENDERER — a section per declared query, each shape drawn the way its
        // shape says — rather than any particular query's panel. A query added later gets
        // this behaviour without a line of view code, which is the property worth holding.
        testCase "the read surface renders a section per declared query, in every shape" <| fun () ->
            let html = Support.render representativeModel
            Expect.isTrue (html.Contains "data-query-panel=\"repos\"") "the rows query renders its section"
            Expect.isTrue (html.Contains "data-query-panel=\"work_environment\"") "the fields query renders its section"
            Expect.isTrue (html.Contains "data-query-panel=\"leases\"") "a query with no value yet still renders its section"
            Expect.isTrue (html.Contains "octo/hello") "a row's cells render"
            // The flag is rendered as a WORD, not a raw `true`: a human reads the answer
            // to "uncommitted changes", and `true` is the wire's word for it, not theirs.
            Expect.isTrue (html.Contains ">yes<") "a flag cell renders as a word"
            Expect.isFalse (html.Contains ">true<") "the wire's boolean does not reach the page"
            Expect.isTrue (html.Contains "data-query-pending") "an unanswered query says so rather than rendering nothing"

        // The one approval card, at both mount points (Plan 15, stage 3c). What is worth
        // pinning is that the CHAT column carries the terminal's pending commands too: the
        // whole claim of this stage is that approving what the agent is about to run is the
        // same act as reading what it is about to say, and a card only a panel shows would
        // quietly make it a different one again.
        testCase "everything waiting on a verdict appears in the chat column" <| fun () ->
            let html = Support.render representativeModel
            Expect.isTrue (html.Contains "data-pending-acts") "the chat column carries the pending list"
            let listStart = html.IndexOf "data-pending-acts"
            Expect.isTrue (listStart > 0) "the list is rendered"
            let list = html.Substring (listStart, min 2000 (html.Length - listStart))
            // The representative model's one pending act is the AGENT's terminal command
            // under the default mode — the case the gate exists for.
            Expect.isTrue (list.Contains "data-terminal-queued") "with the terminal's own queued command in it"
            Expect.isTrue (list.Contains "data-pending-subject") "and a chip saying what it is about"

        // The structure a table owes a screen reader (CLAUDE.md, UI baseline). Held in the
        // renderer, so it is held for every query — the reason the surface is generated.
        testCase "a rows query renders a real table with column headers" <| fun () ->
            let html = Support.render representativeModel
            Expect.isTrue (html.Contains "<table") "rows render as a table"
            Expect.isTrue (html.Contains "scope=\"col\"") "columns carry a scope"
            Expect.isTrue (html.Contains "uncommitted changes") "the column's human label is the heading, not its wire key"

        // What Plan 15 RETIRED: the panel's write actions. A human asks the agent, so
        // there is one authorization path and one place the act is recorded.
        testCase "the read surface offers no way to mutate anything" <| fun () ->
            let html = Support.render representativeModel
            Expect.isFalse (html.Contains "data-repo-add-input") "the add input is gone"
            Expect.isFalse (html.Contains "data-repo-remove") "the remove control is gone"
            Expect.isFalse (html.Contains "data-repo-switch") "the branch switch is gone"
    ]

// The terminal list (Plan 20, stage 0). What is pinned here is AVAILABILITY — which verbs a
// row offers over which state — and nothing about how a row looks: the marks, the tones and
// the order of the controls are the design, and a test that quoted them would go red on the
// next improvement while saying the design was wrong.
let private terminalListTests =
    testList "The terminal list" [
        let listed (model: ClientModel) = Support.render { model with Pane = Some (OnList (model.Pane |> Option.bind PaneMode.onTab)) }
        let id = TerminalId.value terminalId

        // Every case below is the same shape deliberately: the verb where it works, and its
        // absence where it does not. Only asserting the presence would leave a row that
        // offered everything to everyone looking correct.

        testCase "a row offers the kill while its terminal is running, and never once it has stopped" <| fun () ->
            Expect.isTrue
                ((listed representativeModel).Contains (Dom.attr Dom.Hooks.terminalClose id))
                "a running terminal can be killed from its row"
            Expect.isFalse
                ((listed closedTerminalModel).Contains (Dom.attr Dom.Hooks.terminalClose id))
                "a closed one has nothing left to kill"

        testCase "a row offers the rewind while its terminal is live, and never over a recording" <| fun () ->
            Expect.isTrue
                ((listed representativeModel).Contains (Dom.attr Dom.Hooks.terminalListRewind id))
                "a live terminal with something recorded can be stepped back through"
            Expect.isFalse
                ((listed closedTerminalModel).Contains (Dom.attr Dom.Hooks.terminalListRewind id))
                "a closed terminal is replayed, not rewound"

        testCase "a row offers the way back only where a provider said there is one" <| fun () ->
            Expect.isTrue
                ((listed renewableTerminalModel).Contains (Dom.attr Dom.Hooks.terminalReattach id))
                "a closed stream whose provider allows asking again"
            Expect.isFalse
                ((listed closedTerminalModel).Contains (Dom.attr Dom.Hooks.terminalReattach id))
                "and never for a shell terminal, which has no provider to ask"

        testCase "a recording the cap ate is stated on its row rather than left to look empty" <| fun () ->
            // The gap is the one state with no mark of its own, because the absence of a
            // recording has no glyph — so it is the one that says a word.
            Expect.isTrue
                ((listed forgottenTerminalModel).Contains (Dom.attr Dom.Hooks.terminalListGone id))
                "the hole in the audit trail is said"
            Expect.isFalse
                ((listed closedTerminalModel).Contains (Dom.attr Dom.Hooks.terminalListGone id))
                "and a recording that survived says nothing of the sort"

        // An ARIA requirement rather than a layout preference: `role="tablist"` promises a
        // tabpanel showing one of its tabs, and a strip left standing over the list would be
        // promising a panel that is not in the document.
        testCase "the strip and the list are never on screen together" <| fun () ->
            Expect.isTrue
                ((Support.render representativeModel).Contains "role=\"tablist\"")
                "the strip, while the pane is showing a tab"
            Expect.isFalse
                ((listed representativeModel).Contains "role=\"tablist\"")
                "and no tablist promising a panel the list has replaced"

        testCase "every terminal the session has had is reachable from the list, open or not" <| fun () ->
            // The reason the strip can stop being a census (Plan 20, stage 1): the row IS the
            // way to a closed terminal's recording, so nothing is lost by dropping it from
            // the strip.
            Expect.isTrue
                ((listed closedTerminalModel).Contains (Dom.attr Dom.Hooks.terminalListRow id))
                "a closed terminal has a row that opens it"
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

        /// The nav column's connection section alone. The report has two mounts now — the
        /// column, and the bar for where the column cannot be seen — so a whole-page
        /// `Contains` is answered by whichever happened to be right.
        let navConnection (html: string) : string =
            let at = html.IndexOf Dom.Hooks.feed
            Expect.isTrue (at >= 0) "the connection section renders at all"
            let start = html.LastIndexOf ("<section", at)
            html.Substring (start, html.IndexOf ("</section>", at) - start)

        // Replaces, never accompanies: the status word and a button to fix it would be
        // saying the same thing twice.
        testCase "the offer replaces the connection status word" <| fun () ->
            let column = navConnection (Support.render (stopped (Some "http://127.0.0.1:8321") (Some sessionId)))
            Expect.isTrue (column.Contains Dom.Hooks.sessionGone) "the card is what the column shows"
            Expect.isFalse (column.Contains "not connected") "not the status word as well"
            // The reason itself is not lost — it moves behind the card's disclosure.
            Expect.isTrue (column.Contains "the session did not answer") "the reason still reaches the reader"

        // The report has two mounts and only one is ever visible, so the way back has to be on
        // BOTH or it is missing from whichever is showing. It went missing from the bar's:
        // once the column's mount became `max-md:hidden` — the rule that stops the report
        // being read twice — a phone could be told its session had stopped and offered
        // nothing whatever to do about it.
        testCase "the way back is offered at both mounts, since only one is ever seen" <| fun () ->
            let html = Support.render (stopped (Some "http://127.0.0.1:8321") (Some sessionId))
            Expect.equal
                ((html.Split Dom.Hooks.sessionReopen |> Array.length) - 1)
                2
                "the nav column's card, and the bar for where the column cannot be seen"
            Expect.isTrue ((navConnection html).Contains Dom.Hooks.sessionReopen) "one of them is the column's"

        // The three ways the offer must decline to render, each of which would otherwise be
        // a button that cannot work.
        testCase "no manager origin means no offer, just the status" <| fun () ->
            let column = navConnection (Support.render (stopped None (Some sessionId)))
            Expect.isFalse (column.Contains Dom.Hooks.sessionGone) "nothing to ask, so nothing offered"
            Expect.isTrue (column.Contains "not connected") "the ordinary status still renders"

        testCase "no session id means no offer" <| fun () ->
            let column = navConnection (Support.render (stopped (Some "http://127.0.0.1:8321") None))
            Expect.isFalse (column.Contains Dom.Hooks.sessionGone) "nothing to ask FOR"
            Expect.isTrue (column.Contains "not connected") "the ordinary status still renders"

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
            Expect.isTrue (html.Contains Dom.Text.reopenPromise) "the promise is kept where it can be"
            Expect.isFalse (html.Contains Dom.Text.reopenPromiseEphemeral) "and no warning where none is due"

        testCase "an ephemeral-address deployment warns instead of promising" <| fun () ->
            let html =
                Support.render
                    { stopped (Some "http://127.0.0.1:8321") (Some sessionId) with EphemeralStorage = true }
            Expect.isTrue (html.Contains Dom.Text.reopenPromiseEphemeral) "it says what reopening costs"
            Expect.isFalse (html.Contains Dom.Text.reopenPromise) "and never both"

        // The card is the more specific message, so within the column it is the ONLY one:
        // a status word, a promise and a card that repeats both would be one fact three times.
        testCase "the column says it once, by the card" <| fun () ->
            let column = navConnection (Support.render (stopped (Some "http://127.0.0.1:8321") (Some sessionId)))
            Expect.equal
                ((column.Split Dom.Text.reopenPromise |> Array.length) - 1)
                1
                "the promise is stated once"
            Expect.isFalse (column.Contains Dom.Text.localFallback) "and never beside the bar's wording of it"

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
        // The build the shell merely carries into its asset URLs; this suite is about the
        // manager meta tag, so any address does.
        let assets = AssetBuild "testbuild001"

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

        // It used to be said once, having been said three times before that. Now it is said
        // nowhere: "everything is fine" is the least actionable thing a screen can carry, and
        // a client that is working says so by working. What survives is the state TOKEN, on
        // an attribute, which is what a test should have been reading all along.
        testCase "a healthy client says nothing about its connection" <| fun () ->
            let html = (Support.render settled).ToLowerInvariant ()
            for word in [ "up to date"; ">connected<"; ">connecting<"; "not connected" ] do
                Expect.isFalse (html.Contains word) (sprintf "nothing on the screen reads %s" word)
            Expect.isTrue
                ((Support.render settled).Contains (Dom.attr Dom.Hooks.connection Dom.Text.connected))
                "but the state is still on the attribute a test reads"

        testCase "a brief catch-up says nothing at all" <| fun () ->
            let html = Support.render { representativeModel with EventConsumer = { representativeModel.EventConsumer with CatchUpIsSlow = false } }
            Expect.isFalse (html.Contains Dom.Hooks.catchUp) "the sidebar line stays put"
            Expect.isFalse (html.Contains "Catching up") "and nothing anywhere else says it either"

        testCase "a catch-up worth waiting on is reported, with its progress" <| fun () ->
            let html = Support.render representativeModel
            Expect.isTrue (html.Contains (Dom.hookText Dom.Hooks.catchUp Dom.Text.catchingUp)) "the sidebar names it"
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
        // The terminal list (Plan 20, stage 0) replaces the pane's body, so no other render
        // contains its controls. Scanned HERE rather than pinned again beside the list's own
        // tests: the accessibility floor is asserted once and centrally, and a surface that
        // is invisible to the scan is a surface the floor does not cover.
        let listShell = Support.render { representativeModel with Pane = Some (OnList None) }
        // Every notice at once — a dead feed, a credential the provider rejected, a session
        // that stopped, and a deployment that can keep none of it. Scanned here for the same
        // reason the terminal list is: a surface the floor's scan cannot see is a surface the
        // floor does not cover, and the disclosures these notices fold their detail into are
        // controls like any other.
        //
        // TWO renders, because the session leg SUBSUMES the history leg — a Process nobody can
        // reach cannot serve its feed either, and the report says one problem — so a stalled
        // feed is only ever reported over a session that is otherwise fine.
        let stoppedShell =
            Support.render
                { representativeModel with
                    Connection = Disconnected (Some "the session did not answer")
                    Manager = Some "http://127.0.0.1:8321"
                    CanKeepHistory = false
                    EphemeralStorage = true
                    GitHub =
                        { representativeModel.GitHub with
                            Status =
                                { representativeModel.GitHub.Status with
                                    MineCredential = Some { Kind = "static"; SignInRequired = Some "github rejected this credential" } } } }
        let stalledShell =
            Support.render
                { representativeModel with
                    Connection = Connected
                    EventConsumer = { representativeModel.EventConsumer with Feed = FeedStalled "ECONNREFUSED" } }
        let noticeShell = stoppedShell + stalledShell

        // Every input either wears the ONE field face (a ring that goes blue on focus) or
        // wears nothing at all, because the row around it carries the stroke. What is ruled
        // out is the third thing: a control that invents its own border, or one that draws a
        // box with no focus state.
        testCase "every input draws the one field face, or draws nothing" <| fun () ->
            for classes in classesOf [ "input"; "select"; "textarea" ] (shell + settingsShell + listShell) do
                let isField = classes.Contains "focus:border-blue"
                let isBare = classes.Contains "border-0"
                Expect.isTrue (isField || isBare) (sprintf "an input is neither the field face nor bare: %s" classes)

        // Every pressable thing has a visible keyboard focus state (AGENTS.md's UI baseline).
        // The failure this pins is silent by nature: a control with `outline-2` and no
        // `outline` draws nothing, and you only find out with a keyboard.
        testCase "every button and link declares a visible focus ring" <| fun () ->
            for classes in classesOf [ "button"; "a "; "summary" ] (shell + settingsShell + listShell + noticeShell) do
                Expect.isTrue
                    (classes.Contains "focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue")
                    (sprintf "a control has no visible focus ring: %s" classes)

        // The other way to ship no focus ring, and the quieter one: declare it and then turn
        // outlines off. Tailwind v4's `outline-none` is not the absence of an outline but a
        // SETTING — `--tw-outline-style: none` — and every outline utility resolves its style
        // through that variable, so a control wearing both has the classes, is served the CSS,
        // and draws nothing (measured live on the terminal's approval readout: `outlineStyle`
        // reported `none` on a focused control carrying the whole ring).
        //
        // The token is matched exactly, not by substring: `[&_.ProseMirror]:outline-none`
        // reaches a DESCENDANT (a mounted editor's contenteditable, whose focus signal is its
        // container's) and is the one legitimate way to write those characters.
        testCase "a declared focus ring is never switched off by outline-none" <| fun () ->
            let tags = [ "button"; "a "; "input"; "select"; "textarea" ]
            for classes in classesOf tags (shell + settingsShell + listShell) do
                if classes.Contains "focus-visible:outline" then
                    Expect.isFalse
                        (classes.Split ' ' |> Array.contains "outline-none")
                        (sprintf "a control declares a focus ring and then disables outlines: %s" classes)

        // Deliberately no "focus is blue, everywhere" here. That focus is BLUE rather than
        // green is house style, not a floor — a design that moved it would fail such a test
        // while breaking nothing, which is the shape this suite does not keep (AGENTS.md,
        // "Writing tests"). That focus is VISIBLE is the invariant, and the cases above are
        // what hold it.

        // The degradation bar is fixed above all three panes on a phone, so its height and the
        // room the panes leave for it are one number in three class strings — and they cannot
        // be composed from a shared token, because Tailwind emits only classes that appear
        // literally in the source. That makes drift between them silent in the worst way: the
        // stylesheet simply lacks the class, the bar falls back to its content height, and the
        // result is a bar overlapping the header or a band of dead space under it. Arithmetic,
        // not design — the number may be any number, so long as it is the same one.
        testCase "the bar's height and the room the panes leave for it are one number" <| fun () ->
            let sizeOf (what: string) (token: string) =
                let n = token.Substring (token.LastIndexOf '-' + 1)
                Expect.isTrue (n |> Seq.forall System.Char.IsDigit && n <> "") (sprintf "%s ends in a size: %s" what token)
                n
            let height = sizeOf "the bar's height" Style.degradedBarHeight
            Expect.equal (sizeOf "the overlays' inset" Style.degradedBarRoom) height "an overlay leaves exactly the bar"
            Expect.equal (sizeOf "the column's padding" Style.degradedBarRoomPad) height "and so does the column"

        // --- what a notice says first -----------------------------------------------------

        /// The rendered page with every `<details …data-detail>` cut out of it: what a notice
        /// says WITHOUT anyone opening anything.
        let rec onTheSurface (html: string) : string =
            match html.IndexOf Dom.Hooks.detail with
            | -1 -> html
            | at ->
                let start = html.LastIndexOf ("<details", at)
                let stop = html.IndexOf ("</details>", at)
                if start < 0 || stop < 0 then html
                else onTheSurface (html.Remove (start, stop + "</details>".Length - start))

        // A disclosure is only a disclosure if the browser is the one making it. Written as a
        // real `<details>`/`<summary>`, it arrives keyboard-operable and announced; written as
        // a div with a click handler it arrives as neither, and looks identical.
        testCase "every folded mechanism is a real details/summary" <| fun () ->
            let rec check (from: int) (n: int) =
                match noticeShell.IndexOf (Dom.Hooks.detail, from) with
                | -1 -> n
                | at ->
                    let start = noticeShell.LastIndexOf ("<", at)
                    let stop = noticeShell.IndexOf ("</details>", at)
                    Expect.equal (noticeShell.Substring (start, "<details".Length)) "<details" "the element is a details"
                    Expect.isTrue
                        (stop > at && noticeShell.Substring(at, stop - at).Contains "<summary")
                        "and it opens by a summary, not by a handler on something else"
                    check (stop + 1) (n + 1)
            Expect.isTrue (check 0 0 > 0) "the notices this shell renders do fold something away"

        // The half of the split that can regress silently. Leading with the consequence is
        // visible the moment anybody looks at the screen; losing the mechanism is not — a
        // fault nobody can read is a fault nobody can report, and the words that go missing
        // are the provider's and the transport's own.
        testCase "a notice keeps its mechanism, and keeps it off the surface" <| fun () ->
            let surface = onTheSurface noticeShell
            for what, mechanism in
                [ "the transport's reason for stopping", "the session did not answer"
                  "the feed's fault", "ECONNREFUSED"
                  "the provider's own words", "github rejected this credential"
                  "why nothing can be kept here", Dom.Text.historyNotKeptWhy ] do
                Expect.isTrue (noticeShell.Contains mechanism) (sprintf "%s is still in the document" what)
                Expect.isFalse (surface.Contains mechanism) (sprintf "%s is not also on the surface" what)
    ]

// What the session page must keep saying, whatever it comes to look like: one person wears
// one name, an author is never a raw id, and a control is offered only when it does something.
// Deliberately NOT here — the caret marking an empty timeline, the composer's rest-state rail,
// which weight a waiting button wears — because those are the design, and the design changing
// is not a regression (AGENTS.md, "Writing tests").
let private semanticsTests =
    testList "What the screen says about people and their choices" [

        /// What one message ELEMENT says, from its author hook to its body — so an assertion
        /// about the chat's attribution cannot be satisfied by the same word appearing in the
        /// roster, which is exactly how the first version of this test passed while the chat
        /// was still printing a peer id.
        let messageMetaOf (peer: PeerId) (html: string) : string =
            let start = html.IndexOf (Dom.attr Dom.Hooks.messageAuthor (PeerId.value peer))
            Expect.isTrue (start >= 0) "the message renders at all"
            let stop = html.IndexOf (Dom.Hooks.messageBody, start)
            html.Substring (start, stop - start)

        /// The fixture with one message from a COLLABORATOR — `bob`, deliberately not the
        /// local peer, so what the chat prints about him can only have come from the roster
        /// lookup under test rather than from the client's own identity.
        let withMessageFromBob (peers: Map<PeerId, string>) =
            { representativeModel with
                Peers = peers
                Presence = Map.empty
                Conversation =
                    { Items =
                        [ { MessageId = MessageId.create "msg-bob" |> expect
                            Author = PeerRef bob
                            Body = "on it"
                            Status = Complete
                            Kind = ConversationItemKind.Message
                            Offset = EventOffset.create 1L |> expect
                            Woke = None } ]
                      ActiveAgentMessages = Map.empty; WokenTurn = None }
                Timeline = TimelineProjection.empty }

        // A peer id is a token, not a person. The roster, the draft summaries and the lease
        // bar all resolved one to a name; the chat did not, so the same human appeared as
        // `brave-owl` in the sidebar and `bob` on their own message.
        testCase "the chat attributes a message to the name, not the peer id" <| fun () ->
            let html = Support.render (withMessageFromBob (Map.ofList [ bob, "quiet-otter" ]))
            let meta = messageMetaOf bob html
            Expect.isTrue (meta.Contains ">quiet-otter<") "the author is the name the roster knows"
            Expect.isFalse (meta.Contains ">bob<") "and never the raw id as a person's name"
            Expect.isTrue
                (html.Contains (Dom.attr Dom.Hooks.messageAuthor (PeerId.value bob)))
                "while the hook still carries the stable id, so tests and delegation do not move"

        // The fallback is the reason this resolves through the roster rather than asserting a
        // name exists: a peer who left before this client ever saw them has no name to show,
        // and a blank author would be worse than an ugly one.
        testCase "an unknown peer still gets attributed, by id" <| fun () ->
            let html = Support.render (withMessageFromBob Map.empty)
            Expect.isTrue
                ((messageMetaOf bob html).Contains ">bob<")
                "no name known, so the id stands in rather than nothing"

        // The roster is folded from the durable `PeerJoined` log; your own display name comes
        // from THIS connection. When a peer rejoined under a new name the two disagreed, and a
        // chat reading the roster for its own messages would contradict the sidebar's "you"
        // row — two names for one person, which is the defect this whole thread is about.
        testCase "your own messages wear the name the sidebar calls you" <| fun () ->
            let html =
                Support.render
                    { representativeModel with
                        Peer = { PeerId = ada; DisplayName = "warm-tern" }
                        Peers = Map.ofList [ ada, "a-stale-name-from-the-log" ] }
            Expect.isTrue ((messageMetaOf ada html).Contains ">warm-tern<") "the chat says what you are called now"
            Expect.isTrue (html.Contains (Dom.hookText Dom.Hooks.displayName "warm-tern")) "and so does the roster"
            Expect.isFalse (html.Contains "a-stale-name-from-the-log") "the log's older name is nobody's current name"

        // A destructive control offered over nothing is a live-looking button that does not do
        // anything, and the way a working one and a dead one come to look identical. Whether
        // it is a discard `x` at all, and what the send button WEARS while it waits, are
        // design; that the offer follows the content is the invariant.
        testCase "discard is offered only when there is something to discard" <| fun () ->
            let empty = Support.render { representativeModel with Synced = { representativeModel.Synced with Drafts = Map.empty } }
            let full = Support.render representativeModel
            Expect.isFalse (empty.Contains Dom.Hooks.discardDraft) "nothing to discard, so nothing offers to"
            Expect.isTrue (full.Contains Dom.Hooks.discardDraft) "and it is there once there is"
            // The local-first promise, from the composer's side: an empty draft is not a
            // blocked one. `Resilience.fs` pins the same thing against a dead feed.
            Expect.isTrue
                (empty.Contains (Dom.attr Dom.Hooks.sendDraft (PeerId.value ada)))
                "send keeps its place either way — it is never taken away"
    ]

let tests =
    testList "Acceptance" [
        uiChecklistTests
        terminalListTests
        presenceTests
        syncStatusTests
        chromeTests
        semanticsTests
        reconnectOfferTests
        shellTests
    ]
