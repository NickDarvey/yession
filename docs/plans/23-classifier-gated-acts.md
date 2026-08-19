# Plan 23 — the classifier gates every act

Manual approval is removed outright — hard cut, no migrations — and replaced by one seam:

```fsharp
type ProposedAct =
    | TerminalAct of terminal: TerminalId * command: string
    | CommandAct of tool: string * args: string * summary: string

type Classification = Approved | Rejected of reason: string

type Classifier = ActorRef -> ProposedAct -> Async<Classification>
```

Every terminal block and every structured command passes a `Classifier` on its way to
happening. The shipped implementation is `Classifier.approveAll`, the bypass; an AI-driven
classifier is the intended future implementation of the same seam. There is no per-subject
mode, no verdict register, no grace, and no manual path — a person who wants a queued
command not to run withdraws it, which was always a different act from refusing it.

## What was removed

[Plan 13](13-worksandbox-terminals.md) built approval for terminals and
[Plan 15](15-imperative-session-api.md) generalized it: `ApprovalMode`
(`AutoRun | ApproveAgent | ApproveAll`) in a synced `Gates` register keyed by `GateSubject`,
verdict registers (`ApprovedBy`/`RejectedBy`/`RejectedReason`) on every queued act,
Approve/Hold/Reject controls at two mount points, a per-terminal mode select, a settings
panel over the gated-command catalogue, `YESSION_GATED_COMMANDS`, a five-second approval
grace bounding how long a turn waited on a person, `TerminalCommandAwaitingApproval` and
`CommandAwaitingApproval`, the approver's identity on `Authority` and five repo/sandbox
events, and structured-command parking itself — a `CommandCall` act in the pending map was
only ever a parked call awaiting a verdict, so the parking went with the verdicts and
`PendingAct` simplified to what it always was in practice: a command line queued for a
terminal.

## What survived, and why

- **The queue.** Visibility, in-place editing, reordering and withdrawal are not approval —
  they are the one-door property's value: what the agent is about to run is something
  people can read and fix. `execute_command` is still the agent's only execution path, and
  `write_terminal` still refuses raw bytes into an instrumented terminal it does not hold,
  because that would be the door around the classifier.
- **Rejection, end to end.** A classifier reject appends the same `TerminalCommandRejected`
  / `CommandRefused` events the manual gate appended, attributed to `System`, with the
  reason; the agent reads REFUSED rather than a silence it would retry another way; the
  timeline chip and conversation note render it. The AI classifier's whole reporting path
  exists before the AI classifier does.
- **Exactly-once anchoring.** `TerminalCommandRejected` still joins the consumed set beside
  `TerminalBlockStarted`, so a rejection consumes an entry the way a start does, and old
  logs' rejections still seed the set on replay.
- **The work deadline.** `commandTimeout` bounds waiting on WORK — a slow command yields
  `CommandRunning`/`TerminalCommandRunning` with a handle `check_pending` resumes. The
  grace died because the wait it bounded (a person) no longer exists.

## Where the classifier is asked

Inside the act, not at the composition root, and not in the drain driver:

- **Terminal blocks**: inside `SessionTerminals.RunBlock`, after the terminal is marked
  busy and before anything durable is written. The order is load-bearing — an async
  decision taken while the terminal still looked free would let a re-entrant drain start
  the entry BEHIND the head first, reordering execution, which the drain module's header
  promises never to do. The text classified is the text as it stands at the drain, because
  the queue is editable until that moment.
- **Structured commands**: in `CommandGates.run`, before the dispatch-table lookup.
  Synchronous from the caller's point of view; nothing parks.

The composition root (`app/Host.fs`) supplies `Classifier.approveAll` and computes nothing.

## Hard-cut consequences, recorded

- With the bypass, nothing stands between an agent turn and any command except the work
  sandbox's confinement (GAPS.md carries this as the standing gap).
- A terminal command that was parked awaiting approval before the upgrade RUNS on the next
  drain — the classifier approves what a person was still deciding on.
- A structured command parked before the upgrade is DROPPED at decode, not run: carrying
  out an act nobody released is the one direction the cut must not fail in.
- Old event logs replay: the retired keys (`approvedBy` on `terminalBlockStarted` and the
  repo/sandbox events) are ignored by decoders that stopped asking, pinned by
  literal-JSON tests, and the rejection events kept their decode arms. Old Yjs docs keep
  their retired `gates` root and verdict fields as unread garbage — sweeping them would be
  a migration, and there are none.
- `ApproveAll` is gone too: nobody's act waits for anybody. The wire form of a pending
  entry keeps its `terminal:<id>` subject key, so persisted docs and pre-upgrade browser
  tabs keep reading, while the F# shape names the `TerminalId` directly.

## Delivery

Shipped as four PRs, one per independently green stage: the seam plus the
structured-command cut; the terminal cut; the `ApprovedBy` identity cleanup (encode and
decode of `Authority` moved together — the decoder REQUIRED the key); and this shape
simplification with the docs.
