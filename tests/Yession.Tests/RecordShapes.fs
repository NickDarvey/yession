module Yession.Tests.RecordShapes

// When two record types have the same field names, which one does a bare
// `{ … = …; … = … }` build?
//
// The one declared LAST, and silently: there is no diagnostic for this at any warning
// level, so a record added today reaches back and re-points every construction already
// written against the other one, in files its author never opened. It happened here.
// `SecretId` is scope-and-name; a second wire type was given those same two field names,
// and constructions in `SecretsState.fs` quietly became the wrong type. The repair was to
// qualify them one site at a time, after the compiler had already accepted the wrong
// reading everywhere it could still be inferred.
//
// `RequireQualifiedAccess` on a record takes its labels out of unqualified scope
// altogether. The type can no longer capture anybody else's construction, and its own
// sites name it or fail to compile — the error even names the type it wants. So the rule
// is: a field set carried by two or more record types is allowed only when EVERY type in
// that group has the attribute. One left bare is the same ambiguity with fewer candidates.
//
// It is deliberately blind to whether the two are ever in scope together. That is not a
// property of either declaration — it belongs to some third file's `open` list, which
// changes without either author present, which is exactly how the original one landed.
//
// The rule is about CONSTRUCTION, and an exact field set is the right unit for it: a record
// expression must give every field, so only a type with precisely these labels can be built
// from them. A record PATTERN may name a subset, which would make any shared label a hazard
// — but only where the matched type is unknown, and the one form that leaves it unknown
// (destructuring in a parameter position, `let f { Reason = r } = …`) appears nowhere in
// this repository. If it ever does, this rule is not the one that catches it.
//
// It also fires on a group whose field TYPES differ enough that a capture could not have
// type-checked (the synced session state and its adaptive mirror). Those cost one
// qualified construction each, and the alternative is a rule that reads field types out of
// source text and is wrong in ways nobody can see.
//
// Populations are scoped, and not as an exemption. `examples/` is standalone by rule
// (AGENTS.md): an example references nothing from the domain, so it cannot be in scope
// with it, and its copies of product shapes are the duplication that rule ASKS for. Each
// example is therefore scanned against itself and never against the product — which still
// catches a collision inside one, and leaves no directory unscanned.
//
// No capability: it reads files. `TestSources`, `EmitSources` and `LockSource` are the
// other source contracts here, and this is the same trade — the fault is cheap to see at
// the declaration and expensive to see anywhere else.

open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto
open System.Text.RegularExpressions

let private nodeFs : obj = importAll "node:fs"

// Node 20+ walks a tree for us; the repo pins 24. One expression, no declarations, nothing
// repeated — the shape `EmitSources` requires.
[<Emit("$0.readdirSync($1, { recursive: true }).filter(n => n.endsWith('.fs'))")>]
let private fsharpFilesUnder (fs: obj) (root: string) : string array = jsNative

[<Emit("$0.readFileSync($1, 'utf8')")>]
let private readText (fs: obj) (path: string) : string = jsNative

/// Build output and vendored code: none of it is anybody here's to hold to a rule.
let private generated = [ "/out/"; "/obj/"; "/bin/"; "fable_modules"; "node_modules" ]

/// A type declaration. Group 1 is its indent, 2 any attributes written inline after
/// `and`, 3 the name, 4 whatever follows `=` on the same line.
let private declaration =
    Regex (@"^([ \t]*)(?:type|and)[ \t]+(?:\[<([^\]]*)>\][ \t]*)?(?:private[ \t]+|internal[ \t]+)?([A-Za-z_]\w*)(?:<[^>]*>)?[ \t]*=[ \t]*(.*)$")

/// A field label inside a record body: after the brace, a newline or a semicolon. The
/// lookahead keeps `::` out, which is a cons in a default value, not an annotation.
let private label = Regex (@"(?:^|;|\n)[ \t]*(?:mutable[ \t]+)?([A-Z]\w*)[ \t]*:(?!:)")

/// One record type, as the source text has it.
type private Shape =
    { Owner : string
      Where : string
      Fields : string list
      Guarded : bool }

