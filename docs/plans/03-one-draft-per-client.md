# Plan — One WIP draft per client (collaborate, or write your own)

> **Status: delivered.** The re-key, composer lifecycle, two-mode View, and owner-sends
> all landed in one change. Verification: the full solution type-checks (0 warnings,
> 0 errors) and the cheap test tier is **102/102** green (codec round-trip on
> author-keyed drafts, two-client collaboration convergence, a Hedgehog property for
> invariant 4 below, the queue property suite driving the new send path, and the UI
> checklist). One follow-up is noted below.
>
> Phase 1 · Collaboration (refinement)
> Refines the [GAPS.md](../GAPS.md) § Browser client bullet
> *"One draft textarea UX: no presence cursors, no per-peer selections, no rich text."*
> Design context: [docs/design.md](../design.md) §1 "Ylmish is the sync boundary",
> §2.2; builds on [Step 05](00-init/05-ylmish-collaborative-draft-sync.md)
> and the queue in [Plan 01](01-turn-scheduling.md).

## The gap

Before this change `SyncedSessionState.Drafts : Map<DraftId, DraftState>` allowed
**unbounded** drafts — "Start draft" minted a fresh `DraftId` every click — and offered
no product answer to *whose* draft is *whose*. The bodies were already collaborative
(Step 05: any peer editing any draft's `Ylmish.Text` converges), and the View already
rendered every draft with an author badge. What was missing is the **shape of the
composer**: how many drafts exist, who owns them, and how "collaborate" differs from
"start my own".

## Target UX

A draft is the **WIP tail** — the thing after the queue that is *not yet* queued. Like
the last item in the queue, but uncommitted: no agent will ever see it until it is sent.

**The queue is unchanged.** This plan bounds *drafts only*; the shared message queue
stays exactly as Plan 01 delivered it — unbounded, many messages, editable/reorderable
until the agent drains it. A peer still queues **as many messages as they like**: send
moves their one draft into the queue and clears the slot, so they immediately draft the
next. One draft at a time; any number of queued messages.

- **At most one draft per client.** Each peer owns exactly zero-or-one WIP draft. This
  is a *type-level* invariant, not a runtime cap (below). It caps *drafts*, never the
  queue.
- **You see every peer's draft.** Other clients' active drafts are visible in your
  composer as first-class boxes (author badge, live body) — so the WIP tail shows
  everyone's in-flight thought, not just yours.
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
last (the box you type into now, with Send and Discard); other peers' active drafts
appear above it as joinable boxes carrying the author badge (no Send — owner-sends).

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

What changed in the code:

- **`DraftId` deleted.** The author is the identity. The type had no remaining use:
  drafts key on `PeerId`, `DraftStarted` went (a durable event **no projection read** —
  `Conversation.fs` explicitly ignored it), and `MessageSent.DraftId` was redundant with
  `MessageSent.Author`, so the field went too. Nothing is released, so there was no compat
  to keep — `DraftId`, `DraftStarted`, and `MessageSent.DraftId` are gone outright.
- **`Sync.fs` codec:** `draftsByKey` keys on `PeerId.value`; `draftsToDomain` validates
  the key via `PeerId.create` (skipping invalid keys, as it already did — the decode
  stays total over a doc shared with peers we don't control). The per-draft `author`
  register dropped: the key carries it. Body stays `Encode.text`.
- **`ClientMsg`:**
  - `StartDraftMsg` removed. Creation is implicit: the first `EditDraftBodyMsg (myPeer, _)`
    materialises the local peer's slot.
  - `EditDraftBodyMsg of PeerId * Ylmish.Text` (was `DraftId * _`) — the `PeerId` names
    *which slot* you are editing (yours, or a peer's you are collaborating on).
  - `SendDraftMsg of PeerId * QueueId` — owner-only (below), so the `PeerId` is the local
    peer's.
  - `DiscardDraftMsg of PeerId` — clear a slot (empties your WIP without sending).
- **View / lifecycle.** The local composer is *always visible* (there is always somewhere
  to type); a `DraftState` enters synced state on the first keystroke and leaves it on
  send or discard. `Host` no longer appends a draft-announcement event — it keeps only the
  while-idle drain trigger.

## Send policy: owner sends

Only the **slot owner** sends their draft into the queue; collaborators contribute text,
the owner commits. Send is the existing atomic draft → queue transition (`SendDraftMsg`
in `ClientModel.update`), now with the slot key = author: one CRDT transaction removes
`Drafts[myPeer]` and adds the `Queue` entry at the tail (author = the slot's author).

Why owner-sends over anyone-sends:

- **Attribution stays legible** — the queue entry's author is unambiguous, and "your
  draft is yours to send" mirrors the one-per-client invariant.
- **No double-send race** — two peers cannot both commit the same slot, so there is no
  dedup to design at this layer (contrast the queue's drain, which needs one).

*Alternative (noted, not chosen):* anyone-sends, attributed to the slot owner — adds a
send-vs-send race that needs a dedup key, for a marginal UX gain.

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

### Invariants

For any schedule and any delivery order:

1. **One-per-client** — on every replica, every peer authors ≤ 1 draft. *(Structural:
   `Map<PeerId,_>`.)*
2. **Participation is unbounded** — any peer may edit any slot; after quiescence every
   replica agrees on every slot's body (Step 05, per slot).
3. **Offline-safe creation** — concurrent own-draft creation across a partition never
   conflicts and never loses a draft (distinct `PeerId` keys).
4. **Clean send** — a send removes exactly the sender's slot and appends exactly one
   queue entry with the snapshotted body; later edits to that (now-absent) slot never
   mutate the queue entry.
5. **Convergence** — after all updates deliver, replicas agree on the set of slots, each
   body, and the queue.

## What landed, and how it was verified

The change was small enough to land in one commit rather than staged steps:

| Area | Change | Verification (as delivered) |
|---|---|---|
| Re-key | `Drafts : Map<PeerId, DraftState>`; `DraftState` drops `DraftId`; codec keys/validates on `PeerId`; author register dropped | Codec round-trip (`decode∘encode` preserves state); "draft in the doc under the peer key" test |
| Composer & View | Local composer always visible; lazy materialise; `DiscardDraftMsg`; peers' drafts joinable with author badge; single textarea | UI checklist (`Acceptance.fs`) renders the composer, draft body, and `data-send-draft` keyed by peer; E2E-7 keeps unsent drafts out of the timeline |
| Collaboration | Any peer co-edits any slot | Two-client convergence test (in-memory + the E2E-1 collaboration path) |
| Owner-sends | `SendDraftMsg` owner-only; delete `DraftId`/`DraftStarted`/`MessageSent.DraftId` | Send E2E (enqueue → drain → one `MessageSent`, slot clears on both clients); every-`SessionEvent`-case wire round-trip with the two events gone |
| Send-many | Send clears the slot; a peer enqueues repeatedly | The queue property suite (invariants 1–7) drives every enqueue through the author-keyed slot |
| Invariant 4 (clean send) | send removes exactly the slot, appends one snapshotted entry, later edits never mutate it | Dedicated `property {}` — `Properties.fs` "Draft invariant 4 — clean send": generated schedules of slot edits + sends over a real client program, asserting one snapshotted queue entry per send, owner attribution, and immutability under post-send edits |

**Correction (later fix).** The composer lifecycle above — "a `DraftState` enters synced state on
the first keystroke" — is what this plan specified, but the delivered browser wiring published the
slot when the composer *mounted*, so every peer that had ever opened the session showed an empty
draft box on everyone's composer, and the slot stayed in the persisted doc after they left. The
rule now lives in one place (`DraftSlot`, wired on the client's doc): a slot exists **iff** its
author's body has content — published on the first keystroke, retracted when the body empties. The
Session Process sweeps the empty slots older docs accumulated at boot
(`SyncedStateSync.removeEmptyDrafts`), where no peer is connected and an empty body cannot be a
draft in progress.

**Superseded: owner-sends, and the read-only mirror.** Two of this plan's decisions did not
survive contact with the product. The delivered UI rendered other peers' drafts READ-ONLY, so the
"collaborate" mode above existed only in tests — and "owner sends" meant a peer who wrote half a
message could not send it. Both are now gone:

- Any co-editor may edit any draft (the body was always a CRDT; the carets were always presence)
  and any co-editor may send it. The entry is still attributed to the draft's AUTHOR — the sender
  committed it, the author started it — so the queue stays as legible as this plan wanted.
- The double-send race that argued for owner-sends is now unrepresentable rather than avoided:
  `DraftState` carries the `QueueId` it will become, minted by its author when the slot is
  published, so every sender writes the SAME queue key and two concurrent sends merge into one
  entry. A derived key beat a policy.
- The composer shows ONE draft at a time: someone else's in-flight draft is what you land in
  (joining is the default), the rest are one-line summaries with live-caret dots, and "new message"
  is the way out. That state is per-client (`ComposerChoice`), never synced — two people may have
  different drafts open in the same session.

Discard stays the author's alone: a co-editor collapses a draft, it does not destroy one.

**Follow-up (not done):** invariant 4 (clean send) is now pinned by its own `property {}`
block; invariants 1/3/5 are structural (the `Map<PeerId,_>` type and Step 05's existing
convergence coverage). Invariant 2 (participation) remains the noted follow-up — adding
co-edit / offline-rejoin draft schedules to the Hedgehog harness would pin it directly.

## Risks & open questions

- **"Their own" vs "a shared draft".** This plan has no ownerless shared draft — the
  collaborative case is always *someone's* slot that others join. If a truly ownerless
  "session draft" is later wanted, it is one extra optional slot (`SharedBrief` is the
  precedent), not a change to the per-peer invariant.
