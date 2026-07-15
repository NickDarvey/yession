// Packaging (Phase 4, Step 26): two Node single-file executables via Node's built-in
// SEA (single executable applications), orchestrated in F# — the repo's only scripting
// language. Run through mise so the pinned Node is on PATH:
//
//     mise exec -- dotnet fsi scripts/build.fsx 1.0.0-beta.42
//
// Per binary: esbuild-bundle the Fable output to ONE CommonJS file (SEA requires a CJS
// entry) with `node-datachannel` aliased to a shim that, inside a SEA, extracts the
// native addon from the blob's assets to a per-version cache directory and dlopens it
// (native addons cannot load from inside a SEA blob); generate the SEA blob; copy the
// pinned node binary; inject with postject. The result is smoke-tested (the manager
// binary spawns the session binary and serves both surfaces) before it is tarred.

open System
open System.Diagnostics
open System.IO
open System.Net.Http

let version =
    match fsi.CommandLineArgs |> Array.tryItem 1 with
    | Some v -> v
    | None -> "0.0.0-dev"

let repoRoot = Path.GetFullPath (Path.Combine (__SOURCE_DIRECTORY__, ".."))
let dist = Path.Combine (repoRoot, "dist")
let seaDir = Path.Combine (dist, "sea")

let run (command: string) (arguments: string list) : string =
    let psi = ProcessStartInfo (command)
    arguments |> List.iter psi.ArgumentList.Add
    psi.WorkingDirectory <- repoRoot
    psi.RedirectStandardOutput <- true
    use p = Process.Start psi
    let output = p.StandardOutput.ReadToEnd ()
    p.WaitForExit ()
    if p.ExitCode <> 0 then failwithf "%s %s failed (%d)" command (String.concat " " arguments) p.ExitCode
    output.Trim ()

let platform = run "node" [ "-p"; "process.platform + '-' + process.arch" ]
let nodeBinary = run "node" [ "-p"; "process.execPath" ]

printfn "packaging %s for %s (node: %s)" version platform nodeBinary

// --- Preconditions: the Fable outputs `mise run build` produces --------------------------

for required in [ "app/out/Main.js"; "app/SessionMain.js"; "app/out/public/client.js" ] do
    if not (File.Exists (Path.Combine (repoRoot, required))) then
        failwithf "missing %s — run `mise run build` first" required

Directory.CreateDirectory seaDir |> ignore

// --- The native-addon shim ----------------------------------------------------------------
// Inside a SEA, `node-datachannel` resolves to this: extract the prebuilt addon from
// the blob assets into a per-version cache (never next to the binary — installs may be
// read-only) and dlopen it. The raw addon exports everything the host uses.

let shimPath = Path.Combine (seaDir, "node-datachannel-shim.cjs")

File.WriteAllText (
    shimPath,
    $"""'use strict'
const path = require('node:path')
const fs = require('node:fs')
const os = require('node:os')
const sea = require('node:sea')
const cacheDir = path.join(os.homedir() || os.tmpdir(), '.yession', 'native', '{version}-{platform}')
const binPath = path.join(cacheDir, 'node_datachannel.node')
if (!fs.existsSync(binPath)) {{
  fs.mkdirSync(cacheDir, {{ recursive: true }})
  fs.writeFileSync(binPath, Buffer.from(sea.getAsset('node_datachannel.node')))
}}
const mod = {{ exports: {{}} }}
process.dlopen(mod, binPath)
module.exports = mod.exports
""")

// --- Bundle, blob, inject -------------------------------------------------------------------

let esbuild = Path.Combine (repoRoot, "node_modules", ".bin", "esbuild")
let postject = Path.Combine (repoRoot, "node_modules", ".bin", "postject")

type Binary =
    { Name : string
      Entry : string
      Assets : (string * string) list }

let binaries =
    [ { Name = "yession"
        Entry = "app/out/Main.js"
        Assets =
          [ "node_datachannel.node", "node_modules/node-datachannel/build/Release/node_datachannel.node"
            "htmx.min.js", "node_modules/htmx.org/dist/htmx.min.js" ] }
      { Name = "yession-session"
        Entry = "app/SessionMain.js"
        Assets =
          [ "node_datachannel.node", "node_modules/node-datachannel/build/Release/node_datachannel.node"
            "client.js", "app/out/public/client.js" ] } ]

