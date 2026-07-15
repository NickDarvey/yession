namespace Yession.Client

open Yession.Domain

/// Pure rendering of the client shell to HTML. The view is a total function of the model,
/// so it is identical whether produced by the Session Process for the static bootstrap or
/// by the browser as the model updates.
///
/// The layout is a session workspace (docs/plans/02-metro-zune-styling.md): a sidebar
/// (identity, connection + offsets — a core product invariant, not a debug detail —
/// presence, environment, command log) beside a conversation column (timeline pinned to
/// bottom, agent activity strip, the shared message queue, and the draft composer).
/// Styling is composed Tailwind utilities from `Style` — no CSS is authored anywhere.
///
/// Markup contracts: every interactive or observable element carries a `data-*` hook
/// (`data-connection`, `data-draft-input`, `data-queue-up`, …). The shell's delegation and
/// the E2E suites bind to those hooks only — classes are presentation and carry no
/// behaviour. `data-*` attributes stay immediately before the closing `>` where a test
/// asserts `data-foo>text<`.
module View =

    let private connectionLabel =
        function
        | Disconnected -> "Disconnected"
        | Connecting -> "Connecting"
        | Connected -> "Connected"
        | Reconnecting -> "Reconnecting"

    let private offsetText =
        function
        | Some offset -> string (EventOffset.value offset)
        | None -> "—"

    let private catchUpText (consumer: EventConsumerState) =
        if consumer.IsCatchingUp then "Catching up" else "Up to date"

    let private escapeHtml (s: string) =
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")

    // --- Sidebar ---------------------------------------------------------------------------

    /// The session's live sync state: connection, catch-up, and the event-log offsets.
    let private connectionSection (model: ClientModel) : string =
        let consumer = model.EventConsumer
        let catchUpClass = if consumer.IsCatchingUp then Style.statusRun else Style.statusOk
        String.concat "" [
            sprintf "<section class=\"%s\">" Style.sideSectionFirst
            sprintf "<span class=\"%s\" data-connection>%s</span>" Style.body (connectionLabel model.Connection)
            sprintf "<span class=\"%s\" data-catch-up>%s</span>" catchUpClass (catchUpText consumer)
            sprintf "<span class=\"%s tabular-nums\">processed <b class=\"text-ink-dim\" data-last-processed-offset>%s</b> · latest <b class=\"text-ink-dim\" data-latest-known-offset>%s</b></span>"
                Style.label
                (offsetText consumer.LastProcessedOffset)
                (offsetText consumer.LatestKnownOffset)
            "</section>"
        ]

    let private peopleSection (model: ClientModel) : string =
        String.concat "" [
            sprintf "<section class=\"%s\">" Style.sideSection
            sprintf "<span class=\"%s\">people</span>" Style.label
            sprintf "<div class=\"%s\"><span class=\"%s\"></span><span class=\"truncate\" data-display-name>%s</span><span class=\"%s\">you</span></div>"
                Style.person
                (Style.cls [ Style.avatar; Style.humanAvatar (PeerId.value model.Peer.PeerId) ])
                (escapeHtml model.Peer.DisplayName)
                Style.label
            sprintf "<div class=\"%s\"><span class=\"%s\"></span>agent</div>"
                Style.person
                (Style.cls [ Style.avatar; Style.agentAvatar ])
            "</section>"
        ]

    let private environmentLabel =
        function
        | EnvironmentNotStarted -> "not-started"
        | EnvironmentStarting -> "starting"
        | EnvironmentRunning _ -> "running"
        | EnvironmentFailed _ -> "failed"
        | EnvironmentDown -> "stopped"

    let private environmentStatus =
        function
        | EnvironmentNotStarted -> Style.statusFaint, "not started"
        | EnvironmentStarting -> Style.statusRun, sprintf "<span class=\"%s\"></span>starting" Style.statusDotPulse
        | EnvironmentRunning _ -> Style.statusOk, sprintf "<span class=\"%s\"></span>running" Style.statusDot
        | EnvironmentFailed _ -> Style.statusErr, "failed"
        | EnvironmentDown -> Style.statusFaint, "stopped"

    let private environmentSection (status: EnvironmentStatus) : string =
        let statusClass, statusHtml = environmentStatus status
        String.concat "" [
            sprintf "<section class=\"%s\" data-environment=\"%s\">" Style.sideSection (environmentLabel status)
            sprintf "<div class=\"%s\"><span class=\"%s\">environment</span><span class=\"%s\">%s</span></div>"
                Style.sideRow Style.label statusClass statusHtml
            "</section>"
        ]

    let private commandStatusLabel =
        function
        | CommandPending -> "pending"
        | CommandRunning -> "running"
        | CommandFinished (CommandSucceeded code) -> sprintf "succeeded:%d" code
        | CommandFinished (CommandFailed code) -> sprintf "failed:%d" code
        | CommandFinished CommandTimedOut -> "timed-out"
        | CommandFinished (CommandExecutionFailed _) -> "execution-failed"

    let private commandStatusHtml =
        function
        | CommandPending -> sprintf "<span class=\"%s\">pending</span>" Style.statusFaint
        | CommandRunning -> sprintf "<span class=\"%s\"><span class=\"%s\"></span>running</span>" Style.statusRun Style.statusDotPulse
        | CommandFinished (CommandSucceeded code) -> sprintf "<span class=\"%s\">✓ %d</span>" Style.statusOk code
        | CommandFinished (CommandFailed code) -> sprintf "<span class=\"%s\">✗ %d</span>" Style.statusErr code
        | CommandFinished CommandTimedOut -> sprintf "<span class=\"%s\">timed out</span>" Style.statusErr
        | CommandFinished (CommandExecutionFailed _) -> sprintf "<span class=\"%s\">failed</span>" Style.statusErr

    /// The read-only command log — derived from events only; there is no input surface.
    let private commandsSection (log: CommandLog) : string =
        let entries =
            log.Entries
            |> List.map (fun entry ->
                let output =
                    entry.Output
                    |> List.map (fun (stream, text) ->
                        sprintf "<pre class=\"%s\" data-stream=\"%s\">%s</pre>"
                            Style.monoOut
                            (match stream with Stdout -> "stdout" | Stderr -> "stderr")
                            (escapeHtml text))
                    |> String.concat ""
                String.concat "" [
                    sprintf "<article class=\"%s\" data-command-id=\"%s\" data-command-status=\"%s\">"
                        Style.commandCard
                        (CommandId.value entry.CommandId)
                        (commandStatusLabel entry.Status)
                    sprintf "<div class=\"%s\"><code class=\"%s\">%s %s</code>%s</div>"
                        Style.sideRow
                        Style.mono
                        (escapeHtml entry.Executable)
                        (escapeHtml (String.concat " " entry.Arguments))
                        (commandStatusHtml entry.Status)
                    output
                    "</article>"
                ])
            |> String.concat ""
        String.concat "" [
            sprintf "<section class=\"%s\" data-command-log>" Style.sideSection
            sprintf "<span class=\"%s\">commands</span>" Style.label
            entries
            "</section>"
        ]

    let private sidebar (model: ClientModel) : string =
        String.concat "" [
            sprintf "<div class=\"%s\" data-nav-toggle></div>" Style.scrim
            sprintf "<aside class=\"%s\">" Style.sidebar
            sprintf "<div class=\"%s\"><span class=\"%s\">yession<span class=\"text-green\">.</span></span><button type=\"button\" class=\"%s\" aria-label=\"Hide sidebar\" data-nav-toggle>‹</button></div>"
                Style.sideHead Style.wordmark Style.navChevron
            connectionSection model
            peopleSection model
            environmentSection model.Environment
            commandsSection model.Commands
            "<div class=\"flex-1\"></div>"
            sprintf "<span class=\"%s pt-4\">local first · every fact is an event</span>" Style.label
            "</aside>"
        ]

    // --- Conversation column ---------------------------------------------------------------

    let private headerStatus (model: ClientModel) : string =
        match model.Connection with
        | Connected when model.EventConsumer.IsCatchingUp ->
            sprintf "<span class=\"%s\"><span class=\"%s\"></span>catching up</span>"
                (Style.cls [ Style.statusRun; Style.headerStatus ]) Style.statusDotPulse
        | Connected ->
            sprintf "<span class=\"%s\"><span class=\"%s\"></span>up to date</span>"
                (Style.cls [ Style.statusOk; Style.headerStatus ]) Style.statusDot
        | Connecting ->
            sprintf "<span class=\"%s\"><span class=\"%s\"></span>connecting</span>"
                (Style.cls [ Style.statusRun; Style.headerStatus ]) Style.statusDotPulse
        | Reconnecting ->
            sprintf "<span class=\"%s\"><span class=\"%s\"></span>reconnecting</span>"
                (Style.cls [ Style.statusRun; Style.headerStatus ]) Style.statusDotPulse
        | Disconnected ->
            sprintf "<span class=\"%s\">disconnected</span>" (Style.cls [ Style.statusFaint; Style.headerStatus ])

    let private header (model: ClientModel) : string =
        String.concat "" [
            sprintf "<header class=\"%s\">" Style.header
            sprintf "<button type=\"button\" class=\"%s\" aria-label=\"Show sidebar\" data-nav-toggle>›</button>"
                (Style.cls [ Style.navChevron; Style.navReopen ])
            sprintf "<h1 class=\"%s\">session</h1>" (Style.cls [ Style.heading; Style.headerTitle ])
            headerStatus model
            "</header>"
        ]

    let private authorLabel =
        function
        | HumanPeer p -> PeerId.value p
        | ActorRef.Agent -> "agent"
        | ActorRef.SessionProcess -> "session-process"
        | ActorRef.System -> "system"

    let private messageStatusLabel =
        function
        | Complete -> "complete"
        | Streaming -> "streaming"
        | ConversationItemStatus.Failed -> "failed"
        | ConversationItemStatus.Interrupted -> "interrupted"

    let private authorAvatar =
        function
        | HumanPeer p -> Style.humanAvatar (PeerId.value p)
        | ActorRef.Agent -> Style.agentAvatar
        | ActorRef.SessionProcess | ActorRef.System -> Style.humanAvatar "session"

    /// The conversation timeline — rendered from the event projection only, never from
    /// the synced draft state (docs/design.md §1 "Durable facts are events").
    let private conversation (projection: ConversationProjection) : string =
        let items =
            projection.Items
            |> List.map (fun item ->
                let isAgent = (item.Author = ActorRef.Agent)
                let whoClass = if isAgent then Style.whoAgent else Style.who
                let statusHtml =
                    match item.Status with
                    | Complete -> ""
                    | Streaming -> sprintf "<span class=\"%s\"><span class=\"%s\"></span>streaming</span>" Style.statusRun Style.statusDotPulse
                    | ConversationItemStatus.Failed -> sprintf "<span class=\"%s\">failed</span>" Style.statusErr
                    | ConversationItemStatus.Interrupted -> sprintf "<span class=\"%s\">interrupted</span>" Style.statusFaint
                let bodyClass, caretHtml =
                    match item.Status with
                    | Streaming -> Style.messageBodyStreaming, sprintf "<span class=\"%s\"></span>" Style.caret
                    | _ -> Style.messageBody, ""
                String.concat "" [
                    sprintf "<article class=\"%s\" data-message-id=\"%s\" data-message-author=\"%s\" data-message-status=\"%s\">"
                        Style.message
                        (MessageId.value item.MessageId)
                        (authorLabel item.Author)
                        (messageStatusLabel item.Status)
                    sprintf "<span class=\"%s\"></span>" (Style.cls [ Style.avatar; Style.messageAvatar; authorAvatar item.Author ])
                    sprintf "<div class=\"%s\"><span class=\"%s\">%s</span>%s</div>"
                        Style.messageMeta whoClass (escapeHtml (authorLabel item.Author)) statusHtml
                    // The body carries its own hook so tests can assert the snapshotted
                    // text exactly, independent of the author/status markup around it.
                    sprintf "<div class=\"%s\" data-message-body>%s%s</div>" bodyClass (escapeHtml item.Body) caretHtml
                    "</article>"
                ])
            |> String.concat ""
        sprintf "<section class=\"%s\" data-conversation>%s</section>" Style.timeline items

    /// The agent activity strip. The explicit interrupt (Phase 3) cancels the running
    /// turn and drains the queue immediately; the shell wires it to `InterruptTurn`.
    let private agentStrip (agent: AgentViewState) : string =
        match agent.ActiveTurn with
        | Some turn ->
            String.concat "" [
                sprintf "<section class=\"%s\" data-agent-stream>" Style.activity
                sprintf "<span class=\"%s\"></span>" Style.activityPulse
                sprintf "<span class=\"%s\" data-agent-turn=\"%s\">agent is responding</span>"
                    Style.activityText (AgentTurnId.value turn)
                sprintf "<span class=\"%s\">turn %s</span>" Style.activityTurn (escapeHtml (AgentTurnId.value turn))
                sprintf "<button type=\"button\" class=\"%s ml-auto\" data-interrupt-turn=\"%s\">Interrupt</button>"
                    Style.btnDanger (AgentTurnId.value turn)
                "</section>"
            ]
        | None -> "<section class=\"hidden\" data-agent-stream></section>"

    /// The shared message queue (Phase 3), in consumption order. Every entry stays
    /// editable, reorderable (one fractional-index write per move), and deletable by
    /// any peer until the Session Process drains it into the timeline.
    let private queue (synced: SyncedSessionState) : string =
        let entries = QueueOrder.sorted synced.Queue
        let head =
            match entries with
            | [] -> ""
            | _ ->
                sprintf "<div class=\"%s\"><span class=\"%s\">queued · %d</span><span class=\"%s\">editable until the agent takes them</span></div>"
                    Style.queueHead Style.queueCount (List.length entries) Style.small
        let items =
            entries
            |> List.map (fun entry ->
                let id = QueueId.value entry.QueueId
                String.concat "" [
                    sprintf "<article class=\"%s\" data-queue-id=\"%s\" data-queue-author=\"%s\" data-queue-order=\"%s\">"
                        Style.queueItem id (PeerId.value entry.Author) (string entry.Order)
                    sprintf "<span class=\"%s\"></span>" (Style.cls [ Style.avatarSm; Style.humanAvatar (PeerId.value entry.Author) ])
                    sprintf "<textarea rows=\"1\" class=\"%s\" data-queue-input=\"%s\">%s</textarea>"
                        Style.queueInput id (escapeHtml (Ylmish.Text.toString entry.Body))
                    sprintf "<div class=\"%s\">" Style.queueTools
                    sprintf "<button type=\"button\" class=\"%s\" aria-label=\"Move up\" data-queue-up=\"%s\">↑</button>"
                        (Style.cls [ Style.btn; Style.btnIcon ]) id
                    sprintf "<button type=\"button\" class=\"%s\" aria-label=\"Move down\" data-queue-down=\"%s\">↓</button>"
                        (Style.cls [ Style.btn; Style.btnIcon ]) id
                    sprintf "<button type=\"button\" class=\"%s\" aria-label=\"Delete\" data-queue-delete=\"%s\">✕</button>"
                        (Style.cls [ Style.btnDanger; Style.btnIcon ]) id
                    "</div></article>"
                ])
            |> String.concat ""
        sprintf "<section class=\"%s\" data-message-queue>%s%s</section>" Style.queue head items

    /// The synced drafts, rendered in stable (id) order so the markup is deterministic.
    /// Every draft is a composer box, editable and sendable by any peer — sending moves
    /// it into the shared queue (the shell wires the button to `SendDraft`; Phase 3).
    let private drafts (model: ClientModel) : string =
        let items =
            model.Synced.Drafts
            |> Map.toList
            |> List.map (fun (draftId, draft) ->
                let id = DraftId.value draftId
                let author =
                    if draft.Author = model.Peer.PeerId then ""
                    else sprintf "<span class=\"%s\">%s</span>" Style.draftAuthor (escapeHtml (PeerId.value draft.Author))
                String.concat "" [
                    sprintf "<article class=\"%s\" data-draft-id=\"%s\" data-draft-author=\"%s\">"
                        Style.draftBox id (PeerId.value draft.Author)
                    sprintf "<span class=\"%s\"></span>" Style.draftEdge
                    sprintf "<textarea rows=\"2\" placeholder=\"Message the session — sending queues while the agent works\" class=\"%s\" data-draft-input=\"%s\">%s</textarea>"
                        Style.draftInput id (escapeHtml (Ylmish.Text.toString draft.Body))
                    sprintf "<div class=\"%s\">" Style.draftActions
                    sprintf "<button type=\"button\" class=\"%s\" data-send-draft=\"%s\">Send</button>" Style.btnPrimary id
                    author
                    "</div></article>"
                ])
            |> String.concat ""
        String.concat "" [
            sprintf "<section class=\"%s\" data-draft-editor>" Style.composer
            items
            sprintf "<button type=\"button\" class=\"%s self-start\" data-start-draft>Start draft</button>" Style.btn
            "</section>"
        ]

    /// Render the client shell as an HTML fragment (the contents of `#app`).
    let render (model: ClientModel) : string =
        String.concat "" [
            sidebar model
            sprintf "<div class=\"%s\">" Style.mainColumn
            header model
            conversation model.Conversation
            agentStrip model.Agent
            queue model.Synced
            drafts model
            "</div>"
        ]

    /// Render a full HTML document hosting the client shell. Used by the Session Process
    /// static bootstrap so the served page *is* the client shell. The serving Process
    /// embeds its session id, giving the browser a synchronous, pre-connection session
    /// identity — the client's local doc store is keyed by it. The `#app` wrapper's
    /// classes live here: the browser only ever swaps its innerHTML, so they persist.
    let page (sessionId: SessionId) (model: ClientModel) : string =
        String.concat "" [
            "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">"
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
            sprintf "<meta name=\"yession-session\" content=\"%s\">" (escapeHtml (SessionId.value sessionId))
            "<title>Yession</title>"
            Style.headTags
            "</head><body>"
            sprintf "<main id=\"app\" class=\"%s\">%s</main>" Style.app (render model)
            "<script type=\"module\" src=\"/client.js\"></script>"
            "</body></html>"
        ]
