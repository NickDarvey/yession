namespace Yession.App

open Yession.Domain
open Lit

/// The client shell as Fable.Lit templates. The view is a total function of the model
/// (plus injected `ViewActions` for the few things a template cannot derive from the
/// model: fresh ids, the interrupt round-trip, the sidebar toggle). In the browser Lit
/// renders it into `#app` on every model change (no manual innerHTML, no delegation, no
/// focus juggling — Lit diffs); the host renders the same templates to a string for the
/// served bootstrap (`Yession.Host.Ssr`).
///
/// Markup contract: every observable element carries a `data-*` hook. lit-html cannot
/// inject attribute *names* through a hole, so the hook names are written literally here;
/// they ARE the `Dom.Hooks` vocabulary the tests assert against, and the tests fail loudly
/// if the two drift. Classes are `Style.*` compositions (presentation only, no behaviour).

/// The side-effecting actions a template needs but cannot compute from the model. Injected
/// so the view stays free of Guid/random and of the connection; the browser supplies real
/// implementations, tests and SSR supply no-ops.
type ViewActions =
    { /// Send the local peer's draft: mint a queue id, copy the draft body fragment into the
      /// new queue entry, and enqueue. Imperative because the fragment content-copy (shared
      /// types can't be re-parented) can't live in the pure reducer.
      SendDraft : PeerId -> unit
      /// Ask the Session Process to cancel the running agent turn.
      Interrupt : AgentTurnId -> unit
      /// Toggle the sidebar drawer (a presentation bit on the shell root, not model).
      ToggleNav : unit -> unit
      /// Broadcast the local caret position in the title (`None` = caret left the title),
      /// so collaborators see the cursor. Ephemeral presence, relayed to other peers.
      ReportTitleCursor : int option -> unit }

module ViewActions =
    /// A no-op action set for rendering the view to a string (SSR + tests). The handlers
    /// are never invoked while rendering — they fire on user events in the live browser.
    let ssr : ViewActions =
        { SendDraft = ignore
          Interrupt = ignore
          ToggleNav = ignore
          ReportTitleCursor = ignore }

