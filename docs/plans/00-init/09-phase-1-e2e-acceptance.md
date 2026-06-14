# Step 09 — Phase 1 end-to-end acceptance

> Phase 1 · Acceptance gate
> Design context: [docs/design.md](../../design.md) §1 "High-signal automated verification", §5

## Goal

Consolidate Phase 1 into a single, repeatable automated end-to-end suite and confirm the
acceptance standard: Phase 1 is accepted when the suite verifies the required flows — not
because it worked once locally.

## Prerequisites

- All Phase 1 steps: [00](00-foundations-and-domain-types.md) through
  [08](08-claude-agent-turn.md).

## Scope

**In scope**

- A runnable E2E suite covering every required Phase 1 scenario.
- A model/protocol test suite covering the required invariants.
- A single command (or task) that runs the full verification.

**Out of scope**

- Phase 2 (Session Manager / environments).

## Required E2E scenarios

```text
E2E-1: two clients collaboratively edit one draft                         (Step 05)
E2E-2: sending a draft appends MessageSent and updates both clients       (Step 06)
E2E-3: sent message remains immutable after draft changes                 (Step 06)
E2E-4: client disconnects, events continue, reconnects, catches up        (Step 07)
E2E-5: real agent response is appended as an event stream                 (Step 08)
E2E-6: client cannot append events directly                               (Step 07)
E2E-7: conversation renders only from event projection, not Yjs draft     (Step 07)
```

## Required model / protocol tests

```text
Event offsets are monotonic.                                              (Step 01)
Read returns deterministic pages.                                         (Step 01)
Duplicate event pages do not duplicate conversation items.                (Step 02/07)
SessionFrame serialization round-trips.                                   (Step 03)
Ylmish synced state contains drafts but not conversation history.         (Step 05)
Agent failure produces AgentTurnFailed.                                   (Step 08)
```

## UI checklist (must be present and verified)

```text
session connection status
random peer display name
collaborative draft editor
send button
conversation timeline
agent streaming response
last processed event offset
latest known event offset
catch-up status
```

## Verification

- The full E2E suite runs from one entry point and passes deterministically.
- The model/protocol suite passes.
- Re-running the suite produces the same result (no flakiness in the required flows).

## Done when

- [ ] All required E2E scenarios pass in one suite.
- [ ] All required model/protocol tests pass.
- [ ] UI checklist verified.
- [ ] Phase 1 acceptance recorded in [../TODO.md](../TODO.md).
