# Plan 20 — Collaborative terminals: the list, pins, agent lanes, and wakes

> **Status: proposed.** Builds on [Plan 13](13-worksandbox-terminals.md) (terminals, blocks,
> leases, the transcript), [Plan 14](14-terminal-replay-in-chat.md) (chips, the tab strip,
> replay, the DVR) and [Plan 19](19-provider-streams.md) (attached streams, `Renewable`).
> Nothing here changes what a terminal IS — facts fold, bytes stream, tabs are personal —
> it changes who can get work done in one at the same time, and what the surfaces owe them.

Four shortcomings, observed rather than imagined:

1. **The strip is a census.** Plan 14 made every terminal the session has ever had permanent
   strip furniture, because the strip was the only door to a recording. A session that works
   for a week wears every terminal it ever opened, forever, and the tabs a person is actually
   using drown in the ones nobody will tap again.
2. **The agent serializes.** One agent terminal per sandbox and one block at a time per
   terminal means the agent's commands queue behind each other even when they are independent
   — the drain already runs terminals concurrently, and the tool surface is what cannot ask
   for it.
3. **Long work needs a poll or a nudge.** A ten-minute suite outlives the 120s command
   timeout; the tool yields a handle, the turn ends, and the outcome sits in the digest of a
   turn that nothing ever starts. Today a person has to speak to wake the agent into its own
   results.
4. **The chat drowns in chips.** Plan 14 named this risk and deferred it; parallel agent work
   converts it from a risk into a certainty.

One design answers all four, in five sentences: **the list** is every terminal and every
verb; **the strip** is the live terminals a person pinned; **chips** stay the permanent deep
links; **the preview** is one reusable reading slot; and **a wake** is a mailbox entry that
starts an agent turn when work finishes while nobody is talking.

## The list

A new pane surface: every terminal the session has ever had, one row each, most recent
activity first. A row shows title, state, presence, and the stated-gap flag — and offers
exactly the verbs its state affords:

| state                | verbs |
|----------------------|-------|
| open                 | pin / unpin, kill, rewind (the Plan 14 DVR) |
| open, block running  | the same; the row's status is the pulsing block |
| closed               | replay |
| closed + `Renewable` | replay, attach again |

**Affordance is a pure function of `TerminalView`**, declared in the Domain beside the state
it reads rather than decided in the view (Colocation: a rule lives with the state it
governs — and this rule already exists in prose, scattered across three view conditionals):

```fsharp
/// What a terminal's state affords a person right now. The view renders EXACTLY this —
/// a verb this function does not name is not offered, which is what keeps "a destructive
/// control is not offered over nothing" a fold the cheap tier can pin rather than a
/// convention three templates remember separately.
type TerminalAffordances =
    { CanKill : bool          // open only: a "close" on a closed terminal is a dead control
      CanRewind : bool        // open, and something is recorded to rewind into
      CanReplay : bool        // closed, and the recording was kept (DroppedBytes gap stated)
      CanReattach : bool }    // closed, Attached source, provider said asking again is safe

module TerminalAffordances =
    let ofView (view: TerminalView) (recordedAnything: bool) : TerminalAffordances
```

**Replay moves here and lives ONLY here.** The strip never hosts a recording again: a closed
terminal costs the strip nothing, which is the whole tab problem dissolved rather than
managed. Rewinding a live terminal and replaying a closed one become the same gesture on the
same row — which they already are underneath (the same pinned-cast player, Plan 14 stage 7).
No belt-and-braces: the closed-terminal strip tab and its replay body are DELETED, not kept
as a second door.

The list is a real surface under the WCAG floor: a keyboard-operable list, every verb a real
`<button>` with an accessible name, focus returned to the row when a preview it opened
closes. Presence dots render on rows exactly as they render on tabs today — one person, one
name, every surface at once.

## The strip and the pins

The strip shows **pinned, live terminals** — nothing else. Pinning is personal, client-local
state exactly as `PaneTabs` is today; your working set, not the room's.

