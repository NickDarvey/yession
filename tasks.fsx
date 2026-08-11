// The complete, standalone build interface for Yession. Every Yession-specific build/test/
// package function lives here so that devenv (devenv.nix scripts) and CI (.github/workflows)
// are thin wrappers — you can throw both away and still drive everything directly with
// `dotnet fsi tasks.fsx <verb>`. Only tool-specific glue (nix/devenv bootstrap, actions/*
// steps, artifact upload) stays in the callers; no Yession logic does.
//
// The verbs are the dispatch at the bottom of this file; each has its own section below. A
// bare version (`dotnet fsi tasks.fsx 1.2.3`) is shorthand for `package`.
//
// Yession ships as ONE npm package with two bins: `yession` (the Manager) and
// `yession-session` (a Session Process). Each is esbuild-bundled to a single ESM file
// with the native / self-resolving deps kept EXTERNAL — node-datachannel loads its addon and
// the Agent SDK resolves its native `claude` sibling via import.meta.url, neither of which
// works bundled. Assets are copied in and read package-relative at runtime.

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions

let repoRoot = Path.GetFullPath __SOURCE_DIRECTORY__
let dist = Path.Combine (repoRoot, "dist")
let pkg = Path.Combine (dist, "npm")
let binDir = Path.Combine (repoRoot, "node_modules", ".bin")
let esbuild = Path.Combine (binDir, "esbuild")
let tailwind = Path.Combine (binDir, "tailwindcss")

// Run a command capturing stdout (fails on non-zero); used where the output is a value.
let runIn (workingDir: string) (command: string) (arguments: string list) : string =
    let psi = ProcessStartInfo (command)
    arguments |> List.iter psi.ArgumentList.Add
    psi.WorkingDirectory <- workingDir
    psi.RedirectStandardOutput <- true
    use p = Process.Start psi
    let output = p.StandardOutput.ReadToEnd ()
    p.WaitForExit ()
    if p.ExitCode <> 0 then failwithf "%s %s failed (%d)" command (String.concat " " arguments) p.ExitCode
    output.Trim ()

let run (command: string) (arguments: string list) : string = runIn repoRoot command arguments

// Run a command with inherited stdio (streams straight to the console); returns the exit code.
let runInherit (workingDir: string) (command: string) (arguments: string list) : int =
    let psi = ProcessStartInfo (command)
    arguments |> List.iter psi.ArgumentList.Add
    psi.WorkingDirectory <- workingDir
    psi.UseShellExecute <- false
    use p = Process.Start psi
    p.WaitForExit ()
    p.ExitCode

let exec (command: string) (arguments: string list) : unit =
    let code = runInherit repoRoot command arguments
    if code <> 0 then failwithf "%s %s failed (%d)" command (String.concat " " arguments) code

// --- version: the continuous-delivery number, bumped from commit messages ---------------------

// Trunk-based continuous delivery: every green master push publishes 1.0.0-beta.<n>. To move the
// triple, put a marker in the commit message — for a squash-merged PR, its title or body:
//
//     +semver: major | breaking   (or a BREAKING CHANGE: footer)  -> 2.0.0-beta.0
//     +semver: minor | feature                                    -> 1.1.0-beta.0
//     +semver: fix   | patch                                      -> 1.0.1-beta.0
//
// A marker counts anywhere in the message as long as it is alone on its line; BREAKING CHANGE:
// is read only from the footer. Markers are scanned over the commits since the last reachable v*
// tag. A tag is cut on every green push, so that range is normally the single merge commit. A
// plain `feat:` deliberately does NOT bump minor: with a tag per push, nearly every release would
// bump. Breaking changes are the case worth catching automatically, so BREAKING CHANGE: is
// honoured alongside the explicit marker.

type private Bump =
    | Major
    | Minor
    | Patch

/// The FOOTER: the last blank-line-separated block of the message, where conventional commits put
/// their trailers. Only `BREAKING CHANGE:` is read from here (see `bumpOf`).
let private footerOf (message: string) =
    let blocks = Regex.Split (message.Replace("\r\n", "\n").Trim (), @"\n[ \t]*\n")
    if blocks.Length = 0 then "" else blocks.[blocks.Length - 1]

/// A `+semver:` marker counts ANYWHERE in the message — subject, body, or footer. It used to be
/// footer-only, which reads well until something has to come after it: a PR body that ends with a
/// required trailer (an attribution line, a bot signature) pushes the marker out of the last block
/// and the bump is silently lost. That happened, and a lost bump is as wrong as a spurious one.
///
/// What still holds the line is the LINE: a marker must be the ONLY thing on it, whitespace aside.
/// Anything else — a bullet, a quote marker, a trailing comment, a word before it — makes the line
/// prose, so a message that mentions a marker in passing cannot move the release. The separator is
/// `[ \t]*`, not `\s*`: `\s` matches a newline, which would let `+semver:` on one line pair with
/// `fix` on the next, and then neither line carries the marker.
///
/// The sharp edge left is quoting — a marker table reproduced verbatim in a message DOES bump, so
/// keep examples annotated (as the table above is: the trailing `-> 2.0.0-beta.0` makes those lines
/// inert) rather than bare.
///
/// `BREAKING CHANGE:` stays footer-only. It is a conventional-commits trailer, defined to live
/// there, and it is the one marker that moves MAJOR — an earlier policy scanned the whole body for
/// it and line-wrapped prose that happened to start a line with it cut a spurious major tag.
///
/// Highest severity wins when several appear.
let private bumpOf (message: string) =
    let body = message.Replace("\r\n", "\n")
    let has pattern =
        Regex.IsMatch (body, pattern, RegexOptions.IgnoreCase ||| RegexOptions.Multiline)
    if has @"^[ \t]*\+semver:[ \t]*(breaking|major)[ \t]*$"
       || Regex.IsMatch (footerOf message, @"^BREAKING[ -]CHANGE:", RegexOptions.Multiline) then Some Major
    elif has @"^[ \t]*\+semver:[ \t]*(feature|minor)[ \t]*$" then Some Minor
    elif has @"^[ \t]*\+semver:[ \t]*(fix|patch)[ \t]*$" then Some Patch
    else None

// Run a command, returning None instead of failing when it exits non-zero. `git describe` reports
// "no tag" that way, which is a legitimate state (a repository before its first release).
let private tryRun (command: string) (arguments: string list) : string option =
    let psi = ProcessStartInfo (command)
    arguments |> List.iter psi.ArgumentList.Add
    psi.WorkingDirectory <- repoRoot
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true // "fatal: No names found" is an answer here, not an error.
    use p = Process.Start psi
    let output = p.StandardOutput.ReadToEnd ()
    p.StandardError.ReadToEnd () |> ignore
    p.WaitForExit ()
    if p.ExitCode = 0 then Some (output.Trim ()) else None

