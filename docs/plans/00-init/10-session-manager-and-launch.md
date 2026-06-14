# Step 10 — Session Manager & Session Process launch

> Phase 2 · Authority boundary
> Design context: [docs/design.md](../../design.md) §3 "Authority model"

## Goal

Introduce the Session Manager process as the authority that launches Session Processes.
This establishes the boundary that later steps use to delegate scoped, non-ambient
capabilities. The Process no longer self-starts with host authority.

## Prerequisites

- Phase 1 accepted ([Step 09](09-phase-1-e2e-acceptance.md)).

## Scope

**In scope**

- A Session Manager process that creates a session and launches a Session Process.
- Session Process registration back to the Manager.
- Local bootstrap URI returned to the caller for the launched session.

**Out of scope**

- Environment/container capabilities (Step 11) and lifecycle (Step 12).
- The Manager does not yet grant Docker authority — only launch + registration.

## Schemas & interfaces introduced

```fsharp
type SessionLaunchRequest =
    { SessionId    : SessionId
      SessionToken : string }

type SessionLaunchResult =
    { SessionId         : SessionId
      ProcessId         : string
      LocalBootstrapUri : Uri }

type StartSession =
    request: SessionLaunchRequest ->
    Async<SessionLaunchResult>
```

Contract:

- The Manager owns process launch; the Process is started by the Manager, not directly.
- The launched Process registers with the Manager and exposes its local bootstrap URI.
- The Phase 1 Elmish/Ylmish/WebRTC/event-log split is preserved unchanged.

## Work outcome

- Starting the Manager and creating a session launches a working Session Process.
- A browser can reach the launched session via its bootstrap URI and behave exactly as in
  Phase 1.

## Verification

- Integration test: the Manager launches a Session Process and receives its registration.
- Integration test: `StartSession` returns a reachable `LocalBootstrapUri`.
- Regression: the Phase 1 E2E suite still passes against a Manager-launched Process.

## Done when

- [ ] Manager launches and registers a Session Process.
- [ ] `StartSession` returns a usable bootstrap URI.
- [ ] Phase 1 suite passes under Manager-launched topology.
