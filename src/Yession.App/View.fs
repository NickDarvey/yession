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
      /// Take the reader TO settings, never back. `ToggleSettings`'s one-way sibling, and
      /// the difference matters for exactly one caller: the sign-in prompt over the timeline
      /// is on screen whenever a credential needs one — including while the settings face is
      /// already open — so a toggle there would shut the panel it is pointing at.
      RevealSettings : unit -> unit
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
      /// Try the session again NOW, rather than when the supervised loop next would.
      ///
      /// A trigger, never a second schedule (Plan 20): it shortens the wait the lifecycle is
      /// already in. It earns its place on the one client the loop deliberately will not
      /// carry — a peer whose token was refused, which no amount of waiting fixes and which
      /// therefore parks until somebody asks.
      RetryNow : unit -> unit
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
      /// Ask the provider for a closed terminal's stream again (Plan 19, step 4).
      ReattachTerminal : TerminalId -> unit
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
      /// Hand focus to a terminal's watch toggle when the reader has been stranded (Plan 14,
      /// stage 7; Plan 25, stage 3).
      ///
      /// The toggle itself never needs this: it relabels in place, so a press keeps its own
      /// focus. What does is the AUTOMATIC catch-up — a rewound cast playing off its end
      /// unmounts the player under whoever was focused inside it — and that is the only
      /// caller left now the four differently-named exits have become one control.
      FocusWatch : unit -> unit
      /// Scroll a terminal's history to one of its commands and mark it (Plan 25, stage 3) —
      /// the browser's half of "show in terminal". Imperative for the same reason `FocusPane`
      /// is: the model moves the reader's position, and only the document can scroll.
      RevealBlock : TerminalId -> BlockId -> unit }

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
          RevealSettings = ignore
          ClaudeConnect = ignore
          ClaudeComplete = ignore
          ClaudePasteToken = ignore
          ClaudeDisconnect = ignore
          GitHubConnect = ignore
          GitHubPasteToken = ignore
          GitHubDisconnect = ignore
          ReopenSession = ignore
          RetryNow = ignore
          OpenTerminal = ignore
          CloseTerminal = ignore
          SendTerminalDraft = fun _ _ -> ()
          TakeTerminal = ignore
          ReleaseTerminal = ignore
          RearmTerminal = ignore
          ReattachTerminal = ignore
          TypeIntoTerminal = fun _ _ -> ()
          ResizeTerminal = fun _ _ _ -> ()
          FocusPane = ignore
          FocusChat = ignore
          FocusWatch = ignore
          RevealBlock = fun _ _ -> () }

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
                // The reason, and the one thing a person can do about it. The supervised loop
                // is already trying; this is for the client it will not carry — a refused peer
                // parks until asked — and for anyone who would rather not wait out a backoff.
                html $"""
                  <span class="{Style.small}" data-connection-reason>{reason}</span>
                  <button type="button" class="{Style.btn}" data-retry-now
                          @click={Ev(fun _ -> actions.RetryNow ())}>{Dom.Text.retryNow}</button>"""
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
        // The agent's row says whether a turn can RUN, which is not the same question as
        // whether a credential is stored. `agentAvailable` answers the second (any relevant
        // credential, or the host's ambient one), so a Claude sign-in that has stopped
        // working left this reading "ready" in green over a credential the next turn would
        // fail on. The status word follows the credential's health; the gate does not, so
        // the no-agent prompt keeps meaning "nothing is connected" rather than doubling up.
        let claudeNeedsSignIn =
            [ model.Claude.Status.MineCredential; model.Claude.Status.SessionCredential ]
            |> List.exists (fun credential ->
                credential |> Option.map (fun c -> c.SignInRequired.IsSome) |> Option.defaultValue false)
        let agentRow =
            match model.Claude.Status.AgentAvailable with
            | Some true when claudeNeedsSignIn ->
                html $"""<div class="{Style.person}" data-agent-presence="live"><span class="{Style.cls [ Style.avatar; Style.agentAvatar; Style.personAvatar ]}"></span>agent<span class="{Style.statusErr} ml-auto"><span class="{Style.statusDot}"></span>{Dom.Text.signInAgainStatus}</span></div>"""
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

    /// What the non-session sign-in scope actually reaches here. On a deployment that
    /// attributes its users it is that person's own; on one that attributes nobody
    /// (`--auth localhost`) it is EVERYONE who can reach this Manager, and calling that
    /// "mine" would be the panel promising something the deployment cannot keep.
    /// Unknown until the first status probe answers — say the cautious thing meanwhile.
    let private sharedScopeLabel (owner: string option) : string =
        match owner with
        | Some "user" -> "All my sessions"
        | _ -> "Everyone using this Yession"

    /// How a connected credential reads on a panel row: green and its kind, or the fault
    /// style and the words that name the fix. ONE function, because both connection panels
    /// say the same thing about the same shape — a credential that read "sign in again" in
    /// one panel and green in the other would be two copies having drifted, not a design.
    ///
    /// The health is a STATUS here, not a second call to action. Each panel's own Connect
    /// control is already directly below this row and IS the remedy; the one new button for
    /// this lives over the timeline, where somebody who never opens settings will meet it.
    let private credentialStatus (label: string) (credential: ConnectionView) : TemplateResult =
        match credential.SignInRequired with
        | None ->
            html $"""<span class="{Style.statusOk}"><span class="{Style.statusDot}"></span>{label} ({credential.Kind})</span>"""
        | Some _ ->
            html $"""<span class="{Style.statusErr}"><span class="{Style.statusDot}"></span>{label} — {Dom.Text.signInAgainStatus}</span>"""

    /// Why, under the row, and only when there is a why. It is the provider's own words, so
    /// it says what a person could not have guessed — "the refresh token has expired" and
    /// "github rejected this credential" send them to the same button knowing different
    /// things about how they got here.
    let private credentialReason (hook: string) (scopeChoice: string) (credential: ConnectionView) : TemplateResult =
        match credential.SignInRequired with
        | None -> Lit.nothing
        | Some reason -> html $"""<span class="{Style.small}" {hook}="{scopeChoice}">{reason}</span>"""

    /// The Claude connection panel (Plan 08), living in the settings drawer: status per
    /// sign-in scope, the OAuth flow (approve on claude.ai → paste the shown code), and
    /// the paste-a-token fallback.
    let private claudeSection (actions: ViewActions) (dispatch: ClientMsg -> unit) (claude: ClaudeViewState) : TemplateResult =
        let connectedRow (label: string) (scopeChoice: string) (credential: ConnectionView option) =
            match credential with
            | Some credential ->
                html $"""
                    <div class="{Style.sideRow}" data-claude-connected="{scopeChoice}">{credentialStatus label credential}<button type="button" class="{Style.btnIconBareDanger}" aria-label="Disconnect" data-claude-disconnect="{scopeChoice}" @click={Ev(fun _ -> actions.ClaudeDisconnect scopeChoice)}>{Icon.close}</button></div>
                    {credentialReason Dom.Hooks.claudeSignInRequired scopeChoice credential}"""
            | None -> html $""""""
        let controls =
            match claude.Flow with
            | ClaudeBusy ->
                html $"""<span class="{Style.statusRun}" data-claude-busy><span class="{Style.statusDotPulse}"></span>working…</span>"""
            | ClaudeAwaitingCode (url, _) ->
                html $"""
                    <a class="{Style.btnPrimary}" href="{url}" target="_blank" rel="noreferrer" data-claude-authorize>Approve on claude.ai</a>
                    <label class="{Style.label}" for="claude-code">code from claude.ai</label>
                    <input id="claude-code" type="text" class="{Style.field}" data-claude-code placeholder="code#state"
                           autocapitalize="off" autocorrect="off" autocomplete="off" spellcheck="false" />
                    <div class="flex gap-2">
                      <button type="button" class="{Style.btnPrimary}" data-claude-complete @click={Ev(fun _ -> actions.ClaudeComplete ())}>Complete</button>
                      <button type="button" class="{Style.btn}" data-claude-cancel @click={Ev(fun _ -> dispatch (ClaudeFlowMsg ClaudeIdle))}>Cancel</button>
                    </div>"""
            | ClaudeIdle | ClaudeError _ ->
                html $"""
                    <label class="{Style.label}" for="claude-scope">sign in for</label>
                    <select id="claude-scope" class="{Style.field}" data-claude-scope aria-label="Sign-in scope">
                      <option value="mine">{sharedScopeLabel claude.Status.Owner}</option>
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
              {connectedRow ((sharedScopeLabel claude.Status.Owner).ToLowerInvariant ()) "mine" claude.Status.MineCredential}
              {connectedRow "this session" "session" claude.Status.SessionCredential}
              {error}
              {controls}
            </section>"""

    /// The model picker, beside the account that pays for it: which model this session's
    /// turns run on, chosen from whatever the session's provider offers.
    ///
    /// It always offers the provider's own default, and it always offers whatever the
    /// session has CHOSEN — even a model the catalogue no longer lists, because a control
    /// that silently displays something other than the setting behind it is worse than one
    /// showing an id nobody recognises. Everything else is the catalogue, in a person's
    /// order rather than the provider's.
    ///
    /// Nothing here knows which provider answered. The section says "model", the options
    /// carry ids and names a provider gave, and a second provider would change neither.
    let private modelSection (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        let chosen = model.Synced.Model
        let offered =
            match model.Models with
            | ModelsLoaded models -> ModelCatalogue.ordered models
            | ModelsUnknown
            | ModelsUnavailable _ -> []
        // The chosen model, when the catalogue does not carry it: shown by its id, which is
        // the only name anything here has for it.
        let orphan =
            match chosen with
            | Some id when offered |> List.forall (fun m -> m.Id <> id) -> [ AgentModel.create id "" ]
            | _ -> []
        let options =
            (orphan @ offered)
            |> List.map (fun offer ->
                let id = ModelId.value offer.Id
                html $"""<option value="{id}" ?selected={chosen = Some offer.Id}>{offer.Name}</option>""")
        // What the picker cannot yet offer, said rather than left as a short list nobody can
        // explain. A lookup that failed is almost always "no account connected here yet",
        // and the panel above this one is the way out of that.
        let note =
            match model.Models with
            | ModelsUnknown -> html $"""<span class="{Style.small}" data-model-note="pending">…</span>"""
            | ModelsLoaded [] ->
                html $"""<span class="{Style.small}" data-model-note="empty">this provider offered no models to choose from</span>"""
            | ModelsLoaded _ -> Lit.nothing
            | ModelsUnavailable reason ->
                html $"""<span class="{Style.small}" data-model-note="unavailable">{reason}</span>"""
        html $"""
            <section class="{Style.cls [ Style.sideSection; Style.settingsLane1 ]}" data-model-panel>
              <label class="{Style.label}" for="agent-model">model</label>
              <div class="{Style.fieldSelectWrap}">
                <select id="agent-model" class="{Style.fieldSelect}"
                        data-model-select="{chosen |> Option.map ModelId.value |> Option.defaultValue Dom.Text.modelDefault}"
                        @change={EvVal(fun v -> dispatch (SetModelMsg (match ModelId.create v with Ok id -> Some id | Error _ -> None)))}>
                  <option value="" ?selected={chosen.IsNone}>{Dom.Text.modelDefaultLabel}</option>
                  {options}
                </select>
                <span class="{Style.fieldSelectMark}">{Icon.down}</span>
              </div>
              {note}
            </section>"""

    /// The GitHub connection panel (Plan 14), beside the Claude one: status per sign-in
    /// scope, the device flow (show the code → approve on github.com → the poll lands
    /// the grant), and the paste-a-token fallback.
    let private githubSection (actions: ViewActions) (dispatch: ClientMsg -> unit) (github: GitHubViewState) : TemplateResult =
        let connectedRow (label: string) (scopeChoice: string) (credential: ConnectionView option) =
            match credential with
            | Some credential ->
                html $"""
                    <div class="{Style.sideRow}" data-github-connected="{scopeChoice}">{credentialStatus label credential}<button type="button" class="{Style.btnIconBareDanger}" aria-label="Disconnect GitHub" data-github-disconnect="{scopeChoice}" @click={Ev(fun _ -> actions.GitHubDisconnect scopeChoice)}>{Icon.close}</button></div>
                    {credentialReason Dom.Hooks.githubSignInRequired scopeChoice credential}"""
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

    /// Settings, as the sidebar column's OTHER FACE. Not a drawer over the conversation: you
    /// go there and come back, the timeline never moves under a scrim, and configuration keeps
    /// the section rhythm it already had. Open state is the root element's `settings-open`
    /// class — presentation, not model — so it survives re-renders.
    ///
    /// It is laid out as the nav face's mirror: identity in the head (where the wordmark sits),
    /// the way out at the foot (where `settings ›` sits), and the column's own collapse control
    /// in the same corner on both faces — chrome that belongs to the column, not to a face, so
    /// it never disappears under you.
    /// Said only where it is true, and only once: a client that cannot keep history keeps
    /// none, and the alternative to saying so is a session that silently stops remembering.
    /// Not in the degraded strip — that reports LEGS that are down, and this is a property of
    /// how the page was served, fixed for the whole life of the document.
    let private historyStoreNote (model: ClientModel) : TemplateResult =
        if model.CanKeepHistory then Lit.nothing
        else
            html $"""
                <section class="{Style.sideSection}" data-history-store="none">
                  <span class="{Style.label}">history</span>
                  <span class="{Style.small}">{Dom.Text.historyNotKept}</span>
                </section>"""

    let private settingsPane (actions: ViewActions) (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        html $"""
            <div class="{Style.settingsPane}" data-settings-panel>
              <div class="{Style.cls [ Style.settingsHead; Style.settingsLane0 ]}">
                <span class="{Style.settingsTitle}">settings</span>
                <button type="button" class="{Style.navChevronBack}" aria-label="Collapse sidebar" data-nav-toggle="hide" @click={Ev(fun _ -> actions.ToggleNav ())}>{Icon.left}</button>
              </div>
              {claudeSection actions dispatch model.Claude}
              {modelSection dispatch model}
              {githubSection actions dispatch model.GitHub}
              {queriesSection model.Queries}
              {historyStoreNote model}
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

    /// The one call to action for a credential that stopped working, over the timeline where
    /// somebody who never opens settings will meet it.
    ///
    /// It is the only NEW button this feature adds. The panel rows and the agent's roster row
    /// say the same fact as a status, because a call to action repeated is wallpaper — the
    /// rule `Style.fs`'s `noAgent*` block and the acceptance suite both already encode. Here
    /// it is a button because, unlike a degraded leg, a dead credential does not recover on
    /// its own.
    ///
    /// Shown whenever anything needs signing in, at every width — NOT only while the sidebar
    /// is off screen, which is how the header's "no agent" stand-in behaves. The two differ
    /// because their subjects do: an absent agent is a state you chose and can see in the
    /// roster, while a credential that died is news, and news that only reaches you if a
    /// column happens to be collapsed is news that does not reach you.
    ///
    /// Silent while the session leg is down, for a reason and not for tidiness: signing in
    /// runs against the session, so a prompt offering it to somebody who cannot reach the
    /// session is offering a button that cannot work. The degraded strip owns that moment,
    /// and one strip at a time is what it has always promised.
    let private signInPrompt (actions: ViewActions) (model: ClientModel) : TemplateResult =
        match model.Connection, ClientModel.signInRequired model with
        | Disconnected _, _
        | _, [] -> Lit.nothing
        // Whichever came first in the derivation's settled order. A second row would be the
        // same instruction twice, and the panel it opens shows every provider anyway.
        | _, (provider, reason) :: _ ->
            html $"""
                <section class="{Style.signInPrompt}" data-signin-required="{provider}">
                  <span class="{Style.statusErr}"><span class="{Style.statusDot}"></span>{provider}</span>
                  <span class="{Style.cls [ Style.small; Style.signInPromptReason ]}">{reason}</span>
                  <button type="button" class="{Style.btnPrimary}"
                          data-signin-again data-settings-toggle="prompt"
                          @click={Ev(fun _ -> actions.RevealSettings ())}>{Dom.Text.signInAgain}</button>
                </section>"""


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
    [<Fable.Core.Emit("(function (e) { return (e && e.target && typeof e.target.selectionStart === 'number') ? [e.target.selectionStart, e.target.selectionEnd] : null })($0)")>]
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
    [<Fable.Core.Emit("""(function (e) {
  const ev = e
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
})($0)""")>]
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
              <div class="{Style.titleWrap}">
                <input type="text" class="{Style.titleInput}" data-session-title aria-label="Session title" placeholder="session"
                       autocapitalize="off" autocorrect="off" autocomplete="off" spellcheck="false"
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
                        <button type="button" class="{Style.btnIconBare}" aria-label="Move up" data-queue-up="{QueueId.value id}" @click={Ev(fun _ -> match QueueOrder.moveUp synced.Queue id with Some o -> dispatch (ReorderQueuedMsg (id, o)) | None -> ())}>{Icon.up}</button>
                        <button type="button" class="{Style.btnIconBare}" aria-label="Move down" data-queue-down="{QueueId.value id}" @click={Ev(fun _ -> match QueueOrder.moveDown synced.Queue id with Some o -> dispatch (ReorderQueuedMsg (id, o)) | None -> ())}>{Icon.down}</button>
                        <button type="button" class="{Style.btnIconBareDanger}" aria-label="Delete" data-queue-delete="{QueueId.value id}" @click={Ev(fun _ -> dispatch (DeleteQueuedMsg id))}>{Icon.close}</button>
                      </div>
                    </article>""")
        let band = if List.isEmpty entries then Style.queueEmpty else Style.queue
        html $"""<section class="{band}" data-message-queue>{head}{items}</section>"""

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
                        <button type="button" class="{Style.btnDiscardInField}" aria-label="Discard draft"
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
                if hasContent then Style.btnSendInField else Style.btnSendInFieldWaiting
            let author =
                if target = myPeer then Lit.nothing
                else html $"""<span class="{Style.draftAuthor}">{ClientModel.nameOf target model}'s message</span>"""
            html $"""
                <article class="{Style.draftBox}" data-draft-id="{PeerId.value target}" data-draft-author="{PeerId.value target}">
                  <div class="{Style.draftBody}">
                    {author}
                    <div class="{Style.draftInput}" data-rich-body="{BodyKey.draft target}" data-rich-readonly="false" data-draft-input="{PeerId.value target}"></div>
                  </div>
                  <div class="{Style.draftCommit}">
                    <span class="{Style.draftEditors}">{editors target}</span>
                    {discard}
                    <button type="button" class="{sendClass}" aria-label="Send" aria-keyshortcuts="Enter"
                            title="{Dom.Text.composerKeys}"
                            data-send-draft="{PeerId.value target}" @click={Ev(fun _ -> actions.SendDraft target)}>{Icon.send}</button>
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
              <span class="{Style.bandRail}"></span>
              <span class="{Style.bandEdge}"></span>
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
            // Why this turn exists, when nobody asked for it (Plan 20, stage 2). On the meta
            // line rather than in the body: it is attribution, and the body is what the agent
            // said — a sentence the agent did not say does not go in it.
            let wokeInner =
                match item.Woke with
                | None -> Lit.nothing
                | Some reason ->
                    // One word on screen whatever the reason — the meta line is three short
                    // words wide, and a turn that ran unasked says the same thing about
                    // itself however it came to. WHICH reason lives in the hook a test reads
                    // and the title a person can ask for.
                    let token, title =
                        match reason with
                        | CommandFinished -> Dom.Text.wokeCommandFinished, Dom.Text.turnWokeCommandFinished
                        | StreamEnded _ -> Dom.Text.wokeStreamEnded, Dom.Text.turnWokeStreamEnded
                        | IntegrationLost _ -> Dom.Text.wokeIntegrationLost, Dom.Text.turnWokeIntegrationLost
                    html $"""<span class="{Style.statusFaint}" data-message-woke="{token}" title="{title}">{Dom.Text.turnWoke}</span>"""
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
            let bodyClass = Style.cls [ bodyClass; Style.messageVoice isAgent ]
            // The author line is the GROUP's to say (see `group` below); a message's own meta
            // line exists only while it has news of its own — streaming, failed, woken unasked.
            let meta =
                if item.Woke.IsSome || item.Status <> ConversationItemStatus.Complete then
                    html $"""<div class="{Style.messageMeta}">{wokeInner}{statusInner}</div>"""
                else Lit.nothing
            html $"""
                <article class="{Style.messageItem}" data-message-id="{MessageId.value item.MessageId}" data-message-author="{authorLabel item.Author}" data-message-status="{messageStatusLabel item.Status}">
                  {meta}
                  <div class="{bodyClass}" data-message-body>{RichText.render item.Body}{caret}</div>
                </article>"""
        // One line: who ran what, and how it went. No output — a tail inline would make the
        // chat noisiest exactly when it is busiest, and would put everything a command
        // printed one glance from anyone in the session rather than one tap.
        let blockOf (terminalId: TerminalId) (blockId: BlockId) =
            TerminalProjection.tryFind terminalId model.Terminals
            |> Option.bind (fun view -> view.Blocks |> List.tryFind (fun b -> b.BlockId = blockId))
        // No author of its own: WHO ran it is the group's author line above it (`group`
        // below), the same answer a message gets. Takes the block already RESOLVED, because
        // the grouping fold needs the block's authority before it can place the chip — a
        // chip whose block a page boundary withheld never becomes an entry at all.
        let blockChip (terminalId: TerminalId) (block: TerminalBlock) =
            let blockId = block.BlockId
            html $"""
                <button type="button" class="{Style.chatChip}"
                        data-chat-block="{BlockId.value blockId}"
                        data-chat-block-status="{terminalBlockStatusLabel block.Status}"
                        data-terminal-id="{TerminalId.value terminalId}"
                        @click={Ev(fun _ -> dispatch (ShowInPaneMsg (Reading (BlockTab (terminalId, blockId)))); actions.FocusPane ())}>
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
                        @click={Ev(fun _ -> dispatch (ShowInPaneMsg (Reading (StretchTab stretch))); actions.FocusPane ())}>
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
                    <span class="{Style.chatChipText}">used {summary}</span>
                  </summary>
                  {uses |> List.map toolCall}
                </details>"""
        // One agent burst: the commands one turn ran, in one row (Plan 20, stage 4). The
        // lines ARE block chips — same element, same click, same hooks — so a chip does not
        // change what it is by being grouped, and nothing here has to be kept in step with
        // the ungrouped case.
        let taskCard (turn: AgentTurnId) (blocks: (TerminalId * TerminalBlock) list) =
            let lines =
                blocks
                |> List.map (fun (terminalId, block) -> (terminalId, block), TaskCard.stateOf block.Status)
                |> TaskCard.ordered
            let tally = TaskCard.tally (lines |> List.map snd)
            // Each count in the glyph and colour its status already wears on a chip, and only
            // when it is non-zero: `0 ✗` prints red where nothing is wrong, which is the one
            // thing this line must never do.
            let count n inner = if n = 0 then Lit.nothing else inner
            let failed =
                count tally.Failed (html $"""<span class="{Style.statusErr}">{Icon.crossSm} {tally.Failed}</span>""")
            let running =
                count tally.Running (html $"""<span class="{Style.statusRun}"><span class="{Style.statusDotPulse}"></span>{tally.Running}</span>""")
            let done' =
                count tally.Done (html $"""<span class="{Style.statusOk}">{Icon.checkSm} {tally.Done}</span>""")
            let counts =
                html $"""<span class="{Style.chatTaskCounts}">{failed}{running}{done'}</span>"""
            let commands =
                if tally.Commands = 1 then "1 command" else sprintf "%d commands" tally.Commands
            html $"""
                <details class="{Style.chatTaskCard}" data-chat-task-card="{AgentTurnId.value turn}">
                  <summary class="{Style.chatTaskSummary}">
                    <span class="{Style.chatChipText}">ran {commands}</span>
                    {counts}
                  </summary>
                  {lines |> List.map (fun ((terminalId, block), _) -> blockChip terminalId block)}
                </details>"""
        let rows = TimelineProjection.rows model.Conversation model.Timeline
        // Every row resolved to (whose act it is, its rendering) BEFORE grouping, so a row
        // whose backing state a page boundary withheld contributes no entry — never an empty
        // element, and never an author line standing over nothing. An act note's actor is
        // `None`: its sentence carries its own name, so it neither takes an author line nor
        // joins a run under one.
        let entries : (ActorRef option * TemplateResult) list =
            rows
            |> List.choose (function
                | RowItem (TimelineMessage item) ->
                    match item.Kind with
                    | ConversationItemKind.ActNote -> Some (None, actNoteItem item)
                    | ConversationItemKind.Message -> Some (Some item.Author, messageItem item)
                | RowItem (TimelineBlock (_, terminalId, blockId)) ->
                    // Both folds read the same page, so a chip without its block is a page
                    // boundary, not a bug: the next page brings it.
                    blockOf terminalId blockId
                    |> Option.map (fun block ->
                        Some (Authority.author block.Authority), blockChip terminalId block)
                | RowItem (TimelineStretch stretch) -> Some (Some stretch.Holder, stretchItem stretch)
                // `rows` never puts a tool use in a bare row, and never a run of anything
                // else — but both are `TimelineItem`s, so the types cannot say so.
                | RowItem (TimelineToolUse _) -> None
                | RowToolRun (turn, calls) ->
                    let uses =
                        calls
                        |> List.choose (function
                            | TimelineToolUse (_, id) -> TimelineProjection.toolUse id model.Timeline
                            | _ -> None)
                    if List.isEmpty uses then None
                    else Some (Some ActorRef.Agent, toolRun turn uses)
                | RowTaskCard (turn, items) ->
                    let blocks =
                        items
                        |> List.choose (function
                            | TimelineBlock (_, terminalId, blockId) ->
                                blockOf terminalId blockId |> Option.map (fun block -> terminalId, block)
                            | _ -> None)
                    // A card at a page boundary can be short a line, exactly as a lone chip
                    // can be missing: the next page brings it. Below two it is no longer a
                    // burst, so it draws as the chips it is — never as a card of one.
                    match blocks with
                    | [] -> None
                    | [ (terminalId, block) ] ->
                        Some (Some (Authority.author block.Authority), blockChip terminalId block)
                    | many -> Some (Some ActorRef.Agent, taskCard turn many))
        // Consecutive acts by ONE actor fold under one author line — the avatar and name a
        // message used to repeat per turn, said once where the speaker changes. The line is
        // sticky (`Style.messageGroupHead`), so a run longer than the screen keeps saying
        // whose it is; and it is what makes a lone tool run or command wear the same
        // attribution a message does, instead of a private caps label of its own.
        let group (actor: ActorRef) (members: TemplateResult list) =
            let whoClass = if actor = ActorRef.Agent then Style.whoAgent else Style.who
            html $"""
                <section class="{Style.messageGroup}" data-message-author="{authorLabel actor}">
                  <header class="{Style.messageGroupHead}">
                    <span class="{Style.cls [ Style.avatar; authorAvatar actor ]}"></span>
                    <span class="{whoClass}">{authorName model actor}</span>
                  </header>
                  {members}
                </section>"""
        let items =
            let flush acc = function
                | None -> acc
                | Some (actor, members) -> group actor (List.rev members) :: acc
            let step (acc, current) (actor, rendered) =
                match actor, current with
                | Some actor, Some (actor', members) when actor = actor' ->
                    acc, Some (actor', rendered :: members)
                | Some actor, current -> flush acc current, Some (actor, [ rendered ])
                | None, current -> rendered :: flush acc current, None
            let acc, current = entries |> List.fold step ([], None)
            List.rev (flush acc current)
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
        // History this client's own store does not hold (Plan 20), said ONLY while nothing is
        // coming to fill it. The feed repairs a hole by reading from the cursor the replay
        // parked at, so while this client can read, what is missing is arriving — and a line
        // that appeared on every cold open and vanished a round trip later would be the red
        // "history paused" this replaced, one voice quieter. What is left is the case nobody
        // can fix from here: a client that cannot reach its session, holding a conversation
        // that starts in the middle. The gate is the degradation strip's own — a settled,
        // REASONED disconnection — so a client that has not yet asked `/me` says nothing.
        let missing =
            match model.EventConsumer.MissingBefore, model.Connection, model.EventConsumer.Feed with
            | None, _, _ -> None
            | Some _, Disconnected (Some _), _
            | Some _, _, FeedStalled _ ->
                Some (
                    html $"""
                        <p class="{Style.historyGap}" data-history-gap>
                          <span class="{Style.historyGapText}">{Dom.Text.historyMissingLocally}</span>
                        </p>""")
            | Some _, _, _ -> None
        let body =
            match rows, model.HistoryRead with
            // Nothing here, and this client has not looked yet — which after the local store
            // (Plan 20) is the ordinary cold open. The idle caret would say "nothing was ever
            // said here", so it says the opposite of what is known. A pulse says the true
            // thing: someone is reading. `role="status"` because it is a state, not a mark —
            // a reader that cannot see the pulse is told in words.
            | [], false ->
                [ html $"""<div class="{Style.timelineIdle}" role="status" data-history-loading>
                       <span class="{Style.caretReading}"></span>
                       <span class="{Style.srOnly}">{Dom.Text.readingHistory}</span>
                     </div>""" ]
            // Looked, and there is genuinely nothing: the caret now only ever means what it
            // has always said, which is why it can stay wordless and decorative. Unless what
            // is known is that history is missing — then the timeline is truncated rather
            // than empty, and the line saying so stands where the caret would have.
            | [], true ->
                match missing with
                | Some line -> [ line ]
                | None ->
                    [ html $"""<div class="{Style.timelineIdle}" aria-hidden="true"><span class="{Style.caretIdle}"></span></div>""" ]
            | _ -> Option.toList missing @ items
        html $"""<section class="{Style.timeline}" data-conversation>{body}</section>"""

    /// Everything a block printed, as TEXT — the cheap read of the same bytes the recording
    /// holds, and the one both surfaces that show a block are made of.
    ///
    /// One renderer, because a block opened from the chat must not be a second rendering of a
    /// block, free to drift from the first. What it prints over an EMPTY one is the whole
    /// difference between a command still running, one that printed nothing, and one that
    /// never ran at all — three facts a bare blank would flatten into one.
    let private terminalBlockOutput (feed: TerminalFeed) (block: TerminalBlock) : TemplateResult =
        // A running block's output runs to whatever has arrived; a finished one is bounded
        // by the range its completion event recorded — which is what makes a reload show
        // exactly the same block as the live view did.
        let toSeq = block.ToSeq |> Option.defaultValue (max feed.KnownLength block.FromSeq)
        let output = TerminalFeed.outputText block.FromSeq toSeq feed
        if output <> "" then html $"""<div class="{Style.terminalOutput}" data-terminal-output>{ansiText output}</div>"""
        else
            match block.Status with
            | BlockRunning -> html $"""<div class="{Style.terminalOutputEmpty}" data-terminal-output>…</div>"""
            | BlockFinished _ -> html $"""<div class="{Style.terminalOutputEmpty}" data-terminal-output>no output</div>"""
            // "no output" would be true and useless. A refused command has no output
            // because it never ran, and the reason — when one was given — is the thing
            // the next reader actually wants.
            | BlockRejected (_, reason) ->
                let text = reason |> Option.defaultValue "did not run"
                html $"""<div class="{Style.terminalOutputEmpty}" data-terminal-output>{text}</div>"""

    /// Where a player mounts: an empty host the browser shell attaches one to, keyed by the
    /// tab whose recording it plays (Plan 13, stage 3e; Plan 14, stage 4).
    ///
    /// One function for every mount in the pane — a terminal's, a block's, a stretch's, a
    /// rewound terminal's — because they differ in what they play rather than in how they are
    /// mounted, and a mount that forgot its key would be a recording nothing plays while a
    /// mount that forgot its name would be a region no screen reader can announce.
    let private replayMount (label: string) (tab: PaneTab) : TemplateResult =
        html $"""
            <div class="{Style.paneReadonly}" role="region" aria-label="{label}"
                 data-pane-replay="{PaneTab.key tab}"></div>"""

    /// One block: the command that ran, then everything it printed.
    let private terminalBlockView (model: ClientModel) (feed: TerminalFeed) (block: TerminalBlock) : TemplateResult =
        let body = terminalBlockOutput feed block
        // A command that ran and exited 0 says so by being followed by its output and
        // nothing else — which is what every terminal anyone has used does. `✓ 0` beside
        // every line was the same fact, printed whether or not it was news, on the surface
        // that carries the most lines. What is NEWS keeps its status: running, failed,
        // timed out, refused.
        let notable =
            match block.Status with
            | BlockFinished (CommandSucceeded _) -> Lit.nothing
            | status -> html $"""<span class="shrink-0">{terminalBlockStatus model status}</span>"""
        // Whose command this was, on the same terms: a mark only when the answer is not the
        // obvious one. Your own commands need no attribution in your own terminal — but a
        // command the AGENT ran is the thing a person scanning a scrollback is looking for,
        // and with the facts behind a disclosure there was nothing on the line to say so.
        let author =
            let who = Authority.author block.Authority
            if who = ActorRef.PeerRef model.Peer.PeerId then Lit.nothing
            else
                html $"""
                    <span class="{Style.cls [ Style.avatarSm; authorAvatar who ]}" title="{authorName model who}"
                          data-terminal-block-author="{authorLabel who}"></span>"""
        // The facts that used to have nowhere to go, or nowhere better than a status beside
        // the command: who ran it, who let it through, and how it ended. Behind a
        // disclosure, because a scrollback is read for its OUTPUT and who was behind it is what
        // you go looking for afterwards — and it is a real `<details>`, so going looking is
        // a keypress and an announcement rather than a click handler.
        let fact (text: string) = html $"""<span class="{Style.terminalBlockFact}">{text}</span>"""
        let exitFact =
            match block.Status with
            | BlockFinished (CommandSucceeded code)
            | BlockFinished (CommandFailed code) -> [ fact (sprintf "exit %d" code) ]
            | BlockFinished CommandTimedOut -> [ fact "timed out" ]
            | BlockFinished (CommandExecutionFailed reason) -> [ fact (sprintf "did not run — %s" reason) ]
            | BlockRejected (by, _) -> [ fact (sprintf "refused by %s" (authorName model by)) ]
            | BlockRunning -> []
        let facts =
            [ fact (sprintf "ran by %s" (authorName model (Authority.author block.Authority)))
              yield! exitFact ]
        html $"""
            <article class="{Style.terminalBlock}" data-terminal-block="{BlockId.value block.BlockId}"
                     data-terminal-block-status="{terminalBlockStatusLabel block.Status}">
              <details>
                <summary class="{Style.terminalBlockSummary}">
                  {author}
                  <span class="{Style.terminalPrompt}">$</span>
                  <code class="{Style.terminalCommandText}">{block.Command}</code>
                  {notable}
                  <span class="{Style.terminalBlockMark}" aria-hidden="true">…</span>
                </summary>
                <div class="{Style.terminalBlockFacts}" data-terminal-block-facts>{facts}</div>
              </details>
              {body}
            </article>"""

    /// ONE card for a queued act (Plan 15, stage 3c): what is about to run, editable and
    /// withdrawable while it waits its turn.
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
        let statusToken =
            if ClientModel.awaitsIntegration entry model then Dom.Text.queuedAwaitingIntegration
            elif ClientModel.awaitsTerminal entry model then Dom.Text.queuedAwaitingTerminal
            else Dom.Text.queuedReady
        // A held act is a WAIT, and the pulse dot is the wait — the word only names the
        // blocker (the same status voice every other wait in the product wears). The
        // not-marking banner over a held queue already says what resolves it.
        let statusLine =
            if statusToken = Dom.Text.queuedAwaitingIntegration then
                html $"""<span class="{Style.statusRun}"><span class="{Style.statusDotPulse}"></span>not marking</span>"""
            elif statusToken = Dom.Text.queuedAwaitingTerminal then
                html $"""<span class="{Style.statusRun}"><span class="{Style.statusDotPulse}"></span>terminal busy</span>"""
            else html $"""<span class="{Style.statusOk}">queued</span>"""
        // What this act IS, in words — used both as the chip and as the accessible name on
        // the controls, because a screen reader hearing "Delete" eleven times learns
        // nothing about which one it is on.
        let what =
            TerminalProjection.tryFind entry.Terminal model.Terminals
            |> Option.map (fun view -> view.Title)
            |> Option.defaultValue (TerminalId.value entry.Terminal)
        let subject =
            if not showSubject then Lit.nothing
            else html $"""<span class="{Style.chatChipWho}" data-pending-subject="terminal:{TerminalId.value entry.Terminal}">{what}</span>"""
        // The command line is characters, so it is an input any peer can fix before it runs.
        let body =
            html $"""
                <div class="{Style.terminalQueuedRow}">
                  <span class="{Style.terminalPrompt}">$</span>
                  <input type="text" class="{Style.fieldMonoBare}" aria-label="Queued command"
                         autocapitalize="off" autocorrect="off" autocomplete="off" spellcheck="false"
                         data-terminal-input="{BodyKey.terminalQueued id}">
                </div>"""
        let ordering =
            html $"""
                <button type="button" class="{Style.btnIconBare}" aria-label="Move {what} up" @click={Ev(fun _ -> match TerminalQueueOrder.moveUp model.Synced.Pending id with Some o -> dispatch (ReorderPendingMsg (id, o)) | None -> ())}>{Icon.up}</button>
                <button type="button" class="{Style.btnIconBare}" aria-label="Move {what} down" @click={Ev(fun _ -> match TerminalQueueOrder.moveDown model.Synced.Pending id with Some o -> dispatch (ReorderPendingMsg (id, o)) | None -> ())}>{Icon.down}</button>
                <button type="button" class="{Style.btnIconBareDanger}" aria-label="Delete {what}" data-terminal-queue-delete="{QueueId.value id}" @click={Ev(fun _ -> dispatch (DeletePendingMsg id))}>{Icon.close}</button>"""
        html $"""
            <article class="{Style.terminalQueuedReady}"
                     data-terminal-queued="{QueueId.value id}" data-terminal-queued-status="{statusToken}">
              {body}
              <div class="{Style.terminalQueuedRow}">
                {statusLine}
                {subject}
                <span class="{Style.small}">{authorName model (Authority.author entry.Authority)}</span>
                <div class="ml-auto flex items-center gap-2">
                  {ordering}
                </div>
              </div>
            </article>"""

    /// A terminal's own pending list: the same card, filtered to this terminal, with the
    /// chip off because the heading above it already says which terminal this is.
    let private terminalQueue (actions: ViewActions) (dispatch: ClientMsg -> unit) (model: ClientModel) (terminal: TerminalId) : TemplateResult list =
        ClientModel.terminalQueue terminal model |> List.map (pendingCard actions dispatch model false)

    /// Everything queued, in the chat column, directly under the timeline (Plan 15, stage
    /// 3c). Not INSIDE the timeline: that is a fold over events, and a pending act is not
    /// one — it is the tail, and acts join the timeline when they resolve.
    ///
    /// Terminal commands appear here too, and that is the point rather than a side effect:
    /// reading what the agent is about to run is the same act as reading what it is about
    /// to say, and it should not require having the right panel open.
    let private pendingActs (actions: ViewActions) (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        match ClientModel.pendingActs model with
        | [] -> Lit.nothing
        | acts ->
            let cards = acts |> List.map (pendingCard actions dispatch model true)
            html $"""
                <section class="{Style.queue}" data-pending-acts aria-label="Queued commands">{cards}</section>"""

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
            <div class="{Style.terminalBandRow}" data-terminal-lease="{label}" aria-live="polite">
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
                  <button type="button" class="{Style.btnSendInField}" aria-label="Run"
                          data-terminal-send="{PeerId.value author}"
                          @click={Ev(fun _ -> actions.SendTerminalDraft terminal author)}>{Icon.send}</button>
                </div>"""
        let drafting = ClientModel.terminalDrafts terminal model
        let others = drafting |> List.filter (fun author -> author <> mine)
        // The same fact the message composer's Send reads, asked of the terminal's own slots: a
        // slot is published exactly while its body has content. So Run knows whether it has
        // anything to do without the view measuring the field, and the two cannot disagree.
        let hasCommand = drafting |> List.contains mine
        let runClass = if hasCommand then Style.btnSendInField else Style.btnSendInFieldWaiting
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
                      <div class="{Style.terminalCommandWrap}">
                        <!-- The placeholder is a `$`, and that is the prompt glyph rather than a
                             hint that replaced one: a placeholder sits exactly at the text
                             origin, so it marks where the command will start and the first
                             character typed takes its place. `aria-label` carries the NAME —
                             a placeholder has never been one.

                             A phone keyboard treats a text input as prose unless told
                             otherwise: it capitalises the first word, autocorrects the rest,
                             and underlines what it does not know. A command line is none of
                             those things — `Git` is not `git`, and `ls -la` is not a typo. The
                             four attributes are written out at each command surface rather
                             than composed: lit interpolates VALUES, not attribute names. -->
                        <input type="text" class="{Style.terminalCommand}" aria-label="Command"
                               placeholder="$"
                               autocapitalize="off" autocorrect="off" autocomplete="off" spellcheck="false"
                               data-terminal-input="{BodyKey.terminalDraft terminal mine}">
                        <div class="{Style.terminalCommandTrail}">
                          <span class="{Style.terminalEditors}">{editors mine}</span>
                          <button type="button" class="{runClass}" aria-label="Run" aria-keyshortcuts="Enter"
                                  data-terminal-send="{PeerId.value mine}"
                                  @click={Ev(fun _ -> actions.SendTerminalDraft terminal mine)}>{Icon.send}</button>
                        </div>
                      </div>
                    </div>"""
        // Named, not shown as a stall. The queue is held because a command written here could
        // not be bounded — we would not know when it started or finished — and saying that is
        // the difference between a terminal that looks broken and one that says what to do.
        let lostBanner =
            if not integrationLost then Lit.nothing
            else
                html $"""
                    <div class="{Style.terminalBandRow}" data-terminal-lost="{TerminalId.value terminal}" aria-live="polite">
                      <span class="{Style.statusErr}">not marking</span>
                      <span class="{Style.small}">queued commands held</span>
                      <div class="ml-auto flex items-center gap-2">
                        <button type="button" class="{Style.btnPrimary}" data-terminal-rearm="{TerminalId.value terminal}"
                                @click={Ev(fun _ -> actions.RearmTerminal terminal)}>Re-arm</button>
                      </div>
                    </div>"""
        // What is left here is what this region is FOR: what is waiting to run, and the line
        // you say the next thing on.
        html $"""
            <section class="{Style.terminalComposer}">
              <span class="{Style.bandRail}"></span>
              <span class="{Style.bandEdge}"></span>
              {lostBanner}
              {terminalQueue actions dispatch model terminal}
              {commandLines}
            </section>"""

    /// A CLOSED terminal's band: what the composer's slot says once there is nothing left to
    /// type into it.
    ///
    /// Only what the read above cannot show — why the terminal closed, and whether its
    /// recording survived. The recording itself is no longer HERE: a closed terminal that ran
    /// commands has two reads of one history, and printing both at once put a player of the
    /// same two lines under every command and its result. The blocks are the read; the
    /// recording is where you go (`terminalBody`).
    /// A terminal's one control between its two reads (Plan 14, stage 7; Plan 25, stage 3):
    /// its TEXT — the live screen, or the blocks it ran — and its RECORDING.
    ///
    /// It was four controls with four names: two ways in at the top of the scrollback
    /// (`↑ replay from the start`, `↑ play the recording`) and two ways out floating over it
    /// (`Back to blocks`, `Jump to live`). Each removed another from the document, so each
    /// press had to hand focus on after itself; and each was named after the projection it
    /// mounted rather than after anything a reader wants. One control that relabels in place
    /// is the same act with neither problem — the press keeps its own focus, because the
    /// button it was pressed on is still there saying the other thing.
    ///
    /// A surface with only ONE read offers nothing: a recording that is the only read
    /// (`ReplayIsTheRead`) has no text behind it, and a terminal with nothing recorded has no
    /// recording to go to. Both are rules about the terminal, asked here rather than decided
    /// here.
    let private terminalWatchToggle
        (dispatch: ClientMsg -> unit)
        (model: ClientModel)
        (view: TerminalView)
        : TemplateResult =
        let tab = TerminalTab view.TerminalId
        let feed = ClientModel.terminalFeed view.TerminalId model
        let rewound = ClientModel.isRewound view.TerminalId model
        let playing = ClientModel.playsRecording tab model
        // `None` for the press means the rewind's own message: watching a LIVE terminal is
        // pinning its edge, and the pin is read off the feed there rather than by whoever
        // remembered to look it up first.
        let offer =
            if rewound then Some ("live", "Live", Some (Reading tab))
            elif playing then
                // Back to the text, where there is text to go back to.
                if List.isEmpty view.Blocks then None else Some ("output", "Show output", Some (Reading tab))
            elif view.IsOpen then
                if feed.KnownLength > 0 then Some ("watch", "Watch", None) else None
            elif ClientModel.playable tab model then Some ("watch", "Watch", Some (Watching tab))
            else None
        match offer with
        | None -> Lit.nothing
        | Some (face, label, next) ->
            html $"""
                <button type="button" class="{Style.terminalBarAct}" data-terminal-watch="{face}"
                        @click={Ev(fun _ ->
                                      match next with
                                      | Some mode -> dispatch (ShowInPaneMsg mode)
                                      | None -> dispatch (RewindTerminalMsg view.TerminalId))}>{label}</button>"""

    let private terminalClosedBand (model: ClientModel) (view: TerminalView) : TemplateResult =
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
        // The gap in the audit trail, stated as a status rather than narrated: the drop is
        // recorded so it can be SAID, and the caps-err voice is how this design says a fact
        // that is wrong.
        let notKept =
            if not gone then Lit.nothing
            else
                html $"""
                    <span class="{Style.statusErr}"
                          data-terminal-replay-gone="{TerminalId.value view.TerminalId}">recording not kept</span>"""
        html $"""
            <section class="{Style.terminalComposer}">
              <span class="{Style.bandRail}"></span>
              <div class="{Style.terminalBandRow}">
                <span class="{Style.statusFaint}">{closedFor}</span>
                {notKept}
              </div>
            </section>"""

    /// Arrow-key movement inside the pane's tablist — the half of the ARIA tabs pattern a
    /// plain row of buttons does not give you. Declaring `role="tablist"` and leaving
    /// Left/Right dead would be a worse lie than not declaring it.
    ///
    /// Moves FOCUS only; selection follows the Enter/Space the button already handles. That
    /// is ARIA's "manual activation" variant, and it is the right one here: walking the
    /// strip must not mount and unmount a player under the reader on every keypress.
    [<Fable.Core.Emit("""(function (e) {
  const key = e.key
  if (key !== 'ArrowLeft' && key !== 'ArrowRight' && key !== 'Home' && key !== 'End') return
  const tabs = Array.from(e.currentTarget.querySelectorAll('[role="tab"]'))
  if (tabs.length === 0) return
  const here = tabs.indexOf(document.activeElement)
  const next =
    key === 'Home' ? 0
    : key === 'End' ? tabs.length - 1
    : here < 0 ? 0
    : (here + (key === 'ArrowRight' ? 1 : tabs.length - 1)) % tabs.length
  tabs[next].focus()
  e.preventDefault()
})($0)""")>]
    let private moveTabFocus (e: obj) : unit = Fable.Core.Util.jsNative

    /// Delete/Backspace on a focused tab — the keyboard's unpin (Plan 20, stage 1). Returns
    /// the tab's key, or `""` when this keypress is not that: the strip's other keys are the
    /// arrow walk above, and typing must not unpin anything.
    [<Fable.Core.Emit("""(function (e) {
  if (e.key !== 'Delete' && e.key !== 'Backspace') return ''
  const tab = document.activeElement?.closest('[data-pane-tab]')
  if (!tab) return ''
  e.preventDefault()
  return tab.getAttribute('data-pane-tab')
})($0)""")>]
    let private unpinKeyOn (e: obj) : string = Fable.Core.Util.jsNative

    /// Move focus to the tab that will take the released one's place — BEFORE the release,
    /// which is what makes it need no timing assumption at all.
    ///
    /// Focusing afterwards is the obvious shape and it does not work: the strip has to be
    /// re-rendered first, and when that happens is the renderer's business. Measured on both
    /// attempts — synchronously, focus landed on the node about to be removed and the browser
    /// moved it to `body`; on `requestAnimationFrame`, a headless browser that paints no
    /// frames never ran the callback at all. Going first has neither problem: the neighbour
    /// exists right now, and a node that keeps focus keeps it across the patch.
    [<Fable.Core.Emit("""(function (e) {
  const tabs = Array.from(e.currentTarget.querySelectorAll('[role="tab"]'))
  const here = tabs.indexOf(document.activeElement?.closest('[role="tab"]'))
  if (here < 0 || tabs.length < 2) return
  tabs[Math.min(here, tabs.length - 2)].focus()
})($0)""")>]
    let private focusNeighbourTab (e: obj) : unit = Fable.Core.Util.jsNative

    /// One block's read-only view, as a tab opened from its chip shows it: the command, and
    /// everything it printed, from the chunks this client already has.
    ///
    /// The very same renderer the terminal's own history uses — a block read from the chat
    /// must not be a second rendering of a block, free to drift from the first.
    let private paneBlockView (actions: ViewActions) (dispatch: ClientMsg -> unit) (model: ClientModel) (terminalId: TerminalId) (blockId: BlockId) : TemplateResult =
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
            let tab = BlockTab (terminalId, blockId)
            let playing = ClientModel.playsRecording tab model
            // The reader's OTHER question about this command: not what it printed, which the
            // text above already answers, but what was going on around it. That is a question
            // about POSITION, and the answer is more of the same text — the terminal's own
            // history, scrolled to this command — not a recording of it.
            //
            // It used to be "play whole terminal", which answered a text question with a
            // video, mounted a player twenty seconds of dead air away from the command it
            // named, and left the reader with no way back to the block they stepped out of.
            // Watching from here is still one press away: this moves them, and the toggle
            // below is then the same toggle, at the command they were sent to.
            let showInTerminal =
                if List.isEmpty (TerminalProjection.tryFind terminalId model.Terminals
                                 |> Option.map (fun v -> v.Blocks)
                                 |> Option.defaultValue []) then Lit.nothing
                else
                    html $"""
                        <button type="button" class="{Style.btn}" data-pane-show-in-terminal="{BlockId.value blockId}"
                                @click={Ev(fun _ ->
                                              dispatch (ShowInPaneMsg (ReadingAt (terminalId, blockId)))
                                              actions.RevealBlock terminalId blockId
                                              actions.FocusPane ())}>Show in terminal</button>"""
            // Text, then the recording behind one press — the same rule the terminal's own
            // panel follows, because a block IS the case that made it: a command and its
            // result, printed, needed no player of the same two lines under it.
            //
            // ONE control rather than a pair, so the press that swaps the body leaves focus
            // where it was: it is the same button in the same slot, saying the other thing.
            // Offered only where there is something to play, which for a block means it ran
            // and finished — a refusal never ran, and a recording still being written has no
            // end to replay to.
            let watchToggle =
                if not (ClientModel.playable tab model) then Lit.nothing
                else
                    let face = if playing then "output" else "watch"
                    let label = if playing then "Show output" else "Watch"
                    html $"""
                        <button type="button" class="{Style.btn}" data-pane-watch="{face}"
                                @click={Ev(fun _ ->
                                              dispatch (ShowInPaneMsg (if playing then Reading tab else Watching tab)))}>{label}</button>"""
            // A bordered strip with nothing in it is a control bar that says there are no
            // controls. An open terminal's block has no whole recording to step out into, and
            // a refusal has nothing to play.
            let actionsRow =
                html $"""<div class="{Style.paneActions}">{watchToggle}{showInTerminal}</div>"""
            let body =
                if playing then replayMount "Command output, played" tab
                else
                    let feed = ClientModel.terminalFeed terminalId model
                    html $"""
                        <div class="{Style.paneReadonly}" role="region" aria-label="Command output">
                          {terminalBlockOutput feed block}
                        </div>"""
            html $"""
                <section class="{Style.paneBody}" data-pane-block="{BlockId.value blockId}">
                  <div class="{Style.paneFacts}">
                    <div class="{Style.terminalBlockCommand}">
                      <span class="{Style.terminalPrompt}">$</span>
                      <code class="{Style.terminalCommandText}">{block.Command}</code>
                      <span class="ml-auto shrink-0">{terminalBlockStatus model block.Status}</span>
                    </div>
                  </div>
                  {body}
                  {actionsRow}
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
        // A stretch has no other read: somebody held the keyboard, and what they did is bytes
        // rather than commands. So it plays without being asked, which is what the model says
        // about it (`playsRecording`) rather than something this template decides.
        let player =
            if ClientModel.playsRecording (StretchTab stretch) model
            then replayMount "Session recording" (StretchTab stretch)
            else Lit.nothing
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

    /// The terminal LIST (Plan 20, stage 0): every terminal the session has ever had, and
    /// every verb one of them affords.
    ///
    /// The verbs are rendered from `TerminalAffordances` and from nothing else — a row wears
    /// exactly the controls its terminal's state allows, and a control that does not apply is
    /// ABSENT rather than disabled. That is the same rule the pane's own controls already
    /// followed by hand in three places; here it is one fold, which is what lets a test assert
    /// "kill is never offered over a closed terminal" without building a browser.
    ///
    /// Every state a row can be in is carried by a MARK rather than by a sentence: a pulsing
    /// blue dot is a command running, a peer's own colour is that peer typing, a play outline
    /// is a recording, and the one state with no glyph — a recording the cap ate — is the only
    /// one that says a word, in the voice this design keeps for facts that are wrong.
    let private terminalListView (actions: ViewActions) (dispatch: ClientMsg -> unit) (model: ClientModel) : TemplateResult =
        let row (view: TerminalView) =
            let id = TerminalId.value view.TerminalId
            let affords = ClientModel.affordances view model
            let running = TerminalProjection.runningBlock view |> Option.isSome
            let state =
                if not view.IsOpen then
                    if affords.CanReplay then
                        html $"""<span class="{Style.statusFaint}" title="Recording">{Icon.playSm}</span>"""
                    else
                        // No recording and no glyph for the absence of one: a hole in an audit
                        // trail is stated, in the voice reserved for a fact that is wrong.
                        html $"""<span class="{Style.statusErr}" data-terminal-list-gone="{id}">not kept</span>"""
                elif running then
                    html $"""<span class="{Style.statusRun}"><span class="{Style.statusDotPulse}"></span></span>"""
                else
                    match view.Lease with
                    // Whoever is typing, in their own colour — the same dot the roster and the
                    // tabs wear, so one person is one mark on every surface at once.
                    | Some (PeerRef peer) ->
                        html $"""<span class="{Style.syncDot}" style="background:{PeerColour.ofPeer peer}"
                                       title="{authorName model (PeerRef peer)}"></span>"""
                    | Some holder ->
                        html $"""<span class="{Style.statusRun}" title="{authorName model holder}"><span class="{Style.statusDot}"></span></span>"""
                    | None -> html $"""<span class="{Style.statusFaint}"><span class="{Style.statusDot}"></span></span>"""
            let peers =
                ClientModel.peersInTerminal view.TerminalId model
                |> List.map (fun (peer, name) ->
                    html $"""
                        <span class="{Style.draftEditorDot}" style="background:{PeerColour.ofPeer peer}"
                              title="{name}" data-terminal-tab-peer="{PeerId.value peer}"></span>""")
            let rewind =
                if not affords.CanRewind then Lit.nothing
                else
                    html $"""
                        <button type="button" class="{Style.btnIconBare}" data-terminal-list-rewind="{id}"
                                aria-label="Watch {view.Title} from behind its edge"
                                @click={Ev(fun _ ->
                                              // ONE message. It used to be this and a select
                                              // beside it, and the second cleared the pin the
                                              // first had just taken — a verb that did nothing
                                              // but leave the list. The rewind states the whole
                                              // face now, list included.
                                              dispatch (RewindTerminalMsg view.TerminalId)
                                              actions.FocusPane ())}>{Icon.rewind}</button>"""
            let reattach =
                if not affords.CanReattach then Lit.nothing
                else
                    html $"""
                        <button type="button" class="{Style.btnIconBare}" data-terminal-reattach="{id}"
                                aria-label="Attach {view.Title} again"
                                @click={Ev(fun _ -> actions.ReattachTerminal view.TerminalId)}>{Icon.attach}</button>"""
            let kill =
                if not affords.CanKill then Lit.nothing
                else
                    html $"""
                        <button type="button" class="{Style.btnIconBareDanger}" data-terminal-close="{id}"
                                aria-label="Kill {view.Title}"
                                @click={Ev(fun _ -> actions.CloseTerminal view.TerminalId)}>{Icon.stop}</button>"""
            let nameClass = if view.IsOpen then Style.terminalListName else Style.terminalListNameClosed
            html $"""
                <div class="{Style.terminalListRow}" role="listitem">
                  {state}
                  <span class="min-w-0 flex items-center">
                    <button type="button" class="{nameClass}" data-terminal-list-row="{id}"
                            @click={Ev(fun _ -> dispatch (ShowInPaneMsg (Reading (TerminalTab view.TerminalId))); actions.FocusPane ())}>{view.Title}</button>
                    <span class="{Style.terminalTabPeers}">{peers}</span>
                  </span>
                  <span class="{Style.terminalListVerbs}">{rewind}{reattach}{kill}</span>
                </div>"""
        match ClientModel.terminalRows model with
        | [] ->
            html $"""
                <div class="{Style.terminalListEmpty}" data-terminal-list>
                  <span class="font-terminal text-[28px] leading-8 text-ink-faint select-none" aria-hidden="true">$</span>
                  <button type="button" class="{Style.btnPrimary}" data-terminal-new
                          @click={Ev(fun _ -> actions.OpenTerminal "terminal")}>New terminal</button>
                </div>"""
        | rows ->
            let items = rows |> List.map row
            html $"""
                <div class="{Style.terminalListBody}" data-terminal-list role="list"
                     aria-label="Every terminal in this session">
                  {items}
                </div>"""

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
        let terminalTabButton (activate: unit -> unit) (pinMark: TemplateResult) (pinnedAttr: string) (hint: string) (view: TerminalView) =
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
                            aria-selected="{selectedAttr}" tabindex="{tabIndex}" title="{hint}"
                            data-pane-tab-pinned="{pinnedAttr}"
                            @click={Ev(fun _ -> activate ())}>{view.Title}{pinMark}<span class="{Style.terminalTabPeers}">{peers}</span></button>"""
            else
                html $"""
                    <button type="button" role="tab" class="{klass}" data-pane-tab="{key}" data-terminal-closed-tab="{id}"
                            aria-selected="{selectedAttr}" tabindex="{tabIndex}"
                            @click={Ev(fun _ -> activate ())}>{view.Title}<span class="{Style.small}"> · closed</span><span class="{Style.terminalTabPeers}">{peers}</span></button>"""
        // What a tab is CALLED — read by the tab itself and by the properties bar, which names
        // the selected one. One function, so the strip and the bar can never disagree about
        // what you are looking at.
        let tabLabel (tab: PaneTab) =
            match tab with
            | TerminalTab id ->
                TerminalProjection.tryFind id model.Terminals
                |> Option.map (fun v -> v.Title)
                |> Option.defaultValue (TerminalId.value id)
            | BlockTab (terminalId, blockId) ->
                TerminalProjection.tryFind terminalId model.Terminals
                |> Option.bind (fun v -> v.Blocks |> List.tryFind (fun b -> b.BlockId = blockId))
                |> Option.map (fun b -> b.Command)
                |> Option.defaultValue (BlockId.value blockId)
            | StretchTab stretch -> sprintf "%s · %s" (authorName model stretch.Holder) stretch.Title
        let readonlyTabButton (activate: unit -> unit) (pinMark: TemplateResult) (pinnedAttr: string) (hint: string) (tab: PaneTab) =
            let on = isOn tab
            let label = tabLabel tab
            html $"""
                <button type="button" role="tab" class="{if on then Style.terminalTabActive else Style.terminalTab}"
                        data-pane-tab="{PaneTab.key tab}" title="{hint}"
                        data-pane-tab-pinned="{pinnedAttr}"
                        aria-selected="{if on then "true" else "false"}" tabindex="{if on then "0" else "-1"}"
                        @click={Ev(fun _ -> activate ())}>{label}{pinMark}</button>"""
        /// Activating the tab you are ALREADY on is how a tab gets kept, or released.
        ///
        /// The pin used to be a second button beside every keepable tab. On a touch screen
        /// that is a 24px target beside a 30px one, in a strip that scrolls sideways — and it
        /// was there on every tab whether or not it had anything to say. The gesture needs no
        /// target of its own: a tab already takes a tap, a click and an Enter, and the second
        /// one on the same tab is unambiguous because the first has nothing left to do.
        ///
        /// Offered where a pin would MEAN something: a live terminal, or a recording somebody
        /// opened from the chat. A closed terminal appears here only as the preview — its home
        /// is the list — so pinning one would be kept by nothing, and an act whose effect the
        /// next event undoes is worse than no act.
        let tabButton (tab: PaneTab) =
            let pinnable = PaneTab.isLive model.Terminals tab
            let pinned = pinnable && ClientModel.isPinned tab model
            // One message whichever kind of tab it is: showing a tab is showing a tab, and
            // the two spellings only ever differed in which fields they remembered to clear.
            let select () = dispatch (ShowInPaneMsg (Reading tab))
            let activate () =
                if isOn tab && pinnable then dispatch (TogglePinMsg tab) else select ()
            // The mark says the tab is kept, and only when it is. `role="img"` with a name,
            // because a colour and a glyph are not a fact anything that cannot see them can
            // read — and the state is not on the button itself: a `tab` cannot also be a
            // toggle, so `aria-pressed` here would be two roles arguing.
            let pinMark =
                if not pinned then Lit.nothing
                else html $"""<span class="{Style.paneTabPinMark}" role="img" aria-label="{Dom.Text.pinned}">{Icon.pinSm}</span>"""
            // Said where a pointer can find it, since a gesture with no target has nowhere
            // else to announce itself. Only on the tab it would act on — the selected one.
            let hint =
                if not (isOn tab && pinnable) then ""
                elif pinned then Dom.Text.unpinHint
                else Dom.Text.pinHint
            let pinnedAttr = if not pinnable then "" elif pinned then "true" else "false"
            match tab with
            | TerminalTab id ->
                match TerminalProjection.tryFind id model.Terminals with
                | Some view -> terminalTabButton activate pinMark pinnedAttr hint view
                | None -> Lit.nothing
            | BlockTab _ | StretchTab _ -> readonlyTabButton activate pinMark pinnedAttr hint tab
        let terminalBody (view: TerminalView) =
            let feed = ClientModel.terminalFeed view.TerminalId model
            let truncated =
                if view.DroppedBytes > 0 then
                    html $"""<div class="{Style.terminalTruncated}" data-terminal-truncated="{string view.DroppedBytes}">{view.DroppedBytes} bytes dropped</div>"""
                else Lit.nothing
            let blocks =
                // An idle prompt IS the empty state a terminal-shaped surface already has a
                // symbol for; "nothing has run here yet" was the same fact as a sentence.
                //
                // On an OPEN terminal the command line now carries that prompt itself — the `$`
                // is its placeholder — so drawing a second one above it is one idle prompt too
                // many. On a closed one there is no command line, and the symbol is the only
                // thing left to say the surface is a terminal that ran nothing.
                if not (List.isEmpty view.Blocks) then view.Blocks |> List.map (terminalBlockView model feed)
                elif view.IsOpen then []
                else [ html $"""<div class="{Style.terminalOutputEmpty}"><span class="{Style.terminalPrompt}">$</span></div>""" ]
            // A terminal's two reads, and the ONE control between them (Plan 14, stage 7;
            // Plan 25, stage 3): its text — the live screen, or the blocks it ran — and its
            // recording. On a live terminal watching means going behind the edge while it
            // keeps running, and the way back says `Live`.
            //
            // It was four controls: two ways in at the top of the scrollback and two ways out
            // floating over it, each removing another from the document, each needing focus
            // handed on after it, and each named after the projection it mounted rather than
            // after anything a reader wants. One control that relabels in place is the same
            // act with none of that — and because it never leaves, the press keeps its focus
            // and the reader keeps their place.
            let tab = TerminalTab view.TerminalId
            let rewound = ClientModel.isRewound view.TerminalId model
            let playing = ClientModel.playsRecording tab model
            // How far behind the edge a rewound reader is, in the recording's clock, growing
            // as it moves away from them. A fact rather than a control, so it stays where a
            // reader parked behind live will see it.
            let behindLabel =
                if not (view.IsOpen && rewound) then Lit.nothing
                else
                    let behind =
                        match ClientModel.behindLive view.TerminalId model with
                        | Some seconds when seconds >= 1.0 ->
                            sprintf "behind live — %s" (durationText (System.TimeSpan.FromSeconds seconds))
                        | _ -> "behind live"
                    html $"""
                        <div class="{Style.terminalLiveFloat}">
                          <span class="{Style.statusFaint}" data-terminal-behind="{TerminalId.value view.TerminalId}">{behind}</span>
                        </div>"""
            // In live mode the block history gives way to the SCREEN (Plan 14, stage 6). A
            // program is running here and what it displays is not a list of commands and
            // their output — the blocks are block mode's view of a terminal, and they come
            // back the moment the lease does. The transcript keeps both either way.
            let above =
                if playing then
                    // The recording, played — behind a live edge or after a closed one, the
                    // same mount over the same cast. Which is exactly what "rewound like live
                    // TV, through the same mechanism" has to mean, and the reason a closed
                    // terminal's player is here rather than in a section of its own.
                    let label =
                        if rewound then "Terminal recording, behind live" else "Terminal recording"
                    html $"""
                        <div class="{Style.terminalReplayRegion}">
                          {replayMount label tab}
                          {behindLabel}
                        </div>"""
                else
                    match view.Lease with
                    | Some holder ->
                        // The blocks give way to the screen — and so does their BOX. It used
                        // to stay behind as an empty `flex-1` region holding only the
                        // truncation notice, so a live terminal spent a third of its column
                        // (measured 291px of 844 on a phone) on a container with nothing in
                        // it, and the surface the keyboard actually types into got the same
                        // third. The notice is a line and now renders as one.
                        html $"""
                            {truncated}
                            {terminalScreenView actions model view.TerminalId holder}"""
                    | None ->
                        html $"""
                            <div class="{Style.terminalScrollback}" data-terminal-scrollback
                                 data-terminal-id="{TerminalId.value view.TerminalId}">
                              <div class="{Style.terminalStream}">
                                {truncated}
                                {blocks}
                              </div>
                            </div>"""
            html $"""
                {above}
                {if not view.IsOpen then terminalClosedBand model view
                 // Behind the live edge there is nothing to type into and nothing to queue
                 // against what you are watching: the way back is the bar's `Live`.
                 elif rewound then Lit.nothing
                 else terminalComposer actions dispatch model view.TerminalId}"""
        // A thunk, because the list renders INSTEAD of this: a pane body built on every
        // render while the list is showing would walk a terminal's whole block history to
        // produce markup nothing mounts, on every keystroke and every arriving record.
        let body () =
            match selected with
            // The empty pane wears the terminal's own symbol — an idle prompt, display-sized
            // — and the one button that fills it. What a terminal IS was a paragraph here;
            // the glyph and the verb say it.
            | None ->
                html $"""
                    <div class="{Style.terminalEmpty}">
                      <span class="font-terminal text-[28px] leading-8 text-ink-faint select-none" aria-hidden="true">$</span>
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
                    | BlockTab (terminalId, blockId) -> paneBlockView actions dispatch model terminalId blockId
                    | StretchTab stretch -> paneStretchView model stretch
                // `tabindex="-1"` so the panel can take focus programmatically when a chip
                // opens it, without becoming a Tab stop of its own. A DOM swap that leaves
                // focus on the control that vanished is the failure this exists to avoid.
                html $"""
                    <div class="{Style.paneBody}" role="tabpanel" tabindex="-1"
                         data-pane-panel="{PaneTab.key tab}">
                      {inner}
                    </div>"""
        // The acts that are about the terminal rather than about the command you are
        // writing.
        let properties =
            match selected with
            | Some (TerminalTab id) ->
                match TerminalProjection.tryFind id model.Terminals with
                | None -> Lit.nothing
                | Some view ->
                    // Taking the keyboard changes what this terminal IS, not what the next
                    // command says, so it belongs here rather than over the command line. The
                    // STEAL — taking it from whoever holds it — stays on the lease bar, where
                    // the name of the person you would be taking it from is.
                    let take =
                        if not view.IsOpen || Option.isSome view.Lease then Lit.nothing
                        else
                            html $"""
                                <button type="button" class="{Style.terminalBarAct}" data-terminal-take="{TerminalId.value view.TerminalId}"
                                        @click={Ev(fun _ -> actions.TakeTerminal view.TerminalId)}>take</button>"""
                    // The one control between this terminal's two reads, in one slot whatever
                    // it is doing (Plan 25, stage 3). In the bar rather than in the content
                    // because it is an act about the TERMINAL, and because live mode has no
                    // spatial home for it: what is on screen there is a screen, not a
                    // scrollback, so there is no top of the history to scroll up to.
                    let watch = terminalWatchToggle dispatch model view
                    html $"""<span class="{Style.terminalBarActs}">{take}{watch}</span>"""
            | _ -> Lit.nothing
        // The bar names the SELECTED tab, which is the thing a reader cannot work out for
        // themselves. It used to say "terminals" — the largest text on a phone screen, telling
        // someone looking at terminals that these are terminals.
        let paneName =
            match selected with
            | Some tab -> tabLabel tab
            | None -> "terminals"
        // The strip's kill and its attach-again are GONE (Plan 20, stage 1): both are verbs
        // about a terminal rather than about which tab you are reading, and both now live on
        // that terminal's row in the list, offered from the one fold that decides what a
        // terminal's state allows. What the strip keeps is navigation and the pin — so the
        // strip has become incapable of destroying anything, and a person closing tabs out of
        // habit can no longer kill somebody's build by reflex.
        //
        // The strip and the list are alternatives, never both at once, and that is an ARIA
        // requirement rather than a preference: `role="tablist"` promises a tabpanel showing
        // one of its tabs, and a strip left standing over the list would be promising a panel
        // that is not in the document. One surface at a time; the toggle is how you swap them.
        let strip =
            html $"""
                <div class="{Style.terminalTabs}">
                  <div class="{Style.terminalTabList}" role="tablist" aria-label="Terminals and recordings"
                       @keydown={Ev(fun e ->
                                        moveTabFocus e
                                        // Delete/Backspace unpins what is focused. The index
                                        // is taken BEFORE the dispatch and the focus handed
                                        // back after it, because the tab being released may
                                        // be the one leaving the document.
                                        match unpinKeyOn e with
                                        | "" -> ()
                                        | key ->
                                            tabs
                                            |> List.tryFind (fun tab -> PaneTab.key tab = key)
                                            |> Option.filter (fun tab -> ClientModel.isPinned tab model)
                                            |> Option.iter (fun tab ->
                                                focusNeighbourTab e
                                                dispatch (TogglePinMsg tab)))}>
                    {tabs |> List.map tabButton}
                  </div>
                  <button type="button" class="{Style.terminalTabNew}" data-terminal-new
                          @click={Ev(fun _ -> actions.OpenTerminal "terminal")}>+ new</button>
                </div>"""
        // ONE control with two faces rather than a pair that swap places: it never leaves the
        // document, so pressing it can never strand the focus that is on it — the stranded-focus
        // case the DVR's pair needs `FocusDvr` to answer. Its value is the face it will show,
        // which is the same contract `data-nav-toggle` and `data-settings-toggle` carry.
        let listToggle =
            let showingList = ClientModel.showsList model
            html $"""
                <button type="button" class="{Style.cls [ Style.btnIcon; "w-8 h-8 ml-auto" ]}"
                        data-terminal-list-toggle="{if showingList then "pane" else "list"}"
                        aria-pressed="{if showingList then "true" else "false"}"
                        aria-label="Every terminal in this session"
                        @click={Ev(fun _ -> dispatch ToggleTerminalListMsg)}>{Icon.list}</button>"""
        html $"""
            <aside class="{Style.terminalPanel}" data-terminal-panel>
              <!-- The split, as a real separator: `aria-valuenow` and the arrow keys are what
                   make a splitter reachable without a pointer, and the shell keeps the value
                   in step (`PaneShell.installPaneResize`). -->
              <div class="{Style.terminalResize}" data-term-resize role="separator" tabindex="0"
                   aria-orientation="vertical" aria-label="Resize the terminals column"
                   aria-valuemin="320" aria-valuenow="420" aria-valuemax="1080"></div>
              <div class="{Style.terminalPane}">
                <div class="{Style.terminalHead}">
                  <span class="{Style.terminalHeadName}">{paneName}</span>
                  {properties}
                  {listToggle}
                  <button type="button" class="{Style.navChevronForward}" aria-label="Back to the chat"
                          data-terminal-toggle="hide"
                          @click={Ev(fun _ ->
                                        dispatch ToggleTerminalsMsg
                                        // On a phone this control IS the way back, and it is
                                        // about to leave the screen — so focus goes where the
                                        // reader came from, exactly as closing a tab does.
                                        selected |> Option.iter (PaneTab.key >> actions.FocusChat))}>{Icon.right}</button>
                </div>
                {if ClientModel.showsList model then Lit.nothing else strip}
                {if ClientModel.showsList model then terminalListView actions dispatch model else body ()}
              </div>
            </aside>"""

    /// The client shell, rendered into `#app`.
    let view (actions: ViewActions) (model: ClientModel) (dispatch: ClientMsg -> unit) : TemplateResult =
        html $"""
            {sidebar actions dispatch model}
            <div class="{Style.mainColumn}">
              {header actions dispatch model}
              {degradedBanner model}
              {signInPrompt actions model}
              {chat actions dispatch model}
              {pendingActs actions dispatch model}
              {agentStrip actions model.Agent}
              {queue dispatch model.Synced}
              {drafts actions dispatch model}
            </div>
            {terminals actions dispatch model}"""
