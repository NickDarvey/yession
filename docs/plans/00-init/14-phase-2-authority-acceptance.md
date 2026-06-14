# Step 14 — Phase 2 authority & catch-up acceptance

> Phase 2 · Acceptance gate
> Design context: [docs/design.md](../../design.md) §3, §5

## Goal

Consolidate Phase 2 into an automated suite that verifies the Manager/Process authority
boundary, lazy environment behaviour, command logging, and mixed-event catch-up — and
confirm the Phase 1 split is still preserved.

## Prerequisites

- All Phase 2 steps: [10](10-session-manager-and-launch.md) through
  [13](13-command-execution-and-log.md).

## Scope

**In scope**

- An E2E suite covering every required Phase 2 scenario.
- Integration and model/protocol suites for the authority boundary and event projection.
- Confirmation that the Phase 1 E2E suite still passes.

**Out of scope**

- GitHub clone, `.yession.yml`, commit/push, interactive terminal (later phases).

## Required E2E scenarios

```text
E2E-1: conversational one-shot does not start environment                   (Step 12)
E2E-2: development task identifies need and starts environment              (Step 12)
E2E-3: command execution appends Started/OutputReceived/Completed           (Step 13)
E2E-4: browser clients see command log through event pages                  (Step 13)
E2E-5: Session Process cannot exec without a valid scoped container handle  (Step 11)
E2E-6: Session Process cannot exec in another session's container           (Step 11)
E2E-7: stopped container resumed/preserved per the Phase 2 contract         (Step 12)
E2E-8: disconnected client catches up on environment & command events       (Step 07/13)
```

## Required integration tests

```text
Manager launches Session Process.                       (Step 10)
Session Process receives scoped capability.             (Step 11)
StartContainer creates a session-owned container.       (Step 11)
Exec validates container ownership.                     (Step 11)
Command output is streamed into the event log.          (Step 13)
Environment events are projected into UI state.         (Step 12)
```

## Required model / protocol tests

```text
Environment events project deterministically.
Command events project deterministically.
Command output ordering is preserved per command.
Event offsets remain monotonic across agent, environment, and command events.
Client catch-up works across mixed message and command events.
```

## Acceptance criteria (per [design.md](../../design.md) §3)

```text
Session Manager launches a Session Process.
The Process receives scoped environment capabilities.
The environment starts lazily, not at session creation.
The agent distinguishes one-shot conversation from environment-required work.
The Process executes commands only through Manager-delegated capability.
Command output is rendered read-only through the event log.
Authority boundaries are verified by automated E2E tests.
The Elmish/Ylmish/WebRTC/event-log split from Phase 1 is preserved.
```

## Verification

- The full Phase 2 E2E suite runs from one entry point and passes.
- Integration and model/protocol suites pass.
- The Phase 1 E2E suite still passes (regression).

## Done when

- [ ] All required Phase 2 E2E scenarios pass.
- [ ] Integration and model/protocol suites pass.
- [ ] Phase 1 regression suite passes.
- [ ] Phase 2 acceptance recorded in [../TODO.md](../TODO.md).
