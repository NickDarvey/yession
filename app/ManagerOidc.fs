module Yession.Host.ManagerOidc

// The Manager's OIDC provider endpoint (Node adapter over Yession.Oidc): discovery,
// JWKS, /authorize (code + PKCE) and /token, sharing the Manager's HTTP endpoint with
// the control RPC and the management UI.
//
// The signing keypair is generated HERE, once per Manager start, through jose on Node's
// built-in WebCrypto with `extractable = false`: the private key is a non-extractable
// CryptoKey — no code path can serialize it — living only in process memory and dying
// with the process. Nothing signed outlives it: children die with the Manager (parent
// guard), and codes/cookies are equally ephemeral.
//
// Routes (all `cache-control: no-store`):
//   GET  /.well-known/openid-configuration   discovery document
//   GET  /jwks                               the PUBLIC signing key
//   GET  /authorize                          code + PKCE; user via the injected strategy
//   POST /token                              code exchange -> signed ID token

open Fable.Core
open Fable.Core.JsInterop
open Yession.Domain
open Yession.Oidc
open Yession.Host.Interop

[<Emit("new URL($0, 'http://local').pathname")>]
let private pathnameOf (url: string) : string = jsNative

[<Emit("new URL($0, 'http://local').searchParams.get($1)")>]
let private queryOf (url: string) (name: string) : string option = jsNative

[<Emit("encodeURIComponent($0)")>]
let private urlEncode (s: string) : string = jsNative

/// The JWKS document: the public JWK annotated with its id and algorithm. Only ever
/// called with the PUBLIC key — exporting the private key would throw (non-extractable).
[<Emit("JSON.stringify({ keys: [{ ...$0, kid: $1, alg: 'EdDSA', use: 'sig' }] })")>]
let private jwksJson (publicJwk: obj) (kid: string) : string = jsNative

let private readBody (req: IncomingMessage) (cont: string -> unit) =
    let mutable acc = ""
    req.on ("data", fun chunk -> acc <- acc + bufferToString chunk) |> ignore
    req.on ("end", fun _ -> cont acc) |> ignore

let private respond (res: ServerResponse) (status: int) (contentType: string) (body: string) =
    res.writeHead (status, createObj [ "content-type", box contentType; "cache-control", box "no-store" ]) |> ignore
    res.``end`` body

let private redirect (res: ServerResponse) (location: string) =
    res.writeHead (302, createObj [ "location", box location; "cache-control", box "no-store" ]) |> ignore
    res.``end`` ""

type Provider =
    { /// Handle an OIDC route; false when the path is not one, so the composing server
      /// (control RPC + management UI share the port) falls through.
      TryHandle : IncomingMessage -> ServerResponse -> bool
      /// Dynamic client registration, called from the control channel: bind a client to
      /// the calling launch's control secret. Re-registration replaces (relaunch).
      RegisterClient : string -> SessionId -> string -> RegisterClientResponse
      /// The launch died — its client registration dies with it.
      RevokeByControlSecret : string -> unit }

