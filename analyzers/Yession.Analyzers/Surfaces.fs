module Yession.Analyzers.Surfaces

open FSharp.Compiler.Symbols
open FSharp.Compiler.Text
open Yession.Analyzers.Population

/// What an `open` puts in front of a file, for the two rules that care.
///
/// They ask different questions of it — one about a namespace beside a MODULE of the same short
/// name, one about two feature namespaces beside each other — and both break the same way if
/// this is read wrong, so it is read once.

/// One scope, and what an `open` of its parent puts in front of a file.
type Surface =
    { Full: string
      Short: string
      IsNamespace: bool
      Exports: (string * range) list
      Declared: Declaration list }

let shortOf (full: string) =
    let cut = full.LastIndexOf '.'
    if cut < 0 then full else full.Substring (cut + 1)

let enclosing (full: string) =
    let cut = full.LastIndexOf '.'
    if cut < 0 then None else Some (full.Substring (0, cut))

let located (name: string) (at: unit -> range) =
    try Some (name, at ()) with _ -> None

/// What is reachable as `Short.member` from a file that has a MODULE in front of it: its types
/// and nested modules, plus its values.
let moduleExports (e: FSharpEntity) =
    seq {
        for child in e.NestedEntities do
            if child.Accessibility.IsPublic then
                match located child.DisplayName (fun () -> child.DeclarationLocation) with
                | Some export -> yield export
                | None -> ()

        for value in e.MembersFunctionsAndValues do
            if value.Accessibility.IsPublic && not value.IsCompilerGenerated then
                match located value.DisplayName (fun () -> value.DeclarationLocation) with
                | Some export -> yield export
                | None -> ()
    }

/// A namespace is not an entity worth reading. FCS represents the one a file declares as a
/// chain of one entity per SEGMENT, each carrying that segment as its name and holding no
/// children — `namespace Alpha.Beta.Widgets` arrives as `Alpha`, `Beta`, `Widgets`, and the
/// types the file declares in it arrive beside them rather than inside. Nothing in that answers
/// "what does `Alpha.Beta.Widgets` export".
///
/// The members do. A declaration whose parent is a namespace is a member of the namespace its
/// own full name names, and reading it from that direction is also the only thing that works
/// uniformly: a namespace in a REFERENCED assembly does hold its children, so a rule reading
/// entities would have had two shapes to cope with and would have been silently right about
/// one of them.
let namespaceMembership (declaration: Declaration) =
    try
        let e = declaration.Entity

        let inNamespace =
            match e.DeclaringEntity with
            | Some parent -> parent.IsNamespace
            | None -> false

        if not inNamespace then
            None
        else
            match enclosing e.FullName, located e.DisplayName (fun () -> e.DeclarationLocation) with
            | Some owner, Some export -> Some (owner, export, declaration)
            | _ -> None
    with _ ->
        None

let moduleSurface (declaration: Declaration) =
    try
        let e = declaration.Entity

        if not e.IsFSharpModule then
            None
        else
            Some
                { Full = e.FullName
                  Short = e.DisplayName
                  IsNamespace = false
                  Exports = List.ofSeq (moduleExports e)
                  Declared = [ declaration ] }
    with _ ->
        None

/// Only publicly reachable scopes take part. The hazard is what an `open` of a shared parent
/// puts in front of an arbitrary file, and a scope nothing outside can name is not something an
/// arbitrary file can open.
let reachable (declaration: Declaration) = declaration.Scope = Everywhere

/// Every scope a file could have in front of it, namespaces and modules alike. A namespace
/// spans files and assemblies, so what it offers is the union of what each puts in it.
let surfaces (declarations: Declaration list) =
    let open' = declarations |> List.filter reachable

    let namespaces =
        open'
        |> List.choose namespaceMembership
        |> List.groupBy (fun (owner, _, _) -> owner)
        |> List.map (fun (owner, group) ->
            { Full = owner
              Short = shortOf owner
              IsNamespace = true
              Exports = [ for (_, export, _) in group -> export ] |> List.distinct
              Declared = [ for (_, _, declaration) in group -> declaration ] })

    namespaces @ (open' |> List.choose moduleSurface)

