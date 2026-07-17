// Rich-text editor shim: raw ProseMirror + y-prosemirror over a live Y.XmlFragment.
// docs/plans/03-rich-text-editing.md. Isolates all ProseMirror wiring behind a tiny,
// F#-friendly surface (Editor.fs binds these four exports). Node-safe: nothing here
// touches `document` at module load, so the Session Process can import it for
// `fragmentToMarkdown` (draft/queue fragment -> markdown at drain time) without a DOM.
//
// Linear-style behaviour: type or paste Markdown, rendered live as formatted rich text.
// - typing  -> prosemirror-inputrules rewrite `# `, `- `, `1. `, `> `, ```` ``` ````,
//              `**b**`, `*i*`, `` `c` `` into nodes/marks as you type;
// - pasting -> plain-text paste is parsed as Markdown (defaultMarkdownParser);
// - storage -> a ProseMirror doc living in the Y.XmlFragment (ySyncPlugin owns it).

import { schema, defaultMarkdownParser, defaultMarkdownSerializer } from "prosemirror-markdown"
import { EditorState } from "prosemirror-state"
import { EditorView } from "prosemirror-view"
import { keymap } from "prosemirror-keymap"
import { baseKeymap, toggleMark } from "prosemirror-commands"
import {
  inputRules, wrappingInputRule, textblockTypeInputRule, InputRule,
  smartQuotes, emDash, ellipsis
} from "prosemirror-inputrules"
import { wrapInList, splitListItem, liftListItem, sinkListItem } from "prosemirror-schema-list"
import {
  ySyncPlugin, yUndoPlugin, undo as yUndo, redo as yRedo,
  yXmlFragmentToProseMirrorRootNode, prosemirrorToYXmlFragment
} from "y-prosemirror"

// --- Markdown-typing input rules --------------------------------------------------------

/** Apply an inline mark when a delimited pattern is typed (e.g. **bold**, *em*, `code`). */
function markInputRule (regexp, markType) {
  return new InputRule(regexp, (state, match, start, end) => {
    const [full, inner] = [match[0], match[1]]
    const tr = state.tr
    if (inner) {
      const textStart = start + full.indexOf(inner)
      const textEnd = textStart + inner.length
      if (textEnd < end) tr.delete(textEnd, end)
      if (textStart > start) tr.delete(start, textStart)
      const to = start + inner.length
      tr.addMark(start, to, markType.create())
      tr.removeStoredMark(markType)
    }
    return tr
  })
}

function buildInputRules () {
  const rules = smartQuotes.concat(ellipsis, emDash)
  const n = schema.nodes, m = schema.marks
  if (n.blockquote) rules.push(wrappingInputRule(/^\s*>\s$/, n.blockquote))
  if (n.ordered_list)
    rules.push(wrappingInputRule(
      /^(\d+)\.\s$/, n.ordered_list,
      match => ({ order: +match[1] }),
      (match, node) => node.childCount + node.attrs.order === +match[1]))
  if (n.bullet_list) rules.push(wrappingInputRule(/^\s*([-+*])\s$/, n.bullet_list))
  if (n.code_block) rules.push(textblockTypeInputRule(/^```$/, n.code_block))
  if (n.heading)
    rules.push(textblockTypeInputRule(
      new RegExp("^(#{1,6})\\s$"), n.heading, match => ({ level: match[1].length })))
  if (m.strong) rules.push(markInputRule(/(?:\*\*|__)([^*_]+)(?:\*\*|__)$/, m.strong))
  if (m.em) rules.push(markInputRule(/(?:^|[^*_])(?:\*|_)([^*_]+)(?:\*|_)$/, m.em))
  if (m.code) rules.push(markInputRule(/`([^`]+)`$/, m.code))
  return inputRules({ rules })
}

// --- Keymap (base + lists + Yjs-aware undo/redo) ----------------------------------------

function buildKeymap () {
  const keys = {}, n = schema.nodes
  keys["Mod-z"] = yUndo
  keys["Mod-y"] = yRedo
  keys["Mod-Shift-z"] = yRedo
  keys["Mod-b"] = toggleMark(schema.marks.strong)
  keys["Mod-i"] = toggleMark(schema.marks.em)
  if (n.list_item) {
    keys["Enter"] = splitListItem(n.list_item)
    keys["Mod-["] = liftListItem(n.list_item)
    keys["Mod-]"] = sinkListItem(n.list_item)
    keys["Tab"] = sinkListItem(n.list_item)
    keys["Shift-Tab"] = liftListItem(n.list_item)
  }
  return keys
}

// --- Plugins ----------------------------------------------------------------------------

function editorPlugins (fragment) {
  return [
    ySyncPlugin(fragment),
    yUndoPlugin(),
    buildInputRules(),
    keymap(buildKeymap()),
    keymap(baseKeymap)
  ]
}

// Plain-text (markdown) paste -> parsed as markdown. HTML paste falls through to
// ProseMirror's normal clipboard handling.
function markdownPasteHandler () {
  return {
    handlePaste (view, event) {
      const cd = event.clipboardData
      if (!cd) return false
      if (cd.getData("text/html")) return false
      const text = cd.getData("text/plain")
      if (!text) return false
      const doc = defaultMarkdownParser.parse(text)
      if (!doc) return false
      const tr = view.state.tr.replaceSelectionWith(doc, false)
      view.dispatch(tr)
      return true
    }
  }
}

// --- Public surface (bound by Editor.fs) ------------------------------------------------

/**
 * Mount a ProseMirror editor onto `host`, bound to the live `fragment`. Returns a handle
 * with `destroy()`. `readOnly` renders another peer's draft without an edit surface.
 */
export function mountEditor (host, fragment, readOnly) {
  const state = EditorState.create({ schema, plugins: editorPlugins(fragment) })
  const view = new EditorView(host, {
    state,
    editable: () => !readOnly,
    ...markdownPasteHandler()
  })
  return { destroy () { view.destroy() } }
}

/** Serialize a Y.XmlFragment's ProseMirror doc to Markdown (the durable body / agent input). */
export function fragmentToMarkdown (fragment) {
  const node = yXmlFragmentToProseMirrorRootNode(fragment, schema)
  return defaultMarkdownSerializer.serialize(node)
}

/** Parse Markdown into an (empty) Y.XmlFragment — used to seed a queue body on send. */
export function markdownIntoFragment (markdown, fragment) {
  const node = defaultMarkdownParser.parse(markdown || "")
  prosemirrorToYXmlFragment(node, fragment)
}

/** Content-copy one fragment's document into another (draft -> queue on send). Shared
 *  types cannot be re-parented, so we copy via the markdown round-trip. */
export function copyFragment (src, dst) {
  markdownIntoFragment(fragmentToMarkdown(src), dst)
}