/// The version source: the nearest reachable v* tag, plus every commit message since it, OLDEST
/// FIRST. `%x1e` separates the records because a message is multi-line and a marker may sit in a
/// footer. `describe` walks history rather than sorting tag names, so the tag is the one this
/// commit actually descends from.
let private versionHistory () =
    if tryRun "git" [ "rev-parse"; "--is-shallow-repository" ] = Some "true" then
        failwith "version: shallow clone — the history IS the version (git fetch --unshallow --tags)"

    let tag = tryRun "git" [ "describe"; "--tags"; "--abbrev=0"; "--match"; "v*" ]
    let range = match tag with Some t -> t + "..HEAD" | None -> "HEAD"
    let messages =
        (run "git" [ "log"; range; "--reverse"; "--format=%B%x1e" ]).Split '\u001e'
        |> Array.map (fun message -> message.Trim ())
        |> Array.filter (fun message -> message <> "")
    tag, messages

/// `v1.0.0-beta.94` -> (1, 0, 0, Some 94). A tag off this scheme's shape yields None, and is
/// treated as no tag at all.
let private parseTag (tag: string) =
    let m = Regex.Match (tag, @"^v?(\d+)\.(\d+)\.(\d+)(?:-beta\.(\d+))?$")
    if not m.Success then None
    else
        let value (i: int) = int m.Groups.[i].Value
        Some (value 1, value 2, value 3, (if m.Groups.[4].Success then Some (value 4) else None))

let private computeVersion () =
    let tag, messages = versionHistory ()
    let count = messages.Length

    // Every marker in the range, with its position. The STRONGEST decides the new triple; the
    // EARLIEST decides where the prerelease counter restarts.
    let marks =
        messages
        |> Array.mapi (fun i message -> i, bumpOf message)
        |> Array.choose (fun (i, bump) -> bump |> Option.map (fun b -> i, b))
    let strongest =
        if Array.isEmpty marks then None
        else Some (marks |> Array.map snd |> Array.minBy (function Major -> 0 | Minor -> 1 | Patch -> 2))

    match tag |> Option.bind parseTag, strongest with
    // No release tag yet — seed the line.
    | None, _ -> sprintf "1.0.0-beta.%d" count

    // No marker: carry the tag's own prerelease counter forward, so the number keeps climbing
    // across releases (v1.0.0-beta.94 + 1 commit -> 1.0.0-beta.95).
    | Some (major, minor, patch, Some n), None -> sprintf "%d.%d.%d-beta.%d" major minor patch (n + count)

    // A tag with no prerelease (this scheme never cuts one, but a human might). ON the tag, that
    // release IS the version; past it bump patch, because X.Y.Z-beta.n would sort BELOW the tag.
    | Some (major, minor, patch, None), None when count = 0 -> sprintf "%d.%d.%d" major minor patch
    | Some (major, minor, patch, None), None -> sprintf "%d.%d.%d-beta.%d" major minor (patch + 1) count

    // Marked: bump the triple and restart the counter AT the marker, so the first build carrying
    // a breaking change is X.0.0-beta.0 and every commit after it gets a fresh number.
    | Some (major, minor, patch, _), Some bump ->
        let major, minor, patch =
            match bump with
            | Major -> major + 1, 0, 0
            | Minor -> major, minor + 1, 0
            | Patch -> major, minor, patch + 1
        sprintf "%d.%d.%d-beta.%d" major minor patch (count - 1 - fst marks.[0])

// YESSION_VERSION wins when set. It is how a version enters a build that cannot compute one: the
// Nix derivations (lib.cleanSource strips .git, so nix/packages.nix supplies it), and rebuilding
// a past release without today's history reporting today's number.
let gitVersion () =
    match Environment.GetEnvironmentVariable "YESSION_VERSION" with
    | null | "" -> computeVersion ()
    | version -> version

// --- restore: deps (npm + .NET tools) -------------------------------------------------------

// node_modules is provided by the environment when one exists: under devenv it's a Nix artifact
// (the offline npm tree + the source-built node-datachannel addon) symlinked in by enterShell —
// so the native addon is present with no Yession-specific env var or per-file linking, and we
// don't `npm install` over it. Off-Nix (no such tree) a plain `npm install` materializes the
// deps; the node-datachannel addon there still comes from Nix or a manual build (the `Native`
// tier self-skips without it), same as before.
let restore () =
    if not (Directory.Exists (Path.Combine (repoRoot, "node_modules"))) then
        exec "npm" [ "install"; "--ignore-scripts" ]
    exec "dotnet" [ "tool"; "restore" ]

/// The version to build with when the caller supplied none. GitVersion is a dotnet local tool,
/// so the manifest has to be restored before it can be asked.
let private defaultVersion () =
    restore ()
    gitVersion ()

// --- compile: F# -> JS (both entries), the browser client bundle, and the stylesheet --------

// Fable copies each referenced package's `fable/` folder into `<out>/fable_modules/<pkg>`
// verbatim, package.json included — and a package whose manifest omits `"type"` (Fable.Lit
// 1.4.2 is the one) shadows the repo root's `"type": "module"` for everything beneath it. Node
// then has to reparse each of those files to discover they are ESM after all, and says so:
// eighteen five-line MODULE_TYPELESS_PACKAGE_JSON warnings per run, about a tree that is ESM
// and always was. Stamp the field the manifest should have carried. Idempotent, and re-run
// after every Fable invocation because a recompile restores the package's own copy.
let private declareEsm (outDir: string) =
    let modules = Path.Combine (repoRoot, outDir, "fable_modules")
    if Directory.Exists modules then
        for manifest in Directory.GetFiles (modules, "package.json", SearchOption.AllDirectories) do
            let node = Text.Json.Nodes.JsonNode.Parse (File.ReadAllText manifest)
            if isNull node.["type"] then
                node.["type"] <- Text.Json.Nodes.JsonValue.Create "module"
                let options = Text.Json.JsonSerializerOptions (WriteIndented = true)
                File.WriteAllText (manifest, node.ToJsonString options + "\n")

/// The resolved package set behind a project: the exact `<package>/<version>` pairs NuGet
/// settled on, hashed. Only the `libraries` keys, so it does not move with the machine's
/// package-folder path or with a restore that changed nothing.
let private packageGraph (project: string) : string =
    let assets = Path.Combine (Path.GetDirectoryName project, "obj", "project.assets.json")
    let node = Text.Json.Nodes.JsonNode.Parse (File.ReadAllText assets)
    let libraries = node.["libraries"] :?> Text.Json.Nodes.JsonObject
    let keys = libraries |> Seq.map (fun kv -> kv.Key) |> Seq.sort |> String.concat "\n"
    Convert.ToHexString (Security.Cryptography.SHA256.HashData (Text.Encoding.UTF8.GetBytes keys))

