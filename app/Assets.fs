module Yession.Host.Assets

// The static asset service: the files a build ships, read once and served by path, by whichever
// process is running. Both bins use it — the Session Process for the client shell, the Manager
// for its own page — and neither one names a single asset.
//
// That ignorance is the point. A session may be upgraded on its own (`YESSION_SESSION_BIN`
// points at a floating install, Plan 11) and can then bring a typeface, a stylesheet, an image
// that the Manager beside it has never heard of. Nothing here has to learn about it: the
// directory the build left is the whole contract, and the route (`SessionRoute.Asset`) names a
// path and nothing else.
//
// The digest addresses the SET, not each file. One address for the whole directory is what lets
// a stylesheet reference a face by a plain relative name — `url(fonts/x.woff2)` inside
// `assets/<build>/app.css` resolves to `assets/<build>/fonts/x.woff2` — so nothing rewrites CSS
// at build time and nothing has to agree about naming. Immutability holds all the same: any
// byte of any file changes the digest, which changes every address in the set.

open Fable.Core
open Fable.Core.JsInterop
open Yession.App
open Yession.Host.Interop

[<Fable.Core.ImportAll("node:fs")>]
let private fs : obj = jsNative

[<ImportAll("node:crypto")>]
let private nodeCrypto : obj = jsNative

[<Import("fileURLToPath", "node:url")>]
let private fileUrlToPath (url: obj) : string = jsNative

/// The packaged location: `assets/` beside the running bundle. `import.meta.url` resolves to
/// the module, which is the package root once esbuild has flattened everything into one file.
[<Emit("new URL('./assets', import.meta.url)")>]
let private packagedAssets : obj = jsNative

/// One declared file's bytes, from the package's `assets/` when installed and the build output
/// when developing. `None` when it is not there, which is the un-built case: an empty set
/// rather than a crash at boot.
///
/// Bytes, not text: a set holds woff2 as readily as CSS, and `utf8` would mangle it.
[<Emit("""(() => {
  for (const root of [$1, $2]) { try { return $0.readFileSync(root + '/' + $3) } catch {} }
  return null
})()""")>]
let private readFile (fs: obj) (packaged: string) (fallback: string) (path: string) : obj option = jsNative

/// One digest over the whole set: every path and every byte, in the map's own (sorted) order,
/// so the same set always addresses the same. Twelve base64url characters, the same content
/// address `Interop.contentDigest` gives a document.
[<Emit("""(() => {
  const hash = $0.createHash('sha256')
  for (const [path, bytes] of $1) { hash.update(path); hash.update(bytes) }
  return hash.digest('base64url').slice(0, 12)
})()""")>]
let private digestEntries (cryptoModule: obj) (entries: (string * obj) array) : string = jsNative

/// What a build ships, and the one address it all sits under.
type AssetSet =
    { Build: AssetBuild
      Files: Map<string, obj> }

/// Read the asset set once, at boot. Per process, never per request: the addresses a shell
/// hands out have to be the addresses this process will answer for, and a re-read could drift
/// from the document that named them.
let load (fallbackDir: string) : AssetSet =
    let packaged = fileUrlToPath packagedAssets
    let files =
        AssetFile.all
        |> List.choose (fun file ->
            readFile fs packaged fallbackDir (AssetFile.path file)
            |> Option.map (fun bytes -> AssetFile.path file, bytes))
        |> Map.ofList
    { Build = AssetBuild (digestEntries nodeCrypto (Map.toArray files))
      Files = files }

/// The URL a document should name for `file` in this set.
let url (assets: AssetSet) (file: AssetFile) : string = AssetBuild.url assets.Build file

/// Serve one file, but only at this build's address.
///
/// A `build` that is not ours is a stale document asking for a set this process no longer
/// ships, and it gets the same 404 an unknown path does — which sends the browser back to the
/// shell, where it revalidates and learns the set that does exist. Answering with CURRENT bytes
/// at an OLD address would write them into an `immutable` cache entry: wrong for a year, and
/// unfixable from the server.
let serve (assets: AssetSet) (build: string) (path: string) (res: ServerResponse) =
    let found =
        if AssetBuild build = assets.Build then
            AssetFile.ofPath path
            |> Option.bind (fun file -> Map.tryFind path assets.Files |> Option.map (fun bytes -> file, bytes))
        else None
    match found with
    | Some (file, bytes) ->
        res.writeHead (
            200,
            createObj
                [ "content-type", box (AssetFile.contentType file)
                  "cache-control", box CachePolicy.asset ])
        |> ignore
        res.``end`` (unbox bytes)
    | None ->
        res.writeHead (404, createObj [ "content-type", box "text/plain" ]) |> ignore
        // An EMPTY set is the developer case — nothing was built — and saying so beats a bare
        // 404 on an address that looks perfectly reasonable.
        res.``end`` (if Map.isEmpty assets.Files then "not built (run: build)" else "stale asset address (reload)")
