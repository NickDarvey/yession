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
    | RepoNote

type ConversationItem =
    { MessageId : MessageId
      Author    : ActorRef
      Body      : string
      Status    : ConversationItemStatus
      Kind      : ConversationItemKind }

type ConversationProjection =
    { Items : ConversationItem list
      /// Agent messages currently streaming, by turn — so a turn failure (which carries
      /// only the turn id) can mark its item `Failed`. Projection-internal bookkeeping.
      ActiveAgentMessages : Map<AgentTurnId, MessageId> }

module ConversationProjection =

    let empty : ConversationProjection = { Items = []; ActiveAgentMessages = Map.empty }

    let private updateItem (messageId: MessageId) (f: ConversationItem -> ConversationItem) (items: ConversationItem list) =
        items |> List.map (fun item -> if item.MessageId = messageId then f item else item)

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
                          Kind = ConversationItemKind.Message } ] }
        | AgentTurnStarted _ -> proj   // lifecycle; the item appears at AgentMessageStarted
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
        // Terminals (Plan 13) project into `TerminalProjection`. A command someone ran is
        // not something someone said: it belongs beside its output, in the terminal it ran
        // in, not interleaved with the conversation.
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
                          Kind = ConversationItemKind.RepoNote } ] }
        | SessionEvent.RepoRemoved r ->
            { proj with
                Items =
                    proj.Items
                    @ [ { MessageId = r.MessageId
                          Author = r.Actor
                          Body = sprintf "removed repo %s" (RepoRef.value r.Repo)
                          Status = Complete
                          Kind = ConversationItemKind.RepoNote } ] }
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
                          Kind = ConversationItemKind.RepoNote } ] }
        | AgentMessageStarted a ->
            { Items =
                proj.Items
                @ [ { MessageId = a.MessageId
                      Author = ActorRef.Agent
                      Body = ""
                      Status = Streaming
                      Kind = ConversationItemKind.Message } ]
              ActiveAgentMessages = Map.add a.AgentTurnId a.MessageId proj.ActiveAgentMessages }
        | AgentMessageDelta a ->
            { proj with
                Items =
                    proj.Items
                    |> updateItem a.MessageId (fun item ->
                        if item.Status = Streaming then { item with Body = item.Body + a.Delta } else item) }
        | AgentMessageCompleted a ->
            { Items =
                proj.Items
                |> updateItem a.MessageId (fun item -> { item with Body = a.Body; Status = Complete })
              ActiveAgentMessages = Map.remove a.AgentTurnId proj.ActiveAgentMessages }
        | AgentTurnInterrupted a ->
            match Map.tryFind a.AgentTurnId proj.ActiveAgentMessages with
            | Some messageId ->
                // The streaming item keeps its partial body; the status records the
                // explicit interrupt. Late deltas for it no longer apply (not Streaming).
                { Items = proj.Items |> updateItem messageId (fun item -> { item with Status = Interrupted })
                  ActiveAgentMessages = Map.remove a.AgentTurnId proj.ActiveAgentMessages }
            | None ->
                // Interrupted before any message started: nothing to show — the turn
                // simply never produced an item.
                { proj with ActiveAgentMessages = Map.remove a.AgentTurnId proj.ActiveAgentMessages }
        | AgentTurnFailed a ->
            match Map.tryFind a.AgentTurnId proj.ActiveAgentMessages with
            | Some messageId ->
                // The streaming item keeps whatever partial body it accumulated.
                { Items = proj.Items |> updateItem messageId (fun item -> { item with Status = Failed })
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
                              Kind = ConversationItemKind.Message } ] }

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