/// Compile an F# project to JS. `quiet` captures Fable's banner — the build verbs print their
/// own one-line progress; `check` streams it.
///
/// Fable caches the cracked project and the package sources it copied under
/// `<out>/fable_modules`, and its cache key does not include the RESOLVED package graph — so
/// a version bump in Directory.Packages.props leaves the previous package's sources in place
/// and Fable compiles THEM. project.assets.json said Ylmish 1.0.0-beta0222 while fable_modules
/// still held beta0219, and the build was green against the previous library; it was only loud
/// because that release changed the API's shape, and a source-compatible bump would have passed
/// in silence. So key the out dir on the graph here: if it moved, the cached crack is void.
let private fable (quiet: bool) (project: string) (outDir: string) =
    let stamp = Path.Combine (outDir, ".package-graph")
    let graph = packageGraph project
    if File.Exists stamp && File.ReadAllText stamp <> graph then
        let modules = Path.Combine (outDir, "fable_modules")
        if Directory.Exists modules then Directory.Delete (modules, true)
    // Stamped BEFORE compiling, not after: it records which graph `fable_modules` holds, and
    // once the stale copy is gone that is settled. A compile error in OUR sources says nothing
    // about the packages, and re-deleting them on every attempt would make an error-fix loop
    // pay a full dependency re-copy each time round.
    Directory.CreateDirectory outDir |> ignore
    File.WriteAllText (stamp, graph)
    if quiet then run "dotnet" [ "fable"; project; "-o"; outDir ] |> ignore
    else exec "dotnet" [ "fable"; project; "-o"; outDir ]
    declareEsm outDir

let compile () =
    printfn "compiling F# -> JS"
    run "dotnet" [ "build"; "Yession.slnx" ] |> ignore
    fable true "app/main/Yession.Host.Main.fsproj" "app/out"
    fable true "app/browser/Yession.Browser.fsproj" "app/out/browser"
    run esbuild [ "app/out/browser/Browser.js"; "--bundle"; "--format=esm"; "--minify"; "--outfile=app/out/public/client.js" ] |> ignore
    // Tailwind, built locally into a served stylesheet (no CDN); scans the F# sources.
    run tailwind [ "-i"; "app/tailwind.css"; "-o"; "app/out/public/app.css"; "--minify" ] |> ignore

let build () =
    restore ()
    compile ()

// --- start / dev: run the Session Process locally --------------------------------------------

// Local runs are single-machine, so the loopback trust rule is the right default here;
// the shipped binary defaults to `--auth none` (deny) until the operator chooses.
let start () =
    build ()
    exec "node" [ "app/out/Main.js"; "--auth"; "localhost" ]

let dev () =
    restore ()
    exec "dotnet" [ "fable"; "watch"; "app/main/Yession.Host.Main.fsproj"; "-o"; "app/out"; "--runWatch"; "node"; "app/out/Main.js"; "--auth"; "localhost" ]

// --- stage: bundle the two bins (deps external) and assemble dist/npm ------------------------

// node-datachannel (native addon) and @anthropic-ai/claude-agent-sdk (resolves its own native
// `claude` sibling via import.meta.url) MUST NOT be bundled — they only work from their real
// node_modules. @anthropic-ai/sandbox-runtime is the same shape: it reaches for vendored
// helper binaries beside its own module. zod is a dynamic import shared with the SDK; dockerode
// is pure JS but pulls ssh2 (with an optional native addon), so it resolves from node_modules
// too. Everything else (yjs, lib0, Thoth, prosemirror, …) inlines.
//
// `serialport` is NOT here: it belongs to the example provider, which has its own bundle and
// its own externals (see the `example` verb). The product neither imports it nor declares it.
let private externals =
    [ "node-datachannel"; "@anthropic-ai/claude-agent-sdk"; "@anthropic-ai/sandbox-runtime"
      "zod"; "dockerode"; "@napi-rs/keyring" ]
    |> List.map (sprintf "--external:%s")

// The OTel SDK does a dynamic `require('util')`; esbuild's ESM output can't satisfy a runtime
// `require`, so restore a real one at the top of each bundle via createRequire.
let private banner =
    "--banner:js=import { createRequire as __createRequire } from 'module'; const require = __createRequire(import.meta.url);"

// The build version is a COMPILE-TIME constant: esbuild substitutes the `YESSION_BUILD_VERSION`
// identifier for this literal, so `Version.current` costs nothing at run time and the bundle
// needs no package.json (it never ships next to one it could trust). An unbundled run of the
// Fable output has no define and reports "dev" — see app/Version.fs.
let private bundle (version: string) (entry: string) (outFile: string) =
    run esbuild
        ([ Path.Combine (repoRoot, entry); "--bundle"; "--platform=node"; "--format=esm"; banner
           sprintf "--define:YESSION_BUILD_VERSION=\"%s\"" version ]
         @ externals
         @ [ sprintf "--outfile=%s" (Path.Combine (pkg, outFile)) ])
    |> ignore

let private depVersion (name: string) =
    let json = File.ReadAllText (Path.Combine (repoRoot, "package.json"))
    let marker = sprintf "\"%s\":" name
    let start = json.IndexOf marker + marker.Length
    let quote1 = json.IndexOf ('"', start)
    let quote2 = json.IndexOf ('"', quote1 + 1)
    json.Substring (quote1 + 1, quote2 - quote1 - 1)

// Bin shims and package.json live at module level (offside column 0) so their column-0
// string content doesn't trip F#'s indentation rule inside the `stage` function.
let private managerBinJs = """#!/usr/bin/env node
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'
process.env.YESSION_SESSION_MAIN ||= join(dirname(fileURLToPath(import.meta.url)), '..', 'session.js')
import('../manager.js')
"""

let private yessionSessionBinJs = """#!/usr/bin/env node
import('../session.js')
"""

// package.json — runtime deps are exactly the externals; npm resolves their platform-native
// optionalDependencies (node-datachannel's addon, the SDK's native `claude`) on install.
let private packageJson (version: string) =
    sprintf """{
  "name": "yession",
  "version": "%s",
  "description": "Local-first runtime where humans and AI agents collaborate inside a shared session.",
  "type": "module",
  "bin": {
    "yession-manager": "bin/yession-manager.js",
    "yession-session": "bin/yession-session.js"
  },
  "files": ["bin/", "manager.js", "session.js", "assets/", "README.md"],
  "engines": { "node": ">=24" },
  "dependencies": {
    "@anthropic-ai/claude-agent-sdk": "%s",
    "@anthropic-ai/sandbox-runtime": "%s",
    "@napi-rs/keyring": "%s",
    "dockerode": "%s",
    "node-datachannel": "%s",
    "zod": "%s"
  }
}
"""
        version
        (depVersion "@anthropic-ai/claude-agent-sdk")
        (depVersion "@anthropic-ai/sandbox-runtime")
        (depVersion "@napi-rs/keyring")
        (depVersion "dockerode")
        (depVersion "node-datachannel")
        (depVersion "zod")

