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
      /// GitHub connection panel (Plan 14). Same imperative shape as the Claude set;
      /// the flow differs (device code, no paste-back) so there is no Complete — the
      /// browser polls while awaiting approval.
      /// Begin the device-flow sign-in for the scope in the panel's selector.
      GitHubConnect : unit -> unit
      /// Store the pasted personal-access/user token from the panel's token input.
      GitHubPasteToken : unit -> unit
      /// Disconnect the credential stored for a scope choice ("session" | "mine").
      GitHubDisconnect : string -> unit
      // The Repos panel's three actions (Plan 14) were RETIRED by Plan 15: adding,
      // removing and switching a repo are commands, and commands belong to the agent, so
      // a human asks and reads the act-line in the timeline. What is left of that panel is
      // the `repos` QUERY, which needs no action at all.
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
      RearmTerminal : TerminalId -> unit
      /// Send keystrokes to a terminal this peer holds (Plan 14, stage 6). Imperative
      /// because it is a frame, and deliberately not acknowledged: a keystroke that needed a
      /// reply would make typing a round trip. The Session Process checks the lease, which
      /// is the only place it CAN be checked — a client that believes it holds one may be
      /// looking at a steal it has not seen yet.
      TypeIntoTerminal : TerminalId -> string -> unit
      /// Report the holder's viewport size, so the pty and the program inside it agree about
      /// the screen (Plan 14, stage 6).
      ResizeTerminal : TerminalId -> int -> int -> unit
      /// Move focus into the side pane after a chip opened a tab there (Plan 14, stage 2).
      /// Imperative because it is a focus move: the model says which tab is showing, and the
      /// browser has to wait for the render that put it on screen. A chip that opened a pane
      /// and left focus behind it is the failure the WCAG floor names.
      FocusPane : unit -> unit
      /// Return focus to the chat item that opened a tab, once that tab is closed. Takes the
      /// tab's key, which is the only thing the chip and the tab share — the browser turns it
      /// back into a selector. Without this, closing a tab strands focus on a control that
      /// has just been removed from the document.
      FocusChat : string -> unit
      /// Hand focus to whichever DVR control replaced the one just pressed (Plan 14,
      /// stage 7): Rewind and Jump-to-live each remove the other from the document, so the
      /// press that swaps them would otherwise strand focus on a control that has gone.
      FocusDvr : TerminalId -> unit
      /// Hand focus on after a verdict (Plan 15, stage 3c). Approving or refusing REMOVES
      /// the card the button was on, which is precisely the stranded-focus case the WCAG
      /// floor names — and it is worse here than for the DVR's pair, because a reviewer
      /// working down a list of proposals loses their place on every decision.
      FocusAfterVerdict : unit -> unit }

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
          GitHubConnect = ignore
          GitHubPasteToken = ignore
          GitHubDisconnect = ignore
          ReopenSession = ignore
          OpenTerminal = ignore
          CloseTerminal = ignore
          SendTerminalDraft = fun _ _ -> ()
          TakeTerminal = ignore
          ReleaseTerminal = ignore
          RearmTerminal = ignore
          TypeIntoTerminal = fun _ _ -> ()
          ResizeTerminal = fun _ _ _ -> ()
          FocusPane = ignore
          FocusChat = ignore
          FocusDvr = ignore
          FocusAfterVerdict = ignore }

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

    /// An actor as a TOKEN: stable, model-free, and what every `data-*` hook carries — which is
    /// why it stays a total function of the actor alone and why the tests can assert it.
    let private authorLabel =
        function
        | UserRef u -> UserId.value u
        | PeerRef p -> PeerId.value p
        | ActorRef.Agent -> Dom.Text.agent
        | ActorRef.SessionProcess -> Dom.Text.sessionProcess
        | ActorRef.System -> Dom.Text.system

    /// The same actor, said to a person.
    ///
    /// A peer id is a fine token and a poor name — `PEER-129755065` is nobody — and the roster,
    /// the draft summaries and the lease bar all resolve one through `nameOf` already. The chat
    /// did not, so one human appeared under two identities on the one screen. Everything else is
    /// already a word (`agent`, `system`, a user's own subject), so only a peer resolves; a peer
    /// this client has never seen still falls back to the id, because a blank author would be
    /// worse than an ugly one.
    let private authorName (model: ClientModel) (actor: ActorRef) : string =
        match actor with
        | PeerRef peer -> ClientModel.nameOf peer model
        | UserRef _ | ActorRef.Agent | ActorRef.SessionProcess | ActorRef.System -> authorLabel actor

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
            // The dimmed avatar and the status word carry the state; the button carries the
            // fix. What a message does meanwhile (recorded, unanswered — `Scheduler.create`,
            // a `None` runner at drain time) is behaviour the queue itself shows, not a
            // sentence to hang here.
            | Some false ->
                html $"""
                    <div class="{Style.noAgentBlock}" data-agent-presence="absent" data-no-agent>
                      <div class="{Style.person}"><span class="{Style.cls [ Style.avatar; Style.agentAvatar; Style.personAvatar ]} opacity-40"></span><span class="text-ink-faint">agent</span><span class="{Style.statusRun} ml-auto">no agent</span></div>
                      <div class="{Style.noAgentPrompt}">
                        <span class="{Style.noAgentEdge}"></span>
                        <div class="{Style.noAgentBody}">
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
                    <label class="{Style.label}" for="claude-code">code from claude.ai</label>
                    <input id="claude-code" type="text" class="{Style.field}" data-claude-code placeholder="code#state" />
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
                    <label class="{Style.label} pt-2" for="claude-token">setup token / api key</label>
                    <input id="claude-token" type="password" class="{Style.field}" data-claude-token placeholder="sk-ant-…" />
                    <button type="button" class="{Style.btn}" data-claude-save-token @click={Ev(fun _ -> actions.ClaudePasteToken ())}>Save token</button>"""
        let error =
            match claude.Flow with
            | ClaudeError reason -> html $"""<span class="{Style.statusErr}" data-claude-error>{reason}</span>"""
            | _ -> html $""""""
        html $"""
            <section class="{Style.cls [ Style.sideSection; Style.settingsLane1 ]}" data-claude-panel>
              <span class="{Style.label}">claude</span>
              {connectedRow "all my sessions" "mine" claude.Status.MineCredential}
              {connectedRow "this session" "session" claude.Status.SessionCredential}
              {error}
              {controls}
            </section>"""

    /// The GitHub connection panel (Plan 14), beside the Claude one: status per sign-in
    /// scope, the device flow (show the code → approve on github.com → the poll lands
    /// the grant), and the paste-a-token fallback.
    let private githubSection (actions: ViewActions) (dispatch: ClientMsg -> unit) (github: GitHubViewState) : TemplateResult =
        let connectedRow (label: string) (scopeChoice: string) (kind: string option) =
            match kind with
            | Some kind ->
                html $"""<div class="{Style.sideRow}" data-github-connected="{scopeChoice}"><span class="{Style.statusOk}"><span class="{Style.statusDot}"></span>{label} ({kind})</span><button type="button" class="{Style.btnIconDanger}" aria-label="Disconnect GitHub" data-github-disconnect="{scopeChoice}" @click={Ev(fun _ -> actions.GitHubDisconnect scopeChoice)}>{Icon.close}</button></div>"""
            | None -> html $""""""
        let controls =
            match github.Flow with
            | GitHubBusy ->
                html $"""<span class="{Style.statusRun}" data-github-busy><span class="{Style.statusDotPulse}"></span>working…</span>"""
            | GitHubAwaitingApproval (userCode, verificationUri, _, _) ->
                html $"""
                    <span class="{Style.label}">code for github.com</span>
                    <span class="{Style.field}" data-github-user-code aria-label="GitHub device code">{userCode}</span>
                    <div class="flex gap-2">
                      <a class="{Style.btnPrimary}" href="{verificationUri}" target="_blank" rel="noreferrer" data-github-authorize>Approve on github.com</a>
                      <button type="button" class="{Style.btn}" data-github-cancel @click={Ev(fun _ -> dispatch (GitHubFlowMsg GitHubIdle))}>Cancel</button>
                    </div>"""
            | GitHubIdle | GitHubError _ ->
                html $"""
                    <label class="{Style.label}" for="github-scope">sign in for</label>
                    <select id="github-scope" class="{Style.field}" data-github-scope aria-label="GitHub sign-in scope">
                      <option value="mine">All my sessions</option>
                      <option value="session">This session only</option>
                    </select>
                    <button type="button" class="{Style.btnPrimary}" data-github-connect @click={Ev(fun _ -> actions.GitHubConnect ())}>Connect GitHub</button>
                    <label class="{Style.label} pt-2" for="github-token">personal access token</label>
                    <input id="github-token" type="password" class="{Style.field}" data-github-token placeholder="github_pat_…" />
                    <button type="button" class="{Style.btn}" data-github-save-token @click={Ev(fun _ -> actions.GitHubPasteToken ())}>Save token</button>"""
        let error =
            match github.Flow with
            | GitHubError reason -> html $"""<span class="{Style.statusErr}" data-github-error>{reason}</span>"""
            | _ -> html $""""""
        html $"""
            <section class="{Style.cls [ Style.sideSection; Style.settingsLane1 ]}" data-github-panel>
              <span class="{Style.label}">github</span>
              {connectedRow "all my sessions" "mine" github.Status.MineCredential}
              {connectedRow "this session" "session" github.Status.SessionCredential}
              {error}
              {controls}
            </section>"""

    /// The generated read surface (Plan 15): ONE renderer for every query this session
    /// declares, now and later. Registering a query is what puts it on this screen —
    /// nobody writes a panel, which is the whole reason the surface is generated rather
    /// than hand-built.
    ///
    /// It is deliberately read-only. The commands that change any of this belong to the
    /// agent: a human asks, and the act lands in the timeline attributed. So there are no
    /// buttons here, no inputs, and nothing that can be in flight — which is also what
    /// makes the accessibility floor cheap to hold, because it is held once, here, for
    /// every query that will ever exist.
    let private queryValueView (shape: QueryShape) (value: QueryValue option) : TemplateResult =
        let cellText (row: (string * QueryCell) list) (column: QueryColumn) =
            row
            |> List.tryFind (fun (key, _) -> key = column.Key)
            |> Option.map snd
            |> Option.defaultValue CellAbsent
            |> QueryCell.describe
        match shape, value with
        | _, None -> html $"""<span class="{Style.small}" data-query-pending>…</span>"""
        | Value, Some (ValueOf cellValue) ->
            html $"""<span class="{Style.small}" data-query-value>{QueryCell.describe cellValue}</span>"""
        | Fields columns, Some (FieldsOf fields) ->
            let rows =
                columns
                |> List.map (fun column ->
                    html $"""
                        <div class="{Style.sideRow}" data-query-field="{column.Key}">
                          <span class="{Style.statusFaint}">{column.Label}</span>
                          <span class="{Style.small}">{cellText fields column}</span>
                        </div>""")
            html $"""<div class="flex flex-col gap-1">{rows}</div>"""
        | Rows _, Some (RowsOf []) ->
            html $"""<span class="{Style.small}" data-query-empty>(none)</span>"""
        | Rows columns, Some (RowsOf rows) ->
            // A real `<table>` with `<th scope="col">`, because this IS tabular data and a
            // grid of divs tells a screen reader nothing about which heading a value sits
            // under (CLAUDE.md, UI baseline: structure).
            let head =
                columns
                |> List.map (fun column ->
                    html $"""<th scope="col" class="{Style.queryHeadCell}">{column.Label}</th>""")
            let body =
                rows
                |> List.map (fun row ->
                    let cells =
                        columns
                        |> List.map (fun column ->
                            html $"""<td class="{Style.queryCell}" data-query-cell="{column.Key}">{cellText row column}</td>""")
                    html $"""<tr>{cells}</tr>""")
            html $"""
                <div class="{Style.queryTable}">
                  <table class="w-full">
                    <thead><tr>{head}</tr></thead>
                    <tbody>{body}</tbody>
                  </table>
                </div>"""
        // A value that does not match its declared shape never reaches here — the registry
        // refuses it Process-side — so this arm exists only to keep the match total.
        | _, Some _ -> html $"""<span class="{Style.small}" data-query-pending>…</span>"""

    let private queriesSection (queries: QueriesViewState) : TemplateResult list =
        queries.Declared
        |> List.map (fun def ->
            let name = QueryName.value def.Name
            html $"""
                <section class="{Style.cls [ Style.sideSection; Style.settingsLane1 ]}" data-query-panel="{name}">
                  <span class="{Style.label}">{def.Title}</span>
                  {queryValueView def.Shape (Map.tryFind name queries.Values)}
                </section>""")

    /// The command gates a session has: the same three-option control the terminal's header
    /// carries, over `ForCommand` subjects instead of `ForTerminal` ones. One register, one
    /// vocabulary, one control.
    ///
    /// It lists EVERY gated command, not only the ones somebody has configured — the
    /// catalogue is a value in the shared domain, so the browser has it at compile time and
    /// a command nobody has touched renders on its default rather than being invisible until
    /// an operator names it in an environment variable.
    let private gatesSection (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult list =
        GatedCommands.all
        |> List.map (fun command ->
            let subject = GatedCommands.subject command
            let mode = SyncedSessionState.gateOf subject model.Synced
            html $"""
                <section class="{Style.cls [ Style.sideSection; Style.settingsLane1 ]}" data-gate-panel="{command.Tool}">
                  <label class="{Style.label}" for="gate-{command.Tool}">{command.Title}</label>
                  <select id="gate-{command.Tool}" class="{Style.field} w-auto" data-gate-mode="{ApprovalMode.describe mode}"
                          @change={EvVal(fun v -> match ApprovalMode.parse v with Some m -> dispatch (SetGateMsg (subject, m)) | None -> ())}>
                    <option value="auto" ?selected={mode = AutoRun}>happens without asking</option>
                    <option value="approve-agent" ?selected={mode = ApproveAgent}>the agent asks first</option>
                    <option value="approve-all" ?selected={mode = ApproveAll}>always ask, whoever proposed it</option>
                  </select>
                </section>""")

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
              {githubSection actions dispatch model.GitHub}
              {queriesSection model.Queries}
              {gatesSection dispatch model}
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

    /// The bytes a keydown means to a pty (Plan 14, stage 6).
    ///
    /// A keyboard event is not a byte stream, and the translation is the whole of what a
    /// terminal front end does with keys: printable characters go as themselves, Ctrl-<key>
    /// as the control code, and the keys with no character at all (arrows, Home, the
    /// function block) as the escape sequences a program is waiting for. `null` means a key
    /// that sends nothing — a bare modifier, or a shortcut the browser owns.
    ///
    /// `preventDefault` on everything that IS sent, because otherwise the browser also acts
    /// on it: Tab would leave the terminal mid-session, and Backspace used to navigate.
    [<Fable.Core.Emit("""(() => {
  const ev = $0
  if (ev.metaKey || ev.altKey) return null
  const k = ev.key
  const send = d => { ev.preventDefault(); return d }
  if (ev.ctrlKey) {
    if (k.length === 1) {
      const c = k.toUpperCase().charCodeAt(0)
      if (c >= 64 && c <= 95) return send(String.fromCharCode(c - 64))
    }
    return null
  }
  switch (k) {
    case 'Enter': return send('\r')
    case 'Backspace': return send('\x7f')
    case 'Tab': return send('\t')
    case 'Escape': return send('\x1b')
    case 'ArrowUp': return send('\x1b[A')
    case 'ArrowDown': return send('\x1b[B')
    case 'ArrowRight': return send('\x1b[C')
    case 'ArrowLeft': return send('\x1b[D')
    case 'Home': return send('\x1b[H')
    case 'End': return send('\x1b[F')
    case 'PageUp': return send('\x1b[5~')
    case 'PageDown': return send('\x1b[6~')
    case 'Delete': return send('\x1b[3~')
  }
  return k.length === 1 ? send(k) : null
})()""")>]
    let private keystrokeOf (e: obj) : string option = Fable.Core.Util.jsNative

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
                html $"""<div class="{Style.queueHead}"><span class="{Style.queueCount}">queued · {List.length entries}</span></div>"""
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
            // Whether there is anything here to act ON. The draft slot is that fact
            // (`ClientModel.draftHasContent` — `DraftSlot` publishes one exactly while the body
            // has content), so the controls and the send path read the same truth rather than
            // two measurements that can disagree.
            let hasContent = ClientModel.draftHasContent target model
            // Discard exists only once there is something to discard. An empty composer used to
            // offer a destructive control over nothing — and offering a verdict on nothing is
            // how a working button and a dead one come to look identical.
            let discard =
                if target = myPeer && hasContent then
                    html $"""
                        <button type="button" class="{Style.btnIconDangerLg}" aria-label="Discard draft"
                                data-discard-draft @click={Ev(fun _ -> actions.DiscardDraft myPeer)}>{Icon.close}</button>"""
                else Lit.nothing
            // Send STAYS — same place in the layout, same place in focus order, so nothing
            // moves under the hand and no Tab stop appears mid-sentence — and waits at a
            // dimmed weight until there is something to send, coming to full strength with the
            // first character.
            //
            // NOT marked disabled, in either spelling. Send is always pressable; on an empty
            // draft it simply has nothing to do (already a no-op in the model), and announcing
            // "unavailable" would claim more than that — a person with an empty composer is not
            // blocked, they just have not typed yet. The weight is the signal; the control
            // stays whole. `Resilience.fs` pins the same promise from the other direction.
            let sendClass =
                if hasContent then Style.cls [ Style.btnPrimary; "gap-2" ]
                else Style.cls [ Style.btnPrimary; "gap-2"; Style.btnWaiting ]
            let author =
                if target = myPeer then Lit.nothing
                else html $"""<span class="{Style.draftAuthor}">{ClientModel.nameOf target model}'s message</span>"""
            html $"""
                <article class="{Style.draftBox}" data-draft-id="{PeerId.value target}" data-draft-author="{PeerId.value target}">
                  <span class="{Style.draftRail}"></span>
                  <span class="{Style.draftEdge}"></span>
                  <div class="{Style.draftInput}" data-rich-body="{BodyKey.draft target}" data-rich-readonly="false" data-draft-input="{PeerId.value target}"></div>
                  <div class="{Style.draftActions}">
                    <span class="{Style.draftEditors}">{editors target}</span>
                    {author}
                    <div class="{Style.draftCommit}">
                      {discard}
                      <button type="button" class="{sendClass}" aria-keyshortcuts="Enter"
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

    let private terminalBlockStatus (model: ClientModel) =
        function
        | BlockRunning -> html $"""<span class="{Style.statusRun}"><span class="{Style.statusDotPulse}"></span>running</span>"""
        | BlockFinished (CommandSucceeded code) -> html $"""<span class="{Style.statusOk}">{Icon.checkSm} {code}</span>"""
        | BlockFinished (CommandFailed code) -> html $"""<span class="{Style.statusErr}">{Icon.crossSm} {code}</span>"""
        | BlockFinished CommandTimedOut -> html $"""<span class="{Style.statusErr}">timed out</span>"""
        | BlockFinished (CommandExecutionFailed _) -> html $"""<span class="{Style.statusErr}">failed</span>"""
        // Named, not merely absent. "rejected by nick" in line with the commands that ran
        // is the whole reason a refusal mints a block at all — so it is a NAME, resolved like
        // every other person on screen, not the id the hook carries.
        | BlockRejected (by, _) -> html $"""<span class="{Style.statusErr}">rejected by {authorName model by}</span>"""

    let private stretchEndLabel =
        function
        | LeaseReleased -> Dom.Text.stretchReleased
        | LeaseStolen _ -> Dom.Text.stretchStolen
        | LeaseHolderGone -> Dom.Text.stretchGone
        | LeaseIdle -> Dom.Text.stretchIdle

    /// How a stretch ended, said the way a reader asks it.
    let private stretchEnding (model: ClientModel) =
        function
        | LeaseReleased -> html $"""<span class="{Style.statusFaint}">handed back</span>"""
        | LeaseStolen by -> html $"""<span class="{Style.statusFaint}">taken over by {authorName model by}</span>"""
        | LeaseHolderGone -> html $"""<span class="{Style.statusFaint}">holder left</span>"""
        | LeaseIdle -> html $"""<span class="{Style.statusFaint}">went idle</span>"""

    /// A stretch's length, in the coarsest unit that still says something. Sub-second is not
    /// a session someone had; it is a lease that bounced.
    let private durationText (span: System.TimeSpan) : string =
        let seconds = int (round span.TotalSeconds)
        if seconds >= 3600 then sprintf "%dh %dm" (seconds / 3600) ((seconds % 3600) / 60)
        elif seconds >= 60 then sprintf "%dm %ds" (seconds / 60) (seconds % 60)
        else sprintf "%ds" seconds

    /// The chat: what was said and what was run, in the order it happened (Plan 14, stage 1).
    ///
    /// Terminal items are resolved against `TerminalProjection` at render time rather than
    /// copied into the timeline, which is what makes a running chip mutate in place as its
    /// block finishes — the timeline holds where it goes, the projection holds what it says.
    let private chat (actions: ViewActions) (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        // A repo note is something someone DID, not said — one quiet line, actor-attributed,
        // no avatar and no rich body (Plan 14, repos). It rides the same timeline slot a
        // message does (both are `ConversationItem`s at an offset); `Kind` is what tells the
        // two apart at render time.
        let actNoteItem (item: ConversationItem) =
            html $"""
                <article class="{Style.actNote}" data-message-id="{MessageId.value item.MessageId}" data-act-note data-message-author="{authorLabel item.Author}">
                  <span class="{Style.actNoteText}">{authorName model item.Author} {item.Body}</span>
                </article>"""
        let messageItem (item: ConversationItem) =
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
                  <div class="{Style.messageMeta}"><span class="{whoClass}">{authorName model item.Author}</span>{statusInner}</div>
                  <div class="{bodyClass}" data-message-body>{RichText.render item.Body}{caret}</div>
                </article>"""
        let message (item: ConversationItem) =
            match item.Kind with
            | ConversationItemKind.ActNote -> actNoteItem item
            | ConversationItemKind.Message -> messageItem item
        // One line: who ran what, and how it went. No output — a tail inline would make the
        // chat noisiest exactly when it is busiest, and would put everything a command
        // printed one glance from anyone in the session rather than one tap.
        let blockChip (terminalId: TerminalId) (blockId: BlockId) =
            let found =
                TerminalProjection.tryFind terminalId model.Terminals
                |> Option.bind (fun view -> view.Blocks |> List.tryFind (fun b -> b.BlockId = blockId))
            match found with
            // Both folds read the same page, so a chip without its block is a page boundary,
            // not a bug: the next page brings it. Rendering nothing beats rendering a stub.
            | None -> Lit.nothing
            | Some block ->
                html $"""
                    <button type="button" class="{Style.chatChip}"
                            data-chat-block="{BlockId.value blockId}"
                            data-chat-block-status="{terminalBlockStatusLabel block.Status}"
                            data-terminal-id="{TerminalId.value terminalId}"
                            @click={Ev(fun _ -> dispatch (OpenPaneTabMsg (BlockTab (terminalId, blockId))); actions.FocusPane ())}>
                      <span class="{Style.chatChipWho}">{authorName model block.Author}</span>
                      <span class="{Style.terminalPrompt}">$</span>
                      <code class="{Style.chatChipCommand}">{block.Command}</code>
                      <span class="shrink-0">{terminalBlockStatus model block.Status}</span>
                    </button>"""
        let stretchItem (stretch: TerminalStretch) =
            let length = durationText (TerminalStretch.duration stretch)
            html $"""
                <button type="button" class="{Style.chatChip}"
                        data-chat-stretch="{TerminalStretch.key stretch}"
                        data-chat-stretch-end="{stretchEndLabel stretch.End}"
                        data-terminal-id="{TerminalId.value stretch.TerminalId}"
                        @click={Ev(fun _ -> dispatch (OpenPaneTabMsg (StretchTab stretch)); actions.FocusPane ())}>
                  <span class="{Style.chatChipWho}">{authorName model stretch.Holder}</span>
                  <span class="{Style.chatChipText}">typed in {stretch.Title} for {length}</span>
                  <span class="shrink-0">{stretchEnding model stretch.End}</span>
                </button>"""
        // One call the agent made. No pane tab: unlike a block there is nothing recorded to
        // open — what there is to know (where it went, with what, and how it went) fits on
        // the line. The minted id rides the row anyway, because that is what a deep link
        // will address once there is somewhere for it to land.
        let toolCall (use': ToolUse) =
            let status, rendered =
                match use'.Outcome with
                | None ->
                    Dom.Text.blockRunning,
                    html $"""<span class="{Style.statusRun}"><span class="{Style.statusDotPulse}"></span>running</span>"""
                | Some ToolCallOk -> Dom.Text.blockOk, html $"""<span class="{Style.statusOk}">{Icon.checkSm}</span>"""
                | Some (ToolCallFailed reason) -> Dom.Text.blockFailed, html $"""<span class="{Style.statusErr}">{reason}</span>"""
            // `None` is not "no arguments" — it is a foreign tool, whose schema we did not
            // write and therefore cannot trust to have marked its own secrets.
            let args =
                match use'.Arguments with
                | Some recorded -> recorded
                | None -> "(arguments not recorded)"
            html $"""
                <div class="{Style.chatToolCall}"
                     data-chat-tool="{ToolUseId.value use'.ToolUseId}"
                     data-chat-tool-status="{status}">
                  <code class="{Style.chatToolName}">{ToolUse.label use'}</code>
                  <code class="{Style.chatToolArgs}">{args}</code>
                  <span class="shrink-0">{rendered}</span>
                </div>"""
        let toolRun (turn: AgentTurnId) (uses: ToolUse list) =
            let summary =
                match uses with
                | [ one ] -> ToolUse.label one
                | many -> sprintf "%d tools" (List.length many)
            html $"""
                <details class="{Style.chatToolRun}" data-chat-tool-run="{AgentTurnId.value turn}">
                  <summary class="{Style.chatToolSummary}">
                    <span class="{Style.chatChipWho}">{Dom.Text.agent}</span>
                    <span class="{Style.chatChipText}">used {summary}</span>
                  </summary>
                  {uses |> List.map toolCall}
                </details>"""
        let rows = TimelineProjection.rows model.Conversation model.Timeline
        let items =
            rows
            |> List.map (function
                | RowItem (TimelineMessage item) -> message item
                | RowItem (TimelineBlock (_, terminalId, blockId)) -> blockChip terminalId blockId
                | RowItem (TimelineStretch stretch) -> stretchItem stretch
                // `rows` never puts a tool use in a bare row, and never a run of anything
                // else — but both are `TimelineItem`s, so the types cannot say so.
                | RowItem (TimelineToolUse _) -> Lit.nothing
                | RowToolRun (turn, calls) ->
                    let uses =
                        calls
                        |> List.choose (function
                            | TimelineToolUse (_, id) -> TimelineProjection.toolUse id model.Timeline
                            | _ -> None)
                    if List.isEmpty uses then Lit.nothing else toolRun turn uses)
        // A session with nothing in it yet opens on an empty column, and an empty column says
        // nothing about where the conversation starts or that the near-black composer below it
        // is where you type. So the chat carries its OWN idle symbol — a caret standing where
        // the first message will land — exactly as the terminals pane stands an idle `$` in its
        // empty pane. A mark, not a sentence: it is the same blinking caret a streaming message
        // wears, so it reads as "text goes here" without a word of instruction.
        //
        // Keyed on the ROWS, not on the rendered list: the mapping above answers a bare tool
        // use and an empty run with `Lit.nothing`, so a timeline can hold rows and still draw
        // nothing — and "has rows" would then hide the caret on a screen that is blank.
        //
        // `aria-hidden`, because it is a typographic mark rather than content: a reader that
        // cannot see it is told the timeline is empty by the timeline being empty.
        let body =
            match rows with
            | [] ->
                [ html $"""<div class="{Style.timelineIdle}" aria-hidden="true"><span class="{Style.caretIdle}"></span></div>""" ]
            | _ -> items
        html $"""<section class="{Style.timeline}" data-conversation>{body}</section>"""

    /// One block: the command that ran, then everything it printed.
    let private terminalBlockView (model: ClientModel) (feed: TerminalFeed) (block: TerminalBlock) : TemplateResult =
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
                <span class="ml-auto shrink-0">{terminalBlockStatus model block.Status}</span>
              </div>
              {body}
            </article>"""

    /// ONE card for anything waiting on a verdict (Plan 15, stage 3c): a command queued in a
    /// terminal, or a structured command parked at its gate. Approving is the same act either
    /// way, so it is the same component — the alternative being two surfaces that drift until
    /// one of them grows a button the other does not have.
    ///
    /// Rendered at two mount points from this one function: the chat column, where every
    /// pending act appears with the chip that says what it is about, and a terminal's own
    /// panel, where the list is filtered to that terminal and the chip would only repeat the
    /// heading above it.
    let private pendingCard
        (actions: ViewActions)
        (dispatch: ClientMsg -> unit)
        (model: ClientModel)
        (showSubject: bool)
        (entry: PendingAct)
        : TemplateResult =
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
        // A held act is a WAIT, and the pulse dot is the wait — the word only names the
        // blocker (the same status voice every other wait in the product wears). The full
        // explanation used to be a clause per case; the Approve button beside an awaiting
        // entry and the not-marking banner over a held queue already say what resolves it.
        let statusLine =
            if statusToken = Dom.Text.queuedAwaitingApproval then
                html $"""<span class="{Style.statusRun}"><span class="{Style.statusDotPulse}"></span>needs approval</span>"""
            elif statusToken = Dom.Text.queuedAwaitingIntegration then
                html $"""<span class="{Style.statusRun}"><span class="{Style.statusDotPulse}"></span>not marking</span>"""
            elif statusToken = Dom.Text.queuedAwaitingTerminal then
                html $"""<span class="{Style.statusRun}"><span class="{Style.statusDotPulse}"></span>terminal busy</span>"""
            else html $"""<span class="{Style.statusOk}">queued</span>"""
        // What this act IS, in words — used both as the chip and as the accessible name on
        // the verdict buttons, because a screen reader hearing "Approve" eleven times learns
        // nothing about which one it is on.
        let subjectLabel =
            match entry.Subject with
            | ForTerminal terminal ->
                TerminalProjection.tryFind terminal model.Terminals
                |> Option.map (fun view -> view.Title)
                |> Option.defaultValue (TerminalId.value terminal)
            | ForCommand tool -> tool
        let what =
            match entry.Payload with
            | CommandLine -> subjectLabel
            | CommandCall (_, _, summary) -> summary
        let subject =
            if not showSubject then Lit.nothing
            else html $"""<span class="{Style.chatChipWho}" data-pending-subject="{GateSubject.describe entry.Subject}">{subjectLabel}</span>"""
        // The body is the ONE thing that differs between the kinds. A command line is
        // characters, so it is an input any peer can fix before approving — that IS the
        // approval UX. A structured call's arguments are typed, and a form per command is
        // the JSON-Schema-subset renderer this plan deferred, so it is read-only and the
        // verdict is the whole interaction.
        let body =
            match entry.Payload with
            | CommandLine ->
                html $"""
                    <div class="{Style.terminalQueuedRow}">
                      <span class="{Style.terminalPrompt}">$</span>
                      <input type="text" class="{Style.fieldMonoBare}" aria-label="Queued command"
                             data-terminal-input="{BodyKey.terminalQueued id}">
                    </div>"""
            | CommandCall (_, _, summary) ->
                html $"""
                    <div class="{Style.terminalQueuedRow}">
                      <code class="{Style.terminalCommandText}" data-pending-summary>{summary}</code>
                    </div>"""
        let approval =
            if awaiting then
                html $"""
                    <button type="button" class="{Style.btnPrimary}" data-terminal-approve="{QueueId.value id}"
                            aria-label="Approve {what}"
                            @click={Ev(fun _ -> dispatch (ApprovePendingMsg (id, model.Peer.PeerId)); actions.FocusAfterVerdict ())}>Approve</button>"""
            elif Option.isSome entry.ApprovedBy then
                html $"""
                    <button type="button" class="{Style.btn}" data-terminal-unapprove="{QueueId.value id}"
                            aria-label="Hold {what}"
                            @click={Ev(fun _ -> dispatch (UnapprovePendingMsg id))}>Hold</button>"""
            else Lit.nothing
        // Reject sits beside approve wherever a verdict is possible, and it is offered
        // on every entry rather than only awaiting ones: under AutoRun nothing is ever
        // "awaiting", and that is exactly where being able to say no matters most.
        // Deleting is still there and still means withdrawal — this means refusal, and
        // the log records the difference.
        let reject =
            html $"""
                <button type="button" class="{Style.btn}" data-terminal-reject="{QueueId.value id}"
                        aria-label="Reject {what}"
                        @click={Ev(fun _ -> dispatch (RejectPendingMsg (id, model.Peer.PeerId, None)); actions.FocusAfterVerdict ())}>Reject</button>"""
        // Order and withdrawal belong to a queue that DRAINS serially. A command act has no
        // shell to wait for and no place in a line, so offering to move it up would be a
        // control over nothing.
        let ordering =
            match PendingAct.terminal entry with
            | None -> Lit.nothing
            | Some _ ->
                html $"""
                    <button type="button" class="{Style.btnIcon}" aria-label="Move {what} up" @click={Ev(fun _ -> match TerminalQueueOrder.moveUp model.Synced.Pending id with Some o -> dispatch (ReorderPendingMsg (id, o)) | None -> ())}>{Icon.up}</button>
                    <button type="button" class="{Style.btnIcon}" aria-label="Move {what} down" @click={Ev(fun _ -> match TerminalQueueOrder.moveDown model.Synced.Pending id with Some o -> dispatch (ReorderPendingMsg (id, o)) | None -> ())}>{Icon.down}</button>
                    <button type="button" class="{Style.btnIconDanger}" aria-label="Delete {what}" data-terminal-queue-delete="{QueueId.value id}" @click={Ev(fun _ -> dispatch (DeletePendingMsg id))}>{Icon.close}</button>"""
        html $"""
            <article class="{if awaiting then Style.terminalQueuedAwaiting else Style.terminalQueuedReady}"
                     data-terminal-queued="{QueueId.value id}" data-terminal-queued-status="{statusToken}">
              {body}
              <div class="{Style.terminalQueuedRow}">
                {statusLine}
                {subject}
                <span class="{Style.small}">{authorName model entry.Author}</span>
                <div class="ml-auto flex items-center gap-2">
                  {reject}
                  {approval}
                  {ordering}
                </div>
              </div>
            </article>"""

    /// A terminal's own pending list: the same card, filtered to this terminal, with the
    /// chip off because the heading above it already says which terminal this is.
    let private terminalQueue (actions: ViewActions) (dispatch: ClientMsg -> unit) (model: ClientModel) (terminal: TerminalId) : TemplateResult list =
        ClientModel.terminalQueue terminal model |> List.map (pendingCard actions dispatch model false)

    /// Everything waiting on a verdict, in the chat column, directly under the timeline
    /// (Plan 15, stage 3c). Not INSIDE the timeline: that is a fold over events, and a
    /// pending act is not one — it is the tail, and acts join the timeline when they resolve.
    ///
    /// Terminal commands appear here too, and that is the point rather than a side effect:
    /// approving what the agent is about to run is the same act as reading what it is about
    /// to say, and it should not require having the right panel open.
    let private pendingActs (actions: ViewActions) (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        match ClientModel.pendingActs model with
        | [] -> Lit.nothing
        | acts ->
            let cards = acts |> List.map (pendingCard actions dispatch model true)
            html $"""
                <section class="{Style.queue}" data-pending-acts aria-label="Waiting for a decision">{cards}</section>"""

    /// The lease bar (Plan 13, stage 2e): who is typing here, and the one control that
    /// changes it. Shown in place of the command lines — never in place of the queue, which
    /// keeps working while a peer is live and is precisely what the release will run.
    let private terminalLeaseBar (actions: ViewActions) (model: ClientModel) (terminal: TerminalId) (holder: ActorRef) : TemplateResult =
        let mine = ActorRef.PeerRef model.Peer.PeerId
        // The hook keeps the stable token (a test asserting WHO holds a lease should not have
        // to know what this client happens to have learned about their name); the words get
        // the name, like every other person on screen.
        let label = authorLabel holder
        // Who holds it, said the way the roster says who is here: the square avatar and the
        // name. The pulsing "live" is the state; the button is what changes it; a sentence
        // ("X is using this terminal") restated all three.
        let who = if holder = mine then "you" else authorName model holder
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
              <span class="{Style.statusRun}"><span class="{Style.statusDotPulse}"></span>live</span>
              <span class="{Style.cls [ Style.avatarSm; authorAvatar holder ]}"></span>
              <span class="{Style.small}">{who}</span>
              <div class="ml-auto flex items-center gap-2">{control}</div>
            </div>"""

    /// The live screen of a terminal in live mode (Plan 14, stage 6).
    ///
    /// A SCREEN, not a stream: the program running here moves the cursor, and what it
    /// displays is a projection of what it emitted. The platform half keeps an emulator —
    /// the same one the Session Process uses, so the two screens cannot disagree — and hands
    /// this its serialization; here it is rendered through the same ANSI spans a block's
    /// output uses.
    ///
    /// The holder's copy takes keystrokes. Everyone else's is the identical screen, live and
    /// read-only, which is the whole point of a shared terminal: watching is not a lesser
    /// mode, it is the ordinary one.
    let private terminalScreenView (actions: ViewActions) (model: ClientModel) (terminal: TerminalId) (holder: ActorRef) : TemplateResult =
        let mine = ActorRef.PeerRef model.Peer.PeerId
        let screen = ClientModel.terminalScreen terminal model |> Option.defaultValue ""
        let id = TerminalId.value terminal
        let body =
            if screen = "" then
                html $"""<div class="{Style.terminalOutputEmpty}">…</div>"""
            else html $"""{ansiText screen}"""
        if holder = mine then
            // `tabindex="0"` and a keydown handler rather than a text input: what is being
            // typed here is not a value, it is a byte stream, and an input would fight the
            // program on the other end over what the "value" is. The accessible name says
            // what it is and who has it.
            html $"""
                <div class="{Style.terminalScreen}" data-terminal-screen="{id}"
                     role="application" tabindex="0" aria-label="Live terminal, you are typing here"
                     @keydown={Ev(fun e ->
                                     match keystrokeOf e with
                                     | Some data -> actions.TypeIntoTerminal terminal data
                                     | None -> ())}>{body}</div>"""
        else
            html $"""
                <div class="{Style.terminalScreen}" data-terminal-screen="{id}"
                     role="region" aria-live="off" aria-label="Live terminal, {authorName model holder} is typing">{body}</div>"""

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
                      <span class="{Style.small}">queued commands held</span>
                      <div class="ml-auto flex items-center gap-2">
                        <button type="button" class="{Style.btnPrimary}" data-terminal-rearm="{TerminalId.value terminal}"
                                @click={Ev(fun _ -> actions.RearmTerminal terminal)}>Re-arm</button>
                      </div>
                    </div>"""
        html $"""
            <section class="{Style.terminalComposer}">
              {lostBanner}
              <div class="{Style.sideRow}">
                <label class="{Style.label}" for="terminal-mode">approval</label>
                <select id="terminal-mode" class="{Style.field} w-auto" data-terminal-mode="{ApprovalMode.describe mode}"
                        @change={EvVal(fun v -> match ApprovalMode.parse v with Some m -> dispatch (SetGateMsg (ForTerminal terminal, m)) | None -> ())}>
                  <option value="approve-agent" ?selected={mode = ApproveAgent}>the agent's commands</option>
                  <option value="approve-all" ?selected={mode = ApproveAll}>every command</option>
                  <option value="auto" ?selected={mode = AutoRun}>nothing — run them</option>
                </select>
                {takeControl}
              </div>
              {terminalQueue actions dispatch model terminal}
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
        // The per-terminal output cap (stage 3d) can eat a whole recording. Saying so is the
        // point: an empty player would be indistinguishable from a terminal that printed
        // nothing, and the whole reason the drop is recorded is that a gap in an audit trail
        // must be a stated fact.
        let gone = Map.isEmpty feed.Records && view.DroppedBytes > 0
        let closedFor =
            match view.ClosedReason with
            | Some reason -> sprintf "closed — %s" reason
            | None -> "closed"
        if gone then
            // The gap in the audit trail, stated as a status rather than narrated: the drop
            // is recorded so it can be SAID, and the caps-err voice is how this design says
            // a fact that is wrong.
            html $"""
                <section class="{Style.terminalComposer}" data-terminal-replay-gone="{TerminalId.value view.TerminalId}">
                  <div class="{Style.terminalQueuedRow}">
                    <span class="{Style.statusFaint}">{closedFor}</span>
                    <span class="{Style.statusErr}">recording not kept</span>
                  </div>
                </section>"""
        else
            // The player under this row is visibly a recording; a caption saying so was
            // chrome. The row keeps only what the player cannot show — why the terminal
            // closed.
            html $"""
                <section class="{Style.terminalComposer}">
                  <div class="{Style.terminalQueuedRow}">
                    <span class="{Style.statusFaint}">{closedFor}</span>
                  </div>
                  <div class="{Style.terminalBlocks}" role="region" aria-label="Terminal recording"
                       data-pane-replay="{PaneTab.key (TerminalTab view.TerminalId)}"></div>
                </section>"""

    /// Arrow-key movement inside the pane's tablist — the half of the ARIA tabs pattern a
    /// plain row of buttons does not give you. Declaring `role="tablist"` and leaving
    /// Left/Right dead would be a worse lie than not declaring it.
    ///
    /// Moves FOCUS only; selection follows the Enter/Space the button already handles. That
    /// is ARIA's "manual activation" variant, and it is the right one here: walking the
    /// strip must not mount and unmount a player under the reader on every keypress.
    [<Fable.Core.Emit("""(() => {
  const key = $0.key
  if (key !== 'ArrowLeft' && key !== 'ArrowRight' && key !== 'Home' && key !== 'End') return
  const tabs = Array.from($0.currentTarget.querySelectorAll('[role="tab"]'))
  if (tabs.length === 0) return
  const here = tabs.indexOf(document.activeElement)
  const next =
    key === 'Home' ? 0
    : key === 'End' ? tabs.length - 1
    : here < 0 ? 0
    : (here + (key === 'ArrowRight' ? 1 : tabs.length - 1)) % tabs.length
  tabs[next].focus()
  $0.preventDefault()
})()""")>]
    let private moveTabFocus (e: obj) : unit = Fable.Core.Util.jsNative

    /// One block's read-only view, as a tab opened from its chip shows it: the command, and
    /// everything it printed, from the chunks this client already has.
    ///
    /// The very same renderer the terminal's own history uses — a block read from the chat
    /// must not be a second rendering of a block, free to drift from the first.
    let private paneBlockView (dispatch: ClientMsg -> unit) (model: ClientModel) (terminalId: TerminalId) (blockId: BlockId) : TemplateResult =
        let found =
            TerminalProjection.tryFind terminalId model.Terminals
            |> Option.bind (fun view -> view.Blocks |> List.tryFind (fun b -> b.BlockId = blockId))
        match found with
        | None ->
            html $"""
                <div class="{Style.paneReadonly}" data-pane-block="{BlockId.value blockId}">
                  <div class="{Style.terminalOutputEmpty}">not in this client's record</div>
                </div>"""
        | Some block ->
            // The step-out, offered only where there is a whole recording to step out INTO.
            // A live terminal's recording is still being written, and rewinding one of those
            // is the DVR — a different mechanism, and not this one pretending.
            let isClosed =
                TerminalProjection.tryFind terminalId model.Terminals
                |> Option.map (fun v -> not v.IsOpen)
                |> Option.defaultValue false
            let stepOut =
                if not isClosed then Lit.nothing
                else
                    html $"""
                        <button type="button" class="{Style.btn}" data-pane-play-whole="{BlockId.value blockId}"
                                @click={Ev(fun _ -> dispatch (PlayWholeTerminalMsg (terminalId, block.FromSeq)))}>Play whole terminal</button>"""
            let body =
                match block.Status with
                // A refused command printed nothing because it never ran, and a player over
                // nothing is indistinguishable from a quiet one. The reason is the thing the
                // next reader actually wants.
                | BlockRejected (by, reason) ->
                    let text = reason |> Option.defaultValue "did not run"
                    html $"""<div class="{Style.terminalOutputEmpty}">rejected by {authorName model by} — {text}</div>"""
                // A recording that is still being written has no end to replay to, and a
                // player rebuilt on every record would thrash through a streaming build.
                // The command row above already carries the pulsing "running" status; the
                // body says only that output is still arriving, the same way a live block's
                // empty output does.
                | BlockRunning ->
                    html $"""<div class="{Style.terminalOutputEmpty}">…</div>"""
                | BlockFinished _ ->
                    html $"""
                        <div class="{Style.paneReadonly}" role="region" aria-label="Command output"
                             data-pane-replay="{PaneTab.key (BlockTab (terminalId, blockId))}"></div>"""
            html $"""
                <section class="{Style.paneBody}" data-pane-block="{BlockId.value blockId}">
                  <div class="{Style.terminalBlockCommand}">
                    <span class="{Style.terminalPrompt}">$</span>
                    <code class="{Style.terminalCommandText}">{block.Command}</code>
                    <span class="ml-auto shrink-0">{terminalBlockStatus model block.Status}</span>
                  </div>
                  {body}
                  <div class="{Style.paneFacts}">{stepOut}</div>
                </section>"""

    /// A stretch's facts: who held the terminal, for how long, and how it ended. The
    /// recording itself mounts beneath this (Plan 14, stage 4); these are the parts that
    /// come from the event log and therefore render at any scroll depth without a transcript.
    let private paneStretchView (model: ClientModel) (stretch: TerminalStretch) : TemplateResult =
        let length = durationText (TerminalStretch.duration stretch)
        let recording =
            // The count in the metadata voice (caps, tabular figures); the raw transcript
            // seqs are plumbing and stay out of the room.
            match stretch.Range with
            | Some (fromSeq, toSeq) ->
                html $"""<span class="{Style.label} tabular-nums">{toSeq - fromSeq} lines</span>"""
            // Stated, not blank: a stretch with no recorded bounds is a gap in the record,
            // and an empty player would be indistinguishable from a quiet session.
            | None ->
                html $"""<span class="{Style.statusErr}">not recorded</span>"""
        let player =
            match stretch.Range with
            | Some _ ->
                html $"""
                    <div class="{Style.paneReadonly}" role="region" aria-label="Session recording"
                         data-pane-replay="{PaneTab.key (StretchTab stretch)}"></div>"""
            | None -> Lit.nothing
        html $"""
            <section class="{Style.paneBody}">
              <div class="{Style.paneFacts}" data-pane-stretch="{TerminalStretch.key stretch}">
                <div class="{Style.terminalQueuedRow}">
                  <span class="{Style.chatChipWho}">{authorName model stretch.Holder}</span>
                  <span class="{Style.small}">typed in {stretch.Title} for {length}</span>
                  <span class="ml-auto shrink-0">{stretchEnding model stretch.End}</span>
                </div>
                {recording}
              </div>
              {player}
            </section>"""

    /// The side pane: a tab strip over three kinds of thing — a terminal, a block's
    /// read-only view, and a stretch's replay (Plan 14, stage 2).
    ///
    /// Every terminal the session has ever had is furniture in the strip; the read-only tabs
    /// are the ones this client opened by tapping a chip, and only those can be closed.
    let private terminals (actions: ViewActions) (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        let tabs = ClientModel.paneTabs model
        let selected = ClientModel.selectedPane model
        let isOn (tab: PaneTab) =
            match selected with
            | Some chosen -> PaneTab.key chosen = PaneTab.key tab
            | None -> false
        let terminalTabButton (view: TerminalView) =
            let on = isOn (TerminalTab view.TerminalId)
            let key = PaneTab.key (TerminalTab view.TerminalId)
            let id = TerminalId.value view.TerminalId
            let selectedAttr = if on then "true" else "false"
            let tabIndex = if on then "0" else "-1"
            let klass = if on then Style.terminalTabActive else Style.terminalTab
            // Who is in THIS terminal, on its tab — the same presence the roster reports, put
            // where you would look for it. Without it, a collaborator typing a command in a
            // terminal you are not showing is visible nowhere in this column.
            let peers =
                ClientModel.peersInTerminal view.TerminalId model
                |> List.map (fun (peer, name) ->
                    html $"""
                        <span class="{Style.draftEditorDot}" style="background:{PeerColour.ofPeer peer}"
                              title="{name}" data-terminal-tab-peer="{PeerId.value peer}"></span>""")
            // Two literal spellings of one button, because lit-html cannot inject an
            // attribute NAME through a hole — and the open/closed hooks must stay apart:
            // there is nothing to run in a closed terminal, only something to read.
            if view.IsOpen then
                html $"""
                    <button type="button" role="tab" class="{klass}" data-pane-tab="{key}" data-terminal-tab="{id}"
                            aria-selected="{selectedAttr}" tabindex="{tabIndex}"
                            @click={Ev(fun _ -> dispatch (SelectTerminalMsg view.TerminalId))}>{view.Title}<span class="{Style.terminalTabPeers}">{peers}</span></button>"""
            else
                html $"""
                    <button type="button" role="tab" class="{klass}" data-pane-tab="{key}" data-terminal-closed-tab="{id}"
                            aria-selected="{selectedAttr}" tabindex="{tabIndex}"
                            @click={Ev(fun _ -> dispatch (SelectTerminalMsg view.TerminalId))}>{view.Title}<span class="{Style.small}"> · closed</span><span class="{Style.terminalTabPeers}">{peers}</span></button>"""
        let openedTabButton (tab: PaneTab) =
            let on = isOn tab
            let label =
                match tab with
                | TerminalTab id -> TerminalId.value id
                | BlockTab (terminalId, blockId) ->
                    TerminalProjection.tryFind terminalId model.Terminals
                    |> Option.bind (fun v -> v.Blocks |> List.tryFind (fun b -> b.BlockId = blockId))
                    |> Option.map (fun b -> b.Command)
                    |> Option.defaultValue (BlockId.value blockId)
                | StretchTab stretch -> sprintf "%s · %s" (authorName model stretch.Holder) stretch.Title
            html $"""
                <span class="{Style.paneTabGroup}">
                  <button type="button" role="tab" class="{if on then Style.terminalTabActive else Style.terminalTab}"
                          data-pane-tab="{PaneTab.key tab}"
                          aria-selected="{if on then "true" else "false"}" tabindex="{if on then "0" else "-1"}"
                          @click={Ev(fun _ -> dispatch (SelectPaneTabMsg tab))}>{label}</button>
                  <button type="button" class="{Style.paneTabClose}" data-pane-tab-close="{PaneTab.key tab}"
                          aria-label="Close this view of {label}"
                          @click={Ev(fun _ -> dispatch (ClosePaneTabMsg tab); actions.FocusChat (PaneTab.key tab))}>{Icon.close}</button>
                </span>"""
        let tabButton (tab: PaneTab) =
            match tab with
            | TerminalTab id ->
                match TerminalProjection.tryFind id model.Terminals with
                | Some view -> terminalTabButton view
                | None -> Lit.nothing
            | BlockTab _ | StretchTab _ -> openedTabButton tab
        let terminalBody (view: TerminalView) =
            let feed = ClientModel.terminalFeed view.TerminalId model
            let truncated =
                if view.DroppedBytes > 0 then
                    html $"""<div class="{Style.terminalTruncated}" data-terminal-truncated="{string view.DroppedBytes}">{view.DroppedBytes} bytes dropped</div>"""
                else Lit.nothing
            let blocks =
                // An idle prompt IS the empty state a terminal-shaped surface already has a
                // symbol for; "nothing has run here yet" was the same fact as a sentence.
                if List.isEmpty view.Blocks then
                    [ html $"""<div class="{Style.terminalOutputEmpty}"><span class="{Style.terminalPrompt}">$</span></div>""" ]
                else view.Blocks |> List.map (terminalBlockView model feed)
            // The DVR (Plan 14, stage 7): step back through what this terminal has recorded
            // so far while it keeps running, and catch back up. Offered on any LIVE terminal
            // — the mechanism does not care which mode it is in, and both are one growing
            // byte stream — but only once something IS recorded: a DVR with nothing behind
            // it is a control with nothing to do. Each press hands focus to the control
            // that replaces the pressed one, which leaves the document.
            let rewound = ClientModel.isRewound view.TerminalId model
            let dvr =
                if not view.IsOpen then Lit.nothing
                elif rewound then
                    // Say HOW FAR behind, in the recording's clock, as it grows — a reader
                    // parked behind live deserves to know the edge is moving away.
                    let behind =
                        match ClientModel.behindLive view.TerminalId model with
                        | Some seconds when seconds >= 1.0 ->
                            sprintf "behind live — %s" (durationText (System.TimeSpan.FromSeconds seconds))
                        | _ -> "behind live"
                    html $"""
                        <div class="{Style.terminalQueuedRow}">
                          <span class="{Style.statusFaint}" data-terminal-behind="{TerminalId.value view.TerminalId}">{behind}</span>
                          <button type="button" class="{Style.cls [ Style.btnPrimary; "ml-auto" ]}"
                                  data-terminal-live="{TerminalId.value view.TerminalId}"
                                  @click={Ev(fun _ -> dispatch (JumpToLiveMsg view.TerminalId); actions.FocusDvr view.TerminalId)}>Jump to live</button>
                        </div>"""
                elif feed.KnownLength > 0 then
                    html $"""
                        <div class="{Style.terminalQueuedRow}">
                          <button type="button" class="{Style.cls [ Style.btn; "ml-auto" ]}"
                                  data-terminal-rewind="{TerminalId.value view.TerminalId}"
                                  @click={Ev(fun _ -> dispatch (RewindTerminalMsg view.TerminalId); actions.FocusDvr view.TerminalId)}>Rewind</button>
                        </div>"""
                else Lit.nothing
            // In live mode the block history gives way to the SCREEN (Plan 14, stage 6). A
            // program is running here and what it displays is not a list of commands and
            // their output — the blocks are block mode's view of a terminal, and they come
            // back the moment the lease does. The transcript keeps both either way.
            let above =
                if rewound then
                    // Behind the live edge: the recording, played. The same mount and the
                    // same cast a finished terminal's replay uses, which is exactly what
                    // "rewound like live TV, through the same mechanism" has to mean.
                    html $"""
                        {dvr}
                        <div class="{Style.paneReadonly}" role="region" aria-label="Terminal recording, behind live"
                             data-pane-replay="{PaneTab.key (TerminalTab view.TerminalId)}"></div>"""
                else
                    match view.Lease with
                    | Some holder ->
                        html $"""
                            <div class="{Style.terminalBlocks}" data-terminal-id="{TerminalId.value view.TerminalId}">
                              {truncated}
                            </div>
                            {dvr}
                            {terminalScreenView actions model view.TerminalId holder}"""
                    | None ->
                        html $"""
                            <div class="{Style.terminalBlocks}" data-terminal-id="{TerminalId.value view.TerminalId}">
                              {truncated}
                              {blocks}
                            </div>
                            {dvr}"""
            html $"""
                {above}
                {if not view.IsOpen then terminalReplay model view
                 // Behind the live edge there is nothing to type into and nothing to queue
                 // against what you are watching: the way back is "jump to live", above.
                 elif rewound then Lit.nothing
                 else terminalComposer actions dispatch model view.TerminalId}"""
        let body =
            match selected with
            // The empty pane wears the terminal's own symbol — an idle prompt, display-sized
            // — and the one button that fills it. What a terminal IS was a paragraph here;
            // the glyph and the verb say it.
            | None ->
                html $"""
                    <div class="{Style.terminalEmpty}">
                      <span class="font-mono text-[28px] leading-8 text-ink-faint select-none" aria-hidden="true">$</span>
                      <button type="button" class="{Style.btnPrimary}" data-terminal-new
                              @click={Ev(fun _ -> actions.OpenTerminal "terminal")}>New terminal</button>
                    </div>"""
            | Some tab ->
                let inner =
                    match tab with
                    | TerminalTab id ->
                        match TerminalProjection.tryFind id model.Terminals with
                        | Some view -> terminalBody view
                        | None -> Lit.nothing
                    | BlockTab (terminalId, blockId) -> paneBlockView dispatch model terminalId blockId
                    | StretchTab stretch -> paneStretchView model stretch
                // `tabindex="-1"` so the panel can take focus programmatically when a chip
                // opens it, without becoming a Tab stop of its own. A DOM swap that leaves
                // focus on the control that vanished is the failure this exists to avoid.
                html $"""
                    <div class="{Style.paneBody}" role="tabpanel" tabindex="-1"
                         data-pane-panel="{PaneTab.key tab}">
                      {inner}
                    </div>"""
        // Offered only for a terminal that is actually open: a "close" on a closed one either
        // does nothing or reports an error, and both are worse than not being there.
        let closeSelected =
            match selected with
            | Some (TerminalTab id) ->
                match TerminalProjection.tryFind id model.Terminals with
                | Some view when view.IsOpen ->
                    html $"""
                        <button type="button" class="{Style.cls [ Style.terminalTab; "ml-auto" ]}" data-terminal-close="{TerminalId.value view.TerminalId}"
                                aria-label="Close terminal" @click={Ev(fun _ -> actions.CloseTerminal view.TerminalId)}>close</button>"""
                | _ -> Lit.nothing
            | _ -> Lit.nothing
        html $"""
            <aside class="{Style.terminalPanel}" data-terminal-panel>
              <div class="{Style.terminalPane}">
                <div class="{Style.terminalHead}">
                  <span class="{Style.settingsTitle}">terminals</span>
                  <button type="button" class="{Style.navChevronForward}" aria-label="Back to the chat"
                          data-terminal-toggle="hide"
                          @click={Ev(fun _ ->
                                        dispatch ToggleTerminalsMsg
                                        // On a phone this control IS the way back, and it is
                                        // about to leave the screen — so focus goes where the
                                        // reader came from, exactly as closing a tab does.
                                        selected |> Option.iter (PaneTab.key >> actions.FocusChat))}>{Icon.right}</button>
                </div>
                <div class="{Style.terminalTabs}" role="tablist" aria-label="Terminals and recordings"
                     @keydown={Ev(fun e -> moveTabFocus e)}>
                  {tabs |> List.map tabButton}
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
              {chat actions dispatch model}
              {pendingActs actions dispatch model}
              {agentStrip actions model.Agent}
              {queue dispatch model.Synced}
              {drafts actions dispatch model}
            </div>
            {terminals actions dispatch model}"""
