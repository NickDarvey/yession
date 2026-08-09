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
    /// The browser client bundle, addressed by a digest of the bytes served. A build is a
    /// new address, which is what lets those bytes be cached forever.
    | ClientBundle of digest: string
    /// The locally built stylesheet (no CDN), addressed the same way.
    | AppCss of digest: string
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

    /// `<name>.<digest>.<ext>`, or the bare `<name>.<ext>` for the empty digest.
    ///
    /// The empty digest is NOT a second caching policy — it is the only address a shell has
    /// when the build output is missing, which is what keeps the "not built (run: build)"
    /// 404 reachable in development instead of rendering a page that asks for a hash of
    /// nothing.
    let private fingerprinted (name: string) (ext: string) (digest: string) : string =
        if digest = "" then sprintf "%s.%s" name ext
        else sprintf "%s.%s.%s" name digest ext

    /// The digest in `<name>.<digest>.<ext>`; `Some ""` for the bare `<name>.<ext>`; None for
    /// anything else. An EMPTY middle (`client..js`) is None deliberately: it would render
    /// back as `client.js`, and `relative` and `parse` are exact inverses.
    ///
    /// Splitting on `.` is safe because a digest is base64url, whose alphabet has no dot.
    let private fingerprintOf (name: string) (ext: string) (segment: string) : string option =
        let prefix = name + "."
        let suffix = "." + ext
        if segment = prefix + ext then Some ""
        elif segment.StartsWith prefix
             && segment.EndsWith suffix
             && segment.Length > prefix.Length + suffix.Length
        then Some (segment.Substring (prefix.Length, segment.Length - prefix.Length - suffix.Length))
        else None

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
        | ClientBundle digest -> fingerprinted "client" "js" digest
        | AppCss digest -> fingerprinted "app" "css" digest
        | Signal -> "signal"
        | Me -> "me"
        | Login -> "login"
        | Callback -> "callback"
        | Events index -> sprintf "events/%d" index
        | TerminalTranscript (terminal, index) -> sprintf "terminals/%s/%d" terminal index
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
        | "GET", [ "login" ] -> Some Login
        | "GET", [ "callback" ] -> Some Callback
        | "GET", [ "events"; index ] ->
            match System.Int32.TryParse index with
            | true, parsed when parsed >= 0 -> Some (Events parsed)
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
        // Last among the single-segment GETs, because a fingerprinted name is matched by
        // shape rather than by literal and would otherwise shadow the fixed paths above.
        | "GET", [ segment ] ->
            match fingerprintOf "client" "js" segment with
            | Some digest -> Some (ClientBundle digest)
            | None -> fingerprintOf "app" "css" segment |> Option.map AppCss
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

/// The digests a shell needs to address its own assets, carried together so a renderer cannot
/// take them in the wrong order — two bare strings would be silently swappable.
type AssetDigests =
    { Bundle: string
      Css: string }

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
