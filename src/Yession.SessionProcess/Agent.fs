namespace Yession.SessionProcess

open Yession.Domain

/// Orchestration of one agent turn (Step 08): builds the context pack from the
/// projection-derived conversation, drives the injected `RunAgent` capability, and
/// represents the whole lifecycle — including failure — as events. The Session Process
/// is the only writer; the agent itself never touches the log or the Yjs doc.
module AgentTurn =

    /// The product-authored system prompt (Step 12): the agent distinguishes one-shot
    /// conversation from work that needs an environment, and starts one only then.
    ///
    /// Product-authored, not mechanical: the environment lines carry the lazy-start rule
    /// (design.md §3) into the only place that can honour it at run time. The agent decides
    /// whether a one-shot answer opens a sandbox, and no test can see that decision — the
    /// lazy-lifecycle suite scripts its agent — so trimming these lines removes an invariant
    /// while every gate stays green.
    let systemPrompt =
        "You are participating in a collaborative engineering session. "
        + "Reply to the latest message, using the conversation so far as context. "
        + "Be concise and concrete. "
        + "You may answer conversationally without starting an environment. "
        + "Start an environment only when repository or command execution is needed. "
        + "Use command execution deliberately. "
        + "Prefer high-signal investigation over noisy exploration. "
        + "Commands you queue in a terminal run outside your turn, so their results reach "
        + "you on a later turn as terminal activity rather than as a tool result — read it "
        + "before assuming a queued command did nothing. "
        + "Explain meaningful progress to the session."

    /// Why a turn is running (Plan 20, stage 2). A turn has exactly ONE reason to exist,
    /// which is what makes this a choice rather than two optional arguments free to be both
    /// set or neither.
    ///
    /// Both carry an ACTOR, and it is the same actor in both cases: whoever the turn runs
    /// for. A message names its author; a wake names the party whose earlier turn queued the
    /// work that finished. The agent is the acting party either way and has no scope of its
    /// own (Plan 08), so a turn that could not name one would be a turn with no credentials
    /// — which is why the wake resolves it from the log before it starts, rather than after.
    type TurnTrigger =
        | FromMessage of MessageSent
        | FromWake of WakeReason * ActorRef

    module TurnTrigger =

        let actor =
            function
            | FromMessage message -> message.Author
            | FromWake (_, actor) -> actor

    /// Run one agent turn, appending the lifecycle events:
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
        // Takes the turn's ACTOR as well as its id (Plan 20, stage 2): the credentials a
        // turn runs on are bound per turn, and a woken turn has no message to read them off.
        (capabilitiesFor: AgentTurnId -> ActorRef -> AgentCapabilities)
        // Telemetry (Plan 04): fired with the turn's usage on completion. Injected (default
        // `ignore` off the Host) so this module stays OTel-free. Never throws into the turn.
        (emitUsage: AgentTurnId -> AgentUsage -> unit)
        (mintTurnId: unit -> AgentTurnId)
        (mintMessageId: unit -> MessageId)
        (sessionId: SessionId)
        (conversation: ConversationItem list)
        // What the terminals did since the previous turn (Plan 13, stage 3a). Built by the
        // caller from the same log page the conversation came from, so the two describe
        // the same instant.
        (terminals: TerminalBlockDigest list)
        // Which model this turn runs on, read from the session's collaborative register at
        // the same instant the page above it was (`None` = the provider's own default). A
        // value rather than a thunk, so the whole context pack describes ONE moment: a turn
        // that read its conversation now and its model later could run the answer to one
        // question on the model somebody picked for the next.
        (model: ModelId option)
        (trigger: TurnTrigger)
        : Async<unit> =
        async {
            let turnId = mintTurnId ()
            let append event =
                async {
                    let! _ = log.Append ActorRef.Agent event
                    return ()
                }
            let turnActor = TurnTrigger.actor trigger
            let triggeringMessage =
                match trigger with
                | FromMessage message -> Some message
                | FromWake _ -> None
            do!
                append (
                    AgentTurnStarted
                        { AgentTurnId = turnId
                          TriggeredByMessageId = triggeringMessage |> Option.map (fun m -> m.MessageId)
                          Woke = (match trigger with FromWake (reason, _) -> Some reason | FromMessage _ -> None) })
            try
                // The agent's context is the event-log-derived projection — by
                // construction it can never include Yjs/draft state.
                let currentMessage =
                    triggeringMessage
                    |> Option.map (fun message ->
                        conversation
                        |> List.tryFind (fun item -> item.MessageId = message.MessageId)
                        |> Option.defaultValue
                            { MessageId = message.MessageId
                              Author = message.Author
                              Body = message.Body
                              Status = Complete
                              Kind = ConversationItemKind.Message
                              // Synthesized from the trigger rather than folded, so it has no
                              // offset of its own. Nothing here sorts — the agent's context is
                              // built in the projection's order, and this stands in for an item
                              // that projection has not caught up to yet.
                              Offset = EventOffset.zero
                              // A turn with a triggering message is by definition a turn
                              // somebody asked for, so there is nothing here to explain.
                              Woke = None })
                let context =
                    { SessionId = sessionId
                      Conversation = conversation
                      TurnActor = turnActor
                      CurrentMessage = currentMessage
                      Terminals = terminals
                      Model = model
                      SystemPrompt = systemPrompt }
                do! append (AgentContextBuilt { AgentTurnId = turnId; MessageCount = List.length conversation })

                let messageId = mintMessageId ()
                do! append (AgentMessageStarted { AgentTurnId = turnId; MessageId = messageId })

                let onChunk (chunk: AgentResponseChunk) =
                    if not (signal.IsAborted ()) then
                        Async.StartImmediate (
                            append (AgentMessageDelta { AgentTurnId = turnId; MessageId = messageId; Delta = chunk.Text }))

                let! result = runAgent context (capabilitiesFor turnId turnActor) signal onChunk
                if not (signal.IsAborted ()) then
                    match result with
                    | AgentCompleted (body, usage) ->
                        do! append (AgentMessageCompleted { AgentTurnId = turnId; MessageId = messageId; Body = body })
                        // Telemetry after the durable event: the body is the fact, usage is
                        // observability. `emitUsage` never throws (guarded at the sink).
                        match usage with
                        | Some u -> emitUsage turnId u
                        | None -> ()
                    | AgentFailed reason ->
                        do! append (AgentTurnFailed { AgentTurnId = turnId; Reason = reason })
            with e ->
                if not (signal.IsAborted ()) then
                    do! append (AgentTurnFailed { AgentTurnId = turnId; Reason = e.Message })
        }