```fsharp
type ClientModel =
    { ...
      /// Terminals this client pinned to the strip. Client-local like every tab fact:
      /// pinning is reading, not collaborating. Keys are PaneTab keys, so the set is
      /// meaningful for the preview too.
      Pins : Set<string>
      /// The one reusable reading slot (see below). `None` when nothing is being read.
      Preview : PaneView option }

/// What the preview slot can hold — Plan 14's closable tabs, reborn as one slot.
type PaneView =
    | BlockView of TerminalId * BlockId
    | StretchView of TerminalStretch
    | RecordingView of TerminalId
```

- The strip derivation is a pure function: `ClientModel.strip = open terminals ∩ Pins`, in
  open order. Cheap-tier testable, no DOM in the loop.
- **Unpin ≠ close.** The process runs on, the row stays in the list, chips keep landing.
- A terminal that closes leaves the strip after one rendered beat carrying its closed state
  — long enough to see "exit 0", not long enough to be furniture. Its recording is a row.
- `PaneTab.isClosable` and the `BlockTab`/`StretchTab` accumulation are deleted with this:
  chips and list rows open into the **preview slot**, one at a time, replacing what was
  there. Pin the preview to keep it (it joins `Pins` under its `PaneTab` key and renders as
  a strip tab until unpinned). One slot ends the closable-tab bookkeeping for read-only
  views entirely.

**Pin defaults are the policy, and there are exactly three rules:**

1. A terminal a human opens is pinned for that human. You asked for it; it is in your hands.
2. An agent task terminal is unpinned everywhere. It surfaces through the agent indicator
   (below) and the list.
3. Typing into any terminal pins it for you. Touching it is claiming a seat at it — the
   implicit rule that makes rule 2 safe, because watching the agent and joining the agent
   are one gesture apart.

### Visual semantics

The controls say what they are by FORM, not by caption — the design system's job is that a
glyph read once is a vocabulary item forever:

- **Pin is a pin.** A pin glyph (`Icon.pin`, a stroked path beside the existing set; a
  filled variant for the pinned state), rendered as a toggle: `aria-pressed`, filled when
  pinned, outline when not. Its accessible name states the consequence — "Unpin — keeps
  running" — because today's tab-`×` vocabulary means "gone", and this control must never
  inherit that reading. **No `×` appears on a strip tab.** A control that cannot destroy
  anything is the point; muscle-memory tab-closing becomes structurally safe.
- **Kill lives only on the list row**, styled as the destructive verb (the caps-err voice),
  and is offered only while the terminal is open — the affordance fold above, not a
  disabled button. Disabled destructive controls teach people to stop reading controls.
- **State is shown where it is true.** A running block pulses on its row and its card; a
  closed row's replay affordance is the player's own play form; the stated gap
  ("recording not kept") keeps its error voice. No status sentences beside things that can
  show their status.
- Keyboard: the strip stays a real tablist (arrow nav exists); `Delete`/`Backspace` on a
  focused tab unpins it and moves focus to the neighbour, per the existing refocus rule.
  The list row's pin toggle is a Tab stop like any other button.

## Background commands and the wake

The standard answer to "tell the agent when it's done" is the actor answer: **the wake is a
mailbox item, not a callback.** The Session Process's scheduler is already the single
consumer of one message queue, running one coalesced turn per batch — a wake is a reason for
a turn to exist when that queue is empty.

**The digest already did the hard half.** `TerminalDigest.window` reports every block that
started or completed since the previous turn, cursor-free, derived from the page. So a wake
carries **no payload**: it is purely a scheduling fact. The woken turn reads outcomes
through the same door every turn does. No second channel into context.

