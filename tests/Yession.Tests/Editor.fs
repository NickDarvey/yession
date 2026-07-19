module Yession.Tests.Editor

// Regression guards for the rich-text editor (docs/plans/03-rich-text-editing.md). These are
// the DOM-free half — markdown serialization, the send-time content copy, CRDT convergence,
// and the BodyRegistry root anchoring — so they run in the cheap tier on Node (no browser).
// They assert on MARKDOWN output, a serialize idempotence PROPERTY, and reference equality —
// never on brittle DOM structure. The input-rule *typing* behaviour needs contenteditable +
// real key events and is covered by the verify-tier browser E2E against the composer.
//
// `prosemirror-markdown` and `y-prosemirror` are pure JS (no DOM), so `Markdown.ofFragment`
// / `Markdown.intoFragment` / `Markdown.copy` work headless — only `mountEditor` needs a browser.

open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto
open Yjs
open Ylmish.Codec
open Yession.Domain
open Yession.App

/// A fresh, integrated root `Y.XmlFragment` on its own doc.
let private freshFragment () : Y.XmlFragment =
    (Y.Doc.Create ()).getXmlFragment "body"

/// A fragment seeded from markdown, and its serialized-back markdown (trimmed).
let private fromMd (markdown: string) : Y.XmlFragment =
    let f = freshFragment ()
    Markdown.intoFragment markdown f
    f

let private md (f: Y.XmlFragment) : string = (Markdown.ofFragment f).Trim ()

/// A document exercising every node/mark the Linear-like schema supports.
let private canonical =
    String.concat "\n" [
        "# Heading one"
        ""
        "## Heading two"
        ""
        "A paragraph with **bold**, *italic*, `code`, and [a link](https://example.com)."
        ""
        "> a block quote"
        ""
        "* alpha"
        "* beta"
        ""
        "1. first"
        "2. second"
        ""
        "```"
        "code block"
        "```"
    ]

let private serializationTests =
    testList "serialization" [
        testCase "a fresh fragment serializes to empty markdown" <| fun () ->
            Expect.equal (md (freshFragment ())) "" "no content yields empty string"

        testCase "fragmentToMarkdown is deterministic" <| fun () ->
            let f = fromMd canonical
            Expect.equal (Markdown.ofFragment f) (Markdown.ofFragment f) "same fragment, same output"

        testCase "every schema feature survives the round-trip" <| fun () ->
            let out = md (fromMd canonical)
            Expect.stringContains out "# Heading one" "h1"
            Expect.stringContains out "## Heading two" "h2 preserves level"
            Expect.stringContains out "**bold**" "strong"
            Expect.stringContains out "*italic*" "emphasis"
            Expect.stringContains out "`code`" "inline code"
            Expect.stringContains out "[a link](https://example.com)" "link"
            Expect.stringContains out "> a block quote" "blockquote"
            Expect.stringContains out "code block" "code block content"

        testCase "heading levels are preserved" <| fun () ->
            Expect.stringContains (md (fromMd "### three")) "### three" "level-3 heading"

        testCase "bullet list stays two items" <| fun () ->
            let out = md (fromMd "* alpha\n* beta")
            let items = out.Split('\n') |> Array.filter (fun l -> l.TrimStart().StartsWith "*") |> Array.length
            Expect.equal items 2 "two distinct bullet items"

        testCase "ordered list is numbered" <| fun () ->
            let out = md (fromMd "1. first\n2. second")
            Expect.stringContains out "1. first" "first ordered item"
            Expect.stringContains out "2. second" "second ordered item"

        testCase "multiple links across blocks all survive serialization" <| fun () ->
            let out = md (fromMd "[one](https://a.test)\n\n[two](https://b.test)")
            Expect.stringContains out "[one](https://a.test)" "first link kept"
            Expect.stringContains out "[two](https://b.test)" "second link kept"

        // `ofFragment` reads through `yXmlFragmentToProseMirrorRootNode`, which can MUTATE a
        // live, locally-authored doc (y-prosemirror's adjacent-Y.Text merge) and silently drop
        // a block. `ofFragment` guards against that by reading a detached snapshot; this pins
        // that a read never changes what the next read sees.
        testCase "ofFragment does not mutate the source fragment" <| fun () ->
            let f = fromMd "[one](https://a.test)\n\n[two](https://b.test)"
            let first = md f
            let second = md f
            Expect.equal second first "re-reading the same fragment is stable (the read is side-effect-free)"

        // The markdown-level guard above cannot fail on its own: y-prosemirror's merge is
        // content-preserving, so the serialized output is identical even when the CRDT was
        // mutated (only the editor binding's next edit exposes the damage). This test asserts
        // at the CRDT level instead, on the exact layout that trips the merge: two same-client
        // `Y.XmlText` siblings authored in SEPARATE transactions become adjacent items, the
        // second being the first's `_item.right` — deterministically, no editor involved.
        testCase "ofFragment is a pure read: no CRDT ops under same-client Y.Text adjacency" <| fun () ->
            let doc = Y.Doc.Create ()
            let frag = doc.getXmlFragment "body"
            let p = Y.XmlElement.Create "paragraph"
            frag.insert (0., [| !^p |])
            let insertLinked (index: float) (text: string) (href: string) =
                doc.transact (fun _ ->
                    let t = Y.XmlText.Create ()
                    t.insert (0, text, !!(createObj [ "link" ==> createObj [ "href" ==> href; "title" ==> null ] ]))
                    p.insert (index, [| !^t |]))
            insertLinked 0. "one " "https://a.test"
            insertLinked 1. "two" "https://b.test"
            Expect.equal (p.toArray().Count) 2 "precondition: the paragraph holds two sibling Y.XmlText items"
            let before = Y.encodeStateVector !^doc
            let out = Markdown.ofFragment frag
            Expect.stringContains out "one" "first text serialized"
            Expect.stringContains out "two" "second text serialized"
            Expect.equal (p.toArray().Count) 2 "no Y.XmlText was merged/deleted by the read"
            // An empty v1 update encodes to exactly 2 bytes (0 structs + 0 deletions); anything
            // larger means the read emitted CRDT ops.
            let opsSince = Y.encodeStateAsUpdate (doc, before)
            Expect.isTrue (opsSince.length <= 2) "the read produced no new CRDT ops"

        // `ofFragment` can only snapshot fragments it can find by root name; a doc-attached but
        // NESTED fragment would silently take the live-read path and re-expose the mutation bug,
        // so it must refuse instead. (Bodies are top-level roots by invariant — RichText.fs.)
        testCase "ofFragment refuses a nested (non-root) live fragment" <| fun () ->
            let doc = Y.Doc.Create ()
            let map : Y.Map<obj> = doc.getMap "root"
            let frag = Y.XmlFragment.Create ()
            map.set ("body", !!frag) |> ignore
            Expect.throws (fun () -> Markdown.ofFragment frag |> ignore)
                "a nested live fragment must fail loudly, not read (and possibly mutate) the live doc"
    ]

