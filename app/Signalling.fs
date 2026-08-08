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
let private bootstrapHtml (sessionId: SessionId) (mount: string) (managerOrigin: string option) (ephemeralStorage: bool) (assets: AssetDigests) =
    let placeholderPeer =
        match PeerId.create "browser" with
        | Ok peerId -> { PeerId = peerId; DisplayName = "" }
        | Error e -> failwith e
    // Seed the serving session id so the secondary identifier renders on first paint (the
    // browser re-learns it from `PeerAccepted` once connected).
    Ssr.page sessionId mount managerOrigin ephemeralStorage assets { ClientModel.init placeholderPeer with Session = Some sessionId }

let private bundlePath = envOr "YESSION_CLIENT_BUNDLE" "app/out/public/client.js"
let private cssPath = envOr "YESSION_APP_CSS" "app/out/public/app.css"

[<Fable.Core.ImportAll("node:fs")>]
let private fs : obj = Fable.Core.Util.jsNative

// From the package's assets/ when installed; from the build output in development.
let private readBundle () : string option = readAsset "client.js" bundlePath fs
let private readCss () : string option = readAsset "app.css" cssPath fs

/// Serve a fingerprinted asset, but only at its own address.
///
/// A `requested` digest that is not `ours` is a stale shell asking for a build this process no
/// longer is. Answering it with CURRENT bytes would write them into an `immutable` cache entry
/// under the old address — wrong for a year, and unfixable from the server. The 404 sends the
/// browser back to the shell, which revalidates and names the asset that does exist.
let private serveAsset
    (requested: string)
    (ours: string)
    (content: string option)
    (contentType: string)
    (what: string)
    (res: ServerResponse)
    =
    match content with
    | None ->
        res.writeHead (404, createObj [ "content-type", box "text/plain" ]) |> ignore
        res.``end`` (sprintf "%s not built (run: build)" what)
    | Some _ when requested <> ours ->
        res.writeHead (404, createObj [ "content-type", box "text/plain" ]) |> ignore
        res.``end`` (sprintf "stale %s address (reload)" what)
    | Some body ->
        res.writeHead (200, createObj [ "content-type", box contentType; "cache-control", box CachePolicy.asset ]) |> ignore
        res.``end`` body

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

