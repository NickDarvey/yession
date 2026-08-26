module Yession.Host.OperatorResources

// The operator's vocabulary, read off disk and shown back to them.
//
// Two halves of one story, deliberately in one file: the bridge that turns a file into a
// `ResourceProfile`, and the query that says what that profile MEANS. A profile is the first
// thing in this system an operator authors that has a non-obvious consequence — a name can
// reach other names, and what it finally grants is a closure rather than what is written
// beside it — so being able to read the answer back is not a nicety.
//
// Nothing consumes the profile yet. That is the point of it landing this way round: an
// operator can write one, see its closures, and find the cycle or the contradiction, before
// anything at all depends on what it says.
//
// The bridge is the thinnest thing that can work, for the reason `RepoConfig` gives: the
// schema lives in `Yession.Domain.Sandboxes.OperatorProfile`, decodes an already-parsed tree,
// and runs in the cheap tier on both runtimes. This part reads a file and calls a JS parser,
// and it is kept small enough that its own failure modes are all that is left to get wrong.

open Fable.Core
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Tools
open Yession.SessionProcess

[<Import("parseDocument", "yaml")>]
let private parseDocument (text: string) (options: obj) : obj = jsNative

[<Emit("(function (doc) { return doc.errors.concat(doc.warnings).map(p => p.message) })($0)")>]
let private complaints (doc: obj) : string array = jsNative

[<Emit("JSON.stringify($0.toJS())")>]
let private toJson (doc: obj) : string = jsNative

/// The same parser construction `RepoConfig` uses, and every field is load-bearing there for
/// the same reasons: `core` resolves only what JSON could express, `uniqueKeys` makes a
/// repeated key an error rather than a silent last-wins fold — which is what makes the
/// domain's "declared twice" refusal reachable from a real file — and `maxAliasCount` stops
/// a self-referential anchor turning a small file into an unbounded tree.
[<Emit("{ schema: 'core', uniqueKeys: true, maxAliasCount: 100 }")>]
let private parseOptions : obj = jsNative

/// Read and decode the operator's profile.
///
/// Three outcomes, and the middle one is why this is not a `Result` of two: a deployment
/// with NO profile is ordinary and declares nothing, while a profile that cannot be read is
/// an operator's mistake and must be said out loud. Folding the second into the first is how
/// a host silently stops offering everything the day somebody mistypes a key.
let read (path: string) : Result<ProfileFile option, string> =
    if not (Fs.exists path) then Ok None
    else
        let saying (reason: string) = sprintf "%s: %s" path reason
        try
            let doc = parseDocument (Fs.readText path) parseOptions
            match complaints doc with
            | [||] -> OperatorProfile.parse (toJson doc) |> Result.map Some |> Result.mapError saying
            | problems -> Error (saying problems.[0])
        with e -> Error (saying e.Message)

let queryName : QueryName =
    match QueryName.create "resources" with
    | Ok name -> name
    | Error e -> failwithf "resources query name: %s" e

let private queryDef : QueryDef =
    { Name = queryName
      Title = "resources"
      Description =
        "What this host offers a sandbox, as the operator declared it. Each row is a name a \
         repo may select, and what selecting it finally grants — which is the whole closure, \
         not the line written beside the name. A resource is sensitive when something it \
         reaches is, however deeply."
      Shape =
        Rows
            [ QueryColumn.create "resource" "resource"
              QueryColumn.create "grants" "grants"
              QueryColumn.create "sensitive" "sensitive"
              QueryColumn.create "default" "granted to every sandbox" ] }

/// The declared names and what each one comes to.
///
/// Every name, not only the composites: a leaf's closure is itself, and an operator scanning
/// for "what does this host allow at all" should not have to know which of their own names
/// are which. `ResourceClosure.describe` is the same rendering an approval prompt will use,
/// so what is shown here and what is shown there cannot drift into two answers.
let rows (file: ProfileFile) : (string * QueryCell) list list =
    let profile = file.Resources
    ResourceProfile.declared profile
    |> Set.toList
    |> List.map (fun name ->
        let described =
            match ResourceProfile.resolve profile [ name ] with
            | Ok closure ->
                (ResourceClosure.describe closure |> String.concat "; "),
                (if ResourceClosure.isSensitive closure then CellText "yes" else CellAbsent)
            // Unreachable for a loaded profile — `load` refused everything that could fail
            // here. Said rather than swallowed, because a row that quietly showed nothing
            // would read as a resource that grants nothing.
            | Error reason -> reason, CellAbsent
        [ "resource", CellText (ResourceName.value name)
          "grants", CellText (fst described)
          "sensitive", snd described
          // Whether every sandbox on this host gets it without asking. Declared and not
          // granted is a real state, and one an operator cannot see any other way.
          "default", (if List.contains name file.Default then CellText "yes" else CellAbsent) ])

/// Register it. Takes a thunk rather than a value for the reason the other registrations do:
/// what a session holds is settled during composition, and a value read here would be the
/// one that existed before the composition finished.
let query (profile: unit -> ProfileFile option) : Queries.QueryRegistration =
    { Def = queryDef
      Read =
        fun () ->
            async {
                return
                    Ok (RowsOf (
                        match profile () with
                        | Some file -> rows file
                        | None -> []))
            } }
