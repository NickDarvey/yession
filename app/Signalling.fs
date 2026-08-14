module Yession.Host.Signalling

// HTTP serves static app bootstrap, temporary WebRTC signalling, and ONE read-only
// data surface: the event log, read by cursor (docs/plans/20). A client sends the offset
// it has folded through (`/events/after/{n}`) and this redirects to the range it chose
// (`/events/{first}-{last}`), whose bounds do not move — so those bytes are the same for
// ever and a client can keep them, the growing tail included. Everything interactive stays
// on the data channel (design.md §2.3). `/signal` accepts a peer's offer and returns the
// Session Process's answer; the established data channel becomes a session `FrameChannel`.

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
let private bootstrapHtml (sessionId: SessionId) (mount: string) (managerOrigin: string option) (ephemeralStorage: bool) (assets: AssetBuild) =
    let placeholderPeer =
        match PeerId.create "browser" with
        | Ok peerId -> { PeerId = peerId; DisplayName = "" }
        | Error e -> failwith e
    // Seed the serving session id so the secondary identifier renders on first paint (the
    // browser re-learns it from `PeerAccepted` once connected).
    Ssr.page sessionId mount managerOrigin ephemeralStorage assets { ClientModel.init placeholderPeer with Session = Some sessionId }

/// Where the built asset set lives when this is not an installed package (`Assets` looks
/// beside the running bundle first). One variable for the whole directory, not one per file:
/// a deployment that could point the stylesheet and its faces at different builds is a
/// deployment that could serve a stylesheet its faces do not match.
let private assetsDir = envOr "YESSION_ASSETS" "app/out/public/assets"

/// The app icon's bytes. The constant is base64 (`WebApp.iconPngBase64`) because it lives in
/// source; the wire wants the PNG, and `res.end` is typed to the string case it is used with
/// everywhere else — so the Buffer goes through `unbox`, which is what Node's `end` accepts.
[<Fable.Core.Emit("Buffer.from($0, 'base64')")>]
let private decodeBase64 (encoded: string) : string = Fable.Core.Util.jsNative

let private readBody (req: IncomingMessage) (cont: string -> unit) =
    let mutable acc = ""
    req.on ("data", fun chunk -> acc <- acc + bufferToString chunk) |> ignore
    req.on ("end", fun _ -> cont acc) |> ignore

[<Fable.Core.Emit("new URL($0, 'http://local').pathname")>]
let private pathnameOf (url: string) : string = Fable.Core.Util.jsNative

[<Fable.Core.Emit("new URL($0, 'http://local').searchParams.get($1)")>]
let private queryOf (url: string) (name: string) : string option = Fable.Core.Util.jsNative

[<Fable.Core.Emit("encodeURIComponent($0)")>]
let private encodeUriComponent (value: string) : string = Fable.Core.Util.jsNative

/// The auth-gated event-log read surface: a cursor, and the ranges it resolves to.
type EventsEndpoint =
    { /// Validates a `?token=` peer token (minted by this process via `/me`) — the
      /// cookie-less access path (Node tests, headless clients).
      ValidateToken : string -> bool
      /// The bounds of the answer to a cursor — `None` when the caller is already
      /// current, which is what makes `204` rather than an empty range the answer to
      /// "nothing has happened since". The server picks these; no client ever does.
      BoundsAfter : EventOffset option -> Async<(int64 * int64) option>
      /// The JSONL-encoded envelope lines at offsets `[first, last]`, or `None` when the
      /// log has not reached `last` — a range that does not exist yet is not a short
      /// answer, it is a 404.
      ReadRange : int64 -> int64 -> Async<string list option> }

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
      ReadChunk : string -> int -> Async<(string list * bool) option>
      /// The keyframe for one transcript line (Plan 14, stage 3), as its encoded JSON.
      /// `None` covers both "no such terminal" and "no keyframe at that line" — a client
      /// asking for one it cannot get plays the range without it, and says so.
      ReadKeyframe : string -> int -> Async<string option> }