/// The same surface for a terminal's transcript (Plan 13) — separate because the resource
/// is a terminal's, not the session's, so a read can legitimately answer "no such
/// terminal". A missing transcript is a 404 and an empty one is an empty 200: a client
/// catching up must be able to tell "this terminal has printed nothing yet" from "this
/// terminal does not exist".
type TranscriptEndpoint =
    { ValidateToken : string -> bool
      /// The raw terminal segment off the path, deliberately unvalidated here — parsing it
      /// into a `TerminalId` is the endpoint's job, and an unparseable one is simply a
      /// terminal that does not exist.
      ReadChunk : string -> int -> Async<(string list * bool) option> }

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
    // `GET /terminals/{id}/{n}`, when this session has terminals (Plan 13). Gated by the
    // same cookie-or-token check the event chunks use, and cached on the same argument.
    (transcripts: TranscriptEndpoint option)
    (auth: SessionAuth.Auth option)
    (extraRoutes: (IncomingMessage -> ServerResponse -> bool) option)
    (mintPeerToken: PeerAttribution -> string)
    // The path this session is served under (`""` at an origin root, docs/plans/09).
    // The proxy forwards the PUBLIC path unchanged and the session strips its own prefix
    // — the opposite contract (proxy strips, session serves at root) would make
    // correctness depend on per-proxy rewriting behaviour that cannot be tested here.
    (mount: string)
    // The Manager's public origin, baked into the shell (Plan 11). Computed once with the
    // page: this is a deployment fact, fixed at boot, not something that varies per
    // request — which is why the shell stays a single cached string.
    (managerOrigin: string option)
    // Whether this deployment's sessions change address between launches (Plan 12). A
    // deployment fact, fixed at boot like the mount and the origin beside it.
    (ephemeralStorage: bool)
    (port: int)
    : Async<HttpServer * (unit -> Async<unit>)> =
    // Read and address the assets ONCE, here, rather than per request. Three things follow,
    // and all three are the point: the shell can name the exact bytes the server will hand
    // out (a per-request read could drift from the document that named it), the immutable
    // URLs are stable for the life of the process, and the two static routes stop doing a
    // synchronous `readFileSync` on every hit.
    let bundle = readBundle ()
    let css = readCss ()
    let assets = { Bundle = contentDigest bundle; Css = contentDigest css }
    let bootstrapHtml = bootstrapHtml sessionId mount managerOrigin ephemeralStorage assets
    // The shell is a pure function of the session id, the mount, the Manager origin, and the
    // assets it names — all fixed at boot — so its validator is too, and a reload costs a 304
    // instead of the whole document.
    let shellEtag = sprintf "\"%s\"" (contentDigest (Some bootstrapHtml))
    // Every accepted peer connection, so a stopping Host can drain them. Never pruned
    // mid-life (closePeerConnection resolves immediately for already-closed ones, and a
    // session hosts a bounded handful of peers).
    let connections = ResizeArray<PeerConnection> ()
    let routeOf (req: IncomingMessage) = SessionRoute.parseUnder mount req.``method`` (pathnameOf req.url)
    let authorized (req: IncomingMessage) (url: string) (validateToken: string -> bool) =
        (match auth with
         | Some a -> a.IsAuthenticated req
         | None -> false)
        || (queryOf url "token" |> Option.map validateToken |> Option.defaultValue false)
    let unauthorized (res: ServerResponse) =
        res.writeHead (401, createObj [ "content-type", box "text/plain"; "cache-control", box "no-store" ]) |> ignore
        res.``end`` "unauthorized"

    let notFound (res: ServerResponse) =
        res.writeHead (404, createObj [ "content-type", box "text/plain"; "cache-control", box "no-store" ]) |> ignore
        res.``end`` "not found"

    /// Write a JSONL chunk with the cache policy its fullness implies. Shared by the event
    /// log and the transcripts, because the caching argument is identical: fixed bounds
    /// over an append-only sequence make a full chunk immutable for ever.
    let writeChunk (cacheControl: bool -> string) (lines: string list) (isFull: bool) (res: ServerResponse) =
        res.writeHead (
            200,
            createObj
                [ "content-type", box "application/x-ndjson; charset=utf-8"
                  "cache-control", box (cacheControl isFull) ])
        |> ignore
        res.``end`` (lines |> List.map (fun l -> l + "\n") |> String.concat "")

    let serveChunk (endpoint: EventsEndpoint) (req: IncomingMessage) (url: string) (index: int) (res: ServerResponse) =
        if not (authorized req url endpoint.ValidateToken) then unauthorized res
        else
            Async.StartImmediate (
                async {
                    let! lines, isFull = endpoint.ReadChunk index
                    writeChunk EventChunk.cacheControl lines isFull res
                })

    let serveTranscript
        (endpoint: TranscriptEndpoint)
        (req: IncomingMessage)
        (url: string)
        (terminal: string)
        (index: int)
        (res: ServerResponse)
        =
        if not (authorized req url endpoint.ValidateToken) then unauthorized res
        else
            Async.StartImmediate (
                async {
                    match! endpoint.ReadChunk terminal index with
                    | Some (lines, isFull) -> writeChunk TranscriptChunk.cacheControl lines isFull res
                    | None -> notFound res
                })

    // The routes this server owns, dispatched by one match over `SessionRoute` — so the
    // paths it serves, the paths the shell emits, and the paths the browser fetches are
    // one declaration, and a route added there fails this build until it is handled here.
    // The connection-panel routes (`ClaudeStatus`/`Claude`, `GitHubStatus`/`GitHub`) are
    // the session's too but live in `extraRoutes` (defined later in compile order), so
    // they fall through to it exactly as an unknown path does.
    let handler (req: IncomingMessage) (res: ServerResponse) =
        let handleWithExtraRoutes () =
            let handledByExtra =
                match extraRoutes with
                | Some tryRoutes -> tryRoutes req res
                | None -> false
            if not handledByExtra then
                res.writeHead (404, createObj [ "content-type", box "text/plain" ]) |> ignore
                res.``end`` "not found"
        match routeOf req with
        | Some Signal ->
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
        | Some Shell ->
            // The document that NAMES the fingerprinted assets, so it is the one thing that
            // must never be served stale — a cached shell pins the whole UI to the build it
            // was rendered against. Revalidated every time; the ETag makes that a 304.
            if headerOf req "if-none-match" = Some shellEtag then
                res.writeHead (304, createObj [ "cache-control", box CachePolicy.shell; "etag", box shellEtag ]) |> ignore
                res.``end`` ""
            else
                res.writeHead (
                    200,
                    createObj
                        [ "content-type", box "text/html; charset=utf-8"
                          "cache-control", box CachePolicy.shell
                          "etag", box shellEtag ])
                |> ignore
                res.``end`` bootstrapHtml
        | Some (ClientBundle digest) ->
            // The browser client bundle, built by `build` (esbuild output).
            serveAsset digest assets.Bundle bundle "text/javascript; charset=utf-8" "client bundle" res
        | Some (AppCss digest) ->
            // The locally built Tailwind stylesheet (no CDN).
            serveAsset digest assets.Css css "text/css; charset=utf-8" "stylesheet" res
        | Some (Events index) ->
            match events with
            | Some endpoint -> serveChunk endpoint req req.url index res
            | None -> notFound res
        | Some (TerminalTranscript (terminal, index)) ->
            match transcripts with
            | Some endpoint -> serveTranscript endpoint req req.url terminal index res
            | None -> notFound res
        | Some Login ->
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
        | Some Callback ->
            match auth with
            | None ->
                res.writeHead (404, createObj [ "content-type", box "text/plain"; "cache-control", box "no-store" ]) |> ignore
                res.``end`` "this session has no authorization provider"
            | Some a ->
                Async.StartImmediate (
                    async {
                        match! a.HandleCallback req.url with
                        | Ok setCookie ->
                            // `./` relative to `<mount>/callback` is `<mount>/` — the shell,
                            // wherever this session is mounted, with no prefix to know.
                            res.writeHead (
                                302,
                                createObj [ "location", box "./"; "set-cookie", box setCookie; "cache-control", box "no-store" ])
                            |> ignore
                            res.``end`` ""
                        | Error (status, message) ->
                            res.writeHead (status, createObj [ "content-type", box "text/plain"; "cache-control", box "no-store" ]) |> ignore
                            res.``end`` message
                    })
        | Some Me ->
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
        | Some ClaudeStatus
        | Some (Claude _)
        | Some GitHubStatus
        | Some (GitHub _)
        | None -> handleWithExtraRoutes ()

    let server = createServer handler
    let closeConnections () : Async<unit> =
        async {
            for pc in List.ofSeq connections do
                do! WebRtc.closePeerConnection pc
            connections.Clear ()
        }
    Async.FromContinuations(fun (cont, _, _) ->
        server.listen (port, "127.0.0.1", fun () -> cont (server, closeConnections)) |> ignore)
