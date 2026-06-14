# Step 04 — Web app bootstrap & client Elmish shell

> Phase 1 · Client shell
> Design context: [docs/design.md](../../design.md) §2.1, §2.3

## Goal

Serve the static web app and stand up the Browser Client Elmish shell: it connects to
the Session Process over WebRTC and renders connection status and the event-offset
displays required by the product (offsets are a core invariant, not a debug detail).

## Prerequisites

- [Step 03 — WebRTC transport & frame protocol](03-webrtc-transport-and-frames.md)

## Scope

**In scope**

- Static app bootstrap served over HTTP.
- The client Elmish model and update loop shell.
- UI skeleton: connection status, random peer display name, last-processed offset,
  latest-known offset, catch-up status. Placeholders for the draft editor, send button,
  conversation timeline, and agent stream (filled by later steps).

**Out of scope**

- Collaborative draft sync (Step 05), send/command flow (Step 06), event consumption
  logic (Step 07), agent rendering (Step 08).

## Schemas & interfaces introduced

```fsharp
type ClientModel =
    { Peer          : PeerState
      Connection    : ConnectionState
      Synced        : SyncedSessionState
      Conversation  : ConversationProjection
      EventConsumer : EventConsumerState
      Agent         : AgentViewState }

and PeerState = { PeerId : PeerId; DisplayName : string }

and ConnectionState = Disconnected | Connecting | Connected | Reconnecting

and EventConsumerState =
    { LastProcessedOffset : EventOffset option
      LatestKnownOffset   : EventOffset option
      IsCatchingUp        : bool }

and AgentViewState = { ActiveTurn : AgentTurnId option }
```

`SyncedSessionState`, `ConversationProjection`, and related types are defined in
[Step 02](02-session-process-model-and-projection.md); the client reuses them.

## Work outcome

- Loading the app in a browser connects to the Session Process and reflects connection
  state transitions.
- The UI shows a random display name and the offset/catch-up indicators (initially
  empty/zeroed).

## Verification

- E2E: the app loads, completes the handshake, and shows `Connected`.
- E2E: the UI renders both offset displays and the catch-up indicator.
- E2E: a dropped connection moves the UI to `Reconnecting`.

## Done when

- [ ] Static bootstrap serves the Fable app.
- [ ] Client connects and renders connection + offset UI.
- [ ] Connection-state E2E checks pass.
