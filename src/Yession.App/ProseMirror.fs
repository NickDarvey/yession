namespace Yession.App

open Fable.Core
open Fable.Core.JsInterop
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
        abstract removeStoredMark : obj -> Transaction
        abstract replaceSelectionWith : Node * bool -> Transaction

    type [<AllowNullLiteral>] EditorState =
        abstract tr : Transaction

    type [<AllowNullLiteral>] EditorView =
        abstract state : EditorState
        abstract dispatch : Transaction -> unit
        abstract destroy : unit -> unit

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
    /// A fresh JS RegExp from a pattern string (input-rule triggers).
    [<Emit("new RegExp($0)")>]
    let regex (pattern: string) : obj = jsNative

    // --- Structural read of a parsed document (read-only markdown rendering) ----------------
    // A `Node` parsed by `mdParser` is pure data — no DOM — so this walk runs identically on
    // Node (SSR) and in the browser. Only the members the timeline renderer reads are typed.

    /// The node's type name (`paragraph`, `heading`, `bullet_list`, `text`, …).
    [<Emit("$0.type.name")>]
    let nodeTypeName (node: Node) : string = jsNative
    [<Emit("$0.childCount")>]
    let nodeChildCount (node: Node) : int = jsNative
    [<Emit("$0.child($1)")>]
    let nodeChild (node: Node) (index: int) : Node = jsNative
    [<Emit("$0.isText")>]
    let nodeIsText (node: Node) : bool = jsNative
    /// A text node's literal text (empty for non-text nodes).
    [<Emit("$0.text || ''")>]
    let nodeText (node: Node) : string = jsNative
    /// The concatenated text of a node's descendants (code blocks, fallbacks).
    [<Emit("$0.textContent")>]
    let nodeTextContent (node: Node) : string = jsNative
    /// A text node's marks (`em`/`strong`/`code`/`link`), innermost-last.
    [<Emit("$0.marks")>]
    let nodeMarks (node: Node) : obj[] = jsNative
    /// A heading's level (1–6), defaulting to 1.
    [<Emit("($0.attrs && $0.attrs.level) || 1")>]
    let headingLevel (node: Node) : int = jsNative
    /// An ordered list's first number, defaulting to 1.
    [<Emit("($0.attrs && $0.attrs.order) || 1")>]
    let listStart (node: Node) : int = jsNative
    [<Emit("$0.type.name")>]
    let markTypeName (mark: obj) : string = jsNative
    /// A link mark's target (empty when absent).
    [<Emit("($0.attrs && $0.attrs.href) || ''")>]
    let markHref (mark: obj) : string = jsNative

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

    /// What plain Enter does in a bare ProseMirror — split the block, make a paragraph,
    /// lift an empty one, break a line inside code. Read off `baseKeymap` rather than
    /// reassembled from its four parts, so rebinding Enter can hand the ORIGINAL behaviour
    /// to another key without a second definition of it drifting from ProseMirror's.
    [<Emit("$0.Enter")>]
    let baseEnter (bindings: obj) : Command = jsNative

    /// `chainCommands(a, b)`: try `a`, fall through to `b` when it declines. Variadic in JS,
    /// so it is called explicitly rather than imported as a curried F# function.
    [<Import("chainCommands", "prosemirror-commands")>]
    let private chainCommandsFn : obj = jsNative
    [<Emit("$0($1, $2)")>]
    let private callChain (fn: obj) (a: Command) (b: Command) : Command = jsNative
    let chain (a: Command) (b: Command) : Command = callChain chainCommandsFn a b

    /// A command that always handles the key by running an effect — how a keystroke reaches
    /// the app (Enter sends). Returning `true` is what stops ProseMirror inserting anything.
    /// A `System.Func` because ProseMirror calls it with three arguments, not curried.
    let effectCommand (run: unit -> unit) : Command =
        box (System.Func<EditorState, obj, obj, bool>(fun _ _ _ -> run (); true))

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

    // --- Presence: selection, plugin-with-state, decorations, relative positions ------------
    // Everything below powers the collaborative cursor overlays. Positions travel the wire as
    // Yjs RELATIVE positions (base64), which survive concurrent edits; a body's positions ride
    // the y-prosemirror ySync mapping, the title's ride the raw `Y.Text`.

    // State/transaction/view members not on the minimal interfaces above.
    [<Emit("$0.selection")>]
    let selection (state: EditorState) : obj = jsNative
    [<Emit("$0.doc")>]
    let stateDoc (state: EditorState) : obj = jsNative
    [<Emit("$0.hasFocus()")>]
    let viewHasFocus (view: EditorView) : bool = jsNative
    [<Emit("$0.anchor")>]
    let selAnchor (sel: obj) : int = jsNative
    [<Emit("$0.head")>]
    let selHead (sel: obj) : int = jsNative
    [<Emit("$0.setMeta($1, $2)")>]
    let trSetMeta (tr: Transaction) (key: obj) (value: obj) : Transaction = jsNative
    [<Emit("$0.getMeta($1)")>]
    let trGetMeta (tr: Transaction) (key: obj) : obj = jsNative
    [<Emit("$0.docChanged")>]
    let trDocChanged (tr: Transaction) : bool = jsNative
    [<Emit("$0.selectionSet")>]
    let trSelectionSet (tr: Transaction) : bool = jsNative
    [<Emit("$0.mapping")>]
    let trMapping (tr: Transaction) : obj = jsNative
    [<Emit("$0.doc")>]
    let trDoc (tr: Transaction) : obj = jsNative
    [<Emit("$0")>]
    let asTransaction (tr: obj) : Transaction = jsNative

    // prosemirror-state: PluginKey + a Plugin carrying state + props.
    [<Import("PluginKey", "prosemirror-state")>]
    let private pluginKeyClass : obj = jsNative
    [<Emit("new $0($1)")>]
    let private pluginKeyNew (cls: obj) (name: string) : obj = jsNative
    let pluginKey (name: string) : obj = pluginKeyNew pluginKeyClass name
    [<Emit("$0.getState($1)")>]
    let pluginKeyGetState (key: obj) (state: EditorState) : obj = jsNative
    [<Import("Plugin", "prosemirror-state")>]
    let private pluginClass : obj = jsNative
    [<Emit("new $0($1)")>]
    let private pluginNew (cls: obj) (spec: obj) : Plugin = jsNative
    let makePlugin (spec: obj) : Plugin = pluginNew pluginClass spec

    // prosemirror-view: Decoration widgets/inlines + a DecorationSet.
    [<Import("Decoration", "prosemirror-view")>]
    let private decorationClass : obj = jsNative
    [<Emit("$0.widget($1, $2, $3)")>]
    let private decorationWidget (cls: obj) (pos: int) (dom: obj) (spec: obj) : obj = jsNative
    let decoWidget (pos: int) (dom: obj) (spec: obj) : obj = decorationWidget decorationClass pos dom spec
    [<Emit("$0.inline($1, $2, $3)")>]
    let private decorationInline (cls: obj) (from: int) (to': int) (attrs: obj) : obj = jsNative
    let decoInline (from: int) (to': int) (attrs: obj) : obj = decorationInline decorationClass from to' attrs
    [<Import("DecorationSet", "prosemirror-view")>]
    let private decorationSetClass : obj = jsNative
    [<Emit("$0.create($1, $2)")>]
    let private decorationSetCreate (cls: obj) (doc: obj) (decos: obj[]) : obj = jsNative
    let decoSetCreate (doc: obj) (decos: obj[]) : obj = decorationSetCreate decorationSetClass doc decos
    [<Emit("$0.empty")>]
    let private decorationSetEmpty (cls: obj) : obj = jsNative
    let decoSetEmpty : obj = decorationSetEmpty decorationSetClass
    [<Emit("$0.map($1, $2)")>]
    let decoSetMap (set: obj) (mapping: obj) (doc: obj) : obj = jsNative

    // The DOM for one caret + name label (a widget decoration). Authored via `[<Emit>]` interop,
    // never a `.js` file — the repo's no-authored-JS invariant is about source files, not interop.
    [<Emit("""(function(color, name){
      var c = document.createElement('span'); c.className = 'pm-caret'; c.style.borderColor = color;
      var l = document.createElement('span'); l.className = 'pm-caret-label'; l.textContent = name; l.style.background = color;
      c.appendChild(l); return c;
    })($0, $1)""")>]
    let caretDom (color: string) (name: string) : obj = jsNative

    // --- Yjs relative positions (survive concurrent edits) + lib0 base64 for the wire --------

    [<Import("encodeRelativePosition", "yjs")>]
    let private encodeRelPos (rp: obj) : JS.Uint8Array = jsNative
    [<Import("decodeRelativePosition", "yjs")>]
    let private decodeRelPos (bytes: JS.Uint8Array) : obj = jsNative
    [<Import("createRelativePositionFromTypeIndex", "yjs")>]
    let relPosFromTypeIndex (typ: obj) (index: int) : obj = jsNative
    [<Import("createAbsolutePositionFromRelativePosition", "yjs")>]
    let private createAbsPos (rp: obj) (doc: Y.Doc) : obj = jsNative
    [<Import("toBase64", "lib0/buffer")>]
    let private toBase64 (b: JS.Uint8Array) : string = jsNative
    [<Import("fromBase64", "lib0/buffer")>]
    let private fromBase64 (s: string) : JS.Uint8Array = jsNative

    /// A relative position -> its base64 wire form.
    let encodeRel (relPos: obj) : string = toBase64 (encodeRelPos relPos)
    /// Base64 wire form -> a relative position.
    let decodeRel (encoded: string) : obj = decodeRelPos (fromBase64 encoded)
    /// The absolute index of a base64 relative position in a `Y.Text`/`Y.XmlFragment` on `doc`,
    /// or `None` if it no longer resolves (its anchor content was deleted).
    let absIndexInDoc (doc: Y.Doc) (encoded: string) : int option =
        match createAbsPos (decodeRel encoded) doc with
        | null -> None
        | abs -> Some (abs?index |> unbox<int>)

    // --- y-prosemirror position bridging (ProseMirror positions <-> Yjs relative positions) --

    [<Import("ySyncPluginKey", "y-prosemirror")>]
    let ySyncPluginKey : obj = jsNative
    [<Import("getRelativeSelection", "y-prosemirror")>]
    let private getRelativeSelection (binding: obj) (state: EditorState) : obj = jsNative
    [<Import("relativePositionToAbsolutePosition", "y-prosemirror")>]
    let private relToAbs (doc: Y.Doc) (typ: obj) (relPos: obj) (mapping: obj) : obj = jsNative

    /// The ySync binding for a state (holds the ProseMirror<->Yjs `mapping`, the `type`, `doc`).
    let syncBinding (state: EditorState) : obj = (pluginKeyGetState ySyncPluginKey state)?binding

    /// The editor's current selection as base64 relative anchor/head over its body fragment.
    let relSelectionOf (state: EditorState) : (string * string) option =
        match syncBinding state with
        | null -> None
        | binding ->
            let rs = getRelativeSelection binding state
            Some (encodeRel rs?anchor, encodeRel rs?head)

    /// Map a base64 relative position back to an absolute ProseMirror position in this editor,
    /// or `None` if it no longer resolves.
    let absPosInBody (state: EditorState) (encoded: string) : int option =
        match syncBinding state with
        | null -> None
        | binding ->
            match relToAbs binding?doc binding?``type`` (decodeRel encoded) binding?mapping with
            | null -> None
            | pos -> Some (unbox<int> pos)
