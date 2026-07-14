namespace Yession.SessionProcess

open Yession.Domain

/// Orchestration of one agent turn (Step 08): builds the context pack from the
/// projection-derived conversation, drives the injected `RunAgent` capability, and
/// represents the whole lifecycle — including failure — as events. The Session Process
/// is the only writer; the agent itself never touches the log or the Yjs doc.
module AgentTurn =

    /// The product-authored system prompt (Step 12): the agent distinguishes one-shot
    /// conversation from work that needs an environment, and starts one only then.
    let systemPrompt =
        "You are participating in a collaborative engineering session. "
        + "Reply to the latest message, using the conversation so far as context. "
        + "Be concise and concrete. "
        + "You may answer conversationally without starting an environment. "
        + "Start an environment only when repository or command execution is needed. "
        + "Use command execution deliberately. "
        + "Prefer high-signal investigation over noisy exploration. "
        + "Explain meaningful progress to the session."

    /// Run one agent turn for a human `MessageSent`, appending the lifecycle events:
    ///
    ///   AgentTurnStarted -> AgentContextBuilt -> AgentMessageStarted
    ///     -> AgentMessageDelta* -> AgentMessageCompleted | AgentTurnFailed
    ///
    /// Failures — result-level and thrown — become `AgentTurnFailed`, never exceptions
    /// surfaced to callers. Id minting is injected so tests are deterministic.
    ///
    /// If `signal` fires (Step 17), the Session Process has already appended the
    /// terminal `AgentTurnInterrupted`: from that point this orchestrator appends
    /// nothing more — late chunks are dropped and the runner's eventual result is
    /// discarded. The deltas appended before the interrupt stand as the partial body.
    let run
        (log: EventLog<SessionEvent>)
        (runAgent: RunAgent)
        (signal: AgentAbortSignal)
        (capabilitiesFor: AgentTurnId -> AgentCapabilities)
        (mintTurnId: unit -> AgentTurnId)
        (mintMessageId: unit -> MessageId)
        (sessionId: SessionId)
        (conversation: ConversationItem list)
        (trigger: MessageSent)
        : Async<unit> =
        async {
            let turnId = mintTurnId ()
            let append event =
                async {
                    let! _ = log.Append ActorRef.Agent event
                    return ()
                }
            do! append (AgentTurnStarted { AgentTurnId = turnId; TriggeredByMessageId = trigger.MessageId })
            try
                // The agent's context is the event-log-derived projection — by
                // construction it can never include Yjs/draft state.
                let currentMessage =
                    conversation
                    |> List.tryFind (fun item -> item.MessageId = trigger.MessageId)
                    |> Option.defaultValue
                        { MessageId = trigger.MessageId
                          Author = trigger.Author
                          Body = trigger.Body
                          Status = Complete }
                let context =
                    { SessionId = sessionId
                      Conversation = conversation
                      CurrentMessage = currentMessage
                      SystemPrompt = systemPrompt }
                do! append (AgentContextBuilt { AgentTurnId = turnId; MessageCount = List.length conversation })

                let messageId = mintMessageId ()
                do! append (AgentMessageStarted { AgentTurnId = turnId; MessageId = messageId })

                let onChunk (chunk: AgentResponseChunk) =
                    if not (signal.IsAborted ()) then
                        Async.StartImmediate (
                            append (AgentMessageDelta { AgentTurnId = turnId; MessageId = messageId; Delta = chunk.Text }))

                let! result = runAgent context (capabilitiesFor turnId) signal onChunk
                if not (signal.IsAborted ()) then
                    match result with
                    | AgentCompleted body ->
                        do! append (AgentMessageCompleted { AgentTurnId = turnId; MessageId = messageId; Body = body })
                    | AgentFailed reason ->
                        do! append (AgentTurnFailed { AgentTurnId = turnId; Reason = reason })
            with e ->
                if not (signal.IsAborted ()) then
                    do! append (AgentTurnFailed { AgentTurnId = turnId; Reason = e.Message })
        }
