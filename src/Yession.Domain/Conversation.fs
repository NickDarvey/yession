namespace Yession.Domain.Chat

open Yession.Domain
open Yession.Domain.Agent
open Yession.Domain.Prs

/// The conversation is a *projection* of the event log — never read from Yjs/draft state.
/// The projection type and its fold live in the shared Domain library because both the
/// Session Process and the Browser Client derive the conversation the same way.
/// See docs/design.md §1 "Reactive" and §2.2.

type ConversationItemStatus =
    | Complete
    | Streaming
    | Failed
    /// The turn was explicitly interrupted; the partial body streamed so far is kept.
    | Interrupted

/// What an act has to say beyond its headline. The fold KNOWS which half of a sentence is
/// the gist and which is the particulars — it built both from an event whose shape it
/// matched — so the split is made here rather than by a renderer hunting for a punctuation
/// mark in finished prose. A view that split on an em-dash would be re-parsing its own copy,
/// and every rewording would silently move the seam.
///
/// `None` is an act that is already one clause. Most are: "removed repo octo/hello" has no
/// second half to withhold, and inventing one would pad every short line into looking like a
/// long one.
type ActNoteFacts =
    { Detail : string option
      /// Whether this act is a landmark BY NATURE — worth a stroke on the rail without
      /// anybody having marked it.
      ///
      /// It is the fold's to say, for the same reason `Detail` is: the fold matched the
      /// event, and a renderer deciding this would be deciding it by reading the finished
      /// sentence. What is notable is deliberately a short list — a rail that marks
      /// everything marks nothing — and `Landmarks` is where a person's own verdict
      /// overrides it in either direction.
      Notable : bool }

/// What an item in the timeline IS (Plan 14). A message is something someone said; a
/// repo note is something someone DID (added/removed/switched a repo), folded into the
/// same ordered list so humans see it where it happened and the agent's context —
/// built from this projection — carries the same history. Distinguished by a field
/// rather than by author or body convention, so a renderer can style a note without
/// parsing anything.
[<RequireQualifiedAccess>]
type ConversationItemKind =
    | Message
    /// Something a party DID, rather than said: a repo added, a sandbox started. Named
    /// for the category rather than for repos (Plan 15) because every command the agent
    /// gains lands here — the timeline is how a human sees what was done on their behalf,
    /// and a kind per capability would be a renderer per capability.
    ///
    /// It carries what only an ACT can have: a message is one body with no particulars to
    /// hold back, so the facts ride the case rather than the item — and a message cannot be
    /// written carrying a detail it could never show.
    | ActNote of ActNoteFacts

type ConversationItem =
    { MessageId : MessageId
      Author    : ActorRef
      /// What a message said — and, on an act note, only its HEADLINE: the particulars are
      /// in `ActNoteFacts.Detail` beside it. A reader that is not a screen wants both, and
      /// `ConversationItem.said` is the one that gives both. See it for why.
      Body      : string
      Status    : ConversationItemStatus
      Kind      : ConversationItemKind
      /// The offset of the event that CREATED this item — the message that was sent, or the
      /// agent message that started (Plan 14, stage 1). Deltas and completions move the body
      /// and the status; they never move the item, so a streaming answer holds its place in
      /// the order exactly as a running command's chip does.
      ///
      /// Carried so the view can interleave this with terminal work in one timeline. Both are
      /// folds of the SAME ordered log, which makes merging them a sort rather than a clock
      /// reconciliation — and this field is the only thing that was missing.
      Offset    : EventOffset
      /// Why the turn that produced this item exists, when nobody asked for it (Plan 20,
      /// stage 2). `None` on everything a person said and on every turn a person triggered.
      ///
      /// On the ITEM rather than looked up from the turn, because the timeline renders items
      /// and an item does not know its turn. It rides here for the same reason `Offset` does:
      /// the fold knows something the view needs and cannot re-derive.
      Woke      : WakeReason option }

