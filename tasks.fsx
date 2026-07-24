// The complete, standalone build interface for Yession. Every Yession-specific build/test/
// package function lives here so that devenv (devenv.nix scripts) and CI (.github/workflows)
// are thin wrappers — you can throw both away and still drive everything directly with
// `dotnet fsi tasks.fsx <verb>`. Only tool-specific glue (nix/devenv bootstrap, actions/*
// steps, artifact upload) stays in the callers; no Yession logic does.
//
// The verbs are the dispatch at the bottom of this file; each has its own section below. A
// bare version (`dotnet fsi tasks.fsx 1.2.3`) is shorthand for `package`.
//
// Yession ships as ONE npm package with two bins, `yession` (the Manager) and
// `yession-session` (a Session Process). Both entries are esbuild-bundled to single ESM files
// with the native / self-resolving deps kept EXTERNAL — node-datachannel loads its addon and
// the Agent SDK resolves its native `claude` sibling via import.meta.url, neither of which
// works bundled. Assets are copied in and read package-relative at runtime.

open System
open System.Diagnostics
open System.IO

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

// --- version: the GitVersion-style continuous-delivery number --------------------------------

let gitVersion () = sprintf "1.0.0-beta.%s" (run "git" [ "rev-list"; "--count"; "HEAD" ])

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

// --- compile: F# -> JS (both entries), the browser client bundle, and the stylesheet --------

let compile () =
    printfn "compiling F# -> JS"
    run "dotnet" [ "build"; "Yession.slnx" ] |> ignore
    run "dotnet" [ "fable"; "app/main/Yession.Host.Main.fsproj"; "-o"; "app/out" ] |> ignore
    run "dotnet" [ "fable"; "app/browser/Yession.Browser.fsproj"; "-o"; "app/out/browser" ] |> ignore
    run esbuild [ "app/out/browser/Browser.js"; "--bundle"; "--format=esm"; "--minify"; "--outfile=app/out/public/client.js" ] |> ignore
    // Tailwind, built locally into a served stylesheet (no CDN); scans the F# sources.
    run tailwind [ "-i"; "app/tailwind.css"; "-o"; "app/out/public/app.css"; "--minify" ] |> ignore

let build () =
    restore ()
    compile ()

// --- start / dev: run the Session Process locally --------------------------------------------

let start () =
    build ()
    exec "node" [ "app/out/Main.js" ]

let dev () =
    restore ()
    exec "dotnet" [ "fable"; "watch"; "app/main/Yession.Host.Main.fsproj"; "-o"; "app/out"; "--runWatch"; "node"; "app/out/Main.js" ]

// --- stage: bundle the two bins (deps external) and assemble dist/npm ------------------------

// node-datachannel (native addon) and @anthropic-ai/claude-agent-sdk (resolves its own native
// `claude` sibling via import.meta.url) MUST NOT be bundled — they only work from their real
// node_modules. zod is a dynamic import shared with the SDK; dockerode is pure JS but pulls
// ssh2 (with an optional native addon), so it resolves from node_modules too. Everything else
// (yjs, lib0, Thoth, prosemirror, …) inlines.
let private externals =
    [ "node-datachannel"; "@anthropic-ai/claude-agent-sdk"; "zod"; "dockerode" ]
    |> List.map (sprintf "--external:%s")

// The OTel SDK does a dynamic `require('util')`; esbuild's ESM output can't satisfy a runtime
// `require`, so restore a real one at the top of each bundle via createRequire.
let private banner =
    "--banner:js=import { createRequire as __createRequire } from 'module'; const require = __createRequire(import.meta.url);"

