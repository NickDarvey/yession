# Step 01 — Append-only event log

> Phase 1 · Event log
> Design context: [docs/design.md](../../design.md) §1 "Durable facts are events"

## Goal

Provide the Session Process-owned, append-only event log: the single source of durable
session facts. Phase 1 may store events in memory, but the API must not assume in-memory
storage.

## Prerequisites

- [Step 00 — Foundations & shared domain types](00-foundations-and-domain-types.md)

## Scope

**In scope**

- Append behaviour that assigns a monotonic offset and stamps the envelope.
- Paged, offset-based reads.
- An interface that hides the storage implementation.

**Out of scope**

- Transport of pages to clients (Step 07).
- Who is allowed to append (enforced at the transport boundary in Step 07); the log
  itself is only ever invoked by the Session Process.

## Schemas & interfaces introduced

```fsharp
type EventPage<'event> =
    { Events     : EventEnvelope<'event> list
      LastOffset : EventOffset option
      IsEnd      : bool }

type AppendResult =
    { Offset : EventOffset }

type AppendEvent<'event> =
    actor: ActorRef ->
    event: 'event ->
    Async<AppendResult>

type ReadEvents<'event> =
    after: EventOffset option ->
    AsyncSeq<EventPage<'event>>
```

Behavioural contract:

- Offsets are strictly monotonically increasing across all appends.
- `ReadEvents (Some n)` returns only events with offset greater than `n`.
- `ReadEvents None` returns from the beginning.
- Paging is deterministic: identical inputs against identical log state return identical
  pages.

## Work outcome

- The Session Process can append a `SessionEvent` and receive its assigned offset.
- The Session Process can read events as deterministic pages from any offset.
- Storage is replaceable without changing callers.

## Verification

- Model test: offsets are monotonic across interleaved appends.
- Model test: reads return deterministic pages for a fixed log and inputs.
- Model test: `after` correctly excludes already-seen offsets and `IsEnd` is accurate at
  the tail.

## Done when

- [ ] `AppendEvent` and `ReadEvents` implemented behind an interface.
- [ ] Monotonic-offset and deterministic-paging tests pass.
- [ ] No caller depends on the in-memory representation.
