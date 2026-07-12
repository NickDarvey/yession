module Yession.Host.Signalling

// HTTP is used only for static app bootstrap and temporary WebRTC signalling — never as
// the session API (design.md §2.3). `/signal` accepts a peer's offer and returns the
// Session Process's answer; the established data channel becomes a session `FrameChannel`.

open Fable.Core.JsInterop
open Yession.Domain
open Yession.SessionProcess
open Yession.Host.Interop
open Yession.Host.WebRtc
open Yession.Client

/// The static bootstrap page is the client shell itself, rendered from the initial model.
/// The browser hydrates it and connects back over WebRTC; serving the same `View` keeps a
/// single source of truth for the shell markup. The local display name is assigned
/// randomly in the browser, so the server-rendered placeholder is left blank.
let private bootstrapHtml =
    let placeholderPeer =
        match PeerId.create "browser" with
        | Ok peerId -> { PeerId = peerId; DisplayName = "" }
        | Error e -> failwith e
    View.page (ClientModel.init placeholderPeer)

let private bundlePath = envOr "YESSION_CLIENT_BUNDLE" "app/out/public/client.js"

[<Fable.Core.Emit("(() => { try { const fs = $0; return fs.existsSync($1) ? fs.readFileSync($1, 'utf8') : null } catch { return null } })()")>]
let private tryReadFile (fs: obj) (path: string) : string option = Fable.Core.Util.jsNative

[<Fable.Core.ImportAll("node:fs")>]
let private fs : obj = Fable.Core.Util.jsNative

let private readBundle () : string option = tryReadFile fs bundlePath

let private readBody (req: IncomingMessage) (cont: string -> unit) =
    let mutable acc = ""
    req.on ("data", fun chunk -> acc <- acc + bufferToString chunk) |> ignore
    req.on ("end", fun _ -> cont acc) |> ignore

/// Start the HTTP bootstrap + signalling server. For each offer posted to `/signal`, an
/// answering peer connection is created; when its data channel opens, the resulting frame
/// channel is handed to `onConnection`. Resolves once the server is listening.
let start (onConnection: FrameChannel<string> -> unit) (port: int) : Async<HttpServer> =
    let handler (req: IncomingMessage) (res: ServerResponse) =
        match req.``method``, req.url with
        | "POST", "/signal" ->
            readBody req (fun body ->
                let offerSdp = sdpField body
                let pc = createPeerConnection "yession-process"
                pc.onDataChannel (fun dc -> onConnection (frameChannel dc))
                Async.StartImmediate(
                    async {
                        let! answer = answerOffer pc offerSdp
                        res.writeHead (200, createObj [ "content-type", box "application/json" ]) |> ignore
                        res.``end`` answer
                    }))
        | "GET", "/" ->
            res.writeHead (200, createObj [ "content-type", box "text/html; charset=utf-8" ]) |> ignore
            res.``end`` bootstrapHtml
        | "GET", "/client.js" ->
            // The browser client bundle, built by `mise run build` (esbuild output).
            match readBundle () with
            | Some js ->
                res.writeHead (200, createObj [ "content-type", box "text/javascript; charset=utf-8" ]) |> ignore
                res.``end`` js
            | None ->
                res.writeHead (404, createObj [ "content-type", box "text/plain" ]) |> ignore
                res.``end`` "client bundle not built (run: mise run build)"
        | _ ->
            res.writeHead (404, createObj [ "content-type", box "text/plain" ]) |> ignore
            res.``end`` "not found"

    let server = createServer handler
    Async.FromContinuations(fun (cont, _, _) ->
        server.listen (port, "127.0.0.1", fun () -> cont server) |> ignore)
