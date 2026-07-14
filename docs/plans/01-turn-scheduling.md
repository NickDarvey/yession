# Plan 01 — Agent turn scheduling: queue by default, interrupt on request

> Phase 3 · Turn discipline
> Addresses the two paired gaps from [GAPS.md § Agent](../GAPS.md): **overlapping
> turns** ("one agent turn at a time is assumed, not enforced — rapid successive human
> messages start overlapping turns") and the **undecided queueing/cancellation policy**.
> One design resolves both.

## Product behaviour (the contract)

Cursor-style defaults:

1. **One turn in flight, ever.** A human `MessageSent` while the agent is idle starts a
   turn immediately (today's behaviour).
2. **Messages queue by default.** A human `MessageSent` while a turn is running does
   *not* start a turn and does *not* cancel anything. It is durable immediately (it is
   an event) and visibly *queued* in the UI.
3. **The queue coalesces into one follow-up turn.** When the running turn finishes
   (completed, failed, or interrupted), all queued messages become the context of
   exactly **one** next turn — never one turn per queued message. The newest queued
   message is the turn's trigger; the rest arrive as conversation context (they already
   do — the context pack is the projection).
4. **Interrupt is explicit.** The user can push a queued message through by
   interrupting: the running turn is cancelled *now*, its partial response is preserved
   and marked interrupted (a fact, not an error), and the queued messages start their
   coalesced turn immediately.
5. **Interrupting an idle agent is a no-op rejection**, not an error state.

Non-goals (unchanged gaps): context windowing/summarisation, multi-agent turns,
per-message priority. Interrupt cancels the *response*; any commands the turn already
ran remain in the log — history is append-only.

## Design

### 1. Scheduling is Session Process state, not client state

A new `TurnScheduler` in `Yession.SessionProcess` owns single-flight discipline. The
host's `MessageSent` trigger routes through it instead of `Async.StartImmediate` per
message.

```fsharp
// Yession.SessionProcess/Scheduler.fs
type ScheduledTurn =
    { TurnId : AgentTurnId
      Abort : unit -> unit }              // idempotent; wired to the runner's abort seam

type TurnSchedulerState =
    | SchedulerIdle
    | TurnRunning of ScheduledTurn * queued: MessageSent list   // newest last

type TurnScheduler =
    { /// A human message arrived: start a turn now (idle) or queue it (running).
      OnHumanMessage : MessageSent -> unit
      /// Interrupt the running turn (validated against its id); queued messages
      /// start their coalesced turn immediately after the abort completes.
      Interrupt : AgentTurnId -> Result<unit, string>
      /// Observability for tests/UI plumbing.
      State : unit -> TurnSchedulerState }
```

The scheduler is deterministic and engine-free: it is given `startTurn :
MessageSent list (*queued context*) -> MessageSent (*trigger*) -> Abortable`, so unit
tests drive it with scripted turns whose completion the *test* resolves — no timing.

### 2. Cancellation is a capability seam on `RunAgent`

`RunAgent` gains an abort signal, mirroring how chunks already flow:

```fsharp
type AgentAbortSignal =
    { IsAborted : unit -> bool
      /// Register a callback fired at most once, immediately if already aborted.
      OnAbort : (unit -> unit) -> unit }

type RunAgent =
    AgentContextPack -> AgentCapabilities -> AgentAbortSignal
        -> (AgentResponseChunk -> unit) -> Async<AgentRunResult>
```

- **Scripted runners** (deterministic suite) check/subscribe explicitly — tests can
  hold a turn open until told, then assert the abort arrived.
- **The SDK adapter** wires it to an `AbortController` passed into `query({ options:
  { abortController } })`; on abort the adapter resolves `AgentFailed`? No — see the
  result contract below: the adapter returns a distinct `AgentInterrupted`.
- A runner that ignores the signal is still bounded: the orchestrator treats the abort
  as authoritative after firing it (the late result is discarded — the turn's events
  are already closed by `AgentTurnInterrupted`, and a closed turn accepts no further
  appends; enforce with a per-turn `closed` flag in `AgentTurn.run`).

```fsharp
type AgentRunResult =
    | AgentCompleted of body: string
    | AgentFailed of reason: string
    | AgentInterrupted                    // new: cancelled on request, not an error
```

### 3. Interruption is a command, and its outcome is events

Protocol (additive, wire-compatible):

```fsharp
// SessionCommand gains:
| InterruptAgentTurn of AgentTurnId

// SessionEvent gains:
| AgentTurnInterrupted of AgentTurnInterrupted
and AgentTurnInterrupted =
    { AgentTurnId : AgentTurnId
      RequestedBy : PeerId }
```

- `SessionCommands.handle` validates: the named turn must be the one currently running
  (scheduler lookup, injected like `readSynced`) — otherwise `CommandRejected
  "no such running turn"`. Any session peer may interrupt (collaborative session; the
  requester is recorded on the event).
- `AgentTurn.run` on abort: appends `AgentTurnInterrupted` (never `AgentTurnFailed`) and
  stops; the streamed partial body stays.
- Turn coalescing metadata: `AgentContextBuilt` already records `MessageCount`; add
  nothing. `AgentTurnStarted.TriggeredByMessageId` remains the newest queued message
  (the trigger), keeping the wire format unchanged.

### 4. Projection & client model

- `ConversationItemStatus` gains `Interrupted`. `AgentTurnInterrupted` marks the turn's
  streaming item `Interrupted` (partial body kept); a pre-message interruption produces
  no item (unlike failure — nothing went wrong).
- **Queued indicator is derived, not stored**: a human message item is "queued" iff its
  offset is greater than the running turn's trigger offset and no later
  `AgentTurnStarted` covers it. Add `ConversationProjection.pendingFor :
  projection -> AgentTurnId option -> MessageId list` (pure), and fold
  `QueuedMessages : MessageId list` into `AgentViewState` from event pages the same way
  `ActiveTurn` is folded today. No new events needed for queueing — the log already
  contains the truth.
- View: the agent section renders, when a turn is active,
  `data-agent-turn` (exists), plus `data-agent-queued="<n>"` and an
  `<button data-interrupt-turn="<turnId>">Interrupt</button>`; queued message items get
  `data-message-queued`. The browser shell delegates `data-interrupt-turn` clicks to a
  new `Connection.InterruptTurn` (same request/response correlation as `SendDraft`,
  reusing the pending-request map; response dispatches
  `TurnInterruptAcceptedMsg`/`TurnInterruptRejectedMsg` — UI-advisory only, the truth
  arrives as events).

### 5. What deliberately does NOT change

- The event log stays append-only; interrupts never rewrite history.
- The agent still cannot re-trigger itself: only `HumanPeer` messages reach the
  scheduler.
- Draft/send flow, environments, and commands are untouched; a turn interrupted
  mid-command lets the command finish (commands are short-lived and their events are
  already streaming; killing processes mid-flight is a later refinement — note in
  GAPS.md).

## Delivery steps

| # | Step | Outcome | Verification |
|---|------|---------|--------------|
| 15 | Abort seam & `AgentInterrupted` | `RunAgent` carries `AgentAbortSignal`; `AgentTurn.run` closes turns exactly once (late results discarded); `AgentTurnInterrupted` event + codec + projection `Interrupted` status | Unit: scripted runner held open, abort fires callback, events end with `AgentTurnInterrupted`, partial body kept, late completion discarded; wire round-trip for the new case |
| 16 | `TurnScheduler` single-flight + coalescing | Rapid messages produce one running + queued; completion starts exactly one coalesced follow-up turn | Deterministic scheduler tests (test-resolved turns): burst of 3 sends → 2 turns total; queue order preserved; failure/interrupt also drain the queue |
| 17 | Interrupt command + UI | `InterruptAgentTurn` validated against the running turn; browser interrupt button + queued indicators | Integration: interrupt while running → `AgentTurnInterrupted` + next turn starts; interrupt while idle / wrong id → `CommandRejected`; E2E: two sends during a held turn show `data-agent-queued="2"`, click interrupt → coalesced turn answers both; browser E2E extends the Playwright script |
| 18 | Live SDK abort + acceptance | SDK adapter honours `AbortController`; suite-wide gate | Credential-gated: interrupt a real turn mid-stream → `AgentTurnInterrupted`, then the queued turn completes; 5 deterministic runs green; tracker records Phase 3 acceptance |

Each step is one commit with green `mise run test`, per the tracker's rules; GAPS.md's
agent section is updated at the end (overlap/queue/cancel gaps close; "interrupt does
not kill in-flight commands" is added).

## Risks & open questions

- **SDK abort fidelity**: `AbortController` should stop billing/streaming promptly, but
  the adapter must tolerate both "abort ⇒ rejected promise" and "abort ⇒ result message
  with an error subtype" — both map to `AgentInterrupted` when the signal fired first.
- **Interrupt races completion**: the scheduler validates the turn id, but the turn may
  complete between the click and the command. Resolution: the command is rejected
  ("turn already finished") and the queued messages have already started their turn —
  the UI just catches up from events. No special casing.
- **Queued-message editing**: a queued message is already sent (immutable). If users
  want to retract/edit queued messages Cursor-style, that is a *new* feature
  (message retraction events) — explicitly out of scope here.