module ConversationItem =

    /// Everything this item says, headline and particulars, as one sentence.
    ///
    /// The split exists for a SCREEN: an eye needs a gist to land on, and a paragraph with no
    /// gist is a paragraph nobody reads. A reader that is not a screen — the agent's prompt,
    /// a digest, a log line — has no such need and must never be handed the headline alone,
    /// because the half a headline leaves out is the half that says which credential went
    /// into the sandbox, why the declaration was refused, and what the checkout is asking
    /// for. That is the half somebody is being asked to decide about.
    ///
    /// It lives here rather than in each of those readers for the ordinary reason: a rule
    /// about how an act's two halves compose is a rule about the act, and a caller that had
    /// to remember to ask for the second half is a caller that will one day not.
    let said (item: ConversationItem) : string =
        match item.Kind with
        | ConversationItemKind.ActNote { Detail = Some detail } -> item.Body + " — " + detail
        | ConversationItemKind.ActNote _
        | ConversationItemKind.Message -> item.Body

/// Which items in a conversation wear a mark on the rail.
///
/// Two sources, and the order between them is the whole design. Some acts are landmarks by
/// NATURE — a pull request's news is one, because a watch is the reason somebody is waiting
/// — and nobody should have to mark those by hand. But a default nobody can refuse becomes
/// noise the first time it is wrong, so a person's own verdict, recorded per message, wins
/// over it in either direction: it can take a mark off an act that wears one, and put one on
/// anything else that was said.
///
/// The verdict is stored, not the difference from the default. What is notable by nature is a
/// rule this repository will change, and a stored difference would silently flip every
/// message somebody had already decided about the moment it did.
module Landmarks =

    /// Whether this item is marked, as the rail draws it.
    let marked (verdicts: Map<MessageId, bool>) (item: ConversationItem) : bool =
        match verdicts |> Map.tryFind item.MessageId with
        | Some said -> said
        | None ->
            match item.Kind with
            | ConversationItemKind.ActNote facts -> facts.Notable
            | ConversationItemKind.Message -> false

    /// Mark this item, or unmark it.
    ///
    /// ONE verb, and it takes the item rather than the answer. A caller that read the current
    /// state and wrote the opposite would be a caller holding the only copy of the rule about
    /// what an unmarked-by-nature act defaults to — and the second caller has not read it.
    let toggle (item: ConversationItem) (verdicts: Map<MessageId, bool>) : Map<MessageId, bool> =
        verdicts |> Map.add item.MessageId (not (marked verdicts item))

    /// The marked items of a conversation, in the order the conversation holds them.
    let over (verdicts: Map<MessageId, bool>) (items: ConversationItem list) : ConversationItem list =
        items |> List.filter (marked verdicts)

type ConversationProjection =
    { Items : ConversationItem list
      /// Agent messages currently streaming, by turn — so a turn failure (which carries
      /// only the turn id) can mark its item `Failed`. Projection-internal bookkeeping.
      ActiveAgentMessages : Map<AgentTurnId, MessageId>
      /// The turn nobody asked for, while it is the current one (Plan 20, stage 2) — so the
      /// items it goes on to produce can say why they exist. Projection-internal bookkeeping.
      ///
      /// One turn rather than a map, because turns are serial: the scheduler runs one at a
      /// time, and `AgentWake.pending` folds on that same fact — it resets at every
      /// `AgentTurnStarted`. So does this, which is also what keeps it from growing: an
      /// ordinary turn clears it, and there is no turn-completed event that could.
      WokenTurn : (AgentTurnId * WakeReason) option }

