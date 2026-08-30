module Yession.Analyzers.RecordShapes

open System.Collections.Concurrent
open System.IO
open FSharp.Analyzers.SDK
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Symbols
open FSharp.Compiler.Text

/// Which record type does a bare `{ … = …; … = … }` build, when two of them carry the same
/// field names?
///
/// The one declared LAST, and silently: there is no diagnostic for this at any warning level,
/// so a record added today reaches back and re-points every construction already written
/// against the other one, in files its author never opened. It happened here. `SecretId` is
/// scope-and-name; a second wire type was given those same two field names, and constructions
/// in `SecretsState.fs` quietly became the wrong type. The repair was to qualify them one site
/// at a time, after the compiler had already accepted the wrong reading everywhere it could
/// still be inferred.
///
/// `RequireQualifiedAccess` on a record takes its labels out of unqualified scope altogether.
/// The type can no longer capture anybody else's construction, and its own sites name it or
/// fail to compile — the error even names the type it wants. So the rule is: a field set
/// carried by two or more record types is allowed only when EVERY type in that group has the
/// attribute. One left bare is the same ambiguity with fewer candidates, and it is the bare one
/// that is reported, at its own declaration, because it is the one that can still capture.
///
/// It is deliberately blind to whether the two are ever in scope together. That is not a
/// property of either declaration — it belongs to some third file's `open` list, which changes
/// without either author present, which is exactly how the original one landed. What it is NOT
/// blind to is whether they COULD be: see `meet`.
///
/// The rule is about CONSTRUCTION, and an exact field set is the right unit for it: a record
/// expression must give every field, so only a type with precisely these labels can be built
/// from them. A record PATTERN may name a subset, which would make any shared label a hazard —
/// but only where the matched type is unknown, and the one form that leaves it unknown
/// (destructuring in a parameter position, `let f { Reason = r } = …`) appears nowhere in this
/// repository. If it ever does, this rule is not the one that catches it.
///
/// It also fires on a group whose field TYPES differ enough that a capture could not have
/// type-checked. Those cost one qualified construction each, and the alternative is a rule that
/// reasons about assignability and is wrong in ways nobody can see.
///
/// This was a test suite that read F# SOURCE with regular expressions: a pattern for the
/// declaration line, a brace-balancer for the body, an indent scan for where the body ended,
/// and a walk back up over doc comments looking for the attribute. Six of its eight cases
/// tested that READER rather than the rule, its fixtures were assembled out of escaped string
/// literals because a fixture written down the page would have been a real declaration in a
/// real scanned file, and it needed a case asserting it had found at least 250 records, since a
/// reader that has stopped seeing declarations and a repository that obeys the rule are the
/// same green run. Every one of those is a property of reading text. The compiler has already
/// parsed this.
///
/// Two holes came free with the text, and the tree closes both:
///
///   * Its field-label pattern required an initial capital, so a record with a lowercase label
///     was invisible and the rule silently did not apply to it. `tests/Yession.Tests` has five
///     `{ status; body }` records it never saw.
///   * It had no model of accessibility — it matched `private` on the declaration line and
///     discarded it — so it counted records against each other that no scope can hold at once.

[<Literal>]
let Code = "YES004"

/// Where a type's labels can be brought into scope. Not where it is USED: where a file could
/// `open` its way to it, which is the question this rule asks and the one accessibility
/// answers.
type private Scope =
    | Everywhere
    | InAssembly
    /// The full name of the scope a `private` declaration is sealed inside.
    | InModule of string

/// One record type, as the compiler has it.
type private Shape =
    { Owner: string
      Fields: string list
      Guarded: bool
      Where: range
      Assembly: string
      Scope: Scope
      Own: bool }

/// The narrowest scope enclosing a declaration, its own accessibility and its ancestors' both.
/// A public type inside a private module is reachable only from that module's parent, and a
/// rule reading the type alone would call it public.
let rec private scopeOf (e: FSharpEntity) =
    let outer =
        match e.DeclaringEntity with
        | Some parent -> scopeOf parent
        | None -> Everywhere

    let here =
        if e.Accessibility.IsPrivate then
            match e.DeclaringEntity with
            | Some parent -> InModule parent.FullName
            | None -> InAssembly
        elif e.Accessibility.IsInternal then
            InAssembly
        else
            Everywhere

    match here, outer with
    | InModule m, _ -> InModule m
    | _, InModule m -> InModule m
    | InAssembly, _
    | _, InAssembly -> InAssembly
    | Everywhere, Everywhere -> Everywhere

