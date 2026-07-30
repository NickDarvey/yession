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

type SessionRoute =
    /// The client shell itself — the served page IS the app.
    | Shell
    /// The browser client bundle.
    | ClientBundle
    /// The locally built stylesheet (no CDN).
    | AppCss
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
    /// The Claude panel's current credential status.
    | ClaudeStatus
    /// One of the Claude panel's write actions.
    | Claude of action: ClaudeAction

module SessionRoute =

    let private claudeSegment (action: ClaudeAction) =
        match action with
        | ClaudeAction.Begin -> "begin"
        | ClaudeAction.Complete -> "complete"
        | ClaudeAction.Token -> "token"
        | ClaudeAction.Disconnect -> "disconnect"

    /// A route as a URL relative to whatever the session is mounted at. Never begins with
    /// `/` — that is the whole point (see the type's remarks). `Shell` is the empty
    /// string, which resolves to the mount point itself.
    let relative (route: SessionRoute) : string =
        match route with
        | Shell -> ""
        | ClientBundle -> "client.js"
        | AppCss -> "app.css"
        | Signal -> "signal"
        | Me -> "me"
        | Login -> "login"
        | Callback -> "callback"
        | Events index -> sprintf "events/%d" index
        | ClaudeStatus -> "claude"
        | Claude action -> "claude/" + claudeSegment action

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
        | "GET", [ "client.js" ] -> Some ClientBundle
        | "GET", [ "app.css" ] -> Some AppCss
        | "POST", [ "signal" ] -> Some Signal
        | "GET", [ "me" ] -> Some Me
        | "GET", [ "login" ] -> Some Login
        | "GET", [ "callback" ] -> Some Callback
        | "GET", [ "events"; index ] ->
            match System.Int32.TryParse index with
            | true, parsed when parsed >= 0 -> Some (Events parsed)
            | _ -> None
        | "GET", [ "claude" ] -> Some ClaudeStatus
        | "POST", [ "claude"; "begin" ] -> Some (Claude ClaudeAction.Begin)
        | "POST", [ "claude"; "complete" ] -> Some (Claude ClaudeAction.Complete)
        | "POST", [ "claude"; "token" ] -> Some (Claude ClaudeAction.Token)
        | "POST", [ "claude"; "disconnect" ] -> Some (Claude ClaudeAction.Disconnect)
        | _ -> None