module ConversationProjection =

    let empty : ConversationProjection =
        { Items = []; ActiveAgentMessages = Map.empty; WokenTurn = None }

    let private updateItem (messageId: MessageId) (f: ConversationItem -> ConversationItem) (items: ConversationItem list) =
        items |> List.map (fun item -> if item.MessageId = messageId then f item else item)

    /// A failed turn's item body: whatever it managed to say, and why it stopped. Both, and
    /// never only the first — a failure a reader cannot name is a failure they re-run to
    /// diagnose. Separated by a blank line so the model's own prose stays distinguishable
    /// from the machine's account of what happened to it.
    ///
    /// Only ever called with a body that HAS prose in it: a turn that said nothing gets the
    /// reason as an item of its own, at the offset it stopped at, rather than a body here.
    let private withReason (body: string) (reason: string) : string =
        let said = reason.Trim ()
        if said = "" then body else body + "\n\n" + said

    /// Why the given turn exists, if nobody asked for it. Matched on the turn id rather than
    /// taken on trust: a late event from a turn the wake did not start must not inherit the
    /// current one's reason.
    let private wokeBy (turnId: AgentTurnId) (proj: ConversationProjection) : WakeReason option =
        match proj.WokenTurn with
        | Some (woken, reason) when woken = turnId -> Some reason
        | _ -> None

    /// Fold one event into the projection. The match is total over `SessionEvent`, so
    /// adding a case forces this projection to account for it.
    let private applyEvent (proj: ConversationProjection) (envelope: EventEnvelope<SessionEvent>) : ConversationProjection =
        match envelope.Event with
        | SessionCreated _ -> proj // session lifecycle, not a conversation item
        | PeerJoined _ -> proj     // presence, not a conversation item
        | PeerLeft _ -> proj       // presence, not a conversation item
        | MessageSent m ->
            { proj with
                Items =
                    proj.Items
                    @ [ { MessageId = m.MessageId
                          Author = m.Author
                          Body = m.Body
                          Status = Complete
                          Kind = ConversationItemKind.Message
                          Offset = envelope.Offset
                          Woke = None } ] }
        // Lifecycle; the item appears at `AgentMessageStarted`. What is remembered here is
        // only the turn's REASON for existing, which that item cannot re-derive: by the time
        // it arrives, the event that carried the reason is pages behind it.
        | AgentTurnStarted a ->
            { proj with WokenTurn = a.Woke |> Option.map (fun reason -> a.AgentTurnId, reason) }
        | AgentContextBuilt _ -> proj  // lifecycle
        // Environment lifecycle (Step 12) is session state, not conversation content.
        | EnvironmentNeedIdentified _
        | EnvironmentStartRequested _
        | EnvironmentStarted _
        | EnvironmentStartFailed _
        | EnvironmentStopRequested _
        | EnvironmentStopped _ -> proj
        // Command lifecycle (Step 13) projects into the command log, not the conversation.
        | CommandRequested _
        | CommandStarted _
        | CommandOutputReceived _
        | CommandCompleted _ -> proj
        // Terminals (Plan 13) project into `Projection`, and STILL do not fold here
        // (Plan 14, stage 1). This projection is what builds the agent's context, and the
        // agent already receives block outcomes through `Digest` — folding them in
        // here would double-feed the model and silently change what every turn reads.
        //
        // What Plan 14 reverses is the SCREEN, not the fold: a command someone ran does
        // appear in the chat now, interleaved by offset in `TimelineProjection`, which is a
        // view-level merge of this projection with the terminal one. The consequence is
        // deliberate and stated there — the human's chat and the agent's chat diverge.
        | SessionEvent.TerminalOpened _
        | SessionEvent.TerminalClosed _
        | SessionEvent.TerminalBlockStarted _
        | SessionEvent.TerminalBlockCompleted _
        | SessionEvent.TerminalLeaseTaken _
        | SessionEvent.TerminalLeaseReleased _
        | SessionEvent.TerminalCommandRejected _
        | SessionEvent.TerminalIntegrationLost _
        | SessionEvent.TerminalIntegrationRestored _
        | SessionEvent.TerminalTranscriptTruncated _ -> proj
        // Tool use (Plan 16, part C) does not fold here either, and for the same hazard in
        // a sharper form: the agent MADE the call and already has the result in its own
        // transcript, so feeding it back would be pure duplication. It folds into
        // `TimelineProjection` — the screen — and nowhere else.
        | SessionEvent.ToolUseStarted _
        | SessionEvent.ToolUseFinished _ -> proj
        // Repos (Plan 14) DO fold into the timeline — unlike terminals, a repo change is
        // a session-shaping act ("we are now working on X, on branch Y") that reads like
        // a sentence, carries no output stream, and is exactly what a joining human or
        // the agent's next turn needs to know. Each note rides the Process-minted
        // MessageId its event carries.
        | SessionEvent.RepoAdded r ->
            { proj with
                Items =
                    proj.Items
                    @ [ { MessageId = r.MessageId
                          Author = r.Actor
                          Body = sprintf "added repo %s" (RepoRef.value r.Repo)
                          Status = Complete
                          Kind =
                            ConversationItemKind.ActNote
                                { Detail = Some (sprintf "on branch %s" r.Branch); Notable = false }
                          Offset = envelope.Offset
                          Woke = None } ] }
        | SessionEvent.RepoRemoved r ->
            { proj with
                Items =
                    proj.Items
                    @ [ { MessageId = r.MessageId
                          Author = r.Actor
                          Body = sprintf "removed repo %s" (RepoRef.value r.Repo)
                          Status = Complete
                          Kind = ConversationItemKind.ActNote { Detail = None; Notable = false }
                          Offset = envelope.Offset
                          Woke = None } ] }
        | SessionEvent.RepoBranchSwitched r ->
            { proj with
                Items =
                    proj.Items
                    @ [ { MessageId = r.MessageId
                          Author = r.Actor
                          Body =
                            if r.Created then sprintf "created branch %s in %s" r.Branch (RepoRef.value r.Repo)
                            else sprintf "switched %s to branch %s" (RepoRef.value r.Repo) r.Branch
                          Status = Complete
                          Kind = ConversationItemKind.ActNote { Detail = None; Notable = false }
                          Offset = envelope.Offset
                          Woke = None } ] }
        // Named WorkSandboxes (Plan 15, stage 2) fold in for the repo notes' reason and
        // one more: forwarding a credential into a sandbox is the most consequential thing
        // a command here does, and the timeline is where the person whose credential it is
        // finds out. The line names WHAT was forwarded and WHOSE — never a value; the
        // event cannot carry one.
        | SessionEvent.WorkSandboxStarted s ->
            let forwarded =
                match s.Forwarded, s.CredentialOwner with
                | [], _ -> None
                | names, Some owner ->
                    Some (sprintf "forwarding %s from %s" (String.concat ", " names) (ActorRef.token owner))
                | names, None -> Some (sprintf "forwarding %s" (String.concat ", " names))
            // And where this host could not give what the sandbox's resources named. On the
            // start NOTE rather than a note of its own, because it is a property of THIS
            // sandbox coming up — a separate item would be a second thing to correlate, and
            // the correlation is the whole content of it.
            let realisation =
                match s.Realisation with
                | [] -> None
                | lines ->
                    Some (
                        sprintf
                            "where this host could not give exactly what was asked: %s"
                            (String.concat "; " lines))
            { proj with
                Items =
                    proj.Items
                    @ [ { MessageId = s.MessageId
                          Author = s.Actor
                          Body = sprintf "started sandbox %s (%s)" (SandboxRef.render s.Sandbox) s.Backend
                          Status = Complete
                          Kind =
                            ConversationItemKind.ActNote
                                { Detail =
                                    match List.choose id [ forwarded; realisation ] with
                                    | [] -> None
                                    | parts -> Some (String.concat ". " parts)
                                  Notable = false }
                          Offset = envelope.Offset
                          Woke = None } ] }
        // The other outcome of a declaration, beside the start above. Said in the refusal's
        // own words rather than summarised: the `repo_config` query is showing that same
        // sentence, and two renderings of one refusal are two things free to disagree.
        // What a repo asks for, when it changed. A person reading the timeline sees the whole
        // set rather than the diff: a diff answers "what moved", and the question somebody
        // actually has to answer is "is THIS the access I am content for this checkout to
        // have" — which needs the whole of it.
        | SessionEvent.RepoCapabilitiesChanged c ->
            { proj with
                Items =
                    proj.Items
                    @ [ { MessageId = c.MessageId
                          Author = c.Actor
                          Body =
                            match c.Granted with
                            | [] -> "asks for nothing"
                            | [ one ] -> sprintf "asks for %s" one
                            | granted -> sprintf "asks for %d capabilities" (List.length granted)
                          Status = Complete
                          // The whole set, never a count on its own: the detail is rendered
                          // beside the headline rather than behind a disclosure, so what a
                          // person has to decide about is still on the screen.
                          Kind =
                            ConversationItemKind.ActNote
                                { Detail =
                                    match c.Granted with
                                    | []
                                    | [ _ ] -> None
                                    | granted -> Some (String.concat "; " granted)
                                  Notable = false }
                          Offset = envelope.Offset
                          Woke = None } ] }
        | SessionEvent.RepoCapabilitiesApproved a ->
            { proj with
                Items =
                    proj.Items
                    @ [ { MessageId = a.MessageId
                          Author = a.Actor
                          Body = sprintf "approved what %s asks for" (RepoRef.value a.Repo)
                          Status = Complete
                          Kind = ConversationItemKind.ActNote { Detail = None; Notable = false }
                          Offset = envelope.Offset
                          Woke = None } ] }
        | SessionEvent.RepoConfigRefused r ->
            { proj with
                Items =
                    proj.Items
                    @ [ { MessageId = r.MessageId
                          Author = r.Actor
                          Body =
                            match r.Sandbox with
                            | Some sandbox -> sprintf "could not start sandbox %s" (SandboxRef.render sandbox)
                            // The file itself. Its reason already names the repo and the
                            // path inside the file, so anything in front of it would be a
                            // second copy of what it says — which is also why it is the
                            // headline here and not the detail under one.
                            | None -> r.Reason
                          Status = Complete
                          Kind =
                            ConversationItemKind.ActNote
                                { Detail = r.Sandbox |> Option.map (fun _ -> r.Reason); Notable = false }
                          Offset = envelope.Offset
                          Woke = None } ] }
        | SessionEvent.WorkSandboxStopped s ->
            { proj with
                Items =
                    proj.Items
                    @ [ { MessageId = s.MessageId
                          Author = s.Actor
                          Body = sprintf "stopped sandbox %s" (SandboxRef.render s.Sandbox)
                          Status = Complete
                          Kind = ConversationItemKind.ActNote { Detail = None; Notable = false }
                          Offset = envelope.Offset
                          Woke = None } ] }
        // Where new terminals start (Plan 25) folds in for the repo notes' reason: it is a
        // session-shaping act everyone is affected by — the next terminal a PERSON opens
        // lands there too — and the timeline is the only place they would learn it.
        | SessionEvent.ShellProfileSet p ->
            { proj with
                Items =
                    proj.Items
                    @ [ { MessageId = p.MessageId
                          Author = p.Actor
                          Body =
                            match p.WorkingDirectory with
                            | Some cwd ->
                                sprintf "new terminals in %s start in %s" (SandboxRef.render p.Sandbox) cwd
                            | None ->
                                sprintf
                                    "new terminals in %s start where the sandbox puts them"
                                    (SandboxRef.render p.Sandbox)
                          Status = Complete
                          Kind = ConversationItemKind.ActNote { Detail = None; Notable = false }
                          Offset = envelope.Offset
                          Woke = None } ] }
        // A refusal reads in the timeline beside the acts that happened, attributed to the
        // person who said no rather than to the agent that asked (Plan 15, stage 3). Same
        // reason `BlockRejected` renders in the terminal: an act that simply vanishes is
        // indistinguishable from a bug.
        | SessionEvent.CommandRefused c ->
            { proj with
                Items =
                    proj.Items
                    @ [ { MessageId = c.MessageId
                          Author = c.RejectedBy
                          Body = sprintf "refused %s" c.Summary
                          Status = Complete
                          Kind = ConversationItemKind.ActNote { Detail = c.Reason; Notable = false }
                          Offset = envelope.Offset
                          Woke = None } ] }
        // The MCP set changing (Plan 17). `ActorRef.System`, because nobody in the session
        // did it, and the DELTA only — the Process compares what it was last told, from
        // its own events, against the newly resolved set, so a boot, a reconnect and a
        // restart all emit nothing and only a genuine change by the operator is loud.
        | SessionEvent.McpServerAvailable m ->
            { proj with
                Items =
                    proj.Items
                    @ [ { MessageId = m.MessageId
                          Author = ActorRef.System
                          Body = sprintf "you can now use the %s tools" (McpServerName.value m.Name)
                          Status = Complete
                          Kind = ConversationItemKind.ActNote { Detail = None; Notable = false }
                          Offset = envelope.Offset
                          Woke = None } ] }
        | SessionEvent.McpServerUnavailable m ->
            { proj with
                Items =
                    proj.Items
                    @ [ { MessageId = m.MessageId
                          Author = ActorRef.System
                          Body = sprintf "the %s tools are no longer available" (McpServerName.value m.Name)
                          Status = Complete
                          Kind = ConversationItemKind.ActNote { Detail = None; Notable = false }
                          Offset = envelope.Offset
                          Woke = None } ] }
        // Watched pull requests fold in for the repo notes' reason: a watch is a
        // session-shaping act, and a transition is exactly what a joining human or the
        // agent's next turn needs to be told — the news arrived through no other door.
        | SessionEvent.PrWatched p ->
            { proj with
                Items =
                    proj.Items
                    @ [ { MessageId = p.MessageId
                          Author = p.Actor
                          Body = sprintf "PR %s watched" (PrRef.render p.Pr)
                          Status = Complete
                          Kind =
                            ConversationItemKind.ActNote
                                { Detail =
                                    Some (
                                        sprintf
                                            "%s, %s"
                                            (PrState.describe p.Initial.State)
                                            (ChecksRollup.describe p.Initial.Checks))
                                  // Where the waiting began. A landmark by nature, like the
                                  // news that follows it — and unlike the unwatch below,
                                  // which is where the story stops being told rather than a
                                  // place worth coming back to.
                                  Notable = true }
                          Offset = envelope.Offset
                          Woke = None } ] }
        | SessionEvent.PrUnwatched p ->
            { proj with
                Items =
                    proj.Items
                    @ [ { MessageId = p.MessageId
                          Author = p.Actor
                          Body = sprintf "PR %s unwatched" (PrRef.render p.Pr)
                          Status = Complete
                          Kind = ConversationItemKind.ActNote { Detail = None; Notable = false }
                          Offset = envelope.Offset
                          Woke = None } ] }
        // Attributed to the WATCHER rather than the envelope's System: the person whose
        // watch noticed is who the news is for, and whose name it should wear.
        | SessionEvent.PrTransitioned p ->
            { proj with
                Items =
                    proj.Items
                    @ [ { MessageId = p.MessageId
                          Author = p.Watcher
                          Body = sprintf "PR %s %s" (PrRef.render p.Pr) (PrTransition.describe p.Transition)
                          Status = Complete
                          Kind = ConversationItemKind.ActNote { Detail = None; Notable = true }
                          Offset = envelope.Offset
                          Woke = None } ] }
        | AgentMessageStarted a ->
            { proj with
                Items =
                    proj.Items
                    @ [ { MessageId = a.MessageId
                          Author = ActorRef.Agent
                          Body = ""
                          Status = Streaming
                          Kind = ConversationItemKind.Message
                          Offset = envelope.Offset
                          Woke = wokeBy a.AgentTurnId proj } ]
                ActiveAgentMessages = Map.add a.AgentTurnId a.MessageId proj.ActiveAgentMessages }
        | AgentMessageDelta a ->
            { proj with
                Items =
                    proj.Items
                    |> updateItem a.MessageId (fun item ->
                        if item.Status = Streaming then { item with Body = item.Body + a.Delta } else item) }
        | AgentMessageCompleted a ->
            { proj with
                Items =
                    proj.Items
                    |> updateItem a.MessageId (fun item -> { item with Body = a.Body; Status = Complete })
                ActiveAgentMessages = Map.remove a.AgentTurnId proj.ActiveAgentMessages }
        | AgentTurnInterrupted a ->
            match Map.tryFind a.AgentTurnId proj.ActiveAgentMessages with
            | Some messageId ->
                // The streaming item keeps its partial body; the status records the
                // explicit interrupt. Late deltas for it no longer apply (not Streaming).
                { proj with
                    Items = proj.Items |> updateItem messageId (fun item -> { item with Status = Interrupted })
                    ActiveAgentMessages = Map.remove a.AgentTurnId proj.ActiveAgentMessages }
            | None ->
                // Interrupted before any message started: nothing to show — the turn
                // simply never produced an item.
                { proj with ActiveAgentMessages = Map.remove a.AgentTurnId proj.ActiveAgentMessages }
        | AgentTurnFailed a ->
            // Why a turn stopped is an item of its own, ANCHORED WHERE IT STOPPED — unless
            // the turn had already said something, in which case the reason joins what it
            // said and stays with it.
            //
            // The distinction is the whole point, because an agent message is created when
            // the turn STARTS, before the model has spoken and before a single tool call.
            // A tool-only turn — which is most of them — therefore holds an empty item at
            // the top of its own work, and filling that with the reason filed the account of
            // a failure a hundred and forty rows above the thing that failed: directly under
            // the message that asked for it, over every command it had run. A reader saw a
            // turn open with its own obituary. So an item that never said anything is not
            // where this belongs, and it is dropped rather than left standing empty.
            let reasonItem () =
                let messageId =
                    match MessageId.create (sprintf "agent-turn-%s-failed" (AgentTurnId.value a.AgentTurnId)) with
                    | Ok id -> id
                    | Error e -> failwithf "derived message id invariant violated: %s" e
                { MessageId = messageId
                  Author = ActorRef.Agent
                  Body = a.Reason
                  Status = Failed
                  Kind = ConversationItemKind.Message
                  Offset = envelope.Offset
                  Woke = wokeBy a.AgentTurnId proj }
            let closed = Map.remove a.AgentTurnId proj.ActiveAgentMessages
            let spoke =
                Map.tryFind a.AgentTurnId proj.ActiveAgentMessages
                |> Option.bind (fun messageId ->
                    proj.Items
                    |> List.tryFind (fun item -> item.MessageId = messageId)
                    |> Option.map (fun item -> messageId, item.Body.Trim () <> ""))
            match spoke with
            | Some (messageId, true) ->
                { proj with
                    Items =
                        proj.Items
                        |> updateItem messageId (fun item ->
                            { item with Body = withReason item.Body a.Reason; Status = Failed })
                    ActiveAgentMessages = closed }
            | Some (messageId, false) ->
                { proj with
                    Items =
                        (proj.Items |> List.filter (fun item -> item.MessageId <> messageId))
                        @ [ reasonItem () ]
                    ActiveAgentMessages = closed }
            | None ->
                // The turn failed before its message started: same item, same derivation —
                // there was simply never a placeholder to drop.
                { proj with Items = proj.Items @ [ reasonItem () ]; ActiveAgentMessages = closed }

    /// Fold ordered event envelopes into a conversation projection.
    ///
    /// `appliedThrough` is the highest offset already folded in; events at or below it are
    /// skipped, so re-applying overlapping pages is idempotent on offset. Returns the
    /// updated projection together with the new high-water offset.
    ///
    /// The signature deliberately takes only events — never synced/draft state — so the
    /// conversation can never depend on collaborative editing state.
    let applyEvents
        (appliedThrough: EventOffset option)
        (events: EventEnvelope<SessionEvent> list)
        (projection: ConversationProjection)
        : ConversationProjection * EventOffset option =
        events
        |> List.fold
            (fun (proj, highWater) envelope ->
                let beyondApplied =
                    match highWater with
                    | Some o -> EventOffset.value envelope.Offset > EventOffset.value o
                    | None -> true
                if beyondApplied then
                    applyEvent proj envelope, Some envelope.Offset
                else
                    proj, highWater)
            (projection, appliedThrough)

