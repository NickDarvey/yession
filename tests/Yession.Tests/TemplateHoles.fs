module Yession.Tests.TemplateHoles

// What a Lit template hole is allowed to leave unsaid.
//
// `html $"""..."""` is a `FormattableString`, so every hole takes `obj`. Anything at all
// type-checks there, and a value that is not a string renders as whatever JS makes of it —
// which for a single-case union is nothing at all.
//
// That is not hypothetical. `TerminalTitle` replaced a `string`, six holes still read
// `{view.Title}`, all six compiled, and the terminal tab buttons rendered empty and lost
// their accessible names. Nothing in the type system could see it; the acceptance suite's
// "no control is announced as nothing but button" is what caught it, one tier and several
// minutes away from the change that caused it.
//
// The compiler cannot be asked to help here, and that was checked rather than assumed:
//
//   * `%s` is impossible. F# rejects `$"%s{x}"` typed as a `FormattableString` outright
//     (FS3376), so printf specifiers and Lit templates are mutually exclusive.
//   * no warning covers it. With `--warnon` for every implicit-conversion warning and
//     `--warn:5`, a union boxed into a hole is silent — because the hole compiles to an
//     explicit `box` inside `FormattableStringFactory.Create`, which is not a conversion
//     the warnings model. (`--warnon:1182` firing on an unused binding in the same compile
//     is what proves the flags were live and the silence real.)
//
// So the rule is written down here instead: a hole that renders a RECORD FIELD says what
// it renders. `{view.Title}` becomes `{(TerminalTitle.value view.Title : string)}`, and a
// field whose type later changes fails to compile at the hole rather than rendering blank.
//
// Only that shape. F# naming is what makes it separable: a module value reads
// `Style.person` (Pascal then camel) and a record field reads `view.Title` (camel then
// Pascal), so the 800-odd style and handler holes are not swept up in a rule that has
// nothing to say about them.
//
// No capability: it reads files. `EmitSources` and `TestSources` are the other contracts of
// this shape, and hold the same line — a guard on the SOURCE fires on the pull request that
// writes the hole, not on the tier that eventually renders it.

open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto
open System.Text.RegularExpressions

let private nodeFs : obj = importAll "node:fs"

[<Emit("$0.readdirSync($1, { recursive: true }).filter(n => n.endsWith('.fs'))")>]
let private fsharpFilesUnder (fs: obj) (root: string) : string array = jsNative

[<Emit("$0.readFileSync($1, 'utf8')")>]
let private readText (fs: obj) (path: string) : string = jsNative

let private roots = [ "app"; "src" ]
let private generated = [ "/out/"; "/obj/"; "/bin/"; "fable_modules"; "node_modules" ]

/// Assembled rather than written literally, for the reason `EmitSources` gives: a file that
/// matched its own pattern would need exempting from its own scan, and an exemption is a hole.
let private q = "\""
let private templateMarker = "html $" + q + q + q

/// A hole rendering a record field: a camelCase receiver, then a PascalCase field, and
/// nothing else — no call, no annotation, no further dots.
let private fieldHole = Regex (@"\{([a-z][A-Za-z0-9_]*\.[A-Z][A-Za-z0-9_]*)\}")

/// Does this file build Lit templates at all? A hole only takes `obj` inside one.
let private buildsTemplates (text: string) = text.Contains templateMarker

type private Hole = { File : string; Line : int; Text : string }

let private holesIn (root: string) : Hole list =
    fsharpFilesUnder nodeFs root
    |> Array.toList
    |> List.map (fun name -> sprintf "%s/%s" root name)
    |> List.filter (fun path -> generated |> List.forall (fun bad -> not (path.Contains bad)))
    |> List.collect (fun path ->
        let text = readText nodeFs path
        if not (buildsTemplates text) then []
        else
            fieldHole.Matches text
            |> Seq.map (fun m ->
                { File = path
                  Line = (text.Substring (0, m.Index)).Split('\n').Length
                  Text = m.Groups.[1].Value })
            |> List.ofSeq)

let private unsaid = roots |> List.collect holesIn

let private describe (h: Hole) = sprintf "%s:%d  {%s}" h.File h.Line h.Text

let tests =
    testList "Template holes" [
        testCase "the reader can see a template file at all" <| fun () ->
            // Without this the rule below passes by finding nothing, for ever, the moment a
            // path or an extension changes under it.
            let scanned =
                roots
                |> List.collect (fun root ->
                    fsharpFilesUnder nodeFs root
                    |> Array.toList
                    |> List.map (fun n -> sprintf "%s/%s" root n)
                    |> List.filter (fun p -> generated |> List.forall (fun bad -> not (p.Contains bad)))
                    |> List.filter (fun p -> buildsTemplates (readText nodeFs p)))
            Expect.isNonEmpty scanned "some file builds Lit templates"

        testCase "the reader recognises a field hole" <| fun () ->
            Expect.isTrue (fieldHole.IsMatch "<span>{view.Title}</span>") "camel then Pascal"

        testCase "a module value is not a field hole" <| fun () ->
            // The whole reason the rule can be narrow: `Style.person` reads the other way
            // round, and there are hundreds of those with nothing to answer for.
            Expect.isFalse (fieldHole.IsMatch """class="{Style.person}" """) "Pascal then camel"

        testCase "a hole that already says what it renders is not a field hole" <| fun () ->
            Expect.isFalse
                (fieldHole.IsMatch "<span>{(TerminalTitle.value view.Title : string)}</span>")
                "annotated"

        testCase "every template hole rendering a record field says what it renders" <| fun () ->
            Expect.equal
                (unsaid |> List.map describe)
                []
                "annotate the hole — `{x.Field}` becomes `{(x.Field : Type)}`, so a field whose type changes fails to compile here rather than rendering blank"
    ]
