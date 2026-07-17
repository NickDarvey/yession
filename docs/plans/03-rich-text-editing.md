# Step — Linear-style rich text editing

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
- **JS/Fable rich-editor module (new)** — a ProseMirror/TipTap editor factory bound to a body's
  `Y.XmlFragment`: StarterKit/`prosemirror-inputrules` for markdown-typing (`#`, `-`, `1.`,
  `**`, `` ` ``, ``` ``` ```, `>`, links), `prosemirror-markdown` for markdown **paste**, and
  `ySyncPlugin`/`yCursorPlugin`/`yUndoPlugin` for collaboration. Schema = a Linear-like subset
  (headings, bold/italic/strike/code marks, bullet/ordered lists, code blocks, blockquote,
  links). Add deps to [`package.json`](../../package.json) (`prosemirror-*` + `y-prosemirror`,
  or `@tiptap/*` + `@tiptap/extension-collaboration`).
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
  — replace `Ylmish.Text.toString entry.Body` with a ProseMirror-doc→markdown serialization
  (`prosemirror-markdown` `defaultMarkdownSerializer` over the `Y.XmlFragment`) so
  `MessageSent.Body` stays markdown. The Session Process already observes the doc without its own
  Ylmish binding (see `Sync.fs`), so it reads the fragment and serializes JS-side.
- **[`src/Yession.App/View.fs`](../../src/Yession.App/View.fs) (timeline)** — sent-message
  bodies are now markdown strings; render them **formatted** (read-only markdown→HTML) in
  `conversation` so the timeline matches the rich input. A small dependency-light renderer, or
  the editor's parser in read-only mode.

## Verification (automated, per design.md §2.2)

- `mise run build` — type-checks the solution + Fable-compiles the host; proves the new
  `SessionState`/`Sync` body types, the retired messages, and the reworked view compile.
- `mise run test` — existing WebRTC/UI E2E stays green; update the draft/queue selectors to the
  editor mount and keep all other `data-*` hooks.
- New E2E / browser assertions:
  - typing `**x**` produces a bold mark and the `**` syntax is **not** persisted;
  - pasting a markdown block yields headings/lists;
  - two clients editing one body converge (collab round-trip over real WebRTC);
  - a rich body drained by the agent yields the expected **markdown** in `MessageSent.Body`;
  - the timeline renders that markdown formatted.
- The Ylmish codec change carries its own unit test (encode→doc→decode of an XML body handle) in
  the Ylmish repo before the version bump is consumed here.

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
- **TipTap vs. raw ProseMirror** (open sub-decision, resolve during the spike): TipTap is fastest
  (official Yjs collab extension, StarterKit input rules, Markdown extension); raw ProseMirror is
  closest to Linear with fewer layers. Recommend TipTap unless the abstraction fights the
  Fable/Lit mount.

## Open questions (resolve during Step 0)

1. Does Ylmish already expose an opaque/custom shared-element case, or must `Encode.xmlFragment`
   be added and released? (Drives whether an upstream version bump is on the critical path.)
2. Does the model hold a **live handle** or a **snapshot**? Determines the `SessionState.Body`
   type and how `View.fs` hands the fragment to the editor.
3. TipTap vs. raw ProseMirror under Fable + a Lit mount host (bundle size, interop friction).

## Rollout

1. Step 0 spike: confirm Ylmish's codec surface and prove the `y-prosemirror`↔Yjs binding.
2. Land the Ylmish XML-element codec (upstream) + version bump here.
3. Migrate the body type (`SessionState.fs`, `Sync.fs`, `Model.fs`); `mise run build`.
4. Add the editor module + deps; wire the stable Lit mount host (`View.fs`, `Browser.fs`).
5. Markdown serialization at the drain (`Scheduler.fs`) and formatted timeline rendering.
6. Extend the E2E/browser suite; `mise run test`, then `mise run verify`.
