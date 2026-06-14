# Step 13 — Command execution & read-only command log

> Phase 2 · Commands
> Design context: [docs/design.md](../../design.md) §3, §5.8

## Goal

Execute commands inside the session container through the scoped capability, stream output
into the event log, and render a read-only command log in the client UI. Command output is
event-log-derived; the terminal is read-only (no interactive terminal in Phase 2).

## Prerequisites

- [Step 12 — Lazy environment lifecycle](12-lazy-environment-lifecycle.md)
- [Step 11 — Scoped environment capability](11-scoped-environment-capability.md)

## Scope

**In scope**

- The agent capability to execute a command, exposed as a typed function.
- Streaming stdout/stderr chunks into command events, preserving per-command ordering.
- Command lifecycle events (requested, started, output received, completed).
- A read-only command log in the client UI (status, stdout/stderr, exit result).

**Out of scope**

- Interactive terminal, commit/push, repo clone (later phases).

## Schemas & interfaces introduced

```fsharp
type CommandId = private CommandId of string

type CommandRequest =
    { CommandId        : CommandId
      Executable       : string
      Arguments        : string list
      WorkingDirectory : string option
      Environment      : Map<string, string>
      Timeout          : TimeSpan option }

type CommandOutputChunk = { CommandId : CommandId; Stream : OutputStream; Text : string }
and  OutputStream       = Stdout | Stderr

type CommandExecution =
    { Output     : AsyncObservable<CommandOutputChunk>
      Completion : Async<CommandResult> }

type CommandResult =
    | CommandSucceeded       of exitCode: int
    | CommandFailed          of exitCode: int
    | CommandTimedOut
    | CommandExecutionFailed of reason: string

// Agent-facing capability (typed; routed through the scoped container capability):
type ExecuteCommand = command: CommandRequest -> Async<CommandResult>

// SessionEvent cases added this step:
type CommandRequested      = { CommandId : CommandId; Executable : string; Arguments : string list }
type CommandStarted        = { CommandId : CommandId }
type CommandOutputReceived = { CommandId : CommandId; Stream : OutputStream; Text : string }
type CommandCompleted      = { CommandId : CommandId; Result : CommandResult }
```

Contract:

- Output ordering is preserved per command.
- Command output in the client UI is read-only and derived only from events.
- Execution flows through `ExecuteInSessionContainer`
  ([Step 11](11-scoped-environment-capability.md)) — never raw Docker.

## Work outcome

- The agent can run a command in the session container and the session sees a streamed,
  ordered, read-only command log.
- Exit status and failures are represented as events.

## Verification

- **E2E-3:** command execution appends `CommandStarted`, `CommandOutputReceived`,
  `CommandCompleted`.
- **E2E-4:** browser clients see the command log through event pages.
- Integration test: command output is streamed into the event log.
- Model test: command output ordering is preserved per command.

## Done when

- [ ] `ExecuteCommand` streams output into command events.
- [ ] Read-only command log renders from events.
- [ ] E2E-3, E2E-4 and ordering tests pass.
