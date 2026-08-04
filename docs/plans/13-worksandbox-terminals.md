# Plan 13 — Terminals on the WorkSandbox

> **Status: PR 1 of 3 implemented** (blocks, no pty). PRs 2 (pty + live mode) and 3
> (polish) remain. Builds directly on the sandbox seam from
> [PR #73](https://github.com/NickDarvey/yession/pull/73) (`CreateSandbox`,
> session-owned WorkSandbox, `SandboxProcessHandle` with piped stdin).
>
> Deviations taken while implementing PR 1 are recorded in
> [What PR 1 actually shipped](#what-pr-1-actually-shipped) at the foot of this document.

Humans and the agent get zero-to-many terminals against the session's WorkSandbox, on a
new right panel that behaves like the conversation column: everyone sees everyone's
drafts (the agent's included), presence cursors work, and approval — when the terminal's
mode asks for it — happens on the queued command before it runs. Every byte in and out
is captured durably. Full-screen interactive programs (TUIs) are supported without
giving up the collaborative default.

## A "collaborative terminal" is three things, not one

The mode question ("simple-but-collab vs complex-but-solo") dissolves once the surface
is split into what it actually is:

1. **The input draft** — the command about to run. This is *exactly* a message draft:
   collaborative text in the Yjs doc, one slot per author, co-editable, visible to every
   peer as it is typed, sent by moving it into a queue. The existing machinery
   (`DraftState`, `QueueOrder`, `BodyKey`, the drain's exactly-once dedup) transfers
   almost verbatim.
2. **The output stream** — server-authoritative, append-only, unmergeable. Two peers
   cannot "concurrently edit" a process's stdout; there is nothing for a CRDT to do.
   Output is broadcast state plus durable record, never Yjs.
3. **Live keystrokes into a foreground program** — vim, htop, a REPL's line editor. No
   draft exists, no approval is possible mid-keystroke, and interleaving two peers'
   keystrokes is how tmux corrupts a shared editor. Single-writer by nature.

So a terminal has two **modes**, and the flip is not "collab vs solo" but **what
currently owns the pty's stdin**:

- **Block mode** (the shell prompt is idle): the composer owns stdin. Commands are
  drafted collaboratively, queued, approved per the terminal's policy, and executed by
  the Session Process; each run is a *block* — the command, its output, its exit code —
  in the style of Warp or a Jupyter cell. This is the default, and it is where the
  message-like UX lives.
- **Live mode** (a foreground program owns the terminal): one peer holds a **write
  lease**; their keystrokes go straight to the pty, everyone else watches read-only.
  Claim, release, steal (any peer may take the lease — collaborators are trusted; the
  event log records the take), idle-timeout back to block mode. This is GNU screen's
  `multiuser` ACL model, not tmux's merged-keystroke free-for-all.

**The flip is detected, not configured.** Alternate-screen entry (DECSET 1049) is the
universal "a TUI took over" signal, and OSC 133 semantic-prompt marks (emitted by an
instrumented shell; superset dialect used by VS Code, Warp, WezTerm, kitty) give command
start/finish + exit code from an ordinary interactive shell. Detection proposes the
mode; a peer can always override ("take terminal" enters live mode explicitly, releasing
the lease returns to block mode). Mechanism supports both automatic and explicit
flipping; the automatic policy is one small pure function over the emulator's state, so
shipping it, tuning it, or turning it off is cheap.

## Transport: the data channel, not SSE

HTTP SSE-out/POST-in was considered and rejected. `design.md` §5 pins "WebRTC is the
session transport; HTTP is bootstrap/signalling only", and the practical objections are
real: N terminals × M tabs of `EventSource` runs into the browser's 6-connections-per-
origin cap on HTTP/1.1 (local serving has no TLS, so no h2 multiplexing), and every
byte takes a server round-trip where the data channel is already direct, ordered,
reliable, and multiplexed. Terminals add **frames to the existing channel**, exactly as
presence did:

```
SessionFrame gains:
  | Terminal of TerminalFrame

TerminalFrame =
  | TerminalOutput  of TerminalId * seq: int * data: string   // base64 raw bytes
  | TerminalInput   of TerminalId * data: string              // live mode only; lease-checked
  | TerminalResize  of TerminalId * cols: int * rows: int     // live mode; lease holder only
  | TerminalSnapshot of TerminalId * seq: int * screen: string // serialized screen for joiners
```

The one HTTP exception stays the one the design already grants — **immutable,
cacheable history chunks**. Each terminal's transcript is served as fixed-size chunks
(`GET <session>/terminals/{id}/chunks/{n}`), byte-identical in mechanism to
`GET /events/{n}`: full chunks are immutable forever and cache with
`public, immutable`; the tail chunk is `no-store`. Availability hints ride the data
channel; replay and audit reads ride the browser's HTTP cache. Nothing new is invented.

## Durable capture: raw bytes in a sidecar, facts in the event log

Two records, deliberately separate:

**The transcript sidecar** — `<session>.term-<id>.cast`, following the doc-persistence
sidecar precedent (`<session>.doc.jsonl`: append + fsync, torn-tail drop, loud
corruption failure). Format is **asciicast v2**: a JSONL header
(`{version:2, width, height, timestamp}`) then `[t, "o", data]` / `[t, "i", data]` /
`[t, "r", "COLSxROWS"]` events. This buys an existing, well-understood, replayable
audit format — the standard asciinema player replays a session — instead of a bespoke
one. "Every line is captured" is delivered as **every byte is captured**, input
included, which matters because ANSI can rewrite what the *screen* shows: the rendered
buffer is a projection and must never be the audit trail. The raw stream is.

**The main event log** — durable facts only, never raw output. A `yes`-loop's megabytes
must not poison the event fold every client runs, and the session log's chunk cache
must not churn under terminal noise. New `SessionEvent` cases:

```
| TerminalOpened     of { TerminalId; OpenedBy: ActorRef; Cols; Rows }
| TerminalClosed     of { TerminalId; Reason }
| TerminalLeaseTaken of { TerminalId; By: ActorRef }        // live mode entered/stolen
| TerminalLeaseReleased of { TerminalId }                    // back to block mode
| TerminalBlockStarted  of { TerminalId; BlockId; QueueId option; Author: ActorRef; Command: string }
| TerminalBlockCompleted of { TerminalId; BlockId; ExitCode: int option }
| TerminalTranscriptMark of { TerminalId; Seq: int }         // periodic offset bracket
| TerminalTranscriptTruncated of { TerminalId; DroppedBytes: int }
```

Attribution joins at projection time: the log's lease/block events bracket transcript
sequence ranges (via `TerminalTranscriptMark` and the block events' positions), so
"who typed these bytes" is answerable from the two records together without extending
the asciicast format. Backpressure is explicit: output is coalesced (~16 ms flush
windows) before framing and appending, and a hard cap drops output *with a
`TerminalTranscriptTruncated` event* — never silently.

## The pty is a backend capability on the sandbox seam

Pipes are not a tty: no `TERM` line discipline, no SIGWINCH, and most TUIs refuse or
degrade. `Sandbox` (the PR #73 seam) gains a second spawn shape:

```
type PtyHandle =
    { Write  : string -> unit          // raw bytes to the pty master
      Resize : int -> int -> unit      // cols rows
      Kill   : unit -> unit
      Exited : Async<SandboxRun> }

type Sandbox = { ...; SpawnPty : (SandboxExec * cols * rows -> (string -> unit) -> Async<Result<PtyHandle, string>>) option }
```

`SpawnPty` is an **option** because pty support is genuinely per-backend:

- **docker** — free. `docker exec` with `Tty: true` plus the exec-resize endpoint, both
  already in dockerode. No new dependency.
- **host** — needs `node-pty` (the only serious Node pty; native addon). It joins the
  Nix `nodeModules` derivation the way `node-datachannel` did (built from source, baked
  in). Off-Nix, where the addon may be absent, `SpawnPty` is `None`.
- **srt** — wraps the host spawn, so it inherits the host's answer.

A backend without a pty is degraded, not broken: **block mode runs over the existing
piped `Spawn`** (that is precisely today's `Execute` path), and only live mode reports
"unavailable on this backend" — the same declare-and-skip honesty the capability-tagged
test tiers use. Tests gain a `Pty` capability tag, probed like `Docker`
(present under Nix, dropped cleanly elsewhere).

## The Session Process holds the authoritative screen

Each open terminal gets an `@xterm/headless` emulator in the Session Process — the same
terminal emulator the browser renders with, minus the DOM. It is fed every output byte
and is the single source of three derived truths:

- **Join/reconnect snapshots.** A joining peer receives `TerminalSnapshot` (the
  serialize-addon dump of the current screen + scrollback tail, tagged with the
  transcript seq it represents) and then live `TerminalOutput` frames after that seq —
  the terminal equivalent of the event-offset catch-up, so a reconnect never replays
  megabytes.
- **Mode detection.** Alt-screen state and OSC 133 marks are read off the emulator, and
  the block/live flip is a pure function of them.
- **Block boundaries.** OSC 133 `C`/`D` marks (or, for Process-spawned block commands,
  the process lifecycle itself) delimit each block's output range and exit code.

Determinism is testable: folding a transcript through a fresh headless emulator must
reproduce the serialized snapshot — a property the cheap tier can pin without any pty.

**Size policy** (easy to fumble, so decided here): a terminal's size is a synced LWW
register, default 80×24; in live mode the lease holder's resize writes it (their
foreground program is the one that must agree with the pty); in block mode any peer may
set it. Viewers whose viewport is smaller render with xterm.js scrolling — the pty is
never resized to the smallest viewer (tmux's worst inheritance).

## Drafts, queue, and approval: the message machinery, re-keyed

The synced state gains terminal composers and a per-terminal command queue, shaped
exactly like drafts and the message queue:

- **Composer body**: one per (terminal, author), a `Y.Text` root under
  `BodyKey.terminalDraft terminalId author` — commands are plain text, so `Y.Text`
  (the title's type), not the rich-text `XmlFragment`. Slot publication follows the
  body, the `DraftSlot` rule verbatim.
- **Presence**: `FocusField` gains `TerminalDraft of TerminalId * PeerId`, and every
  collaborator's caret shows in terminal composers exactly as it does in message
  drafts. **The agent drafts through the same slot**: its tool writes the command text
  into its own composer body before queueing, so peers watch the agent type where they
  watch each other type.
- **Queue entry**: `{ QueueId; Author; Order: float; Approval }` in a per-terminal map.
  The minted-`QueueId` merge trick carries over, so two peers concurrently sending the
  same draft still produce one entry.
- **Approval is a CRDT register on the entry**, and the terminal's mode is a synced
  register deciding who needs it:

  ```
  TerminalApprovalMode = AutoRun | ApproveAgent | ApproveAll   (default ApproveAgent)
  Approval             = AutoApproved | AwaitingApproval | Approved of ActorRef
  ```

  The Session Process's drain (the queue's single consumer, same exactly-once anchor in
  the log) consumes an entry only when its approval state satisfies the mode; an
  awaiting entry simply sits, visible, editable, reorderable — which *is* the approval
  UX: the human reads the agent's queued command, perhaps edits it, and approves it in
  place.

### Rejection is an answer, not a deletion (PR 2)

PR 1 made rejection a CRDT delete, by symmetry with deleting a queued message. The
symmetry is false. A queued message is your own text and deleting it is a withdrawal; a
queued *command* is frequently the agent's, and refusing it is the review gate doing the
one thing it exists for. Deletion leaves no trace of that: the entry is simply gone, and
a log that records every approval records no refusal. "The agent proposed this and a
human said no" is the more interesting half, and it is the half currently thrown away.

So rejection becomes an explicit answer, on the same rails as approval:

```
| TerminalCommandRejected of
    { TerminalId ; QueueId
      Author     : ActorRef        // whose command it was
      RejectedBy : ActorRef        // who said no
      Command    : string          // snapshotted — the queue entry is about to vanish
      Reason     : string option }
```

**Doc proposes, drain disposes, log records** — the shape the rest of this design already
has. A peer writes `RejectedBy` on the entry, a register beside `Approval`, so it merges
and survives a disconnect exactly as an approval does and every peer sees *who* refused
before the entry goes. The drain observes it, appends the event, removes the entry.
Rejection is deliberately NOT a `SessionCommand`: a command frame from a peer that drops
mid-flight is lost, and the log stays the Session Process's alone to write.

The command text is snapshotted into the event for the same reason the drain snapshots it
at consumption — the doc entry is deleted immediately after, and a record saying
*something* was rejected is not a record.

**The race is settled where every other one here is: in the log.** Under `AutoRun` a human
can hit reject in the same tick the drain takes the entry. Neither side needs a lock,
because `TerminalQueueDrain.consumedOf` already folds `TerminalBlockStarted` into the
consumed set and rejection simply joins it — whichever event reaches the append-only log
first wins, and the second is dropped as already consumed. The Session Process is the log's
only writer, so check-and-append is serial there by construction. A rejected `QueueId` can
therefore never run afterwards, and a started one can never be retro-rejected: stopping
something already running is `kill`, a different verb with a different event.

`TerminalQueueDrain.plan` gains `Rejections` beside `Ready`/`Removals`, and a rejected
entry is never `Ready` under any mode — the rejection check precedes the mode gate,
because a refusal outranks a policy that would otherwise have auto-run it.

**A refusal is visible, not merely recorded.** The projection folds it into the terminal's
block list as an entry with `Status = Rejected (by, reason)` and no transcript range, so
the terminal reads *"agent: `rm -rf /` — rejected by nick"* in line with the commands that
did run. That mints a `BlockId` for something which never spawned, and the widening is
deliberate: a `BlockId` names **a proposed command and its outcome**, not a process. The
alternative — a parallel list merged by timestamp in the view — is worse in every way that
matters. Without it the entry just disappears from everyone's screen, which is
indistinguishable from a bug.

Who may reject: any peer, per the terminal-access-equals-session-access posture below; the
event records which. The agent may not — it proposes, humans dispose. An agent withdrawing
its *own* queued command is coherent, and a different action.

### A leased terminal holds its queue (PR 2)

Live mode is the pty's stdin belonging to one peer. A drain that fired anyway would type
a queued command into a terminal somebody is already typing into — which is precisely the
merged-keystroke corruption this design rejected tmux for on page one. So the lease is a
gate on the drain, and PR 2 cannot ship without it: it is the correctness hole PR 2 opens
by introducing leases at all. Today `TerminalQueueDrain.plan` knows `busy`, `isOpen` and
the mode, and nothing about who holds the terminal.

The case is narrower than "the terminal is in live mode", and the narrowing matters. A
block that *becomes* live — the drain runs `vim`, alt-screen entry flips the terminal, a
peer holds the lease for the editor's lifetime — is already covered, because that terminal
is `busy` with a running block. The gap is a terminal that is live with **no block
running**: someone pressed "take terminal" and is typing at the shell. `busy` is false, the
terminal is open, an approved entry is sitting there, and nothing stops the drain.

So `plan` takes the holder alongside the rest, and the gates order:

```
rejected      -> Rejections   (before everything; a refusal outranks any policy)
consumed      -> Removals     (log-anchored repair)
closed        -> nothing
busy          -> skip         (one block per terminal)
leased        -> hold         (someone owns stdin)
approval/mode -> hold or Ready
```

Rejection sits above the lease gate as well as the mode gate: refusing a command touches
no pty, so a leased terminal is irrelevant to it. Someone can clear out a bad queue while
a colleague is inside vim.

**Release re-arms the drain**, exactly as block completion already does (the `drain ()`
call after `RunBlock` resolves, which is what lets a terminal's next command start
immediately). `TerminalLeaseReleased` gets the same treatment, so handing the terminal
back starts whatever was waiting for it.

**Held-for-a-terminal is not held-for-approval**, and the two must never render or report
as one. "Nick is using this terminal" resolves when a person finishes a task; "waiting for
approval" resolves when a person makes a decision. A queue that says only *pending* leaves
both looking like a stall. The composer shows the holder and a steal control — any peer may
take the lease, which is already how leases work here — and the PR 3 tool reports
`AwaitingTerminal` distinctly from `AwaitingApproval`.

That distinction also settles the tool's wait: the `approvalGrace` does **not** apply to a
leased terminal. The grace exists because a supervised approval often lands in seconds; a
peer with a terminal open is mid-task and will not be done in five. `execute_command`
returns `AwaitingTerminal` at once rather than burning the grace on a wait that was never
going to resolve.

**Starvation is bounded by the idle-lease timeout** (PR 3), and until it lands a lease held
indefinitely does starve its queue. That is acceptable only because it is *visible* — the
composer names the holder — and because any peer can steal the lease. An invisible hold
would not be.

Opening and closing terminals are durable facts the CRDT cannot express, so they are
`SessionCommand`s (`OpenTerminal`, `CloseTerminal`, plus `TakeTerminalLease` /
`ReleaseTerminalLease`), answered by the Process and recorded as events. The open
terminal list every client renders is a pure fold over `TerminalOpened`/`Closed`. An
`OpenTerminal` triggers the environment's lazy `Ensure` — a terminal is a need, and a
session that never opens one never starts a sandbox.

## The agent on the same surface

The agent gets one tool (named in "Closing the loophole" below — it shipped as
`queue_terminal_command` and ends up as `execute_command`), which drafts into
its composer slot and queues — it does **not** get a private execution path. In
`ApproveAgent`/`ApproveAll` modes its entry waits for a human; in `AutoRun` it drains
immediately. Live mode is human-only for now (an agent lease is a policy decision, not
a mechanism gap; recorded in GAPS).

This also unifies a seam PR #73 left visible: the Step-13 command log
(`CommandRequested/…/CommandCompleted`, the read-only sidebar section) and terminals
would otherwise be two parallel command surfaces. The agent's existing `ExecuteCommand`
capability is re-pointed at a designated terminal ("agent terminal", opened lazily on
first use) so its commands become ordinary blocks there; the old sidebar commands
section retires once nothing feeds it. One surface, one audit trail.

## Closing the loophole: one `execute_command`, on the terminal

PR 1 shipped the terminal tool *beside* `execute_command` rather than instead of it, and
that split is worse than either end of it. `execute_command` is synchronous, returns exit
code and output, and can be chained — run, read, decide, run again. `queue_terminal_command`
is visible, editable and approvable, and returns before anything has happened. So the
gated path is the weak one, and a model that needs an answer will take the path that gives
it one. The tool description says to prefer the terminal "whenever a human should see what
you are about to run", but prose does not beat a capability gradient.

The consequence is worse than a nudge being ignored: **`ApproveAgent` is currently
unenforceable.** A session can set every terminal to require approval and the agent
routes around it, because it has a second door with no gate on it. The gate only becomes
real when there is one door.

So the two converge into one tool, keeping the name that describes what it does:

```
execute_command(command, terminal?) -> outcome
read_terminal_block(block)          -> outcome
```

Both return the same shape, so the agent learns one thing. `queue_terminal_command` and
the old `execute_command` both go; net tool count is unchanged.

### The wait is two waits, not one

The reason "queue and return" looked forced is that it answered one question — *how do we
avoid blocking a turn on a human?* — by giving up a different thing: waiting for a
*process*, which was never the problem. Those are separate waits with separate bounds, and
separating them is the whole design:

- **Waiting for approval is unbounded in principle.** A human may be asleep. It gets a
  short grace (`approvalGrace`, 5s) so that a supervised session — where approvals come in
  seconds — still chains normally, and then the tool returns `AwaitingApproval` with the
  block id. The turn continues; the agent says what it queued and why.
- **Waiting for a process is already bounded**, by the same 120s command timeout
  `execute_command` uses today. Keeping it is not a new risk, it is the existing one.

So the shape is: enqueue (visible immediately) → await approval up to `approvalGrace` →
await completion up to the command timeout → return exit code and output. Under `AutoRun`
the first wait does not exist: the drain subscribes to the doc, the agent's write is local
to the Session Process, and the entry drains on the update. **`AutoRun` is synchronous, at
the cost of one in-process doc round trip** — which is the "same perf" bar, met by not
having a network hop in the path rather than by hiding one.

A command still running when the timeout fires returns `Running` with the block id and the
output so far. `read_terminal_block` resumes any handle — an approval that arrived late, a
long build — and returns the outcome or the current state. Nothing is lost by a deadline;
it is a yield, not a cancellation.

### What the outcome carries

Exit code, and the output tail (capped — the block's full bytes are in the transcript,
which is what the cap on `blockOutputCap` already protects), plus the block id and its
transcript range so `read_terminal_block` can fetch the rest. The status is one of
`Completed code | Running block | AwaitingApproval block | Rejected block`, and the wording
must keep saying which — telling a model "queued" when it is actually blocked on a person
has it conclude, after a silent pause, that the command failed.

**`command` becomes a shell command line, not `executable` + argv.** Today's
`execute_command` takes an argv array. A terminal block is a line a human reads in a queue
and may edit before approving, and an argv array is not that. The quoting burden moves to
the model, which is the side that knows what it meant.

### Two things this fixes that are not about tools

**The agent gets its output back.** Terminal events fold into `TerminalProjection` and
deliberately *not* into the conversation — a command someone ran is not something someone
said. That is right for the chat log and wrong for the agent, whose context pack is built
from the conversation, so today it cannot see the result of anything it queued even on the
next turn. `AgentContextPack` gains a separate, bounded terminal digest: for each block
since the agent's last turn, the command, who authored and approved it, the exit code, and
an output tail. Separate field, so the conversation stays a conversation.

**A block outlives its turn.** Today's `execute_command` runs a process owned by the turn;
an interrupt kills it. A terminal block is owned by the session. Aborting a turn cancels
the *wait*, not the command — the block runs on, its output lands in the transcript, and
the next turn's digest reports it. An audit trail with holes where someone pressed stop is
not an audit trail.

### `ensure_environment` retires with it

It exists to start the environment lazily before a command. Opening a terminal already
ensures the environment, and `execute_command` opens the agent terminal on first use, so
the tool has nothing left to do. Its `reason` argument was the one genuinely useful thing
about it, and it survives as the **agent terminal's title** — so the strip says *"running
the test suite"* rather than *"agent"*, which is a better answer to "what is that terminal
for" than the tool ever gave.

### The default is a policy decision, and it is stated here

Terminals default to `ApproveAgent`. If the agent terminal inherited that, replacing the
tool would silently turn every agent command into an approval prompt — a large change to
autonomy smuggled in under a refactor. So **the agent terminal opens in the session's agent
policy, which defaults to `AutoRun`**: identical to today's behaviour, because a tool
replacement should not change what the agent may do.

What changes is that the gate becomes real. Setting `ApproveAgent` today does nothing;
after this it stops the agent, because there is no second door. And the asymmetry is
useful in itself: the agent's own scratch terminal is auto, while a terminal a *human*
opened keeps its own mode, so an agent asked to run something in your terminal is gated by
your terminal's policy.

**Secrets posture, stated honestly:** resolve-at-spawn puts the sandbox's env in reach
of anyone who can run `env` in a terminal. This is not a new privilege — the agent
could already be asked to run `env`, and any peer can already ask the agent — but a
terminal makes it one keystroke instead of one prompt. Terminal access therefore equals
session access (no separate gate), and the mitigation stays where Plan 06 put it:
sensitive values reach the sandbox only when the spec references them. Recorded in
GAPS as a place a future per-user terminal gate could attach.

## The right panel

A third column, the conversation column's mirror: a terminal strip (tabs or list, one
per open terminal, plus "new terminal"), and per terminal an xterm.js viewport
(`@xterm/xterm` + fit/serialize addons — the ProseMirror-style Fable binding is the
established pattern) over the composer/queue area. In block mode the area under the
viewport is the composer row (every peer's published slots + the queue with approval
chips) — visually the message composer's sibling. In live mode it collapses to a lease
bar ("nick is typing · take over"), and the viewport becomes the input surface for the
lease holder. WCAG floor applies as everywhere: the viewport is focusable and
scrollable by keyboard, mode/lease state is announced, approval buttons are real
buttons, and terminal theme tokens ride `app/tailwind.css` under the Phase4 contrast
test.

## Delivery

Three PRs, each independently green and shippable:

**1. Blocks, no pty.** Domain vocabulary (`TerminalId`, events, folds), transcript
sidecar + chunk route, `Terminal` frames (output/snapshot only), synced composers +
queue + approval, the drain gate, the right panel rendering blocks through xterm.js
(as a renderer only — commands run through the existing piped `Spawn`), agent tool +
`ExecuteCommand` re-point. Zero native dependencies; runs on every backend today.
*Verify:* cheap-tier folds and drain/approval properties (incl. transcript-replay
determinism vs a headless emulator), `Browser` E2E for the panel + two-peer draft
visibility, `Ports` E2E for chunk immutability/caching headers.

**2. Pty + live mode, and rejection.** `SpawnPty` on the seam (docker via exec-tty, host
via node-pty in the Nix `nodeModules`), the headless emulator per terminal,
snapshots-after-seq, lease claim/steal/release + `TerminalInput`/`TerminalResize`,
alt-screen + OSC 133 detection with the auto-flip policy and manual override, size
register. New `Pty` test capability. Plus the two queue changes leases and review demand:
**the lease as a drain gate** (above) — the holder threaded into
`TerminalQueueDrain.plan`, release re-arming the drain, and `AwaitingTerminal` reported
distinctly from `AwaitingApproval` — and **rejection as an answer** — the `RejectedBy`
register, `TerminalCommandRejected`, `Rejections` on the drain plan, rejection joining
`consumedOf`, the `Rejected` block status, and the reject control beside approve.
*Verify:* `Pty`-tagged suites (a real vim/alt-screen round trip; lease enforcement — a
non-holder's input frame is dropped and logged), `Docker`-tagged exec-tty suite,
cheap-tier flip-policy purity tests. For the lease gate, cheap-tier over `plan`: an
approved entry in a leased terminal with NO running block is held, not `Ready` (the case
`busy` does not cover); the hold names the terminal, not approval; release yields it
`Ready`; and a `Pty` E2E that a command queued during a live session runs on release and
not before. For rejection, cheap-tier: a rejected entry is never `Ready` under ANY mode,
`AutoRun` included, and is rejected even while the terminal is leased; a rejected
`QueueId` folds into `consumedOf` so it cannot run afterwards; both orderings of the
reject/drain race leave exactly one event and one outcome; the projection surfaces the
refusal with its actor and reason; the event round-trips. `Browser`: reject removes the
entry for both peers and both see who refused.

Rejection rides in PR 2 rather than PR 3 because it is the half of the approval gate PR 1
left out, and a gate that records every yes and no no is the weaker thing wearing the
stronger thing's face. It shares nothing with the pty work; if PR 2 splits, it goes first.

**3. One `execute_command`, and the seams.** The convergence above: the bounded two-phase
wait (`approvalGrace` then the existing command timeout), `read_terminal_block` to resume a
handle, the agent terminal opened lazily in the session's agent policy, the terminal digest
on `AgentContextPack`, and the block surviving its turn. Then the retirements the
convergence unblocks: the old `execute_command` and `queue_terminal_command` both deleted in
favour of the merged tool, `ensure_environment` deleted with its `reason` living on as the
agent terminal's title, and the sidebar commands section retired once nothing feeds it. Plus
idle-lease timeout, transcript compaction/retention policy, asciinema-player replay view for
closed terminals (the audit read), GAPS entries (agent lease, per-user terminal gating,
docker non-root unchanged).
*Verify:* cheap-tier state machine for the wait (pure: which phase a given mode/approval
timeline lands in, and that every path names its status rather than reporting a bare
"queued"); `Ports Native` E2E that the agent chains — runs a command, reads real output,
runs a second command conditioned on it — under `AutoRun`, and that under `ApproveAgent` the
same call returns `AwaitingApproval` inside the grace and `read_terminal_block` picks it up
after a human approves; a test that an interrupted turn leaves the block running and its
outcome in the next turn's digest; and a `LiveAgent` turn proving a real model uses the one
tool it now has.

**A note on ordering.** This is PR 3 because it depends on nothing in PR 2 and everything in
PR 1, and because the retirements need somewhere to land — but the loophole is open until it
ships. If PR 2 slips, this should go before it rather than wait: `ApproveAgent` claiming a
gate it does not have is the kind of thing that reads as a feature and behaves as a
blind spot.

Protocol note: terminals extend the Session↔Browser frame protocol and session events;
the Manager↔Session control protocol is untouched, so no major bump — each PR is a
`feat:` with `+semver: minor` on its branch commit body only if it lands user-facing
capability (PR 1 and 2 do).

---

## What PR 1 actually shipped

Blocks, no pty — the whole collaborative half, with zero native dependencies. Five
deviations from the plan above, each forced by something the plan did not know.

**xterm.js is not in PR 1.** The plan had the panel render blocks through xterm.js. It
renders them through a pure-F# SGR parser instead (`Yession.Domain/Ansi.fs`), and the
sixteen named ANSI colours are theme tokens held to the same WCAG AA floor as every other
text colour (`app/tailwind.css`, pinned by the Phase4 contrast test — raw ANSI would fail
it outright: the classic blue is `#000080`, 1.3:1 on this ground). Three reasons. PR 1 is
meant to have no new dependencies, and an npm dependency here also means rebuilding the
Nix `nodeModules` derivation's fixed-output hash. Block output is a *stream to read*, not
a *screen to maintain* — a parser answers it exactly, and half an emulator would be a
worse emulator and a worse parser. And PR 2 needs a real emulator anyway (`@xterm/headless`
server-side for snapshots and mode detection, `@xterm/xterm` in the browser), so that is
where the dependency belongs and where it earns itself.

**The agent's command appears queued, not typed.** The plan hoped to show the agent
drafting into its own composer slot. A tool call delivers a whole command in one go, not
keystrokes, so "watch it type" would be a single flash — theatre. `queue_terminal_command`
puts the command straight into the terminal's queue, where it is visible, editable, and
(under the default mode) waiting for a human. That is the part that mattered.

**`ExecuteCommand` is not re-pointed at a terminal.** The plan put that in PR 1 and the
old command log's retirement in PR 3. Doing the first without the second would change the
agent's existing tool from "runs and returns output" to "blocks until a human approves",
which turns a review gate into a deadlock whenever nobody is looking. The agent gets a new
tool that queues and returns; re-pointing and retiring now belong together, in PR 3.

That reasoning holds and the outcome was still wrong, because shipping the queue-only tool
*alongside* `execute_command` left the gated path strictly weaker than the ungated one —
and `ApproveAgent` unenforceable, since the agent can route around it. The escape from the
dilemma is that "blocks until a human approves" and "blocks until the process exits" are
different waits with different bounds; bounding only the first keeps the review gate from
deadlocking without giving up the output. Designed above ("Closing the loophole"),
delivered in PR 3.

**Presence in a terminal composer renders as chips, not carets.** Focus is reported and
relayed end to end (`FocusField.TerminalDraftBody`/`TerminalQueuedBody`), and the composer
shows who is in it, coloured by peer. Pixel-positioned remote carets need per-input
measurement in the browser shell; the title and the ProseMirror bodies each solve that
their own way, and a third measurement path is its own change.

**One upstream defect found, mitigated here, then fixed upstream.** `withYlmish`'s doc
subscription decoded against `currentModel` — the model as of the last message Elmish
*processed* — and dispatched `Set m` carrying that already-decoded model, which `update`
returned in place of the live one. So a message dispatched but not yet processed when a
remote doc update arrived was undone. Elmish's ring buffer makes that window ordinary:
anything dispatched from inside the dispatch loop queues. The terminal drain made it
reproducible (it removes a queue entry between two event appends); the message drain could
always hit it.

Filed as [Ylmish#136](https://github.com/NickDarvey/Ylmish/issues/136) with a deterministic
40-line repro, and fixed by [Ylmish#137](https://github.com/NickDarvey/Ylmish/pull/137):
`Set` is now a payload-free SIGNAL and `update` decodes against the model Elmish hands it,
which deletes the `currentModel` mutable along with the race and drops `'model` from
`Message`. Yession moved to `1.0.0-beta0222` and the pinned regression test — which asserted
the defect on purpose — went red on cue (`expected: 1, actual: 2`) and was deleted with it.

The mitigation went with it: the doc-update re-arm and the `parked` flag that existed only
to stop that re-arm hammering a settled-failed feed are gone. `ConnectOptions.ReadPosition`
**stays**, on its own merits rather than as a workaround — a read loop and a model that
each track their own consumed offset will drift the moment a fold is discarded, and one
source of truth is the fix for that whatever Ylmish does.

### Verification

`check` 410/410, `check Ports Native` 473/473, `check Browser Ports Native` 473 + 9
browser, all green. New coverage: 47 cheap-tier cases in `tests/Yession.Tests/Terminals.fs`
(the approval policy, the drain's decision, the projection's fold and idempotence, the ANSI
parser, transcript chunking and the asciicast wire shape, every terminal codec, per-terminal
queue ordering, and the terminal manager and scheduler over a scripted sandbox), two
end-to-end scenarios through the real Host in `InMemory.fs` (a peer opens a terminal and
runs a command with both peers seeing the block and its output; the agent's command waits
for a human and one approval releases it), and the panel pinned in the SSR checklist.

The browser E2E earned itself immediately. The input binding's `[<Emit>]` template declared
`const el = $0`, and Fable substitutes `$0` with the ARGUMENT'S OWN IDENTIFIER — so against
an F# value also called `el` it emitted `let el = el`, a temporal-dead-zone self-reference
that threw on the first keystroke and took the whole render with it. Every cheap-tier test
passed throughout: the composer was simply broken in every real browser. The templates now
use `__y`-prefixed locals that no F# binding will collide with.
