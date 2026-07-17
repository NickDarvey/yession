module Yession.Tests.Editor

// Regression guards for the rich-text editor (docs/plans/03-rich-text-editing.md). These are
// the DOM-free half — markdown serialization, the send-time content copy, CRDT convergence,
// and the RichBody/registry foundation — so they run in the cheap tier on Node (no browser).
// They assert on MARKDOWN output, a serialize idempotence PROPERTY, and reference equality —
// never on brittle DOM structure. The input-rule *typing* behaviour needs contenteditable +
// real key events and is covered by the verify-tier browser E2E against the composer.
//
// `prosemirror-markdown` and `y-prosemirror` are pure JS (no DOM), so `Editor.fragmentToMarkdown`
// / `markdownIntoFragment` / `copyFragment` work headless — only `mountEditor` needs a browser.

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
    Editor.markdownIntoFragment markdown f
    f

let private md (f: Y.XmlFragment) : string = (Editor.fragmentToMarkdown f).Trim ()

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
            Expect.equal (Editor.fragmentToMarkdown f) (Editor.fragmentToMarkdown f) "same fragment, same output"

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
            Editor.copyFragment src dst
            Expect.equal (md dst) (md src) "the queue body is a content copy of the draft body"

        testCase "copyFragment does not mutate the source" <| fun () ->
            let src = fromMd canonical
            let before = md src
            Editor.copyFragment src (freshFragment ())
            Expect.equal (md src) before "copying leaves the draft body untouched"

        testCase "copying an empty draft yields an empty queue body" <| fun () ->
            let dst = fromMd "# will be replaced by nothing? no — copy is additive into empty"
            // copy from an empty source into a fresh dst
            let dst2 = freshFragment ()
            Editor.copyFragment (freshFragment ()) dst2
            Expect.equal (md dst2) "" "empty source copies to empty"
            ignore dst
    ]

let private convergenceTests =
    testList "collaboration" [
        testCase "edits converge across two docs (y-prosemirror over the shared fragment)" <| fun () ->
            let a = Y.Doc.Create ()
            let b = Y.Doc.Create ()
            Editor.markdownIntoFragment "# From A" (a.getXmlFragment "body")
            Y.applyUpdate (b, Y.encodeStateAsUpdate a)
            Expect.equal (md (b.getXmlFragment "body")) "# From A" "B sees A's content after a CRDT sync"

        testCase "a later edit on A propagates to B on a second sync" <| fun () ->
            let a = Y.Doc.Create ()
            let b = Y.Doc.Create ()
            let fa = a.getXmlFragment "body"
            Editor.markdownIntoFragment "# one" fa
            Y.applyUpdate (b, Y.encodeStateAsUpdate a)
            Editor.markdownIntoFragment "# one\n\n## two" fa
            Y.applyUpdate (b, Y.encodeStateAsUpdate a)
            Expect.equal (md (b.getXmlFragment "body")) (md fa) "B converges on A's latest"
    ]

let private registryTests =
    testList "RichBody / registry" [
        testCase "BodyRegistry returns a stable RichBody per id" <| fun () ->
            let reg = BodyRegistry ()
            let a1 = reg.GetOrCreate "d1"
            let a2 = reg.GetOrCreate "d1"
            Expect.isTrue (System.Object.ReferenceEquals (a1, a2)) "same instance for the same id (U5 stability)"
            Expect.isFalse (System.Object.ReferenceEquals (a1, reg.GetOrCreate "d2")) "distinct ids give distinct bodies"

        testCase "TryFragment is None until the body is connected" <| fun () ->
            let reg = BodyRegistry ()
            reg.GetOrCreate "d1" |> ignore
            Expect.isNone (reg.TryFragment "d1") "no live fragment before Ylmish attaches (Connect)"

        testCase "Forget drops the handle" <| fun () ->
            let reg = BodyRegistry ()
            let frag = freshFragment ()
            let body = reg.GetOrCreate "d1"
            let ctx : BindContext =
                { GetText = fun () -> failwith "unused"
                  GetMap = fun () -> failwith "unused"
                  GetArray = fun () -> failwith "unused"
                  GetXmlFragment = fun () -> frag
                  Origin = box () }
            use _d = (body :> CustomElement).Connect ctx
            Expect.isSome (reg.TryFragment "d1") "present after connect"
            reg.Forget "d1"
            Expect.isNone (reg.TryFragment "d1") "gone after forget"

        testCase "RichBody exposes the live fragment after Connect" <| fun () ->
            let frag = freshFragment ()
            let body = RichBody ()
            let ctx : BindContext =
                { GetText = fun () -> failwith "unused"
                  GetMap = fun () -> failwith "unused"
                  GetArray = fun () -> failwith "unused"
                  GetXmlFragment = fun () -> frag
                  Origin = box () }
            use _d = (body :> CustomElement).Connect ctx
            Expect.isTrue body.Connected "connected after Connect"
            Expect.isTrue (System.Object.ReferenceEquals (body.Fragment, frag)) "exposes the fragment GetXmlFragment gave"
    ]

let tests =
    testList "Rich-text editor" [
        serializationTests
        idempotenceTests
        sendCopyTests
        convergenceTests
        registryTests
    ]
