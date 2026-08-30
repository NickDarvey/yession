module Yession.Analyzers.DomainExports

open FSharp.Analyzers.SDK
open Yession.Analyzers.Population
open Yession.Analyzers.Surfaces

/// The other way an opened scope wins silently.
///
/// `NamespaceShadowing` is about a namespace beside a MODULE. This is about two feature
/// namespaces beside each other, and it is stricter for a reason: the domain is split so that a
/// file opens the two or three slices it needs, and nothing makes it open them in a particular
/// order. If `Yession.Domain.Terminals` and `Yession.Domain.Chat` both exported a `Projection`,
/// a file opening both would get whichever was opened LAST, with no diagnostic — and the two
/// are not interchangeable.
///
/// So: no two domain namespaces may export the same name at all. Not "may share a short name
/// but not a member", which is the module rule — those two are only ever in scope together by
/// somebody's `open` of a shared parent, and a namespace is opened for its CONTENTS, so the
/// whole export list is what lands in front of the file.
///
/// This is what makes the short names affordable. `TerminalProjection` and `AuthzSubject` were
/// prefixed to clear a flat namespace of 267 types; inside a slice they disambiguate nothing,
/// and they came off once each slice had a namespace. What stops `Projection` and `Subject`
/// from being ambiguous is not luck, it is that nothing else in the domain is called either —
/// and that is only true for as long as something checks.
///
/// The population is named rather than derived, and deliberately: `Yession.Domain.*` is the one
/// family of namespaces this repository expects a file to open several of at once. Nothing in
/// the assembly graph says that — it is a fact about how the domain is meant to be used, and a
/// rule that guessed it from the graph would either miss the slices or drag in every namespace
/// that has nothing to do with them.
///
/// It replaced the second half of a suite that read F# source with regular expressions, which
/// needed a case asserting it had found at least eight domain namespaces: a filter that has
/// stopped matching and a domain that obeys the rule are the same green run. A fixture that
/// says which of its own scopes must be reported answers that without a count to keep current.

[<Literal>]
let Code = "YES006"

/// The family a file is expected to open several of at once.
[<Literal>]
let private family = "Yession.Domain."

let private slices (surfaces: Surface list) =
    surfaces
    |> List.filter (fun s -> s.IsNamespace && s.Full.StartsWith family)

let private describe (name: string) (owners: string list) =
    let among = owners |> List.distinct |> List.sort |> String.concat " and "

    $"`%s{name}` is exported by %s{among}. A file opens the slices it needs, in no particular "
    + "order, so a name exported twice resolves to whichever was opened last with nothing said. "
    + "Rename one of them, or put the shared concept in the kernel where there is one of it."

let private offenders (declarations: Declaration list) =
    [ for (name, exported) in
          slices (surfaces declarations)
          |> List.collect (fun s -> s.Exports |> List.map (fun (name, where) -> name, (s, where)))
          |> List.groupBy fst do
          let owners =
              exported |> List.map (fun (_, (s, _)) -> s) |> List.distinctBy (fun s -> s.Full)

          if
              List.length owners > 1
              && mine (owners |> List.collect (fun s -> s.Declared))
          then
              let named = owners |> List.map (fun s -> s.Full)

              // Anchored at each declaration that carries the name: every one of them is a
              // place the ambiguity could be resolved, and a namespace has no line of its own.
              for (_, where) in List.map snd exported -> where, describe name named ]

[<CliAnalyzer("DomainExports", "No two feature namespaces of the domain export the same name", "")>]
let domainExports: Analyzer<CliContext> =
    fun ctx ->
        async {
            if not (Population.reportsHere ctx) then
                return []
            else
                return
                    [ for (where, message) in List.distinct (offenders (Population.of' ctx)) ->
                        { Type = "DomainExports"
                          Message = message
                          Code = Code
                          Severity = Severity.Error
                          Range = where
                          Fixes = [] } ]
        }
