namespace Yession.Domain

open System

/// Pure HTTP authorization primitives: cookie and form parsing for the session's
/// auth-gated surfaces. Deliberately small — the protocol itself (JWT, PKCE, discovery,
/// the code exchange) lives in the standard `jose` / `openid-client` libraries; only the
/// plumbing that gates plain `node:http` requests is hand-written, and it is pure so the
/// cheap test tier covers it.
module Cookies =

    /// Parse a `Cookie:` request header into (name, value) pairs. Tolerant: malformed
    /// segments are dropped, values keep any embedded `=`.
    let parse (header: string) : (string * string) list =
        header.Split ';'
        |> Array.toList
        |> List.choose (fun segment ->
            let segment = segment.Trim ()
            match segment.IndexOf '=' with
            | index when index > 0 -> Some (segment.Substring (0, index), segment.Substring (index + 1))
            | _ -> None)

    /// Find a cookie by name in an optional `Cookie:` header.
    let tryFind (name: string) (header: string option) : string option =
        header
        |> Option.bind (fun h -> parse h |> List.tryPick (fun (k, v) -> if k = name then Some v else None))

    /// A `Set-Cookie:` value for an HttpOnly session cookie, scoped to the path the
    /// session is served under (`""` at an origin root ⇒ `Path=/`). No `Max-Age`/`Expires`:
    /// the cookie lives with the browser session, matching the server side (auth state is
    /// in-memory and dies with the process).
    ///
    /// Scoping is a real narrowing where sessions share a host: a path-mounted session's
    /// cookie is no longer sent to its siblings. At an origin root the behaviour is
    /// unchanged, and the id in the cookie's NAME still carries the separation cookies
    /// cannot get from a port.
    let set (name: string) (mount: string) (value: string) : string =
        let path = if mount = "" then "/" else mount + "/"
        sprintf "%s=%s; Path=%s; HttpOnly; SameSite=Lax" name value path

    /// The session's auth-cookie name. Cookies on 127.0.0.1 are NOT port-scoped — the
    /// Manager and every Session Process share one browser cookie jar — so each session's
    /// cookie must be namespaced by its id to avoid clobbering.
    let sessionCookieName (sessionId: SessionId) : string =
        "yession_auth_" + SessionId.value sessionId

/// `application/x-www-form-urlencoded` body parsing (the token-endpoint request format).
module Form =

    let parse (body: string) : Map<string, string> =
        body.Split '&'
        |> Array.toList
        |> List.choose (fun pair ->
            match pair.IndexOf '=' with
            | index when index > 0 ->
                let decode (s: string) = Uri.UnescapeDataString (s.Replace ("+", " "))
                Some (decode (pair.Substring (0, index)), decode (pair.Substring (index + 1)))
            | _ -> None)
        |> Map.ofList
