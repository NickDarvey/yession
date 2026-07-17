namespace Yession.App

open Fable.Core
open Fable.Core.JsInterop
open Yjs
open Yession.App.ProseMirror

/// The Linear-style rich-text editor: type or paste Markdown, rendered live as formatted
/// rich text. Pure F# over the `ProseMirror` bindings (no authored JS). The document lives
/// in a `Y.XmlFragment` (via `ySyncPlugin`), so edits flow straight into the CRDT and merge.
/// Exposed to the browser host as `mountEditor`; the drain path uses `fragmentToMarkdown`
/// and send uses `copyFragment`.
module Editor =

    // Tiny boundary lambdas for the input-rule attribute/predicate callbacks (JS functions
    // ProseMirror invokes with its match array). Kept minimal — the composition is in F#.
    [<Emit("(m => ({ order: +m[1] }))")>]
    let private orderedListAttrs : obj = jsNative
    [<Emit("((m, node) => node.childCount + node.attrs.order === +m[1])")>]
    let private orderedListJoin : obj = jsNative
    [<Emit("(m => ({ level: m[1].length }))")>]
    let private headingAttrs : obj = jsNative
    /// `event.clipboardData.getData(fmt)` (empty when absent).
    [<Emit("$0.clipboardData ? $0.clipboardData.getData($1) : ''")>]
    let private clipboard (event: obj) (fmt: string) : string = jsNative

    /// Inline mark rule: when `**b**` / `*i*` / `` `c` `` is completed at the cursor, replace
    /// the delimited text with the marked text (deleting the delimiters). Later positions are
    /// deleted first so the earlier offsets stay valid.
    let private markRule (pattern: string) (mark: MarkType) : InputRule =
        let handler =
            System.Func<EditorState, string[], int, int, Transaction>(fun state m start endPos ->
                let full = m.[0]
                let inner = if m.Length > 1 then m.[1] else null
                if isNull (box inner) || inner = "" then null
                else
                    let tr = state.tr
                    let textStart = start + full.IndexOf inner
                    let textEnd = textStart + inner.Length
                    let tr = if textEnd < endPos then tr.delete (textEnd, endPos) else tr
                    let tr = if textStart > start then tr.delete (start, textStart) else tr
                    (tr.addMark(start, start + inner.Length, markCreate mark)).removeStoredMark mark)
        makeInputRule (regex pattern) handler

    /// Markdown-typing input rules (block via prosemirror-inputrules helpers, inline via
    /// `markRule`). Each is guarded on the schema actually having the node/mark.
    let private markdownInputRules () : Plugin =
        let n = nodeType schema
        let m = markType schema
        let rules = ResizeArray<InputRule> ()
        rules.AddRange smartQuotes
        rules.Add ellipsis
        rules.Add emDash
        if present (n "blockquote") then rules.Add (wrappingInputRule (regex "^\\s*>\\s$") (n "blockquote"))
        if present (n "ordered_list") then
            rules.Add (wrappingInputRuleAttrs (regex "^(\\d+)\\.\\s$") (n "ordered_list") orderedListAttrs orderedListJoin)
        if present (n "bullet_list") then rules.Add (wrappingInputRule (regex "^\\s*([-+*])\\s$") (n "bullet_list"))
        if present (n "code_block") then rules.Add (textblockTypeInputRule (regex "^```$") (n "code_block"))
        if present (n "heading") then rules.Add (textblockTypeInputRuleAttrs (regex "^(#{1,6})\\s$") (n "heading") headingAttrs)
        if present (m "strong") then rules.Add (markRule "(?:\\*\\*|__)([^*_]+)(?:\\*\\*|__)$" (m "strong"))
        if present (m "em") then rules.Add (markRule "(?:^|[^*_])(?:\\*|_)([^*_]+)(?:\\*|_)$" (m "em"))
        if present (m "code") then rules.Add (markRule "`([^`]+)`$" (m "code"))
        inputRules (createObj [ "rules" ==> rules.ToArray () ])

    /// Base editing keys + list handling + Yjs-aware undo/redo.
    let private editorKeymap () : obj =
        let keys = createObj []
        keys?("Mod-z") <- yUndo
        keys?("Mod-y") <- yRedo
        keys?("Mod-Shift-z") <- yRedo
        keys?("Mod-b") <- toggleMark (markType schema "strong")
        keys?("Mod-i") <- toggleMark (markType schema "em")
        if present (nodeType schema "list_item") then
            let li = nodeType schema "list_item"
            keys?("Enter") <- splitListItem li
            keys?("Tab") <- sinkListItem li
            keys?("Shift-Tab") <- liftListItem li
            keys?("Mod-[") <- liftListItem li
            keys?("Mod-]") <- sinkListItem li
        keys

    let private plugins (fragment: Y.XmlFragment) : Plugin[] =
        [| ySyncPlugin fragment
           yUndoPlugin ()
           markdownInputRules ()
           keymap (editorKeymap ())
           keymap baseKeymap |]

    /// Plain-text (Markdown) paste -> parsed as Markdown; HTML paste falls through to
    /// ProseMirror's normal clipboard handling.
    let private handlePaste =
        System.Func<EditorView, obj, bool>(fun view event ->
            if clipboard event "text/html" <> "" then false
            else
                let text = clipboard event "text/plain"
                if text = "" then false
                else
                    let doc = mdParser.parse text
                    if isNull (box doc) then false
                    else
                        view.dispatch ((view.state.tr).replaceSelectionWith (doc, false))
                        true)

    /// Mount a ProseMirror editor onto `host`, bound to the live `fragment`. Returns a
    /// dispose thunk. `readOnly` renders another peer's draft without an edit surface.
    let mountEditor (host: obj) (fragment: Y.XmlFragment) (readOnly: bool) : (unit -> unit) =
        let state = createState (createObj [ "schema" ==> schema; "plugins" ==> plugins fragment ])
        let view =
            createView host (createObj [
                "state" ==> state
                "editable" ==> (System.Func<bool>(fun () -> not readOnly))
                "handlePaste" ==> handlePaste ])
        fun () -> view.destroy ()

    /// Serialize a fragment's ProseMirror doc to Markdown (the durable body / agent input).
    let fragmentToMarkdown (fragment: Y.XmlFragment) : string =
        mdSerializer.serialize (fragmentToRootNode fragment schema)

    /// Parse Markdown into an (empty) fragment — seeds a queue body on send.
    let markdownIntoFragment (markdown: string) (fragment: Y.XmlFragment) : unit =
        rootNodeToFragment (mdParser.parse (if isNull (box markdown) then "" else markdown)) fragment

    /// Content-copy one fragment's document into another (draft -> queue on send). Shared
    /// types cannot be re-parented, so copy via the Markdown round-trip.
    let copyFragment (src: Y.XmlFragment) (dst: Y.XmlFragment) : unit =
        markdownIntoFragment (fragmentToMarkdown src) dst