let stage (version: string) =
    // Every build states what it is — a release number, `test`, `dev`, or a rev. An empty string
    // is no provenance at all, and would reach the published package.json, so it fails here.
    if String.IsNullOrWhiteSpace version then
        failwith "stage: no version (pass one, or set YESSION_VERSION)"

    compile ()
    printfn "staging yession %s (npm, one package / two bins) -> dist/npm" version

    for required in
        [ "app/out/Main.js"; "app/SessionMain.js"
          "app/out/public/client.js"; "app/out/public/app.css" ] do
        if not (File.Exists (Path.Combine (repoRoot, required))) then
            failwithf "missing %s after compile" required

    if Directory.Exists pkg then Directory.Delete (pkg, true)
    Directory.CreateDirectory pkg |> ignore
    Directory.CreateDirectory (Path.Combine (pkg, "bin")) |> ignore
    Directory.CreateDirectory (Path.Combine (pkg, "assets")) |> ignore

    bundle version "app/out/Main.js" "manager.js"
    bundle version "app/SessionMain.js" "session.js"

    // Assets (read package-relative at runtime by Interop.readAsset).
    File.Copy (Path.Combine (repoRoot, "app/out/public/client.js"), Path.Combine (pkg, "assets/client.js"), true)
    File.Copy (Path.Combine (repoRoot, "app/out/public/app.css"), Path.Combine (pkg, "assets/app.css"), true)

    // Bin shims. `yession-manager` points the Manager at the packaged session bundle (both live
    // in one install), so it spawns `node session.js` with no PATH assumptions.
    File.WriteAllText (Path.Combine (pkg, "bin/yession-manager.js"), managerBinJs)
    File.WriteAllText (Path.Combine (pkg, "bin/yession-session.js"), yessionSessionBinJs)
    File.WriteAllText (Path.Combine (pkg, "package.json"), packageJson version)
    File.Copy (Path.Combine (repoRoot, "README.md"), Path.Combine (pkg, "README.md"), true)

// --- boot-smoke: run a yession bin with ephemeral ports and assert it comes up ---------------

// What each bin says when it is up, quoted once so a caller cannot smoke the wrong line.
let managerReady = "management UI at"
// Both examples are MCP servers, and both announce their control url the same way. One
// string, because "the provider is up" is one fact however the provider was written.
let providerReady = "MCP at"

// The Manager's smoke arguments. `--secrets ephemeral` matches the throwaway data dir: a smoke
// boot has no business minting a KEK in the developer's real Keychain, and it makes the boot
// behave the same on a laptop (credential manager present) as in CI (absent). It is also the
// only place anything proves the flag STRING reaches its resolver — `Interop.argValue` is an
// [<Emit>] over process.argv that no unit test can drive.
//
// A CALL-SITE argument rather than something `bootSmoke` adds: the verb smokes other bins too,
// and `--secrets` means nothing to them.
let managerSmokeArgs = [ "--secrets"; "ephemeral" ]

// Reused by `package`, `install-smoke`, and CI's nix-package job: spawn the given command with
// an ephemeral data dir + port 0, and assert it prints `ready` before a deadline. A bin that
// cannot boot never passes the gate.
//
// The readiness line is a PARAMETER because not everything bootable announces the same thing:
// the Manager serves the management UI, an example provider serves an MCP endpoint and names
// itself. Hard-coding the Manager's line once made a bin unsmokeable, and an unsmokeable bin
// is how one of them shipped unwrapped.
let bootSmoke (ready: string) (command: string) (arguments: string list) =
    let dataDir = Path.Combine (Path.GetTempPath (), "yession-boot-" + Guid.NewGuid().ToString "N")
    Directory.CreateDirectory dataDir |> ignore

    // A command with a path separator (e.g. ./result/bin/yession) resolves to an absolute path;
    // a bare name (node) is left for PATH lookup.
    let command = if command.Contains "/" then Path.GetFullPath command else command
    let psi = ProcessStartInfo (command)
    arguments |> List.iter psi.ArgumentList.Add
    psi.WorkingDirectory <- repoRoot
    psi.RedirectStandardOutput <- true
    psi.EnvironmentVariables.["YESSION_DATA_DIR"] <- dataDir
    psi.EnvironmentVariables.["YESSION_MANAGER_PORT"] <- "0"
    psi.EnvironmentVariables.["YESSION_SERIAL_PORT"] <- "0"
    psi.EnvironmentVariables.["JUMPSTARTER_PROVIDER_PORT"] <- "0"
    let p = Process.Start psi

    try
        let mutable seen = false
        let deadline = DateTime.UtcNow.AddSeconds 30.0
        while not seen && DateTime.UtcNow < deadline && not p.HasExited do
            let line = p.StandardOutput.ReadLine ()
            if line <> null then
                printfn "[smoke] %s" line
                if line.Contains ready then seen <- true
        if not seen then failwithf "boot-smoke: %s never printed %s" command ready
        printfn "boot-smoke: %s booted and printed %s" command ready
    finally
        try p.Kill true with _ -> ()

// --- package: restore + stage + boot smoke + npm pack ----------------------------------------

let package (version: string) =
    restore ()
    stage version
    // Boot the packaged bin shim (it self-sets YESSION_SESSION_MAIN); externals resolve from the
    // repo node_modules two levels up from dist/npm.
    bootSmoke managerReady "node" ([ Path.Combine (pkg, "bin/yession-manager.js") ] @ managerSmokeArgs)
    let packed = runIn pkg "npm" [ "pack"; "--pack-destination"; dist ] |> fun out -> out.Split('\n') |> Array.last
    printfn "packaged dist/%s" (Path.GetFileName (packed.Trim ()))

// --- install-smoke: prove a clean npm install pulls the native deps and boots ----------------

