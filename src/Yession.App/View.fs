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
      ClaudeDisconnect : string -> unit
      /// Ask the Manager to bring this session back and take the browser to it (Plan 11).
      /// Imperative because it is a navigation, and a navigation is not a state change this
      /// document survives to fold.
      ReopenSession : unit -> unit
      /// Ask the Session Process to open a terminal (Plan 13). A command, so imperative:
      /// the terminal's id is minted by the Process and comes back as an event.
      OpenTerminal : string -> unit
      /// Ask the Session Process to close a terminal.
      CloseTerminal : TerminalId -> unit
      /// Send a terminal composer slot: enqueue its command. Imperative for exactly the
      /// reason `SendDraft` is — the command text is a shared type the reducer cannot move.
      SendTerminalDraft : TerminalId -> PeerId -> unit
      /// Take the terminal's stdin — enter live mode (Plan 13, stage 2e). Also the STEAL:
      /// there is one control because there is one act, and any peer may perform it.
      TakeTerminal : TerminalId -> unit
      /// Hand it back to block mode.
      ReleaseTerminal : TerminalId -> unit
      /// Type the shell instrumentation in again after the terminal stopped marking (Plan 13,
      /// stage 2f). Any peer may — it repairs rather than takes.
      RearmTerminal : TerminalId -> unit }

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
          ClaudeDisconnect = ignore
          ReopenSession = ignore
          OpenTerminal = ignore
          CloseTerminal = ignore
          SendTerminalDraft = fun _ _ -> ()
          TakeTerminal = ignore
          ReleaseTerminal = ignore
          RearmTerminal = ignore }

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

    let private authorAvatar =
        function
        | UserRef u -> Style.humanAvatar (UserId.value u)
        | PeerRef p -> Style.humanAvatar (PeerId.value p)
        | ActorRef.Agent -> Style.agentAvatar
        | ActorRef.SessionProcess | ActorRef.System -> Style.humanAvatar "session"

    // --- Sidebar ------------------------------------------------------------------------

    /// The offer to bring a stopped session back (Plan 11), in place of the connection
    /// status word — the same move `peopleSection` makes for a missing agent: when the
    /// thing being reported is not a state you can wait out, a status word is the wrong
    /// shape, and what belongs there is what is wrong plus the button that fixes it.
    ///
    /// TOTAL over the model, which is what makes the failure modes structural rather than
    /// defensive. The offer needs a settled disconnection (a session still reconnecting has
    /// nothing to reopen), a Manager to ask, and a session to ask for; absent any of them
    /// the ordinary status renders. The view never reads the DOM, so there is no path that
    /// produces a button with nowhere to go — the shell omitting the meta tag is enough.
    let private reconnectOffer (actions: ViewActions) (model: ClientModel) : TemplateResult option =
        match model.Connection, model.Manager, model.Session with
        | Disconnected (Some reason), Some origin, Some sessionId ->
            let target = sprintf "%s/sessions/%s/open" origin (SessionId.value sessionId)
            // What reopening actually costs. Under a `{id}` template the session returns to
            // the same address, so the doc in this browser is still its doc and syncs on
            // reconnect. Addressed by port it returns somewhere new, and everything written
            // here since it went is stranded — say so before they click, not after.
            let reopenPromise =
                if model.EphemeralStorage then
                    "It reopens at a new address, so anything written here since it stopped will not come with it."
                else
                    "Your work is saved here and syncs when it comes back."
            Some (
                html
                    $"""
                    <div class="{Style.noAgentBlock}" data-session-gone>
                      <span class="{Style.syncRow}"><span class="{Style.syncDot} bg-err"></span><span class="{Style.statusErr}">session stopped</span></span>
                      <div class="{Style.noAgentPrompt}">
                        <span class="{Style.noAgentEdge}"></span>
                        <div class="{Style.noAgentBody}">
                          <span class="{Style.small}">{reason}. {reopenPromise}</span>
                          <a class="{Style.cls [ Style.btnPrimary; Style.noAgentAction ]}"
                             href="{target}"
                             data-session-reopen="{target}"
                             @click={Ev(fun _ -> actions.ReopenSession ())}>{Dom.Text.reopenSession}</a>
                        </div>
                      </div>
                    </div>"""
            )
        | _ -> None

    let private connectionSection (actions: ViewActions) (model: ClientModel) : TemplateResult =
        let consumer = model.EventConsumer
        // The two legs are reported separately because they fail separately: `data-connection`
        // is the data channel (collaborative state), `data-feed` (on the section, always
        // carrying its exact token) is the HTTP history feed. But HEALTHY is one quiet line —
        // faint caps behind a green dot. Colour, the feed's own line, and the catch-up
        // offsets appear only while a leg actually needs attention; four stacked green
        // status lines said "everything is fine" louder than anything else on the page.
        let dot, connClass =
            match model.Connection with
            | Connected -> html $"""<span class="{Style.syncDot} bg-green"></span>""", Style.statusFaint
            | Connecting | Reconnecting -> html $"""<span class="{Style.syncDotPulse} bg-blue"></span>""", Style.statusRun
            | Disconnected _ -> html $"""<span class="{Style.syncDot} bg-err"></span>""", Style.statusErr
        // Catch-up rides the same line, and ONLY while it is worth reporting: the offsets are
        // progress, so they exist while there is progress to describe, and a catch-up too
        // brief to have been waited on (every send is one) says nothing rather than blinking
        // the line. Offline, freshness is unknowable and nothing is said either.
        //
        // "Up to date" is deliberately absent: the header says it, and a green dot beside
        // the word "connected" already says it here.
        let catchUp =
            match model.Connection, consumer.IsCatchingUp && consumer.CatchUpIsSlow with
            | Disconnected _, _ | _, false -> Lit.nothing
            | _, true ->
                html $"""<span class="{Style.statusFaint}">·</span><span class="{Style.statusRun}" data-catch-up>{Dom.Text.catchingUp}</span><span class="{Style.label} tabular-nums"><b class="text-ink-dim" data-last-processed-offset>{offsetText consumer.LastProcessedOffset}</b> / <b class="text-ink-dim" data-latest-known-offset>{offsetText consumer.LatestKnownOffset}</b></span>"""
        // A reason is only ever known for a settled disconnection; `data-connection` keeps its
        // exact one-word token so the reason is additive, never a rewrite of the status.
        let connectionReason =
            match model.Connection with
            | Disconnected (Some reason) ->
                html $"""<span class="{Style.small}" data-connection-reason>{reason}</span>"""
            | _ -> Lit.nothing
        let feedLine =
            match consumer.Feed with
            | FeedLive -> Lit.nothing
            | FeedRetrying (attempt, reason) ->
                html $"""<span class="{Style.statusRun}"><span class="{Style.statusDotPulse}"></span>history retrying · {reason} ({attempt})</span>"""
            | FeedStalled reason -> html $"""<span class="{Style.statusErr}">history paused · {reason}</span>"""
        // The offer REPLACES the sync row and the reason line rather than sitting under
        // them: a red dot reading "Disconnected", its reason, and a button to fix it would
        // be saying the same thing three times. The feed line stays — the history leg is a
        // separate leg, and it says something the offer does not.
        let statusOrOffer =
            match reconnectOffer actions model with
            | Some offer -> offer
            | None ->
                html $"""
                  <span class="{Style.syncRow}">{dot}<span class="{connClass}" data-connection>{connectionLabel model.Connection}</span>{catchUp}</span>
                  {connectionReason}"""
        html $"""
            <section class="{Style.cls [ Style.sideSectionFirst; Style.navLane1 ]}" data-feed="{feedToken consumer.Feed}">
              {statusOrOffer}
              {feedLine}
            </section>"""

    /// Where a peer is, as the roster says it: a stable field token for the markup contract,
    /// and the words for a person. The words name things when naming them helps — the
    /// terminal they are in, whose message they are co-writing — because "somewhere" is not
    /// what anyone wanted to know.
    let private whereIs (model: ClientModel) (peer: PeerId) (field: FocusField) : string * string =
        // A terminal is NAMED when this client knows it. One that has not folded the
        // `TerminalOpened` event yet knows the peer is in some terminal and says exactly
        // that, rather than inventing a title or going quiet.
        let terminalWords () =
            ClientModel.terminalOfFocus field model
            |> Option.bind (fun terminal -> TerminalProjection.tryFind terminal model.Terminals)
            |> Option.map (fun view -> Dom.Text.inTerminal view.Title)
            |> Option.defaultValue Dom.Text.atSomeTerminal
        match field with
        | Title -> Dom.Text.atTitle, Dom.Text.renamingSession
        | DraftBody author when author = peer -> Dom.Text.atDraft, Dom.Text.writing
        | DraftBody author when author = model.Peer.PeerId -> Dom.Text.atDraft, Dom.Text.inYourDraft
        | DraftBody author -> Dom.Text.atDraft, Dom.Text.inDraftOf (ClientModel.nameOf author model)
        | QueueBody _ -> Dom.Text.atQueued, Dom.Text.editingQueued
        | TerminalDraftBody _ -> Dom.Text.atTerminal, terminalWords ()
        | TerminalQueuedBody _ -> Dom.Text.atTerminalQueued, terminalWords ()

    /// Who is in this session — and, when the agent is not, the ONE place the product asks for
    /// a connection. A missing member belongs in the membership list, so all three agent states
    /// wear the SAME roster row — avatar cell, name, right-aligned status — and only the words
    /// (and the prompt hanging under the row) change. Connecting flips "no agent" to "ready" in
    /// place; the roster never jumps.
    let private peopleSection (actions: ViewActions) (model: ClientModel) : TemplateResult =
        let agentRow =
            match model.Claude.Status.AgentAvailable with
            | Some true ->
                html $"""<div class="{Style.person}" data-agent-presence="live"><span class="{Style.cls [ Style.avatar; Style.agentAvatar; Style.personAvatar ]}"></span>agent<span class="{Style.statusOk} ml-auto"><span class="{Style.statusDot}"></span>ready</span></div>"""
            // What actually happens with no agent: the drain appends the message with no turn
            // (`Scheduler.create` — a `None` runner at drain time), so it is recorded and simply
            // unanswered. The old strip promised messages "will wait", which is not what the
            // queue does; the copy says what it does.
            | Some false ->
                html $"""
                    <div class="{Style.noAgentBlock}" data-agent-presence="absent" data-no-agent>
                      <div class="{Style.person}"><span class="{Style.cls [ Style.avatar; Style.agentAvatar; Style.personAvatar ]} opacity-40"></span><span class="text-ink-faint">agent</span><span class="{Style.statusRun} ml-auto">no agent</span></div>
                      <div class="{Style.noAgentPrompt}">
                        <span class="{Style.noAgentEdge}"></span>
                        <div class="{Style.noAgentBody}">
                          <span class="{Style.small}">messages still send — they go unanswered until Claude is connected.</span>
                          <button type="button" class="{Style.cls [ Style.btnPrimary; Style.noAgentAction ]}" data-settings-toggle="prompt" data-no-agent-connect @click={Ev(fun _ -> actions.ToggleSettings ())}>Connect Claude</button>
                        </div>
                      </div>
                    </div>"""
            | None ->
                html $"""<div class="{Style.person}" data-agent-presence="unknown"><span class="{Style.cls [ Style.avatar; Style.agentAvatar; Style.personAvatar ]} opacity-40"></span><span class="text-ink-faint">agent</span></div>"""
        // Everyone else who is here, and WHERE. The same roster row as yours and the agent's
        // — avatar, name, right-aligned slot — so the section is one list rather than a list
        // with an appendix, and a collaborator moving from the composer to a terminal changes
        // the words in place without moving anything.
        let peerRows =
            ClientModel.presentPeers model
            |> List.map (fun (peer, name, field) ->
                let token, words = whereIs model peer field
                html $"""
                    <div class="{Style.person}" data-peer-presence="{PeerId.value peer}">
                      <span class="{Style.cls [ Style.avatar; Style.humanAvatar (PeerId.value peer); Style.personAvatar ]}"></span>
                      <span class="truncate min-w-0">{name}</span>
                      <span class="{Style.label} ml-auto shrink-0" data-peer-at="{token}">{words}</span>
                    </div>""")
        html $"""
            <section class="{Style.cls [ Style.sideSection; Style.navLane1 ]}">
              <span class="{Style.label}">in this session</span>
              <div class="{Style.person}"><span class="{Style.cls [ Style.avatar; Style.humanAvatar (PeerId.value model.Peer.PeerId); Style.personAvatar ]}"></span><span class="truncate" data-display-name>{model.Peer.DisplayName}</span><span class="{Style.label} ml-auto">you</span></div>
              {peerRows}
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

    /// The Claude connection panel (Plan 08), living in the settings drawer: status per
    /// sign-in scope, the OAuth flow (approve on claude.ai → paste the shown code), and
    /// the paste-a-token fallback.
    let private claudeSection (actions: ViewActions) (dispatch: ClientMsg -> unit) (claude: ClaudeViewState) : TemplateResult =
        let connectedRow (label: string) (scopeChoice: string) (kind: string option) =
            match kind with
            | Some kind ->
                html $"""<div class="{Style.sideRow}" data-claude-connected="{scopeChoice}"><span class="{Style.statusOk}"><span class="{Style.statusDot}"></span>{label} ({kind})</span><button type="button" class="{Style.btnIconDanger}" aria-label="Disconnect" data-claude-disconnect="{scopeChoice}" @click={Ev(fun _ -> actions.ClaudeDisconnect scopeChoice)}>{Icon.close}</button></div>"""
            | None -> html $""""""
        let controls =
            match claude.Flow with
            | ClaudeBusy ->
                html $"""<span class="{Style.statusRun}" data-claude-busy><span class="{Style.statusDotPulse}"></span>working…</span>"""
            | ClaudeAwaitingCode (url, _) ->
                html $"""
                    <a class="{Style.btnPrimary}" href="{url}" target="_blank" rel="noreferrer" data-claude-authorize>Approve on claude.ai</a>
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
              <span class="{Style.small}">the agent answers each message with its sender's Claude account</span>
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
            </div>"""

    /// The workspace face of the column: identity, sync health, membership, environment, log.
    let private navPane (actions: ViewActions) (model: ClientModel) : TemplateResult =
        html $"""
            <div class="{Style.navPane}">
              <div class="{Style.cls [ Style.sideHead; Style.navLane0 ]}">
                <span class="{Style.wordmark}">yession<span class="text-green">.</span></span>
                <button type="button" class="{Style.navChevronBack}" aria-label="Collapse sidebar" data-nav-toggle="hide" @click={Ev(fun _ -> actions.ToggleNav ())}>{Icon.left}</button>
              </div>
              {connectionSection actions model}
              {peopleSection actions model}
              {environmentSection model.Environment}
              <div class="flex-1"></div>
              <button type="button" class="{Style.cls [ Style.navPivot; Style.navLane2 ]}" data-settings-toggle="open" @click={Ev(fun _ -> actions.ToggleSettings ())}>settings<span class="{Style.pivotMarkForward}">{Icon.pivotRight}</span></button>
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
        // The local-first promise, stated only where it is true. A deployment that addresses
        // sessions by port brings them back at a new origin, and a browser partitions storage
        // by origin — so "everything is saved locally" is exactly wrong there, and wrong at
        // the one moment someone would rely on it.
        let localPromise =
            if model.EphemeralStorage then Dom.Text.localFallbackEphemeral else Dom.Text.localFallback
        let strip (token: string) (status: TemplateResult) (detail: string) =
            html $"""
                <section class="{Style.degradedBanner}" data-degraded="{token}">
                  {status}
                  <span class="{Style.small}">{detail}</span>
                </section>"""
        match model.Connection, model.EventConsumer.Feed with
        // The session leg subsumes the history leg: a Process that cannot be reached cannot
        // serve its feed either, and one strip is the honest report of one problem.
        // Deliberately bare: `reconnectOffer` is on screen for exactly this case, saying
        // what happened AND offering the way back. Repeating the local-first promise here
        // would state it twice on the one screen where it matters most — and, on an
        // ephemeral deployment, would have been wrong twice.
        | Disconnected (Some reason), _ ->
            strip
                Dom.Text.degradedOffline
                (html $"""<span class="{Style.statusErr}">not connected</span>""")
                reason
        | Reconnecting, _ ->
            strip
                Dom.Text.degradedReconnecting
                (html $"""<span class="{Style.statusRun}"><span class="{Style.statusDotPulse}"></span>reconnecting</span>""")
                localPromise
        | _, FeedRetrying (attempt, reason) ->
            strip
                Dom.Text.feedRetrying
                (html $"""<span class="{Style.statusRun}"><span class="{Style.statusDotPulse}"></span>history retrying</span>""")
                (sprintf "%s · attempt %d · %s" reason attempt localPromise)
        | _, FeedStalled reason ->
            strip
                Dom.Text.feedPaused
                (html $"""<span class="{Style.statusErr}">history paused</span>""")
                (reason + " · " + localPromise)
        | _, FeedLive -> Lit.nothing

    let private headerStatus (model: ClientModel) : TemplateResult =
        // A stalled feed outranks the connection line: "up to date" would be a lie while
        // history is not arriving, even though the data channel is perfectly healthy.
        match model.EventConsumer.Feed, model.Connection with
        | FeedStalled _, _ ->
            html $"""<span class="{Style.cls [ Style.statusErr; Style.headerStatus ]}">history paused</span>"""
        | FeedRetrying _, _ ->
            html $"""<span class="{Style.cls [ Style.statusRun; Style.headerStatus ]}"><span class="{Style.statusDotPulse}"></span>history retrying</span>"""
        // Catching up is the NORMAL state for a moment after anything happens — your own
        // send puts you behind your own event until the page comes back — so it is reported
        // only once it has lasted long enough to be something you are waiting on
        // (`CatchUpIsSlow`). Reporting the raw truth made the header flicker green → blue →
        // green on every message sent, which reads as a fault rather than as progress.
        | FeedLive, Connected when model.EventConsumer.IsCatchingUp && model.EventConsumer.CatchUpIsSlow ->
            html $"""<span class="{Style.cls [ Style.statusRun; Style.headerStatus ]}"><span class="{Style.statusDotPulse}"></span>catching up</span>"""
        | FeedLive, Connected ->
            // The ONE place this is said (the sidebar's sync row used to say it too, three
            // words away from the same green dot). Suppressed on a phone: "everything is
            // fine" is the least actionable thing in a 390px header, and it costs the
            // session title the room it needs. Every UNhealthy state above stays, at every
            // width.
            html $"""<span class="{Style.cls [ Style.statusOk; Style.headerStatus ]} max-md:hidden"><span class="{Style.statusDot}"></span>{Dom.Text.upToDate}</span>"""
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
            html $"""<button type="button" class="{Style.headerNoAgent}" data-settings-toggle="prompt" @click={Ev(fun _ -> actions.ToggleSettings ())}>no agent</button>"""
        | _ -> Lit.nothing

    /// The way back into the terminals column once it is shut. Present only while it IS
    /// shut, so there are never two controls for the one column on screen at once.
    let private terminalsReopen (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        if model.TerminalsOpen then Lit.nothing
        else
            html $"""
                <button type="button" class="{Style.terminalReopen}" aria-label="Show terminals"
                        data-terminal-toggle="show" @click={Ev(fun _ -> dispatch ToggleTerminalsMsg)}>{Icon.left}terminals</button>"""

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
                {terminalsReopen dispatch model}
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
                        <button type="button" class="{Style.btnIcon}" aria-label="Move up" data-queue-up="{QueueId.value id}" @click={Ev(fun _ -> match QueueOrder.moveUp synced.Queue id with Some o -> dispatch (ReorderQueuedMsg (id, o)) | None -> ())}>{Icon.up}</button>
                        <button type="button" class="{Style.btnIcon}" aria-label="Move down" data-queue-down="{QueueId.value id}" @click={Ev(fun _ -> match QueueOrder.moveDown synced.Queue id with Some o -> dispatch (ReorderQueuedMsg (id, o)) | None -> ())}>{Icon.down}</button>
                        <button type="button" class="{Style.btnIconDanger}" aria-label="Delete" data-queue-delete="{QueueId.value id}" @click={Ev(fun _ -> dispatch (DeleteQueuedMsg id))}>{Icon.close}</button>
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
                <button type="button" class="{Style.draftSummary}" style="border-left-color:{PeerColour.ofPeer peerId}"
                        data-draft-summary="{PeerId.value peerId}"
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
                        <button type="button" class="{Style.btnIconDangerLg}" aria-label="Discard draft"
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
                    <span class="{Style.draftHint}">{Dom.Text.composerKeys}</span>
                    <div class="{Style.draftCommit}">
                      {discard}
                      <button type="button" class="{Style.btnPrimary} gap-2" aria-keyshortcuts="Enter"
                              data-send-draft="{PeerId.value target}" @click={Ev(fun _ -> actions.SendDraft target)}>Send{Icon.send}</button>
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

    // --- Terminals (Plan 13) -------------------------------------------------------------

    /// Styled terminal output. Each parsed run becomes one span carrying its SGR styling;
    /// lines are separated by real newlines inside a `pre-wrap` block, so selecting and
    /// copying output yields the text a person would expect rather than a run of divs.
    let private ansiText (text: string) : TemplateResult list =
        Ansi.parse text
        |> List.mapi (fun i line ->
            let spans =
                line.Spans
                |> List.map (fun span ->
                    // A run with no styling at all is emitted bare — the overwhelmingly
                    // common case, and one span per plain line is a span too many.
                    let classes = Style.ansiClasses span.Style
                    let inline' = Style.ansiInline span.Style
                    if classes = "" && inline' = "" then html $"{span.Text}"
                    else html $"""<span class="{classes}" style="{inline'}">{span.Text}</span>""")
            // The newline BEFORE every line but the first, so a trailing line adds no
            // trailing blank one.
            if i = 0 then html $"{spans}" else html $"""{"\n"}{spans}""")

    let private terminalBlockStatusLabel =
        function
        | BlockRunning -> Dom.Text.blockRunning
        | BlockFinished (CommandSucceeded _) -> Dom.Text.blockOk
        | BlockFinished _ -> Dom.Text.blockFailed
        | BlockRejected _ -> Dom.Text.blockRejected

    let private terminalBlockStatus =
        function
        | BlockRunning -> html $"""<span class="{Style.statusRun}"><span class="{Style.statusDotPulse}"></span>running</span>"""
        | BlockFinished (CommandSucceeded code) -> html $"""<span class="{Style.statusOk}">{Icon.checkSm} {code}</span>"""
        | BlockFinished (CommandFailed code) -> html $"""<span class="{Style.statusErr}">{Icon.crossSm} {code}</span>"""
        | BlockFinished CommandTimedOut -> html $"""<span class="{Style.statusErr}">timed out</span>"""
        | BlockFinished (CommandExecutionFailed _) -> html $"""<span class="{Style.statusErr}">failed</span>"""
        // Named, not merely absent. "rejected by nick" in line with the commands that ran
        // is the whole reason a refusal mints a block at all.
        | BlockRejected (by, _) -> html $"""<span class="{Style.statusErr}">rejected by {authorLabel by}</span>"""

    /// One block: the command that ran, then everything it printed.
    let private terminalBlockView (feed: TerminalFeed) (block: TerminalBlock) : TemplateResult =
        // A running block's output runs to whatever has arrived; a finished one is bounded
        // by the range its completion event recorded — which is what makes a reload show
        // exactly the same block as the live view did.
        let toSeq = block.ToSeq |> Option.defaultValue (max feed.KnownLength block.FromSeq)
        let output = TerminalFeed.outputText block.FromSeq toSeq feed
        let body =
            if output = "" then
                match block.Status with
                | BlockRunning -> html $"""<div class="{Style.terminalOutputEmpty}" data-terminal-output>…</div>"""
                | BlockFinished _ -> html $"""<div class="{Style.terminalOutputEmpty}" data-terminal-output>no output</div>"""
                // "no output" would be true and useless. A refused command has no output
                // because it never ran, and the reason — when one was given — is the thing
                // the next reader actually wants.
                | BlockRejected (_, reason) ->
                    let text = reason |> Option.defaultValue "did not run"
                    html $"""<div class="{Style.terminalOutputEmpty}" data-terminal-output>{text}</div>"""
            else html $"""<div class="{Style.terminalOutput}" data-terminal-output>{ansiText output}</div>"""
        html $"""
            <article class="{Style.terminalBlock}" data-terminal-block="{BlockId.value block.BlockId}"
                     data-terminal-block-status="{terminalBlockStatusLabel block.Status}">
              <div class="{Style.terminalBlockCommand}">
                <span class="{Style.terminalPrompt}">$</span>
                <code class="{Style.terminalCommandText}">{block.Command}</code>
                <span class="ml-auto shrink-0">{terminalBlockStatus block.Status}</span>
              </div>
              {body}
            </article>"""

    /// The queued commands for a terminal: still editable, still reorderable, and — when the
    /// terminal's mode says so — still waiting for someone to say yes. That waiting state is
    /// the approval UX: there is no modal, because the thing you approve is a thing you can
    /// fix first.
    let private terminalQueue (dispatch: ClientMsg -> unit) (model: ClientModel) (terminal: TerminalId) : TemplateResult list =
        ClientModel.terminalQueue terminal model
        |> List.map (fun entry ->
            let id = entry.QueueId
            let awaiting = ClientModel.awaitsApproval entry model
            // Approval outranks the lease in what is REPORTED, matching the drain's own gate
            // order in reverse: an entry that needs a yes needs it whether or not the terminal
            // is free, so saying "waiting for the terminal" would name the hold that will
            // resolve first rather than the one that is actually blocking.
            let statusToken =
                if awaiting then Dom.Text.queuedAwaitingApproval
                elif ClientModel.awaitsIntegration entry model then Dom.Text.queuedAwaitingIntegration
                elif ClientModel.awaitsTerminal entry model then Dom.Text.queuedAwaitingTerminal
                else Dom.Text.queuedReady
            let approval =
                if awaiting then
                    html $"""
                        <button type="button" class="{Style.btnPrimary}" data-terminal-approve="{QueueId.value id}"
                                @click={Ev(fun _ -> dispatch (ApproveTerminalQueuedMsg (id, model.Peer.PeerId)))}>Approve</button>"""
                elif Option.isSome entry.ApprovedBy then
                    html $"""
                        <button type="button" class="{Style.btn}" data-terminal-unapprove="{QueueId.value id}"
                                @click={Ev(fun _ -> dispatch (UnapproveTerminalQueuedMsg id))}>Hold</button>"""
                else Lit.nothing
            // Reject sits beside approve wherever a verdict is possible, and it is offered
            // on every entry rather than only awaiting ones: under AutoRun nothing is ever
            // "awaiting", and that is exactly where being able to say no matters most.
            // Deleting is still there and still means withdrawal — this means refusal, and
            // the log records the difference.
            let reject =
                html $"""
                    <button type="button" class="{Style.btn}" data-terminal-reject="{QueueId.value id}"
                            @click={Ev(fun _ -> dispatch (RejectTerminalQueuedMsg (id, model.Peer.PeerId, None)))}>Reject</button>"""
            html $"""
                <article class="{if awaiting then Style.terminalQueuedAwaiting else Style.terminalQueuedReady}"
                         data-terminal-queued="{QueueId.value id}" data-terminal-queued-status="{statusToken}">
                  <div class="{Style.terminalQueuedRow}">
                    <span class="{Style.terminalPrompt}">$</span>
                    <input type="text" class="{Style.fieldMonoBare}" aria-label="Queued command"
                           data-terminal-input="{BodyKey.terminalQueued id}">
                  </div>
                  <div class="{Style.terminalQueuedRow}">
                    <span class="{if awaiting then Style.statusRun else Style.statusOk}">{if statusToken = Dom.Text.queuedAwaitingApproval then "waiting for approval" elif statusToken = Dom.Text.queuedAwaitingIntegration then "waiting for the terminal to be re-armed" elif statusToken = Dom.Text.queuedAwaitingTerminal then "waiting for the terminal" else "queued"}</span>
                    <span class="{Style.small}">{authorLabel entry.Author}</span>
                    <div class="ml-auto flex items-center gap-2">
                      {reject}
                      {approval}
                      <button type="button" class="{Style.btnIcon}" aria-label="Move up" @click={Ev(fun _ -> match TerminalQueueOrder.moveUp model.Synced.TerminalQueue id with Some o -> dispatch (ReorderTerminalQueuedMsg (id, o)) | None -> ())}>{Icon.up}</button>
                      <button type="button" class="{Style.btnIcon}" aria-label="Move down" @click={Ev(fun _ -> match TerminalQueueOrder.moveDown model.Synced.TerminalQueue id with Some o -> dispatch (ReorderTerminalQueuedMsg (id, o)) | None -> ())}>{Icon.down}</button>
                      <button type="button" class="{Style.btnIconDanger}" aria-label="Delete" data-terminal-queue-delete="{QueueId.value id}" @click={Ev(fun _ -> dispatch (DeleteTerminalQueuedMsg id))}>{Icon.close}</button>
                    </div>
                  </div>
                </article>""")

    /// The lease bar (Plan 13, stage 2e): who is typing here, and the one control that
    /// changes it. Shown in place of the command lines — never in place of the queue, which
    /// keeps working while a peer is live and is precisely what the release will run.
    let private terminalLeaseBar (actions: ViewActions) (model: ClientModel) (terminal: TerminalId) (holder: ActorRef) : TemplateResult =
        let mine = ActorRef.PeerRef model.Peer.PeerId
        let label = authorLabel holder
        let who = if holder = mine then "You are typing here" else sprintf "%s is using this terminal" label
        let control =
            if holder = mine then
                html $"""
                    <button type="button" class="{Style.btnPrimary}" data-terminal-release="{TerminalId.value terminal}"
                            @click={Ev(fun _ -> actions.ReleaseTerminal terminal)}>Hand it back</button>"""
            else
                // Any peer may take it, and no permission is asked for: collaborators are
                // trusted, so a steal needs to be VISIBLE rather than authorised — which the
                // event log is, and this button says so plainly.
                html $"""
                    <button type="button" class="{Style.btn}" data-terminal-take="{TerminalId.value terminal}"
                            @click={Ev(fun _ -> actions.TakeTerminal terminal)}>Take over</button>"""
        html $"""
            <div class="{Style.terminalQueuedRow}" data-terminal-lease="{label}" aria-live="polite">
              <span class="{Style.statusRun}">live</span>
              <span class="{Style.small}">{who}</span>
              <div class="ml-auto flex items-center gap-2">{control}</div>
            </div>"""

    /// The terminal composer: your command line, and everyone else's as they type them.
    let private terminalComposer (actions: ViewActions) (dispatch: ClientMsg -> unit) (model: ClientModel) (terminal: TerminalId) : TemplateResult =
        let mine = model.Peer.PeerId
        let mode = SyncedSessionState.modeOf terminal model.Synced
        let lease =
            TerminalProjection.tryFind terminal model.Terminals |> Option.bind (fun view -> view.Lease)
        let integrationLost =
            TerminalProjection.tryFind terminal model.Terminals
            |> Option.map (fun view -> view.IntegrationLost)
            |> Option.defaultValue false
        let editors (author: PeerId) =
            ClientModel.terminalEditorsOf terminal author model
            |> List.map (fun (editor, name) ->
                html $"""
                    <span class="{Style.draftEditorDot}" style="background:{PeerColour.ofPeer editor}"
                          title="{name}" data-terminal-draft-editor="{PeerId.value editor}"></span>""")
        // Someone else mid-command: their live text, read-only here. Watching a collaborator
        // type a command is the same affordance as watching them type a message, which is
        // the whole reason the terminal composer is built out of the message composer's parts.
        let peerDraft (author: PeerId) =
            html $"""
                <div class="{Style.terminalPeerDraft}" style="border-left-color:{PeerColour.ofPeer author}"
                     data-terminal-draft-author="{PeerId.value author}">
                  <span class="{Style.terminalPrompt}">$</span>
                  <input type="text" class="{Style.fieldMonoBare}" readonly aria-label="{ClientModel.nameOf author model}'s command"
                         data-terminal-input="{BodyKey.terminalDraft terminal author}">
                  <span class="{Style.terminalEditors}">{editors author}</span>
                  <button type="button" class="{Style.btn}" data-terminal-send="{PeerId.value author}"
                          @click={Ev(fun _ -> actions.SendTerminalDraft terminal author)}>Run</button>
                </div>"""
        let others = ClientModel.terminalDrafts terminal model |> List.filter (fun author -> author <> mine)
        let takeControl =
            if Option.isSome lease then Lit.nothing
            else
                html $"""
                    <button type="button" class="{Style.cls [ Style.btn; "ml-auto" ]}" data-terminal-take="{TerminalId.value terminal}"
                            @click={Ev(fun _ -> actions.TakeTerminal terminal)}>Take terminal</button>"""
        // In live mode the command lines give way to the lease bar. Drafting into a box marked
        // "Run" that cannot run anything is the misleading half; the QUEUE above stays, because
        // queueing during a live session is meaningful — the entry runs the moment the terminal
        // comes back.
        let commandLines =
            match lease with
            | Some holder -> terminalLeaseBar actions model terminal holder
            | None ->
                html $"""
                    <div>
                      {others |> List.map peerDraft}
                      <div class="{Style.terminalQueuedRow}">
                        <span class="{Style.terminalPrompt}">$</span>
                        <input type="text" class="{Style.fieldMono}" aria-label="Command"
                               placeholder="a command to run here"
                               data-terminal-input="{BodyKey.terminalDraft terminal mine}">
                        <span class="{Style.terminalEditors}">{editors mine}</span>
                        <button type="button" class="{Style.btnPrimary}" aria-keyshortcuts="Enter"
                                data-terminal-send="{PeerId.value mine}"
                                @click={Ev(fun _ -> actions.SendTerminalDraft terminal mine)}>Run</button>
                      </div>
                    </div>"""
        // Named, not shown as a stall. The queue is held because a command written here could
        // not be bounded — we would not know when it started or finished — and saying that is
        // the difference between a terminal that looks broken and one that says what to do.
        let lostBanner =
            if not integrationLost then Lit.nothing
            else
                html $"""
                    <div class="{Style.terminalQueuedRow}" data-terminal-lost="{TerminalId.value terminal}" aria-live="polite">
                      <span class="{Style.statusErr}">not marking</span>
                      <span class="{Style.small}">This terminal's shell stopped reporting when commands start and finish, so queued commands are held.</span>
                      <div class="ml-auto flex items-center gap-2">
                        <button type="button" class="{Style.btnPrimary}" data-terminal-rearm="{TerminalId.value terminal}"
                                @click={Ev(fun _ -> actions.RearmTerminal terminal)}>Re-arm it</button>
                      </div>
                    </div>"""
        html $"""
            <section class="{Style.terminalComposer}">
              {lostBanner}
              <div class="{Style.sideRow}">
                <label class="{Style.label}" for="terminal-mode">approval</label>
                <select id="terminal-mode" class="{Style.field} w-auto" data-terminal-mode="{TerminalApprovalMode.describe mode}"
                        @change={EvVal(fun v -> match TerminalApprovalMode.parse v with Some m -> dispatch (SetTerminalModeMsg (terminal, m)) | None -> ())}>
                  <option value="approve-agent" ?selected={mode = ApproveAgent}>the agent's commands</option>
                  <option value="approve-all" ?selected={mode = ApproveAll}>every command</option>
                  <option value="auto" ?selected={mode = AutoRun}>nothing — run them</option>
                </select>
                {takeControl}
              </div>
              {terminalQueue dispatch model terminal}
              {commandLines}
            </section>"""

    /// A CLOSED terminal's recording (Plan 13, stage 3e) — the audit read.
    ///
    /// Its blocks still render above; this is the OTHER read. A list of commands says what
    /// ran; the recording shows the terminal as it behaved, at the speed it behaved, which is
    /// what someone auditing a session actually wants to watch. The player is attached by the
    /// browser shell to the mount below; the `.cast` it replays is rebuilt from the records
    /// this client already fetched (`TranscriptReplay.cast`), so the replay rides the same
    /// immutable chunk cache the rest of the history does.
    let private terminalReplay (model: ClientModel) (view: TerminalView) : TemplateResult =
        let feed = ClientModel.terminalFeed view.TerminalId model
        // Retention (stage 3d) deletes a closed terminal's transcript whole once it is old
        // enough, and its chunks then 404. Saying so is the point: an empty player would be
        // indistinguishable from a terminal that printed nothing, and the whole reason the
        // drop is recorded is that a gap in an audit trail must be a stated fact.
        let gone = Map.isEmpty feed.Records && view.DroppedBytes > 0
        let closedFor =
            match view.ClosedReason with
            | Some reason -> sprintf "closed — %s" reason
            | None -> "closed"
        if gone then
            html $"""
                <section class="{Style.terminalComposer}" data-terminal-replay-gone="{TerminalId.value view.TerminalId}">
                  <div class="{Style.terminalQueuedRow}">
                    <span class="{Style.statusFaint}">{closedFor}</span>
                    <span class="{Style.small}">This terminal's recording has passed its retention window and was deleted. The commands it ran are above; what they printed is no longer kept.</span>
                  </div>
                </section>"""
        else
            html $"""
                <section class="{Style.terminalComposer}">
                  <div class="{Style.terminalQueuedRow}">
                    <span class="{Style.statusFaint}">{closedFor}</span>
                    <span class="{Style.small}">A recording of everything this terminal printed.</span>
                  </div>
                  <div class="{Style.terminalBlocks}" role="region" aria-label="Terminal recording"
                       data-terminal-replay="{TerminalId.value view.TerminalId}"></div>
                </section>"""

    /// The terminals column: the conversation's mirror on the right.
    let private terminals (actions: ViewActions) (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        let openTerminals = TerminalProjection.openTerminals model.Terminals
        // Closed terminals are reachable too (Plan 13, stage 3e). Without this the audit
        // outlives the process in the DATA and not on the screen: a closed terminal's blocks
        // are in the projection and nothing renders them.
        let closedTerminals = model.Terminals.Terminals |> List.filter (fun t -> not t.IsOpen)
        let selected = ClientModel.selectedTerminal model
        let tab (view: TerminalView) =
            let isSelected = selected = Some view.TerminalId
            // Who is in THIS terminal, on its tab — the same presence the roster reports, put
            // where you would look for it. Without it, a collaborator typing a command in a
            // terminal you are not showing is visible nowhere in this column.
            let peers =
                ClientModel.peersInTerminal view.TerminalId model
                |> List.map (fun (peer, name) ->
                    html $"""
                        <span class="{Style.draftEditorDot}" style="background:{PeerColour.ofPeer peer}"
                              title="{name}" data-terminal-tab-peer="{PeerId.value peer}"></span>""")
            html $"""
                <button type="button" class="{if isSelected then Style.terminalTabActive else Style.terminalTab}"
                        data-terminal-tab="{TerminalId.value view.TerminalId}" aria-pressed="{if isSelected then "true" else "false"}"
                        @click={Ev(fun _ -> dispatch (SelectTerminalMsg view.TerminalId))}>{view.Title}<span class="{Style.terminalTabPeers}">{peers}</span></button>"""
        // Rendered after the open ones and marked apart, because they behave differently:
        // there is nothing to run in a closed terminal, only something to read.
        let closedTabs =
            closedTerminals
            |> List.map (fun view ->
                let isSelected = selected = Some view.TerminalId
                html $"""
                    <button type="button" class="{if isSelected then Style.terminalTabActive else Style.terminalTab}"
                            data-terminal-closed-tab="{TerminalId.value view.TerminalId}"
                            aria-pressed="{if isSelected then "true" else "false"}"
                            @click={Ev(fun _ -> dispatch (SelectTerminalMsg view.TerminalId))}>{view.Title}<span class="{Style.small}"> · closed</span></button>""")
        let body =
            match selected |> Option.bind (fun id -> TerminalProjection.tryFind id model.Terminals) with
            | None ->
                html $"""
                    <div class="{Style.terminalEmpty}">
                      <span class="{Style.small}">Nothing is open. A terminal runs commands in this session's workspace — everything it prints is recorded.</span>
                      <button type="button" class="{Style.btnPrimary}" data-terminal-new
                              @click={Ev(fun _ -> actions.OpenTerminal "terminal")}>New terminal</button>
                    </div>"""
            | Some view ->
                let feed = ClientModel.terminalFeed view.TerminalId model
                let truncated =
                    if view.DroppedBytes > 0 then
                        html $"""<div class="{Style.terminalTruncated}" data-terminal-truncated="{string view.DroppedBytes}">{view.DroppedBytes} bytes of output were not recorded</div>"""
                    else Lit.nothing
                let blocks =
                    if List.isEmpty view.Blocks then
                        [ html $"""<div class="{Style.terminalOutputEmpty}">Nothing has run here yet.</div>""" ]
                    else view.Blocks |> List.map (terminalBlockView feed)
                html $"""
                    <div class="{Style.terminalBlocks}" data-terminal-id="{TerminalId.value view.TerminalId}">
                      {truncated}
                      {blocks}
                    </div>
                    {if view.IsOpen then terminalComposer actions dispatch model view.TerminalId
                     else terminalReplay model view}"""
        // Offered only for a terminal that is actually open: a "close" on a closed one either
        // does nothing or reports an error, and both are worse than not being there.
        let closeSelected =
            match selected |> Option.bind (fun id -> TerminalProjection.tryFind id model.Terminals) with
            | Some view when view.IsOpen ->
                html $"""
                    <button type="button" class="{Style.cls [ Style.terminalTab; "ml-auto" ]}" data-terminal-close="{TerminalId.value view.TerminalId}"
                            aria-label="Close terminal" @click={Ev(fun _ -> actions.CloseTerminal view.TerminalId)}>close</button>"""
            | _ -> Lit.nothing
        html $"""
            <aside class="{Style.terminalPanel}" data-terminal-panel>
              <div class="{Style.terminalPane}">
                <div class="{Style.terminalHead}">
                  <span class="{Style.settingsTitle}">terminals</span>
                  <button type="button" class="{Style.navChevronForward}" aria-label="Hide terminals"
                          data-terminal-toggle="hide" @click={Ev(fun _ -> dispatch ToggleTerminalsMsg)}>{Icon.right}</button>
                </div>
                <div class="{Style.terminalTabs}">
                  {openTerminals |> List.map tab}
                  {closedTabs}
                  <button type="button" class="{Style.terminalTabNew}" data-terminal-new
                          @click={Ev(fun _ -> actions.OpenTerminal "terminal")}>+ new</button>
                  {closeSelected}
                </div>
                {body}
              </div>
            </aside>"""

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
            </div>
            {terminals actions dispatch model}"""
