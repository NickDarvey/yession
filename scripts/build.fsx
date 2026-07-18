// Packaging (Phase 4, Step 28): ship Yession as ONE npm package with two bins,
// `yession` (the Manager) and `yession-session` (the Session Process). npm's
// optionalDependencies resolve the platform-specific native binaries at install time —
// both `node-datachannel` AND the native `claude` executable the Agent SDK spawns — so
// installing the package is all it takes; nothing native is bundled or downloaded by
// us. Node is a required runtime (`engines`). Run through mise for the pinned Node:
//
//     mise exec -- dotnet fsi scripts/build.fsx 1.0.0-beta.42
//
// Output: `dist/npm/` (the package staging) and `dist/yession-<version>.tgz` from
// `npm pack`. The two entries are esbuild-bundled to single ESM files with the native
// / self-resolving deps kept EXTERNAL — node-datachannel loads its addon, and the Agent
// SDK resolves its own native `claude` sibling via import.meta.url, both of which only
// work when they run from their real node_modules, never bundled. Assets (the client
// bundle, the stylesheet) are copied in and read package-relative at runtime.

open System
open System.Diagnostics
open System.IO

let version =
    match fsi.CommandLineArgs |> Array.tryItem 1 with
    | Some v -> v
    | None -> "0.0.0-dev"

let repoRoot = Path.GetFullPath (Path.Combine (__SOURCE_DIRECTORY__, ".."))
let dist = Path.Combine (repoRoot, "dist")
let pkg = Path.Combine (dist, "npm")

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

printfn "packaging yession %s (npm, one package / two bins)" version

// --- Preconditions: the Fable outputs `mise run build` produces --------------------------

for required in [ "app/out/Main.js"; "app/SessionMain.js"; "app/out/public/client.js"; "app/out/public/app.css" ] do
    if not (File.Exists (Path.Combine (repoRoot, required))) then
        failwithf "missing %s — run `mise run build` first" required

if Directory.Exists pkg then Directory.Delete (pkg, true)
Directory.CreateDirectory pkg |> ignore
Directory.CreateDirectory (Path.Combine (pkg, "bin")) |> ignore
Directory.CreateDirectory (Path.Combine (pkg, "assets")) |> ignore

// --- Bundle each entry to one ESM file, deps kept external where they must stay --------
// node-datachannel (native addon) and @anthropic-ai/claude-agent-sdk (resolves its own
// native `claude` sibling via import.meta.url) MUST NOT be bundled — they only work from
// their real node_modules. zod is a dynamic import shared with the SDK. Everything else
// (yjs, lib0, Thoth) inlines.

let esbuild = Path.Combine (repoRoot, "node_modules", ".bin", "esbuild")
let externals =
    // dockerode is kept external too: it's pure JS but pulls ssh2 (with an optional native
    // addon), so it resolves from node_modules rather than being bundled.
    [ "node-datachannel"; "@anthropic-ai/claude-agent-sdk"; "zod"; "dockerode" ]
    |> List.map (sprintf "--external:%s")

// The OTel SDK (@opentelemetry/core) does a dynamic `require('util')`; esbuild's ESM output
// can't satisfy a runtime `require`, so it emits a shim that throws "Dynamic require of ...".
// Restore a real `require` at the top of each bundle via createRequire — esbuild's own
// `__require` helper delegates to it when a top-level `require` exists.
let banner =
    "--banner:js=import { createRequire as __createRequire } from 'module'; const require = __createRequire(import.meta.url);"

let bundle (entry: string) (outFile: string) =
    run esbuild
        ([ Path.Combine (repoRoot, entry); "--bundle"; "--platform=node"; "--format=esm"; banner ]
         @ externals
         @ [ sprintf "--outfile=%s" (Path.Combine (pkg, outFile)) ])
    |> ignore

bundle "app/out/Main.js" "manager.js"
bundle "app/SessionMain.js" "session.js"

// --- Assets (read package-relative at runtime by Interop.readAsset) ----------------------

File.Copy (Path.Combine (repoRoot, "app/out/public/client.js"), Path.Combine (pkg, "assets/client.js"), true)
File.Copy (Path.Combine (repoRoot, "app/out/public/app.css"), Path.Combine (pkg, "assets/app.css"), true)

// --- Bin shims ----------------------------------------------------------------------------
// `yession` points the Manager at the packaged session bundle (both live in one install),
// so the Manager spawns `node session.js` with no PATH assumptions.

File.WriteAllText (
    Path.Combine (pkg, "bin/yession.js"),
    """#!/usr/bin/env node
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'
process.env.YESSION_SESSION_MAIN ||= join(dirname(fileURLToPath(import.meta.url)), '..', 'session.js')
import('../manager.js')
""")

File.WriteAllText (
    Path.Combine (pkg, "bin/yession-session.js"),
    """#!/usr/bin/env node
import('../session.js')
""")

// --- package.json ------------------------------------------------------------------------
// Runtime deps are exactly the externals; npm resolves their platform-specific native
// optionalDependencies (node-datachannel's addon, the SDK's native `claude`) on install.

let depVersion (name: string) =
    let json = File.ReadAllText (Path.Combine (repoRoot, "package.json"))
    let marker = sprintf "\"%s\":" name
    let start = json.IndexOf marker + marker.Length
    let quote1 = json.IndexOf ('"', start)
    let quote2 = json.IndexOf ('"', quote1 + 1)
    json.Substring (quote1 + 1, quote2 - quote1 - 1)

File.WriteAllText (
    Path.Combine (pkg, "package.json"),
    sprintf
        """{
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
        (depVersion "zod"))

File.Copy (Path.Combine (repoRoot, "README.md"), Path.Combine (pkg, "README.md"), true)

// --- Boot smoke on the assembled bundles --------------------------------------------------
// Run the packaged manager.js against the repo's node_modules (the deps npm would
// install are present): it must spawn the session bundle and serve both surfaces. This
// gates packaging — a bundle that cannot boot never gets packed.

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

// --- npm pack -----------------------------------------------------------------------------

let packed = runIn pkg "npm" [ "pack"; "--pack-destination"; dist ] |> fun out -> out.Split('\n') |> Array.last
printfn "packaged dist/%s" (Path.GetFileName (packed.Trim ()))
