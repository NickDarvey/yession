# Yession — Design & System Fundamentals

This document captures the durable principles and system design for Yession. It is the
canonical reference for *why* the runtime is shaped the way it is. Delivery steps in
[plans/00-init/](plans/00-init/) reference this document rather than restating it.

For the product framing, see the [root README](../README.md).

---

## 1. Core principles

These are architectural constraints, not preferences.

### Local first

A session runs on a local node. The first implementation assumes one node. Central
discovery, placement, and remote directories are deferred.

Clients connect to the local Session Process over WebRTC. HTTP may be used for static
app bootstrap and signalling, but **not** as the main session API.

### Reactive

The system is modelled as explicit state transitions driven by events, with no shared
mutable state across components. Transitions should be deterministic and composable. UI,
collaboration, agent progress, and event consumption are projections of state, while
side effects are isolated behind explicit boundaries.

### Types first

Correctness is specified through the type system: core invariants, valid states, and
allowed transitions are encoded in F# types and checked at compile time where possible.
Yjs is not the product model. JSON blobs, Y maps, and stringly-typed payloads remain
boundary formats and must not leak into application logic.

### Ylmish as the sync boundary

Elmish owns the model and update loop. Ylmish encodes selected Elmish state into Yjs and
decodes synced Yjs state back into Elmish state.

```text
Elmish Model / Msg / update
  -> Ylmish codec
  -> Yjs document
  -> WebRTC sync
  -> Ylmish codec
  -> Elmish Model
```

### Durable facts are events

Collaborative editing state belongs in Yjs. Durable session history belongs in a
Session Process-owned event log.

```text
Yjs        = collaborative editable state
Event log  = durable facts
Client offset = read progress through the event log
```

The Session Process is the only event writer.

### Composition at the top

Capabilities are small and function-shaped. Concrete infrastructure is composed at the
application boundary, not spread through domain code.

### High-signal automated verification

Each implementation phase must include automated end-to-end tests. Manual testing is not
a substitute for verification. There is no "one-shot" manual validation.

---

## 2. System overview

### 2.1 Runtime components

```text
Session Process
  - owns one session
  - hosts the web app bootstrap
  - owns the append-only event log
  - owns/hosts the Yjs document
  - runs the Elmish model/update loop
  - runs the agent runtime
  - exposes a multiplexed WebRTC protocol to clients
  - later receives delegated capabilities from the Session Manager

Browser Client
  - runs the Elmish model/update loop
  - connects over WebRTC
  - edits collaborative state through Ylmish/Yjs
  - consumes read-only event pages by offset
  - renders projections from events and synced state

Session Manager (Phase 2)
  - launches Session Processes
  - grants scoped capabilities
  - owns container/repo/environment authority
```

### 2.2 State split

```text
Elmish model        Product state and transitions.
Ylmish/Yjs          Encoding and synchronization of selected collaborative state.
Event log           Append-only durable session facts owned by the Session Process.
Client event offset Client-side read position through the event log.
```

### 2.3 Transport

The session transport is WebRTC. The connection multiplexes:

```text
collaboration sync frames (opaque payload)
event-log page requests/responses
session commands
control/presence frames
agent progress notifications (hints only)
```

HTTP is allowed only for: serving the web app, initial local bootstrap, and temporary
signalling. **HTTP is not the session API.**

---

## 3. Authority model (Phase 2)

The Session Process must not hold ambient Docker or host authority. The Session Manager
launches the Session Process and grants capabilities already scoped to that session.

```text
Session Manager   owns authority; enforces session/container scope.
Session Process   owns orchestration; calls delegated, scoped capabilities.
Session Environment   a container associated with exactly one SessionId; started lazily.
```

Environments start **lazily**, only when the agent determines work requires one. A
one-shot conversational answer must not start a container.

### Threat model (Phases 1–2)

```text
Protect against LAN snooping in the client/session transport.
Manager may read plaintext.
Process may read plaintext.
Command-to-container encryption is designed for but not implemented yet.
```

A random session token is acceptable for local development.

---

## 4. Naming

The repo-local environment config is not implemented in the first two phases, but the
concept is reserved. The tentative name, in the style of "Ylmish":

```text
.yession.yml
```

Treat this as tentative until the environment configuration phase.

---

## 5. Architectural invariants

These must be visible in code review. They matter more than polish in the first phases.

```text
Elmish is the product model.
Ylmish is the encoding/sync boundary.
Yjs is not the domain model.
WebRTC is the session transport.
HTTP is bootstrap/signalling only.
The Session Process is the only event writer.
Clients consume events read-only by offset.
Drafts are collaborative state.
Sent messages are durable events.
Conversation is a projection.
Manager owns authority.
Session Process owns orchestration.
Environment starts lazily.
Capabilities are scoped, not ambient.
Verification is automated end-to-end, not manual.
```

---

## 6. Cross-cutting type vocabulary

Identity and envelope types are shared across nearly every step. They are introduced in
[00-foundations-and-domain-types.md](plans/00-init/00-foundations-and-domain-types.md)
and referenced throughout. Each delivery step introduces the additional schemas it owns
and links back here for the surrounding context.
