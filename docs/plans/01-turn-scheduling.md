# Plan 01 (rev 2) — Collaborative message queue: edit/reorder until the agent takes it

> Phase 3 · Turn discipline
> Addresses the paired gaps from [GAPS.md § Agent](../GAPS.md): overlapping turns and
> the undecided queueing/cancellation policy — now with an **editable, reorderable
> queue**. Rev 2 supersedes rev 1's "queued messages are already-immutable events"
> stance: queued messages are *collaborative state* and become events only at their
> terminal transition.

## Product behaviour

1. **Sending enqueues.** "Send" moves a draft into the session's shared **message
   queue** — collaborative state, not yet history. While queued, any peer can **edit
   the body** (merging text), **reorder** (drag ⇒ one fractional-index write), or
   **delete** it. Offline-capable like drafts.
2. **The agent consumes the queue.** When the agent is idle and the queue is
   non-empty, the Session Process **drains** it: every queued message becomes an
   immutable `MessageSent` event (body snapshotted at consumption, in queue order) and
   one coalesced turn starts. While a turn runs, new sends simply accumulate in the
   queue — Cursor's default.
3. **Interrupt is explicit.** Interrupting cancels the running turn
   (`AgentTurnInterrupted`, partial response kept) and drains the queue immediately.
4. **The terminal transition is the linearization point.** Once consumed, a message is
   history: late edits/reorders/deletes of it are no-ops. Until consumed, deletion
   wins: a deleted queue entry never becomes an event.

## Data model

The queue lives in `SyncedSessionState`, encoded exactly per the Ylmish recipes already
used for drafts (keyed map for offline-safe creation; text merge; fractional index):

```fsharp
type QueueId = private QueueId of string          // app-minted at enqueue

type QueuedMessage =
    { QueueId : QueueId
      Author  : PeerId
      Body    : Ylmish.Text      // Encode.text  — concurrent edits interleave
      Order   : float }          // Encode.float — LWW register; reorder = one write

// SyncedSessionState gains:
//   Queue : Map<QueueId, QueuedMessage>          // Encode.map — element-wise merge
```

- **Edit** = `Text` splice (merges; nobody's keystrokes lost while queued).
- **Reorder** = write `Order` between neighbours (never a structural move — Yjs's
  concurrent-move duplication, U13, is unrepresentable). Ties are broken by `QueueId`,
  so order is always total and deterministic.
- **Delete** = remove the map key (delete beats concurrent edits inside — U9 — which
  is the semantics we want).
- `MessageSent` gains `QueueId : QueueId option` — the durable link from doc-world to
  event-world, and the exactly-once dedup key.
- `SendDraft` (the Step 06 command) is retired: sending is now a pure client model
  update (draft → queue entry), fully CRDT, no request/response. The command decodes
  to `CommandRejected "superseded"` for old clients; draft `Sending/Sent` statuses
  collapse into "it's in the queue / it's in the timeline".

## Consumption (the Session Process's atomic act)

The Process is the only event writer and the **single consumer** (one Process per
session, Manager-enforced). Drain, in one synchronous block on its replica:

```text
drain():                                    # runs only when scheduler is idle
  snapshot ← queue entries of MY replica, sorted by (Order, QueueId)
  batch    ← snapshot minus any QueueId already named by a MessageSent event   # dedup
  if batch is empty: return
  for m in batch: append MessageSent { …, QueueId = Some m.QueueId }           # 1. durable
  Y.transact: remove batch keys from the doc (process origin)                  # 2. visible
  start ONE coalesced turn (trigger = last of batch)                           # 3. run
```

- Append **before** remove: durability first. A crash between 1 and 2 cannot
  double-consume because the dedup filter consults the log on the next drain.
- The doc removal is the Process's **first write to the doc**. It stays inside the
  sync boundary: a narrow `SyncedStateSync.removeQueued : Y.Doc -> QueueId list ->
  unit` next to the codec (boundary code may touch Y types; application logic still
  never does), transacted under a process origin so it relays like any peer update.
- Drain triggers: turn completion/failure/interruption, **and** any doc update
  observed while idle that leaves the queue non-empty (the Process already observes
  every update for `DraftStarted`) — so there are no lost wakeups.

## Concurrency analysis

Two clocks exist: the CRDT doc (per-peer replicas, eventual) and the event log (single
writer, total order). **The linearization point of "accepted by the agent" is the
drain's snapshot on the Process replica.** Everything below follows from that plus
Yjs's pinned semantics (Ylmish's validated assumptions U2–U15).