let installSmoke (tgz: string) =
    let tgz = Path.GetFullPath tgz // a bare dist/x.tgz looks like a GitHub owner/repo to npm.
    let prefix = Path.Combine (Path.GetTempPath (), "yession-install-" + Guid.NewGuid().ToString "N")
    Directory.CreateDirectory prefix |> ignore
    exec "npm" [ "install"; "--prefix"; prefix; tgz ]

    // The SDK's native `claude` binary is an optional dep keyed by platform (…-linux-x64 etc.);
    // assert one such package resolved, without hard-coding the arch suffix.
    let scope = Path.Combine (prefix, "node_modules/@anthropic-ai")
    let claudePulled =
        Directory.Exists scope
        && Directory.GetDirectories (scope, "claude-agent-sdk-*")
           |> Array.exists (fun d -> File.Exists (Path.Combine (d, "claude")))
    if not claudePulled then failwith "install-smoke: native claude binary was not pulled by npm"

    let ndcRelease = Path.Combine (prefix, "node_modules/node-datachannel/build/Release")
    if not (Directory.Exists ndcRelease && (Directory.GetFiles (ndcRelease, "*.node")).Length > 0) then
        failwith "install-smoke: node-datachannel addon was not built"

    bootSmoke managerReady "node" ([ Path.Combine (prefix, "node_modules/.bin/yession-manager") ] @ managerSmokeArgs)
    printfn "install-smoke: native deps resolved and the installed package booted"

// --- example: build a standalone example integration -----------------------------------------

// The examples are NOT part of the product: `stage` never sees them, the npm package does not
// carry them, and the Nix installable does not wrap them. That separation is the whole point —
// an example that quietly rides the product's build is not demonstrating anything a reader
// could reproduce.
//
// They are built here anyway, because an example that does not compile is worse than none, and
// because the suite drives one of them. The bundle keeps `serialport` external for the same
// reason the product's does: a native addon cannot be bundled, it resolves at run time.
//
// An example is built the way ITS OWN ecosystem builds one, which is the point of the
// examples at all: the F# one goes through Fable and esbuild, the Python one through uv. What
// they owe this verb is identical and small — compile, and then BOOT, because an example that
// does not start is worse than none.
let private pythonExample (dir: string) (name: string) =
    // `uv sync` against the committed lock, so the suite runs the resolution the author had
    // rather than whatever PyPI holds today. (The DEPLOYMENT resolves fresh from the ranges
    // in pyproject.toml — uvx from a git ref never sees a lock — which is why those ranges
    // are pinned where the API is not stable.)
    exec "uv" [ "sync"; "--project"; dir; "--all-groups" ]
    printfn "testing example %s" name
    exec "uv" [ "run"; "--project"; dir; "pytest"; "-q" ]
    bootSmoke providerReady "uv" [ "run"; "--project"; dir; name + "-provider" ]
    printfn "built and tested examples/%s" name

let example (name: string) =
    let dir = Path.Combine (repoRoot, "examples", name)
    if not (Directory.Exists dir) then
        let available =
            Directory.GetDirectories (Path.Combine (repoRoot, "examples"))
            |> Array.map Path.GetFileName
            |> String.concat ", "
        failwithf "no example called '%s' (have: %s)" name available
    if File.Exists (Path.Combine (dir, "pyproject.toml")) then pythonExample dir name else

    let project = Directory.GetFiles (dir, "*.fsproj") |> Array.exactlyOne
    restore ()
    printfn "building example %s" name
    run "dotnet" [ "build"; project ] |> ignore
    let out = Path.Combine (dir, "out")
    fable true project out
    // Fable mirrors the project's own source layout under `-o`, so the entry is wherever
    // `Main.fs` sat rather than at the root — found rather than assumed, so an example is free
    // to lay its sources out however reads best. `fable_modules` is excluded because the
    // copied packages have `Main.js` files of their own.
    let entry =
        Directory.GetFiles (out, "Main.js", SearchOption.AllDirectories)
        |> Array.filter (fun path -> not (path.Contains "fable_modules"))
        |> function
            | [| one |] -> one
            | [||] -> failwithf "example %s: no Main.js under %s — is the entry module called Main?" name out
            | many -> failwithf "example %s: %d candidate entries under %s" name many.Length out
    Directory.CreateDirectory (Path.Combine (dir, "dist")) |> ignore
    let outFile = Path.Combine (dir, "dist/main.js")
    run esbuild
        [ entry; "--bundle"; "--platform=node"; "--format=esm"
          "--external:serialport"; sprintf "--outfile=%s" outFile ]
    |> ignore
    // A build is not a boot, here for the same reason it is not one for the product's bins.
    bootSmoke providerReady "node" [ outFile ]
    printfn "built examples/%s/dist/main.js" name

// --- check: capability-gated test orchestration ----------------------------------------------

let private hasAny (caps: Set<string>) names = names |> List.exists caps.Contains

// Run the Fable-compiled test bundle on Node with a hard timeout, so a hung WebRTC connection
// (or any hang) can never block the suite. Inherits stdio (output streams; env passes through,
// incl. YESSION_TEST_CAPS) and forwards the suite's exit code; a timeout is a failure.
let private runNodeSuite (target: string) (timeoutMs: int) =
    let psi = ProcessStartInfo "node"
    psi.ArgumentList.Add target
    psi.WorkingDirectory <- repoRoot
    psi.UseShellExecute <- false
    use p = Process.Start psi
    if p.WaitForExit timeoutMs then
        if p.ExitCode <> 0 then failwithf "tests failed (exit %d)" p.ExitCode
    else
        eprintfn "tests: timed out after %dms — killing" timeoutMs
        p.Kill true
        failwith "tests timed out"

// Ask the box whether it really has a capability, by running the cheapest command that can
// only succeed if it does. A missing binary, a non-zero exit and a hang all answer false.
//
// The deadline is a parameter because one probe is not cheap: resolving a Python environment
// fetches wheels the first time, and thirty seconds would make `check Jumpstarter` refuse on a
// cold cache and pass on a warm one — a capability that depends on what you did yesterday.
let private probeWithin (timeoutMs: int) (command: string) (arguments: string list) =
    try
        let psi = ProcessStartInfo command
        arguments |> List.iter psi.ArgumentList.Add
        psi.WorkingDirectory <- repoRoot
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        use p = Process.Start psi
        if p.WaitForExit timeoutMs then p.ExitCode = 0
        else
            p.Kill true
            false
    with _ -> false

let private probeSucceeds (command: string) (arguments: string list) = probeWithin 30000 command arguments

// `docker info` talks to the daemon over the same socket / DOCKER_HOST the dockerode backend
// uses, so it answers the question the suites care about (not merely "is the socket file
// there").
let private dockerReachable () = probeSucceeds "docker" [ "info" ]

// For Nix the CLI being there is the whole question: the invocations below pass
// `--extra-experimental-features nix-command` themselves, so a box that never opted into the
// new CLI still runs the gate rather than failing on an unrecognised subcommand.
let private nixAvailable () = probeSucceeds "nix" [ "--version" ]

// A pty is probed by OPENING one, not by looking for the addon's file. `require` succeeding
// proves the package resolved; it does not prove the compiled `.node` beside it matches this
// Node's ABI, and it certainly does not prove the kernel will hand out a pty — which is the
// thing the suites need and the thing a container can be configured without.
let private ptyAvailable () =
    probeSucceeds
        "node"
        [ "-e"
          "const p=require('node-pty');const t=p.spawn('/bin/sh',['-c','exit 0'],{cols:80,rows:24});t.kill()" ]