/// Whether two declarations can have their labels in one scope at once, which is what it takes
/// for a bare construction to be ambiguous between them.
///
/// This is the half F# metadata will not do for you by omission. A referenced assembly's
/// `Contents` hands over its non-public entities with the rest — `SerialProvider.Mcp`'s
/// `private Request` arrives in `Yession.Tests`'s population as readily as anything public — so
/// a rule that reads absence as inaccessibility reports a collision with `Yession.Domain`'s
/// `JsonRpcRequest` that no file can write. Two records sealed in DIFFERENT modules are the
/// same story one assembly down, and that one is live: the suite declares `{ status; body }`
/// privately in five modules, none of which can see another's.
///
/// Both fall out of one question — is there a scope that holds both label sets — asked of the
/// pair rather than of either declaration.
let private meet (a: Shape) (b: Shape) =
    match a.Scope, b.Scope with
    // Two public types meet wherever both assemblies are referenced, which is here.
    | Everywhere, Everywhere -> true
    // Sealed inside a module: reachable from that module and what it encloses, and from
    // nowhere in another assembly at all.
    | InModule m, InModule n ->
        a.Assembly = b.Assembly && (m = n || m.StartsWith (n + ".") || n.StartsWith (m + "."))
    // Anything else has one side narrowed to an assembly or a module, so both have to be in it.
    | _ -> a.Assembly = b.Assembly

let private guarded (e: FSharpEntity) =
    e.Attributes
    |> Seq.exists (fun a ->
        a.AttributeType.TryFullName = Some "Microsoft.FSharp.Core.RequireQualifiedAccessAttribute")

/// The shape of an entity, when it is a record at all. Everything here can throw for a symbol
/// FCS declines to describe — a full name it cannot spell, a declaration location it does not
/// have — and a rule that cannot read one type must still read the rest.
let private shapeOf (assembly: string) (own: bool) (e: FSharpEntity) =
    try
        if not e.IsFSharpRecord then
            None
        else
            match [ for f in e.FSharpFields -> f.Name ] with
            | [] -> None
            | fields ->
                Some
                    { Owner = e.FullName
                      Fields = List.sort fields
                      Guarded = guarded e
                      Where = e.DeclarationLocation
                      Assembly = assembly
                      Scope = scopeOf e
                      Own = own }
    with _ ->
        None

let rec private nested (es: seq<FSharpEntity>) =
    seq {
        for e in es do
            yield e
            yield! nested e.NestedEntities
    }

let rec private declared (ds: FSharpImplementationFileDeclaration list) =
    seq {
        for d in ds do
            match d with
            | FSharpImplementationFileDeclaration.Entity (e, inner) ->
                yield e
                yield! declared inner
            | _ -> ()
    }

/// The repository, found from the project being analyzed by walking up to the solution file.
/// It bounds the population to code somebody here can change: the rule's remedy is that EVERY
/// type in a group carries the attribute, which is unavailable against a package, and a
/// collision between two of THEM — Thoth.Json and Thoth.Json.Net declare `ExtraCoders` twice —
/// is nobody here's to answer for. The source scan this replaced drew the same line by keeping
/// a list of directories; this reads it off the tree it is already analyzing.
let rec private repositoryOf (dir: string) =
    if isNull dir then None
    elif File.Exists (Path.Combine (dir, "Yession.slnx")) then Some dir
    else repositoryOf (Path.GetDirectoryName dir)

let private inside (root: string) (path: string) =
    try
        (Path.GetFullPath path).StartsWith (Path.GetFullPath root + string Path.DirectorySeparatorChar)
    with _ ->
        false