```fsharp
/// Why an agent turn started with nobody speaking. Attribution, not payload: the turn's
/// substance arrives through the digest and the tool roster exactly as it always does.
type WakeReason =
    | CommandFinished                      // a backgrounded block completed
    | ToolsChanged of server: string       // an MCP server's tool list changed (stage 5)
    | StreamEnded of TerminalId            // an attached terminal's source closed
    | IntegrationLost of TerminalId        // marks gone; the agent's queue is held

/// Whether a wake is due, as a fold over the same page the digest reads.
module AgentWake =
    /// Due iff some background-flagged block completed at or after the last
    /// `AgentTurnStarted` — the digest's own window trick, applied to scheduling. No
    /// stored cursor, no new durable state, and therefore restart-safe for free: a
    /// process that dies between the completion and the turn re-derives the same
    /// pending wake from the log at boot.
    let due (events: SessionEvent list) : bool
```

Mechanics, in the order they matter:

- `execute_command` gains `background` (default `false`). A background command enqueues
  (visible, editable, refusable — the one door is untouched), waits out nothing, and
  returns its handle immediately with the observed status. The queue entry carries
  `Background : bool`; `TerminalBlockStarted` projects it onto the block, which is what
  makes `AgentWake.due` a pure fold over the log.
- The scheduler's drain grows one arm: queue empty, no turn running, `AgentWake.due` →
  run a turn with a wake trigger instead of a message trigger. `AgentTurnStarted` gains
  `Woke : WakeReason option` — the durable attribution, and the event that closes the
  window so a wake cannot fire twice for one completion.
- **Coalescing is free.** Five parallel commands finishing over two minutes arm one due
  wake; everything landed before the turn starts is inside that turn's digest window. A
  wake is idempotent by construction, which is the property that makes it safe to compute
  on every drain rather than deliver exactly once.
- **Humans outrank wakes.** A queued message and a due wake resolve to one turn — the
  message's, with the digest carrying the finished work anyway. A wake never interrupts a
  running turn; it is only ever read by an idle drain.
- **No gate bypass.** A woken turn is an ordinary turn. Waking decides WHEN, never WHAT.

What humans see, by form:

- While a background command runs, its chip already pulses (Plan 14). A chip whose
  completion will wake the agent additionally wears the agent's presence mark — the same
  dot vocabulary the roster and tabs use, placed on the chip. Waiting is shown as presence,
  not narrated as a sentence.
- A woken turn's first chat item carries its reason as attribution — *agent · woke:
  `running the tests` exited 1* — rendered with the same author-attribution styling every
  message has. An agent that acts unprompted always shows cause; that is the
  identity-and-attribution promise the UI test doctrine already pins, extended to turns.

## Agent lanes: task terminals

With ephemeral tabs costing nothing, parallel agent work stops being a furniture problem:

- The agent may open **task terminals**: `execute_command` gains `lane : string option`. A
  lane names an intent ("tests", "build docs"); the first command in a lane opens a
  terminal titled by it — the same lazy open, title-is-the-reason move the per-sandbox
  agent terminal already makes — and later commands naming the lane join its queue.
- **Capped at 4 lanes RUNNING per sandbox** (built: the plan originally capped lanes
  *existing*, which could not be a hold at all — a pending act names a terminal, so at cap
  there would be nothing to queue against and a held command would live nowhere anyone could
  see it, which is the one thing the one-door design cannot afford. What is worth bounding is
  concurrent work anyway; the idle close bounds how many lanes there are). The cap is enforced
  inside the terminal manager,
  beside the state it governs, and hitting it is a QUEUE HOLD with a name
  (`AwaitingLane`), never an error and never a silent drop — the same doctrine as every
  other hold: a stall with a name beats a stall.
- A lane closes itself when it has been idle for a beat past its last block with nothing
  queued — reason "task finished" — and becomes a list row like any closed terminal. The
  close is appended by the Process, so the audit reads exactly what happened.
- Lanes default to `background: true` — a lane exists to fan out, and fanning out then
  blocking the turn on lane one would be the old serialization wearing a new name.

**The agent indicator** replaces N agent tabs with one strip-end element: the agent's
presence dot plus a count of running lanes, wearing the failed-state colour the moment any
lane's last block failed. It is a button into the list filtered to agent rows — not a tab.
Pin a lane from its row to watch it; typing in it pins it (rule 3) and makes it yours to
collaborate in, queue, approvals and all.

## Task cards

