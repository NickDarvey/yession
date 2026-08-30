module Yession.Analyzers.Population

open System.Collections.Concurrent
open System.IO
open FSharp.Analyzers.SDK
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Symbols
open FSharp.Compiler.Text

/// What a whole-population rule reads: every declaration one project could name, of the code
/// this repository builds.
///
/// Two rules want this and a third is likely, so it is one module rather than a copy each. The
/// walk is not cheap — it reads every referenced assembly — and the answer is the same for
/// every file in a project, so it is done once per project and kept.

/// Where a declaration's name can be brought into scope. Not where it is USED: where a file
/// could `open` its way to it, which is the question these rules ask and the one accessibility
/// answers.
type Scope =
    | Everywhere
    | InAssembly
    /// The full name of the scope a `private` declaration is sealed inside.
    | InModule of string

type Declaration =
    { Entity: FSharpEntity
      Owner: string
      Assembly: string
      /// Declared by the project under analysis, rather than reached through a reference.
      Own: bool
      Scope: Scope
      Where: range }

/// The narrowest scope enclosing a declaration, its own accessibility and its ancestors' both.
/// A public type inside a private module is reachable only from that module's parent, and a
/// rule reading the type alone would call it public.
let rec scopeOf (e: FSharpEntity) =
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

/// Whether two declarations can have their names in one scope at once, which is what it takes
/// for a reference to be ambiguous between them.
///
/// This is the half F# metadata will not do for you by omission. A referenced assembly's
/// `Contents` hands over its non-public entities with the rest — `SerialProvider.Mcp`'s
/// `private Request` arrives in `Yession.Tests`'s population as readily as anything public — so
/// a rule that reads absence as inaccessibility reports a collision no file can write. Two
/// declarations sealed in DIFFERENT modules are the same story one assembly down, and that one
/// is live: the suite declares `{ status; body }` privately in five modules, none of which can
/// see another's.
let meet (a: Declaration) (b: Declaration) =
    match a.Scope, b.Scope with
    // Two public declarations meet wherever both assemblies are referenced, which is here.
    | Everywhere, Everywhere -> true
    // Sealed inside a module: reachable from that module and what it encloses, and from
    // nowhere in another assembly at all.
    | InModule m, InModule n ->
        a.Assembly = b.Assembly && (m = n || m.StartsWith (n + ".") || n.StartsWith (m + "."))
    // Anything else has one side narrowed to an assembly or a module, so both have to be in it.
    | _ -> a.Assembly = b.Assembly

/// Whether this project is the one that answers for a set of declarations. Every project sees
/// what it references, so a set living entirely inside ONE other assembly is visible from every
/// project downstream of it and would be reported by each — while the project that declares it
/// is already reporting it, anchored at the same lines. That set is somebody else's.
///
/// What is left is exactly the two cases with nowhere else to go: a set this project declares
/// part of, and a set spanning assemblies that need not reference each other at all, whose
/// members only ever meet in a project like this one — `Yession.Domain`'s MCP tool beside the
/// serial example's, which no run of either project can see.
let mine (declarations: Declaration list) =
    declarations |> List.exists (fun d -> d.Own)
    || (declarations |> List.map (fun d -> d.Assembly) |> List.distinct |> List.length) > 1

let rec private nested (es: seq<FSharpEntity>) =
    seq {
        for e in es do
            yield e
            yield! nested e.NestedEntities
    }

let rec private declaredIn (ds: FSharpImplementationFileDeclaration list) =
    seq {
        for d in ds do
            match d with
            | FSharpImplementationFileDeclaration.Entity (e, inner) ->
                yield e
                yield! declaredIn inner
            | _ -> ()
    }

/// The repository, found from the project being analyzed by walking up to the solution file.
/// It bounds the population to code somebody here can change: these rules' remedies are edits
/// to a declaration, which is not available against a package, and a collision between two of
/// THEM — Thoth.Json and Thoth.Json.Net declare `ExtraCoders` twice — is nobody here's to
/// answer for. The source scans this replaced drew the same line by keeping lists of
/// directories; this reads it off the tree it is already analyzing.
let rec private repositoryOf (dir: string) =
    if isNull dir then None
    elif File.Exists (Path.Combine (dir, "Yession.slnx")) then Some dir
    else repositoryOf (Path.GetDirectoryName dir)

let private inside (root: string) (path: string) =
    try
        (Path.GetFullPath path).StartsWith (Path.GetFullPath root + string Path.DirectorySeparatorChar)
    with _ ->
        false

/// Everything here can throw for a symbol FCS declines to describe — a full name it cannot
/// spell, a declaration location it does not have — and a rule that cannot read one entity must
/// still read the rest.
let private describe (assembly: string) (own: bool) (e: FSharpEntity) =
    try
        Some
            { Entity = e
              Owner = e.FullName
              Assembly = assembly
              Own = own
              Scope = scopeOf e
              Where = e.DeclarationLocation }
    with _ ->
        None

let private walk (name: string) (root: string) (results: FSharpCheckProjectResults) =
    let own =
        seq {
            for file in results.AssemblyContents.ImplementationFiles do
                yield! declaredIn file.Declarations
        }
        |> nested
        |> Seq.choose (describe name true)

    let referenced =
        seq {
            for assembly in results.ProjectContext.GetReferencedAssemblies () do
                match (try Some (assembly.SimpleName, assembly.Contents.Entities) with _ -> None) with
                | Some (from, es) -> yield! nested es |> Seq.choose (describe from false)
                | None -> ()
        }

    Seq.append own referenced
    |> Seq.filter (fun d -> inside root d.Where.FileName)
    // One entity reaches the walk by more than one route — a nested module is a member of its
    // parent as well as an entity in its own right.
    |> Seq.distinctBy (fun d -> d.Owner, d.Where)
    |> List.ofSeq

let private cache = ConcurrentDictionary<string, Declaration list> ()

let of' (ctx: CliContext) =
    let project = ctx.ProjectOptions.ProjectFileName

    match repositoryOf (Path.GetDirectoryName project) with
    | None -> []
    | Some root ->
        cache.GetOrAdd (project, fun _ -> walk (Path.GetFileNameWithoutExtension project) root ctx.CheckProjectResults)

/// Where a project's one verdict is reported: its last hand-written source file. A
/// whole-population answer is the same for every file in the project, so emitting it from each
/// would say it as many times as the project has files. Generated files (an `AssemblyInfo` the
/// SDK writes into `obj`) are not somewhere a person will look, and which of them compiles last
/// is not these rules' business.
let reportsHere (ctx: CliContext) =
    match
        ctx.ProjectOptions.SourceFiles
        |> Seq.filter (fun (f: string) -> not ((f.Replace ('\\', '/')).Contains "/obj/"))
        |> List.ofSeq
    with
    | [] -> false
    | own -> Path.GetFullPath (List.last own) = Path.GetFullPath ctx.FileName
