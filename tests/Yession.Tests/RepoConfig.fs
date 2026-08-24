module Yession.Tests.RepoConfig

// The `yession.yaml` bridge (Plan 27): a real file on disk, through a real YAML parser, into
// the domain's decoder.
//
// The DECODER's own behaviour is pinned in `Domain.fs` from JSON literals, on both runtimes.
// What is left for this suite is only what a file can do that a JSON literal cannot — YAML
// syntax the parser has to be configured correctly to refuse or to allow, and the difference
// between a repo with no file and a repo with a broken one.

open Fable.Core
open Fable.Pyxpecto
open Yession.Domain
open Yession.Host

let private expect = function Ok v -> v | Error e -> failwithf "%A" e

[<ImportAll("node:fs")>]
let private nodeFs : obj = jsNative

[<ImportAll("node:os")>]
let private nodeOs : obj = jsNative

[<Emit("$0.mkdtempSync($1.tmpdir() + '/yession-config-')")>]
let private mkdtemp (fs: obj) (os: obj) : string = jsNative

[<Emit("$0.mkdirSync($1, { recursive: true })")>]
let private mkdirp (fs: obj) (path: string) : unit = jsNative

[<Emit("$0.writeFileSync($1, $2)")>]
let private writeFile (fs: obj) (path: string) (text: string) : unit = jsNative

let private repo (raw: string) = RepoRef.create raw |> expect

/// A repos directory holding one checkout, with `text` as its `yession.yaml` when given.
let private checkout (r: RepoRef) (text: string option) : string =
    let reposDir = mkdtemp nodeFs nodeOs
    mkdirp nodeFs (sprintf "%s/%s" reposDir (RepoRef.relativePath r))
    text |> Option.iter (writeFile nodeFs (RepoConfig.pathIn reposDir r))
    reposDir

// YAML fixtures live at module level: a triple-quoted string whose content starts at column 0
// is offside inside the list expression below.

let private devWithNet = """
version: 1
sandboxes:
  dev:
    image: node:24
    net:
      - registry.npmjs.org
    forward: [ github ]
"""

let private duplicateName = """
version: 1
sandboxes:
  dev:
    image: node:24
  dev:
    image: node:20
"""

let private anchored = """
version: 1
sandboxes:
  dev: &base
    net: [ registry.npmjs.org ]
  gate: *base
"""

let tests =
    testList "yession.yaml on disk (Plan 27)" [

        testCase "a real file decodes to what the repo asked for" <| fun () ->
            // The end-to-end claim: YAML on disk reaches the domain intact.
            let r = repo "octo/hello"
            let dir = checkout r (Some devWithNet)
            let file = RepoConfig.read dir r |> expect |> Option.get
            let dev = file.Sandboxes |> Map.find (SandboxName.create "dev" |> expect)
            Expect.equal dev.Image (Some { Name = "node"; Tag = Some "24" }) "the image survived the round trip"
            Expect.equal dev.Net [ "registry.npmjs.org" ] "so did the egress it asks for"
            Expect.equal dev.Forward [ "github" ] "and the credential names"

        testCase "a repo with no file asks for nothing, and that is not an error" <| fun () ->
            // The ordinary case. Most repos will never carry one.
            let r = repo "octo/plain"
            Expect.equal (RepoConfig.read (checkout r None) r) (Ok None) "absent is absent, not broken"

        testCase "a repo with a broken file is refused, not treated as absent" <| fun () ->
            // Folding this into the case above is how a repo silently stops being configured
            // the day somebody mistypes a key.
            let r = repo "octo/broken"
            Expect.isError (RepoConfig.read (checkout r (Some "version: 1\nsandboxes:\n  dev:\n    imagge: node:24\n")) r)
                "an unknown key fails the file rather than yielding an empty one"

        testCase "the refusal names the repo it came from" <| fun () ->
            // A session can hold several checkouts; "a config is broken" is not actionable.
            let r = repo "octo/broken"
            match RepoConfig.read (checkout r (Some "version: 99\n")) r with
            | Ok _ -> failwith "expected a refusal"
            | Error e -> Expect.isTrue (e.Contains "octo/broken") "it says whose file"

        testCase "a repeated sandbox name is refused rather than silently folded" <| fun () ->
            // This is what `uniqueKeys` buys, and it cannot be tested from a JSON literal:
            // JSON object semantics fold a duplicate to last-wins BEFORE any decoder sees it,
            // so without the option the second `dev` would quietly win.
            let r = repo "octo/hello"
            let dir = checkout r (Some duplicateName)
            Expect.isError (RepoConfig.read dir r) "one name declared twice fails the file"

        testCase "a YAML anchor is resolved before the decoder sees anything" <| fun () ->
            // Reuse inside a file costs the schema nothing — which is what lets a repo declare
            // a second sandbox on the same configuration without repeating it.
            let r = repo "octo/hello"
            let dir = checkout r (Some anchored)
            let file = RepoConfig.read dir r |> expect |> Option.get
            let gate = file.Sandboxes |> Map.find (SandboxName.create "gate" |> expect)
            Expect.equal gate.Net [ "registry.npmjs.org" ] "the alias carried the anchor's value"

        testCase "a custom tag cannot ask the parser for something the schema never agreed to" <| fun () ->
            // `schema: 'core'` is the whole mitigation, and a test that never exercises a tag
            // would not notice it being dropped.
            let r = repo "octo/hello"
            Expect.isError
                (RepoConfig.read (checkout r (Some "version: 1\nsandboxes: !!python/object:os.system {}\n")) r)
                "an unknown tag is refused"

        testCase "one repo's broken file does not cost the others their configuration" <| fun () ->
            // A session held hostage by whichever checkout happens to have a typo would be
            // worse than no file support at all.
            let good, bad = repo "octo/good", repo "octo/bad"
            let dir = mkdtemp nodeFs nodeOs
            for r in [ good; bad ] do mkdirp nodeFs (sprintf "%s/%s" dir (RepoRef.relativePath r))
            writeFile nodeFs (RepoConfig.pathIn dir good) "version: 1\nsandboxes:\n  dev: {}\n"
            writeFile nodeFs (RepoConfig.pathIn dir bad) "version: 1\nsandboxes:\n  dev:\n    nope: 1\n"
            let declared, refused = RepoConfig.readAll dir [ good; bad ]
            Expect.equal (Map.count declared) 1 "the good repo's sandbox survived"
            Expect.isTrue
                (declared |> Map.containsKey (SandboxRef.inScope good (SandboxName.create "dev" |> expect)))
                "and it is the good repo's, scoped to it"
            Expect.equal (List.length refused) 1 "the broken one is reported rather than dropped"
            Expect.equal (fst refused.[0]) bad "named, so somebody can fix it"
    ]
