namespace Yession.Domain

/// Where Yession is reachable from OUTSIDE this machine (docs/plans/09).
///
/// Two facts have to hold together: the Manager's public origin — it is the OIDC issuer
/// every session bounces its users through — and where each session's own port is
/// reachable. They are ONE value here because of the four combinations three are legal
/// deployments and the fourth is always broken: sessions fronted while the Manager that
/// authorizes their users answers only on loopback sends a remote browser to
/// `http://127.0.0.1` to log in, every time. `PublicAccess.create` is the only
/// constructor, so that fourth state is rejected once, at boot, and nothing downstream
/// can hold it.
///
/// Pure — no environment reads. The process boundary supplies the two raw strings
/// (`YESSION_MANAGER_URL`, `YESSION_SESSION_URL`); this decides what they mean.

/// A Manager's public origin: scheme + host, optional port, no path. Private, so a
/// `PublicAccess` cannot carry an unvalidated one.
type ManagerOrigin = private ManagerOrigin of string

/// Where sessions are reachable, as a template over a session's own identity: `{port}`
/// (its OS-assigned port) and `{id}` (its session id). The placeholders are what let one
/// declaration describe whichever topology the operator's proxy implements — a port
/// mirror (`https://host:{port}`), a subdomain per session (`https://{id}.example.com`),
/// or a path per session (`https://example.com/s/{id}`) — instead of Yession picking one
/// on their behalf.
type SessionTemplate = private SessionTemplate of string

/// A session's resolved public address. `Url` and `Mount` are derived together from one
/// template so they cannot disagree about where the session lives.
type SessionAddress =
    { /// The absolute URL a browser opens. No trailing slash — callers append the path
      /// they want (`/`, `/callback`).
      Url : string
      /// The path prefix this session is served under: `/s/01hx…` for a path-mounted
      /// session, `""` when it sits at an origin root (loopback, port mirror, subdomain).
      Mount : string }

module ManagerOrigin =
    let value (ManagerOrigin origin) = origin

module private UrlShape =

    /// Split `scheme://authority[/path]`. The one place either type parses a URL, so the
    /// two agree on what an origin is.
    let split (raw: string) : Result<string * string * string, string> =
        match [ "http://"; "https://" ] |> List.tryFind raw.StartsWith with
        | None -> Error "must start with http:// or https://"
        | Some scheme ->
            let rest = raw.Substring scheme.Length
            match rest.IndexOf '/' with
            | -1 when rest = "" -> Error "has no host"
            | -1 -> Ok (scheme, rest, "")
            | index when index = 0 -> Error "has no host"
            | index -> Ok (scheme, rest.Substring (0, index), rest.Substring index)

    /// The first `{…}` token that is not in `known`, or the first unterminated `{`.
    let unknownPlaceholder (known: string list) (raw: string) : string option =
        let rec scan (from: int) =
            match raw.IndexOf ('{', from) with
            | -1 -> None
            | start ->
                match raw.IndexOf ('}', start) with
                | -1 -> Some (raw.Substring start)
                | finish ->
                    let token = raw.Substring (start, finish - start + 1)
                    if List.contains token known then scan (finish + 1) else Some token
        scan 0

    let hasWhitespace (raw: string) =
        raw |> Seq.exists System.Char.IsWhiteSpace

