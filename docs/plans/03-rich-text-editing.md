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

### The linchpin: `Encode.text` is value-semantics; `y-prosemirror` needs a live handle

Today `Sync.fs` binds a body with `Encode.text (AVal.constant d.Body)` and
`Decode.object.optional "body" Decode.text` — the Elmish model **owns a `Ylmish.Text` value**
and Ylmish reconciles it into the `Y.Text`. `y-prosemirror`, by contrast, binds
**imperatively to the live `Y.XmlFragment`** and owns it directly. So the Ylmish change is *not*
a value-semantics `Encode.xmlFragment` mirroring `Encode.text` (that would force us to diff a
ProseMirror doc against an F# value and throw away the whole point). It is an **opaque /
live-handle element** — the "add a xml element to `CustomElement`" shape: Ylmish materializes a
named Yjs `XmlFragment` under the encoded object and **hands the app the live handle**, which
the editor gives to `y-prosemirror`. The Elmish model then holds an opaque body **handle** per
id, not a text value. Editor edits flow editor → live `Y.XmlFragment` → Ylmish relays the
update; they no longer pass through `EditDraftBodyMsg`/`EditQueuedBodyMsg` (those body-text
messages retire; start / send / reorder / delete stay).

## Step 0 — spike first (de-risk the boundary before committing)

The Ylmish package (`Ylmish 1.0.0-beta0218`) is not on disk in this environment, and the
live-handle binding is the riskiest part. Do this before any product edit:

1. `mise run restore`, then read Ylmish's `Codec` surface: the `Encoded` cases, whether an
   opaque/custom shared-element case (`CustomElement` or similar) already exists, and **how a
   decoded field reaches the model** (value vs. live handle). Confirm `Fable.Yjs` exposes
   `Y.XmlFragment`/`Y.XmlElement`.
2. Throwaway prototype: hand-create a `Y.XmlFragment` on a `Y.Doc`, mount a ProseMirror editor
   with `ySyncPlugin`/`yCursorPlugin`/`yUndoPlugin`, and confirm two docs converge over a
   round-trip — proving the `y-prosemirror`↔Yjs wiring independent of Ylmish and of Lit.
3. Decide the Ylmish change from (1): a first-class `Encode.xmlFragment` / opaque
   `CustomElement` that yields a **live handle**. If it must land upstream in Ylmish, note the
   version bump; if an existing escape hatch already suffices, prototype through it first. The
   proposal is: **add an XML element to Ylmish's `CustomElement` (live-handle) codec.**

## File-by-file changes (after the spike confirms feasibility)

- **Ylmish (upstream package)** — add/confirm an XML-element codec that yields a **live
  `Y.XmlFragment` handle** at a named key (`Encode.xmlFragment` + `Decode.xmlFragment`, or an
  opaque `CustomElement`). Its own unit test in the Ylmish repo (encode → doc → decode of an
  XML body handle). Consumed here via a version bump in
  [`Directory.Packages.props`](../../Directory.Packages.props).
- **[`src/Yession.Domain/SessionState.fs`](../../src/Yession.Domain/SessionState.fs)** —
  `DraftState.Body` and `QueuedMessage.Body` change from `Ylmish.Text` to the rich-body handle
  type (`Ylmish.XmlFragment` / a `RichBody` wrapper). `SharedBrief` stays a plain string unless
  it also needs richness.
- **[`src/Yession.Domain/Sync.fs`](../../src/Yession.Domain/Sync.fs)** — swap
  `Encode.text`/`Decode.text` for the XML codec in `encodeDraft`, `encodeQueued`, `decodeDraft`,
  `decodeQueued`; author/order unchanged. Keep the entry-skipping totality of
  `draftsToDomain`/`queueToDomain` (the doc is shared with peers we don't control).
- **[`src/Yession.App/Model.fs`](../../src/Yession.App/Model.fs)** — retire
  `EditDraftBodyMsg`/`EditQueuedBodyMsg` (body edits go through the editor→Yjs binding, not the
  reducer). `StartDraftMsg` seeds an **empty** rich body; `SendDraftMsg` moves the same handle
  draft→queue; `ReorderQueuedMsg`/`DeleteQueuedMsg` are unaffected.
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
