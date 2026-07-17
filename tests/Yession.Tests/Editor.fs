module Yession.Tests.Editor

// Regression guards for the rich-text editor (docs/plans/03-rich-text-editing.md). These are
// the DOM-free half — the markdown round-trip, the send-time content copy, CRDT convergence,
// and the RichBody/registry foundation — so they run in the cheap tier on Node (no browser).
// They assert on MARKDOWN output and reference equality, never on brittle DOM structure. The
// input-rule *typing* behaviour needs contenteditable + real key events and is covered by the
// verify-tier browser E2E against the composer.
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

let private md (f: Y.XmlFragment) : string = (Editor.fragmentToMarkdown f).Trim ()

let tests =
    testList "Rich-text editor" [

        testCase "markdown round-trips through the fragment (parse then serialize)" <| fun () ->
            let f = freshFragment ()
            Editor.markdownIntoFragment "# Title\n\n**strong** and *em*" f
            let out = md f
            Expect.stringContains out "# Title" "heading survives the round-trip"
            Expect.stringContains out "**strong**" "strong mark survives"
            Expect.stringContains out "*em*" "emphasis survives"

        testCase "bullet lists round-trip as list items" <| fun () ->
            let f = freshFragment ()
            Editor.markdownIntoFragment "* alpha\n* beta" f
            let out = md f
            Expect.stringContains out "alpha" "first item survives"
            Expect.stringContains out "beta" "second item survives"
            // Two list markers, not one run-on paragraph.
            let markers = out.Split('\n') |> Array.filter (fun l -> l.TrimStart().StartsWith "*") |> Array.length
            Expect.equal markers 2 "two distinct list items"

        testCase "a fresh fragment serializes to empty markdown" <| fun () ->
            Expect.equal (md (freshFragment ())) "" "no content yields empty string"

        testCase "copyFragment duplicates a draft body into a queue body (send)" <| fun () ->
            let src = freshFragment ()
            Editor.markdownIntoFragment "# hi\n\n* x\n* y" src
            let dst = freshFragment ()
            Editor.copyFragment src dst
            Expect.equal (md dst) (md src) "the queue body is a content copy of the draft body"

        testCase "edits converge across two docs (y-prosemirror over the shared fragment)" <| fun () ->
            let a = Y.Doc.Create ()
            let b = Y.Doc.Create ()
            Editor.markdownIntoFragment "# From A" (a.getXmlFragment "body")
            Y.applyUpdate (b, Y.encodeStateAsUpdate a)
            Expect.equal (md (b.getXmlFragment "body")) "# From A" "B sees A's content after a CRDT sync"

        // --- RichBody / registry (the app-resolved body handle) ----------------------------

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
