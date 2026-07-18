# Plan — rich-text test cleanup (AAA-targeted, high-signal)

The XmlFragment flip broke ~83 test call sites that referenced the old `Ylmish.Text` body.
That number is the real finding: **the body was scaffolding, not the subject** in almost all
of them. A test earns its place only if it covers one behaviour nothing else does. So we
introduce a body-agnostic seam (kills the coupling), keep tests whose *point* survives, delete
redundant/obsolete ones, and cover the genuinely new rich-body behaviour once, at the right
layer. Every retained test has a stated reason to exist.

Grounded in a full per-test inventory (all 11 suites read). Of the ~83 sites, only **6 tests
assert body content**; the rest are `setDraft`-once scaffolding.

## 1. The seam — remove the coupling (do first)

**Centralize send in one place.** `App.connect` gains the `BodyRegistry`; `Connection.SendDraft
peer` does capture-markdown → mint queueId → dispatch `SendDraftMsg` → seed the new queue
fragment, in ONE implementation. `Browser`'s `actions.SendDraft` becomes just
`connection.SendDraft` (delete Browser's bespoke `sendDraft`); tests get copy-on-send for free.

**A minimal body-agnostic harness seam** (`Support.fs`) — the ONLY code that touches a fragment:
- `Client` gains `Registry : BodyRegistry` (created in the connectors, threaded into `makeProgram`).
- `compose client peer markdown : Async<unit>` — ensure the slot, await its fragment, write it
  (`Markdown.intoFragment`). Replaces `editBody`/`setDraft`.
- `draftBody client peer : string option` — read via `registry.TryFragment` + `Markdown.ofFragment`.
  Replaces `bodyOf`.
- `queueBodies client : (string * string) list` and `queueBody client queueId` — read queue
  bodies via the doc (`SyncedStateSync.queuedBodyMarkdown`). Replaces `queueView`/`queueBodyOf`.
- **Delete** `queueBodyOf` (dead — no caller) and `editQueued` (only two callers; reworked below).

Result: the ~83 sites collapse to these four helpers. A `setDraft`-once scaffold becomes
`do! compose client peer "hi"`; a body assertion becomes `draftBody`/`queueBodies`. No suite
outside the seam knows the body is a fragment.

## 2. Per-suite decisions (keep / delete / rework — with the reason)

**Untouched — no draft/queue body** (need zero work; listed so we know they were reviewed):
Agent `turnTests`; Phase2 `authorityTests`/`environmentProjectionTests`/`commandFoldTests`/
`acceptanceTests`; Phase4 `stateTests`/`uiRenderTests`/control-RPC/UI-flow; EventsHttp
`chunkMathTests`+headers; Client.fs; InMemory.fs; Sync `queueUnitTests` (bodies are only labels
— fix the local `entry` builder to drop the body arg).

**Rework to the seam — body is incidental (keep the assertion, swap the scaffolding):**
| Suite / test | Why it exists (kept) |
|---|---|
| Sync E2E-2/3 (send→drain exactly-once, snapshot immutable, queue empties) | the send→drain integration over real WebRTC |
| Sync E2E-4/E2E-7 (offset catch-up; unsent draft renders in editor not timeline) | client-side offset catch-up + routing |
| Sync codec: drafts-not-conversation; enqueue round-trips | the codec boundary + draft→queue transition |
| Properties inv-1,3,4,5 + draft-invariant-4 | queue invariants (exactly-once / order / clean-send / no-post-terminal-mutation) |
| Phase3 delete/edit/reorder-vs-accept races; crash-repair; interrupt; doc-persistence; torn-tail | the concurrency contract + durability |
| Phase2 lazy-env E2E-1/2/7, command log E2E-3/4, catch-up E2E-8, durable-log restart | authority/lifecycle integration |
| Phase4 spawn-contract, child-RPC, composition (release gate) | the process/packaging topology |
| Agent E2E-5; EventsHttp fetcher-parity | agent turn from events; HTTP vs frame timeline parity |
| Acceptance UI checklist | every `data-*` hook renders (fix the direct `Ylmish.Text.ofString` fixture) |

**Delete — redundant or obsolete (the behaviour moved to the fragment CRDT):**
| Test | Why deleted |
|---|---|
| Sync codec "collaborate on one draft slot (bodies interleave → oh, hello world)" | collaborative char-merge is now the **fragment CRDT**, covered by the `Editor` cheap test "edits converge across two docs" + Browser E2E. In-memory dup of E2E-1. |
| Sync "converge on the title" (in-memory) | dup of InMemory "converge on the title through the Host relay" (stronger — real relay). |
| `titlePresenceTests` EditTitleMsg-sets-title | trivial reducer, subsumed by the title codec + relay tests. |

**Consolidate near-duplicate clusters** (keep the strongest per cluster, drop re-assertions):
- *Send-snapshot immutability* asserted in ~4 places → keep the **property** (draft-invariant-4);
  drop the immutability re-assert from Sync `enqueue round-trips` (keep only the transition).
- *Draft interleave "oh, hello world"* → keep **only** Sync/E2E-1, reworked to assert fragment
  convergence via `draftBody` over real WebRTC (the one real-transport rich-body convergence
  test); the in-memory twin is deleted (above).

## 3. Cover the new rich-body behaviour once, at the right layer
- **Fragment CRDT convergence / round-trip / copy** — `Editor` cheap tests (already exist).
- **Snapshot fidelity (Properties inv-2)** — reworked: the drain snapshots `Markdown.ofFragment`;
  the shadow oracle predicts markdown; the schedule edits via the seam. Kept — it's the one
  place tying drain output to replica content.
- **Typing markdown → formatted, in the real app** — fold `scripts/editor-e2e.fsx` into
  `Browser.tests` (#9's Pyxpecto browser suite already spawns the host + drives Chromium):
  update its composer interaction to the `[data-rich-body]` editor, assert a typed heading/bold
  renders and a sent rich message appears in the timeline as markdown. **Delete
  `scripts/editor-e2e.fsx`** (orphaned by #9's move to the in-suite `Browser.tests`).

## 4. Net effect
- The ~83 body sites become ~4 seam helpers used by the scaffolding tests.
- ~3 tests deleted outright (redundant/obsolete), a few immutability/interleave re-asserts trimmed.
- Every kept test has a one-line reason (table above). No behaviour loses its last cover: rich
  merge/typing moves to the `Editor` cheap tests + `Browser` E2E.

## 5. Verification
- `mise run test` (cheap tier, Node) green — the reworked model/property/integration suites.
- `mise run verify` (adds Browser E2E on the CLR) — the editor typing + real-app flow.
- Invariant intact: `git ls-files '*.js' '*.mjs' '*.cjs'` stays empty.
