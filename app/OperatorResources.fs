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

/// Every path a resource names must be the one the KERNEL will check.
///
/// A grant is not addressed to the operator, it is addressed to a sandbox — and the two do
/// not read a path the same way. srt canonicalises an allow-list entry, and the OS then
/// denies reading the symlink NODES that an access traverses on the way in: macOS's escape
/// hatch is `file-read-metadata` on DIRECTORIES only, and `/etc`, `/tmp` and `/run` are all
/// symlinks there. So `/etc/ssl/cert.pem` is granted at `/private/etc/ssl/cert.pem` and
/// denied at the path it was written as. Measured, not deduced: a sandbox holding exactly
/// that resource reads the canonical path and is refused the written one.
///
/// Refused rather than rewritten, and that is the whole decision. Silently canonicalising
/// would fix the mount and leave the `SSL_CERT_FILE` beside it pointing at the denied path —
/// a resource half-corrected is worse than one that failed, because the failure moves to
/// whatever reads the variable. The operator writes both lines, so the refusal has to reach
/// the operator, naming the form to write.
///
/// A path that does not resolve is left alone. A cache directory a tool has yet to create is
/// ordinary, and refusing it here would be an existence check wearing this rule's name.
let private canonicalPaths (file: ProfileFile) : Result<ProfileFile, string> =
    let offence (kind: string) (written: string) (real: string) =
        sprintf
            "%s %s is reached through a symlink — a sandbox is granted %s and denied %s, so write %s here (and in anything that points at it)"
            kind written real written real
    ResourceProfile.ceiling file.Resources
    |> Set.toList
    |> List.tryPick (fun leaf ->
        let check kind written =
            match Fs.canonical written with
            | Some real when real <> written -> Some (offence kind written real)
            | _ -> None
        match leaf with
        // Endpoints and variables name no path, and a mount's `At` is where the sandbox
        // SEES it — on a backend that cannot remount, `At` follows `From` and checking it
        // twice would say the same thing twice.
        | Mount mount -> check "the mount" mount.From
        | Socket path -> check "the socket" path
        | Exec path -> check "the executable" path
        // A volume's name is docker's, not a filesystem path; its `at` is a container
        // path, which no host symlink can reach through.
        | Endpoint _
        | Variable _
        | Volume _ -> None)
    |> function
        | Some reason -> Error reason
        | None -> Ok file

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
            | [||] ->
                OperatorProfile.parse (toJson doc)
                |> Result.bind canonicalPaths
                |> Result.map Some
                |> Result.mapError saying
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
         reaches is, however deeply. "
        // The legend travels with the answer, because this is where a model meets the
        // notation and there is no panel beside it to read one off.
        + GrantNotation.sentence
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
/// are which.
///
/// What a NAME grants, and deliberately not what a sandbox would end up holding. Those differ
/// — a host is the third author of a grant and can only widen it — and an approval prompt
/// shows the second, through `RealisedClosure.describeOn`. This surface cannot: a session
/// runs sandboxes on more than one backend, they do not scope the same things, and a row per
/// name has nowhere to put two answers. The per-leaf wording is shared either way, so the
/// two readings differ by the host's sentence and never by how a grant is spelled.
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
