# Step 12 — Lazy environment lifecycle

> Phase 2 · Environment lifecycle
> Design context: [docs/design.md](../../design.md) §3 "Environments start lazily"

## Goal

Start environments **lazily**: a one-shot conversational answer must not start a
container, while a task requiring code/command access causes the agent to signal need and
the Session Process to start an environment through its scoped capability. Lifecycle is
recorded as events.

## Prerequisites

- [Step 11 — Scoped environment capability](11-scoped-environment-capability.md)
- [Step 08 — Claude Code SDK agent turn](08-claude-agent-turn.md)

## Scope

**In scope**

- The agent capability to ensure an environment exists, exposed as a typed function (not
  raw Docker).
- Session Process logic that, on agent need, requests start via the scoped capability.
- Environment lifecycle events (need identified, start requested/started/failed, stop
  requested/stopped).
- A product-authored system prompt that teaches the agent when to start an environment.

**Out of scope**

- Command execution + output (Step 13).
- Repo clone, `.yession.yml`, commits/pushes (later phases).

## Schemas & interfaces introduced

```fsharp
// Agent-facing capability (typed; no raw Docker access):
type EnsureEnvironment = reason: string -> Async<EnsureEnvironmentResult>
and  EnsureEnvironmentResult = EnvironmentAvailable | EnvironmentUnavailable of reason: string

// SessionEvent cases added this step:
type EnvironmentNeedIdentified = { Reason : string; AgentTurnId : AgentTurnId option }
type EnvironmentStartRequested = { EnvironmentId : string; SpecSummary : string }
type EnvironmentStarted        = { EnvironmentId : string; ContainerRef : string }
type EnvironmentStartFailed    = { EnvironmentId : string; Reason : string }
type EnvironmentStopRequested  = { EnvironmentId : string }
type EnvironmentStopped        = { EnvironmentId : string }
```

Lifecycle (per [design.md](../../design.md) §3):

```text
One-shot:    human asks a question -> agent answers from context -> no environment.
Dev task:    agent indicates need
             -> append EnvironmentNeedIdentified
             -> StartSessionContainer (scoped capability)
             -> Manager starts a session-owned container
             -> append EnvironmentStarted
```

System prompt must communicate (product-authored, not mechanical):

```text
You are participating in a collaborative engineering session.
You may answer conversationally without starting an environment.
Start an environment only when repository or command execution is needed.
Use command execution deliberately.
Prefer high-signal investigation over noisy exploration.
Explain meaningful progress to the session.
```

## Work outcome

- Conversational questions never start a container.
- A development task drives `EnvironmentNeedIdentified` → started container, all recorded
  as events.

## Verification

- **E2E-1:** a conversational one-shot does not start an environment.
- **E2E-2:** a development task causes `EnvironmentNeedIdentified` and starts an
  environment.
- Model test: environment events project deterministically into UI state.

## Done when

- [ ] `EnsureEnvironment` capability + system prompt drive lazy start.
- [ ] Lifecycle events appended and projected.
- [ ] E2E-1 and E2E-2 pass.