/// Start the HTTP bootstrap + signalling server. For each offer posted to `/signal`, an
/// answering peer connection is created; when its data channel opens, the resulting frame
/// channel is handed to `onConnection`. When `events` is given, the event cursor and the
/// ranges it resolves to are served, gated by the auth cookie or a minted `?token=`. When
/// `auth` is given, the login surface
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
    // same cookie-or-token check the event surface uses. Still chunk-indexed and still
    // cached by the browser — docs/plans/20 gives it the same treatment as a follow-up.
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
    // URLs are stable for the life of the process, and the static route stops doing a
    // synchronous `readFileSync` on every hit.
    let assets = Assets.load assetsDir
    let bootstrapHtml = bootstrapHtml sessionId mount managerOrigin ephemeralStorage assets.Build
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

    /// The cursor. Never carries events and never caches: it answers where the events
    /// this caller has not seen are, and that moves.
    let serveCursor (endpoint: EventsEndpoint) (req: IncomingMessage) (url: string) (after: EventOffset option) (res: ServerResponse) =
        if not (authorized req url endpoint.ValidateToken) then unauthorized res
        else
            Async.StartImmediate (
                async {
                    match! endpoint.BoundsAfter after with
                    | None ->
                        // Up to date. `204` rather than an empty range, because an empty
                        // range is a resource a client would keep, and "nothing yet" is
                        // exactly the thing that stops being true.
                        res.writeHead (204, createObj [ "cache-control", box "no-store" ]) |> ignore
                        res.``end`` ""
                    | Some (first, last) ->
                        // An absolute-path Location, built from the mount this session is
                        // served under: a RELATIVE one would resolve against the cursor's
                        // own path (`…/events/after/99`) and land two segments deep. The
                        // token rides along when the caller used one — a redirect drops
                        // the query, and the cookie-less path would arrive unauthorized.
                        let target =
                            let path = (if mount = "" then "" else mount) + "/" + SessionRoute.relative (Events (first, last))
                            match queryOf url "token" with
                            | Some token -> sprintf "%s?token=%s" path (encodeUriComponent token)
                            | None -> path
                        res.writeHead (
                            307,
                            createObj [ "location", box target; "cache-control", box "no-store" ])
                        |> ignore
                        res.``end`` ""
                })

    /// The events themselves, at an address that names its bounds — the same answer for
    /// ever, which is why the client keeps it. `no-store` all the same: the copy that
    /// matters is the one the client puts in its own store, and a second one in the HTTP
    /// cache would be a spare nobody reads (docs/plans/20).
    let serveRange (endpoint: EventsEndpoint) (req: IncomingMessage) (url: string) (first: int64) (last: int64) (res: ServerResponse) =
        if not (authorized req url endpoint.ValidateToken) then unauthorized res
        else
            Async.StartImmediate (
                async {
                    match! endpoint.ReadRange first last with
                    | Some lines -> writeChunk (fun _ -> "no-store") lines true res
                    | None -> notFound res
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

    let serveKeyframe
        (endpoint: TranscriptEndpoint)
        (req: IncomingMessage)
        (url: string)
        (terminal: string)
        (seq: int)
        (res: ServerResponse)
        =
        if not (authorized req url endpoint.ValidateToken) then unauthorized res
        else
            Async.StartImmediate (
                async {
                    match! endpoint.ReadKeyframe terminal seq with
                    // `immutable` on the same argument the full chunks earn it on: a
                    // keyframe is written once, at a position that never moves, so its
                    // bytes can never change.
                    | Some json ->
                        res.writeHead (
                            200,
                            createObj
                                [ "content-type", box "application/json; charset=utf-8"
                                  "cache-control", box (TranscriptChunk.cacheControl true) ])
                        |> ignore
                        res.``end`` json
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
        | Some (Asset (build, path)) ->
            // Whatever this build left in its asset directory — the bundle, the stylesheet, a
            // typeface, something added after this line was written. A `build` that is not
            // ours holds nothing, so a stale shell gets a 404 and reloads.
            Assets.serve assets build path res
        // The two an INSTALL reads (`WebApp`), and the only routes here served to nobody in
        // particular: a browser fetches a manifest without credentials, and there is nothing
        // in either that a person outside the session could not see from the login page.
        // Neither is fingerprinted — the document names a fixed address and the manifest
        // names the icon — so both revalidate on the shell's policy rather than being
        // cached under an address whose bytes a release can change.
        | Some Manifest ->
            res.writeHead (
                200,
                createObj
                    [ "content-type", box "application/manifest+json; charset=utf-8"
                      "cache-control", box CachePolicy.shell ])
            |> ignore
            res.``end`` WebApp.manifest
        | Some Icon ->
            res.writeHead (
                200,
                createObj [ "content-type", box "image/png"; "cache-control", box CachePolicy.shell ])
            |> ignore
            res.``end`` (decodeBase64 WebApp.iconPngBase64)
        | Some (EventsAfter after) ->
            match events with
            | Some endpoint -> serveCursor endpoint req req.url after res
            | None -> notFound res
        | Some (Events (first, last)) ->
            match events with
            | Some endpoint -> serveRange endpoint req req.url first last res
            | None -> notFound res
        | Some (TerminalTranscript (terminal, index)) ->
            match transcripts with
            | Some endpoint -> serveTranscript endpoint req req.url terminal index res
            | None -> notFound res
        | Some (TerminalKeyframe (terminal, seq)) ->
            match transcripts with
            | Some endpoint -> serveKeyframe endpoint req req.url terminal seq res
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
        | Some Queries
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
