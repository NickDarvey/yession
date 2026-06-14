# Step 02 — Session Process model & conversation projection

> Phase 1 · Process model
> Design context: [docs/design.md](../../design.md) §1 "Reactive", §2.2

## Goal

Establish the Session Process Elmish model and the conversation projection derived purely
by folding the event log. The conversation is a projection of events — never read from
Yjs/draft state.

## Prerequisites

- [Step 00 — Foundations & shared domain types](00-foundations-and-domain-types.md)
- [Step 01 — Append-only event log](01-event-log.md)

## Scope

**In scope**

- The Session Process model holding session id, synced state, event-log state, peers,
  the conversation projection, and agent runtime state.
- A pure fold from `SessionEvent` pages to `ConversationProjection`.
- The synced-state shape (owned/synced via Ylmish in Step 05; defined here as the model).

**Out of scope**

- Encoding synced state into Yjs (Step 05).
- Producing the events the projection consumes (Steps 06, 08).

## Schemas & interfaces introduced

```fsharp
type SyncedSessionState =
    { Drafts      : Map<DraftId, DraftState>
      SharedBrief : SharedBrief option }

and DraftState =
    { DraftId : DraftId
      Author  : PeerId
      Body    : string
      Status  : DraftStatus }

and DraftStatus = Active | Sending | Sent
and SharedBrief = { Body : string }

type ConversationProjection = { Items : ConversationItem list }

and ConversationItem =
    { MessageId : MessageId
      Author    : ActorRef
      Body      : string
      Status    : ConversationItemStatus }

and ConversationItemStatus = Complete | Streaming | Failed

type ProcessModel =
    { SessionId    : SessionId
      Synced       : SyncedSessionState
      EventLog     : EventLogState
      Peers        : Map<PeerId, PeerConnectionState>
      Conversation : ConversationProjection
      Agent        : AgentRuntimeState }

and EventLogState = { LatestOffset : EventOffset option }

and PeerConnectionState =
    { PeerId         : PeerId
      DisplayName    : string
      LastSeenOffset : EventOffset option }

and AgentRuntimeState =
    | Idle
    | Running of AgentTurnId
    | Failed  of AgentTurnId * string
```

Projection contract:

- Folding the same ordered events yields the same projection (deterministic).
- Applying a page that overlaps already-applied offsets does not duplicate items
  (idempotent on offset).
- The projection never consults `SyncedSessionState` / draft bodies.

## Work outcome

- The Session Process holds a single typed model.
- A reusable fold turns event pages into the conversation projection, shared by the
  Process and (later) the client.

## Verification

- Model test: projection is deterministic for a fixed ordered event sequence.
- Model test: re-applying overlapping pages does not duplicate conversation items.
- Model test: projection output is independent of any draft/synced state.

## Done when

- [ ] `ProcessModel` and projection types exist.
- [ ] Pure event-fold projection implemented and shared.
- [ ] Determinism and idempotency tests pass.
