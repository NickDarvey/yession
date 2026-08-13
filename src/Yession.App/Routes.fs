namespace Yession.App

/// The HTTP contract of a Session Process: every path it serves, declared once. The
/// server matches on these, the shell emits them, and the browser client fetches them —
/// the same role `Dom` plays for markup hooks, one level up. Before this, `/client.js`
/// was spelled independently in the route match, in the emitted `<script src>`, and in
/// the browser's fetch, three strings that agreed only by inspection.
///
/// The property that matters: `relative` never emits a leading slash, and it is the only
/// way to render a route as a URL. A session does not necessarily own the root of its
/// origin — an operator's proxy may mount it under a path (docs/plans/09) — so a
/// root-anchored URL is not something a caller should be able to write by accident. The
/// browser resolves these against the shell's `<base href>`.

/// The Claude connection panel's write actions (Plan 08). Separate from the status read
/// so the two cannot be confused for one another. Qualified access is required because
/// these names are reused elsewhere in the domain (`Complete` is also a
/// `ConversationItemStatus`), which is exactly the ambiguity that makes an unqualified
/// case a hazard here.
[<RequireQualifiedAccess>]
type ClaudeAction =
    | Begin
    | Complete
    | Token
    | Disconnect

/// The GitHub connection panel's write actions (Plan 14). Same shape as `ClaudeAction`
/// with one difference of flow: GitHub signs in by DEVICE CODE (the panel shows a code,
/// the user approves on github.com, and this session polls the token endpoint), so
/// `Poll` stands where Claude's pasted-code `Complete` stands.
[<RequireQualifiedAccess>]
type GitHubAction =
    | Begin
    | Poll
    | Token
    | Disconnect

type SessionRoute =
    /// The client shell itself — the served page IS the app.
    | Shell
    /// Any static file this build ships — `assets/<build>/<path>`, where `<build>` is a
    /// digest over the WHOLE asset set and `<path>` is the file's place inside it.
    ///
    /// One case, deliberately. This used to be four — the bundle, the stylesheet, the replay
    /// player's stylesheet, a typeface — and every asset added to the product cost a case
    /// here, a rendering, a parse, a case in each of the two servers, and a line in `stage`.
    /// That is a build serving what the ROUTER knows about, and it is backwards: a session
    /// upgraded on its own can bring a typeface, a sheet, an image that this Manager's
    /// binary has never heard of, and it must be able to serve them. So the route names a
    /// path and nothing else, and the server answers from whatever its own build left in the
    /// asset directory (`Assets`).
    ///
    /// The digest is on the DIRECTORY rather than on each file, which is what lets a
    /// stylesheet reference a face by a plain relative name: `url(fonts/x.woff2)` inside
    /// `assets/<build>/app.css` resolves to `assets/<build>/fonts/x.woff2` with no build-time
    /// rewriting, and immutability still holds because any change to any file changes
    /// `<build>` and so changes every address in the set.
    | Asset of build: string * path: string
    /// The web manifest, which is what makes the shell installable — and, once installed,
    /// what makes it launch without the browser's chrome (`WebApp`). NOT fingerprinted: a
    /// browser re-reads it by the address the document names, and the document names this.
    | Manifest
    /// The app icon the manifest and the head both point at. Constant bytes in the binary,
    /// so a session with no assets directory beside it still has a mark.
    | Icon
    /// WebRTC offer in, answer out. The only interactive HTTP surface.
    | Signal
    /// The auth probe: mints the peer token a data channel's `PeerHello` needs.
    | Me
    /// Begin the authorization-code + PKCE bounce through the Manager.
    | Login
    /// Land the bounce back here.
    | Callback
    /// Immutable chunk `index` of the event log. Named `Events` after the path, not
    /// after `EventChunk` — that module already exists in the domain, and one identifier
    /// meaning two things is what makes F# symbols hard to find.
    | Events of index: int
    /// Immutable chunk `index` of a terminal's transcript (Plan 13) — the history leg of
    /// the terminal feed, cacheable on exactly the same argument as `Events`. The terminal
    /// is carried as a raw string because a route is a PATH, and validating it into a
    /// `TerminalId` is the server's job at dispatch, not the router's at parse.
    | TerminalTranscript of terminal: string * index: int
    /// The keyframe for transcript line `seq` of a terminal (Plan 14, stage 3) — the screen
    /// a ranged replay starts from. Immutable on the same argument the chunks are: a
    /// keyframe is written once, at a position that never moves.
    | TerminalKeyframe of terminal: string * seq: int
    /// The Claude panel's current credential status.
    | ClaudeStatus
    /// One of the Claude panel's write actions.
    | Claude of action: ClaudeAction
    /// The GitHub panel's current credential status (Plan 14).
    | GitHubStatus
    /// One of the GitHub panel's write actions.
    | GitHub of action: GitHubAction
    /// The session's read-only query surface (Plan 15): one multiplexed SSE stream
    /// carrying every registered query's declaration and value. It is a STREAM rather
    /// than a fetch-plus-stream pair because its opening burst already is the snapshot,
    /// so a second route would only be a second thing to keep correct.
    ///
    /// This is where the Repos panel's `/repos*` routes went. Their listing is now a
    /// query, and their three write actions were retired outright: a human asks the agent
    /// to add a repo, and the mutation lands in the timeline attributed (Plan 15).
    | Queries

