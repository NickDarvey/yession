# Draft upstream issue — yjs/y-prosemirror

> Ready to file at <https://github.com/yjs/y-prosemirror/issues/new>. No existing issue covers
> this (searched 2026-07-19). Companion note: [y-prosemirror-read-mutates.md](y-prosemirror-read-mutates.md).

---

**Title:** `yXmlFragmentToProseMirrorRootNode` mutates the Y.Doc it reads — the #160 merge deletes a `Y.XmlText` during conversion

## Environment

- `y-prosemirror` 1.3.7 (current npm `latest`) — the whole released 1.x line is affected
  (the code landed in 627b6b22, 2024-08-05, closing #160/#161)
- `yjs` 13.6.31
- Node 24 (headless — no editor, no binding involved)

Master appears **unaffected**: the v2 rewrite for yjs v14 (delta-based `fragmentToPm` in
`sync-utils.js`) no longer contains this merge-on-read path. So this is a request for a **1.x
maintenance patch**, since 1.3.7 is what npm installs today.

## Summary

`yXmlFragmentToProseMirrorRootNode` / `yXmlFragmentToProseMirrorFragment` are conversion
utilities, but they are not pure reads. `createNodeFromYElement`'s `createChildren` contains the
fix for #160 (merging adjacent `Y.Text` siblings), which **writes to the CRDT while
converting**: it `applyDelta`s the right sibling's content into the left and then **deletes the
right sibling** ([`sync-plugin.js` lines 751–763][src]):

```js
const nextytext = /** @type {Y.ContentType} */ (type._item.right?.content)?.type
if (nextytext instanceof Y.Text && !nextytext._item.deleted && nextytext._item.id.client === nextytext.doc.clientID) {
  type.applyDelta([{ retain: type.length }, ...nextytext.toDelta()])
  nextytext.doc.transact(tr => { nextytext._item.delete(tr) })
}
```

The `id.client === doc.clientID` guard means this only fires when the doc being converted
contains content authored by its own client — i.e. exactly the doc a local editor is bound to.

[src]: https://github.com/yjs/y-prosemirror/blob/v1.3.7/src/plugins/sync-plugin.js#L751-L763

## Reproduction (deterministic, headless)

The adjacency triggers whenever two same-client `Y.XmlText` siblings are authored in separate
transactions (each transaction produces a distinct item, so the second is the first's
`_item.right`) — no editor needed:

```js
// node --input-type=module   (y-prosemirror 1.3.7, yjs 13.6.31, prosemirror-markdown 1.13.x)
import * as Y from 'yjs'
import { schema } from 'prosemirror-markdown'
import { yXmlFragmentToProseMirrorRootNode } from 'y-prosemirror'

const doc = new Y.Doc()
const frag = doc.getXmlFragment('body')
const p = new Y.XmlElement('paragraph')
frag.insert(0, [p])

doc.transact(() => {
  const t = new Y.XmlText()
  t.insert(0, 'one ', { link: { href: 'https://a.test', title: null } })
  p.insert(0, [t])
})
doc.transact(() => {
  const t = new Y.XmlText()
  t.insert(0, 'two', { link: { href: 'https://b.test', title: null } })
  p.insert(1, [t])
})

console.log('children before:', p.length)          // 2
const sv = Y.encodeStateVector(doc)

yXmlFragmentToProseMirrorRootNode(frag, schema)     // nominally a pure read

console.log('children after :', p.length)          // 1  <-- a Y.XmlText was deleted
console.log('new CRDT ops   :', Y.encodeStateAsUpdate(doc, sv).length > 2)  // true
```

**Expected:** converting to a ProseMirror node leaves the `Y.Doc` unchanged.
**Actual:** the paragraph's second `Y.XmlText` is merged into the first and deleted from the
CRDT; the doc emits new ops.

## With a `ySyncPlugin` binding attached, the merge is re-entrant and duplicates content

Standalone, the merge is content-preserving (`frag.toString()` is unchanged). But the merge
runs as **two transactions** (the `applyDelta`, then the delete), and if a `ySyncPlugin`
binding is attached to the doc — the normal situation for a doc containing own-client content —
the binding's observer (`_typeChanged`) fires after the **first** one. It evicts the changed
parent from its mapping (`transaction.changedParentTypes.forEach(delType)`) and re-renders the
fragment with a full `createNodeFromYElement` descent — which reaches the same still-adjacent
pair and **runs the merge again**, appending the right sibling's content a second time.

Demonstrable headlessly by attaching an observer that replicates `_typeChanged`'s eviction +
re-render (including its `mux` semantics), then running the read from the previous section:

```js
// binding replica: on any foreign transaction, evict changed types then re-render fully
const meta = createEmptyMeta()
const mux = createMutex()
frag.observeDeep((events, transaction) => mux(() => {
  Y.iterateDeletedStructs(transaction, transaction.deleteSet, s => {
    if (s.constructor === Y.Item) s.content.type && meta.mapping.delete(s.content.type)
  })
  const delType = (_, type) => meta.mapping.delete(type)
  transaction.changed.forEach(delType)
  transaction.changedParentTypes.forEach(delType)
  frag.toArray().map(t => meta.mapping.get(t) ?? createNodeFromYElement(t, schema, meta))
}))

yXmlFragmentToProseMirrorRootNode(frag, schema)   // the "read"

// paragraph content before: "one "|"two"  (two Y.XmlText children)
// paragraph content after :  "one twotwo" (one child — content DUPLICATED, sibling deleted)
```

(`createNodeFromYElement` / `createEmptyMeta` imported from `src/plugins/sync-plugin.js`;
in the real plugin the same descent runs inside `_typeChanged`, which additionally dispatches
a whole-document ProseMirror replace — in the middle of the caller's "read".)

## Real-world impact

We hit this in production shape, where the adjacent text node was empty (whole-block link
layout), making the duplication invisible in content reads: serialize the live body on send →
raw CRDT still *looks* correct → user keeps typing → the next edit, diffed through
`updateYFragment` against the binding state the read had churned, **silently dropped a whole
linked paragraph** from the CRDT. Reproduced under an editor E2E with controls: no read
between paste and typing → block survives; a ~300 ms settle before the read → block survives.
No error, no log — content just goes missing on a later edit, which made this extremely hard
to trace.

Beyond bindings: any consumer using the converter for read-only purposes (serialization,
export, indexing, diffing) is unknowingly writing to — and broadcasting ops from — the doc it
reads.

## Suggested fix

The merge is a normalization that only the editor binding needs (it owns the doc and the
mapping). Suggestion: gate it on binding context — e.g. a flag on the `meta` /
`BindingMetadata` that only the sync-plugin's render paths set — so the exported converters
(`yXmlFragmentToProseMirrorFragment`, `yXmlFragmentToProseMirrorRootNode`) become pure reads.
Without the merge the converter's output is already correct: adjacent `Y.Text` siblings simply
become adjacent ProseMirror text nodes.

Happy to send a PR against a 1.x maintenance branch if that direction sounds right.

## Possibly related field reports

["Random, rare data loss in production with y-prosemirror"](https://discuss.yjs.dev/t/random-rare-data-loss-in-production-with-y-prosemirror/3728)
(discuss.yjs.dev, currently offline) describes the same fingerprint — rich-text-only, rare,
silent, unreproducible data loss, while Y.Map/Y.Array data in the same app is never affected —
with no root cause found. Not confirmed to be this bug, but any app that converts a live local
doc (serialization on save/export is a common pattern) and keeps editing would produce exactly
that symptom profile.

## Workaround (for anyone else hitting this)

Convert from a detached snapshot instead of the live doc — a fresh doc has a different
`clientID`, so the merge guard never fires:

```js
const snapshot = new Y.Doc()
Y.applyUpdate(snapshot, Y.encodeStateAsUpdate(liveDoc))
const node = yXmlFragmentToProseMirrorRootNode(snapshot.getXmlFragment('body'), schema)
```