// The strongest non-brittle guard: parsing then serializing is a FIXED POINT — running it
// again changes nothing. This pins the whole schema/parser/serializer pipeline without
// hardcoding one exact expected string (which the serializer is free to normalize).
let private idempotenceTests =
    testList "idempotence" [
        for name, input in
            [ "heading", "# Title"
              "marks", "text with **bold** and *em* and `code`"
              "bullets", "* a\n* b\n* c"
              "ordered", "1. a\n2. b"
              "quote", "> quoted"
              "link", "see [here](https://example.com)"
              "mixed", canonical ] ->
            testCase (sprintf "parse∘serialize is a fixed point: %s" name) <| fun () ->
                let once = md (fromMd input)
                let twice = md (fromMd once)
                Expect.equal twice once (sprintf "re-serializing '%s' must be stable" name)
    ]

let private sendCopyTests =
    testList "send content-copy" [
        testCase "copyFragment duplicates a draft body into a queue body" <| fun () ->
            let src = fromMd "# hi\n\n* x\n* y"
            let dst = freshFragment ()
            Markdown.copy src dst
            Expect.equal (md dst) (md src) "the queue body is a content copy of the draft body"

        testCase "copyFragment does not mutate the source" <| fun () ->
            let src = fromMd canonical
            let before = md src
            Markdown.copy src (freshFragment ())
            Expect.equal (md src) before "copying leaves the draft body untouched"

        testCase "copying an empty draft yields an empty queue body" <| fun () ->
            let dst = fromMd "# will be replaced by nothing? no — copy is additive into empty"
            // copy from an empty source into a fresh dst
            let dst2 = freshFragment ()
            Markdown.copy (freshFragment ()) dst2
            Expect.equal (md dst2) "" "empty source copies to empty"
            ignore dst
    ]

let private convergenceTests =
    testList "collaboration" [
        testCase "edits converge across two docs (y-prosemirror over the shared fragment)" <| fun () ->
            let a = Y.Doc.Create ()
            let b = Y.Doc.Create ()
            Markdown.intoFragment "# From A" (a.getXmlFragment "body")
            Y.applyUpdate (b, Y.encodeStateAsUpdate a)
            Expect.equal (md (b.getXmlFragment "body")) "# From A" "B sees A's content after a CRDT sync"

        testCase "a later edit on A propagates to B on a second sync" <| fun () ->
            let a = Y.Doc.Create ()
            let b = Y.Doc.Create ()
            let fa = a.getXmlFragment "body"
            Markdown.intoFragment "# one" fa
            Y.applyUpdate (b, Y.encodeStateAsUpdate a)
            Markdown.intoFragment "# one\n\n## two" fa
            Y.applyUpdate (b, Y.encodeStateAsUpdate a)
            Expect.equal (md (b.getXmlFragment "body")) (md fa) "B converges on A's latest"
    ]

let private registryTests =
    testList "BodyRegistry" [
        testCase "Fragment returns a stable top-level root per id" <| fun () ->
            let reg = BodyRegistry (Y.Doc.Create ())
            let a1 = reg.Fragment "d1"
            let a2 = reg.Fragment "d1"
            Expect.isTrue (System.Object.ReferenceEquals (a1, a2)) "same fragment for the same id (idempotent root)"
            Expect.isFalse (System.Object.ReferenceEquals (a1, reg.Fragment "d2")) "distinct ids give distinct fragments"

        testCase "Fragment binds the doc's named root (edits are visible through it)" <| fun () ->
            let doc = Y.Doc.Create ()
            let reg = BodyRegistry doc
            Markdown.intoFragment "# hi" (reg.Fragment "d1")
            // The same key resolves to the same live root, so the write is visible on re-read
            // and via the doc's own getXmlFragment.
            Expect.equal ((Markdown.ofFragment (reg.Fragment "d1")).Trim ()) "# hi" "re-read sees the write"
            Expect.equal ((Markdown.ofFragment (doc.getXmlFragment "d1")).Trim ()) "# hi" "it is the doc's named root"

        testCase "a fresh id's fragment is empty" <| fun () ->
            let reg = BodyRegistry (Y.Doc.Create ())
            Expect.equal ((Markdown.ofFragment (reg.Fragment "never-written")).Trim ()) "" "an untouched body is empty"
    ]

let tests =
    testList "Rich-text editor" [
        serializationTests
        idempotenceTests
        sendCopyTests
        convergenceTests
        registryTests
    ]
