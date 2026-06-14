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
| 00 | [Foundations & domain types](00-init/00-foundations-and-domain-types.md) | Solution builds; identity, envelope, `SessionEvent` exist | Todo | |
| 01 | [Append-only event log](00-init/01-event-log.md) | Monotonic offsets; deterministic paged reads | Todo | |
| 02 | [Process model & projection](00-init/02-session-process-model-and-projection.md) | Conversation projected purely from events | Todo | |
| 03 | [WebRTC transport & frames](00-init/03-webrtc-transport-and-frames.md) | Multiplexed `SessionFrame`; handshake; presence | Todo | |
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
| — | — | None recorded | — | — |

## Decisions log

| Date | Decision |
|------|----------|
| 2026-06-14 | Runtime targets: Browser Client = F#/Fable; Session Process = F# on Node. |
| 2026-06-14 | Steps live in `docs/plans/00-init/` as `NN-*.md`; schemas scoped per step. |
| 2026-06-14 | Principles & system design in [docs/design.md](../design.md); product intent in root README. |