| Race | Outcome | Why it's safe |
|---|---|---|
| **Delete vs accept** (peer deletes Q while the Process drains) | Delete arrives at the Process *before* the snapshot ⇒ Q is not consumed, ever. Arrives *after* ⇒ Q is already history; the delete targets a removed key ⇒ CRDT no-op. UI converges: entry leaves the queue either into the timeline or into nothing. | Single consumer + synchronous snapshot ⇒ no window where Q is half-consumed. Deleting an already-removed map key merges as a no-op. |
| **Edit vs accept** (peer types into Q while it is consumed) | The event body = the Process replica's content at the snapshot. Late splices target a removed entry ⇒ discarded by Yjs (delete-beats-edit works *for* us). The typist sees the entry jump to the timeline with the taken snapshot. | Snapshot-at-consumption mirrors "snapshot at send" (E2E-3); U9/U11 guarantee late edits cannot resurrect or corrupt. Events are append-only, so the body can never change afterwards. |
| **Reorder vs accept** | Timeline order = `(Order, QueueId)` sort of the Process replica at the snapshot. A late reorder writes a register inside a removed entry ⇒ no-op. | Order is data, not structure: no duplication is representable. The tie-break makes the drain order a *total, deterministic* function of the snapshot. |
| **Reorder vs reorder / edit vs edit** (no drain involved) | Standard CRDT merge: registers LWW with deterministic clientID tie-break; text interleaves. All replicas converge. | Exactly Step 05's guarantees; property-tested here across the queue shape too. |
| **Enqueue vs drain boundary** | An add that reaches the Process before the snapshot rides this batch; after it, it waits — and the while-idle drain trigger guarantees it is picked up (liveness). | No lost wakeups: drain re-arms on every doc update while idle. |
| **Interrupt vs completion** (turn finishes as the user clicks) | Command rejected ("turn already finished"); the completion-drain has already run. UI catches up from events. | Scheduler validates the running turn id; drains are serialized by single-flight. |
| **Crash between event-append and doc-removal** | Restart sees queue entries whose `QueueId` already has a `MessageSent` ⇒ dedup filter skips them and repairs the doc by removing them. | Exactly-once is anchored in the log, not the doc. (Requires doc persistence or peers re-syncing their replicas on reconnect — see risk below.) |
| **Two consumers** | Cannot happen: one Process per session by Manager construction (Step 10/11). | Single-consumer is structural, not cooperative. |

### Invariants (the property-test contract)

For any schedule of operations and any delivery order:

1. **Exactly-once**: each `QueueId` appears in ≤ 1 `MessageSent`; = 1 iff it was in
   some drain snapshot; = 0 iff deleted before every snapshot that could have taken it.
2. **Snapshot fidelity**: each consumed body/order equals the Process replica's value
   at that drain's snapshot (the oracle's linearized state).
3. **No mutation after terminal**: folding the log twice yields identical projections;
   post-consumption doc ops never alter any event or projection item.
4. **Convergence**: after quiescence (all updates delivered), every replica agrees on
   queue contents, queue order, and timeline.
5. **Total order**: the drain batch order is the `(Order, QueueId)` sort — no
   duplicates, no drops, deterministic under ties.