module View =

    // --- Label helpers: map model cases to the shared `Dom.Text` tokens -----------------

    let private connectionLabel =
        function
        | Disconnected -> Dom.Text.disconnected
        | Connecting -> Dom.Text.connecting
        | Connected -> Dom.Text.connected
        | Reconnecting -> Dom.Text.reconnecting

    let private offsetText =
        function
        | Some offset -> string (EventOffset.value offset)
        | None -> Dom.Text.offsetNone

    let private catchUpText (consumer: EventConsumerState) =
        if consumer.IsCatchingUp then Dom.Text.catchingUp else Dom.Text.upToDate

    let private authorLabel =
        function
        | HumanPeer p -> PeerId.value p
        | ActorRef.Agent -> Dom.Text.agent
        | ActorRef.SessionProcess -> Dom.Text.sessionProcess
        | ActorRef.System -> Dom.Text.system

    let private messageStatusLabel =
        function
        | Complete -> Dom.Text.complete
        | Streaming -> Dom.Text.streaming
        | ConversationItemStatus.Failed -> Dom.Text.failed
        | ConversationItemStatus.Interrupted -> Dom.Text.interrupted

    let private environmentLabel =
        function
        | EnvironmentNotStarted -> Dom.Text.envNotStarted
        | EnvironmentStarting -> Dom.Text.envStarting
        | EnvironmentRunning _ -> Dom.Text.envRunning
        | EnvironmentFailed _ -> Dom.Text.envFailed
        | EnvironmentDown -> Dom.Text.envStopped

    let private commandStatusLabel =
        function
        | CommandPending -> Dom.Text.cmdPending
        | CommandRunning -> Dom.Text.cmdRunning
        | CommandFinished (CommandSucceeded code) -> Dom.Text.cmdSucceeded code
        | CommandFinished (CommandFailed code) -> Dom.Text.cmdFailed code
        | CommandFinished CommandTimedOut -> Dom.Text.cmdTimedOut
        | CommandFinished (CommandExecutionFailed _) -> Dom.Text.cmdExecutionFailed

    let private authorAvatar =
        function
        | HumanPeer p -> Style.humanAvatar (PeerId.value p)
        | ActorRef.Agent -> Style.agentAvatar
        | ActorRef.SessionProcess | ActorRef.System -> Style.humanAvatar "session"

    // --- Sidebar ------------------------------------------------------------------------

    let private connectionSection (model: ClientModel) : TemplateResult =
        let consumer = model.EventConsumer
        let catchUpClass = if consumer.IsCatchingUp then Style.statusRun else Style.statusOk
        html $"""
            <section class="{Style.sideSectionFirst}">
              <span class="{Style.body}" data-connection>{connectionLabel model.Connection}</span>
              <span class="{catchUpClass}" data-catch-up>{catchUpText consumer}</span>
              <span class="{Style.label} tabular-nums">processed <b class="text-ink-dim" data-last-processed-offset>{offsetText consumer.LastProcessedOffset}</b> · latest <b class="text-ink-dim" data-latest-known-offset>{offsetText consumer.LatestKnownOffset}</b></span>
            </section>"""

    let private peopleSection (model: ClientModel) : TemplateResult =
        html $"""
            <section class="{Style.sideSection}">
              <span class="{Style.label}">people</span>
              <div class="{Style.person}"><span class="{Style.cls [ Style.avatar; Style.humanAvatar (PeerId.value model.Peer.PeerId) ]}"></span><span class="truncate" data-display-name>{model.Peer.DisplayName}</span><span class="{Style.label}">you</span></div>
              <div class="{Style.person}"><span class="{Style.cls [ Style.avatar; Style.agentAvatar ]}"></span>agent</div>
            </section>"""

    let private environmentStatus =
        function
        | EnvironmentNotStarted -> Style.statusFaint, html $"""not started"""
        | EnvironmentStarting -> Style.statusRun, html $"""<span class="{Style.statusDotPulse}"></span>starting"""
        | EnvironmentRunning _ -> Style.statusOk, html $"""<span class="{Style.statusDot}"></span>running"""
        | EnvironmentFailed _ -> Style.statusErr, html $"""failed"""
        | EnvironmentDown -> Style.statusFaint, html $"""stopped"""

    let private environmentSection (status: EnvironmentStatus) : TemplateResult =
        let statusClass, statusInner = environmentStatus status
        html $"""
            <section class="{Style.sideSection}" data-environment="{environmentLabel status}">
              <div class="{Style.sideRow}"><span class="{Style.label}">environment</span><span class="{statusClass}">{statusInner}</span></div>
            </section>"""

    let private commandStatusInner =
        function
        | CommandPending -> html $"""<span class="{Style.statusFaint}">pending</span>"""
        | CommandRunning -> html $"""<span class="{Style.statusRun}"><span class="{Style.statusDotPulse}"></span>running</span>"""
        | CommandFinished (CommandSucceeded code) -> html $"""<span class="{Style.statusOk}">✓ {code}</span>"""
        | CommandFinished (CommandFailed code) -> html $"""<span class="{Style.statusErr}">✗ {code}</span>"""
        | CommandFinished CommandTimedOut -> html $"""<span class="{Style.statusErr}">timed out</span>"""
        | CommandFinished (CommandExecutionFailed _) -> html $"""<span class="{Style.statusErr}">failed</span>"""

    let private commandsSection (log: CommandLog) : TemplateResult =
        let entries =
            log.Entries
            |> List.map (fun entry ->
                let output =
                    entry.Output
                    |> List.map (fun (stream, text) ->
                        html $"""<pre class="{Style.monoOut}" data-stream="{match stream with Stdout -> Dom.Text.stdout | Stderr -> Dom.Text.stderr}">{text}</pre>""")
                html $"""
                    <article class="{Style.commandCard}" data-command-id="{CommandId.value entry.CommandId}" data-command-status="{commandStatusLabel entry.Status}">
                      <div class="{Style.sideRow}"><code class="{Style.mono}">{entry.Executable} {String.concat " " entry.Arguments}</code>{commandStatusInner entry.Status}</div>
                      {output}
                    </article>""")
        html $"""
            <section class="{Style.sideSection}" data-command-log>
              <span class="{Style.label}">commands</span>
              {entries}
            </section>"""

    let private sidebar (actions: ViewActions) (model: ClientModel) : TemplateResult =
        html $"""
            <div class="{Style.scrim}" data-nav-toggle @click={Ev(fun _ -> actions.ToggleNav ())}></div>
            <aside class="{Style.sidebar}">
              <div class="{Style.sideHead}"><span class="{Style.wordmark}">yession<span class="text-green">.</span></span><button type="button" class="{Style.navChevron}" aria-label="Hide sidebar" data-nav-toggle @click={Ev(fun _ -> actions.ToggleNav ())}>‹</button></div>
              {connectionSection model}
              {peopleSection model}
              {environmentSection model.Environment}
              {commandsSection model.Commands}
              <div class="flex-1"></div>
              <span class="{Style.label} pt-4">local first · every fact is an event</span>
            </aside>"""

    // --- Conversation column ------------------------------------------------------------

    let private headerStatus (model: ClientModel) : TemplateResult =
        match model.Connection with
        | Connected when model.EventConsumer.IsCatchingUp ->
            html $"""<span class="{Style.cls [ Style.statusRun; Style.headerStatus ]}"><span class="{Style.statusDotPulse}"></span>catching up</span>"""
        | Connected ->
            html $"""<span class="{Style.cls [ Style.statusOk; Style.headerStatus ]}"><span class="{Style.statusDot}"></span>up to date</span>"""
        | Connecting ->
            html $"""<span class="{Style.cls [ Style.statusRun; Style.headerStatus ]}"><span class="{Style.statusDotPulse}"></span>connecting</span>"""
        | Reconnecting ->
            html $"""<span class="{Style.cls [ Style.statusRun; Style.headerStatus ]}"><span class="{Style.statusDotPulse}"></span>reconnecting</span>"""
        | Disconnected ->
            html $"""<span class="{Style.cls [ Style.statusFaint; Style.headerStatus ]}">disconnected</span>"""

    /// The caret index (`selectionStart`) of the event's target input, or `None` when it
    /// has no collapsed caret. Read live from the DOM; only ever invoked in the browser
    /// (SSR drops event bindings), so the `.NET` type-check sees a signature it never runs.
    [<Fable.Core.Emit("($0 && $0.target && typeof $0.target.selectionStart === 'number') ? $0.target.selectionStart : null")>]
    let private caretOf (e: obj) : int option = Fable.Core.Util.jsNative

    /// One collaborator's caret marker: an empty bar the browser positions and colours by
    /// measurement after render (its `left` and accent are inline styles set there). The
    /// index and peer ride `data-*` so the measurement pass finds and places them.
    let private remoteCursor (peerId: PeerId) (cursor: RemoteCursor) : TemplateResult =
        html $"""
            <span class="{Style.remoteCursor}" data-title-cursor="{string cursor.Index}" data-cursor-peer="{PeerId.value peerId}">
              <span class="{Style.remoteCursorLabel}">{cursor.DisplayName}</span>
            </span>"""

    let private header (actions: ViewActions) (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        let titleStr = Ylmish.Text.toString model.Synced.Title
        let sessionIdText = model.Session |> Option.map SessionId.value |> Option.defaultValue ""
        let cursors = model.Presence |> Map.toList |> List.map (fun (peerId, cursor) -> remoteCursor peerId cursor)
        html $"""
            <header class="{Style.header}">
              <button type="button" class="{Style.cls [ Style.navChevron; Style.navReopen ]}" aria-label="Show sidebar" data-nav-toggle @click={Ev(fun _ -> actions.ToggleNav ())}>›</button>
              <div class="{Style.cls [ Style.titleWrap; Style.headerTitle ]}">
                <input type="text" class="{Style.titleInput}" data-session-title aria-label="Session title" placeholder="session"
                       value="{titleStr}"
                       .value={titleStr}
                       @input={EvVal(fun v -> dispatch (EditTitleMsg (Ylmish.Text.edit v model.Synced.Title)))}
                       @keyup={Ev(fun e -> actions.ReportTitleCursor (caretOf e))}
                       @click={Ev(fun e -> actions.ReportTitleCursor (caretOf e))}
                       @select={Ev(fun e -> actions.ReportTitleCursor (caretOf e))}
                       @focus={Ev(fun e -> actions.ReportTitleCursor (caretOf e))}
                       @blur={Ev(fun _ -> actions.ReportTitleCursor None)}>
                {cursors}
                <span class="{Style.titleId}" data-session-id>{sessionIdText}</span>
              </div>
              {headerStatus model}
            </header>"""

    let private conversation (projection: ConversationProjection) : TemplateResult =
        let items =
            projection.Items
            |> List.map (fun item ->
                let isAgent = (item.Author = ActorRef.Agent)
                let whoClass = if isAgent then Style.whoAgent else Style.who
                let statusInner =
                    match item.Status with
                    | Complete -> Lit.nothing
                    | Streaming -> html $"""<span class="{Style.statusRun}"><span class="{Style.statusDotPulse}"></span>streaming</span>"""
                    | ConversationItemStatus.Failed -> html $"""<span class="{Style.statusErr}">failed</span>"""
                    | ConversationItemStatus.Interrupted -> html $"""<span class="{Style.statusFaint}">interrupted</span>"""
                let bodyClass, caret =
                    match item.Status with
                    | Streaming -> Style.messageBodyStreaming, html $"""<span class="{Style.caret}"></span>"""
                    | _ -> Style.messageBody, Lit.nothing
                html $"""
                    <article class="{Style.message}" data-message-id="{MessageId.value item.MessageId}" data-message-author="{authorLabel item.Author}" data-message-status="{messageStatusLabel item.Status}">
                      <span class="{Style.cls [ Style.avatar; Style.messageAvatar; authorAvatar item.Author ]}"></span>
                      <div class="{Style.messageMeta}"><span class="{whoClass}">{authorLabel item.Author}</span>{statusInner}</div>
                      <div class="{bodyClass}" data-message-body>{item.Body}{caret}</div>
                    </article>""")
        html $"""<section class="{Style.timeline}" data-conversation>{items}</section>"""

    let private agentStrip (actions: ViewActions) (agent: AgentViewState) : TemplateResult =
        match agent.ActiveTurn with
        | Some turn ->
            html $"""
                <section class="{Style.activity}" data-agent-stream>
                  <span class="{Style.activityPulse}"></span>
                  <span class="{Style.activityText}" data-agent-turn="{AgentTurnId.value turn}">agent is responding</span>
                  <span class="{Style.activityTurn}">turn {AgentTurnId.value turn}</span>
                  <button type="button" class="{Style.btnDanger} ml-auto" data-interrupt-turn="{AgentTurnId.value turn}" @click={Ev(fun _ -> actions.Interrupt turn)}>Interrupt</button>
                </section>"""
        | None -> html $"""<section class="hidden" data-agent-stream></section>"""

    let private queue (dispatch: ClientMsg -> unit) (synced: SyncedSessionState) : TemplateResult =
        let entries = QueueOrder.sorted synced.Queue
        let head =
            match entries with
            | [] -> Lit.nothing
            | _ ->
                html $"""<div class="{Style.queueHead}"><span class="{Style.queueCount}">queued · {List.length entries}</span><span class="{Style.small}">editable until the agent takes them</span></div>"""
        let items =
            entries
            |> List.map (fun entry ->
                let id = entry.QueueId
                html $"""
                    <article class="{Style.queueItem}" data-queue-id="{QueueId.value id}" data-queue-author="{PeerId.value entry.Author}" data-queue-order="{string entry.Order}">
                      <span class="{Style.cls [ Style.avatarSm; Style.humanAvatar (PeerId.value entry.Author) ]}"></span>
                      <div class="{Style.queueInput}" data-rich-body="{BodyKey.queued id}" data-rich-readonly="false" data-queue-input="{QueueId.value id}"></div>
                      <div class="{Style.queueTools}">
                        <button type="button" class="{Style.cls [ Style.btn; Style.btnIcon ]}" aria-label="Move up" data-queue-up="{QueueId.value id}" @click={Ev(fun _ -> match QueueOrder.moveUp synced.Queue id with Some o -> dispatch (ReorderQueuedMsg (id, o)) | None -> ())}>↑</button>
                        <button type="button" class="{Style.cls [ Style.btn; Style.btnIcon ]}" aria-label="Move down" data-queue-down="{QueueId.value id}" @click={Ev(fun _ -> match QueueOrder.moveDown synced.Queue id with Some o -> dispatch (ReorderQueuedMsg (id, o)) | None -> ())}>↓</button>
                        <button type="button" class="{Style.cls [ Style.btnDanger; Style.btnIcon ]}" aria-label="Delete" data-queue-delete="{QueueId.value id}" @click={Ev(fun _ -> dispatch (DeleteQueuedMsg id))}>✕</button>
                      </div>
                    </article>""")
        html $"""<section class="{Style.queue}" data-message-queue>{head}{items}</section>"""

    let private drafts (actions: ViewActions) (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        let myPeer = model.Peer.PeerId
        // Other peers' drafts: a read-only rich editor bound to their body fragment (the
        // browser mounts it onto this host). Author-badged, not sendable — owner-sends.
        let others =
            model.Synced.Drafts
            |> Map.toList
            |> List.filter (fun (peerId, _) -> peerId <> myPeer)
            |> List.map (fun (peerId, _) ->
                html $"""
                    <article class="{Style.draftBox}" data-draft-id="{PeerId.value peerId}" data-draft-author="{PeerId.value peerId}">
                      <span class="{Style.draftEdge}"></span>
                      <div class="{Style.draftInput}" data-rich-body="{BodyKey.draft peerId}" data-rich-readonly="true" data-draft-input="{PeerId.value peerId}"></div>
                      <div class="{Style.draftActions}"><span class="{Style.draftAuthor}">{PeerId.value peerId}</span></div>
                    </article>""")
        // The local composer: always present; a rich ProseMirror editor bound to the local
        // peer's body fragment (mounted imperatively by the browser). Send + Discard.
        let mine =
            html $"""
                <article class="{Style.draftBox}" data-draft-id="{PeerId.value myPeer}" data-draft-author="{PeerId.value myPeer}">
                  <span class="{Style.draftEdge}"></span>
                  <div class="{Style.draftInput}" data-rich-body="{BodyKey.draft myPeer}" data-rich-readonly="false" data-draft-input="{PeerId.value myPeer}"></div>
                  <div class="{Style.draftActions}">
                    <button type="button" class="{Style.btnPrimary}" data-send-draft="{PeerId.value myPeer}" @click={Ev(fun _ -> actions.SendDraft myPeer)}>Send</button>
                    <button type="button" class="{Style.cls [ Style.btn; Style.btnIcon ]}" aria-label="Discard draft" data-discard-draft @click={Ev(fun _ -> dispatch (DiscardDraftMsg myPeer))}>✕</button>
                  </div>
                </article>"""
        html $"""
            <section class="{Style.composer}" data-draft-editor>
              {others}
              {mine}
            </section>"""

    /// The client shell, rendered into `#app`.
    let view (actions: ViewActions) (model: ClientModel) (dispatch: ClientMsg -> unit) : TemplateResult =
        html $"""
            {sidebar actions model}
            <div class="{Style.mainColumn}">
              {header actions dispatch model}
              {conversation model.Conversation}
              {agentStrip actions model.Agent}
              {queue dispatch model.Synced}
              {drafts actions dispatch model}
            </div>"""
