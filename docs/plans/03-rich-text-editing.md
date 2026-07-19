# Step — Linear-style rich text editing

> **Status: delivered.** (`dcd4b7a`…`6d1739e`; test cleanup in [04-rich-text-test-cleanup.md](04-rich-text-test-cleanup.md))
>
> Client presentation · follows [02-metro-zune-styling.md](02-metro-zune-styling.md)
> Design context: [docs/design.md](../design.md) §1 (Ylmish is the sync boundary; Yjs is
> not the domain model), §2.2 (state split), §5 (invariants)

## Goal

Edit draft and queued-message bodies the way **Linear** does: type or paste Markdown
(`**bold**`, `# heading`, `- list`, `1.` ordered, `` `code` ``, ``` ``` ``` fences, `>` quote,
links) and see it **rendered as formatted rich text live** — the syntax is transformed away as
you type, not left as raw source. Collaboration, offline, and the agent path all keep working.

The interchange format stays **Markdown**. Everything downstream already treats a body as a
markdown string: the agent reads `Ylmish.Text.toString entry.Body`
([`Scheduler.fs`](../../src/Yession.SessionProcess/Scheduler.fs)) and that string becomes the
durable `MessageSent.Body`. Rich editing must preserve that — bodies serialize to markdown at
the drain point.

## Can we reuse Linear's implementation? No.

Researched (2026): Linear's editor is **ProseMirror-based** — rich content is stored as
ProseMirror JSON (`descriptionData`, validated against a ProseMirror schema; Linear changelog
2026-03-24), and their docs describe exactly this behaviour ("Type in Markdown or paste it
directly and it will be converted into rich text automatically"). But:

- **There is no `@linear/*` editor package** and no official or community port. Linear
  open-sources only its API (`@linear/sdk`, `@linear/cli`); the editor lives in the closed app.
- Linear does **not** use Yjs — it rides a bespoke transaction-based sync engine we should not
  and need not replicate.

So this is a **build-equivalent** task, not an adopt-theirs one. The reusable stack that
reproduces Linear's feel is **ProseMirror + Markdown input rules + Markdown paste + Yjs
(`y-prosemirror`)**. Markdown-typing is ProseMirror **input rules** (`prosemirror-inputrules`):
a `{regex → transform}` plugin rewrites the matched syntax into rich nodes/marks the instant
you type it. Markdown **paste** is a separate clipboard path through `prosemirror-markdown`'s
parser. TipTap wraps all of this (StarterKit input rules, a Markdown extension, an official
Yjs collaboration extension) and is the fastest route; raw ProseMirror is the closest match to
how Linear itself is built.

## Approach — WYSIWYG via ProseMirror over a Yjs XML fragment, through Ylmish

Bodies become a **structured ProseMirror document synced as a Yjs `XmlFragment`**, and that
fragment is exposed as a **first-class Ylmish-encoded field** — not a raw Yjs type the editor
grabs behind Ylmish's back. This keeps invariant *"Ylmish is the sync boundary / Yjs is not the
domain model"* (design.md §5) true: a body is still an Ylmish-encoded field; it is encoded as an
`XmlFragment` instead of `Text`.

### Two findings that shape the design (both from reading Ylmish source, one merged upstream)

**1. The live handle exists via `CustomElement` — and it's shipped.** Ylmish's escape hatch is
`Encode.custom` over a `CustomElement { Connect : BindContext -> IDisposable; Value : obj }`.
`Connect` receives live get-or-adopt getters and `Decode.custom` returns `c.Value`, so a custom
can hand the model a **live Yjs handle**. The only gap was that `BindContext` exposed
`GetText`/`GetMap`/`GetArray` but no `GetXmlFragment`. That is now **merged upstream**
(`NickDarvey/Ylmish` PR #135 → `1.0.0-beta0219`): `BindContext.GetXmlFragment` + an
`ensureXmlFragment` slot helper, wired at both the top-level and nested construction sites, with
`Fable.Yjs` already binding `YXmlFragment`/`getXmlFragment`. A `RichBody : CustomElement` can now
`ctx.GetXmlFragment()` in `Connect` and hand the fragment to `y-prosemirror`.

**2. A custom nested in a keyed `Encode.map` anchors but does NOT decode.** The drafts/queue
collections are `Encode.map` (dynamically keyed by `DraftId`/`QueueId`, offline-creatable). In
Ylmish's decode direction (`Binding.read`), a top-level `EncMap` field is read **structurally**
via `ElementOfY.ofYValue`, which only recognizes `Y.Text`/`Y.Map`/`Y.Array` — it never yields
`ElCustom`, and the per-item schema's customs are not overlaid. So today's `Encode.text` body
decodes fine (a `Y.Text` → `ElText`), but an `Encode.custom` XML body **cannot round-trip its
value through decode**. (Top-level fixed customs decode via `readSlot`; keyed-map *items* do not —
and a remote-created item has no local `CustomElement` instance to decode against anyway.)

### The design consequence: the body handle is app-resolved, not decoded

The rich body is still declared in the Ylmish schema via `Encode.custom` — that is what anchors
the nested `Y.XmlFragment` race-free at `drafts.<id>.body` and keeps the sync boundary honest —
but its **value is not decoded into the model**. Instead:

- A small **`RichBody` registry** keyed by `DraftId`/`QueueId` lives at the app composition root.
  `encodeDraft`/`encodeQueued` pull `registry.getOrCreate id` (a stable `RichBody` per id, reused
  across re-encodes for `Connect`-instance stability). On attach, each `RichBody.Connect` grabs
  the live fragment via `ctx.GetXmlFragment()` and stashes it.
- `decodeDraft`/`decodeQueued` **skip the body** (decode author/order only). The model's
  `DraftState`/`QueuedMessage` no longer carry a body *value*.
- The **View resolves the live fragment from the registry** by id at editor-mount time and hands
  it to `y-prosemirror`. Remote drafts get a `RichBody` on the encode pass that follows their
  decode; the mount tolerates a one-render lag (create-on-demand fallback).
- Editor edits flow editor → live `Y.XmlFragment` → y-webrtc → peers; they never pass through
  `EditDraftBodyMsg`/`EditQueuedBodyMsg` (those retire). Start/send/reorder/delete stay, but
  **send copies fragment *content*** draft→queue (Yjs shared types can't be re-parented): read the
  draft fragment, write its structure into the queue entry's fragment.

## Step 0 — spike: DONE (Ylmish source read, capability merged)

Read `NickDarvey/Ylmish@master` (`Codec.fs`, `Binding.fs`, `Fable.Yjs/Yjs.fs`). Confirmed the
`CustomElement` live-handle mechanism and the two findings above; implemented + merged the
`GetXmlFragment` addition (PR #135, `1.0.0-beta0219`, 113 tests green). `Directory.Packages.props`
is bumped to `beta0219`. Remaining de-risking (fold into the first build step): the ProseMirror +
`y-prosemirror` ↔ Yjs wiring and the Lit stable-mount, which are app-side, not Ylmish-side.

## File-by-file changes (after the spike confirms feasibility)

- **Ylmish (upstream) — DONE.** `BindContext.GetXmlFragment` + `ensureXmlFragment` merged
  (`NickDarvey/Ylmish` PR #135, `1.0.0-beta0219`). Consumed here via
  [`Directory.Packages.props`](../../Directory.Packages.props) (bumped `beta0218`→`beta0219`).
- **[`src/Yession.Domain/SessionState.fs`](../../src/Yession.Domain/SessionState.fs)** — drop the
  `Body : Ylmish.Text` *value* field from `DraftState` and `QueuedMessage` (the body lives in the
  doc as a `Y.XmlFragment`, resolved via the registry, not carried in the model). Author/order/id
  stay. `SharedBrief` unchanged.
- **`RichBody` + registry (new, app composition root)** — `RichBody : CustomElement` wrapping a
  `Y.XmlFragment` (Connect → `ctx.GetXmlFragment()`; `Value` unused since the body isn't decoded).
  A registry `getOrCreate : DraftId/QueueId -> RichBody` returns a stable instance per id.
- **[`src/Yession.Domain/Sync.fs`](../../src/Yession.Domain/Sync.fs)** — `encodeDraft`/`encodeQueued`
  emit `"body", Encode.custom (registry.getOrCreate id)` (anchors the fragment); the codec gains a
  registry parameter threaded from `App.makeProgram`. `decodeDraft`/`decodeQueued` **omit body**
  (author/order only). Keep the entry-skipping totality of `draftsToDomain`/`queueToDomain`.
- **[`src/Yession.App/Model.fs`](../../src/Yession.App/Model.fs)** — retire
  `EditDraftBodyMsg`/`EditQueuedBodyMsg` (body edits go through the editor→Yjs binding, not the
  reducer). `StartDraftMsg` just creates the draft (its empty fragment is anchored on encode);
  `SendDraftMsg` triggers the draft→queue **content copy** of the fragment;
  `ReorderQueuedMsg`/`DeleteQueuedMsg` unaffected.
- **Fable bindings for ProseMirror + y-prosemirror (new, pure F#)** — the repo forbids authored
  JavaScript (master #7: `git ls-files '*.js' '*.mjs' '*.cjs'` is empty; the only boundary is
  `[<Import>]`/`[<Emit>]` in F#). So the editor is **not** a `.mjs` shim. Following the
  **`Fable.Yjs` precedent** (its `Yjs.fs`/`Lib0.fs` are `ts2fable`-generated then hand-edited),
  generate Fable bindings from the packages' bundled `.d.ts` with **`ts2fable`** and hand-edit
  them, in a new bindings area (e.g. `src/Fable.ProseMirror/` mirroring `src/Fable.Yjs/`, or
  app-local `[<Import>]` modules for the few symbols used): `prosemirror-{model,state,view,
  keymap,commands,inputrules,schema-list,markdown}` and `y-prosemirror`. Add these npm packages
  to [`package.json`](../../package.json) — they supply the runtime JS the bindings import; the
  editor *code* stays F#.
- **`src/Yession.App/Editor.fs` (new, pure F#)** — the editor itself over those bindings: a
  Linear-like markdown schema (headings, bold/italic/strike/code marks, bullet/ordered lists,
  code blocks, blockquote, links), markdown **input rules** (`#`, `-`, `1.`, `>`, ```` ``` ````,
  `**b**`, `*i*`, `` `c` ``), a markdown **paste** handler (`prosemirror-markdown` parser), and
  `ySyncPlugin`/`yUndoPlugin` (optionally `yCursorPlugin`). Exposes `mountEditor`,
  `fragmentToMarkdown`, `copyFragment` as F# functions. Small regex/lambda glue uses `[<Emit>]`.
- **[`src/Yession.App/View.fs`](../../src/Yession.App/View.fs)** — replace the draft and queue
  `<textarea>`s (the `data-draft-input` / `data-queue-input` elements) with a **stable editor
  mount host** per body id. Keep every `data-*` hook so the E2E selectors resolve.
- **[`app/browser/Browser.fs`](../../app/browser/Browser.fs)** — the Lit DOM-ownership gotcha.
  `setState` runs `Lit.render` into `#app` on every model change; ProseMirror owns its subtree
  imperatively and Lit must never diff into it. Mount each editor **once per body id** and reuse
  the same `EditorView`/DOM node across renders — a Lit directive returning a stable host node
  keyed by `DraftId`/`QueueId`, mounted after first render and destroyed on removal. Mirror the
  existing hand-managed-DOM precedents in `Browser.fs` (focus preservation, timeline scroll
  preservation).
- **[`src/Yession.SessionProcess/Scheduler.fs`](../../src/Yession.SessionProcess/Scheduler.fs)**
  — replace `Ylmish.Text.toString entry.Body` with `Editor.fragmentToMarkdown` (the pure-F#
  serializer over `prosemirror-markdown`) applied to the queue entry's `Y.XmlFragment` so
  `MessageSent.Body` stays markdown. The Session Process already observes the doc without its own
  Ylmish binding (see `Sync.fs`); it reads `(doc.getMap "queue").get(id)."body"` and serializes it
  (missing/empty fragment → empty string).
- **[`src/Yession.App/View.fs`](../../src/Yession.App/View.fs) (timeline)** — sent-message
  bodies are now markdown strings; render them **formatted** (read-only markdown→HTML) in
  `conversation` so the timeline matches the rich input. A small dependency-light renderer, or
  the editor's parser in read-only mode.

## Verification (automated, per design.md §2.2)

- `mise run build` — type-checks the solution + Fable-compiles the host; proves the bindings, the
  new `SessionState`/`Sync` body types, the retired messages, `Editor.fs`, and the reworked view
  all compile, and that **no authored JS crept in** (`git ls-files '*.js' '*.mjs' '*.cjs'` stays
  empty — the master #7 invariant).
- `mise run test` — existing WebRTC/UI E2E stays green; update the draft/queue selectors to the
  editor mount and keep all other `data-*` hooks.
- **New browser assertions in the repo's F# Playwright E2E** (`scripts/browser-e2e.fsx`, the
  `Microsoft.Playwright` .NET driver that already drives two real Chromium peers against the live
  Session Process):
  - typing `**x**` produces a bold mark and the `**` syntax is **not** persisted;
  - pasting a markdown block yields headings/lists;
  - two peers editing one body converge (collab round-trip over real WebRTC);
  - a rich body drained by the agent yields the expected **markdown** in `MessageSent.Body`;
  - the timeline renders that markdown formatted.
- The Ylmish `GetXmlFragment` change carries its own unit test upstream (`NickDarvey/Ylmish` #135,
  already merged and green).

## Decisions & alternatives

- **Extend Ylmish (chosen)** over bypassing it: a raw editor-owned Yjs type would violate
  *"Ylmish is the sync boundary / Yjs is not the domain model"*; a first-class XML element keeps
  the invariant true. This is the agreed direction — propose the XML element on Ylmish's
  `CustomElement` (live-handle) codec.
- **Rejected — keep `Y.Text` markdown-source + CodeMirror live-preview.** Boundary-pure and
  zero agent-path change (Obsidian-style: markdown stays the document, decorations render it),
  but not true hide-the-syntax WYSIWYG. Set aside in favour of the structured route.
- **Rejected — ProseMirror over `Y.Text` with markdown round-trip.** Serializing doc→markdown
  on every keystroke into a text CRDT is an impedance mismatch that degrades collaborative merge.
- **Raw ProseMirror + `y-prosemirror` (chosen)** over TipTap: leanest deps, closest to how Linear
  is built, and no framework layer to fight the Fable/Lit mount. TipTap was rejected — its value
  is JS-side ergonomics we don't get from F#, and it is a heavier dep tree.
- **Pure-F# `ts2fable` bindings (chosen)** over an authored `.mjs` editor shim: the repo forbids
  authored JS (master #7 — `git ls-files '*.js' '*.mjs' '*.cjs'` must stay empty; interop is
  `[<Import>]`/`[<Emit>]` only). `Fable.Yjs` set the precedent (ts2fable-generated + hand-edited).
  An earlier `editor.mjs` shim was written and then **removed** for this reason.

## Open questions

1. ~~Ylmish custom-element case / live handle~~ — **resolved** (shipped in beta0219; the model holds
   a live handle via `RichBody`, but the body is *not* decoded — see the two findings above).
2. **Bindings scope:** full `ts2fable` generation into a `src/Fable.ProseMirror` project vs.
   hand-written `[<Import>]` modules for just the symbols the editor uses. Prefer the latter if the
   used surface is small; escalate to generated bindings if it sprawls.
3. **Lit stable-mount timing:** confirm (in the F# Playwright E2E) that a remote draft's fragment
   is available by the time its editor mounts, or that the create-on-demand fallback covers the lag.

## Rollout

1. Step 0 spike: confirm Ylmish's codec surface and prove the `y-prosemirror`↔Yjs binding.
2. Land the Ylmish XML-element codec (upstream) + version bump here.
3. Migrate the body type (`SessionState.fs`, `Sync.fs`, `Model.fs`); `mise run build`.
4. Add the editor module + deps; wire the stable Lit mount host (`View.fs`, `Browser.fs`).
5. Markdown serialization at the drain (`Scheduler.fs`) and formatted timeline rendering.
6. Extend the E2E/browser suite; `mise run test`, then `mise run verify`.

## Implementation note — the body is a top-level fragment root, not a nested custom (revised)

The shipped design anchors each rich body as a **top-level `Y.XmlFragment` root** keyed by
`BodyKey` (`draft:<peer>` / `queue:<id>`), managed directly by a doc-aware `BodyRegistry`
(`getXmlFragment` is idempotent and merges by name, so there is no creation race). It is a
sibling CRDT root the app co-manages on the doc — synced by the same update transport, read by
the Session Process straight from the doc — and is **deliberately not part of the Ylmish-encoded
state** (`encode`/`decode` name only `drafts`/`queue`/`title`/`sharedBrief`).

This replaces the earlier plan of nesting the body under the draft/queue map via `Encode.custom`
over a `RichBody`. Two findings forced the change:

1. A custom nested in a keyed `Encode.map` never yields `ElCustom` on Ylmish's structural decode,
   so the value would never round-trip anyway (already known).
2. **Ylmish's structural reader (`ElementOfY.ofYValue`) walks a `Y.XmlFragment` as a plain object
   and recurses into its cyclic internals — a stack overflow.** So a fragment reachable *anywhere*
   in the decoded tree crashes the decode. Both the client's `Binding.read` (which reads keyed-map
   entries structurally) and `SyncedStateSync.ofDoc` (a whole-doc structural read) hit this the
   instant a body exists. Keeping bodies out of the decoded tree — as sibling roots — is what makes
   the decode total.

Consequences in code:
- `SyncedStateSync.encode`/`encodeDraft`/`encodeQueued` carry no body; `encodeDraft` re-states the
  author only so the (otherwise empty) slot actually materializes a Yjs key.
- `SyncedStateSync.ofDoc` reads the four named roots directly (never a whole-doc structural read),
  sidestepping the body roots.
- `queuedBodyMarkdown`/`draftBodyMarkdown` read `doc.getXmlFragment(BodyKey…)`.
- `Connection.SendDraft` copies draft→queue markdown and then clears the (durable) draft root so
  the composer empties on send.
- `RichBody`/`CustomElement` are gone; `BodyRegistry(doc).Fragment key` is the whole surface.