module SessionTemplate =

    /// The placeholders a template may use. `{id}` and `{port}` are exactly the two facts
    /// the Manager's session registry publishes, so a proxy binding driven by that stream
    /// can implement any template written with them.
    let private known = [ "{id}"; "{port}" ]

    /// No configuration: each session at its own loopback port — the single-machine
    /// default, and what every session did before Plan 09. Expressed as a template so
    /// consumers never branch on whether a deployment is fronted.
    let loopback = SessionTemplate "http://127.0.0.1:{port}"

    let value (SessionTemplate template) = template

    /// Parse an operator's template. Rejects a bare origin with no placeholder: every
    /// session would resolve to the same URL, which is never what was meant (before this
    /// existed the port was appended implicitly, so `http://host:8443` silently produced
    /// `http://host:8443:54321`).
    let create (raw: string) : Result<SessionTemplate, string> =
        let trimmed = raw.Trim().TrimEnd '/'
        if trimmed = "" then Error "is empty"
        elif UrlShape.hasWhitespace trimmed then Error "contains whitespace"
        else
            match UrlShape.unknownPlaceholder known trimmed with
            | Some token -> Error (sprintf "has unknown placeholder %s — use {id} or {port}" token)
            | None ->
                match UrlShape.split trimmed with
                | Error e -> Error e
                | Ok _ when not (known |> List.exists trimmed.Contains) ->
                    Error "has no {id} or {port} placeholder, so every session would share one address"
                // A session must know the path it is mounted under BEFORE it binds a port
                // — the shell's `<base href>`, its cookie's `Path`, and the prefix it
                // strips off requests are all fixed at boot. So the mount may depend on
                // the session's id but not on its port. `{port}` in the authority is
                // untouched: that is port mirroring, where the mount is empty anyway.
                | Ok (_, _, path) when path.Contains "{port}" ->
                    Error
                        "puts {port} in its path — a path-mounted session is addressed by \
                         {id}, because it must know its mount before it has a port"
                | Ok _ -> Ok (SessionTemplate trimmed)

    /// The path a session is served under: `/s/01hx…` path-mounted, `""` at an origin
    /// root. Needs no port (see `create`), so a session can know it at boot.
    let mount (sessionId: SessionId) (SessionTemplate template) : string =
        match UrlShape.split (template.Replace("{id}", SessionId.value sessionId)) with
        | Ok (_, _, path) -> path
        | Error _ -> ""

    /// Resolve a session's address. `Mount` comes from the same template as `Url`, so the
    /// address a browser is given and the prefix the session serves under cannot disagree.
    let address (sessionId: SessionId) (port: int) (template: SessionTemplate) : SessionAddress =
        let (SessionTemplate raw) = template
        { Url = raw.Replace("{id}", SessionId.value sessionId).Replace("{port}", string port)
          Mount = mount sessionId template }

/// How this deployment is reached. Constructed only by `create`.
type PublicAccess =
    /// Nothing configured: single machine. The Manager answers at its own loopback
    /// endpoint and sessions at their loopback ports.
    | Loopback
    /// The Manager is fronted; sessions are not. A real deployment — manage sessions
    /// remotely, use them on the host — and what Plan 07 shipped before Plan 09.
    | ManagerOnly of manager: ManagerOrigin
    /// Both are fronted: the Manager at an origin, sessions wherever the template says.
    | Fronted of manager: ManagerOrigin * sessions: SessionTemplate

