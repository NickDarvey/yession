# Step 05 — Ylmish/Yjs collaborative draft sync

> Phase 1 · Collaboration
> Design context: [docs/design.md](../../design.md) §1 "Ylmish as the sync boundary"

## Goal

Make the draft message collaborative through the Ylmish/Yjs sync boundary. Two clients
editing the same draft converge. Application code reasons in typed Elmish state; Yjs is
only the encoding/transport of selected collaborative state.

## Prerequisites

- [Step 02 — Session Process model & projection](02-session-process-model-and-projection.md)
- [Step 04 — Web app bootstrap & client shell](04-web-app-bootstrap-and-client-shell.md)

## Scope

**In scope**

- A Ylmish codec that encodes selected Elmish state (`SyncedSessionState`) into the Yjs
  document and decodes synced Yjs state back into Elmish state.
- Draft creation and collaborative editing of the draft body.
- The Session Process owning/hosting the Yjs document; clients syncing over the `State`
  frame defined in [Step 03](03-webrtc-transport-and-frames.md).
- `DraftStarted` event payload appended when a draft is started.

**Out of scope**

- Sending a draft / `MessageSent` (Step 06).
- Conversation history (it is never synced via Yjs — it is a projection).

## Schemas & interfaces introduced

```fsharp
// Codec at the sync boundary. Concrete Yjs types are boundary formats only.
type SyncedStateCodec =
    { Encode : SyncedSessionState -> unit   // apply into the Yjs document
      Decode : unit -> SyncedSessionState }  // read typed state out of the Yjs document

// SessionEvent case added this step:
//   | DraftStarted of DraftStarted
type DraftStarted = { DraftId : DraftId; StartedBy : PeerId }
```

Boundary contract:

- Only `SyncedSessionState` (drafts + shared brief) crosses the Ylmish boundary.
- Conversation history must **not** be encoded into Yjs.
- Raw Y maps / JSON do not appear in application logic — only typed state does.

## Work outcome

- A user can start a draft and edit its body.
- A second connected client sees edits converge in real time.
- Starting a draft appends `DraftStarted`.

## Verification

- **E2E-1:** two clients collaboratively edit one draft and converge.
- Model test: encoded Ylmish state contains drafts but not conversation history.
- Model test: decode∘encode preserves `SyncedSessionState`.

## Done when

- [ ] Ylmish codec encodes/decodes `SyncedSessionState`.
- [ ] Two-client convergence (E2E-1) passes.
- [ ] "Drafts synced, conversation not synced" test passes.
