# Step 03 — WebRTC transport & multiplexed frame protocol

> Phase 1 · Transport
> Design context: [docs/design.md](../../design.md) §2.3

## Goal

Establish the WebRTC session transport and the multiplexed `SessionFrame` protocol,
including local bootstrap/signalling over HTTP and the peer hello/accept handshake. Two
peers can connect to the Session Process locally.

## Prerequisites

- [Step 00 — Foundations & shared domain types](00-foundations-and-domain-types.md)

## Scope

**In scope**

- WebRTC peer connection between client and Session Process.
- HTTP used only for static bootstrap and temporary signalling.
- The multiplexed `SessionFrame` protocol and its serialization.
- The control/presence handshake (`PeerHello` → `PeerAccepted`/`PeerRejected`) including
  a random local session token.
- `PeerJoined` / `PeerLeft` event payloads appended by the Process on connect/disconnect.

**Out of scope**

- State sync payloads (Step 05) — the `State` frame is carried but opaque here.
- Command handling (Step 06) and event paging logic (Step 07) — frame shapes exist; their
  handlers arrive in later steps.

## Schemas & interfaces introduced

```fsharp
type SessionFrame<'State> =
    | State   of StateFrame<'State>
    | Command of CommandFrame
    | EventLog of EventLogFrame
    | Control of ControlFrame

type StateFrame<'State> = StateSync of 'State

type CommandFrame =
    | Request  of RequestId * SessionCommand
    | Response of RequestId * SessionCommandResult

type SessionCommand = SendDraft of DraftId | StartDraft
type SessionCommandResult = CommandAccepted | CommandRejected of reason: string

type EventLogFrame =
    | EventsAvailable  of latestOffset: EventOffset
    | ReadEventsAfter  of RequestId * after: EventOffset option * limit: int
    | EventsPage       of RequestId * EventPage<SessionEvent>

type ControlFrame =
    | PeerHello    of PeerHello
    | PeerAccepted of PeerAccepted
    | PeerRejected of reason: string
    | Ping
    | Pong

type PeerHello    = { PeerId : PeerId; DisplayName : string; Token : string }
type PeerAccepted = { SessionId : SessionId; AssignedDisplayName : string; LatestOffset : EventOffset option }

// SessionEvent cases added this step:
//   | PeerJoined of PeerJoined
//   | PeerLeft   of PeerLeft
type PeerJoined = { PeerId : PeerId; DisplayName : string }
type PeerLeft   = { PeerId : PeerId }
```

State serialization belongs to the sync-boundary layer (Step 05), not to the transport
protocol model — the `State` frame payload is opaque to the transport.

## Work outcome

- A client can establish a WebRTC connection to the Session Process after a hello/accept
  handshake gated by the session token.
- Frames of every variant can be serialized and exchanged.
- Connect/disconnect produce `PeerJoined`/`PeerLeft` events.

## Verification

- Model test: `SessionFrame` serialization round-trips for every variant.
- Integration test: two local peers complete the handshake; a bad token is rejected.
- Integration test: disconnect appends `PeerLeft`.

## Done when

- [ ] WebRTC connection + HTTP bootstrap/signalling working locally.
- [ ] `SessionFrame` round-trip test passes for all variants.
- [ ] Handshake accept/reject and presence events verified.