module PublicAccess =

    let private managerOrigin (raw: string) : Result<ManagerOrigin, string> =
        let trimmed = raw.Trim().TrimEnd '/'
        if UrlShape.hasWhitespace trimmed then Error "YESSION_MANAGER_URL contains whitespace"
        else
            match UrlShape.unknownPlaceholder [] trimmed with
            | Some token ->
                Error (
                    sprintf
                        "YESSION_MANAGER_URL has placeholder %s — the Manager is one address, so placeholders mean nothing here"
                        token)
            | None ->
                match UrlShape.split trimmed with
                | Error e -> Error (sprintf "YESSION_MANAGER_URL %s" e)
                // The Manager's routes are origin-anchored and its issuer is a
                // concatenation base (`<issuer>/connections/callback`), so a path prefix
                // would work only if the proxy stripped it again. Refused rather than
                // shipped as an unverified maybe.
                | Ok (_, _, path) when path <> "" ->
                    Error (
                        sprintf
                            "YESSION_MANAGER_URL has path %s — the Manager must be fronted at an origin root"
                            path)
                | Ok (scheme, authority, _) -> Ok (ManagerOrigin (scheme + authority))

    /// Read the pair as configured, `""` for unset. The illegal combination is an Error
    /// here and nowhere else: after this returns, every remaining state is deployable.
    let create (managerUrl: string) (sessionUrl: string) : Result<PublicAccess, string> =
        match managerUrl.Trim (), sessionUrl.Trim () with
        | "", "" -> Ok Loopback
        | "", _ ->
            Error
                "YESSION_SESSION_URL is set but YESSION_MANAGER_URL is not: sessions would be \
                 reachable remotely while the Manager that authorizes their users answers only \
                 on loopback, so every remote login would bounce to 127.0.0.1. Set both, or neither."
        | manager, "" -> managerOrigin manager |> Result.map ManagerOnly
        | manager, sessions ->
            managerOrigin manager
            |> Result.bind (fun origin ->
                SessionTemplate.create sessions
                |> Result.mapError (sprintf "YESSION_SESSION_URL %s")
                |> Result.map (fun template -> Fronted (origin, template)))

    /// The Manager's public URL, or None when it answers on loopback only (the caller
    /// substitutes its own endpoint URL — it knows its port, this type does not).
    let managerUrl (access: PublicAccess) : string option =
        match access with
        | Loopback -> None
        | ManagerOnly origin
        | Fronted (origin, _) -> Some (ManagerOrigin.value origin)

    /// The Manager's public URL, falling back to whatever endpoint the caller can offer
    /// when this deployment is loopback-only.
    ///
    /// ONE statement of the precedence, so the Manager's OIDC issuer and the origin a
    /// session publishes to its clients cannot disagree: the CONFIGURED public origin
    /// always wins, because a loopback endpoint is not reachable from a browser that is
    /// not on this machine. The fallback is for the deployment where it is — a single
    /// machine, where the Manager's own `http://127.0.0.1:<port>` is exactly right.
    let managerUrlOr (fallback: string option) (access: PublicAccess) : string option =
        match managerUrl access with
        | Some url -> Some url
        | None -> fallback

    /// Where sessions live. Total — an unfronted deployment serves the loopback
    /// template — so no consumer branches on deployment shape.
    let sessions (access: PublicAccess) : SessionTemplate =
        match access with
        | Loopback
        | ManagerOnly _ -> SessionTemplate.loopback
        | Fronted (_, template) -> template

    /// A session's public address under this deployment.
    let sessionAddress (sessionId: SessionId) (port: int) (access: PublicAccess) : SessionAddress =
        sessions access |> SessionTemplate.address sessionId port

    /// The path a session is served under, known at boot — before it binds a port — so it
    /// can set its shell's `<base href>`, scope its cookie, and strip the prefix off
    /// incoming requests. `""` unless the deployment path-mounts its sessions.
    let sessionMount (sessionId: SessionId) (access: PublicAccess) : string =
        sessions access |> SessionTemplate.mount sessionId

    /// Does a session keep its address across launches?
    ///
    /// True when the template addresses sessions by `{id}` alone. The address is then
    /// derived from the session rather than from whatever port it happened to bind, so it
    /// is the same string before and after a stop, a reap, or a resume — and a browser's
    /// storage, which is partitioned by ORIGIN, survives with it.
    ///
    /// False wherever `{port}` appears, including the loopback default: the OS assigns a
    /// fresh port per launch, so the session returns to an origin the browser has never
    /// seen, with whatever its user wrote offline stranded in a database nothing will open
    /// again. That is a real constraint of not path-mounting rather than a defect — the
    /// server still has everything that reached it — but it is the deployment the product
    /// most needs to say so on, because it is the one you get with no configuration at all.
    let sessionAddressIsStable (access: PublicAccess) : bool =
        sessions access |> SessionTemplate.value |> fun template -> not (template.Contains "{port}")
