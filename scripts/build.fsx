// The build + package authority. One place owns compiling F#→JS and assembling the npm
// package; devenv scripts and the Nix package (nix/yession.nix) both delegate here so the
// logic is never duplicated. Modes:
//
//     dotnet fsi scripts/build.fsx compile            # Fable-compile both entries, bundle the
//                                                     # browser client, build the stylesheet
//     dotnet fsi scripts/build.fsx stage   [version]  # compile + bundle the two bins + assemble
//                                                     # dist/npm (no smoke, no pack) — for Nix
//     dotnet fsi scripts/build.fsx package [version]  # stage + boot smoke + npm pack (the .tgz)
//
// `dotnet fsi scripts/build.fsx <version>` (no mode) is treated as `package <version>` for
// backwards compatibility. Output: dist/npm/ (staging) and dist/yession-<version>.tgz.
//
// Yession ships as ONE npm package with two bins, `yession` (the Manager) and
// `yession-session` (a Session Process). The two entries are esbuild-bundled to single ESM
// files with the native / self-resolving deps kept EXTERNAL — node-datachannel loads its
// addon and the Agent SDK resolves its native `claude` sibling via import.meta.url, both of
// which only work from a real node_modules, never bundled. Assets (the client bundle, the
// stylesheet) are copied in and read package-relative at runtime.

open System
open System.Diagnostics
open System.IO

let repoRoot = Path.GetFullPath (Path.Combine (__SOURCE_DIRECTORY__, ".."))
let dist = Path.Combine (repoRoot, "dist")
let pkg = Path.Combine (dist, "npm")
let binDir = Path.Combine (repoRoot, "node_modules", ".bin")
let esbuild = Path.Combine (binDir, "esbuild")
let tailwind = Path.Combine (binDir, "tailwindcss")

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

// --- compile: F# -> JS (both entries), the browser client bundle, and the stylesheet --------

let compile () =
    printfn "compiling F# -> JS"
    run "dotnet" [ "build"; "Yession.slnx" ] |> ignore
    run "dotnet" [ "fable"; "app/main/Yession.Host.Main.fsproj"; "-o"; "app/out" ] |> ignore
    run "dotnet" [ "fable"; "app/browser/Yession.Browser.fsproj"; "-o"; "app/out/browser" ] |> ignore
    run esbuild [ "app/out/browser/Browser.js"; "--bundle"; "--format=esm"; "--minify"; "--outfile=app/out/public/client.js" ] |> ignore
    // Tailwind, built locally into a served stylesheet (no CDN); scans the F# sources.
    run tailwind [ "-i"; "app/tailwind.css"; "-o"; "app/out/public/app.css"; "--minify" ] |> ignore

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
let private yessionBinJs = """#!/usr/bin/env node
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
    "yession": "bin/yession.js",
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

    // Bin shims. `yession` points the Manager at the packaged session bundle (both live in one
    // install), so it spawns `node session.js` with no PATH assumptions.
    File.WriteAllText (Path.Combine (pkg, "bin/yession.js"), yessionBinJs)
    File.WriteAllText (Path.Combine (pkg, "bin/yession-session.js"), yessionSessionBinJs)
    File.WriteAllText (Path.Combine (pkg, "package.json"), packageJson version)
    File.Copy (Path.Combine (repoRoot, "README.md"), Path.Combine (pkg, "README.md"), true)

// --- package: stage + boot smoke + npm pack --------------------------------------------------

let private smoke () =
    // Run the packaged manager.js against the repo's node_modules (the deps npm would install
    // are present): it must spawn the session bundle and serve both surfaces. Gates packaging —
    // a bundle that cannot boot never gets packed.
    let smokeData = Path.Combine (dist, "npm-smoke-data")
    if Directory.Exists smokeData then Directory.Delete (smokeData, true)

    let smoke = ProcessStartInfo (run "node" [ "-p"; "process.execPath" ])
    smoke.ArgumentList.Add (Path.Combine (pkg, "manager.js"))
    smoke.WorkingDirectory <- repoRoot
    smoke.RedirectStandardOutput <- true
    smoke.EnvironmentVariables.["YESSION_DATA_DIR"] <- smokeData
    smoke.EnvironmentVariables.["YESSION_PORT"] <- "0"
    smoke.EnvironmentVariables.["YESSION_MANAGER_PORT"] <- "0"
    smoke.EnvironmentVariables.["YESSION_SESSION_MAIN"] <- Path.Combine (pkg, "session.js")
    let smokeProcess = Process.Start smoke

    try
        let mutable ready = false
        let deadline = DateTime.UtcNow.AddSeconds 30.0
        while not ready && DateTime.UtcNow < deadline && not smokeProcess.HasExited do
            let line = smokeProcess.StandardOutput.ReadLine ()
            if line <> null then
                printfn "[smoke] %s" line
                if line.Contains "management UI at" then ready <- true
        if not ready then failwith "smoke: the packaged manager never reported readiness"
        printfn "smoke: the packaged bundles booted and composed"
    finally
        try smokeProcess.Kill (true) with _ -> ()

let package (version: string) =
    stage version
    smoke ()
    let packed = runIn pkg "npm" [ "pack"; "--pack-destination"; dist ] |> fun out -> out.Split('\n') |> Array.last
    printfn "packaged dist/%s" (Path.GetFileName (packed.Trim ()))

// --- dispatch --------------------------------------------------------------------------------

let arg i = fsi.CommandLineArgs |> Array.tryItem i

match arg 1 with
| Some "compile" -> compile ()
| Some "stage" -> stage (arg 2 |> Option.defaultValue "0.0.0-dev")
| Some "package" -> package (arg 2 |> Option.defaultValue "0.0.0-dev")
| Some version -> package version // backwards compat: `build.fsx <version>` == `package <version>`
| None -> package "0.0.0-dev"
