namespace Yession.Domain

open Fable.Core
open Yjs

/// Headless ProseMirror-markdown serialization for rich-text bodies. A body is a
/// `Y.XmlFragment` holding a ProseMirror document; this converts between that fragment and
/// Markdown with no DOM, so it is shared by the browser editor AND the Session Process drain
/// (which snapshots a queue body to Markdown for the durable `MessageSent`). Pure
/// `[<Import>]` interop against the same npm packages the editor uses — no authored JS.
module Markdown =

    type Schema = obj
    type private Node = obj

    type [<AllowNullLiteral>] private MarkdownParser =
        abstract parse : string -> Node
    type [<AllowNullLiteral>] private MarkdownSerializer =
        abstract serialize : Node -> string

    /// The prosemirror-markdown schema — the single source of the node/mark vocabulary the
    /// editor and the serializer share.
    [<Import("schema", "prosemirror-markdown")>]
    let schema : Schema = jsNative

    [<Import("defaultMarkdownParser", "prosemirror-markdown")>]
    let private parser : MarkdownParser = jsNative
    [<Import("defaultMarkdownSerializer", "prosemirror-markdown")>]
    let private serializer : MarkdownSerializer = jsNative
    [<Import("yXmlFragmentToProseMirrorRootNode", "y-prosemirror")>]
    let private fragmentToRootNode (fragment: Y.XmlFragment) (schema: Schema) : Node = jsNative
    [<Import("prosemirrorToYXmlFragment", "y-prosemirror")>]
    let private rootNodeToFragment (node: Node) (fragment: Y.XmlFragment) : unit = jsNative

    /// Serialize a fragment's ProseMirror doc to Markdown (durable body / agent input).
    let ofFragment (fragment: Y.XmlFragment) : string =
        serializer.serialize (fragmentToRootNode fragment schema)

    /// Parse Markdown into an (empty) fragment — seeds a queue body on send.
    let intoFragment (markdown: string) (fragment: Y.XmlFragment) : unit =
        rootNodeToFragment (parser.parse (if isNull (box markdown) then "" else markdown)) fragment

    /// Content-copy one fragment's document into another (draft -> queue on send). Shared
    /// types cannot be re-parented, so copy via the Markdown round-trip.
    let copy (src: Y.XmlFragment) (dst: Y.XmlFragment) : unit =
        intoFragment (ofFragment src) dst
