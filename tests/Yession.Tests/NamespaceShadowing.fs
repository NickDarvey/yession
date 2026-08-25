module Yession.Tests.NamespaceShadowing

// `open Yession.Domain` brings every SUB-NAMESPACE of it into scope by short name. So the
// moment the domain is split into feature namespaces, `Yession.Domain.Terminals` puts the
// name `Terminals` in front of every file that opens the domain — including files that
// already meant something else by it.
//
// What happens then is the record-shape fault again, on a different axis. Given a file
// whose enclosing scope offers a module `Terminals`, and an `open` that offers a namespace
// `Terminals`, a reference to `Terminals.foo` resolves to the NAMESPACE when the namespace
// has a `foo`, and falls through to the module when it does not. Neither outcome is
// reported. Verified against the compiler rather than assumed: with a member of the same
// name on both sides, the opened namespace wins silently; with no shared member, the
// module resolves and the build is clean.
//
// So the fall-through is what makes the natural names usable — `Yession.Domain.Terminals`
// alongside `Yession.Host.Terminals` is fine while nothing is named twice — and it is also
// what makes the hazard invisible. Nothing goes wrong until somebody adds a member to the
// domain side that the module already has, in a file that has never heard of either. That
// is the same shape as the `{ Scope; Name }` capture: a declaration reaching back to
// re-point call sites its author never opened.
//
// The rule is therefore about MEMBERS, not names. Two things may share a short name; they
// may not also share a member. Sharing the name alone is not the fault, which matters
// because the codebase already does it four times over (`Yession.Domain` beside the
// suite's `Yession.Tests.Domain`, `Yession.Manager` beside `Yession.Host.Manager`) and
// those are correct — they are simply disjoint, and this pins them there.
//
// A namespace's members are its types and its modules: a namespace cannot hold a `let`, so
// nothing else is reachable as `Short.member`. A module's are those plus its values.
//
// It is deliberately blind to whether the two are ever in scope together, for the reason
// `RecordShapes` is: that belongs to some third file's `open` list, which changes without
// either author present.
//
// No capability: it reads files, like `RecordShapes`, `TestSources`, `EmitSources` and
// `LockSource` beside it.

open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto
open System.Text.RegularExpressions

let private nodeFs : obj = importAll "node:fs"

[<Emit("$0.readdirSync($1, { recursive: true }).filter(n => n.endsWith('.fs'))")>]
let private fsharpFilesUnder (fs: obj) (root: string) : string array = jsNative

[<Emit("$0.readFileSync($1, 'utf8')")>]
let private readText (fs: obj) (path: string) : string = jsNative

/// Build output and vendored code: none of it is anybody here's to hold to a rule.
let private generated = [ "/out/"; "/obj/"; "/bin/"; "fable_modules"; "node_modules" ]

/// The declaration that opens a scope. A `module` with no dot in its name is a module
/// INSIDE the current scope, not the file's own — it is a member, and the member regex
/// below is what picks it up.
let private namespaceDecl = Regex (@"^namespace[ \t]+(?:rec[ \t]+)?([A-Za-z_][\w.]*)[ \t]*$")
let private fileModuleDecl = Regex (@"^module[ \t]+(?:rec[ \t]+)?([A-Za-z_][\w.]*\.[A-Za-z_]\w*)[ \t]*$")

/// A type or module declared at the top level of whatever scope is open.
let private memberDecl =
    Regex (@"^(?:type|and|module)[ \t]+(?:\[<[^\]]*>\][ \t]*)?(?:private[ \t]+|internal[ \t]+)?([A-Za-z_]\w*)")

/// A value binding, which only a module can hold.
let private valueDecl =
    Regex (@"^let[ \t]+(?:mutable[ \t]+|private[ \t]+|rec[ \t]+|inline[ \t]+)*([a-zA-Z_]\w*)")

/// One scope, as the source text has it. `Full` is what it is called; `Short` is the name
/// an `open` of its parent puts in front of everybody.
type private Surface =
    { Full : string
      IsNamespace : bool
      Members : string list
      Where : string list }

let private shortOf (full: string) =
    let parts = full.Split '.'
    parts.[parts.Length - 1]