// The live agent suites need a real credential; the SDK reads either of these.
let private agentCredentials () =
    [ "ANTHROPIC_API_KEY"; "CLAUDE_CODE_OAUTH_TOKEN" ]
    |> List.exists (fun name -> not (String.IsNullOrEmpty (Environment.GetEnvironmentVariable name)))

// The srt backend confines with bubblewrap on Linux and Seatbelt on macOS. Seatbelt is part
// of the OS, so darwin always has the capability; on Linux it is bubblewrap, socat (the bridge
// into the unshared network namespace) and ripgrep (srt refuses to start a sandbox without
// one) that have to be there — and, since bwrap needs unprivileged user namespaces, being
// installed is not the same as WORKING. Run the profile the suites will run under, rather than
// look for binaries: a kernel that forbids the nested namespace fails here, where the reason
// is one line, instead of inside a suite.
let private srtAvailable () =
    if OperatingSystem.IsMacOS () then true
    else
        let named name = Environment.GetEnvironmentVariable name
        let bwrap = match named "YESSION_BWRAP_PATH" with null | "" -> "bwrap" | path -> path
        let socat = match named "YESSION_SOCAT_PATH" with null | "" -> "socat" | path -> path
        let ripgrep = match named "YESSION_RIPGREP_PATH" with null | "" -> "rg" | path -> path
        let weak = (match named "YESSION_SANDBOX_NESTED" with null -> "" | v -> v.Trim().ToLowerInvariant ()) = "weak"
        let confines =
            if weak then
                probeSucceeds bwrap [ "--ro-bind"; "/"; "/"; "--dev"; "/dev"; "--unshare-net"; "--unshare-pid"
                                      "--unshare-user"; "--bind"; "/proc"; "/proc"; "true" ]
            else
                probeSucceeds bwrap [ "--ro-bind"; "/"; "/"; "--dev"; "/dev"; "--unshare-net"; "--unshare-pid"
                                      "--unshare-user"; "--cap-drop"; "ALL"; "--proc"; "/proc"; "--"
                                      bwrap; "--ro-bind"; "/"; "/"; "--unshare-user"; "true" ]
        probeSucceeds socat [ "-V" ] && probeSucceeds ripgrep [ "--version" ] && confines

// A real serial engine, probed by USING it rather than by looking for files — the same rule
// `Srt` follows, and for a sharper reason here: the `serialport` addon can be present and still
// unable to enumerate, because `SerialPort.list()` shells out to `udevadm` on Linux and a box
// without it throws. socat is the third leg: a PTY pair is a serial port at both ends, which is
// how the engine is exercised with no hardware attached.
let private serialAvailable () =
    let socat = match Environment.GetEnvironmentVariable "YESSION_SOCAT_PATH" with null | "" -> "socat" | path -> path
    let listed =
        // `SerialPort.list()` for real. Zero ports is a pass — the question is whether the
        // engine ANSWERS, not whether this box has hardware.
        probeSucceeds
            "node"
            [ "-e"
              "import('serialport').then(m => m.SerialPort.list()).then(() => process.exit(0), () => process.exit(1))" ]
    probeSucceeds socat [ "-V" ] && listed

// uv, an interpreter it can resolve, and every dependency the jumpstarter example locked —
// probed by BUILDING that environment, because each of those three is a way for this to be
// absent and only the last one is visible from outside. `--frozen` makes it the lock's
// resolution or nothing: a probe that quietly re-resolved would answer a different question
// from the one the suite goes on to ask.
let private jumpstarterAvailable () =
    probeWithin 600000 "uv" [ "sync"; "--project"; "examples/jumpstarter"; "--all-groups"; "--frozen" ]

// ASKING FOR A CAPABILITY IS REQUIRING IT. A run names the capabilities it wants; anything it
// named and cannot host is an ERROR, with the reason, before a single test runs.
//
// This used to drop the capability instead, so its suites reported a skip — which reads as
// prudence and behaves as a blind spot. A release workflow that named a secret this repository
// does not have shipped every version up to v5.0.0-beta.0 with the live agent suite quietly
// skipped: green, and never once a real agent turn. The `YESSION_REQUIRE_*` overrides that
// existed to patch exactly that hole are gone with the drop that needed them; there is one
// rule now, and it is the obvious one.
//
// What stays a skip is the RUNTIME partition (`Tag.needs`): a Node suite on the .NET CLR and a
// browser suite on Node are not availability questions, they are the other runtime's tests.
let private requireCapabilities (caps: string list) =
    let missing =
        [ if List.contains "Docker" caps && not (dockerReachable ()) then
            "Docker: no daemon answers `docker info` (is it running, is DOCKER_HOST right?)"
          if List.contains "Nix" caps && not (nixAvailable ()) then
            "Nix: no `nix` on PATH"
          if List.contains "LiveAgent" caps && not (agentCredentials ()) then
            "LiveAgent: no ANTHROPIC_API_KEY or CLAUDE_CODE_OAUTH_TOKEN in the environment"
          if List.contains "Pty" caps && not (ptyAvailable ()) then
            "Pty: node-pty could not open a pseudo-terminal (is the native addon built, and "
            + "does this box allow /dev/pts?)"
          if List.contains "Serial" caps && not (serialAvailable ()) then
            "Serial: no working serial engine (needs the `serialport` addon, `udevadm` for "
            + "enumeration, and socat for a PTY pair — `devenv shell` provides all three on Linux)"
          if List.contains "Jumpstarter" caps && not (jumpstarterAvailable ()) then
            "Jumpstarter: the example's Python environment would not resolve (needs `uv` and a "
            + "CPython >= 3.11 it can find, plus a network on the first run — try "
            + "`uv sync --project examples/jumpstarter --all-groups`)"
          if List.contains "Srt" caps && not (srtAvailable ()) then
            "Srt: no working confinement (bubblewrap, socat, ripgrep, and — under the strict "
            + "profile — a nested user namespace; an unprivileged container needs "
            + "YESSION_SANDBOX_NESTED=weak)" ]
    if not (List.isEmpty missing) then
        failwithf
            "check: this box cannot host every capability it was asked for:\n  - %s\nRun a tier it can host, or install what is missing."
            (String.concat "\n  - " missing)

