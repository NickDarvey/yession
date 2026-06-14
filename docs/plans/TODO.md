# Yession — Delivery Tracker

The single place to track progress and blockers across delivery steps. Update the status
and notes as you work. Schemas and detailed scope live in the per-step files; this file
stays high-level.

- Product intent: [../../README.md](../../README.md)
- Design fundamentals & invariants: [../design.md](../design.md)
- Plan folder (step files): [00-init/](00-init/)

## How to use this tracker

1. Pick the next `Todo` step in order; mark it `In progress`.
2. Do the work described in the step file. Each step lists the schemas it introduces and
   its verification.
3. A step is **Done** only when its automated verification passes (no manual one-shot).
4. Record blockers inline in the Notes column and in the Blockers log below.

Status legend: `Todo` · `In progress` · `Blocked` · `Done`

## Phase 1 — Local collaborative session core

| #  | Step | Outcome | Status | Notes / Blockers |
|----|------|---------|--------|------------------|
| 00 | [Foundations & domain types](00-init/00-foundations-and-domain-types.md) | Solution builds; identity, envelope, `SessionEvent` exist | Done | Solution + domain library + tests green (`mise run test`) |
| 01 | [Append-only event log](00-init/01-event-log.md) | Monotonic offsets; deterministic paged reads | Done | In-memory log behind `EventLog<'event>`; 7 model tests green |
| 02 | [Process model & projection](00-init/02-session-process-model-and-projection.md) | Conversation projected purely from events | Done | Pure offset-gated fold in Domain; `ProcessModel` in Session Process; determinism + idempotency tests green |
| 03 | [WebRTC transport & frames](00-init/03-webrtc-transport-and-frames.md) | Multiplexed `SessionFrame`; handshake; presence | Done | Real libdatachannel transport + HTTP bootstrap/signalling; Fable→Node host; event-driven WebRTC E2E green |
| 04 | [Web app bootstrap & client shell](00-init/04-web-app-bootstrap-and-client-shell.md) | App connects; connection + offset UI | Todo | |
| 05 | [Ylmish draft sync](00-init/05-ylmish-collaborative-draft-sync.md) | Two clients converge on a draft | Todo | |
| 06 | [Send draft & MessageSent](00-init/06-send-draft-and-message-events.md) | Send snapshots body; immutable sent message | Todo | |
| 07 | [Client event consumption](00-init/07-client-event-consumption.md) | Offset paging; reconnect catch-up; read-only | Todo | |
| 08 | [Claude agent turn](00-init/08-claude-agent-turn.md) | Real streamed agent response as events | Todo | |
| 09 | [Phase 1 E2E acceptance](00-init/09-phase-1-e2e-acceptance.md) | Full E2E + model suite green | Todo | |

**Phase 1 acceptance:** not started.

## Phase 2 — Session Manager & scoped lazy environment capability

| #  | Step | Outcome | Status | Notes / Blockers |
|----|------|---------|--------|------------------|
| 10 | [Session Manager & launch](00-init/10-session-manager-and-launch.md) | Manager launches & registers a Process | Todo | |
| 11 | [Scoped environment capability](00-init/11-scoped-environment-capability.md) | Session-scoped, unforgeable capabilities | Todo | |
| 12 | [Lazy environment lifecycle](00-init/12-lazy-environment-lifecycle.md) | One-shot starts nothing; tasks start env | Todo | |
| 13 | [Command execution & log](00-init/13-command-execution-and-log.md) | Streamed, ordered, read-only command log | Todo | |
| 14 | [Phase 2 authority acceptance](00-init/14-phase-2-authority-acceptance.md) | Authority + catch-up suite green | Todo | |

**Phase 2 acceptance:** not started.

## Blockers log

| Date | Step | Blocker | Owner | Resolution |
|------|------|---------|-------|------------|
| 2026-06-14 | 03 | Real WebRTC data channel + HTTP signalling needed the F#-on-Node toolchain bootstrap. | — | Resolved — Fable→Node host (`app/`) with a libdatachannel `FrameChannel` adapter and HTTP bootstrap/signalling; verified by an event-driven WebRTC E2E. |

## Decisions log

| Date | Decision |
|------|----------|
| 2026-06-14 | Runtime targets: Browser Client = F#/Fable; Session Process = F# on Node. |
| 2026-06-14 | Steps live in `docs/plans/00-init/` as `NN-*.md`; schemas scoped per step. |
| 2026-06-14 | Principles & system design in [docs/design.md](../design.md); product intent in root README. |
| 2026-06-14 | Solution uses the `.slnx` format; layout is `src/` (Domain, SessionProcess, Client) + `tests/`. |
| 2026-06-14 | Wire format = hand-written Thoth.Json codecs (Fable) / Thoth.Json.Net (.NET) selected via `#if FABLE_COMPILER`; private constructors are honoured, no auto-coders. |
| 2026-06-14 | Event log is the function-shaped `EventLog<'event>` capability (Append/Read) in the Session Process; in-memory impl assigns offsets = append count. Reads are single-page `after -> limit -> Async<EventPage>` (Fable-portable; matches the `ReadEventsAfter`/`EventsPage` frames). `EventPage`/`AppendResult` live in Domain (shared/Fable-safe). |
| 2026-06-14 | Conversation projection (`ConversationProjection` + `ConversationProjection.applyEvents`) lives in Domain (shared Process/client); offset-gated fold is idempotent and never reads synced/draft state. `ProcessModel` + synced state live in the Session Process. |
| 2026-06-14 | Transport is a pure `SessionFrame<'State>` protocol in Domain (state payload opaque, `'State` parameterised). The Session Process side is a `FrameChannel<'State>` capability with token-gated `PeerSession.run` handshake + presence. |
| 2026-06-14 | Session Process runs as F# compiled by Fable to JS on Node (`app/` host). The real transport is a libdatachannel `FrameChannel` adapter; signalling is non-trickle (await ICE gathering-complete, exchange one SDP over an HTTP request/response) so connection establishment never depends on timing. Core (`EventLog`/`Model`/`FrameChannel`/`PeerSession`) is Fable-safe; .NET is build-only. mise `start`/`dev`/`test` drive it. |
| 2026-06-14 | One test framework: Fable.Pyxpecto (Expecto-style), compiled by Fable and run on Node — no `dotnet test`. A single project `tests/Yession.Tests/` covers domain/protocol units and the real WebRTC E2E, exercising the same JS the product runs. A watchdog (`run.mjs`) hard-caps the run. JUnit XML is not built into Pyxpecto; a reporter can be layered later for pipelines. |
