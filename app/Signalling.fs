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
    // Seed the serving session id so the secondary identifier renders on first paint (the
    // browser re-learns it from `PeerAccepted` once connected).
    Ssr.page sessionId { ClientModel.init placeholderPeer with Session = Some sessionId }

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

/// The auth-gated, HTTP-cacheable event-log read surface.
type EventsEndpoint =
    { /// Validates a `?token=` peer token (minted by this process via `/me`) — the
      /// cookie-less access path (Node tests, headless clients).
      ValidateToken : string -> bool
      /// Read chunk `n`: the JSONL-encoded envelope lines, plus whether the chunk is
      /// full (and therefore immutable).
      ReadChunk : int -> Async<string list * bool> }

/// Start the HTTP bootstrap + signalling server. For each offer posted to `/signal`, an
/// answering peer connection is created; when its data channel opens, the resulting frame
/// channel is handed to `onConnection`. When `events` is given, `GET /events/{n}` serves
/// the log in fixed-size chunks with cache headers derived from immutability, gated by
/// the auth cookie or a minted `?token=`. When `auth` is given, the login surface
/// (`/login`, `/callback`, `/me`) rides the same server; the SHELL stays ungated and
/// cacheable — offline-first — because it is a pure function of the session id with no
/// content and no secrets; authorization gates the data surfaces, and the browser client
/// renavigates to `/login` when `/me` says it must.
/// Start the bootstrap/signalling server. Returns the server AND `closeConnections`:
/// close every peer connection this server accepted and resolve once libdatachannel has
/// reported each one closed — the deterministic drain a stopping Host runs before any
/// global teardown (no live native objects may outlive it).
/// `extraRoutes` composes additional HTTP routes onto the same server (Plan 08: the
/// session's connection surface, defined later in compile order): tried before the
/// final 404, `false` = not this handler's path.
let start
    (sessionId: SessionId)
    (onConnection: FrameChannel<string> -> unit)
    (events: EventsEndpoint option)
    (auth: SessionAuth.Auth option)
    (extraRoutes: (IncomingMessage -> ServerResponse -> bool) option)
    (mintPeerToken: PeerAttribution -> string)
    (port: int)
    : Async<HttpServer * (unit -> Async<unit>)> =
    let bootstrapHtml = bootstrapHtml sessionId
    // Every accepted peer connection, so a stopping Host can drain them. Never pruned
    // mid-life (closePeerConnection resolves immediately for already-closed ones, and a
    // session hosts a bounded handful of peers).
    let connections = ResizeArray<PeerConnection> ()
    let authorized (req: IncomingMessage) (url: string) (validateToken: string -> bool) =
        (match auth with
         | Some a -> a.IsAuthenticated req
         | None -> false)
        || (queryOf url "token" |> Option.map validateToken |> Option.defaultValue false)
    let serveChunk (endpoint: EventsEndpoint) (req: IncomingMessage) (url: string) (index: int) (res: ServerResponse) =
        if not (authorized req url endpoint.ValidateToken) then
            res.writeHead (401, createObj [ "content-type", box "text/plain"; "cache-control", box "no-store" ]) |> ignore
            res.``end`` "unauthorized"
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
                connections.Add pc
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
            // The browser client bundle, built by `build` (esbuild output).
            match readBundle () with
            | Some js ->
                res.writeHead (200, createObj [ "content-type", box "text/javascript; charset=utf-8"; "cache-control", box "max-age=86400" ]) |> ignore
                res.``end`` js
            | None ->
                res.writeHead (404, createObj [ "content-type", box "text/plain" ]) |> ignore
                res.``end`` "client bundle not built (run: build)"
        | "GET", "/app.css" ->
            // The locally built Tailwind stylesheet (no CDN); same one-day offline window.
            match readCss () with
            | Some css ->
                res.writeHead (200, createObj [ "content-type", box "text/css; charset=utf-8"; "cache-control", box "max-age=86400" ]) |> ignore
                res.``end`` css
            | None ->
                res.writeHead (404, createObj [ "content-type", box "text/plain" ]) |> ignore
                res.``end`` "stylesheet not built (run: build)"
        | "GET", path when path.StartsWith "/events/" ->
            match events, System.Int32.TryParse (path.Substring "/events/".Length) with
            | Some endpoint, (true, index) when index >= 0 -> serveChunk endpoint req req.url index res
            | _ ->
                res.writeHead (404, createObj [ "content-type", box "text/plain"; "cache-control", box "no-store" ]) |> ignore
                res.``end`` "not found"
        | "GET", "/login" ->
            // Begin the authorization-code + PKCE dance: 302 to the Manager's authorize
            // endpoint. The BROWSER navigates here (renavigation on a 401 from `/me`) —
            // the cached shell itself never redirects, preserving offline reopen.
            match auth with
            | None ->
                res.writeHead (404, createObj [ "content-type", box "text/plain"; "cache-control", box "no-store" ]) |> ignore
                res.``end`` "this session has no authorization provider"
            | Some a ->
                // The browser's stable peer id rides the bounce (docs/plans/07) so the
                // Manager can witness which peer signed in; absent for headless logins.
                let peer =
                    queryOf req.url "peer_id"
                    |> Option.bind (fun raw ->
                        match PeerId.create raw with
                        | Ok peerId -> Some peerId
                        | Error _ -> None)
                Async.StartImmediate (
                    async {
                        match! a.BeginLogin peer with
                        | Some url ->
                            res.writeHead (302, createObj [ "location", box url; "cache-control", box "no-store" ]) |> ignore
                            res.``end`` ""
                        | None ->
                            res.writeHead (503, createObj [ "content-type", box "text/plain"; "cache-control", box "no-store" ]) |> ignore
                            res.``end`` "session is still registering with its manager"
                    })
        | "GET", "/callback" ->
            match auth with
            | None ->
                res.writeHead (404, createObj [ "content-type", box "text/plain"; "cache-control", box "no-store" ]) |> ignore
                res.``end`` "this session has no authorization provider"
            | Some a ->
                Async.StartImmediate (
                    async {
                        match! a.HandleCallback req.url with
                        | Ok setCookie ->
                            res.writeHead (
                                302,
                                createObj [ "location", box "/"; "set-cookie", box setCookie; "cache-control", box "no-store" ])
                            |> ignore
                            res.``end`` ""
                        | Error (status, message) ->
                            res.writeHead (status, createObj [ "content-type", box "text/plain"; "cache-control", box "no-store" ]) |> ignore
                            res.``end`` message
                    })
        | "GET", "/me" ->
            // The browser's probe: a valid cookie (or no auth requirement at all) mints
            // a peer token for the WebRTC `PeerHello` — cookies cannot ride the data
            // channel. The minted token CARRIES the cookie's attribution (docs/plans/07),
            // so the Manager-verified user reaches the event log without riding any
            // peer-controlled frame. 401 tells the client to renavigate to `/login`; a
            // network error (offline) tells it to stay on the cached shell and stores.
            let respondMe (subject: string) (attribution: PeerAttribution) =
                let attributed = match attribution with AttributedUser _ -> true | UnattributedAccess -> false
                res.writeHead (200, createObj [ "content-type", box "application/json"; "cache-control", box "no-store" ]) |> ignore
                res.``end`` (sprintf """{"peerToken":"%s","sub":"%s","attributed":%b}""" (mintPeerToken attribution) subject attributed)
            match auth with
            | None -> respondMe "local" UnattributedAccess
            | Some a ->
                match a.IdentityOf req with
                | Some identity -> respondMe identity.Subject identity.Attribution
                | None ->
                    res.writeHead (401, createObj [ "content-type", box "text/plain"; "cache-control", box "no-store" ]) |> ignore
                    res.``end`` "unauthorized"
        | _ ->
            let handledByExtra =
                match extraRoutes with
                | Some tryRoutes -> tryRoutes req res
                | None -> false
            if not handledByExtra then
                res.writeHead (404, createObj [ "content-type", box "text/plain" ]) |> ignore
                res.``end`` "not found"

    let server = createServer handler
    let closeConnections () : Async<unit> =
        async {
            for pc in List.ofSeq connections do
                do! WebRtc.closePeerConnection pc
            connections.Clear ()
        }
    Async.FromContinuations(fun (cont, _, _) ->
        server.listen (port, "127.0.0.1", fun () -> cont (server, closeConnections)) |> ignore)