/// Every record one project could construct, of the code this repository builds: its own
/// declarations and everything it references. Both go in whole — `meet` is what decides which
/// pairs are hazards, rather than the population pretending the unreachable ones are not there.
let private populationOf (name: string) (root: string) (results: FSharpCheckProjectResults) =
    let own =
        seq {
            for file in results.AssemblyContents.ImplementationFiles do
                yield! declared file.Declarations
        }
        |> nested
        |> Seq.choose (shapeOf name true)

    let referenced =
        seq {
            for assembly in results.ProjectContext.GetReferencedAssemblies () do
                match (try Some (assembly.SimpleName, assembly.Contents.Entities) with _ -> None) with
                | Some (from, es) -> yield! nested es |> Seq.choose (shapeOf from false)
                | None -> ()
        }

    Seq.append own referenced
    |> Seq.filter (fun s -> inside root s.Where.FileName)
    // One entity reaches the walk by more than one route — a nested module is a member of its
    // parent as well as an entity in its own right.
    |> Seq.distinctBy (fun s -> s.Owner, s.Where)
    |> List.ofSeq

/// A whole-population verdict is the same for every file in a project, and the walk that
/// produces it reads every referenced assembly. Once per project, not once per file.
let private populations = ConcurrentDictionary<string, Shape list> ()

let private describe (fields: string list) (partners: Shape list) (culprit: Shape) =
    let others =
        partners
        |> List.map (fun s -> if s.Guarded then s.Owner else s.Owner + " (also unqualified)")
        |> List.sort
        |> String.concat ", "

    let labels = String.concat "; " fields

    $"`%s{culprit.Owner}` carries {{ %s{labels} }}, and so does %s{others}. "
    + "A bare construction of these labels builds whichever type was declared last, with no "
    + "diagnostic anywhere. Give every type in the group [<RequireQualifiedAccess>] and name "
    + "the type at its construction sites."

/// Whether this project is the one that answers for a pair. Every project sees what it
/// references, so a pair living entirely inside ONE other assembly is visible from every
/// project downstream of it and would be reported by each — while the project that declares it
/// is already reporting it, anchored at the same lines. That pair is somebody else's.
///
/// What is left is exactly the two cases with nowhere else to go: a pair this project declares
/// part of, and a pair spanning assemblies that need not reference each other at all, whose
/// members only ever meet in a project like this one — `Yession.Domain`'s MCP tool beside the
/// serial example's, which no run of either project can see.
let private mine (shapes: Shape list) =
    shapes |> List.exists (fun s -> s.Own)
    || (shapes |> List.map (fun s -> s.Assembly) |> List.distinct |> List.length) > 1

let private offenders (shapes: Shape list) =
    shapes
    |> List.groupBy (fun s -> s.Fields)
    |> List.collect (fun (fields, group) ->
        [ for culprit in group do
              if not culprit.Guarded then
                  let partners =
                      group
                      |> List.filter (fun other -> other.Where <> culprit.Where && meet culprit other)

                  if not (List.isEmpty partners) && mine (culprit :: partners) then
                      yield culprit.Where, describe fields partners culprit ])

/// Where the project's one verdict is reported: its last hand-written source file. Generated
/// files (an `AssemblyInfo` the SDK writes into `obj`) are not somewhere a person will look,
/// and which of them compiles last is not this rule's business.
let private authored files =
    files
    |> Seq.filter (fun (f: string) -> not ((f.Replace ('\\', '/')).Contains "/obj/"))
    |> List.ofSeq

let private reportsHere (ctx: CliContext) =
    match authored ctx.ProjectOptions.SourceFiles with
    | [] -> false
    | own -> Path.GetFullPath (List.last own) = Path.GetFullPath ctx.FileName

[<CliAnalyzer("RecordShapes", "Record types that share a field set all require qualified access", "")>]
let recordShapes: Analyzer<CliContext> =
    fun ctx ->
        async {
            match reportsHere ctx, repositoryOf (Path.GetDirectoryName ctx.ProjectOptions.ProjectFileName) with
            | false, _
            | _, None -> return []
            | true, Some root ->
                let shapes =
                    populations.GetOrAdd (
                        ctx.ProjectOptions.ProjectFileName,
                        fun project ->
                            populationOf (Path.GetFileNameWithoutExtension project) root ctx.CheckProjectResults
                    )

                return
                    [ for (where, message) in offenders shapes ->
                        { Type = "RecordShapes"
                          Message = message
                          Code = Code
                          Severity = Severity.Error
                          Range = where
                          Fixes = [] } ]
        }
