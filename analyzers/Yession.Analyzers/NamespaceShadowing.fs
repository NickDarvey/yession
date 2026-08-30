module Yession.Analyzers.NamespaceShadowing

open FSharp.Compiler.Symbols
open FSharp.Compiler.Text
open FSharp.Analyzers.SDK
open Yession.Analyzers.Population

/// `open Yession.Domain` brings every SUB-NAMESPACE of it into scope by short name. So the
/// moment the domain is split into feature namespaces, `Yession.Domain.Terminals` puts the name
/// `Terminals` in front of every file that opens the domain — including files that already
/// meant something else by it.
///
/// What happens then is the record-shape fault again, on a different axis. Given a file whose
/// enclosing scope offers a module `Terminals`, and an `open` that offers a namespace
/// `Terminals`, a reference to `Terminals.foo` resolves to the NAMESPACE when the namespace has
/// a `foo`, and falls through to the module when it does not. Neither outcome is reported.
/// Verified against the compiler rather than assumed: with a member of the same name on both
/// sides, the opened namespace wins silently; with no shared member, the module resolves and
/// the build is clean.
///
/// So the fall-through is what makes the natural names usable — `Yession.Domain.Terminals`
/// alongside `Yession.Host.Terminals` is fine while nothing is named twice — and it is also
/// what makes the hazard invisible. Nothing goes wrong until somebody adds a member to the
/// domain side that the module already has, in a file that has never heard of either. That is
/// the same shape as the `{ Scope; Name }` capture: a declaration reaching back to re-point
/// call sites its author never opened.
///
/// The rule is therefore about MEMBERS, not names. Two things may share a short name; they may
/// not also share a member. Sharing the name alone is not the fault, which matters because the
/// codebase already does it four times over (`Yession.Domain` beside the suite's
/// `Yession.Tests.Domain`, `Yession.Manager` beside `Yession.Host.Manager`) and those are
/// correct — they are simply disjoint, and this pins them there.
///
/// A namespace's members are its types and its nested namespaces and modules: a namespace
/// cannot hold a `let`, so nothing else is reachable as `Short.member`. A module's are those
/// plus its values.
///
/// It is deliberately blind to whether the two are ever in scope together. That is not a
/// property of either declaration — it belongs to some third file's `open` list, which changes
/// without either author present. What it is not blind to is whether they COULD be: only
/// publicly reachable scopes take part, because the hazard is what an `open` of a shared parent
/// puts in front of an arbitrary file, and a scope nothing outside can name is not something an
/// arbitrary file can open.
///
/// This was a test suite that read F# source with regular expressions — one pattern for the
/// declaration that opens a scope, one for a type or module inside it, one for a value binding
/// — over a hand-kept list of directories. Its fixtures were assembled out of escaped string
/// literals, because a scope written down the page would have been a real scope in a real
/// scanned file, and it needed two anti-vacuity cases (more than 100 scopes found, at least
/// eight domain namespaces) because a reader that has stopped seeing declarations and a
/// repository that obeys the rule are the same green run. What the text could not recover at
/// all is a member this assembly did not declare: the population was files, so a namespace
/// extended by a referenced assembly was short by exactly that member. `Population.fs` reads
/// the assembly graph, so a namespace is what the compiler says it is.

[<Literal>]
let Code = "YES005"

/// One scope, and what an `open` of its parent puts in front of a file.
type private Surface =
    { Full: string
      Short: string
      IsNamespace: bool
      Exports: (string * range) list
      Declared: Declaration list }

let private shortOf (full: string) =
    let cut = full.LastIndexOf '.'
    if cut < 0 then full else full.Substring (cut + 1)

let private enclosing (full: string) =
    let cut = full.LastIndexOf '.'
    if cut < 0 then None else Some (full.Substring (0, cut))

let private located (name: string) (at: unit -> range) =
    try Some (name, at ()) with _ -> None

/// What is reachable as `Short.member` from a file that has a MODULE in front of it: its types
/// and nested modules, plus its values.
let private moduleExports (e: FSharpEntity) =
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
let private namespaceMembership (declaration: Declaration) =
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

let private moduleSurface (declaration: Declaration) =
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
let private reachable (declaration: Declaration) = declaration.Scope = Everywhere

/// A namespace spans files and assemblies, so what it offers is the union of what each puts in
/// it.
let private surfaces (declarations: Declaration list) =
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

    let modules = open' |> List.choose moduleSurface

    namespaces, modules

let private describe (namespace': Surface) (module': Surface) (shared: string list) =
    let names = shared |> List.sort |> String.concat ", "

    $"`%s{namespace'.Full}` and `%s{module'.Full}` are both `%s{namespace'.Short}` to a file "
    + $"that opens their parent, and both export %s{names}. A reference to the shared name "
    + "resolves to the namespace, with nothing said — and would fall through to the module the "
    + "day the namespace stopped exporting it. Rename the member on one side, or give the "
    + "namespace a short name nothing else uses."

let private offenders (declarations: Declaration list) =
    let namespaces, modules = surfaces declarations
    // Indexed by the short name, because only scopes that answer to one name can shadow and the
    // product has hundreds of each.
    let byShort = modules |> List.groupBy (fun s -> s.Short) |> Map.ofList

    [ for namespace' in namespaces do
          match Map.tryFind namespace'.Short byShort with
          | None -> ()
          | Some candidates ->
              for module' in candidates do
                  if namespace'.Full <> module'.Full then
                      let offered = module'.Exports |> List.map fst |> Set.ofList
                      let wanted = namespace'.Exports |> List.map fst |> Set.ofList
                      let names = Set.intersect offered wanted

                      if
                          not (Set.isEmpty names)
                          && mine (namespace'.Declared @ module'.Declared)
                      then
                          let message = describe namespace' module' (List.ofSeq names)

                          // Anchored at each shared member rather than once at a scope: a
                          // namespace has no single declaration to point at, and the member is
                          // the thing that has to be renamed.
                          for (name, where) in namespace'.Exports @ module'.Exports do
                              if Set.contains name names then
                                  yield where, message ]

[<CliAnalyzer("NamespaceShadowing",
              "A namespace and a module of the same short name share no member",
              "")>]
let namespaceShadowing: Analyzer<CliContext> =
    fun ctx ->
        async {
            if not (Population.reportsHere ctx) then
                return []
            else
                return
                    [ for (where, message) in List.distinct (offenders (Population.of' ctx)) ->
                        { Type = "NamespaceShadowing"
                          Message = message
                          Code = Code
                          Severity = Severity.Error
                          Range = where
                          Fixes = [] } ]
        }
