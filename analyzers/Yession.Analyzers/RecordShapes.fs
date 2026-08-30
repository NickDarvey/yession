module Yession.Analyzers.RecordShapes

open FSharp.Analyzers.SDK
open FSharp.Compiler.Symbols
open Yession.Analyzers.Population

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
///
/// Both of those, and the population the rule reads, live in `Population.fs`, which the
/// namespace rules read too.

[<Literal>]
let Code = "YES004"

/// One record type, as the compiler has it.
type private Shape =
    { Declared: Declaration
      Fields: string list
      Guarded: bool }

let private guarded (e: FSharpEntity) =
    e.Attributes
    |> Seq.exists (fun a ->
        a.AttributeType.TryFullName = Some "Microsoft.FSharp.Core.RequireQualifiedAccessAttribute")

let private shapeOf (declaration: Declaration) =
    try
        if not declaration.Entity.IsFSharpRecord then
            None
        else
            match [ for f in declaration.Entity.FSharpFields -> f.Name ] with
            | [] -> None
            | fields ->
                Some
                    { Declared = declaration
                      Fields = List.sort fields
                      Guarded = guarded declaration.Entity }
    with _ ->
        None

let private describe (fields: string list) (partners: Shape list) (culprit: Shape) =
    let others =
        partners
        |> List.map (fun s -> if s.Guarded then s.Declared.Owner else s.Declared.Owner + " (also unqualified)")
        |> List.sort
        |> String.concat ", "

    let labels = String.concat "; " fields

    $"`%s{culprit.Declared.Owner}` carries {{ %s{labels} }}, and so does %s{others}. "
    + "A bare construction of these labels builds whichever type was declared last, with no "
    + "diagnostic anywhere. Give every type in the group [<RequireQualifiedAccess>] and name "
    + "the type at its construction sites."

let private offenders (shapes: Shape list) =
    shapes
    |> List.groupBy (fun s -> s.Fields)
    |> List.collect (fun (fields, group) ->
        [ for culprit in group do
              if not culprit.Guarded then
                  let partners =
                      group
                      |> List.filter (fun other ->
                          other.Declared.Where <> culprit.Declared.Where
                          && meet culprit.Declared other.Declared)

                  if not (List.isEmpty partners) then
                      let involved = [ for s in culprit :: partners -> s.Declared ]

                      if mine involved then
                          yield culprit.Declared.Where, describe fields partners culprit ])

[<CliAnalyzer("RecordShapes", "Record types that share a field set all require qualified access", "")>]
let recordShapes: Analyzer<CliContext> =
    fun ctx ->
        async {
            if not (Population.reportsHere ctx) then
                return []
            else
                let shapes = Population.of' ctx |> List.choose shapeOf

                return
                    [ for (where, message) in offenders shapes ->
                        { Type = "RecordShapes"
                          Message = message
                          Code = Code
                          Severity = Severity.Error
                          Range = where
                          Fixes = [] } ]
        }
