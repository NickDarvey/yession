module Yession.Tests.Assets

// The promise the static asset service makes: a build serves what it SHIPS, not what the code
// knows the names of.
//
// That is what lets a session be upgraded on its own. A newer session brings a typeface, a
// stylesheet, an image that the binary beside it has never heard of, and it has to be able to
// hand them out — so the thing under test here is deliberately a file no source file names.
//
// Node only: `Assets` reads a directory and writes an HTTP response, which is the runtime the
// product runs on. No capability — writing a temp directory is what every store suite here
// already does.

open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto
open Yession.App
open Yession.Host

let private nodeFs : obj = importAll "node:fs"

[<Emit("$0.mkdirSync($1, { recursive: true })")>]
let private mkdirp (fs: obj) (dir: string) : unit = jsNative

[<Emit("$0.rmSync($1, { recursive: true, force: true })")>]
let private rmrf (fs: obj) (dir: string) : unit = jsNative

[<Emit("$0.writeFileSync($1, $2)")>]
let private writeFile (fs: obj) (path: string) (contents: string) : unit = jsNative

/// What a response was: the status, the headers, and the body as text. A `ServerResponse` is an
/// interface over Node's, so the cheapest honest double is an object with the three members
/// `Assets.serve` uses.
type private Reply =
    { mutable Status: int
      mutable Headers: obj
      mutable Body: string }

[<Emit("({ writeHead: (status, headers) => { $0.Status = status; $0.Headers = headers; return null }, write: () => true, end: (body) => { $0.Body = body == null ? '' : String(body) } })")>]
let private responseInto (reply: Reply) : Interop.ServerResponse = jsNative

[<Emit("($0 ?? {})[$1] ?? ''")>]
let private headerOf (headers: obj) (name: string) : string = jsNative

let private serveInto (assets: Assets.AssetSet) (build: string) (path: string) =
    let reply = { Status = 0; Headers = null; Body = "" }
    Assets.serve assets build path (responseInto reply)
    reply

/// A directory of assets, one of which — `unheard-of.woff2` — is named nowhere in the product.
let private withAssets (name: string) (body: Assets.AssetSet -> 'a) : 'a =
    let dir = "tests/Yession.Tests/out/.assets/" + name
    rmrf nodeFs dir
    mkdirp nodeFs (dir + "/fonts")
    writeFile nodeFs (dir + "/app.css") "body{color:red}"
    writeFile nodeFs (dir + "/fonts/unheard-of.woff2") "not really a face, but bytes are bytes"
    body (Assets.load dir)

let tests =
    testList "Static assets" [
        testCase "a build serves a file nothing in the product names" <| fun () ->
            withAssets "unnamed" (fun assets ->
                let (AssetBuild build) = assets.Build
                let reply = serveInto assets build "fonts/unheard-of.woff2"
                Expect.equal reply.Status 200 "the service hands out what the build shipped"
                Expect.equal
                    (headerOf reply.Headers "content-type")
                    "font/woff2"
                    "labelled from its extension, which is all the service knows about it")

        testCase "an address is only ever this build's" <| fun () ->
            // The whole staleness story. A document from an older build names an older set, and
            // answering it with CURRENT bytes would write them into an `immutable` cache entry
            // under that address — wrong for a year, and unfixable from the server.
            withAssets "stale" (fun assets ->
                let reply = serveInto assets "notthisbuild" "app.css"
                Expect.equal reply.Status 404 "another build's address is refused, not answered")

        testCase "changing any file changes every address in the set" <| fun () ->
            // Why one digest over the whole directory is safe to cache forever, and why a
            // stylesheet may reference a face by a plain relative name: the sheet and the face
            // move together or not at all.
            let before = withAssets "digest" id
            let dir = "tests/Yession.Tests/out/.assets/digest"
            writeFile nodeFs (dir + "/fonts/unheard-of.woff2") "different bytes entirely"
            let after = Assets.load dir
            Expect.notEqual after.Build before.Build "a byte anywhere is a new set"
            Expect.equal (Assets.load dir).Build after.Build "and the same directory always addresses the same"

        testCase "an unbuilt directory is an empty set that says so" <| fun () ->
            // The developer case: not a crash at boot, and not a bare 404 on an address that
            // looks perfectly reasonable.
            let assets = Assets.load "tests/Yession.Tests/out/.assets/never-built"
            let (AssetBuild build) = assets.Build
            let reply = serveInto assets build "app.css"
            Expect.equal reply.Status 404 "nothing to serve"
            Expect.stringContains reply.Body "not built" "and it names the reason"

        testCase "a document names a file through the build's address" <| fun () ->
            withAssets "urls" (fun assets ->
                let url = Assets.url assets AssetFile.appCss
                Expect.isFalse (url.StartsWith "/") "relative, so a path-mounted session resolves it under its own prefix"
                Expect.equal
                    (SessionRoute.parse "GET" ("/" + url))
                    (Some (Asset ((let (AssetBuild b) = assets.Build in b), AssetFile.appCss)))
                    "and what the document names is what the router claims")
    ]