let private bundle (entry: string) (outFile: string) =
    run esbuild
        ([ Path.Combine (repoRoot, entry); "--bundle"; "--platform=node"; "--format=esm"; banner ]
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
    "dockerode": "%s",
    "node-datachannel": "%s",
    "zod": "%s"
  }
}
"""
        version
        (depVersion "@anthropic-ai/claude-agent-sdk")
        (depVersion "dockerode")
        (depVersion "node-datachannel")
        (depVersion "zod")

let stage (version: string) =
    compile ()
    printfn "staging yession %s (npm, one package / two bins) -> dist/npm" version

    for required in [ "app/out/Main.js"; "app/SessionMain.js"; "app/out/public/client.js"; "app/out/public/app.css" ] do
        if not (File.Exists (Path.Combine (repoRoot, required))) then
            failwithf "missing %s after compile" required

    if Directory.Exists pkg then Directory.Delete (pkg, true)
    Directory.CreateDirectory pkg |> ignore
    Directory.CreateDirectory (Path.Combine (pkg, "bin")) |> ignore
    Directory.CreateDirectory (Path.Combine (pkg, "assets")) |> ignore

    bundle "app/out/Main.js" "manager.js"
    bundle "app/SessionMain.js" "session.js"

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

// Reused by `package`, `install-smoke`, and CI's nix-package job: spawn the given command
// with an ephemeral data dir + port 0, and assert it logs "management UI at" before a deadline.
// A bin that cannot boot never passes the gate.
let bootSmoke (command: string) (arguments: string list) =
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
    psi.EnvironmentVariables.["YESSION_PORT"] <- "0"
    psi.EnvironmentVariables.["YESSION_MANAGER_PORT"] <- "0"
    let p = Process.Start psi

    try
        let mutable ready = false
        let deadline = DateTime.UtcNow.AddSeconds 30.0
        while not ready && DateTime.UtcNow < deadline && not p.HasExited do
            let line = p.StandardOutput.ReadLine ()
            if line <> null then
                printfn "[smoke] %s" line
                if line.Contains "management UI at" then ready <- true
        if not ready then failwithf "boot-smoke: %s never reported readiness" command
        printfn "boot-smoke: %s booted and served both surfaces" command
    finally
        try p.Kill true with _ -> ()

// --- package: restore + stage + boot smoke + npm pack ----------------------------------------

let package (version: string) =
    restore ()
    stage version
    // Boot the packaged bin shim (it self-sets YESSION_SESSION_MAIN); externals resolve from the
    // repo node_modules two levels up from dist/npm.
    bootSmoke "node" [ Path.Combine (pkg, "bin/yession-manager.js") ]
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

    bootSmoke "node" [ Path.Combine (prefix, "node_modules/.bin/yession-manager") ]
    printfn "install-smoke: native deps resolved and the installed package booted"

// --- check: capability-gated test orchestration ----------------------------------------------

// Install the Playwright chromium the browser E2Es drive — idempotent, and skipped when
// PLAYWRIGHT_BROWSERS_PATH already carries one (the sandbox preinstalls it there).
let private ensureBrowser () =
    let path = Environment.GetEnvironmentVariable "PLAYWRIGHT_BROWSERS_PATH"
    let hasChromium =
        not (String.IsNullOrEmpty path)
        && Directory.Exists path
        && (Directory.GetDirectories path |> Array.exists (fun d -> (Path.GetFileName d).StartsWith "chromium"))
    if not hasChromium then
        exec "npx" [ "--yes"; "playwright@1.61.1"; "install"; "--with-deps"; "chromium" ]

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

let private runCheckOnce (caps: string list) =
    let capSet = Set.ofList caps
    Environment.SetEnvironmentVariable ("YESSION_TEST_CAPS", String.concat " " caps)
    exec "dotnet" [ "build"; "Yession.slnx" ]

    // Browser output feeds both the host-spawning Node suites and the editor Browser E2E.
    if hasAny capSet [ "Ports"; "Native"; "Docker"; "LiveAgent"; "Browser" ] then
        exec "dotnet" [ "fable"; "app/browser/Yession.Browser.fsproj"; "-o"; "app/out/browser" ]

    // Host-spawning Node suites drive the assembled npm package — stage it (compile + bundle).
    if hasAny capSet [ "Ports"; "Native"; "Docker"; "LiveAgent" ] then
        stage "0.0.0-test"

    // The Node (Fable/JS) path — always runs; self-skips suites whose caps/runtime don't match.
    exec "dotnet" [ "fable"; "tests/Yession.Tests/Yession.Tests.fsproj"; "-o"; "tests/Yession.Tests/out" ]
    runNodeSuite "tests/Yession.Tests/out/Main.js" 240000

    // The .NET CLR (Playwright) path — only when a Browser-tagged suite is enabled.
    if capSet.Contains "Browser" then
        ensureBrowser ()
        Directory.CreateDirectory (Path.Combine (repoRoot, "tests/browser/out")) |> ignore
        exec esbuild [ "app/out/browser/EditorHarness.js"; "--bundle"; "--format=esm"; "--outfile=tests/browser/out/harness.js" ]
        exec "dotnet" [ "run"; "--project"; "tests/Yession.Tests/Yession.Tests.fsproj" ]

// check [caps…]. Default = cheap tier; each cap adds its suites (Browser, Ports, Native, …).
// The gate runs once and is deterministic — the native WebRTC suites used to abort intermittently,
// but that was a real defect (the addon carried its own C++ runtime; see nix/node-datachannel.nix),
// now fixed, not inherent flakiness. A failure here is a genuine break, so don't paper it over.
let check (caps: string list) =
    restore ()
    runCheckOnce caps

let verify () = check [ "Browser"; "Ports"; "Native"; "Docker"; "LiveAgent" ]

// --- clean -----------------------------------------------------------------------------------

let clean () =
    for d in [ "node_modules"; "app/out"; "tests/Yession.Tests/out"; "dist" ] do
        let p = Path.Combine (repoRoot, d)
        if Directory.Exists p then
            // A symlinked node_modules is the Nix-managed tree — immutable and always fresh, so
            // leave it (removing the link would strand the shell without its addon-baked deps).
            // A real dir (off-Nix npm install) is cleaned normally.
            if (DirectoryInfo p).LinkTarget = null then Directory.Delete (p, true)
    // Sweep the F#/.NET build dirs, without descending into deps or git.
    let rec sweep dir =
        for sub in Directory.GetDirectories dir do
            match Path.GetFileName sub with
            | "bin" | "obj" | "fable_modules" -> Directory.Delete (sub, true)
            | "node_modules" | ".git" -> ()
            | _ -> sweep sub
    sweep repoRoot

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
| Some "version" -> printfn "%s" (gitVersion ())
| Some "stage" -> stage (arg 2 |> Option.defaultValue "0.0.0-dev")
| Some "check" -> check (rest 2)
| Some "verify" -> verify ()
| Some "package" -> package (arg 2 |> Option.defaultValue "0.0.0-dev")
| Some "install-smoke" ->
    match arg 2 with
    | Some tgz -> installSmoke tgz
    | None -> failwith "install-smoke <tgz>"
| Some "boot-smoke" ->
    match rest 2 with
    | cmd :: cmdArgs -> bootSmoke cmd cmdArgs
    | [] -> failwith "boot-smoke <command…>"
| Some "clean" -> clean ()
| Some "clean-docker" -> cleanDocker ()
| Some version -> package version // backwards compat: `tasks.fsx <version>` == `package <version>`
| None -> package "0.0.0-dev"
