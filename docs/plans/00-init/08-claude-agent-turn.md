# Step 08 — Claude Code SDK agent turn

> Phase 1 · Agent
> Design context: [docs/design.md](../../design.md) §1 "Reactive", §2.1

## Goal

After a `MessageSent`, the Session Process runs a real agent turn using the Claude Code
SDK. The agent has no tools and no environment access in Phase 1; its context is the
event-log-derived conversation projection. The agent's response is represented entirely
as events and streamed to clients.

## Prerequisites

- [Step 06 — Send draft & MessageSent event flow](06-send-draft-and-message-events.md)
- [Step 07 — Client event consumption by offset](07-client-event-consumption.md)
- [Step 02 — Session Process model & projection](02-session-process-model-and-projection.md)

## Scope

**In scope**

- Building an `AgentContextPack` from the conversation projection.
- Running the Claude Code SDK and streaming chunks.
- Emitting agent lifecycle as events: turn started, context built, message started,
  deltas, completed, or failed.
- Driving `AgentRuntimeState` (`Idle`/`Running`/`Failed`) and the client `AgentViewState`.

**Out of scope**

- Any tools, command execution, or environment (Phase 2).

## Schemas & interfaces introduced

```fsharp
type AgentContextPack =
    { SessionId      : SessionId
      Conversation   : ConversationItem list
      CurrentMessage : ConversationItem
      SystemPrompt   : string }

type AgentResponseChunk = { Text : string }

type AgentRun =
    { Chunks     : AsyncObservable<AgentResponseChunk>
      Completion : Async<AgentRunResult> }

type RunAgent = context: AgentContextPack -> Async<AgentRun>

and AgentRunResult =
    | Completed of body: string
    | Failed    of reason: string

// SessionEvent cases added this step:
type AgentTurnStarted    = { AgentTurnId : AgentTurnId; TriggeredByMessageId : MessageId }
type AgentContextBuilt   = { AgentTurnId : AgentTurnId; MessageCount : int }
type AgentMessageStarted = { AgentTurnId : AgentTurnId; MessageId : MessageId }
type AgentMessageDelta   = { AgentTurnId : AgentTurnId; MessageId : MessageId; Delta : string }
type AgentMessageCompleted = { AgentTurnId : AgentTurnId; MessageId : MessageId; Body : string }
type AgentTurnFailed     = { AgentTurnId : AgentTurnId; Reason : string }
```

Contract:

- A `MessageSent` from a human triggers exactly one agent turn.
- Streaming deltas project as a `Streaming` conversation item; completion flips it to
  `Complete`.
- Any agent error produces `AgentTurnFailed` and a `Failed` conversation item.
- The agent reads only the projection-derived context, never Yjs/draft state.

## Work outcome

- Sending a message yields a real, streamed agent response built from events.
- The conversation shows the streaming response and its final state.
- Failures are represented as events, not exceptions surfaced to the client.

## Verification

- **E2E-5:** a real agent response is appended as an event stream and rendered.
- Model test: an agent failure produces `AgentTurnFailed`.
- Model test: the streamed response projects deterministically (deltas → completed).

## Done when

- [ ] `RunAgent` integrates the Claude Code SDK and streams chunks.
- [ ] Agent lifecycle events drive the projection and runtime state.
- [ ] E2E-5 and the failure test pass.
