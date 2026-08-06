namespace Yession.App

open Fable.Core
open Fable.Core.JsInterop
open Yjs
open Yession.App.ProseMirror

/// The Linear-style rich-text editor: type or paste Markdown, rendered live as formatted
/// rich text. Pure F# over the `ProseMirror` bindings (no authored JS). The document lives
/// in a `Y.XmlFragment` (via `ySyncPlugin`), so edits flow straight into the CRDT and merge.
/// Exposed to the browser host as `mountEditor`; fragment↔markdown lives in `Domain.Markdown`.
module Editor =

    /// One remote peer's caret+selection to overlay on a body editor. Positions are base64 Yjs
    /// relative positions over this body's fragment; colours are precomputed (`PeerColour`).
    type RemoteBodyCursor =
        { Colour : string      // solid — the caret bar and name label
          Selection : string   // translucent — the selection highlight
          Name : string
          Anchor : string      // base64 relative position
          Head : string }      // base64 relative position

    /// A mounted editor: dispose it, and push the current set of remote cursors into it (which
    /// re-derives the on-screen decorations from their relative positions).
    type EditorHandle =
        { Dispose : unit -> unit
          PushPresences : RemoteBodyCursor list -> unit }

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

    /// Base editing keys + list handling + Yjs-aware undo/redo, and Enter.
    ///
    /// `onSubmit` is what Enter does in a COMPOSER: send. That is what Enter does in every
    /// chat surface, and the new line it displaces moves to `Alt-Enter` — which is bound to
    /// exactly the behaviour Enter used to have (split the list item, else split the block),
    /// read off `baseKeymap` rather than rebuilt, so the two can never drift.
    ///
    /// A body with nothing to send (a queued message being edited in place) passes `None`
    /// and keeps plain Enter: binding a send there would fire an action that does not exist.
    let private editorKeymap (onSubmit: (unit -> unit) option) : obj =
        let keys = createObj []
        keys?("Mod-z") <- yUndo
        keys?("Mod-y") <- yRedo
        keys?("Mod-Shift-z") <- yRedo
        keys?("Mod-b") <- toggleMark (markType schema "strong")
        keys?("Mod-i") <- toggleMark (markType schema "em")
        // What a plain Enter means in prose: split the list item when in one, else whatever
        // ProseMirror's own Enter does.
        let newLine =
            if present (nodeType schema "list_item") then
                chain (splitListItem (nodeType schema "list_item")) (baseEnter baseKeymap)
            else baseEnter baseKeymap
        if present (nodeType schema "list_item") then
            let li = nodeType schema "list_item"
            keys?("Tab") <- sinkListItem li
            keys?("Shift-Tab") <- liftListItem li
            keys?("Mod-[") <- liftListItem li
            keys?("Mod-]") <- sinkListItem li
        match onSubmit with
        | Some submit ->
            keys?("Enter") <- effectCommand submit
            keys?("Alt-Enter") <- newLine
        | None -> keys?("Enter") <- newLine
        keys

    // --- Presence: report the local selection, overlay remote ones -------------------------

    /// Reports the local caret+selection (as base64 relative anchor/head) whenever it moves
    /// while focused, on focus, and clears (`None`) on blur/destroy. Added only to editable
    /// editors. The Browser tags the report with this body's field and rAF-throttles it.
    let private presenceReportPlugin (report: (string * string) option -> unit) : Plugin =
        makePlugin (createObj [
            "props" ==> createObj [
                "handleDOMEvents" ==> createObj [
                    "focus" ==> System.Func<EditorView, obj, bool>(fun v _ -> report (relSelectionOf v.state); false)
                    "blur" ==> System.Func<EditorView, obj, bool>(fun _ _ -> report None; false) ] ]
            "view" ==> System.Func<EditorView, obj>(fun _ ->
                let mutable last : (string * string) option = None
                createObj [
                    "update" ==> System.Func<EditorView, EditorState, unit>(fun v _prev ->
                        if viewHasFocus v then
                            let cur = relSelectionOf v.state
                            if cur <> last then
                                last <- cur
                                report cur)
                    "destroy" ==> System.Func<unit, unit>(fun () -> report None) ]) ])

    /// The decoration plugin's key — private so remote cursors can only be pushed through
    /// `EditorHandle.PushPresences`, never by reaching into plugin state.
    let private presenceDecoKey = pluginKey "yession-presence-cursors"

    /// Build the `DecorationSet` for a set of remote cursors: a translucent selection span
    /// `min..max` (when non-empty) and a caret widget + name label at `head`, per peer. A
    /// position that no longer resolves (its content was deleted) is skipped.
    let private buildBodyDecorations (state: EditorState) (remotes: RemoteBodyCursor[]) : obj =
        let decos = ResizeArray<obj> ()
        for r in remotes do
            match absPosInBody state r.Anchor, absPosInBody state r.Head with
            | Some a, Some h ->
                let lo, hi = (min a h), (max a h)
                if lo <> hi then
                    decos.Add (decoInline lo hi (createObj [ "style" ==> sprintf "background-color:%s" r.Selection ]))
                decos.Add (decoWidget h (caretDom r.Colour r.Name) (createObj [ "side" ==> 10 ]))
            | _ -> ()
        decoSetCreate (stateDoc state) (decos.ToArray ())

    /// Holds a `DecorationSet` in plugin state: a `setMeta` push rebuilds it from the remote
    /// cursors; any other transaction remaps the existing set through the doc change.
    let private presenceDecorationsPlugin () : Plugin =
        makePlugin (createObj [
            "key" ==> presenceDecoKey
            "state" ==> createObj [
                "init" ==> System.Func<obj, EditorState, obj>(fun _ _ -> decoSetEmpty)
                "apply" ==> System.Func<Transaction, obj, EditorState, EditorState, obj>(fun tr old _oldS newS ->
                    match trGetMeta tr presenceDecoKey with
                    | null -> if trDocChanged tr then decoSetMap old (trMapping tr) (trDoc tr) else old
                    | remotes -> buildBodyDecorations newS (unbox<RemoteBodyCursor[]> remotes)) ]
            "props" ==> createObj [
                "decorations" ==> System.Func<EditorState, obj>(fun s -> pluginKeyGetState presenceDecoKey s) ] ])

    let private plugins
        (fragment: Y.XmlFragment)
        (report: ((string * string) option -> unit) option)
        (onSubmit: (unit -> unit) option)
        : Plugin[] =
        let ps =
            ResizeArray<Plugin> [
                ySyncPlugin fragment
                yUndoPlugin ()
                markdownInputRules ()
                keymap (editorKeymap onSubmit)
                keymap baseKeymap
                presenceDecorationsPlugin () ]
        report |> Option.iter (fun r -> ps.Add (presenceReportPlugin r))
        ps.ToArray ()

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

    /// Mount a ProseMirror editor onto `host`, bound to the live `fragment`. The fragment is
    /// the synced, doc-backed body, so edits flow straight into the CRDT and to peers through
    /// the doc — no change callback. `readOnly` renders another peer's content without an edit
    /// surface (and without reporting presence, and with no Enter binding: there is nothing to
    /// send from a body you cannot type in). `reportFocus` receives the local selection as
    /// base64 relative anchor/head (or `None`); `onSubmit` is what Enter does, when this body
    /// has something to send. The returned handle pushes remote cursors in. Serialization for
    /// the drain lives in `Domain.Markdown`.
    let mountEditor
        (host: obj)
        (fragment: Y.XmlFragment)
        (readOnly: bool)
        (reportFocus: (string * string) option -> unit)
        (onSubmit: (unit -> unit) option)
        : EditorHandle =
        let report = if readOnly then None else Some reportFocus
        let submit = if readOnly then None else onSubmit
        let state = createState (createObj [ "schema" ==> schema; "plugins" ==> plugins fragment report submit ])
        let view =
            createView host (createObj [
                "state" ==> state
                "editable" ==> (System.Func<bool>(fun () -> not readOnly))
                "handlePaste" ==> handlePaste ])
        { Dispose = fun () -> view.destroy ()
          PushPresences =
            fun remotes ->
                view.dispatch (trSetMeta view.state.tr presenceDecoKey (box (List.toArray remotes))) }