// Build the installable from the WORKING TREE and boot it.
//
// Every nix build CI runs goes through a flake, and a flake source copy is what git tracks —
// so no CI job has ever built the tree a developer actually has, and nothing outside this gate
// notices when the two diverge (a `node_modules` symlink from the dev shell landing in the
// derivation; a NuGet FOD hash that no longer matches what a restore produces). release.yml's
// package-nix job stays the check of the pure consumer route; this is the check of yours.
let private buildNixPackage () =
    let out =
        run "nix" [ "build"; "--extra-experimental-features"; "nix-command"
                    "--file"; "nix/worktree.nix"; "nix"
                    "--no-link"; "--print-out-paths"; "--print-build-logs" ]
    let outPath = out.Split '\n' |> Array.last |> fun s -> s.Trim ()
    // A build is not a boot: the wrapped bins resolve their runtime node_modules and the native
    // addons out of the store paths this derivation assembled, and only running them proves it.
    // Every bin the package declares gets booted — a wrapper the derivation forgot to make is a
    // missing file here, and a wrapper that cannot find its addon is a dead process.
    bootSmoke managerReady (Path.Combine (outPath, "bin/yession-manager")) managerSmokeArgs

// A full `verify` spends its first ~80 seconds in restore, build, Fable and stage — tools that
// are either silent or so chatty their own progress reads as scrollback, so the run looks
// hung. One line per stage, named for the stage and not the tool, is enough to tell "working"
// from "wedged"; the stages the caps skip never announce themselves.
let private progress (label: string) = printfn "check: %s" label

let private runCheckOnce (requested: string list) =
    let caps = requested
    requireCapabilities caps
    let capSet = Set.ofList caps
    Environment.SetEnvironmentVariable ("YESSION_TEST_CAPS", String.concat " " caps)
    progress (sprintf "capabilities: %s" (if List.isEmpty caps then "none (cheap tier)" else String.concat " " caps))
    progress "building the solution"
    exec "dotnet" [ "build"; "Yession.slnx" ]

    // Browser output feeds both the host-spawning Node suites and the editor Browser E2E.
    if hasAny capSet [ "Ports"; "Native"; "Docker"; "LiveAgent"; "Browser" ] then
        progress "compiling the browser client"
        fable false "app/browser/Yession.Browser.fsproj" "app/out/browser"

    // Host-spawning Node suites drive the assembled npm package — stage it (compile + bundle).
    // `test` names what this build is; the suites assert the bins report it back.
    if hasAny capSet [ "Ports"; "Native"; "Docker"; "LiveAgent" ] then
        progress "staging the npm package"
        stage "test"

    // The Node (Fable/JS) path — always runs; self-skips suites whose caps/runtime don't match.
    progress "compiling the suite"
    fable false "tests/Yession.Tests/Yession.Tests.fsproj" "tests/Yession.Tests/out"
    progress "running the Node suite"
    runNodeSuite "tests/Yession.Tests/out/Main.js" 240000

    // The .NET CLR (Playwright) path — only when a Browser-tagged suite is enabled.
    if capSet.Contains "Browser" then
        // No browser install step: Chromium comes from the environment
        // (PLAYWRIGHT_BROWSERS_PATH, set by devenv.nix from nixpkgs' playwright-driver), like
        // every other tool the suite needs. `check` used to shell out to `npx playwright
        // install --with-deps`, which fetched from the network and installed system packages
        // as root — mid-test-run, and only ever workable on a CI image.
        Directory.CreateDirectory (Path.Combine (repoRoot, "tests/browser/out")) |> ignore
        exec esbuild [ "app/out/browser/EditorHarness.js"; "--bundle"; "--format=esm"; "--outfile=tests/browser/out/harness.js" ]
        // The harness renders the REAL shell (Plan 14), so it needs the real stylesheet:
        // without it every Tailwind class is inert, and any layout the browser tier measures
        // there — a phone viewport most of all — is a layout nobody will ever get.
        exec tailwind [ "-i"; "app/tailwind.css"; "-o"; "tests/browser/out/app.css" ]
        progress "running the browser suite (.NET CLR)"
        exec "dotnet" [ "run"; "--project"; "tests/Yession.Tests/Yession.Tests.fsproj" ]

    // Last, because it is the long pole (a cold NuGet FOD fetch plus the whole compile again,
    // offline, inside the sandbox) and because the suites are the sharper signal. The Node run
    // above already asserted what the derivation is allowed to SEE (NixSource.fs); this asserts
    // that what it sees still builds and boots.
    if capSet.Contains "Nix" then
        progress "building the Nix package from the working tree"
        buildNixPackage ()

// The Keyring-tagged suites need a usable Secret Service. A desktop has one; a headless Linux
// host (CI, dev containers) has no session bus at all, so `check` wraps ITSELF in a private
// D-Bus session with gnome-keyring's secrets component unlocked by an empty password. The bus
// must be an ancestor of every test process, which a running process cannot arrange for itself
// — hence the re-exec under this script rather than an env tweak.
let private keyringWrapper = """#!/usr/bin/env bash
set -euo pipefail
# The Nix dbus package expects its config in /etc, which headless containers lack — hand the
# daemon the config that ships inside the package instead. dbus-run-session only accepts a
# bare binary name, so wrap the flag in a shim.
dbus_bin="$(readlink -f "$(command -v dbus-daemon)")"
session_conf="$(dirname "$dbus_bin")/../share/dbus-1/session.conf"
shim_dir="$(mktemp -d)"
trap 'rm -rf "$shim_dir"' EXIT
cat > "$shim_dir/dbus-daemon-shim" <<SHIM
#!/usr/bin/env bash
# dbus-run-session passes --session; replace it with the packaged config file.
args=()
for a in "\$@"; do [ "\$a" = "--session" ] || args+=("\$a"); done
exec "$dbus_bin" --config-file="$session_conf" "\${args[@]}"
SHIM
chmod +x "$shim_dir/dbus-daemon-shim"

# dbus-daemon always tries to raise RLIMIT_NOFILE to 65536 and says so when it cannot. Here it
# never can — the container's hard limit is 4096 and raising it needs CAP_SYS_RESOURCE — and the
# daemon carries on regardless, but the line lands third in every `check Keyring` looking like a
# hard failure. Drop that ONE pattern and let every other word reach stderr.
#
# The filter sits out here, on dbus-run-session, and NOT inside the shim: a process substitution
# in the shim would hand `grep` a copy of the fd dbus-daemon prints the bus address on, so
# dbus-run-session would wait forever for an EOF that a live grep is holding open.
exec dbus-run-session --dbus-daemon="$shim_dir/dbus-daemon-shim" -- bash -euo pipefail -c '
  # An isolated keyring home so runs never touch (or depend on) a real user keyring.
  export XDG_DATA_HOME="$(mktemp -d)"
  export XDG_RUNTIME_DIR="$XDG_DATA_HOME/runtime"
  mkdir -p "$XDG_RUNTIME_DIR"
  chmod 700 "$XDG_RUNTIME_DIR"
  # Unlock with an empty (newline) password on stdin — the keyring-rs CI recipe: this
  # creates + unlocks the login collection and its "default" alias on first run.
  eval "$(printf "\n" | gnome-keyring-daemon --unlock | sed "s/^/export /")"
  exec "$@"
' -- "$@" 2> >(grep --line-buffered -v 'Failed to set fd limit to' >&2)
"""

