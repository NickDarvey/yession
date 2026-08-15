namespace Yession.Domain

/// The conversation is a *projection* of the event log — never read from Yjs/draft state.
/// The projection type and its fold live in the shared Domain library because both the
/// Session Process and the Browser Client derive the conversation the same way.
/// See docs/design.md §1 "Reactive", §2.2 and docs/plans/00-init/02-*.

type ConversationItemStatus =
    | Complete
    | Streaming
    | Failed
    /// The turn was explicitly interrupted; the partial body streamed so far is kept.
    | Interrupted

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
    | ActNote

type ConversationItem =
    { MessageId : MessageId
      Author    : ActorRef
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
        // Terminals (Plan 13) project into `TerminalProjection`, and STILL do not fold here
        // (Plan 14, stage 1). This projection is what builds the agent's context, and the
        // agent already receives block outcomes through `TerminalDigest` — folding them in
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
                          Body = sprintf "added repo %s (branch %s)" (RepoRef.value r.Repo) r.Branch
                          Status = Complete
                          Kind = ConversationItemKind.ActNote
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
                          Kind = ConversationItemKind.ActNote
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
                          Kind = ConversationItemKind.ActNote
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
                | [], _ -> ""
                | names, Some owner ->
                    sprintf ", forwarding %s from %s" (String.concat ", " names) (ActorRef.token owner)
                | names, None -> sprintf ", forwarding %s" (String.concat ", " names)
            { proj with
                Items =
                    proj.Items
                    @ [ { MessageId = s.MessageId
                          Author = s.Actor
                          Body = sprintf "started sandbox %s (%s)%s" (SandboxName.value s.Sandbox) s.Backend forwarded
                          Status = Complete
                          Kind = ConversationItemKind.ActNote
                          Offset = envelope.Offset
                          Woke = None } ] }
        | SessionEvent.WorkSandboxStopped s ->
            { proj with
                Items =
                    proj.Items
                    @ [ { MessageId = s.MessageId
                          Author = s.Actor
                          Body = sprintf "stopped sandbox %s" (SandboxName.value s.Sandbox)
                          Status = Complete
                          Kind = ConversationItemKind.ActNote
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
                          Body =
                            match c.Reason with
                            | Some reason -> sprintf "refused %s — %s" c.Summary reason
                            | None -> sprintf "refused %s" c.Summary
                          Status = Complete
                          Kind = ConversationItemKind.ActNote
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
                          Kind = ConversationItemKind.ActNote
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
                          Kind = ConversationItemKind.ActNote
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
            match Map.tryFind a.AgentTurnId proj.ActiveAgentMessages with
            | Some messageId ->
                // The streaming item keeps whatever partial body it accumulated.
                { proj with
                    Items = proj.Items |> updateItem messageId (fun item -> { item with Status = Failed })
                    ActiveAgentMessages = Map.remove a.AgentTurnId proj.ActiveAgentMessages }
            | None ->
                // The turn failed before its message started: the failure still shows in
                // the conversation, under an id derived deterministically from the turn.
                let messageId =
                    match MessageId.create (sprintf "agent-turn-%s-failed" (AgentTurnId.value a.AgentTurnId)) with
                    | Ok id -> id
                    | Error e -> failwithf "derived message id invariant violated: %s" e
                { proj with
                    Items =
                        proj.Items
                        @ [ { MessageId = messageId
                              Author = ActorRef.Agent
                              Body = a.Reason
                              Status = Failed
                              Kind = ConversationItemKind.Message
                              Offset = envelope.Offset
                              Woke = wokeBy a.AgentTurnId proj } ] }

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
