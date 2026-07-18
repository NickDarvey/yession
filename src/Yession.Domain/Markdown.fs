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

    // Reading a *live* fragment with `yXmlFragmentToProseMirrorRootNode` is not side-effect-free:
    // y-prosemirror's adjacent-`Y.Text` merge (its issue #160 fix) DELETES a neighbouring text
    // node when it was authored by the reading doc's own client — which, on a body the local
    // editor is bound to, can silently drop a whole block (e.g. a second linked paragraph). So
    // serialization reads from a DETACHED SNAPSHOT on a throwaway doc: a fresh doc has a
    // different clientID, the merge is skipped, and the live body is left untouched.
    [<Import("Doc", "yjs")>]
    let private docClass : obj = jsNative
    [<Import("encodeStateAsUpdate", "yjs")>]
    let private encodeStateAsUpdate (doc: obj) : obj = jsNative
    [<Import("applyUpdate", "yjs")>]
    let private applyUpdate (doc: obj) (update: obj) : unit = jsNative
    [<Emit("new $0()")>]
    let private newDoc (cls: obj) : obj = jsNative
    [<Emit("$0.getXmlFragment($1)")>]
    let private docXmlFragment (doc: obj) (name: string) : Y.XmlFragment = jsNative
    /// The top-level root name of a doc-attached fragment (its key in `doc.share`), or `null`
    /// when the fragment is already detached / nested.
    [<Emit("(function (f) { const d = f.doc; if (!d) return null; for (const [k, v] of d.share) { if (v === f) return k; } return null; })($0)")>]
    let private rootName (fragment: Y.XmlFragment) : string = jsNative
    [<Emit("$0.doc")>]
    let private fragDoc (fragment: Y.XmlFragment) : obj = jsNative

    /// Serialize a fragment's ProseMirror doc to Markdown (durable body / agent input). Reads a
    /// detached snapshot so the live body is never mutated (see the note above).
    let ofFragment (fragment: Y.XmlFragment) : string =
        let name = rootName fragment
        let node =
            if isNull (box name) then fragmentToRootNode fragment schema
            else
                let snapshot = newDoc docClass
                applyUpdate snapshot (encodeStateAsUpdate (fragDoc fragment))
                fragmentToRootNode (docXmlFragment snapshot name) schema
        serializer.serialize node

    /// Parse Markdown into an (empty) fragment — seeds a queue body on send.
    let intoFragment (markdown: string) (fragment: Y.XmlFragment) : unit =
        rootNodeToFragment (parser.parse (if isNull (box markdown) then "" else markdown)) fragment

    /// Content-copy one fragment's document into another (draft -> queue on send). Shared
    /// types cannot be re-parented, so copy via the Markdown round-trip.
    let copy (src: Y.XmlFragment) (dst: Y.XmlFragment) : unit =
        intoFragment (ofFragment src) dst
