# Step 07 — Client event consumption by offset

> Phase 1 · Event replication
> Design context: [docs/design.md](../../design.md) §1 "Durable facts are events", §2.2

## Goal

Let clients build their conversation by consuming read-only event pages by offset, track
their own read progress, and catch up after reconnecting. Event pages are the source of
truth; "events available" notifications are hints only. Clients cannot append events.

## Prerequisites

- [Step 06 — Send draft & MessageSent event flow](06-send-draft-and-message-events.md)
- [Step 03 — WebRTC transport & frame protocol](03-webrtc-transport-and-frames.md)

## Scope

**In scope**

- Client request/response handling for `ReadEventsAfter` / `EventsPage`.
- Client offset tracking (`LastProcessedOffset`, `LatestKnownOffset`, `IsCatchingUp`).
- Applying pages through the shared projection (from [Step 02](02-session-process-model-and-projection.md)).
- Reacting to `EventsAvailable` hints by requesting pages.
- Reconnect → catch-up from the last processed offset.
- Enforcing that clients are read-only consumers (no client-side append path exists).

**Out of scope**

- Agent events (Step 08) — consumption is generic over `SessionEvent`, so agent events
  flow once they exist.

## Schemas & interfaces introduced

No new domain types. Uses `EventConsumerState`
([Step 04](04-web-app-bootstrap-and-client-shell.md)) and the `EventLogFrame`
([Step 03](03-webrtc-transport-and-frames.md)).

Consumption loop:

```text
client has offset N
client requests events after N (ReadEventsAfter)
Session Process returns a page (EventsPage)
client applies events via the shared projection
client stores latest processed offset
```

Contract:

- Applying a duplicate/overlapping page does not duplicate conversation items.
- `LatestKnownOffset` follows `EventsAvailable` hints and page metadata.
- There is no protocol path for a client to append an event.

## Work outcome

- The client conversation timeline is built entirely from event pages.
- Offset displays update as pages are applied.
- A client that disconnects, misses events, and reconnects catches up to current.

## Verification

- **E2E-4:** client disconnects, events continue, client reconnects and catches up by
  offset.
- **E2E-6:** a client cannot append events directly (no path exists / attempts rejected).
- **E2E-7:** the conversation renders only from the event projection, not from Yjs draft
  state.
- Model test: duplicate event pages do not duplicate conversation items.

## Done when

- [ ] Offset-based paged consumption + catch-up implemented.
- [ ] E2E-4, E2E-6, E2E-7 pass.
- [ ] Duplicate-page idempotency test passes.