/// The declaration's body: what follows `=` on its own line, then every line indented
/// deeper than it. A blank line continues the body; the first line at or left of the
/// declaration's own indent ends it.
let private bodyFrom (lines: string array) (start: int) (indent: int) (rest: string) =
    let rec gather acc i =
        if i >= lines.Length then List.rev acc
        else
            let line = lines.[i]
            if line.Trim () = "" then gather (line :: acc) (i + 1)
            elif line.Length - (line.TrimStart ()).Length > indent then gather (line :: acc) (i + 1)
            else List.rev acc
    String.concat "\n" (rest :: gather [] (start + 1))

/// The inside of the body's first balanced brace pair, when the body IS a record. `{|` is
/// an anonymous record and belongs to no declaration; anything before the brace that is
/// not an access modifier means this is a union or an abbreviation that merely contains
/// one.
let private recordBody (body: string) =
    let opened = body.IndexOf '{'
    if opened < 0 then None
    elif opened + 1 < body.Length && body.[opened + 1] = '|' then None
    else
        let before = (body.Substring (0, opened)).Trim ()
        if before <> "" && before <> "private" && before <> "internal" then None
        else
            let rec scan depth i =
                if i >= body.Length then None
                elif body.[i] = '{' then scan (depth + 1) (i + 1)
                elif body.[i] = '}' && depth = 1 then Some (body.Substring (opened + 1, i - opened - 1))
                elif body.[i] = '}' then scan (depth - 1) (i + 1)
                else scan depth (i + 1)
            scan 0 opened

/// Whether the declaration carries the attribute — written inline after `and`, or on the
/// lines above it, across the doc comment that usually sits between.
let private guarded (lines: string array) (index: int) (attributes: string) =
    if attributes.Contains "RequireQualifiedAccess" then true
    else
        let rec look i =
            if i < 0 then false
            else
                let line = lines.[i].Trim ()
                if line = "" || line.StartsWith "//" then look (i - 1)
                elif line.StartsWith "[<" then line.Contains "RequireQualifiedAccess" || look (i - 1)
                else false
        look (index - 1)

let private fieldsOf (inner: string) =
    label.Matches inner
    |> Seq.map (fun m -> m.Groups.[1].Value)
    |> Seq.distinct
    |> Seq.sort
    |> List.ofSeq

/// Every record declared in one file's text.
let private shapesIn (path: string) (text: string) : Shape list =
    let lines = (text.Replace ("\r\n", "\n")).Split '\n'
    [ for index in 0 .. lines.Length - 1 do
        let m = declaration.Match lines.[index]
        if m.Success then
            let indent = m.Groups.[1].Value.Length
            let body = bodyFrom lines index indent (m.Groups.[4].Value)
            match recordBody body with
            | Some inner ->
                let fields = fieldsOf inner
                if not (List.isEmpty fields) then
                    yield
                        { Owner = m.Groups.[3].Value
                          Where = sprintf "%s:%d" path (index + 1)
                          Fields = fields
                          Guarded = guarded lines index (m.Groups.[2].Value) }
            | None -> () ]

let private sourcesUnder (root: string) =
    fsharpFilesUnder nodeFs root
    |> Array.toList
    |> List.map (fun name -> sprintf "%s/%s" root name)
    |> List.filter (fun path -> generated |> List.forall (fun bad -> not (path.Contains bad)))

/// The product and its suite compile against each other and share namespaces, so a field
/// set repeated across them is the ambiguity. Each example is its own population, for the
/// reason at the top of this file.
let private populations : (string * string list) list =
    let product = [ "app"; "src"; "tests" ] |> List.collect sourcesUnder
    let examples =
        sourcesUnder "examples"
        |> List.groupBy (fun path -> (path.Split '/').[1])
        |> List.map (fun (name, files) -> sprintf "examples/%s" name, files)
    ("the product and its suite", product) :: examples

let private scanned =
    lazy (populations
          |> List.map (fun (name, files) ->
              name, files |> List.collect (fun path -> shapesIn path (readText nodeFs path))))

