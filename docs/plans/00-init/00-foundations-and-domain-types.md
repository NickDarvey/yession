# Step 00 — Foundations & shared domain types

> Phase 1 · Foundations
> Design context: [docs/design.md](../../design.md) §1, §2, §6

## Goal

Establish the repository structure and the shared, typed domain vocabulary that every
later step builds on. After this step the solution builds and the core identity and
event types exist with smart constructors and round-trippable serialization.

## Prerequisites

None. This is the first step.

## Scope

**In scope**

- Repository / solution layout for: a shared domain library, the Session Process
  (F# on Node), the Browser Client (F#/Fable), and a test project.
- Core identity types with private constructors and smart constructors.
- The actor model and the generic event envelope.
- The `SessionEvent` discriminated union declared as the single growing event type.
  Only the payloads relevant to foundations are defined here; later steps add cases.

**Out of scope**

- Any event log behaviour (Step 01).
- Any transport, UI, sync, or agent behaviour.

## Schemas & interfaces introduced

```fsharp
type SessionId   = private SessionId   of string
type PeerId      = private PeerId      of string
type DraftId     = private DraftId     of string
type MessageId   = private MessageId   of string
type AgentTurnId = private AgentTurnId of string
type EventId     = private EventId     of Guid
type EventOffset = private EventOffset of int64
type RequestId   = private RequestId   of Guid

type ActorRef =
    | HumanPeer of PeerId
    | Agent
    | SessionProcess
    | System

type EventEnvelope<'event> =
    { EventId   : EventId
      SessionId : SessionId
      Offset    : EventOffset
      Actor     : ActorRef
      Timestamp : DateTimeOffset
      Event     : 'event }

// The single, append-only event type. Grows per step; Phase 1 cases below.
type SessionEvent =
    | SessionCreated of SessionCreated
    // PeerJoined / PeerLeft           -> Step 03 (control/presence)
    // DraftStarted                    -> Step 05/06
    // MessageSent                     -> Step 06
    // Agent* events                   -> Step 08

and SessionCreated =
    { SessionId : SessionId }
```

Each identity type must expose a smart constructor (validating/normalising input) and a
way to read its underlying value. The envelope and `SessionEvent` must serialize and
deserialize without loss (the wire format is a boundary concern, not the domain model).

## Work outcome

- The solution builds across all projects.
- Domain types are usable from the Session Process, Client, and tests.
- A clear convention exists for where new `SessionEvent` cases and payloads are added.

## Verification

- Solution builds with no warnings treated as errors.
- Unit tests: smart constructors reject invalid input and normalise valid input.
- Unit tests: `EventEnvelope<SessionEvent>` round-trips through serialization unchanged.

## Done when

- [ ] Projects build and reference the shared domain library.
- [ ] Identity types, `ActorRef`, `EventEnvelope`, and `SessionEvent` exist.
- [ ] Round-trip and smart-constructor tests pass.