6. **Liveness**: quiescent + agent idle + queue non-empty ⇒ a drain occurs.
7. **Single-flight**: `AgentTurnStarted` events never overlap (started_n+1 only after
   terminal event of turn_n).

## Verification: property tests

Following Ylmish's proven differential-harness pattern (its Harness.fs + Stress.fs)
rather than betting on a Fable-compatible QuickCheck port: **seeded random schedules
against a sequential oracle** — property testing with repo-grade repeatability
(deterministic seeds, pinned Yjs clientIDs; failures print the seed; a failing seed
becomes a pinned regression test).

- **Generator**: a seeded PRNG produces N-peer schedules over
  `Enqueue | EditQueued | Reorder | DeleteQueued | Deliver(peer↔process, subset) |
  TurnCompletes | Interrupt`, under delivery policies (immediate / hold-all /
  random-partial delivery to model partitions).
- **System under test**: real client programs (withYlmish) + the real drain/scheduler
  over in-memory channels — the same machinery production runs, no WebRTC in the loop.
- **Oracle**: a ~50-line sequential model applying the linearization rule ("the
  Process's delivered-set at drain wins"), from which invariants 1, 2, 5 are computed;
  3, 4, 6, 7 are checked structurally on the SUT.
- Volume: a few hundred schedules per suite run (bounded seconds); the two
  user-named races — *delete-when-accepted* and *reorder-when-accepted* — also get
  explicit, named example tests (both orderings each) plus a browser E2E for the
  delete race so the UX (entry jumps to timeline, delete no-ops) is pinned visibly.

## Delivery steps (tracker Phase 3, revised)

| # | Step | Outcome | Verification |
|---|------|---------|--------------|
| 15 | Queue in synced state | `QueuedMessage` map + codec; send = enqueue (SendDraft retired); edit/reorder/delete ops + UI (queue list, drag = fractional index) | Codec round-trip; two-client converge on edit/reorder/delete; wire-compat for `MessageSent.QueueId` |
| 16 | Drain & scheduler | `removeQueued` boundary write; atomic drain with log-anchored dedup; single-flight scheduler with while-idle trigger | Named race tests (delete-vs-accept ×2 orderings, reorder-vs-accept ×2); crash-replay dedup test; liveness test |
| 17 | Abort seam & interrupt | `AgentAbortSignal` on `RunAgent`; `InterruptAgentTurn` command → `AgentTurnInterrupted` (+ `Interrupted` projection status); interrupt ⇒ immediate drain | Held-turn interrupt tests; interrupt-vs-completion race; UI button + queued indicators |
| 18 | Property harness & acceptance | Seeded schedule generator + oracle; invariants 1–7; browser E2E for the delete race; live SDK abort | Hundreds of seeded schedules green and repeatable; 5 deterministic suite runs; Phase 3 acceptance recorded |

## Risks & open questions

- **Doc durability now matters more** (GAPS: the Yjs doc is not persisted). Queued
  messages are user-visible content that survives *peers* leaving but not a Process
  restart — unless a peer replica re-syncs it back on reconnect (full-state exchange
  does this if any peer was online across the restart). Recommendation: pull doc
  persistence (append Yjs updates to a sidecar file, compact on load) into this phase
  as step 19 if the loss window is unacceptable; otherwise record it as an accepted,
  sharpened gap.
- **Drain atomicity depends on the single-threaded Process tick** — the snapshot,
  appends, and removal must not yield to IO between them. With the file log's
  synchronous append this holds today; if the log ever goes async-batched, the drain
  needs an explicit mutex. Pin with a test that injects a slow log.
- **Editing during the drain broadcast window** on *another* peer (removal not yet
  delivered) is just the edit-vs-accept race — covered — but the UX flicker (typing
  into an entry that then vanishes) may warrant a "locked" visual the moment
  `AgentTurnStarted` arrives.
- **Old clients** sending retired `SendDraft` commands get a rejection; acceptable
  pre-1.0, noted in the release notes.