module SessionRoute =

    /// Where every static file sits, relative to whatever the session is mounted at.
    /// Public because the Manager serves its own set from its own origin root and has to
    /// recognise the same addresses — the two servers agreeing by inspection is exactly what
    /// this type exists to prevent.
    let assetsPrefix = "assets/"

    /// A path inside the asset set, as a build emits it. Segments are ordinary file names, so
    /// anything outside that shape is simply not a route — which is also what keeps a path
    /// the server may look up from carrying a dot-segment.
    let private isAssetPath (segments: string list) =
        let ok (c: char) =
            (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
            || c = '.' || c = '-' || c = '_'
        not segments.IsEmpty
        && segments |> List.forall (fun s -> s <> "" && not (s.Contains "..") && Seq.forall ok s)

    let private claudeSegment (action: ClaudeAction) =
        match action with
        | ClaudeAction.Begin -> "begin"
        | ClaudeAction.Complete -> "complete"
        | ClaudeAction.Token -> "token"
        | ClaudeAction.Disconnect -> "disconnect"

    let private githubSegment (action: GitHubAction) =
        match action with
        | GitHubAction.Begin -> "begin"
        | GitHubAction.Poll -> "poll"
        | GitHubAction.Token -> "token"
        | GitHubAction.Disconnect -> "disconnect"

    /// A route as a URL relative to whatever the session is mounted at. Never begins with
    /// `/` — that is the whole point (see the type's remarks). `Shell` is the empty
    /// string, which resolves to the mount point itself.
    let relative (route: SessionRoute) : string =
        match route with
        | Shell -> ""
        | Asset (build, path) -> assetsPrefix + build + "/" + path
        | Manifest -> "manifest.webmanifest"
        | Icon -> "icon.png"
        | Signal -> "signal"
        | Me -> "me"
        | Login -> "login"
        | Callback -> "callback"
        | Events index -> sprintf "events/%d" index
        | TerminalTranscript (terminal, index) -> sprintf "terminals/%s/%d" terminal index
        | TerminalKeyframe (terminal, seq) -> sprintf "terminals/%s/keyframes/%d" terminal seq
        | ClaudeStatus -> "claude"
        | Claude action -> "claude/" + claudeSegment action
        | GitHubStatus -> "github"
        | GitHub action -> "github/" + githubSegment action
        | Queries -> "queries"

    /// A route as an absolute URL under a session's address — what a client outside a
    /// browser needs, having no document base to resolve against. The single `/` between
    /// the two halves lives here, so no caller writes a leading slash of its own.
    let at (sessionUrl: string) (route: SessionRoute) : string =
        sessionUrl.TrimEnd '/' + "/" + relative route

    /// The route a request is for, or None when the session serves nothing there — which
    /// includes a known path reached with the wrong method, so a mismatch 404s exactly as
    /// an unknown one does. Taking the method here is what lets the server dispatch with a
    /// single match over this type: a new case then fails the build until every consumer
    /// handles it, which is the reason the contract is a union at all.
    let parse (method: string) (path: string) : SessionRoute option =
        let segments = path.Trim('/').Split '/' |> Array.toList
        match method, segments with
        | "GET", [ "" ] -> Some Shell
        | "POST", [ "signal" ] -> Some Signal
        | "GET", [ "me" ] -> Some Me
        | "GET", [ "manifest.webmanifest" ] -> Some Manifest
        | "GET", [ "icon.png" ] -> Some Icon
        // Everything static, by path, with no opinion about what is in there.
        | "GET", "assets" :: build :: path when isAssetPath (build :: path) ->
            Some (Asset (build, String.concat "/" path))
        | "GET", [ "login" ] -> Some Login
        | "GET", [ "callback" ] -> Some Callback
        | "GET", [ "events"; index ] ->
            match System.Int32.TryParse index with
            | true, parsed when parsed >= 0 -> Some (Events parsed)
            | _ -> None
        | "GET", [ "terminals"; terminal; "keyframes"; seq ] ->
            match System.Int32.TryParse seq with
            | true, parsed when parsed >= 0 && terminal <> "" -> Some (TerminalKeyframe (terminal, parsed))
            | _ -> None
        | "GET", [ "terminals"; terminal; index ] ->
            match System.Int32.TryParse index with
            | true, parsed when parsed >= 0 && terminal <> "" -> Some (TerminalTranscript (terminal, parsed))
            | _ -> None
        | "GET", [ "claude" ] -> Some ClaudeStatus
        | "POST", [ "claude"; "begin" ] -> Some (Claude ClaudeAction.Begin)
        | "POST", [ "claude"; "complete" ] -> Some (Claude ClaudeAction.Complete)
        | "POST", [ "claude"; "token" ] -> Some (Claude ClaudeAction.Token)
        | "POST", [ "claude"; "disconnect" ] -> Some (Claude ClaudeAction.Disconnect)
        | "GET", [ "github" ] -> Some GitHubStatus
        | "POST", [ "github"; "begin" ] -> Some (GitHub GitHubAction.Begin)
        | "POST", [ "github"; "poll" ] -> Some (GitHub GitHubAction.Poll)
        | "POST", [ "github"; "token" ] -> Some (GitHub GitHubAction.Token)
        | "POST", [ "github"; "disconnect" ] -> Some (GitHub GitHubAction.Disconnect)
        | "GET", [ "queries" ] -> Some Queries
        | _ -> None

    /// The route a request is for when this session is served under `mount` (`""` at an
    /// origin root). The operator's proxy forwards the PUBLIC path unchanged, so a
    /// path-mounted session sees its own prefix and strips it here — one place, the same
    /// string the shell's `<base href>` and the cookie's `Path` are built from. A request
    /// that does not carry the prefix is not this session's and gets the ordinary 404.
    let parseUnder (mount: string) (method: string) (path: string) : SessionRoute option =
        if mount = "" then parse method path
        elif path = mount then parse method "/"
        elif path.StartsWith (mount + "/") then parse method (path.Substring mount.Length)
        else None

/// Which build's asset set a document should name: a digest over every static file this
/// process ships. It used to be one digest per asset, which meant a renderer had to be handed
/// each of them and a new asset changed the type. A set has one address, so a document that
/// has this can name ANY file in it — including one this code has never heard of.
type AssetBuild =
    | AssetBuild of digest: string

module AssetBuild =

    /// Where `file` is served for this build — the URL a document should name. Relative, like
    /// every route: it resolves against the shell's `<base href>`, and the stylesheet's own
    /// relative `url()`s then resolve against IT. Takes the declared file rather than a path,
    /// so a document cannot name one this build does not ship.
    let url (AssetBuild digest) (file: AssetFile) : string =
        SessionRoute.relative (Asset (digest, AssetFile.path file))

/// What the two static surfaces may be cached for, stated once because the session server and
/// the Manager UI both serve them and the pair only works together: the shell is the document
/// that NAMES the fingerprinted assets, so caching it would pin the whole UI to the build it
/// was rendered against — which is exactly the bug that produced these values (a 24-hour
/// window on stable URLs made every release invisible for a day).
///
/// Alongside `EventChunk.cacheControl`, which is the same idea for the event log.
module CachePolicy =

    /// A fingerprinted asset: the address changes whenever the bytes do, so a cache entry can
    /// never be stale — only unused. `public`, unlike the event chunks' `private`: these are
    /// ungated static bytes, identical for every user.
    let asset = "public, max-age=31536000, immutable"

    /// The shell. `no-cache` is "revalidate before every use", NOT "do not store" — the
    /// browser keeps the copy and asks; an `ETag` turns the usual answer into a 304.
    let shell = "no-cache"
