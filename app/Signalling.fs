module Yession.Host.Signalling

// HTTP serves static app bootstrap, temporary WebRTC signalling, and ONE read-only
// data surface: the event log as immutable, cache-friendly chunks (`/events/{n}`).
// Everything interactive stays on the data channel (design.md §2.3); the chunk
// endpoint exists precisely because HTTP caching is the point — full chunks of the
// append-only log never change, so the browser's cache becomes the client-side event
// store. `/signal` accepts a peer's offer and returns the Session Process's answer;
// the established data channel becomes a session `FrameChannel`.

open Fable.Core.JsInterop
open Yession.Domain
open Yession.SessionProcess
open Yession.Host.Interop
open Yession.Host.WebRtc
open Yession.App

/// The static bootstrap page is the client shell itself, rendered from the initial model.
/// The browser hydrates it and connects back over WebRTC; serving the same `View` keeps a
/// single source of truth for the shell markup. The local display name is assigned
/// randomly in the browser, so the server-rendered placeholder is left blank. The page
/// embeds the serving session's id, so the browser can key its local doc store by
/// session before (and without) any connection.
let private bootstrapHtml (sessionId: SessionId) =
    let placeholderPeer =
        match PeerId.create "browser" with
        | Ok peerId -> { PeerId = peerId; DisplayName = "" }
        | Error e -> failwith e
    Ssr.page sessionId (ClientModel.init placeholderPeer)

let private bundlePath = envOr "YESSION_CLIENT_BUNDLE" "app/out/public/client.js"
let private cssPath = envOr "YESSION_APP_CSS" "app/out/public/app.css"

[<Fable.Core.ImportAll("node:fs")>]
let private fs : obj = Fable.Core.Util.jsNative

// From the package's assets/ when installed; from the build output in development.
let private readBundle () : string option = readAsset "client.js" bundlePath fs
let private readCss () : string option = readAsset "app.css" cssPath fs

let private readBody (req: IncomingMessage) (cont: string -> unit) =
    let mutable acc = ""
    req.on ("data", fun chunk -> acc <- acc + bufferToString chunk) |> ignore
    req.on ("end", fun _ -> cont acc) |> ignore

[<Fable.Core.Emit("new URL($0, 'http://local').pathname")>]
let private pathnameOf (url: string) : string = Fable.Core.Util.jsNative

[<Fable.Core.Emit("new URL($0, 'http://local').searchParams.get($1)")>]
let private queryOf (url: string) (name: string) : string option = Fable.Core.Util.jsNative

/// The token-gated, HTTP-cacheable event-log read surface.
type EventsEndpoint =
    { /// The session token; chunk requests must carry it as `?token=`.
      Token : string
      /// Read chunk `n`: the JSONL-encoded envelope lines, plus whether the chunk is
      /// full (and therefore immutable).
      ReadChunk : int -> Async<string list * bool> }

/// Start the HTTP bootstrap + signalling server. For each offer posted to `/signal`, an
/// answering peer connection is created; when its data channel opens, the resulting frame
/// channel is handed to `onConnection`. When `events` is given, `GET /events/{n}?token=…`
/// serves the log in fixed-size chunks with cache headers derived from immutability.
/// Resolves once the server is listening.
let start
    (sessionId: SessionId)
    (onConnection: FrameChannel<string> -> unit)
    (events: EventsEndpoint option)
    (port: int)
    : Async<HttpServer> =
    let bootstrapHtml = bootstrapHtml sessionId
    let serveChunk (endpoint: EventsEndpoint) (url: string) (index: int) (res: ServerResponse) =
        if queryOf url "token" <> Some endpoint.Token then
            res.writeHead (401, createObj [ "content-type", box "text/plain"; "cache-control", box "no-store" ]) |> ignore
            res.``end`` "invalid session token"
        else
            Async.StartImmediate (
                async {
                    let! lines, isFull = endpoint.ReadChunk index
                    res.writeHead (
                        200,
                        createObj
                            [ "content-type", box "application/x-ndjson; charset=utf-8"
                              "cache-control", box (EventChunk.cacheControl isFull) ])
                    |> ignore
                    res.``end`` (lines |> List.map (fun l -> l + "\n") |> String.concat "")
                })

    let handler (req: IncomingMessage) (res: ServerResponse) =
        match req.``method``, pathnameOf req.url with
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
            // A one-day cache window: the browser can reopen the app offline for up to a
            // day before it must fetch a fresh shell (local-first, tight back-compat window).
            res.writeHead (200, createObj [ "content-type", box "text/html; charset=utf-8"; "cache-control", box "max-age=86400" ]) |> ignore
            res.``end`` bootstrapHtml
        | "GET", "/client.js" ->
            // The browser client bundle, built by `mise run build` (esbuild output).
            match readBundle () with
            | Some js ->
                res.writeHead (200, createObj [ "content-type", box "text/javascript; charset=utf-8"; "cache-control", box "max-age=86400" ]) |> ignore
                res.``end`` js
            | None ->
                res.writeHead (404, createObj [ "content-type", box "text/plain" ]) |> ignore
                res.``end`` "client bundle not built (run: mise run build)"
        | "GET", "/app.css" ->
            // The locally built Tailwind stylesheet (no CDN); same one-day offline window.
            match readCss () with
            | Some css ->
                res.writeHead (200, createObj [ "content-type", box "text/css; charset=utf-8"; "cache-control", box "max-age=86400" ]) |> ignore
                res.``end`` css
            | None ->
                res.writeHead (404, createObj [ "content-type", box "text/plain" ]) |> ignore
                res.``end`` "stylesheet not built (run: mise run build)"
        | "GET", path when path.StartsWith "/events/" ->
            match events, System.Int32.TryParse (path.Substring "/events/".Length) with
            | Some endpoint, (true, index) when index >= 0 -> serveChunk endpoint req.url index res
            | _ ->
                res.writeHead (404, createObj [ "content-type", box "text/plain"; "cache-control", box "no-store" ]) |> ignore
                res.``end`` "not found"
        | _ ->
            res.writeHead (404, createObj [ "content-type", box "text/plain" ]) |> ignore
            res.``end`` "not found"

    let server = createServer handler
    Async.FromContinuations(fun (cont, _, _) ->
        server.listen (port, "127.0.0.1", fun () -> cont server) |> ignore)