let stage = Path.Combine (dist, sprintf "yession-%s-%s" version platform)
if Directory.Exists stage then Directory.Delete (stage, true)
Directory.CreateDirectory stage |> ignore

for binary in binaries do
    let bundle = Path.Combine (seaDir, binary.Name + ".cjs")
    run esbuild
        [ Path.Combine (repoRoot, binary.Entry)
          "--bundle"
          "--platform=node"
          "--format=cjs"
          sprintf "--alias:node-datachannel=%s" shimPath
          sprintf "--outfile=%s" bundle ]
    |> ignore

    let blob = Path.Combine (seaDir, binary.Name + ".blob")
    let assetsJson =
        binary.Assets
        |> List.map (fun (name, path) -> sprintf "\"%s\": \"%s\"" name (Path.Combine (repoRoot, path)))
        |> String.concat ", "
    let configPath = Path.Combine (seaDir, binary.Name + ".sea.json")
    File.WriteAllText (
        configPath,
        sprintf
            """{ "main": "%s", "output": "%s", "disableExperimentalSEAWarning": true, "assets": { %s } }"""
            bundle blob assetsJson)
    run "node" [ "--experimental-sea-config"; configPath ] |> ignore

    let target = Path.Combine (stage, binary.Name)
    File.Copy (nodeBinary, target, true)
    let machoArgs = if platform.StartsWith "darwin" then [ "--macho-segment-name"; "NODE_SEA" ] else []
    run postject ([ target; "NODE_SEA_BLOB"; blob; "--sentinel-fuse"; "NODE_SEA_FUSE_fce680ab2cc467b6e072b8b5df1996b2" ] @ machoArgs)
    |> ignore
    run "chmod" [ "+x"; target ] |> ignore
    printfn "built %s" target

// --- Boot smoke on the packaged artifacts ---------------------------------------------------
// The manager binary must spawn the session binary (its sibling) from a clean data
// directory and serve both surfaces. This gates packaging: a binary that cannot boot
// never reaches a release.

let smokeData = Path.Combine (seaDir, "smoke-data")
if Directory.Exists smokeData then Directory.Delete (smokeData, true)

let smoke = ProcessStartInfo (Path.Combine (stage, "yession"))
smoke.WorkingDirectory <- repoRoot
smoke.RedirectStandardOutput <- true
smoke.EnvironmentVariables.["YESSION_DATA_DIR"] <- smokeData
smoke.EnvironmentVariables.["YESSION_PORT"] <- "0"
smoke.EnvironmentVariables.["YESSION_MANAGER_PORT"] <- "0"
let smokeProcess = Process.Start smoke

try
    let mutable sessionUrl = None
    let mutable uiUrl = None
    let deadline = DateTime.UtcNow.AddSeconds 30.0
    while (Option.isNone sessionUrl || Option.isNone uiUrl) && DateTime.UtcNow < deadline && not smokeProcess.HasExited do
        let line = smokeProcess.StandardOutput.ReadLine ()
        if line <> null then
            printfn "[smoke] %s" line
            let urlOf (marker: string) =
                if line.Contains marker then
                    let index = line.IndexOf "http://"
                    if index >= 0 then Some (line.Substring(index).Split(' ').[0].TrimEnd('/') + "/") else None
                else None
            match urlOf "launched at" with Some u -> sessionUrl <- Some u | None -> ()
            match urlOf "management UI at" with Some u -> uiUrl <- Some u | None -> ()

    match sessionUrl, uiUrl with
    | Some sessionUrl, Some uiUrl ->
        use http = new HttpClient ()
        let shell = http.GetStringAsync(sessionUrl).Result
        if not (shell.Contains "yession-session") then failwith "smoke: the session binary did not serve the client shell"
        let ui = http.GetStringAsync(uiUrl).Result
        if not (ui.Contains "data-create-session") then failwith "smoke: the manager binary did not serve the management UI"
        printfn "smoke: both binaries booted, spawned, and served"
    | _ ->
        failwith "smoke: the packaged manager never reported readiness"
finally
    try smokeProcess.Kill (true) with _ -> ()

// --- Artifact ---------------------------------------------------------------------------------

File.Copy (Path.Combine (repoRoot, "README.md"), Path.Combine (stage, "README.md"), true)
let tarball = sprintf "yession-%s-%s.tar.gz" version platform
run "tar" [ "-czf"; Path.Combine (dist, tarball); "-C"; dist; Path.GetFileName stage ] |> ignore
printfn "packaged dist/%s" tarball
