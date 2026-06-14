# Step 06 — Send draft & MessageSent event flow

> Phase 1 · Commands & durable facts
> Design context: [docs/design.md](../../design.md) §1 "Durable facts are events"

## Goal

Turn a collaborative draft into a durable, immutable fact. When a user sends a draft, the
Session Process snapshots the current synced body and appends `MessageSent`; clients see
it via the conversation projection. Sent messages are immutable in Phase 1.

## Prerequisites

- [Step 05 — Ylmish/Yjs collaborative draft sync](05-ylmish-collaborative-draft-sync.md)
- [Step 02 — Session Process model & projection](02-session-process-model-and-projection.md)

## Scope

**In scope**

- Handling the `SendDraft` command on the Session Process.
- Snapshotting the synced draft body at send time.
- Appending `MessageSent` (the Process is the only writer).
- Draft status transitions (`Active` → `Sending` → `Sent`) in synced state.

**Out of scope**

- Client paging/consumption mechanics (Step 07) — this step appends and projects on the
  Process side; client display arrives once Step 07 lands the consumer.
- Agent response (Step 08).

## Schemas & interfaces introduced

```fsharp
// SessionEvent case added this step:
//   | MessageSent of MessageSent
type MessageSent =
    { MessageId : MessageId
      DraftId   : DraftId option
      Author    : ActorRef
      Body      : string }
```

Command flow (per [design.md](../../design.md) §1):

```text
Client sends SendDraft command over WebRTC.
Session Process reads the current synced draft state.
Session Process snapshots the body.
Session Process appends MessageSent to the event log.
Conversation projection updates.
```

Contract:

- The snapshot is taken at send time; later draft edits do not change the sent message.
- `SendDraft` for an unknown/invalid draft returns `CommandRejected`.

## Work outcome

- Sending a draft appends exactly one `MessageSent` with the snapshotted body.
- The conversation projection contains the sent message.
- Continued draft edits after send do not mutate the sent message.

## Verification

- **E2E-2:** sending a draft appends `MessageSent` and updates both clients.
- **E2E-3:** a sent message remains immutable after subsequent draft changes.
- Model test: `SendDraft` on an invalid draft yields `CommandRejected`.

## Done when

- [ ] `SendDraft` handled; `MessageSent` appended with a body snapshot.
- [ ] E2E-2 and E2E-3 pass.
- [ ] Invalid-send rejection test passes.