/// Every scope one file declares, with the members it puts in each. A file may open more
/// than one namespace in sequence, so the current scope moves as the lines are read.
let private surfacesIn (path: string) (text: string) : Surface list =
    let lines = (text.Replace ("\r\n", "\n")).Split '\n'
    let mutable current : (string * bool) option = None
    let found = System.Collections.Generic.Dictionary<string * bool, ResizeArray<string>> ()
    for line in lines do
        let ns = namespaceDecl.Match line
        let md = fileModuleDecl.Match line
        if ns.Success then current <- Some (ns.Groups.[1].Value, true)
        elif md.Success then current <- Some (md.Groups.[1].Value, false)
        else
            match current with
            | None -> ()
            | Some (name, isNamespace) ->
                let add (value: string) =
                    let key = name, isNamespace
                    if not (found.ContainsKey key) then found.[key] <- ResizeArray ()
                    if not (found.[key].Contains value) then found.[key].Add value
                let decl = memberDecl.Match line
                if decl.Success then add decl.Groups.[1].Value
                elif not isNamespace then
                    let value = valueDecl.Match line
                    if value.Success then add value.Groups.[1].Value
    [ for pair in found do
        let name, isNamespace = pair.Key
        yield { Full = name; IsNamespace = isNamespace; Members = List.ofSeq pair.Value; Where = [ path ] } ]

let private sourcesUnder (root: string) =
    fsharpFilesUnder nodeFs root
    |> Array.toList
    |> List.map (fun name -> sprintf "%s/%s" root name)
    |> List.filter (fun path -> generated |> List.forall (fun bad -> not (path.Contains bad)))

/// A namespace spans files, so what it offers is the union of what each file puts in it.
let private merge (surfaces: Surface list) =
    surfaces
    |> List.groupBy (fun s -> s.Full, s.IsNamespace)
    |> List.map (fun ((full, isNamespace), group) ->
        { Full = full
          IsNamespace = isNamespace
          Members = group |> List.collect (fun s -> s.Members) |> List.distinct
          Where = group |> List.collect (fun s -> s.Where) |> List.distinct })

let private scanned =
    lazy
        ([ "app"; "src"; "tests"; "examples" ]
         |> List.collect sourcesUnder
         |> List.collect (fun path -> surfacesIn path (readText nodeFs path))
         |> merge)

/// The pairs that break the rule, as one line each.
let private offendersAmong (surfaces: Surface list) =
    let namespaces = surfaces |> List.filter (fun s -> s.IsNamespace)
    let modules = surfaces |> List.filter (fun s -> not s.IsNamespace)
    [ for ns in namespaces do
        for md in modules do
            if shortOf ns.Full = shortOf md.Full && ns.Full <> md.Full then
                let shared = ns.Members |> List.filter (fun m -> List.contains m md.Members) |> List.sort
                if not (List.isEmpty shared) then
                    yield
                        sprintf
                            "  %s and %s are both `%s` to an opener, and both export %s"
                            ns.Full
                            md.Full
                            (shortOf ns.Full)
                            (String.concat ", " shared) ]

let private offenders () = offendersAmong scanned.Value

// --- The other way an opened scope wins silently ------------------------------------------
//
// The rule above is about a namespace beside a MODULE. This one is about two feature
// namespaces beside each other, and it is stricter for a reason: the domain is split so that
// a file opens the two or three slices it needs, and nothing makes it open them in a
// particular order. If `Yession.Domain.Terminals` and `Yession.Domain.Chat` both exported a
// `Projection`, a file opening both would get whichever was opened LAST, with no diagnostic —
// and the two are not interchangeable.
//
// So: no two domain namespaces may export the same name at all. Not "may share a name but not
// a member", which is the module rule — a namespace is opened for its CONTENTS, so the whole
// export list is what lands in front of the file.
//
// This is what makes the short names affordable. `TerminalProjection` and `AuthzSubject` were
// prefixed to clear a flat namespace of 267 types; inside a slice they disambiguate nothing,
// and they came off once each slice had a namespace. What stops `Projection` and `Subject`
// from being ambiguous is not luck, it is that nothing else in the domain is called either —
// and that is only true for as long as something checks.
let private domainNamespaces (surfaces: Surface list) =
    surfaces
    |> List.filter (fun s -> s.IsNamespace && s.Full.StartsWith "Yession.Domain.")

