namespace Yession.App

open Fable.Core
open Lit
open Yession.App.ProseMirror

/// Read-only rendering of a Markdown body to formatted rich text for the conversation
/// timeline — the mirror of the composer's live formatting, so the timeline shows exactly
/// what the editor produced. It reuses the *same* `prosemirror-markdown` parser the editor's
/// paste path and the drain serializer use (`ProseMirror.mdParser`), then walks the parsed
/// document into Lit templates. That document is pure data (no DOM), so the walk runs
/// identically under Node (SSR) and in the browser, and every text value flows through a
/// Lit text/attribute hole — so it is escaped, never injected.
module RichText =

    // Marks wrap a text run from the innermost outward. prosemirror-markdown's default schema
    // carries em / strong / code / link; an unknown mark passes its content through unwrapped.
    let private wrapMark (mark: obj) (inner: TemplateResult) : TemplateResult =
        match markTypeName mark with
        | "strong" -> html $"""<strong class="{Style.proseStrong}">{inner}</strong>"""
        | "em" -> html $"""<em>{inner}</em>"""
        | "code" -> html $"""<code class="{Style.proseCode}">{inner}</code>"""
        | "link" ->
            let href = markHref mark
            html $"""<a class="{Style.proseLink}" href="{href}" target="_blank" rel="noopener noreferrer">{inner}</a>"""
        | "s" | "strike" | "strikethrough" | "del" -> html $"""<s>{inner}</s>"""
        | _ -> inner

    /// One inline node — a text run under its marks, or a hard break.
    let private inlineNode (node: Node) : TemplateResult =
        if nodeIsText node then
            Array.foldBack wrapMark (nodeMarks node) (html $"{nodeText node}")
        elif nodeTypeName node = "hard_break" then html $"<br>"
        else html $"{nodeTextContent node}"

    let private children (node: Node) : Node list =
        [ for i in 0 .. nodeChildCount node - 1 -> nodeChild node i ]

    let private inlineContent (node: Node) : TemplateResult list =
        children node |> List.map inlineNode

    /// One block node. Recurses for nested structure (lists, list items, blockquotes).
    let rec private block (node: Node) : TemplateResult =
        match nodeTypeName node with
        | "paragraph" -> html $"""<p class="{Style.proseP}">{inlineContent node}</p>"""
        | "heading" ->
            match headingLevel node with
            | 1 -> html $"""<h1 class="{Style.proseH1}">{inlineContent node}</h1>"""
            | 2 -> html $"""<h2 class="{Style.proseH2}">{inlineContent node}</h2>"""
            | 3 -> html $"""<h3 class="{Style.proseH3}">{inlineContent node}</h3>"""
            | _ -> html $"""<h4 class="{Style.proseH4}">{inlineContent node}</h4>"""
        | "blockquote" -> html $"""<blockquote class="{Style.proseQuote}">{blocks node}</blockquote>"""
        | "code_block" -> html $"""<pre class="{Style.prosePre}"><code>{nodeTextContent node}</code></pre>"""
        | "bullet_list" -> html $"""<ul class="{Style.proseUl}">{blocks node}</ul>"""
        | "ordered_list" -> html $"""<ol class="{Style.proseOl}" start="{listStart node}">{blocks node}</ol>"""
        | "list_item" -> html $"""<li class="{Style.proseLi}">{blocks node}</li>"""
        | "horizontal_rule" -> html $"""<hr class="{Style.proseHr}">"""
        // Any node the default schema adds later still shows its text rather than vanishing.
        | _ -> html $"""<p class="{Style.proseP}">{inlineContent node}</p>"""

    and private blocks (node: Node) : TemplateResult list =
        children node |> List.map block

    /// Render a Markdown body as read-only formatted rich text. A parse failure (or an absent
    /// body) degrades to the raw text so a message can never silently vanish from the timeline.
    let render (markdown: string) : TemplateResult =
        let md = if isNull (box markdown) then "" else markdown
        let doc = mdParser.parse md
        if isNull (box doc) then html $"{md}" else html $"{blocks doc}"