/// Create the provider. `issuerOf` is read lazily per request because the Manager's
/// endpoint port is only known once its server listens. `onTokenIssued` fires on every
/// successful /token redeem with the launch's control secret, the client session, the
/// authenticated subject, the verified claims behind it (None = unattributed), and the
/// peer that rode the bounce (docs/plans/07) — the Manager's one chance to RECORD which
/// user (and peer) it verified into which launch (Plan 06: the bindings behind ABAC).
let create
    (issuerOf: unit -> string)
    (strategy: AuthenticationStrategy)
    (onTokenIssued: string -> SessionId -> UserId -> UserClaims option -> PeerId option -> unit)
    : Async<Provider> =
    async {
        // Ed25519 via WebCrypto; the `false` here is the non-extractability invariant.
        let! keys = Fable.Jose.generateKeyPair "EdDSA" (createObj [ "extractable" ==> false ]) |> Interop.awaitPromise
        let! publicJwk = Fable.Jose.exportJWK keys.publicKey |> Interop.awaitPromise
        let kid = randomSecret ()

        let registry = ClientRegistry (randomSecret)
        let nowUnix () = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds ()
        let codes = CodeStore (randomSecret, nowUnix, sha256Base64Url)

        let discoveryJson () =
            let issuer = issuerOf ()
            Wire.toString
                Wire.discovery
                { Issuer = issuer
                  AuthorizationEndpoint = issuer + "/authorize"
                  TokenEndpoint = issuer + "/token"
                  JwksUri = issuer + "/jwks" }

        let handleAuthorize (req: IncomingMessage) (res: ServerResponse) =
            match Provider.authorize registry (queryOf req.url) with
            | Error (ClientError message) -> respond res 400 "text/plain" message
            | Error (RedirectableError (redirectUri, error, state)) ->
                let stateSuffix = state |> Option.map (fun s -> "&state=" + urlEncode s) |> Option.defaultValue ""
                redirect res (sprintf "%s?error=%s%s" redirectUri (urlEncode error) stateSuffix)
            | Ok request ->
                Async.StartImmediate (
                    async {
                        let context =
                            { RemoteAddress = remoteAddressOf req
                              Query = queryOf req.url
                              Header = fun name -> headerOf req name }
                        let! outcome = strategy.Authenticate context
                        match GrantedIdentity.ofOutcome outcome with
                        | None ->
                            let reason = match outcome with Denied r -> r | _ -> "denied"
                            respond res 401 "text/plain" reason
                        | Some identity ->
                            let code = codes.Issue request.Client request.Challenge identity request.Peer
                            redirect
                                res
                                (sprintf
                                    "%s?code=%s&state=%s"
                                    request.Client.RedirectUri
                                    (urlEncode code)
                                    (urlEncode request.State))
                    })

        let handleToken (req: IncomingMessage) (res: ServerResponse) =
            readBody req (fun body ->
                match Provider.token registry codes timingSafeEqualStr (Form.parse body) with
                | Error InvalidClient -> respond res 401 "application/json" (Wire.toString Wire.tokenError "invalid_client")
                | Error (InvalidGrant _) -> respond res 400 "application/json" (Wire.toString Wire.tokenError "invalid_grant")
                | Error (InvalidRequest _) -> respond res 400 "application/json" (Wire.toString Wire.tokenError "invalid_request")
                | Ok grant ->
                    // Record the binding BEFORE the token response leaves: the moment
                    // the RP holds the token, the Manager already knows the user.
                    // ClientId is a session id by construction (only DCR from the
                    // control channel registers clients); a parse failure is a bug
                    // surfaced by the policy denying, never a crash here.
                    (match SessionId.create grant.Client.ClientId, UserId.create grant.Identity.Subject with
                     | Ok sessionId, Ok subject ->
                         onTokenIssued grant.Client.ControlSecret sessionId subject grant.Identity.Claims grant.Peer
                     | _ -> ())
                    // Standard profile claims when the strategy attributed a real user,
                    // plus `yession_attribution` — the RP-side discriminator between a
                    // durable user identity and shared unattributed access.
                    let payload =
                        [ yield "yession_attribution",
                                box (match grant.Identity.Claims with Some _ -> "user" | None -> "unattributed")
                          match grant.Identity.Claims with
                          | Some claims ->
                              match claims.DisplayName with Some v -> yield "name", box v | None -> ()
                              match claims.Email with Some v -> yield "email", box v | None -> ()
                              match claims.Picture with Some v -> yield "picture", box v | None -> ()
                          | None -> () ]
                    Async.StartImmediate (
                        async {
                            let! idToken =
                                (Fable.Jose.signJwt (createObj payload))
                                    .setProtectedHeader(createObj [ "alg" ==> "EdDSA"; "kid" ==> kid ])
                                    .setIssuer(issuerOf ())
                                    .setSubject(grant.Identity.Subject)
                                    .setAudience(grant.Client.ClientId)
                                    .setIssuedAt()
                                    .setExpirationTime("10m")
                                    .sign keys.privateKey
                                |> Interop.awaitPromise
                            respond
                                res
                                200
                                "application/json"
                                (Wire.toString
                                    Wire.tokenResponse
                                    { AccessToken = randomSecret ()
                                      TokenType = "Bearer"
                                      IdToken = idToken
                                      ExpiresIn = 600 })
                        }))

        return
            { TryHandle =
                fun req res ->
                    match req.``method``, pathnameOf req.url with
                    | "GET", "/.well-known/openid-configuration" ->
                        respond res 200 "application/json" (discoveryJson ())
                        true
                    | "GET", "/jwks" ->
                        respond res 200 "application/json" (jwksJson publicJwk kid)
                        true
                    | "GET", "/authorize" ->
                        handleAuthorize req res
                        true
                    | "POST", "/token" ->
                        handleToken req res
                        true
                    | _ -> false
              RegisterClient =
                fun controlSecret sessionId redirectUri ->
                    let client = registry.Register controlSecret sessionId redirectUri
                    { ClientId = client.ClientId
                      ClientSecret = client.ClientSecret
                      Issuer = issuerOf () }
              RevokeByControlSecret = registry.RevokeByControlSecret }
    }
