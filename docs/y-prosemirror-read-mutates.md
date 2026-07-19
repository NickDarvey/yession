# Bug note — `yXmlFragmentToProseMirrorRootNode` mutates the live doc it reads

> Status: **mitigated** (see [Mitigation](#mitigation-shipped)). This note is for you to
> decide whether the mitigation is enough or whether to push a fix upstream.
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

## Open questions for you

1. **Is the snapshot copy acceptable long-term?** It copies the *entire* doc (all body roots) per
   serialize. If bodies grow or serialization gets hot, prefer copying just the one root, or cache.
2. **Upstream fix?** The `id.client === doc.clientID` merge deleting during a nominally-read API is
   arguably a `y-prosemirror` bug. Worth a minimal repro (snippet A, hardened to force adjacency)
   and an issue against `y-prosemirror`. We already carry a patch to that repo elsewhere, so an
   upstream fix + version bump is a viable path and would let us drop the snapshot dance.
3. **Any other live-fragment readers?** Audit for any *other* call that runs
   `yXmlFragmentToProseMirrorRootNode` / `initProseMirrorDoc` on a live local doc. Today only
   `Markdown.ofFragment` does, and it is now snapshot-guarded — but the trap is easy to reintroduce.
4. **Test coverage gap.** The Node regression guard (`ofFragment does not mutate the source`) only
   bites if the adjacency triggers in Node; the deterministic guard is the browser E2E. If you want
   a hard headless guard, harden snippet A to force the adjacency and assert `frag.toString()` is
   unchanged across a read.
