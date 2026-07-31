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
      /// Discard the local peer's draft: EMPTY its body, which retracts the slot through the
      /// same publication rule typing published it with (`DraftSlot`). Imperative for the same
      /// reason `SendDraft` is — the body is a fragment the reducer cannot touch. Retracting
      /// the slot alone (what the button used to do) left the text sitting in the composer and
      /// the next keystroke published it straight back, so the button looked broken.
      DiscardDraft : PeerId -> unit
      /// Ask the Session Process to cancel the running agent turn.
      Interrupt : AgentTurnId -> unit
      /// Collapse or reveal the sidebar column (a presentation bit on the shell root, not
      /// model; the browser also remembers a desktop collapse and moves focus to whichever
      /// control replaces the one that was pressed).
      ToggleNav : unit -> unit
      /// Broadcast the local selection in the title as `(anchor, head)` UTF-16 indices
      /// (`None` = caret left the title), so collaborators see the cursor. The Browser turns
      /// the indices into relative positions and relays them; ephemeral presence.
      ReportTitleSelection : (int * int) option -> unit
      /// Turn the sidebar column to its settings face, or back (a presentation bit on the
      /// shell root, like the nav); the browser also brings that column on screen and
      /// re-probes the Claude status on toggle, so settings always opens fresh.
      ToggleSettings : unit -> unit
      /// Claude connection panel (Plan 08). Imperative because they read panel inputs and
      /// drive the /claude round-trips; the reducer only folds the resulting messages.
      /// Begin the sign-in flow for the scope in the panel's selector.
      ClaudeConnect : unit -> unit
      /// Complete a flow with the pasted `code#state` from the panel's code input.
      ClaudeComplete : unit -> unit
      /// Store the pasted setup-token/API key from the panel's token input.
      ClaudePasteToken : unit -> unit
      /// Disconnect the credential stored for a scope choice ("session" | "mine").
      ClaudeDisconnect : string -> unit }

module ViewActions =
    /// A no-op action set for rendering the view to a string (SSR + tests). The handlers
    /// are never invoked while rendering — they fire on user events in the live browser.
    let ssr : ViewActions =
        { SendDraft = ignore
          DiscardDraft = ignore
          Interrupt = ignore
          ToggleNav = ignore
          ReportTitleSelection = ignore
          ToggleSettings = ignore
          ClaudeConnect = ignore
          ClaudeComplete = ignore
          ClaudePasteToken = ignore
          ClaudeDisconnect = ignore }

