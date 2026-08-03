module Yession.Host.SessionAuth

// The Session Process as an OAuth/OIDC client (RP), over the certified `openid-client`
// library: discovery, PKCE, the code exchange, and ID-token validation are all the
// certified implementation — nothing protocol-shaped is hand-rolled here. This module
// only adapts it to the session's plain `node:http` surface: begin-login builds the
// authorize redirect, the callback redeems the code and mints an HttpOnly cookie, and
// `/me` turns a valid cookie into a peer token (see SessionProcess.Auth).
//
// Configuration is DEFERRED: the client registers with the Manager only after its
// server listens (the redirect URI needs the OS-assigned port), so the Auth value
// exists before its RP configuration does. Until `Configure` resolves, `BeginLogin`
// yields None (surfaced as a 503 — unreachable in practice, because the session
// registers before printing its readiness line).

open Fable.Core
open Fable.Core.JsInterop
open Yession.Domain
open Yession.SessionProcess
open Fable.OpenIdClient
open Yession.Host.Interop

[<Fable.Core.Emit("new URL($0, 'http://local').searchParams.get($1)")>]
let private queryOf (url: string) (name: string) : string option = Fable.Core.Util.jsNative

type Auth =
    { /// Complete the RP configuration after dynamic client registration: run OIDC
      /// discovery against the issuer and bind the client credentials + redirect URI.
      Configure : string -> string -> string -> string -> Async<Result<unit, string>>
      /// Does the request carry a cookie this process minted?
      IsAuthenticated : IncomingMessage -> bool
      /// The authenticated identity behind the request's cookie, when any: the subject
      /// plus the attribution the validated ID token carried (docs/plans/07).
      IdentityOf : IncomingMessage -> CookieIdentity option
      /// Start a login: mint state + PKCE verifier, return the authorize URL. The
      /// browser's peer id (when the /login request carried one) rides along so the
      /// Manager can witness which peer signed in (docs/plans/07).
      /// None until `Configure` has completed.
      BeginLogin : PeerId option -> Async<string option>
      /// Handle the callback request URL. Ok = the `Set-Cookie` value to send with the
      /// redirect back to `/`; Error = (status, message).
      HandleCallback : string -> Async<Result<string, int * string>>
      CookieName : string }

/// `mount` is the path this session is served under (`""` at an origin root): the
/// auth cookie is scoped to it, so a path-mounted session's cookie is not sent to its
/// siblings on the same host.
let create (sessionId: SessionId) (mount: string) : Auth =
    let cookieName = Cookies.sessionCookieName sessionId
    let nowUnix () = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds ()
    let pendingLogins = PendingLogins (nowUnix)
    let cookies = CookieSessions (randomSecret)
    let mutable configuration : (Configuration * string) option = None

    let identityOf (req: IncomingMessage) : CookieIdentity option =
        Cookies.tryFind cookieName (headerOf req "cookie")
        |> Option.bind cookies.IdentityOf

    { Configure =
        fun issuer clientId clientSecret redirectUri ->
            async {
                try
                    let! resolved =
                        discovery
                            (newUrl issuer)
                            clientId
                            clientSecret
                            // Loopback HTTP issuer (RFC 8252 pattern) — see the binding.
                            (createObj [ "execute" ==> [| allowInsecureRequests |] ])
                        |> Interop.awaitPromise
                    configuration <- Some (resolved, redirectUri)
                    return Ok ()
                with e ->
                    return Error (sprintf "OIDC discovery against %s failed: %s" issuer e.Message)
            }
      IsAuthenticated = identityOf >> Option.isSome
      IdentityOf = identityOf
      BeginLogin =
        fun peer ->
            async {
                match configuration with
                | None -> return None
                | Some (config, redirectUri) ->
                    let verifier = randomPKCECodeVerifier ()
                    let! challenge = calculatePKCECodeChallenge verifier |> Interop.awaitPromise
                    let state = randomState ()
                    pendingLogins.Add state verifier
                    let url =
                        buildAuthorizationUrl
                            config
                            (createObj
                                [ "redirect_uri" ==> redirectUri
                                  "scope" ==> "openid"
                                  "state" ==> state
                                  "code_challenge" ==> challenge
                                  "code_challenge_method" ==> "S256"
                                  match peer with
                                  | Some peerId -> "peer_id" ==> PeerId.value peerId
                                  | None -> () ])
                    return Some (urlHref url)
            }
      HandleCallback =
        fun requestUrl ->
            async {
                match configuration with
                | None -> return Error (503, "session is still registering with its manager")
                | Some (config, redirectUri) ->
                    match queryOf requestUrl "state" |> Option.bind pendingLogins.Take with
                    | None -> return Error (400, "unknown or expired login; reopen the session URL")
                    | Some verifier ->
                        try
                            // The certified client redeems the code AND validates the ID
                            // token (signature via the discovered JWKS, iss, aud, exp)
                            // before this resolves.
                            let! tokens =
                                authorizationCodeGrant
                                    config
                                    (newUrlWithBase requestUrl redirectUri)
                                    (createObj
                                        [ "pkceCodeVerifier" ==> verifier
                                          "expectedState" ==> (queryOf requestUrl "state" |> Option.defaultValue "") ])
                                |> Interop.awaitPromise
                            let claims = tokens.claims ()
                            // The attribution rides the validated ID token: only the
                            // provider's own `yession_attribution = "user"` claim makes
                            // this cookie a real user — never anything client-supplied.
                            let attribution =
                                match claims.yession_attribution, UserId.create claims.sub with
                                | Some "user", Ok user -> AttributedUser user
                                | _ -> UnattributedAccess
                            let cookieValue =
                                cookies.Mint
                                    { Subject = claims.sub
                                      DisplayName = claims.name
                                      Attribution = attribution }
                            return Ok (Cookies.set cookieName mount cookieValue)
                        with e ->
                            return Error (401, sprintf "authorization failed: %s" e.Message)
            }
      CookieName = cookieName }
