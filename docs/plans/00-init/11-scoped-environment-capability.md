# Step 11 — Scoped environment capability & container handles

> Phase 2 · Capability delegation
> Design context: [docs/design.md](../../design.md) §3, §5 "Capabilities are scoped, not ambient"

## Goal

Grant the Session Process environment capabilities that are **already scoped to its
session**. The Process can start/stop/exec only for its own session and cannot forge
handles or pass arbitrary `SessionId`s. This step delivers the capability surface and its
enforcement, without yet wiring lazy lifecycle behaviour (Step 12).

## Prerequisites

- [Step 10 — Session Manager & Session Process launch](10-session-manager-and-launch.md)

## Scope

**In scope**

- Scoped capability functions handed to the Process at launch.
- Unforgeable / Manager-validated container handles.
- The future-compatible `EnvironmentSpec` (a minimal built-in spec is acceptable).
- Manager-side enforcement of session/container ownership.

**Out of scope**

- Deciding *when* to start an environment (Step 12).
- Command execution streaming into events (Step 13) — `Execute*` signature is defined
  here; event wiring lands in Step 13.

## Schemas & interfaces introduced

```fsharp
// Capabilities are pre-scoped to the session; no SessionId parameter is accepted.
type StartSessionContainer    = spec: EnvironmentSpec -> Async<StartContainerResult>
type StopSessionContainer     = handle: ContainerHandle -> Async<StopContainerResult>
type ExecuteInSessionContainer = handle: ContainerHandle -> command: CommandRequest -> Async<CommandExecution>

type ContainerHandle =
    private { SessionId : SessionId; ContainerId : string }

type StartContainerResult =
    | ContainerStarted     of ContainerHandle
    | ContainerStartFailed of reason: string

type StopContainerResult =
    | ContainerStopped
    | ContainerStopFailed of reason: string

type EnvironmentSpec =
    { Kind                 : EnvironmentKind
      WorkingDirectory     : string option
      Image                : ContainerImage option
      Build                : ContainerBuildSpec option
      Mounts               : ContainerMount list
      EnvironmentVariables : Map<string, EnvironmentVariableRef> }

and EnvironmentKind = Docker
and ContainerImage  = { Name : string; Tag : string option }
and ContainerBuildSpec = { ContextPath : string; DockerfilePath : string option }
and ContainerMount  = { Source : MountSource; Target : string; Mode : MountMode }
and MountSource     = HostPath of string | NamedVolume of string | SessionWorkspace
and MountMode       = ReadOnly | ReadWrite
and EnvironmentVariableRef = PlainValue of string | SecretRef of SecretName
and SecretName      = private SecretName of string
```

`CommandRequest` / `CommandExecution` are defined in
[Step 13](13-command-execution-and-log.md).

Enforcement contract (per [design.md](../../design.md) §3 and §5.9):

- A Process can start/exec only for its own session.
- A Process cannot enumerate unrelated containers.
- A Process cannot pass arbitrary `SessionId`s.
- Handles are unforgeable or validated by the Manager.

## Work outcome

- The Process holds typed, session-scoped capabilities — never raw Docker access.
- The Manager rejects any cross-session or forged-handle operation.

## Verification

- Integration test: `StartContainer` creates a session-owned container.
- Integration test: `Exec` validates container ownership.
- **E2E (authority):** a Process cannot exec in another session's container.
- **E2E (authority):** a Process cannot exec with an invalid/forged handle.

## Done when

- [ ] Scoped capabilities delivered to the Process at launch.
- [ ] Ownership/forgery enforcement verified by tests.
- [ ] No ambient Docker authority exists in the Process.
