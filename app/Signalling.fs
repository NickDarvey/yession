module Yession.Host.Signalling

// HTTP is used only for static app bootstrap and temporary WebRTC signalling — never as
// the session API (design.md §2.3). `/signal` accepts a peer's offer and returns the
// Session Process's answer; the established data channel becomes a session `FrameChannel`.

open Fable.Core.JsInterop
open Yession.SessionProcess
open Yession.Host.Interop
open Yession.Host.WebRtc

let private bootstrapHtml =
    "<!doctype html><html><head><meta charset=\"utf-8\"><title>Yession</title></head>"
    + "<body><main id=\"app\">Yession Session Process is running. "
    + "The browser client shell arrives in delivery step 04.</main></body></html>"

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
        | _ ->
            res.writeHead (404, createObj [ "content-type", box "text/plain" ]) |> ignore
            res.``end`` "not found"

    let server = createServer handler
    Async.FromContinuations(fun (cont, _, _) ->
        server.listen (port, "127.0.0.1", fun () -> cont server) |> ignore)