let private sharedExportsAmong (surfaces: Surface list) =
    domainNamespaces surfaces
    |> List.collect (fun s -> s.Members |> List.map (fun m -> m, s.Full))
    |> List.groupBy fst
    |> List.filter (fun (_, owners) -> (owners |> List.map snd |> List.distinct |> List.length) > 1)
    |> List.map (fun (name, owners) ->
        sprintf
            "  `%s` is exported by %s"
            name
            (owners |> List.map snd |> List.distinct |> List.sort |> String.concat " and "))

let private sharedExports () = sharedExportsAmong scanned.Value

// Assembled with escapes rather than written down the page, for the reason `RecordShapes`
// assembles its fixtures: spelled out as real lines, they would be real declarations in a
// real scanned file, and this suite would read its own fixtures back as product code.
let private namespaceFixture = "namespace Alpha.Beta.Widgets\ntype Gadget =\n    { Size : int }\nmodule Gadget =\n    let make () = 1"
let private moduleFixture = "module Other.Place.Widgets\nlet Gadget () = 2\ntype Unrelated = Unrelated"
let private disjointModuleFixture = "module Other.Place.Widgets\nlet nothingInCommon () = 2"

let private readOne (text: string) = surfacesIn "fixture.fs" text

let tests =
    testList "Namespace shadowing" [

        // Anti-vacuity: a reader that has stopped seeing declarations passes exactly like a
        // repository that obeys the rule.
        testCase "the reader can see scopes at all" <| fun () ->
            Expect.isTrue
                (List.length scanned.Value > 100)
                (sprintf
                    "found only %d namespaces and modules across app, src, tests and examples — matching none of them would mean the reader has stopped seeing scopes, not that the rule holds"
                    (List.length scanned.Value))

        testCase "the reader collects what a namespace puts in scope" <| fun () ->
            Expect.equal
                (readOne namespaceFixture |> List.map (fun s -> s.Members))
                [ [ "Gadget" ] ]
                "a namespace offers its types and modules, and `make` belongs to the module rather than to the namespace"

        testCase "the reader collects a module's values as well as its types" <| fun () ->
            Expect.equal
                (readOne moduleFixture |> List.collect (fun s -> s.Members) |> List.sort)
                [ "Gadget"; "Unrelated" ]
                "a module can be reached for a value, which is most of what the host modules are"

        testCase "a shared short name with a shared member is reported" <| fun () ->
            Expect.equal
                (List.length (offendersAmong (merge (readOne namespaceFixture @ readOne moduleFixture))))
                1
                "both are `Widgets` to an opener and both export `Gadget`, so `Widgets.Gadget` resolves to the namespace with nothing said"

        testCase "a shared short name with no shared member is allowed" <| fun () ->
            Expect.isEmpty
                (offendersAmong (merge (readOne namespaceFixture @ readOne disjointModuleFixture)))
                "sharing the name is not the fault; the reference falls through to the module and the build is clean"

        testCase "the reader sees every feature namespace of the domain" <| fun () ->
            // Anti-vacuity for the rule below, which passes trivially if the filter matches
            // nothing.
            Expect.isTrue
                (List.length (domainNamespaces scanned.Value) >= 8)
                (sprintf
                    "found only %d Yession.Domain.* namespaces — the rule below would be checking almost nothing"
                    (List.length (domainNamespaces scanned.Value)))

        testCase "two feature namespaces exporting one name is reported" <| fun () ->
            let a = surfacesIn "a.fs" "namespace Yession.Domain.Alpha\ntype Projection = { Rows : int }"
            let b = surfacesIn "b.fs" "namespace Yession.Domain.Beta\ntype Projection = { Items : int }"
            Expect.equal
                (List.length (sharedExportsAmong (merge (a @ b))))
                1
                "a file opening both would get whichever was opened last, and the two are not interchangeable"

        testCase "no two feature namespaces of the domain export the same name" <| fun () ->
            let found = sharedExports ()
            Expect.isEmpty
                found
                (sprintf
                    "a file opens the slices it needs, in no particular order, so a name exported twice resolves to whichever was opened last with nothing said:\n%s\nRename one of them, or put the shared concept in the kernel where there is one of it"
                    (String.concat "\n" found))

        testCase "no namespace shares a member with a module of the same short name" <| fun () ->
            let found = offenders ()
            Expect.isEmpty
                found
                (sprintf
                    "an `open` of the parent puts both of these in front of a file under one name, and a reference to the shared member silently resolves to the namespace:\n%s\nRename the member on one side, or give the namespace a short name nothing else uses"
                    (String.concat "\n" found))
    ]
