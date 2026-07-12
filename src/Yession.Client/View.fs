namespace Yession.Client

open Yession.Domain

/// Pure rendering of the client shell to HTML. The view is a total function of the model,
/// so it is identical whether produced by the Session Process for the static bootstrap or
/// by the browser as the model updates. Step 04 renders the connection status, the local
/// display name, and the offset / catch-up indicators (offsets are a core product
/// invariant, not a debug detail); the draft editor, send button, conversation timeline,
/// and agent stream are placeholders filled by later steps.
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

    let private draftStatusLabel =
        function
        | Active -> "active"
        | Sending -> "sending"
        | Sent -> "sent"

    /// The synced drafts, rendered in stable (id) order so the markup is deterministic.
    /// Each active draft carries its send button (wired to `SendDraft` by the browser
    /// shell; the markup is the single source of truth either way).
    let private drafts (synced: SyncedSessionState) : string =
        synced.Drafts
        |> Map.toList
        |> List.map (fun (draftId, draft) ->
            let sendButton =
                match draft.Status with
                | Active -> sprintf "<button type=\"button\" data-send-draft=\"%s\">Send</button>" (DraftId.value draftId)
                | Sending | Sent -> ""
            sprintf "<article class=\"draft-item\" data-draft-id=\"%s\" data-draft-author=\"%s\" data-draft-status=\"%s\">%s%s</article>"
                (DraftId.value draftId)
                (PeerId.value draft.Author)
                (draftStatusLabel draft.Status)
                (escapeHtml (Ylmish.Text.toString draft.Body))
                sendButton)
        |> String.concat ""

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

    /// The conversation timeline — rendered from the event projection only, never from
    /// the synced draft state (docs/design.md §1 "Durable facts are events").
    let private conversation (projection: ConversationProjection) : string =
        projection.Items
        |> List.map (fun item ->
            sprintf "<article class=\"message\" data-message-id=\"%s\" data-message-author=\"%s\" data-message-status=\"%s\">%s</article>"
                (MessageId.value item.MessageId)
                (authorLabel item.Author)
                (messageStatusLabel item.Status)
                (escapeHtml item.Body))
        |> String.concat ""

    let private environmentLabel =
        function
        | EnvironmentNotStarted -> "not-started"
        | EnvironmentStarting -> "starting"
        | EnvironmentRunning _ -> "running"
        | EnvironmentFailed _ -> "failed"
        | EnvironmentDown -> "stopped"

    let private agentStream (agent: AgentViewState) : string =
        match agent.ActiveTurn with
        | Some turn -> sprintf "<span data-agent-turn=\"%s\">Agent is responding…</span>" (AgentTurnId.value turn)
        | None -> ""

    /// Render the client shell as an HTML fragment (the contents of `#app`).
    let render (model: ClientModel) : string =
        let consumer = model.EventConsumer
        String.concat "" [
            "<section class=\"connection\">"
            sprintf "<span class=\"status status-%s\" data-connection>%s</span>"
                (connectionLabel model.Connection |> fun s -> s.ToLowerInvariant())
                (connectionLabel model.Connection)
            sprintf "<span class=\"peer\" data-display-name>%s</span>" model.Peer.DisplayName
            "</section>"
            "<section class=\"offsets\">"
            sprintf "<span class=\"offset offset-processed\" data-last-processed-offset>%s</span>"
                (offsetText consumer.LastProcessedOffset)
            sprintf "<span class=\"offset offset-latest\" data-latest-known-offset>%s</span>"
                (offsetText consumer.LatestKnownOffset)
            sprintf "<span class=\"catch-up\" data-catch-up>%s</span>" (catchUpText consumer)
            "</section>"
            // Drafts are the synced collaborative state (Step 05); send lands in Step 06.
            sprintf "<section class=\"draft\" data-draft-editor>%s</section>" (drafts model.Synced)
            sprintf "<section class=\"timeline\" data-conversation>%s</section>" (conversation model.Conversation)
            sprintf "<section class=\"agent\" data-agent-stream>%s</section>" (agentStream model.Agent)
            sprintf "<section class=\"environment\" data-environment=\"%s\"></section>" (environmentLabel model.Environment)
        ]

    /// Render a full HTML document hosting the client shell. Used by the Session Process
    /// static bootstrap so the served page *is* the client shell.
    let page (model: ClientModel) : string =
        String.concat "" [
            "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">"
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
            "<title>Yession</title></head><body>"
            sprintf "<main id=\"app\">%s</main>" (render model)
            "</body></html>"
        ]
