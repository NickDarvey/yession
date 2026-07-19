# Bug note — `yXmlFragmentToProseMirrorRootNode` mutates the live doc it reads

> Status: **mitigated + hardened** (see [Mitigation](#mitigation-shipped) and
> [Resolution](#resolution-of-the-open-questions)). An upstream issue is drafted at
> [y-prosemirror-upstream-issue.md](y-prosemirror-upstream-issue.md), not yet filed.
> Discovered while adding rich-text hyperlinks (`Markdown.ofFragment` on a body a peer had
> just linked).

## One-line summary

`y-prosemirror`'s `yXmlFragmentToProseMirrorRootNode` (the function `Markdown.ofFragment`
uses to turn a body's `Y.XmlFragment` into a ProseMirror doc for serialization) is **not a
pure read**. On a *live, locally-authored* Yjs doc it can **delete a text node from the CRDT
as a side effect of reading it**, silently dropping a whole block from the body.

## Where it lives

- Consumer: [`src/Yession.Domain/Markdown.fs`](../src/Yession.Domain/Markdown.fs) — `ofFragment`.
- Root cause: `node_modules/y-prosemirror/src/plugins/sync-plugin.js`, function
  `createNodeFromYElement`, the branch commented *"This is a fix for #160 -- duplication of
  characters when two Y.Text exist next to each other."* It runs during
  `yXmlFragmentToProseMirrorRootNode` → `yXmlFragmentToProseMirrorFragment` →
  `createNodeFromYElement` → `createChildren`.

The offending code (abridged):

```js
// createChildren, for a Y.XmlText child:
const nextytext = type._item.right?.content?.type
if (nextytext instanceof Y.Text &&
    !nextytext._item.deleted &&
    nextytext._item.id.client === nextytext.doc.clientID) {   // <-- only for the reading doc's OWN client
  type.applyDelta([{ retain: type.length }, ...nextytext.toDelta()])
  nextytext.doc.transact(tr => { nextytext._item.delete(tr) })  // <-- DELETES from the CRDT, during a read
}
```

The `id.client === doc.clientID` guard is why it only bites the **authoring** client reading
its **own** live doc. A different doc (different `clientID`) that received the same content over
the wire skips the branch entirely.

## Why it surfaced with links (not plain text)

The merge fires when a text node's immediate right-sibling (`_item.right`) is another
`Y.Text`. Whole-block **link marks** produce exactly that adjacency in the Yjs item list
across paragraph boundaries in a way plain paragraphs did not, so the corruption showed up the
moment a body contained two linked blocks. It is not fundamentally link-specific — any layout
that puts two same-client `Y.Text` items adjacent can trigger it — links are just the reliable
trigger we hit.

## Precise repro

Two equivalent ways to reproduce. The **headless (Node, no browser)** one is the cleanest.

### A. Headless — `yXmlFragmentToProseMirrorRootNode` deletes on read

Runs against the installed `y-prosemirror` / `yjs`. Note the `clientID` line — that is what
makes the difference.

```js
// node --input-type=module
import * as Y from 'yjs'
import { schema } from 'prosemirror-markdown'
import { yXmlFragmentToProseMirrorRootNode } from 'y-prosemirror'

const doc = new Y.Doc()
const frag = doc.getXmlFragment('body')

// Author two linked paragraphs *as this doc's own client* (the guard cares about clientID).
const mk = (txt, href) => {
  const p = new Y.XmlElement('paragraph')
  const t = new Y.XmlText()
  t.insert(0, txt, { link: { href, title: null } })
  p.insert(0, [t]); return p
}
frag.insert(0, [mk('one', 'https://a.test'), mk('two', 'https://b.test')])

console.log('before:', frag.toString())
// -> <paragraph><link ...>one</link></paragraph><paragraph><link ...>two</link></paragraph>

yXmlFragmentToProseMirrorRootNode(frag, schema)   // "read" — but it MUTATES

console.log('after :', frag.toString())
// EXPECTED: unchanged.  ACTUAL (when the #160 adjacency + clientID guard line up): a block is gone.
```

> Caveat: whether the standalone snippet trips the adjacency depends on the exact item layout
> `insert` produces; the browser path (B) reproduces it deterministically because the editor's
> `applyDelta`-authored text nodes reliably end up adjacent.

### B. In the editor (deterministic) — read corrupts the next edit

This is how it actually bit us. Reproduced with the host-free editor harness
(`app/browser/EditorHarness.fs`, driven by Playwright). Sequence:

1. Type `docs`, select all, **paste** `https://paste.example.com` over it → `docs` becomes a link.
2. `Markdown.ofFragment` the live body (in the harness, `window.__md()`), read the raw CRDT right
   after — **still correct** (`<p><a>docs</a></p>`), so the read looks harmless.
3. Press `End`, `Enter`, type `linear`.
4. Read the CRDT again → it is now `<p>linear</p>`. **The `docs` link paragraph is gone.**

Key detail that makes it look like a heisenbug: the deletion is **not observable immediately
after the read** — the raw fragment reads correct right after `ofFragment`. It only manifests on
the **next edit**, which re-syncs against a binding whose mapping the read left inconsistent. So
"read, check, looks fine, keep typing, content vanishes."

Controls that pin the cause (all run during investigation):
- Same steps but **no `ofFragment` call** between paste and typing → both blocks survive.
- Same steps but with a **~300 ms settle** before `ofFragment` → both blocks survive (timing race).
- The **raw CRDT is always internally correct** until the read+edit interaction; the editor's own
  PM↔Y sync is not the culprit.

## Consequences

- **Data loss in the durable record.** `MessageSent.Body` is produced by serializing the body to
  Markdown. Any code path that serializes a *live, local* body and then the user keeps editing can
  drop a block from what gets sent/stored — silently, no error.
- **Silent, not loud.** No exception, no log. The body just comes back short.
- **Timing-dependent.** Needs read-then-edit within a short window, which makes it flaky and hard
  to reproduce on demand — the worst kind for a durable-facts system.
- **Scope in this codebase:** the send path calls `Markdown.copy` (which calls `ofFragment`) on
  the *live local draft*. The Session Process reads bodies on a *separate doc* (different
  `clientID`) so it is not exposed by the guard. The timeline renders from Markdown strings, not
  live fragments.

## Mitigation (shipped)

`Markdown.ofFragment` now serializes from a **detached snapshot** rather than the live fragment:

```
snapshot = new Y.Doc()
Y.applyUpdate(snapshot, Y.encodeStateAsUpdate(liveDoc))
yXmlFragmentToProseMirrorRootNode(snapshot.getXmlFragment(rootName), schema)
```

The snapshot doc has a **different `clientID`**, so the `#160` guard is false and the merge never
runs; the live body is never touched. Cost: one whole-doc `encodeStateAsUpdate`/`applyUpdate`
per serialize — fine because `ofFragment` is called on send/drain, not per keystroke; bodies are
small.

## Resolution of the open questions

1. **Snapshot copy acceptable long-term?** Kept, with a caveat: the cost is **O(whole doc), not
   O(body)** — `BodyKey.queued <id>` means the doc accumulates a body root per message, so each
   serialize copies the full session history. Fine at current scale (send/drain only). If it ever
   gets hot, the O(1) alternative is temporarily reassigning `doc.clientID` around the read (the
   merge guard compares against the *current* `clientID`, and a pure read runs no transactions) —
   but it leans on the same internals, so not worth it until measured.
2. **Upstream fix?** No existing y-prosemirror issue covers this (searched 2026-07-19; bug
   confirmed present in 1.3.7, our locked version and latest). A ready-to-file issue with a
   deterministic headless repro and a suggested fix (gate the merge on binding context) is in
   [y-prosemirror-upstream-issue.md](y-prosemirror-upstream-issue.md). N.B. this repo carries no
   npm patch mechanism today, so "patch + version bump" would be new infrastructure — filing
   upstream and keeping the snapshot until a release lands is the cheaper path.
3. **Other live-fragment readers?** Audited: only `Markdown.ofFragment` converts a live local
   fragment; `initProseMirrorDoc` runs only inside `mountEditor`, where the binding legitimately
   owns the doc. `ofFragment` itself had one hole — a doc-attached but *nested* fragment (no root
   name) silently took the live-read path — now closed: it refuses loudly instead
   (`Markdown.fs`), pinned by a test.
4. **Test coverage gap.** Closed. The trick that makes the adjacency deterministic in Node:
   author the two `Y.XmlText` siblings in **separate transactions** (each transaction produces a
   distinct item, so the second is the first's `_item.right`). The new guard
   (`ofFragment is a pure read`, `tests/Yession.Tests/Editor.fs`) asserts at the **CRDT level**
   (child count + no new ops since a state vector) — necessarily, because the merge is
   content-preserving: markdown-level round-trip equality passes even when the CRDT was mutated,
   which is also why the read "looked harmless" in the original investigation. Verified by
   mutation testing: neutering the snapshot makes the new tests fail while the markdown-level
   test still passes.