/// What a person in this session still has to decide about.
///
/// Folded from the events by BOTH sides — the Process to know what to gate, a client to know
/// what to offer — so the prompt somebody sees and the sandbox that is waiting are two
/// readings of one log rather than two answers that can disagree.
///
/// A repo is pending when the last thing it was recorded as asking for is sensitive, and no
/// approval since names exactly that set. "Exactly" is the whole rule: a repo that widens
/// what it asks for is a new decision, not one the old yes silently covers.
module RepoApprovals =

    /// What each repo was last recorded as asking for, and whether anybody still has to
    /// decide about it. A fold state rather than a function over the whole log, because a
    /// client sees the log in PAGES and re-reading all of it per page is the cost this
    /// projection exists to avoid.
    type Pending = private Pending of Map<string, RepoRef * string list * bool>

    let empty : Pending = Pending Map.empty

    let apply (Pending state) (events: SessionEvent list) : Pending =
        events
        |> List.fold
            (fun state event ->
                match event with
                | SessionEvent.RepoCapabilitiesChanged c ->
                    Map.add (RepoRef.value c.Repo) (c.Repo, c.Granted, c.Sensitive) state
                | SessionEvent.RepoCapabilitiesApproved a ->
                    match Map.tryFind (RepoRef.value a.Repo) state with
                    // Approval settles the set it NAMES. An approval of something else leaves
                    // the ask standing, which is what makes a widening a fresh decision rather
                    // than one an old yes silently covers.
                    | Some (repo, granted, _) when granted = a.Granted ->
                        Map.add (RepoRef.value a.Repo) (repo, granted, false) state
                    | _ -> state
                | _ -> state)
            state
        |> Pending

    /// Who is still waiting on somebody, in a stable order.
    let waiting (Pending state) : (RepoRef * string list) list =
        state
        |> Map.toList
        |> List.choose (fun (_, (repo, granted, pending)) -> if pending then Some (repo, granted) else None)
        |> List.sortBy (fun (repo, _) -> RepoRef.value repo)

    /// The whole log at once — the Process's reading, where there are no pages.
    let pending (events: SessionEvent list) : (RepoRef * string list) list = apply empty events |> waiting
