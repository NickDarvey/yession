# Yession — Design & System Fundamentals

This document captures the durable principles and system design for Yession. It is the
canonical reference for *why* the runtime is shaped the way it is. The delivery plans
referenced this document rather than restating it.

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
  - owns launch, identity, and secret custody
  - declares what a session may reach; holds no environment authority itself
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
also served over HTTP, by CURSOR — the event log and each terminal's transcript, on
identical terms. A client sends the position it has folded
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
  holds a notion of retrying. Each fault gets a `Verdict` rather than a yes/no, because a
  provider that names its own window (`RetryAfter`) is a third answer, and honouring one
  outside the policy would put the pace in two places. A hang is not a fault any schedule
  can see, so `Policy.deadline` bounds one attempt and hands the policy a fault to rule
  on; it composes inside `guard`, which is what makes the limit per attempt. A
  `Breaker` is the question after the policy's — not "is this call worth making
  again" but "is this RESOURCE worth calling at all just now" — held per resource
  and composed outside `guard`, so it counts settled failures rather than attempts.
  A rate limit is the question none of those ask, because it is answerable before the
  call: `Quota` holds the provider's OWN counter (`Allowance`), read from its replies
  rather than tallied here — GitHub's budget belongs to a credential, not a process,
  so one session's reply already reports what every other session holding it spent —
  and `Spend` decides who may take the last of it, so a poller cannot spend the
  request a person is waiting on.
  What an HTTP answer means for any of it is said once, in `Resilience.Http.verdict`,
  so a 503 means the same thing to every leg that reads one and a leg still owns
  what is peculiar to it by composing over that rather than answering it again;
- the feed's health (`FeedHealth`) is model state, rendered as a status and a banner. A
  stalled feed disables nothing: collaborative state is CRDT state in the local doc, so
  reading, writing, and sending continue — per "local first", a lost history feed costs
  history and nothing else.

The session leg is supervised on the same terms, for the same reason: it is a leg that can
fail independently, and it used to fail in a way nothing could see. A WebRTC data channel dies
without saying so — a backgrounded phone, a WiFi-to-cellular switch — leaving `readyState` at
`open`, sends accepted into nothing, and no `close` event ever. So every channel that carries
a session is wrapped in `Link.supervise` (`Yession.Domain/Link.fs`) before anything else holds
it: any inbound frame is proof of life, a quiet link is probed once a second, and three quiet
ticks make it dead. Supervision is symmetric — the Session Process holds every peer to the
heartbeat it answers — which is also how a silently-dead peer stops holding a terminal lease.

Death has exactly one expression: the channel CLOSES. Nothing above the wrapper learns about
liveness through a second channel, so there is no second state to keep consistent with the
first, and the two pumps needed no change — the client reconnects and re-pushes its full doc
state, the Session Process runs the cleanup it already ran. `LinkPolicy` takes its clock as a
port exactly as `Resilience.Policy` does, so the whole quiet-tick sequence is asserted in the
cheap tier in zero real time ([ADR](decisions/2026-08-20-session-link-liveness.md)).

---

## 3. Authority model (Phase 2)

The Manager owns launch, identity, and secret custody — not environment authority. A
Session Process spawns its own sandboxes through the `CreateSandbox` seam and confines
them with whichever `SandboxBackend` it was booted with (`Yession.Domain/Sandbox.fs`);
secrets are the one thing it cannot mint, and they cross from the Manager only at sandbox
spawn, over the authenticated control channel. The earlier design — Manager-issued,
session-scoped container handles — was replaced by the sandbox seam, which confines the
agent's own CLI as well as the work it runs.

```text
Session Manager   owns launch, identity, and secret custody.
Session Process   owns orchestration; spawns and confines its own sandboxes.
Session Environment   a sandbox associated with exactly one SessionId; started lazily.
```

Environments start **lazily**, only when the agent determines work requires one. A
one-shot conversational answer must not start a sandbox.

### Threat model (Phases 1–2)

```text
Protect against LAN snooping in the client/session transport.
Manager may read plaintext.
Process may read plaintext.
Command-to-container encryption is designed for but not implemented yet.
```

User access to a session is authorized through the Manager as an OIDC provider
(authorization code + PKCE; each Session Process registers as a client with its
per-launch control secret). Three authentication strategies ship, selected by the
`--auth` argument at Manager start: `none` (the default — nothing authenticates
until the operator chooses a trust rule), `localhost` (any loopback request is the
single unattributed `local` subject, matching this threat model), and
`trusted-headers` (an operator-run authenticating proxy — e.g. Tailscale plus a
header-rewriting proxy — asserts the user in canonical `x-yession-*` headers; the
proxy must be the only non-local path in and must strip those headers from client
requests). Further schemes are strategy swaps, not redesigns.

