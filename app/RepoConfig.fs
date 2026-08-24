module Yession.Host.RepoConfig

// The bridge between a `yession.yaml` on disk and `ConfigFile` in the domain (Plan 27).
//
// Deliberately the thinnest thing that can work, because everything worth testing lives on
// the other side of it: `Yession.Domain.ConfigFile` decodes an already-parsed tree, so it
// runs in the cheap tier on both runtimes from JSON literals. This module is the part that
// cannot — it reads a file and calls a JS parser — and it is kept small enough that its own
// failure modes are the only thing left to get wrong.
//
// YAML is a superset of JSON, so the parse hands the decoder a JSON tree and nothing about
// the schema knows which surface syntax it came from. Changing that syntax later is a change
// to this file alone.

open Fable.Core
open Yession.Domain
open Yession.Domain.Sandboxes

[<Import("parseDocument", "yaml")>]
let private parseDocument (text: string) (options: obj) : obj = jsNative

/// Everything the parser objected to, as messages.
///
/// `parse` would not do: it RESOLVES what it can and reports the rest as warnings it then
/// discards, so a file carrying a tag the schema does not define comes back as an ordinary
/// value and decodes as though the tag had never been written. `parseDocument` keeps the
/// complaints, which is what lets an unrecognised tag be a refusal rather than a silent
/// downgrade.
[<Emit("(function (doc) { return doc.errors.concat(doc.warnings).map(p => p.message) })($0)")>]
let private complaints (doc: obj) : string array = jsNative

[<Emit("JSON.stringify($0.toJS())")>]
let private toJson (doc: obj) : string = jsNative

/// How the parser is constructed, and every field is load-bearing.
///
/// `schema: "core"` is YAML's own JSON-compatible schema and nothing more, so the only tags
/// it resolves are the ones JSON could have expressed; anything else becomes a complaint
/// above. `uniqueKeys` turns a repeated key into an error rather than a silent last-wins fold
/// — which is what makes the domain's "declared twice" refusal reachable from a real file,
/// since JSON object semantics would have folded the duplicate before the decoder saw it.
/// `maxAliasCount` bounds alias expansion, so an anchor referring to itself cannot turn a
/// small file into an unbounded tree.
///
/// Anchors themselves are deliberately allowed: `&base` / `*base` resolve before the decoder
/// sees anything, so reuse inside a file costs the schema nothing.
[<Emit("{ schema: 'core', uniqueKeys: true, maxAliasCount: 100 }")>]
let private parseOptions : obj = jsNative

/// Where one repo's file lives, given the session's repos directory.
let pathIn (reposDir: string) (repo: RepoRef) : string =
    sprintf "%s/%s/%s" reposDir (RepoRef.relativePath repo) ConfigFile.FileName

/// Read and decode one repo's file.
///
/// Three outcomes, and the distinction between the first two is the point: a repo with no
/// `yession.yaml` is ORDINARY and asks for nothing, while a repo whose file cannot be read is
/// a fact somebody has to see. Folding the second into the first would make a broken file
/// indistinguishable from an absent one — which is how a repo silently stops being configured
/// the day somebody mistypes a key.
let read (reposDir: string) (repo: RepoRef) : Result<ConfigFile option, string> =
    let path = pathIn reposDir repo
    if not (Fs.exists path) then Ok None
    else
        // Every refusal names the repo: a session holds several checkouts, and "a config is
        // broken" is not something anybody can act on.
        let saying (reason: string) =
            sprintf "%s in %s: %s" ConfigFile.FileName (RepoRef.value repo) reason
        try
            let doc = parseDocument (Fs.readText path) parseOptions
            match complaints doc with
            | [||] -> ConfigFile.parse (toJson doc) |> Result.map Some |> Result.mapError saying
            | problems -> Error (saying problems.[0])
        with e -> Error (saying e.Message)

/// Every configured repo's declarations, as one session-wide map.
///
/// A repo whose file is unreadable contributes NOTHING and does not stop the others: a
/// session that failed to boot because one checkout had a typo would be a session held
/// hostage by any repo it happened to contain. The refusals come back beside the map so the
/// caller can record them where somebody will read them.
let readAll (reposDir: string) (repos: RepoRef list) : Map<SandboxRef, SandboxDecl> * (RepoRef * string) list =
    let results = repos |> List.map (fun repo -> repo, read reposDir repo)
    let declared =
        results
        |> List.choose (fun (repo, result) ->
            match result with
            | Ok (Some file) -> Some (repo, file)
            | _ -> None)
    let refused =
        results
        |> List.choose (fun (repo, result) ->
            match result with
            | Error reason -> Some (repo, reason)
            | _ -> None)
    ConfigFile.union declared, refused
