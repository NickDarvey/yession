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

    /// The synced drafts, rendered in stable (id) order so the markup is deterministic.
    /// Every draft is editable and sendable — sending moves it into the shared queue
    /// (the shell wires the button to `SendDraft`, which enqueues; Phase 3).
    let private drafts (synced: SyncedSessionState) : string =
        synced.Drafts
        |> Map.toList
        |> List.map (fun (draftId, draft) ->
            sprintf "<article class=\"draft-item\" data-draft-id=\"%s\" data-draft-author=\"%s\"><textarea data-draft-input=\"%s\">%s</textarea><button type=\"button\" data-send-draft=\"%s\">Send</button></article>"
                (DraftId.value draftId)
                (PeerId.value draft.Author)
                (DraftId.value draftId)
                (escapeHtml (Ylmish.Text.toString draft.Body))
                (DraftId.value draftId))
        |> String.concat ""
        |> fun items -> "<button type=\"button\" data-start-draft>Start draft</button>" + items

    /// The shared message queue (Phase 3), in consumption order. Every entry stays
    /// editable, reorderable (one fractional-index write per move), and deletable by
    /// any peer until the Session Process drains it into the timeline.
    let private queue (synced: SyncedSessionState) : string =
        QueueOrder.sorted synced.Queue
        |> List.map (fun entry ->
            let id = QueueId.value entry.QueueId
            String.concat "" [
                sprintf "<article class=\"queue-item\" data-queue-id=\"%s\" data-queue-author=\"%s\" data-queue-order=\"%s\">"
                    id (PeerId.value entry.Author) (string entry.Order)
                sprintf "<textarea data-queue-input=\"%s\">%s</textarea>" id (escapeHtml (Ylmish.Text.toString entry.Body))
                sprintf "<button type=\"button\" data-queue-up=\"%s\">Up</button>" id
                sprintf "<button type=\"button\" data-queue-down=\"%s\">Down</button>" id
                sprintf "<button type=\"button\" data-queue-delete=\"%s\">Delete</button>" id
                "</article>"
            ])
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
        | ConversationItemStatus.Interrupted -> "interrupted"

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

    let private commandStatusLabel =
        function
        | CommandPending -> "pending"
        | CommandRunning -> "running"
        | CommandFinished (CommandSucceeded code) -> sprintf "succeeded:%d" code
        | CommandFinished (CommandFailed code) -> sprintf "failed:%d" code
        | CommandFinished CommandTimedOut -> "timed-out"
        | CommandFinished (CommandExecutionFailed _) -> "execution-failed"

    /// The read-only command log — derived from events only; there is no input surface.
    let private commandLog (log: CommandLog) : string =
        log.Entries
        |> List.map (fun entry ->
            let output =
                entry.Output
                |> List.map (fun (stream, text) ->
                    sprintf "<pre data-stream=\"%s\">%s</pre>"
                        (match stream with Stdout -> "stdout" | Stderr -> "stderr")
                        (escapeHtml text))
                |> String.concat ""
            sprintf "<article class=\"command\" data-command-id=\"%s\" data-command-status=\"%s\"><code>%s %s</code>%s</article>"
                (CommandId.value entry.CommandId)
                (commandStatusLabel entry.Status)
                (escapeHtml entry.Executable)
                (escapeHtml (String.concat " " entry.Arguments))
                output)
        |> String.concat ""

    let private agentStream (agent: AgentViewState) : string =
        match agent.ActiveTurn with
        | Some turn ->
            // The explicit interrupt (Phase 3): cancel the running turn and drain the
            // queue immediately. The shell wires the button to `InterruptTurn`.
            sprintf "<span data-agent-turn=\"%s\">Agent is responding…</span><button type=\"button\" data-interrupt-turn=\"%s\">Interrupt</button>"
                (AgentTurnId.value turn)
                (AgentTurnId.value turn)
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
            // Drafts and the message queue are the synced collaborative state; the
            // queue drains into the timeline when the agent is idle (Phase 3).
            sprintf "<section class=\"draft\" data-draft-editor>%s</section>" (drafts model.Synced)
            sprintf "<section class=\"queue\" data-message-queue>%s</section>" (queue model.Synced)
            sprintf "<section class=\"timeline\" data-conversation>%s</section>" (conversation model.Conversation)
            sprintf "<section class=\"agent\" data-agent-stream>%s</section>" (agentStream model.Agent)
            sprintf "<section class=\"environment\" data-environment=\"%s\"></section>" (environmentLabel model.Environment)
            sprintf "<section class=\"commands\" data-command-log>%s</section>" (commandLog model.Commands)
        ]

    /// Render a full HTML document hosting the client shell. Used by the Session Process
    /// static bootstrap so the served page *is* the client shell.
    let page (model: ClientModel) : string =
        String.concat "" [
            "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">"
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
            "<title>Yession</title></head><body>"
            sprintf "<main id=\"app\">%s</main>" (render model)
            "<script type=\"module\" src=\"/client.js\"></script>"
            "</body></html>"
        ]
