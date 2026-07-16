# Plan — One WIP draft per client (collaborate, or write your own)

> Phase 1 · Collaboration (refinement)
> Refines the [GAPS.md](docs/GAPS.md) § Browser client bullet
> *"One draft textarea UX: no presence cursors, no per-peer selections, no rich text."*
> Design context: [docs/design.md](docs/design.md) §1 "Ylmish is the sync boundary",
> §2.2; builds on [Step 05](docs/plans/00-init/05-ylmish-collaborative-draft-sync.md)
> and the queue in [Plan 01](docs/plans/01-turn-scheduling.md).

## The gap

Today `SyncedSessionState.Drafts : Map<DraftId, DraftState>` allows **unbounded**
drafts — "Start draft" mints a fresh `DraftId` every click — and offers no product
answer to *whose* draft is *whose*. The bodies are already collaborative (Step 05: any
peer editing any draft's `Ylmish.Text` converges), and the View already renders every
draft with an author badge. What's missing is the **shape of the composer**: how many
drafts exist, who owns them, and how "collaborate" differs from "start my own".

## Target UX

A draft is the **WIP tail** — the thing after the queue that is *not yet* queued. Like
the last item in the queue, but uncommitted: no agent will ever see it until it is sent.

- **At most one draft per client.** Each peer owns exactly zero-or-one WIP draft. This
  is a *type-level* invariant, not a runtime cap (below).
- **Collaborate, or write your own.** Two modes, no new machinery:
  - *Write your own* — type into **your** slot (keyed by your `PeerId`). This is the
    default composer, always present at the bottom.
  - *Collaborate* — open **another peer's** active draft and type into it. Same synced
    `Ylmish.Text`, edits interleave and merge — exactly Step 05's convergence, now
    per-slot.
- **Ownership bounds creation, not participation.** You may co-edit any number of other
  peers' slots; you *own* exactly one. "At most one per client" is a statement about the
  slot you can create and send, never about who may help you write it.
- **One textarea each. No presence cursors, no per-peer selections, no rich text** — the
  GAPS constraint, kept verbatim. Collaboration is body-merge, not a shared cursor.

Layout: drafts render **below the queue** (the WIP tail). Your own composer is pinned
last (the box you type into now); other peers' active drafts appear above it as joinable
boxes carrying the existing author badge (`View.drafts` already draws this).

## Data model

Key drafts by **author**, not by a minted id. A `Map<PeerId, _>` holds at most one value
per peer *by construction* — "one draft per client" becomes unrepresentable-otherwise
(design.md §1 "invalid states unrepresentable"), so no cap check, no reconciliation, no
race that could produce two.

```fsharp
// SessionState.fs
type DraftState =
    { Author : PeerId            // == the map key; a draft IS its author's WIP
      Body   : Ylmish.Text }     // collaborative — concurrent edits interleave (Step 05)

// SyncedSessionState:
//   Drafts : Map<PeerId, DraftState>      // was Map<DraftId, DraftState>
```

Deltas from the current code:

- **`DraftId` retires from synced state.** The author is the identity. `DraftId` the
  type survives only where it is already durable — `MessageSent.DraftId : DraftId option`
  — and even there it is redundant with `MessageSent.Author`; keep it `None`-able for
  wire-compat and stop minting it (open question below).
- **`Sync.fs` codec:** `draftsByKey` keys on `PeerId.value` instead of `DraftId.value`;
  `draftsToDomain` validates the key via `PeerId.create` (skipping invalid keys, as it
  already does — the decode stays total over a doc shared with peers we don't control).
  The per-draft `author` register drops: the key carries it. Body stays `Encode.text`.
- **`ClientMsg`:**
  - `StartDraftMsg` is **removed**. Creation is implicit: the first non-empty
    `EditDraftBodyMsg (myPeer, _)` materialises the local peer's slot.
  - `EditDraftBodyMsg of PeerId * Ylmish.Text` (was `DraftId * _`) — the `PeerId` names
    *which slot* you are editing (yours, or a peer's you are collaborating on).
  - `SendDraftMsg of PeerId * QueueId` — owner-only (below), so the `PeerId` is the local
    peer's.
  - Add `DiscardDraftMsg of PeerId` — clear a slot (empties your WIP without sending).
- **Lifecycle.** The local composer is *always visible* (there is always somewhere to
  type). A `DraftState` enters synced state on the first non-empty keystroke and leaves
  it on send, on discard, or when cleared to empty and blurred — so peers never see
  phantom empty boxes and the slot stays honestly optional.

## Send policy: owner sends

Only the **slot owner** sends their draft into the queue; collaborators contribute text,
the owner commits. Send is the existing atomic draft → queue transition (`SendDraftMsg`
in `ClientModel.update`), now with the slot key = author: one CRDT transaction removes
`Drafts[myPeer]` and adds the `Queue` entry at the tail.

Why owner-sends over anyone-sends:

- **Attribution stays legible** — the queue entry's author is unambiguous, and "your
  draft is yours to send" mirrors the one-per-client invariant.
- **No double-send race** — two peers cannot both commit the same slot, so there is no
  dedup to design at this layer (contrast the queue's drain, which needs one).

*Alternative (noted, not recommended):* anyone-sends, attributed to the slot owner —
adds a send-vs-send race that needs a dedup key, for a marginal UX gain.

## Concurrency (Ylmish / CRDT)

Everything below is Step 05 + Plan 01 semantics applied one layer earlier; no new CRDT
assumptions.

| Race | Outcome | Why it's safe |
|---|---|---|
| **Two peers edit the same slot** | Bodies interleave and merge; all replicas converge. | Exactly Step 05 — collaborative `Text` per slot. |
| **Two offline peers each write their *own* slot** | Distinct keys (their `PeerId`s); no conflict; both appear on rejoin. | The one-per-client invariant survives partitions *for free* — different peers, different keys. |
| **Concurrent creation of the *same* peer's slot** | Cannot diverge: same key ⇒ element-wise merge to one `DraftState`. | `Map<PeerId,_>` is structural — two slots for one peer are unrepresentable. |
| **Owner sends while a collaborator types** | Send removes the slot key; late splices target a removed entry ⇒ discarded (delete-beats-edit, U9). Collaborator sees the draft jump into the queue with the sent snapshot. | The queue's edit-vs-accept race (Plan 01), one layer up: snapshot-at-send is the linearization point. |
| **Discard vs edit** | Discard removes the key; concurrent edits inside a removed entry no-op. | Delete beats concurrent edit (U9) — the semantics we want. |

### Invariants (property-test contract)

For any schedule and any delivery order:

1. **One-per-client** — on every replica, every peer authors ≤ 1 draft. *(Structural:
   `Map<PeerId,_>`. The property asserts the type is used, i.e. no code path stores a
   draft under a non-author key.)*
2. **Participation is unbounded** — any peer may edit any slot; after quiescence every
   replica agrees on every slot's body (Step 05, per slot).
3. **Offline-safe creation** — concurrent own-draft creation across a partition never
   conflicts and never loses a draft (distinct `PeerId` keys).
4. **Clean send** — a send removes exactly the sender's slot and appends exactly one
   queue entry with the snapshotted body; later edits to that (now-absent) slot never
   mutate the queue entry.
5. **Convergence** — after all updates deliver, replicas agree on the set of slots, each
   body, and the queue.

## Delivery steps

| # | Step | Outcome | Verification |
|---|------|---------|--------------|
| 1 | Re-key drafts | `Drafts : Map<PeerId, DraftState>`; `DraftState` drops `DraftId`; codec keys/validates on `PeerId`; author register dropped | Codec round-trip (`decode∘encode` preserves state); "drafts keyed by author" model test |
| 2 | Composer lifecycle | Local composer always visible; lazy materialise on first keystroke; `DiscardDraftMsg`; empty+blur removes slot | Model tests: first edit creates the slot; discard/empty removes it; no phantom empty slot syncs |
| 3 | Two modes in the View | Own composer pinned below the queue; peers' active drafts joinable above it with author badge; single textarea, no cursors/selections | E2E: peer A types own draft; peer B joins A's draft and both converge; peer B also writes B's own; both drafts distinct and co-editable |
| 4 | Owner-sends + retire minting | `SendDraftMsg` owner-only; stop minting `DraftId`; `MessageSent.DraftId = None` | E2E: only the owner's Send commits the slot; edit-during-send lands the snapshot (delete-beats-edit); one queue entry appended |
| 5 | Properties | Invariants 1–5 as `property {}` blocks in the vendored Hedgehog harness (Plan 01 §Verification), schedules over `Edit(slot) \| Send \| Discard \| Deliver \| Offline/Rejoin` | Hundreds of deterministic cases per property; named example: two offline peers each draft own, rejoin ⇒ both present |

## Risks & open questions

- **`DraftId` and `DraftStarted`.** Drafts are not durable facts (only `MessageSent`
  is), so the `DraftStarted { DraftId; StartedBy }` event and `MessageSent.DraftId` are
  arguably vestigial once the author is the key. Recommend: retire `DraftStarted`, keep
  `MessageSent.DraftId` as `None` for wire-compat, revisit the type's removal in a
  cleanup. Confirm no consumer reads `DraftStarted`.
- **Re-key is a doc-format change.** Old docs key drafts by `DraftId`; those keys fail
  `PeerId.create` only if they aren't valid peer ids — otherwise they'd decode as
  spurious slots. Pre-1.0, the safe move is to treat pre-change draft keys as
  non-authoritative (the decode already skips keys it can't validate; drafts are
  ephemeral WIP, never history, so dropping stale draft keys on upgrade is acceptable).
  Note in release notes.
- **"Their own" vs "a shared draft".** This plan has no ownerless shared draft — the
  collaborative case is always *someone's* slot that others join. If a truly ownerless
  "session draft" is later wanted, it is one extra optional slot (`SharedBrief` is the
  precedent), not a change to the per-peer invariant.

## Fold back into GAPS.md

On delivery, rewrite the § Browser client bullet from *"One draft textarea UX"* to note:
one WIP draft **per client**, co-editable by any peer, keyed by author — still one
textarea each, still no presence cursors / per-peer selections / rich text.
