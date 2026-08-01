module Yession.Tests.NixSource

// What the Nix derivations are allowed to see of this repository (`src` in nix/packages.nix).
//
// This is the one contract in the build that NO CI job can check. Every route CI takes —
// `nix build .#yession`, the darwin package job, release.yml's package-nix — evaluates a FLAKE,
// and a flake's source copy has already been filtered by git: nothing ignored can reach the
// derivation, whatever the filter in packages.nix does or fails to do. Only a working tree
// reaches it unfiltered (`nix build --file nix/worktree.nix`, `devenv build outputs.…`), and
// only a machine that has entered the dev shell HAS the artefacts that leak. So the failure
// mode is exactly inverted from the usual one: green everywhere in CI, broken on the laptop.
//
// It broke that way once already. devenv's enterShell points `node_modules` at
// `${nodeModules}/node_modules` in the store, that same path is a build input of `staged`, and
// the unfiltered copy landed a live symlink to a read-only directory exactly where `staged`
// copies its own:
//
//     cp: cannot create directory './node_modules/node_modules'
//
// Alongside it, ~176MB of dotnet obj/bin, Fable's emitted .js and app/out rode in, so a local
// build was neither the build CI ran nor cacheable across a single edit.
//
// The invariant asserted here is the general one — the derivation source carries nothing git
// ignores — rather than a list of the artefacts that leaked. A new build output added to
// .gitignore next month is covered without touching this file.
//
// Needs the `Nix` capability: `check` probes for the nix CLI and drops it when absent, so this
// reports a skip rather than an error on a box without Nix.

open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto

let private nodeFs: obj = importAll "node:fs"
let private nodeChild: obj = importAll "node:child_process"

[<Emit("$0.readdirSync($1, { withFileTypes: true }).map(d => [d.name, d.isDirectory(), d.isSymbolicLink()])")>]
let private readDir (fs: obj) (dir: string) : (string * bool * bool) array = jsNative

[<Emit("$0.spawnSync($1, $2, { encoding: 'utf8', input: $3, maxBuffer: 33554432 })")>]
let private spawnSync (cp: obj) (command: string) (args: string array) (input: string) : obj = jsNative

[<Emit("($0.status === null ? -1 : $0.status)")>]
let private exitCode (result: obj) : int = jsNative

[<Emit("($0.stdout || '')")>]
let private stdoutOf (result: obj) : string = jsNative

[<Emit("($0.stderr || '')")>]
let private stderrOf (result: obj) : string = jsNative

/// Every entry under `root`, as paths relative to it. A symlink is reported as itself and never
/// descended into — in the failure this suite guards, `node_modules` is a symlink into the Nix
/// store, and following it would walk that entire closure instead of finding the leak.
let rec private entriesUnder (root: string) (rel: string) : string list =
    readDir nodeFs (if rel = "" then root else root + "/" + rel)
    |> Array.toList
    |> List.collect (fun (name, isDirectory, isSymlink) ->
        let path = if rel = "" then name else rel + "/" + name
        if isDirectory && not isSymlink then path :: entriesUnder root path else [ path ])

/// The build source, materialised in the store exactly as `staged` unpacks it. `--file`
/// evaluates nix/worktree.nix in place, so this is the WORKING TREE — the route CI never takes
/// and the only one where the filter can be caught failing. `--extra-experimental-features` so
/// a box that never opted into the new CLI still runs this.
let private buildSource =
    lazy
        (let result =
            spawnSync
                nodeChild
                "nix"
                [| "--extra-experimental-features"; "nix-command"
                   "eval"; "--raw"; "--file"; "nix/worktree.nix"; "staged.src" |]
                ""
         if exitCode result <> 0 then
             failwithf "could not evaluate the build source: %s" (stderrOf result)
         (stdoutOf result).Trim ())

let private entries = lazy (entriesUnder buildSource.Value "")

/// What the build reads out of the source. A file missing here fails the Nix build in a way
/// that names a tool rather than the filter (`MSB1003`, `ENOENT package.json`), which is a
/// long way from "the src filter dropped it".
let private consumed =
    [ "tasks.fsx"
      "package.json"
      "package-lock.json"
      "Yession.slnx"
      "Directory.Build.props"
      "Directory.Packages.props"
      ".config/dotnet-tools.json"
      "README.md" ]

/// Tracked, but not consumed: editing CI, docs, the agent notes or the devenv config must not
/// invalidate the (slow) F#/Fable build.
let private notConsumed =
    [ "nix/"; ".github/"; "docs/"; ".claude/"; ".agents/"
      "flake.nix"; "flake.lock"; "devenv.nix"; "devenv.yaml"; "AGENTS.md"; "CLAUDE.md" ]

let tests =
    testList "the Nix build source" [
        testCase "carries nothing git ignores" <| fun () ->
            let listed = entries.Value
            let result = spawnSync nodeChild "git" [| "check-ignore"; "--stdin" |] (String.concat "\n" listed)
            // check-ignore exits 1 when NOTHING matched — the passing outcome here. 0 means it
            // named at least one ignored path; anything else means git failed to answer, which
            // must not read as a pass.
            Expect.isTrue
                (exitCode result = 0 || exitCode result = 1)
                (sprintf "git check-ignore could not answer: %s" (stderrOf result))
            let ignored = (stdoutOf result).Trim ()
            // The message carries the offending paths: an equality assertion over the whole
            // listing reports "string was longer than expected", which names nothing.
            Expect.isTrue
                (ignored = "")
                (sprintf "the derivation source carries paths git ignores, so it is not the tree CI builds:\n%s" ignored)

        testCase "carries no node_modules — a dev shell points it at the store, read-only" <| fun () ->
            Expect.isFalse
                (entries.Value |> List.exists (fun p -> p = "node_modules" || p.StartsWith "node_modules/"))
                "`staged` copies its own node_modules to that path; anything already there makes cp descend into it"

        testCase "carries everything the build reads" <| fun () ->
            let listed = Set.ofList entries.Value
            for path in consumed do
                Expect.isTrue (Set.contains path listed) (sprintf "%s is a build input and must survive the filter" path)

        testCase "carries nothing the build does not read" <| fun () ->
            let files =
                entries.Value
                |> List.filter (fun p ->
                    notConsumed
                    |> List.exists (fun excluded ->
                        if excluded.EndsWith "/" then p.StartsWith excluded else p = excluded))
            Expect.isTrue
                (List.isEmpty files)
                (sprintf
                    "editing these must not invalidate the build, so they are filtered out of its source:\n%s"
                    (String.concat "\n" files))
    ]