Chips from one agent burst — same turn, overlapping or consecutive blocks across the
agent's lanes — coalesce into one **task card** in the timeline: a summary line
(`5 commands · 3 ✓ · 1 ✗ · 1 running`, each state in its established glyph and colour) over
one line per command, statuses mutating in place exactly as chips do. Collapsed, failures
sort first: red is what a person scans for. Tapping a line opens its block in the preview;
the card anchors at its first block's start so long work stays visible above later
messages — the same anchoring rule chips follow today.

The grouping is a pure view-level fold over `TimelineProjection`'s output (a
`TimelineCards` pass: same shape as the timeline merge itself, cheap-tier testable,
`ConversationProjection` untouched and byte-identical). Human-authored blocks never group:
grouping is for work nobody is hand-driving.

## Delivery

Six stages, each independently shippable. Stages 0–1 are pure client/view work and are
what make the rest affordable; stage 6 is the largest and least certain, and nothing
before it depends on it.

### Stage 0 — the terminal list

The list pane ships beside the existing strip, with `TerminalAffordances.ofView` in the
Domain and replay/rewind/kill/attach-again offered on rows. Additive: nothing is deleted
yet, so the stage is a pure win to ship early.

*Tests:* cheap tier for the affordance fold (one case per state × verb, each pinning one
invariant); `Browser` for keyboard operation of the list and focus return from a row's
preview. The availability invariants — kill never offered on a closed row, replay never
offered over a stated-empty recording — are cheap-tier SSR assertions, scoped to the row.

### Stage 1 — pins, the preview slot, and the strip diet

`Pins` and `Preview` land in `ClientModel`; the strip becomes pinned-live-only; the
closed-terminal tab, its replay body, `PaneTab.isClosable` and the `PaneTabs`
accumulation are deleted. `Icon.pin`/`Icon.pinFilled` land beside the existing stroked
set. Pin defaults 1–3. Chips open the preview.

*Tests:* cheap tier for the strip derivation, the pin defaults (a human open pins, an
agent open does not, typing pins), and the preview's replace-not-accumulate property.
`Browser` for the tablist keyboard contract (arrow nav intact, Delete unpins and
refocuses) and for the pin toggle's `aria-pressed` state. No test asserts the pin's
geometry — the toggle CONTRACT is the invariant, the glyph is the design.

### Stage 2 — background commands and the wake

`background` on `execute_command`; `Background` through the queue entry onto
`TerminalBlockStarted`; `AgentWake.due`; the scheduler's wake arm; `Woke` on
`AgentTurnStarted`; the chip's agent-presence mark; woken-turn attribution in the chat.

*Tests:* cheap tier owns nearly all of it, because everything interesting is a fold:
`due` true after a background completion, false after the turn it woke, false for
foreground blocks, coalescing (N completions, one wake), restart derivation (the same
events at boot re-arm the same wake), humans-outrank-wakes at the drain. `Ports Native`
for one end-to-end: a real backgrounded command completes, a turn runs unprompted, its
digest carries the outcome.

**The arm, as built.** `Wake` is called from exactly three places, and the third was not in
this plan: at boot, when a terminal block completes, and **at the end of every turn**. The
third exists because the first two can both miss. A background command that finishes while the
agent is mid-turn fires the wake against a taken slot, and nothing looks again — the debt sits
in the log for ever. Re-reading when the slot frees costs one page and terminates on its own,
because `AgentWake.pending` resets at every `AgentTurnStarted`.

It is a re-read rather than a queued flag deliberately: a flag is a second place the answer
lives, and the whole shape of the wake is that the log already knows.

`TerminalScheduler` takes an `onBlockFinished` seam for the same reason the manager takes a
transcript reader — the drain knows a block finished, and what that is WORTH belongs to
whoever composed it with an agent. A terminal queue that knew about turns would be the wrong
thing knowing it.

### Stage 3 — task lanes

`lane` on `execute_command`; lane open/join/cap/auto-close in the terminal manager
(`AwaitingLane` beside the other holds, reported apart because it resolves differently);
the agent indicator; list filtering.