let private needsKeyringWrap (caps: string list) =
    List.contains "Keyring" caps
    && OperatingSystem.IsLinux ()
    && String.IsNullOrEmpty (Environment.GetEnvironmentVariable "DBUS_SESSION_BUS_ADDRESS")
    && Environment.GetEnvironmentVariable "YESSION_KEYRING_WRAPPED" <> "1"

let private rerunUnderKeyring (caps: string list) : int =
    for tool in [ "dbus-run-session"; "gnome-keyring-daemon" ] do
        if runInherit repoRoot "bash" [ "-c"; sprintf "command -v %s >/dev/null" tool ] <> 0 then
            failwithf "check Keyring: %s not found — it backs the headless Secret Service (devenv provides it; see devenv.nix packages)" tool
    let script = Path.Combine (Path.GetTempPath (), sprintf "yession-keyring-%s.sh" (Path.GetRandomFileName ()))
    File.WriteAllText (script, keyringWrapper)
    try
        Environment.SetEnvironmentVariable ("YESSION_KEYRING_WRAPPED", "1")
        runInherit repoRoot "bash" ([ script; "dotnet"; "fsi"; "tasks.fsx"; "check" ] @ caps)
    finally
        File.Delete script

// check [caps…]. Default = cheap tier; each cap adds its suites (Browser, Ports, Native, …).
// The gate runs once and is deterministic — the native WebRTC suites used to abort intermittently,
// but that was a real defect (the addon carried its own C++ runtime; see nix/node-datachannel.nix),
// now fixed, not inherent flakiness. A failure here is a genuine break, so don't paper it over.
let check (caps: string list) =
    if needsKeyringWrap caps then exit (rerunUnderKeyring caps)
    restore ()
    runCheckOnce caps

let verify () =
    check
        [ "Browser"; "Ports"; "Native"; "Docker"; "LiveAgent"; "Keyring"; "Nix"; "Srt"; "Pty"; "Serial"
          "Jumpstarter" ]

// --- lint: the GitHub Actions workflows -------------------------------------------------------

// A workflow file is only validated by GitHub when it RUNS. release.yml runs on master — after a
// PR has merged — so a syntax error in it is invisible to PR CI and lands already broken: a
// colon-space in an unquoted step name once stopped the whole file parsing, and every master
// release failed at startup (zero jobs) until it was fixed. actionlint parses every workflow and
// type-checks its expressions, so the PR gate can catch that class of break before it merges.
// Run from repoRoot with no arguments, it finds .github/workflows itself.
let lint () = exec "actionlint" []

// --- clean -----------------------------------------------------------------------------------

let clean () =
    for d in [ "node_modules"; "app/out"; "tests/Yession.Tests/out"; "dist" ] do
        let p = Path.Combine (repoRoot, d)
        if Directory.Exists p then
            // A symlinked node_modules is the Nix-managed tree — immutable and always fresh, so
            // leave it (removing the link would strand the shell without its addon-baked deps).
            // A real dir (off-Nix npm install) is cleaned normally.
            if (DirectoryInfo p).LinkTarget = null then Directory.Delete (p, true)
    // .NET build outputs live BESIDE a project file, so find the projects and delete THEIR
    // bin/obj — rather than deleting every directory in the tree that happens to carry the
    // name. A name match is a guess about where output lives; a project's own output
    // directory is not, and the guess had teeth: this walk used to follow `.devenv/profile`
    // into the Nix store and delete the devenv profile's `bin`, 806 tool symlinks, after
    // which every devenv invocation failed until the store path was repaired.
    //
    // Symlinks and dot-directories are skipped, which is one rule and not two: this
    // repository's build outputs are inside this repository, and both are paths out of it.
    // `node_modules` is named as well because off Nix it is a real directory, full of `bin`.
    let rec projects dir = seq {
        yield! Directory.GetFiles (dir, "*.fsproj")
        for sub in Directory.GetDirectories dir do
            let name = Path.GetFileName sub
            if (DirectoryInfo sub).LinkTarget = null
               && not (name.StartsWith ".")
               && name <> "node_modules" then yield! projects sub
    }
    for project in projects repoRoot do
        for out in [ "bin"; "obj" ] do
            let p = Path.Combine (Path.GetDirectoryName project, out)
            if Directory.Exists p && (DirectoryInfo p).LinkTarget = null then Directory.Delete (p, true)

// Reused CI runners must not leak session containers/volumes between jobs. Best-effort.
let cleanDocker () =
    let lines (s: string) = s.Split('\n') |> Array.map (fun x -> x.Trim ()) |> Array.filter (fun x -> x <> "")
    try
        for id in lines (run "docker" [ "ps"; "-aq"; "--filter"; "label=yession-session" ]) do
            runInherit repoRoot "docker" [ "rm"; "-f"; id ] |> ignore
        for v in lines (run "docker" [ "volume"; "ls"; "-q"; "--filter"; "label=yession-session" ]) do
            runInherit repoRoot "docker" [ "volume"; "rm"; v ] |> ignore
    with ex -> eprintfn "clean-docker: %s" ex.Message

// --- dispatch --------------------------------------------------------------------------------

let argv = fsi.CommandLineArgs
let arg i = argv |> Array.tryItem i
let rest i = if argv.Length > i then argv.[i..] |> Array.toList else []

match arg 1 with
| Some "compile" -> compile ()
| Some "restore" -> restore ()
| Some "build" -> build ()
| Some "start" -> start ()
| Some "dev" -> dev ()
| Some "version" -> printfn "%s" (defaultVersion ())
| Some "stage" -> stage (arg 2 |> Option.defaultWith defaultVersion)
| Some "check" -> check (rest 2)
| Some "verify" -> verify ()
| Some "lint" -> lint ()
| Some "package" -> package (arg 2 |> Option.defaultWith defaultVersion)
| Some "install-smoke" ->
    match arg 2 with
    | Some tgz -> installSmoke tgz
    | None -> failwith "install-smoke <tgz>"
| Some "boot-smoke" ->
    match rest 2 with
    | ready :: cmd :: cmdArgs -> bootSmoke ready cmd cmdArgs
    | _ -> failwith "boot-smoke <ready-line> <command…>"
| Some "example" ->
    match arg 2 with
    | Some name -> example name
    | None -> failwith "example <name>   (see examples/)"
| Some "clean" -> clean ()
| Some "clean-docker" -> cleanDocker ()
| Some version -> package version // backwards compat: `tasks.fsx <version>` == `package <version>`
| None -> package (defaultVersion ())
