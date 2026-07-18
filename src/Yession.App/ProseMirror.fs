namespace Yession.App

open Fable.Core
open Yjs

/// Fable bindings for the focused ProseMirror + y-prosemirror surface the rich-text editor
/// uses. Pure `[<Import>]`/`[<Emit>]` interop — the platform boundary, not authored JS (repo
/// invariant, master #7). Hand-written for the small used surface rather than full ts2fable
/// generation (the `Fable.Yjs` precedent, scaled down). Opaque PM values we only pass around
/// are `obj`; only the members actually called are typed.
module ProseMirror =

    type Node = obj
    type Plugin = obj
    type Schema = obj
    type NodeType = obj
    type MarkType = obj
    type Command = obj
    type InputRule = obj

    /// A ProseMirror transaction. Members are chainable (each returns the mutated `this`).
    type [<AllowNullLiteral>] Transaction =
        abstract delete : int * int -> Transaction
        abstract addMark : int * int * obj -> Transaction
        abstract removeMark : int * int * obj -> Transaction
        abstract removeStoredMark : obj -> Transaction
        abstract insertText : string * int * int -> Transaction
        abstract replaceSelectionWith : Node * bool -> Transaction

    /// The current selection. `from`/`to` are document positions; `empty` is the collapsed
    /// caret case (a link then targets the surrounding link mark, not a range).
    type [<AllowNullLiteral>] Selection =
        abstract from : int
        abstract ``to`` : int
        abstract empty : bool

    type [<AllowNullLiteral>] EditorState =
        abstract tr : Transaction
        abstract selection : Selection

    type [<AllowNullLiteral>] EditorView =
        abstract state : EditorState
        abstract dispatch : Transaction -> unit
        abstract destroy : unit -> unit
        /// `false` for the read-only peer-draft mirrors — the link editor stays inert there.
        abstract editable : bool
        /// Return focus to the editable surface after the link popover closes.
        abstract focus : unit -> unit
        /// Viewport coordinates (`{ left, top, right, bottom }`) of a document position —
        /// where the link popover anchors.
        abstract coordsAtPos : int -> obj

    // --- prosemirror-markdown: the schema + parser/serializer (markdown round-trip) --------

    type [<AllowNullLiteral>] MarkdownParser =
        abstract parse : string -> Node

    [<Import("schema", "prosemirror-markdown")>]
    let schema : Schema = jsNative
    [<Import("defaultMarkdownParser", "prosemirror-markdown")>]
    let mdParser : MarkdownParser = jsNative

    /// `schema.nodes[name]` / `schema.marks[name]`, and a JS truthiness test for "present".
    [<Emit("$0.nodes[$1]")>]
    let nodeType (s: Schema) (name: string) : NodeType = jsNative
    [<Emit("$0.marks[$1]")>]
    let markType (s: Schema) (name: string) : MarkType = jsNative
    [<Emit("!!$0")>]
    let present (x: obj) : bool = jsNative
    [<Emit("$0.create()")>]
    let markCreate (m: MarkType) : obj = jsNative
    /// A mark carrying attributes — `link.create({ href })`.
    [<Emit("$0.create($1)")>]
    let markCreateAttrs (m: MarkType) (attrs: obj) : obj = jsNative
    /// A fresh JS RegExp from a pattern string (input-rule triggers).
    [<Emit("new RegExp($0)")>]
    let regex (pattern: string) : obj = jsNative

    /// A single bare URL (no surrounding whitespace) — the Slack paste trigger: a URL dropped
    /// over a selection links the selection rather than replacing it.
    [<Emit("/^(https?:\\/\\/|mailto:)\\S+$/.test(($0 || '').trim())")>]
    let isBareUrl (s: string) : bool = jsNative

    /// The link mark touching the current selection, as `{ from; to; href }`, or `null`.
    /// A collapsed caret expands to the whole surrounding link run (so Mod-K edits the link
    /// the cursor sits in); a range returns its own bounds and the href at its start (empty
    /// when the range carries no link — the create case). The mark-run walk is the standard
    /// ProseMirror idiom (`ResolvedPos` index arithmetic), kept in one emit.
    type [<AllowNullLiteral>] LinkRange =
        abstract from : int
        abstract ``to`` : int
        abstract href : string
    [<Emit("""(function (state, markType) {
      const sel = state.selection
      if (sel.empty) {
        const $pos = sel.$from
        const parent = $pos.parent
        const at = $pos.parentOffset
        // A caret carries a mark via marks() only mid-run; at a run edge (link is
        // inclusive:false) it does not. So scan the parent's inline children for the linked
        // run the caret sits in or against, then expand over contiguous same-link children.
        let mark = null, startIndex = -1, offset = 0
        for (let i = 0; i < parent.childCount; i++) {
          const child = parent.child(i)
          const m = markType.isInSet(child.marks)
          if (m && at >= offset && at <= offset + child.nodeSize) { mark = m; startIndex = i; break }
          offset += child.nodeSize
        }
        if (!mark) return null
        let endIndex = startIndex + 1
        while (startIndex > 0 && mark.isInSet(parent.child(startIndex - 1).marks)) startIndex--
        while (endIndex < parent.childCount && mark.isInSet(parent.child(endIndex).marks)) endIndex++
        let from = $pos.start(), to = from
        for (let i = 0; i < endIndex; i++) {
          const size = parent.child(i).nodeSize
          if (i < startIndex) from += size
          to += size
        }
        return { from: from, to: to, href: (mark.attrs && mark.attrs.href) || '' }
      }
      const mark = markType.isInSet(state.doc.resolve(sel.from).marks())
      return { from: sel.from, to: sel.to, href: (mark && mark.attrs && mark.attrs.href) || '' }
    })($0, $1)""")>]
    let linkRangeAt (state: EditorState) (markType: MarkType) : LinkRange = jsNative

    // --- prosemirror-state / -view ---------------------------------------------------------

    [<Import("EditorState", "prosemirror-state")>]
    let private editorStateClass : obj = jsNative
    [<Emit("$0.create($1)")>]
    let private stateCreate (cls: obj) (config: obj) : EditorState = jsNative
    let createState (config: obj) : EditorState = stateCreate editorStateClass config

    [<Import("EditorView", "prosemirror-view")>]
    let private editorViewClass : obj = jsNative
    [<Emit("new $0($1, $2)")>]
    let private viewNew (cls: obj) (host: obj) (props: obj) : EditorView = jsNative
    let createView (host: obj) (props: obj) : EditorView = viewNew editorViewClass host props

    // --- prosemirror-keymap / -commands ----------------------------------------------------

    [<Import("keymap", "prosemirror-keymap")>]
    let keymap (bindings: obj) : Plugin = jsNative
    [<Import("baseKeymap", "prosemirror-commands")>]
    let baseKeymap : obj = jsNative
    [<Import("toggleMark", "prosemirror-commands")>]
    let toggleMark (mark: MarkType) : Command = jsNative

    // --- prosemirror-inputrules ------------------------------------------------------------

    [<Import("inputRules", "prosemirror-inputrules")>]
    let inputRules (config: obj) : Plugin = jsNative
    [<Import("wrappingInputRule", "prosemirror-inputrules")>]
    let wrappingInputRule (regexp: obj) (nodeType: NodeType) : InputRule = jsNative
    [<Import("wrappingInputRule", "prosemirror-inputrules")>]
    let wrappingInputRuleAttrs (regexp: obj) (nodeType: NodeType) (getAttrs: obj) (joinPredicate: obj) : InputRule = jsNative
    [<Import("textblockTypeInputRule", "prosemirror-inputrules")>]
    let textblockTypeInputRule (regexp: obj) (nodeType: NodeType) : InputRule = jsNative
    [<Import("textblockTypeInputRule", "prosemirror-inputrules")>]
    let textblockTypeInputRuleAttrs (regexp: obj) (nodeType: NodeType) (getAttrs: obj) : InputRule = jsNative
    [<Import("smartQuotes", "prosemirror-inputrules")>]
    let smartQuotes : InputRule[] = jsNative
    [<Import("emDash", "prosemirror-inputrules")>]
    let emDash : InputRule = jsNative
    [<Import("ellipsis", "prosemirror-inputrules")>]
    let ellipsis : InputRule = jsNative
    [<Import("InputRule", "prosemirror-inputrules")>]
    let private inputRuleClass : obj = jsNative
    /// `new InputRule(regexp, handler)` — the handler is a JS multi-arg callback, so it is a
    /// `System.Func` (Fable emits a native n-ary function, never a curried F# closure).
    [<Emit("new $0($1, $2)")>]
    let private inputRuleNew (cls: obj) (regexp: obj) (handler: System.Func<EditorState, string[], int, int, Transaction>) : InputRule = jsNative
    let makeInputRule (regexp: obj) (handler: System.Func<EditorState, string[], int, int, Transaction>) : InputRule =
        inputRuleNew inputRuleClass regexp handler

    // --- prosemirror-schema-list -----------------------------------------------------------

    [<Import("splitListItem", "prosemirror-schema-list")>]
    let splitListItem (itemType: NodeType) : Command = jsNative
    [<Import("liftListItem", "prosemirror-schema-list")>]
    let liftListItem (itemType: NodeType) : Command = jsNative
    [<Import("sinkListItem", "prosemirror-schema-list")>]
    let sinkListItem (itemType: NodeType) : Command = jsNative

    // --- y-prosemirror ---------------------------------------------------------------------

    [<Import("ySyncPlugin", "y-prosemirror")>]
    let ySyncPlugin (fragment: Y.XmlFragment) : Plugin = jsNative
    [<Import("yUndoPlugin", "y-prosemirror")>]
    let yUndoPlugin () : Plugin = jsNative
    [<Import("undo", "y-prosemirror")>]
    let yUndo : Command = jsNative
    [<Import("redo", "y-prosemirror")>]
    let yRedo : Command = jsNative
