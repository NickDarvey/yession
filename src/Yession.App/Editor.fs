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

    // --- Link editing (Slack paste + Linear-style Mod-K popover) ---------------------------

    /// The `link` mark from the shared markdown schema (may be absent in a stripped schema).
    let private linkType () : MarkType = markType schema "link"

    /// A single floating link editor at a time (a second Mod-K, or a click elsewhere, closes
    /// the first). Idempotent: the popover's own dispose thunk guards re-entry.
    let mutable private closePopover : unit -> unit = ignore

    /// The Linear-style floating link popover: a bare input anchored under the selection with
    /// Add/Update and (when editing) Remove. Enter applies, Escape or an outside click cancels.
    /// Built imperatively in F# (the no-authored-JS invariant — `[<Emit>]` is the boundary, not
    /// a `.js` file); styled inline against the Metro/Zune palette so it needs no stylesheet.
    /// Returns a dispose thunk. The `onApply`/`onRemove`/`onCancel` callbacks are F# closures.
    [<Emit("""(function (left, top, href, isEdit, onApply, onRemove, onCancel) {
      const wrap = document.createElement('div')
      wrap.setAttribute('data-link-editor', '')
      wrap.style.cssText = 'position:fixed;z-index:60;display:flex;align-items:center;gap:6px;'
        + 'background:#0a0a0a;border:1px solid #1f1f1f;padding:6px;'
        + 'box-shadow:0 8px 24px rgba(0,0,0,0.6);font-family:inherit;'
      wrap.style.left = Math.round(left) + 'px'
      wrap.style.top = Math.round(top + 6) + 'px'
      const input = document.createElement('input')
      input.type = 'text'
      input.value = href || ''
      input.placeholder = 'Paste or type a link'
      input.setAttribute('data-link-input', '')
      input.style.cssText = 'background:transparent;border:0;outline:none;color:#fff;'
        + "font-family:inherit;font-size:13px;width:240px;padding:2px 4px;"
      const apply = document.createElement('button')
      apply.type = 'button'
      apply.textContent = isEdit ? 'Update' : 'Add'
      apply.setAttribute('data-link-apply', '')
      apply.style.cssText = 'cursor:pointer;background:transparent;border:1px solid #1ba1e2;'
        + 'color:#1ba1e2;font-family:inherit;font-size:11px;text-transform:uppercase;'
        + 'letter-spacing:0.14em;padding:4px 10px;'
      wrap.appendChild(input)
      wrap.appendChild(apply)
      let removeBtn = null
      if (isEdit) {
        removeBtn = document.createElement('button')
        removeBtn.type = 'button'
        removeBtn.textContent = 'Remove'
        removeBtn.setAttribute('data-link-remove', '')
        removeBtn.style.cssText = 'cursor:pointer;background:transparent;border:1px solid #2e2e2e;'
          + 'color:#b4b4b4;font-family:inherit;font-size:11px;text-transform:uppercase;'
          + 'letter-spacing:0.14em;padding:4px 10px;'
        wrap.appendChild(removeBtn)
      }
      let done = false
      const cleanup = () => {
        if (done) return
        done = true
        document.removeEventListener('mousedown', onDoc, true)
        wrap.remove()
      }
      const onDoc = (e) => { if (!wrap.contains(e.target)) { cleanup(); onCancel() } }
      const submit = () => { const v = input.value.trim(); cleanup(); if (v) onApply(v); else onCancel() }
      apply.addEventListener('click', submit)
      if (removeBtn) removeBtn.addEventListener('click', () => { cleanup(); onRemove() })
      input.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') { e.preventDefault(); submit() }
        else if (e.key === 'Escape') { e.preventDefault(); cleanup(); onCancel() }
      })
      document.addEventListener('mousedown', onDoc, true)
      document.body.appendChild(wrap)
      const r = wrap.getBoundingClientRect()
      if (r.right > window.innerWidth - 8) wrap.style.left = Math.max(8, window.innerWidth - r.width - 8) + 'px'
      input.focus()
      input.select()
      return cleanup
    })($0, $1, $2, $3, $4, $5, $6)""")>]
    let private showLinkPopover
        (left: float) (top: float) (href: string) (isEdit: bool)
        (onApply: System.Action<string>) (onRemove: System.Action) (onCancel: System.Action)
        : (unit -> unit) = jsNative

    /// Apply `href` as a link over `[fromPos, toPos)`: a range keeps its text and (re)links it
    /// (`addMark` replaces any existing link, since a mark excludes its own type); a collapsed
    /// caret inserts the URL as its own linked text (the no-selection insert case).
    let private applyLink (view: EditorView) (fromPos: int) (toPos: int) (href: string) : unit =
        let lm = linkType ()
        let mark = markCreateAttrs lm (createObj [ "href" ==> href ])
        let tr =
            if fromPos = toPos then
                (view.state.tr.insertText (href, fromPos, toPos)).addMark (fromPos, fromPos + href.Length, mark)
            else
                view.state.tr.addMark (fromPos, toPos, mark)
        view.dispatch tr
        view.focus ()

    /// Open the link editor for the current selection: edit the surrounding link, (re)link the
    /// selected text, or — with no selection and no link — create one from the typed URL. Inert
    /// on read-only mirrors and when the schema has no link mark. Returns `true` (Mod-K handled).
    let private openLinkEditor (view: EditorView) : bool =
        if not view.editable || not (present (linkType ())) then false
        else
            closePopover ()
            let sel = view.state.selection
            let range = linkRangeAt view.state (linkType ())
            let fromPos, toPos, initialHref =
                if isNull (box range) then sel.from, sel.``to``, ""
                else range.from, range.``to``, range.href
            let isEdit = initialHref <> ""
            let coords = view.coordsAtPos fromPos
            let left : float = coords?left
            let top : float = coords?bottom
            let onApply = System.Action<string>(fun href -> applyLink view fromPos toPos href)
            let onRemove =
                System.Action(fun () ->
                    if fromPos <> toPos then view.dispatch (view.state.tr.removeMark (fromPos, toPos, linkType ()))
                    view.focus ())
            let onCancel = System.Action(fun () -> view.focus ())
            closePopover <- showLinkPopover left top initialHref isEdit onApply onRemove onCancel
            true

    /// Mod-K keymap command. ProseMirror calls it `(state, dispatch, view)`; the popover needs
    /// the live view (coordinates, focus), so the command is view-driven, not dispatch-driven.
    let private linkKeyCommand =
        System.Func<EditorState, obj, EditorView, bool>(fun _state _dispatch view -> openLinkEditor view)

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
        if present (linkType ()) then keys?("Mod-k") <- linkKeyCommand
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

    /// Clipboard paste. Slack-style link first: a bare URL pasted over a selection links the
    /// selected text (kept as the label); with no selection it inserts the URL as its own linked
    /// text. Otherwise plain-text (Markdown) paste is parsed as Markdown, and HTML paste falls
    /// through to ProseMirror's normal clipboard handling.
    let private handlePaste =
        System.Func<EditorView, obj, bool>(fun view event ->
            let text = clipboard event "text/plain"
            let sel = view.state.selection
            if isBareUrl text && present (linkType ()) then
                applyLink view sel.from sel.``to`` (text.Trim ())
                true
            elif clipboard event "text/html" <> "" then false
            elif text = "" then false
            else
                let doc = mdParser.parse text
                if isNull (box doc) then false
                else
                    view.dispatch ((view.state.tr).replaceSelectionWith (doc, false))
                    true)

    /// Mount a ProseMirror editor onto `host`, bound to the live `fragment`. The fragment is
    /// the synced, doc-backed body, so edits flow straight into the CRDT and to peers through
    /// the doc — no change callback. Returns a dispose thunk; `readOnly` renders another peer's
    /// content without an edit surface. Serialization for the drain lives in `Domain.Markdown`.
    let mountEditor (host: obj) (fragment: Y.XmlFragment) (readOnly: bool) : (unit -> unit) =
        let state = createState (createObj [ "schema" ==> schema; "plugins" ==> plugins fragment ])
        let view =
            createView host (createObj [
                "state" ==> state
                "editable" ==> (System.Func<bool>(fun () -> not readOnly))
                "handlePaste" ==> handlePaste ])
        fun () -> view.destroy ()
