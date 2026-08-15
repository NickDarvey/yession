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

A test pins one invariant and goes red only when that invariant breaks: arranged, acted,
and asserted once, against what the system PROMISES rather than how it currently looks, is
built, or is worded. A suite whose red can also mean "something moved" stops being read.
Writing them: [AGENTS.md](../AGENTS.md#writing-tests).

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

One read path is the exception the rule permits, and it is read-only: the durable HISTORY is
also served over HTTP, by CURSOR — the event log (docs/plans/20) and each terminal's
transcript (docs/plans/22), on identical terms. A client sends the position it has folded
through (`GET /events/after/{n}`, `GET /terminals/{t}/after/{n}`, or the bare path from the
beginning) and the server answers with a redirect to the range it chose (`GET
/events/{first}-{last}`, `GET /terminals/{t}/{first}-{last}`), or `204` when that client is
already current. A range's bounds do not move, so its bytes are the same for ever — the
growing tail included, which a fixed chunk index could never manage — and that is what makes
an answer worth keeping. The client keeps them, in a store it can enumerate and ask to
persist (the Cache API, one named for the session's events and one per terminal); every
response on the surface is `no-store`, because the copy that matters is the client's and a
second one in the HTTP cache would be a spare nobody reads.

The client computes no address: it holds a position and stores what it is given under the
address it was given. The one number it does compute is where an answer starts, and that
comes from what it ASKED rather than from where the answer lives — the answer to `after n`
begins at `n + 1`. Events carry their own offsets and do not need even that; transcript lines
cannot, because the file is an asciicast and a private index field in it would stop it being
one, which is why the cursor's start is a server contract with a test of its own.

That makes the durable history feed a second, independently failing leg, and it is treated
as one:

- a read is `EventOffset option -> Async<Result<EventPage, FeedFault>>`, so a dead feed is
  distinguishable from an empty one — never an empty page standing in for a failure;
- transient-fault handling is a `Resilience.Policy` (`Yession.Domain/Resilience.fs`) —
  retry, backoff, and jitter as values, with the clock injected — composed *with the
  transport* at the application boundary (`app/browser/Browser.fs`), per "composition at
  the top". `App.connect` receives a feed that has already settled, so no application code
  holds a notion of retrying;
- the feed's health (`FeedHealth`) is model state, rendered as a status and a banner. A
  stalled feed disables nothing: collaborative state is CRDT state in the local doc, so
  reading, writing, and sending continue — per "local first", a lost history feed costs
  history and nothing else.

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

User access to a session is authorized through the Manager as an OIDC provider
(authorization code + PKCE; each Session Process registers as a client with its
per-launch control secret — see docs/plans/04-session-authorization.md). Three
authentication strategies ship (docs/plans/07-byo-user-authorization.md), selected by
the `--auth` argument at Manager start: `none` (the default — nothing authenticates
until the operator chooses a trust rule), `localhost` (any loopback request is the
single unattributed `local` subject, matching this threat model), and
`trusted-headers` (an operator-run authenticating proxy — e.g. Tailscale plus a
header-rewriting proxy — asserts the user in canonical `x-yession-*` headers; the
proxy must be the only non-local path in and must strip those headers from client
requests). Further schemes are strategy swaps, not redesigns.

Secrets are Manager-owned authority (docs/plans/06-secrets-and-abac.md): encrypted at
rest under a key the OS credential manager holds (no credential manager → no
persistence), authorized by a pure default-deny policy over the composite identity the
Manager verified itself (the calling session + the users bound to its launch at
ID-token issuance). Sessions hold pre-scoped write/list/delete capabilities only —
secret values reach workloads exclusively by Manager-side injection into a launched
environment, never through the agent loop and never back over the control channel.

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

The actor glossary (docs/plans/07-byo-user-authorization.md):

- **User** (`UserId`) — a durable, Manager-verified human identity: the OIDC `sub` the
  Manager itself issued. Exists only when a real authentication strategy attributed one.
- **Peer** (`PeerId`) — one client connection (a browser profile, stable via
  localStorage). Connection identity, not human identity; self-minted, never verified.
- **Actor** (`ActorRef`) — the attribution union on events:
  `UserRef | PeerRef | Agent | SessionProcess | System`.
- **Unattributed access** — the localhost strategy's grant: the request is allowed in
  under the shared `local` subject, but no attributable user stands behind it, so
  events fall back to `PeerRef` attribution. The `Attributed`/`Unattributed` split in
  `AuthenticationOutcome` is how "real user" is distinguished — never by comparing
  subject strings.