let private allShapes = lazy (scanned.Value |> List.collect snd)

/// The groups that break the rule, as one line each.
let private offenders () =
    scanned.Value
    |> List.collect (fun (population, shapes) ->
        shapes
        |> List.groupBy (fun shape -> shape.Fields)
        |> List.filter (fun (_, group) ->
            List.length group > 1 && group |> List.exists (fun shape -> not shape.Guarded))
        |> List.map (fun (fields, group) ->
            sprintf
                "  in %s, { %s } is carried by %s"
                population
                (String.concat "; " fields)
                (group
                 |> List.map (fun shape ->
                     sprintf "%s (%s)%s" shape.Owner shape.Where (if shape.Guarded then "" else " — unqualified"))
                 |> String.concat ", ")))

// The fixtures are assembled with escapes rather than written across real lines, for the
// reason `TestSources` and `EmitSources` assemble theirs: a declaration spelled out down
// the page here would be a real declaration in a real scanned file, and this suite would
// be reading its own fixtures back as product code.
let private plainFixture = "type Fixture =\n    { Alpha : int\n      Beta : string }"
let private inlineFixture = "type Fixture = { Alpha : int; Beta : string }"
let private attributedFixture = "[<RequireQualifiedAccess>]\ntype Fixture =\n    { Alpha : int }"
let private recursiveFixture = "and [<RequireQualifiedAccess>] Fixture =\n    { Alpha : int }"
let private unionFixture = "type Fixture =\n    | Only of int"
let private commentedFixture = "/// type Fixture = { Alpha : int }\ntype Real =\n    { Gamma : int }"

let private readOne (text: string) = shapesIn "fixture.fs" text

let tests =
    testList "Record shapes" [

        // Anti-vacuity, and the reason the rest can be believed: a reader that has stopped
        // seeing declarations passes exactly like a repository that obeys the rule.
        testCase "the reader can see record declarations at all" <| fun () ->
            Expect.isTrue
                (List.length allShapes.Value > 250)
                (sprintf
                    "found only %d record declarations across %s — matching none of them would mean the reader has stopped seeing records, not that the rule holds"
                    (List.length allShapes.Value)
                    (populations |> List.map fst |> String.concat ", "))

        testCase "the reader collects the fields of a record written across lines" <| fun () ->
            Expect.equal
                (readOne plainFixture |> List.map (fun s -> s.Fields))
                [ [ "Alpha"; "Beta" ] ]
                "a record spanning several lines must still read as one"

        testCase "the reader collects the fields of a record written on one line" <| fun () ->
            Expect.equal
                (readOne inlineFixture |> List.map (fun s -> s.Fields))
                [ [ "Alpha"; "Beta" ] ]
                "the single-line form is the one the original collision was written in"

        testCase "the reader sees the attribute above a declaration" <| fun () ->
            Expect.equal
                (readOne attributedFixture |> List.map (fun s -> s.Guarded))
                [ true ]
                "the attribute usually sits on its own line above the type"

        testCase "the reader sees the attribute written inline after `and`" <| fun () ->
            Expect.equal
                (readOne recursiveFixture |> List.map (fun s -> s.Guarded))
                [ true ]
                "every event payload is an `and` case, where the attribute has to go inline"

        testCase "a union is not a record" <| fun () ->
            Expect.isEmpty (readOne unionFixture) "a union has no field labels to capture anything with"

        testCase "a declaration inside a doc comment is not a declaration" <| fun () ->
            // Prose about this rule contains examples of records, this file included.
            Expect.equal
                (readOne commentedFixture |> List.map (fun s -> s.Owner))
                [ "Real" ]
                "a commented declaration is prose, not a type"

        testCase "record types that share a field set all require qualified access" <| fun () ->
            let found = offenders ()
            Expect.isEmpty
                found
                (sprintf
                    "these field sets are carried by more than one record type, so a bare construction of one silently builds whichever was declared last:\n%s\nGive every type in the group [<RequireQualifiedAccess>], and name the type at its construction sites"
                    (String.concat "\n" found))
    ]
