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