Secrets are Manager-owned authority: encrypted at rest under a key the OS credential
manager holds (no credential manager → no persistence), authorized by a pure
default-deny policy over the composite identity the Manager verified itself (the
calling session + the users bound to its launch at ID-token issuance). Sessions hold
pre-scoped write/list/delete capabilities and no agent-facing read. Values leave the
store through exactly two resolve-shaped routes, each gated to the caller's readable
scopes: `/control/secrets/resolve`, which feeds `SecretRef` env injection at the
session's own sandbox spawn (sandboxes are session-owned, so the injection point is
there rather than in the Manager), and `/control/connections/resolve`, which releases a
connection credential for one agent turn. Refresh tokens never leave the Manager, and no
agent tool wraps either route. That refresh is one of the resilience policies (`Broker.
resilient`, composed in `ProcessManager`): it runs where nobody is looking, inside a turn
or a git verb, so a provider's bad minute used to read as a broken credential — and a
provider calling the grant dead still stops at once, which is a different answer from a
failed one.

MCP servers are Manager-owned authority in the same way: a declaration names a url and
who reaches it, it lives in `ManagerState`, and every session it names connects itself
over the reverse control leg. The Manager never becomes an MCP client, and neither an
agent nor a peer can name a url — what a session talks to is the operator's decision,
and a stream a tool result offers is admitted only on the host the operator already
declared. The tool descriptions that come back are the one untrusted text the model must
read; see [GAPS.md](GAPS.md).

---

## 4. Repo-local configuration

A checkout may carry a `yession.yaml` at its root, and a session folds every one it holds
into the commands it already has (Plan 27). Undotted, matching the other files a repo means
people to read.

```yaml
version: 1
sandboxes:
  app:
    container: { image: node:24 }
    workdir: ./packages/web
    env:
      DATABASE_URL: { secret: db-url }   # a name, never a value
    net: [ registry.npmjs.org ]
    read: [ ~/.cache/npm ]
    forward: [ github ]
```

Two top-level keys, and one is a version. **The whole file is sandboxes**, because a sandbox
is the only scope where "two repos both said something" has a total answer: a sandbox is
named, the name is scoped to its repo, so `config(session) = ⋃ over repos r of { (r, name) ↦
spec }` is disjoint by construction. There is no precedence rule and nothing shadows
anything; a clash inside one file is refused where it was written. Anything session-wide has
no honest tie-break and stays the operator's — see [GAPS.md](GAPS.md).

Three properties make the file safe to read from code a session just cloned:

- **It is a fold, not an executor.** Every mutating command is ensure-shaped, so a
  declaration is one `start_work_sandbox` through the same gate the agent's goes through.
  Asking twice changes nothing and records nothing, which is what lets the fold re-run at
  boot and after every verb that changes a checkout.
- **The operator's environment is a ceiling, not a default.** Narrowing lands at once; an ask
  that exceeds the ceiling is refused, naming what exceeded it. A file that says nothing gets
  the operator's list whole.
- **The file is authored by whoever can push to the repo**, which is neither the operator nor
  anybody in the session. So its acts are attributed to `ActorRef.Configured`, it owns no
  credential of its own, and the schema refuses what a repo may not name: the whole
  `YESSION_` prefix in `env:`, a host path as a volume source, a `workdir` outside the
  checkout.

This repository carries its own, which is the schema's acceptance test: what yession
needs to maintain yession is the measure of whether the file can say anything real.

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

`Ylmish is the encoding/sync boundary` has one deliberate exception, and it is narrow
enough to name: message bodies and terminal command lines are top-level Yjs roots the app
co-manages directly (`src/Yession.Domain/RichText.fs`), because Ylmish's structural decode
cannot traverse a `Y.XmlFragment` — it recurses into the fragment's cyclic internals and
crashes the decode. They are keyed by `BodyKey`, never nested in the encoded tree, and
never read by a whole-doc structural decode. Everything else still crosses through the
codec; folding a body back into the Ylmish schema breaks decoding for every peer at once.

---

## 6. Cross-cutting type vocabulary

Identity and envelope types are shared across nearly every step. They are declared in
`Yession.Domain/Identity.fs` and referenced throughout; each delivery step introduced
the additional schemas it owned and leant on this document for the surrounding context.

The actor glossary:

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