*Tests:* cheap tier for the lane policy (first command opens, second joins, fifth holds
with the named reason, idle lane closes with "task finished"); `Ports Native` for two
lanes genuinely running concurrently — the property the stage exists for.

### Stage 4 — task cards

The `TimelineCards` fold; the card render; failure-first collapse ordering; preview
opening from card lines.

*Tests:* cheap tier for the fold (agent bursts group, human blocks never, statuses
mutate in place, anchor at first start) and for the one availability invariant (a card
line is a real button). Nothing pins the card's layout.

### Stage 5 — the unified wake vocabulary

`ToolsChanged` wired from MCP `notifications/tools/list_changed` on Plan 17's declared
servers; `StreamEnded` and `IntegrationLost` join. Process-local reasons (a tool-roster
change is not a session event; the turn rebuilds the roster regardless, and losing the
nudge across a restart costs a delay, not a fact — stated, not hidden).

*Tests:* cheap tier for reason precedence and coalescing across kinds;
`Jumpstarter` for a real provider's `list_changed` producing a woken turn whose roster
differs.

### Stage 6 — the agent lease and the handoff

The agent takes a lease (closing Plan 13's standing GAP): bounded, always stealable,
self-released at turn end, worn as the agent's presence colour on row and tab. When an
agent-authored block wedges interactive (the alt-screen flip's third rule finally gets
its answer), the chat gets a handoff card — take terminal, keyframed stretch, release
resumes the queue. The one place the chat acts, and it trails everything else.

*Tests:* cheap tier for the flip-policy revision and lease bounds; `Browser` for the
handoff card's focus contract; `Ports Native` for wedge → handoff → release → drain.

## Protocol and versioning

Session events change (`Background` on `TerminalBlockStarted`, `Woke` on
`AgentTurnStarted`); the Manager↔Session contract is untouched: **no major bump**. Stages
0, 2, 3 and 6 land user-facing capability and carry a `+semver: minor` marker on a line of
its own in a commit body on the branch (the marker line bare in the commit, never pasted
bare into this document or a PR body). Stages 1, 4 and 5 are `+semver: patch` at most.

## What this plan leaves open

- **Cancelling a wake.** A person can always interrupt a woken turn; a control that
  unsubscribes the agent from a completion it asked about needs a durable fact (the wake
  is derived, so cancellation cannot be client-local) and is not being built until wanted.
- **A session-wide transcript budget** — unchanged from Plan 14, and lanes make the
  64 MB × terminals floor grow faster. The list at least makes the growth visible.
- **Following a lane** (auto-selecting the loudest agent terminal) — cheap once the
  indicator exists, cut from scope until someone misses it.
- **Grouping human chips** — task cards group agent bursts only; twenty hand-run
  commands still render as twenty chips, deliberately.

## Things worth considering before this starts

- **The wake is a behaviour change to agent autonomy.** An agent that runs unprompted
  turns is new, even gated to its own finished work. The attribution rule is the
  mitigation, and it must ship IN stage 2, not after it.
- **Wake→turn→command→wake is a legitimate loop and a possible runaway.** The bound is
  visibility plus the human interrupt, same as approvals — if that proves thin, a
  per-session woken-turn budget is the next dial, and it is not being built in advance.
- **The list makes every recording one tap closer for everyone.** Plan 14 already
  widened the exposure profile; this widens discoverability again. Nothing newly unsafe;
  the distance between a secret on a screen and a casual reader shortens again.
- **Pin-state loss is cheap but real.** Pins are client-local; a new device starts with
  rule-1 defaults only. Durable per-user pins are a deliberate non-goal until tabs stop
  being personal, which would be its own decision.

## Authority, as a value (stage 2b)

> Named `ActProvenance` when this was written, and renamed on landing. *Provenance* promises a
> chain — a lineage of who delegated to whom — and this is a flat triple describing ONE act.
> A type whose name over-claims is a type the next reader will look inside expecting history.

Wiring the wake turned up a bug older than this plan: **agent terminal commands recorded no
`OnBehalfOf` at all.** Gated commands set it (`SessionMain`, twice); the terminal enqueue path
never did. So "the agent is the acting party and the credential is the turn human's" — Plan
08's no-borrowing rule — held in two places and was silently absent in a third.

That is not a missed line. It is what happens when a domain concept is carried as three loose
fields that every site re-spells:

```fsharp
Author     : ActorRef          // who wrote it
OnBehalfOf : ActorRef option   // whose authority it runs on
ApprovedBy : ActorRef option   // who released it, when a gate held it
```

`PendingAct` spells them, `TerminalBlockStarted` spells them, `TerminalCommandRejected` and
`CommandRefused` spell their share. Each is free to drift, and one did. An invariant that
holds only because a caller remembered to set a field is a convention with a good reputation.

**Make it a value object with the rule inside it.**

```fsharp
/// Who is behind an act: the three parties an audit asks about, as ONE value.
type Authority =
    private { Author : ActorRef; OnBehalfOf : ActorRef option; ApprovedBy : ActorRef option }

module Authority =
    /// A party acting for themselves. There is no authority to borrow, so there is none to
    /// state — which is why a person's act cannot accidentally carry somebody else's.
    let ofAuthor (actor: ActorRef) : Authority

    /// The agent, acting on a turn human's authority (Plan 08). The rule that was missing
    /// from one call site, as the ONLY way to build an agent-authored act: you cannot
    /// construct one without naming whose authority it runs on.
    let agentFor (turnActor: ActorRef) : Authority

    /// Released by a peer, when the subject's mode demanded one.
    let approvedBy (peer: PeerId) (authority: Authority) : Authority

    /// Whose credentials this resolves to — the borrowed authority when there is one, the
    /// author otherwise. The question every dispatch actually asks, answered once.
    let effective (authority: Authority) : ActorRef
```

What this buys, in order of how much it matters:

1. **The bug becomes unrepresentable.** `agentFor` takes the turn actor; there is no
   agent-authored `Authority` without one. The terminal enqueue path could not have
   forgotten, because forgetting would not compile.
2. **`effective` replaces four hand-rolled `defaultArg`s.** The dispatch, the wake, the repo
   verbs and the sandbox verbs each answer "whose credential?" today; they would ask once.
3. **One shape to serialize.** A single codec, used by `PendingAct`, the block event and the
   refusal events, instead of three encoders that agree by inspection.

Deliberately NOT included: the peer/user attribution step (`actorFor`, at the durable-append
boundary). That converts a connection into a party and is a different question from which
parties are behind an act — folding it in here would make this type know about transports.

**Delivery.** Shipped after the wake arm rather than with it: `effective` is what the arm
needed, and the arm was already merged by the time this landed — banking a refactor of this
width behind it would have been the rebase tax `contributing-changes` warns about.

**Two things the sketch above did not anticipate, found by building it.**

*The approval is not part of a PENDING act's authority.* `PendingAct.ApprovedBy` is a CRDT
register peers write and clear; folding it into an immutable value would have meant a setter
and an un-setter on a type whose point is that it cannot be edited into a lie — and would have
split the entry's two verdicts, approval and refusal, into two shapes. So a pending act carries
Author + OnBehalfOf, and `approvedBy` is applied at the transition: the drain stamps the
approver on as it mints the block. Which is what `approvedBy` was always for.

*Decoding is not authoring.* A doc entry or a stored event whose owner does not read back is a
state the constructors deliberately cannot express — and a decoder that could not represent it
would drop the act instead of recovering it, turning a corrupt field into a missing command. So
there is a fourth function, `rehydrate`, for the boundary. It is an escape hatch only in the
sense that a repository's reconstitutor is one: nothing AUTHORS through it, and the guarantee
that matters — no code path can propose an agent act with nobody's authority on it — is intact.

The field names inside the record are prefixed (`AuthAuthor`, ...) and private. Bare
`Author`/`OnBehalfOf` fields in this namespace made every other record carrying those names
ambiguous to F#'s inference.