module View =

    // --- Label helpers: map model cases to the shared `Dom.Text` tokens -----------------

    let private connectionLabel =
        function
        | Disconnected _ -> Dom.Text.disconnected
        | Connecting -> Dom.Text.connecting
        | Connected -> Dom.Text.connected
        | Reconnecting -> Dom.Text.reconnecting

    let private offsetText =
        function
        | Some offset -> string (EventOffset.value offset)
        | None -> Dom.Text.offsetNone

    let private catchUpText (consumer: EventConsumerState) =
        if consumer.IsCatchingUp then Dom.Text.catchingUp else Dom.Text.upToDate

    let private feedToken =
        function
        | FeedLive -> Dom.Text.feedLive
        | FeedRetrying _ -> Dom.Text.feedRetrying
        | FeedStalled _ -> Dom.Text.feedPaused

    let private authorLabel =
        function
        | UserRef u -> UserId.value u
        | PeerRef p -> PeerId.value p
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
        | UserRef u -> Style.humanAvatar (UserId.value u)
        | PeerRef p -> Style.humanAvatar (PeerId.value p)
        | ActorRef.Agent -> Style.agentAvatar
        | ActorRef.SessionProcess | ActorRef.System -> Style.humanAvatar "session"

    // --- Sidebar ------------------------------------------------------------------------

    let private connectionSection (model: ClientModel) : TemplateResult =
        let consumer = model.EventConsumer
        let catchUpClass = if consumer.IsCatchingUp then Style.statusRun else Style.statusOk
        // The two legs are reported separately because they fail separately: `data-connection`
        // is the data channel (collaborative state), `data-feed` is the HTTP history feed.
        let feedClass, feedInner =
            match consumer.Feed with
            | FeedLive -> Style.statusOk, html $"""history live"""
            | FeedRetrying (attempt, reason) ->
                Style.statusRun,
                html $"""<span class="{Style.statusDotPulse}"></span>history retrying · {reason} ({attempt})"""
            | FeedStalled reason -> Style.statusErr, html $"""history paused · {reason}"""
        // A reason is only ever known for a settled disconnection; `data-connection` keeps its
        // exact one-word token so the reason is additive, never a rewrite of the status.
        let connectionReason =
            match model.Connection with
            | Disconnected (Some reason) ->
                html $"""<span class="{Style.small}" data-connection-reason>{reason}</span>"""
            | _ -> Lit.nothing
        html $"""
            <section class="{Style.cls [ Style.sideSectionFirst; Style.navLane1 ]}">
              <span class="{Style.body}" data-connection>{connectionLabel model.Connection}</span>
              {connectionReason}
              <span class="{catchUpClass}" data-catch-up>{catchUpText consumer}</span>
              <span class="{feedClass}" data-feed="{feedToken consumer.Feed}">{feedInner}</span>
              <span class="{Style.label} tabular-nums">processed <b class="text-ink-dim" data-last-processed-offset>{offsetText consumer.LastProcessedOffset}</b> · latest <b class="text-ink-dim" data-latest-known-offset>{offsetText consumer.LatestKnownOffset}</b></span>
            </section>"""

    /// Who is in this session — and, when the agent is not, the ONE place the product asks for
    /// a connection. A missing member belongs in the membership list, so the absent state is not
    /// a status word here but a card with a real call to action: what is missing, what it costs
    /// while it is missing, and the button that fixes it.
    let private peopleSection (actions: ViewActions) (model: ClientModel) : TemplateResult =
        // The agent's row tells the truth about its presence: live (green), absent (the
        // call to action), or unknown until the first probe answers.
        let agentRow =
            match model.Claude.Status.AgentAvailable with
            | Some true ->
                html $"""<div class="{Style.person}" data-agent-presence="live"><span class="{Style.cls [ Style.avatar; Style.agentAvatar ]}"></span>agent<span class="{Style.statusOk} ml-auto"><span class="{Style.statusDot}"></span>ready</span></div>"""
            // What actually happens with no agent: the drain appends the message with no turn
            // (`Scheduler.create` — a `None` runner at drain time), so it is recorded and simply
            // unanswered. The old strip promised messages "will wait", which is not what the
            // queue does; the copy says what it does.
            | Some false ->
                html $"""
                    <div class="{Style.noAgentCard}" data-agent-presence="absent" data-no-agent>
                      <div class="{Style.person}"><span class="{Style.cls [ Style.avatar; Style.agentAvatar ]} opacity-40"></span><span class="{Style.statusRun}">no agent</span></div>
                      <span class="{Style.small}">messages still send — nothing answers them until a Claude account is connected.</span>
                      <button type="button" class="{Style.cls [ Style.btnPrimary; Style.noAgentAction ]}" data-settings-toggle="open" data-no-agent-connect @click={Ev(fun _ -> actions.ToggleSettings ())}>Connect Claude</button>
                    </div>"""
            | None ->
                html $"""<div class="{Style.person}" data-agent-presence="unknown"><span class="{Style.cls [ Style.avatar; Style.agentAvatar ]} opacity-40"></span><span class="text-ink-faint">agent</span></div>"""
        html $"""
            <section class="{Style.cls [ Style.sideSection; Style.navLane1 ]}">
              <span class="{Style.label}">in this session</span>
              <div class="{Style.person}"><span class="{Style.cls [ Style.avatar; Style.humanAvatar (PeerId.value model.Peer.PeerId) ]}"></span><span class="truncate" data-display-name>{model.Peer.DisplayName}</span><span class="{Style.label}">you</span></div>
              {agentRow}
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
            <section class="{Style.cls [ Style.sideSection; Style.navLane2 ]}" data-environment="{environmentLabel status}">
              <div class="{Style.sideRow}"><span class="{Style.label}">environment</span><span class="{statusClass}">{statusInner}</span></div>
            </section>"""

    let private commandStatusInner =
        function
        | CommandPending -> html $"""<span class="{Style.statusFaint}">pending</span>"""
        | CommandRunning -> html $"""<span class="{Style.statusRun}"><span class="{Style.statusDotPulse}"></span>running</span>"""
        | CommandFinished (CommandSucceeded code) -> html $"""<span class="{Style.statusOk}">{Icon.checkSm} {code}</span>"""
        | CommandFinished (CommandFailed code) -> html $"""<span class="{Style.statusErr}">{Icon.crossSm} {code}</span>"""
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
            <section class="{Style.cls [ Style.sideSection; Style.navLane2 ]}" data-command-log>
              <span class="{Style.label}">commands</span>
              {entries}
            </section>"""

    /// The Claude connection panel (Plan 08), living in the settings drawer: status per
    /// sign-in scope, the OAuth flow (approve on claude.ai → paste the shown code), and
    /// the paste-a-token fallback.
    let private claudeSection (actions: ViewActions) (dispatch: ClientMsg -> unit) (claude: ClaudeViewState) : TemplateResult =
        let connectedRow (label: string) (scopeChoice: string) (kind: string option) =
            match kind with
            | Some kind ->
                html $"""<div class="{Style.sideRow}" data-claude-connected="{scopeChoice}"><span class="{Style.statusOk}"><span class="{Style.statusDot}"></span>{label} ({kind})</span><button type="button" class="{Style.cls [ Style.btnDanger; Style.btnIcon ]}" aria-label="Disconnect" data-claude-disconnect="{scopeChoice}" @click={Ev(fun _ -> actions.ClaudeDisconnect scopeChoice)}>{Icon.close}</button></div>"""
            | None -> html $""""""
        let controls =
            match claude.Flow with
            | ClaudeBusy ->
                html $"""<span class="{Style.statusRun}" data-claude-busy><span class="{Style.statusDotPulse}"></span>working…</span>"""
            | ClaudeAwaitingCode (url, _) ->
                html $"""
                    <a class="{Style.btnPrimary} text-center" href="{url}" target="_blank" rel="noreferrer" data-claude-authorize>Approve on claude.ai</a>
                    <span class="{Style.small}">a code appears after you approve — paste it here</span>
                    <input type="text" class="{Style.field}" data-claude-code placeholder="code#state" />
                    <div class="flex gap-2">
                      <button type="button" class="{Style.btnPrimary}" data-claude-complete @click={Ev(fun _ -> actions.ClaudeComplete ())}>Complete</button>
                      <button type="button" class="{Style.btn}" data-claude-cancel @click={Ev(fun _ -> dispatch (ClaudeFlowMsg ClaudeIdle))}>Cancel</button>
                    </div>"""
            | ClaudeIdle | ClaudeError _ ->
                html $"""
                    <label class="{Style.label}" for="claude-scope">sign in for</label>
                    <select id="claude-scope" class="{Style.field}" data-claude-scope aria-label="Sign-in scope">
                      <option value="mine">All my sessions</option>
                      <option value="session">This session only</option>
                    </select>
                    <button type="button" class="{Style.btnPrimary}" data-claude-connect @click={Ev(fun _ -> actions.ClaudeConnect ())}>Connect Claude</button>
                    <span class="{Style.small} pt-2">or paste a setup token / API key</span>
                    <input type="password" class="{Style.field}" data-claude-token placeholder="sk-ant-…" />
                    <button type="button" class="{Style.btn}" data-claude-save-token @click={Ev(fun _ -> actions.ClaudePasteToken ())}>Save token</button>"""
        let error =
            match claude.Flow with
            | ClaudeError reason -> html $"""<span class="{Style.statusErr}" data-claude-error>{reason}</span>"""
            | _ -> html $""""""
        html $"""
            <section class="{Style.cls [ Style.sideSection; Style.settingsLane1 ]}" data-claude-panel>
              <span class="{Style.label}">claude</span>
              <span class="{Style.small}">the agent answers with whoever sent the message — connect your account here</span>
              {connectedRow "all my sessions" "mine" claude.Status.MineCredential}
              {connectedRow "this session" "session" claude.Status.SessionCredential}
              {error}
              {controls}
            </section>"""

    /// Settings, as the sidebar column's OTHER FACE. Not a drawer over the conversation: you
    /// go there and come back, the timeline never moves under a scrim, and configuration keeps
    /// the section rhythm it already had. Open state is the root element's `settings-open`
    /// class — presentation, not model — so it survives re-renders.
    ///
    /// It is laid out as the nav face's mirror: identity in the head (where the wordmark sits),
    /// the way out at the foot (where `settings ›` sits), and the column's own collapse control
    /// in the same corner on both faces — chrome that belongs to the column, not to a face, so
    /// it never disappears under you.
    let private settingsPane (actions: ViewActions) (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        html $"""
            <div class="{Style.settingsPane}" data-settings-panel>
              <div class="{Style.cls [ Style.settingsHead; Style.settingsLane0 ]}">
                <span class="{Style.settingsTitle}">settings</span>
                <button type="button" class="{Style.navChevronBack}" aria-label="Collapse sidebar" data-nav-toggle="hide" @click={Ev(fun _ -> actions.ToggleNav ())}>{Icon.left}</button>
              </div>
              {claudeSection actions dispatch model.Claude}
              <div class="flex-1"></div>
              <button type="button" class="{Style.cls [ Style.navPivot; Style.settingsLane2 ]}" aria-label="Back to session" data-settings-toggle="close" @click={Ev(fun _ -> actions.ToggleSettings ())}><span class="{Style.pivotMarkBack}">{Icon.pivotLeft}</span>back</button>
              <span class="{Style.cls [ Style.label; Style.settingsLane2 ]} pt-3">credentials are sealed by the manager</span>
            </div>"""

    /// The workspace face of the column: identity, sync health, membership, environment, log.
    let private navPane (actions: ViewActions) (model: ClientModel) : TemplateResult =
        html $"""
            <div class="{Style.navPane}">
              <div class="{Style.cls [ Style.sideHead; Style.navLane0 ]}">
                <span class="{Style.wordmark}">yession<span class="text-green">.</span></span>
                <button type="button" class="{Style.navChevronBack}" aria-label="Collapse sidebar" data-nav-toggle="hide" @click={Ev(fun _ -> actions.ToggleNav ())}>{Icon.left}</button>
              </div>
              {connectionSection model}
              {peopleSection actions model}
              {environmentSection model.Environment}
              {commandsSection model.Commands}
              <div class="flex-1"></div>
              <button type="button" class="{Style.cls [ Style.navPivot; Style.navLane2 ]}" data-settings-toggle="open" @click={Ev(fun _ -> actions.ToggleSettings ())}>settings<span class="{Style.pivotMarkForward}">{Icon.pivotRight}</span></button>
              <span class="{Style.cls [ Style.label; Style.navLane2 ]} pt-3">local first · every fact is an event</span>
            </div>"""

    /// The sidebar column: one region, two faces, and — on mobile — the scrim behind it.
    let private sidebar (actions: ViewActions) (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        html $"""
            <div class="{Style.scrim}" data-nav-toggle="hide" @click={Ev(fun _ -> actions.ToggleNav ())}></div>
            <aside class="{Style.sidebar}">
              {navPane actions model}
              {settingsPane actions dispatch model}
            </aside>"""

    // --- Conversation column ------------------------------------------------------------

    /// ONE degradation strip over the timeline: whichever leg is down, said once, with what
    /// still works. Nothing here disables anything below it — the composer, the queue, and the
    /// title are CRDT state in a local doc, not reads off the network.
    let private degradedBanner (model: ClientModel) : TemplateResult =
        let strip (token: string) (status: TemplateResult) (detail: string) =
            html $"""
                <section class="{Style.degradedBanner}" data-degraded="{token}">
                  {status}
                  <span class="{Style.small}">{detail}</span>
                </section>"""
        match model.Connection, model.EventConsumer.Feed with
        // The session leg subsumes the history leg: a Process that cannot be reached cannot
        // serve its feed either, and one strip is the honest report of one problem.
        | Disconnected (Some reason), _ ->
            strip
                Dom.Text.degradedOffline
                (html $"""<span class="{Style.statusErr}">not connected</span>""")
                (reason + " · " + Dom.Text.localFallback)
        | Reconnecting, _ ->
            strip
                Dom.Text.degradedReconnecting
                (html $"""<span class="{Style.statusRun}"><span class="{Style.statusDotPulse}"></span>reconnecting</span>""")
                Dom.Text.localFallback
        | _, FeedRetrying (attempt, reason) ->
            strip
                Dom.Text.feedRetrying
                (html $"""<span class="{Style.statusRun}"><span class="{Style.statusDotPulse}"></span>history retrying</span>""")
                (sprintf "%s · attempt %d · %s" reason attempt Dom.Text.localFallback)
        | _, FeedStalled reason ->
            strip
                Dom.Text.feedPaused
                (html $"""<span class="{Style.statusErr}">history paused</span>""")
                (reason + " · " + Dom.Text.localFallback)
        | _, FeedLive -> Lit.nothing

    let private headerStatus (model: ClientModel) : TemplateResult =
        // A stalled feed outranks the connection line: "up to date" would be a lie while
        // history is not arriving, even though the data channel is perfectly healthy.
        match model.EventConsumer.Feed, model.Connection with
        | FeedStalled _, _ ->
            html $"""<span class="{Style.cls [ Style.statusErr; Style.headerStatus ]}">history paused</span>"""
        | FeedRetrying _, _ ->
            html $"""<span class="{Style.cls [ Style.statusRun; Style.headerStatus ]}"><span class="{Style.statusDotPulse}"></span>history retrying</span>"""
        | FeedLive, Connected when model.EventConsumer.IsCatchingUp ->
            html $"""<span class="{Style.cls [ Style.statusRun; Style.headerStatus ]}"><span class="{Style.statusDotPulse}"></span>catching up</span>"""
        | FeedLive, Connected ->
            // The one status worth suppressing on a phone: "everything is fine" is the least
            // actionable thing in a 390px header, and it costs the session title the room it
            // needs. Every UNhealthy state above stays, at every width, and the sidebar still
            // reports this one in full.
            html $"""<span class="{Style.cls [ Style.statusOk; Style.headerStatus ]} max-md:hidden"><span class="{Style.statusDot}"></span>up to date</span>"""
        | FeedLive, Connecting ->
            html $"""<span class="{Style.cls [ Style.statusRun; Style.headerStatus ]}"><span class="{Style.statusDotPulse}"></span>connecting</span>"""
        | FeedLive, Reconnecting ->
            html $"""<span class="{Style.cls [ Style.statusRun; Style.headerStatus ]}"><span class="{Style.statusDotPulse}"></span>reconnecting</span>"""
        | FeedLive, Disconnected _ ->
            html $"""<span class="{Style.cls [ Style.statusFaint; Style.headerStatus ]}">disconnected</span>"""

    /// The `(selectionStart, selectionEnd)` of the event's target input, or `None`. Read live
    /// from the DOM; only ever invoked in the browser (SSR drops event bindings), so the `.NET`
    /// type-check sees a signature it never runs. (A Fable tuple is a 2-array at runtime.)
    [<Fable.Core.Emit("($0 && $0.target && typeof $0.target.selectionStart === 'number') ? [$0.target.selectionStart, $0.target.selectionEnd] : null")>]
    let private selectionOf (e: obj) : (int * int) option = Fable.Core.Util.jsNative

    /// One collaborator's title caret+selection marker: a selection highlight span and a caret
    /// bar with a name label. The browser positions all three by measurement after render (from
    /// the peer's relative positions, decoded against the title `Y.Text`); colour is fixed here.
    let private remoteCursor (peerId: PeerId) (presence: RemotePresence) : TemplateResult =
        let colour = PeerColour.ofPeer peerId
        // Container = the translucent selection highlight (positioned `lo..hi` by the browser);
        // the caret bar is offset to `head` inside it; the label rides above the caret.
        html $"""
            <span class="{Style.remoteCursor}" data-cursor-peer="{PeerId.value peerId}" style="background:{PeerColour.translucent peerId}">
              <span class="{Style.remoteCursorCaret}" style="background:{colour}">
                <span class="{Style.remoteCursorLabel}" style="background:{colour}">{presence.DisplayName}</span>
              </span>
            </span>"""

    /// The agent's absence, said in the header only while the sidebar — where the real call to
    /// action lives — is off screen. A phone's sidebar is off-canvas by default, so without this
    /// the one prompt would be one a phone never sees; the CSS in `Style.headerNoAgent` makes the
    /// two mutually exclusive, so it is never said twice.
    let private agentAbsence (actions: ViewActions) (claude: ClaudeViewState) : TemplateResult =
        match claude.Status.AgentAvailable with
        | Some false ->
            html $"""<button type="button" class="{Style.headerNoAgent}" data-settings-toggle="open" @click={Ev(fun _ -> actions.ToggleSettings ())}>no agent</button>"""
        | _ -> Lit.nothing

    let private header (actions: ViewActions) (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        let titleStr = Ylmish.Text.toString model.Synced.Title
        let sessionIdText = model.Session |> Option.map SessionId.value |> Option.defaultValue ""
        // Only peers whose caret is in the title get a marker here; each other field renders
        // its own overlay (bodies decorate their editors).
        let cursors =
            model.Presence
            |> Map.toList
            |> List.filter (fun (_, p) -> p.Focus.Field = Title)
            |> List.map (fun (peerId, p) -> remoteCursor peerId p)
        html $"""
            <header class="{Style.header}">
              <button type="button" class="{Style.cls [ Style.navChevronForward; Style.navReopen ]}" aria-label="Show sidebar" data-nav-toggle="show" @click={Ev(fun _ -> actions.ToggleNav ())}>{Icon.right}</button>
              <div class="{Style.cls [ Style.titleWrap; Style.headerTitle ]}">
                <input type="text" class="{Style.titleInput}" data-session-title aria-label="Session title" placeholder="session"
                       value="{titleStr}"
                       .value={titleStr}
                       @input={EvVal(fun v -> dispatch (EditTitleMsg (Ylmish.Text.edit v model.Synced.Title)))}
                       @keyup={Ev(fun e -> actions.ReportTitleSelection (selectionOf e))}
                       @click={Ev(fun e -> actions.ReportTitleSelection (selectionOf e))}
                       @select={Ev(fun e -> actions.ReportTitleSelection (selectionOf e))}
                       @focus={Ev(fun e -> actions.ReportTitleSelection (selectionOf e))}
                       @blur={Ev(fun _ -> actions.ReportTitleSelection None)}>
                {cursors}
                <span class="{Style.titleId}" data-session-id>{sessionIdText}</span>
              </div>
              <div class="{Style.headerAside}">
                {agentAbsence actions model.Claude}
                {headerStatus model}
              </div>
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
                      <div class="{bodyClass}" data-message-body>{RichText.render item.Body}{caret}</div>
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
                        <button type="button" class="{Style.cls [ Style.btn; Style.btnIcon ]}" aria-label="Move up" data-queue-up="{QueueId.value id}" @click={Ev(fun _ -> match QueueOrder.moveUp synced.Queue id with Some o -> dispatch (ReorderQueuedMsg (id, o)) | None -> ())}>{Icon.up}</button>
                        <button type="button" class="{Style.cls [ Style.btn; Style.btnIcon ]}" aria-label="Move down" data-queue-down="{QueueId.value id}" @click={Ev(fun _ -> match QueueOrder.moveDown synced.Queue id with Some o -> dispatch (ReorderQueuedMsg (id, o)) | None -> ())}>{Icon.down}</button>
                        <button type="button" class="{Style.cls [ Style.btnDanger; Style.btnIcon ]}" aria-label="Delete" data-queue-delete="{QueueId.value id}" @click={Ev(fun _ -> dispatch (DeleteQueuedMsg id))}>{Icon.close}</button>
                      </div>
                    </article>""")
        html $"""<section class="{Style.queue}" data-message-queue>{head}{items}</section>"""

    /// The composer: ONE draft open, everyone else's as a line you can open.
    ///
    /// A draft is shared WIP — any peer may edit any draft (the body is a CRDT; the carets are
    /// presence) and any peer may send one, so the open draft is a full composer whoever's it is.
    /// What differs by ownership is destruction: discard is the author's alone.
    let private drafts (actions: ViewActions) (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        let myPeer = model.Peer.PeerId
        let target = ClientModel.composerTarget model
        // Live carets in a draft, coloured per peer: "grace and ada are in this one".
        let editors (peerId: PeerId) =
            ClientModel.editorsOf peerId model
            |> List.map (fun (editor, name) ->
                html $"""
                    <span class="{Style.draftEditorDot}" style="background:{PeerColour.ofPeer editor}"
                          title="{name}" data-draft-editor-peer="{PeerId.value editor}"></span>""")
        // A collapsed draft: whose it is, one clamped line of it (the same read-only editor the
        // browser mounts everywhere, so the CRDT keeps it current), and who is in it. Opening it
        // collapses whatever was open — including your own composer.
        let summary (peerId: PeerId) =
            html $"""
                <button type="button" class="{Style.draftSummary}" data-draft-summary="{PeerId.value peerId}"
                        data-draft-expand="{PeerId.value peerId}" @click={Ev(fun _ -> dispatch (ExpandDraftMsg peerId))}>
                  <span class="{Style.cls [ Style.avatarSm; Style.humanAvatar (PeerId.value peerId) ]}"></span>
                  <span class="{Style.draftSummaryName}">{ClientModel.nameOf peerId model}</span>
                  <span class="{Style.draftSummaryBody}" data-rich-body="{BodyKey.draft peerId}" data-rich-readonly="true"></span>
                  <span class="{Style.draftEditors}">{editors peerId}</span>
                </button>"""
        // The open draft: an editable rich editor bound to that body fragment (mounted
        // imperatively by the browser), Send for anyone, Discard for its author.
        let open' =
            let discard =
                if target = myPeer then
                    html $"""
                        <button type="button" class="{Style.cls [ Style.btn; Style.btnIcon ]}" aria-label="Discard draft"
                                data-discard-draft @click={Ev(fun _ -> actions.DiscardDraft myPeer)}>{Icon.close}</button>"""
                else Lit.nothing
            let author =
                if target = myPeer then Lit.nothing
                else html $"""<span class="{Style.draftAuthor}">{ClientModel.nameOf target model}'s message</span>"""
            html $"""
                <article class="{Style.draftBox}" data-draft-id="{PeerId.value target}" data-draft-author="{PeerId.value target}">
                  <span class="{Style.draftEdge}"></span>
                  <div class="{Style.draftInput}" data-rich-body="{BodyKey.draft target}" data-rich-readonly="false" data-draft-input="{PeerId.value target}"></div>
                  <div class="{Style.draftActions}">
                    <span class="{Style.draftEditors}">{editors target}</span>
                    {author}
                    <div class="{Style.draftCommit}">
                      {discard}
                      <button type="button" class="{Style.btnPrimary} flex items-center gap-2" data-send-draft="{PeerId.value target}" @click={Ev(fun _ -> actions.SendDraft target)}>Send{Icon.send}</button>
                    </div>
                  </div>
                </article>"""
        // "New message" only says something when you are in someone else's draft: it is the way
        // out of collaborating, and pressing it collapses theirs to a summary.
        let startMine =
            if target = myPeer then Lit.nothing
            else
                html $"""
                    <button type="button" class="{Style.draftNew}" data-draft-new
                            @click={Ev(fun _ -> dispatch StartDraftMsg)}>+ New message</button>"""
        html $"""
            <section class="{Style.composer}" data-draft-editor>
              {ClientModel.collapsedDrafts model |> List.map summary}
              {open'}
              {startMine}
            </section>"""

    /// The client shell, rendered into `#app`.
    let view (actions: ViewActions) (model: ClientModel) (dispatch: ClientMsg -> unit) : TemplateResult =
        html $"""
            {sidebar actions dispatch model}
            <div class="{Style.mainColumn}">
              {header actions dispatch model}
              {degradedBanner model}
              {conversation model.Conversation}
              {agentStrip actions model.Agent}
              {queue dispatch model.Synced}
              {drafts actions dispatch model}
            </div>"""
