# Draft upstream issue — yjs/y-prosemirror

> Ready to file at <https://github.com/yjs/y-prosemirror/issues/new>. No existing issue covers
> this (searched 2026-07-19). Companion note: [y-prosemirror-read-mutates.md](y-prosemirror-read-mutates.md).

---

**Title:** `yXmlFragmentToProseMirrorRootNode` mutates the Y.Doc it reads — the #160 merge deletes a `Y.XmlText` during conversion

## Environment

- `y-prosemirror` 1.3.7 (current latest)
- `yjs` 13.6.31
- Node 24 (headless — no editor, no binding involved)

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

## Why this is nasty

The mutation is invisible at the content level — `frag.toString()` and serialized output are
unchanged, because the merge is content-preserving. But:

- If an editor binding (`ySyncPlugin`) is attached to the same doc, its `mapping` still
  references the deleted item. The next local edit re-syncs against that inconsistent mapping
  and can **silently drop a whole block** from the document. We hit this in production shape:
  serialize-body-on-send → user keeps typing → a linked paragraph vanishes. No error, no log.
- Any consumer using the converter for read-only purposes (serialization, export, indexing,
  diffing) is unknowingly writing to — and broadcasting ops from — the doc it reads.

## Suggested fix

The merge is a normalization that only the editor binding needs (it owns the doc and the
mapping). Suggestion: gate it on binding context — e.g. a flag on the `meta` /
`BindingMetadata` that only the sync-plugin's render paths set — so the exported converters
(`yXmlFragmentToProseMirrorFragment`, `yXmlFragmentToProseMirrorRootNode`) become pure reads.
Without the merge the converter's output is already correct: adjacent `Y.Text` siblings simply
become adjacent ProseMirror text nodes.

Happy to send a PR if that direction sounds right.

## Workaround (for anyone else hitting this)

Convert from a detached snapshot instead of the live doc — a fresh doc has a different
`clientID`, so the merge guard never fires:

```js
const snapshot = new Y.Doc()
Y.applyUpdate(snapshot, Y.encodeStateAsUpdate(liveDoc))
const node = yXmlFragmentToProseMirrorRootNode(snapshot.getXmlFragment('body'), schema)
```
