module Yession.Tests.Oidc

// Conformance checks for the OIDC provider (Manager = OP) and the session's access
// gating, in three layers:
//
//   * PURE (cheap tier): the provider core against the specs it implements — PKCE S256
//     against the RFC 7636 Appendix B vector, the RFC 6749 §4.1.2.1/§5.2 error
//     taxonomy, single-use/expiring codes, registration lifecycle, wire codecs, and
//     the pure auth stores (pending logins, cookies, peer tokens).
//   * OP over HTTP ([Ports]): the real ManagerOidc endpoint driven as a raw OAuth
//     client — happy path with a jose-verified ID token (the second independent
//     verifier; the certified openid-client inside the session is the first), code
//     replay, wrong verifier, wrong secret.
//   * The composed flow ([Ports]): a real Manager + child Session Process, the full
//     login bounce via OidcHttp, the gated data surfaces, the ungated cached shell
//     (offline-first invariant), and registration revocation on stop.
//
// The key non-extractability invariant is pinned at the mechanism level: a jose
// keypair generated `extractable = false` must refuse to export its private half.

open System
open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto
open Yession.Domain
open Yession.Oidc
open Yession.Host
open Yession.Tests.Support

// --- Pure: PKCE ---------------------------------------------------------------------

let private pkceTests =
    testList "PKCE (RFC 7636)" [
        testCase "S256 matches the Appendix B vector" <| fun () ->
            let verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"
            let challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"
            Expect.equal (Interop.sha256Base64Url verifier) challenge "BASE64URL(SHA256(verifier))"
    ]

// --- Pure: provider core ------------------------------------------------------------

let private sessionId = SessionId.create "op-session" |> expect

/// A registry+codestore pair over injected primitives; the clock is a mutable cell so
/// expiry is deterministic.
let private makeProvider () =
    let mutable now = 1000L
    let mutable counter = 0
    let mint () =
        counter <- counter + 1
        sprintf "minted-%d" counter
    let registry = ClientRegistry (mint)
    let codes = CodeStore (mint, (fun () -> now), Interop.sha256Base64Url)
    registry, codes, (fun t -> now <- t)

let private verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"
let private challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"

/// The unattributed identity the localhost strategy grants.
let private localIdentity : GrantedIdentity = { Subject = "local"; Claims = None }

let private queryOfList (pairs: (string * string) list) : string -> string option =
    fun name -> pairs |> List.tryPick (fun (k, v) -> if k = name then Some v else None)

let private goodAuthorize (client: RegisteredClient) =
    [ "client_id", client.ClientId
      "redirect_uri", client.RedirectUri
      "response_type", "code"
      "state", "xyz"
      "code_challenge", challenge
      "code_challenge_method", "S256" ]

let private providerTests =
    testList "Provider core (RFC 6749 error taxonomy)" [
        testCase "registration binds a client to its control secret; revocation removes it; re-registration replaces" <| fun () ->
            let registry, _, _ = makeProvider ()
            let client = registry.Register "ctl-secret" sessionId "http://127.0.0.1:9001/callback"
            Expect.equal client.ClientId (SessionId.value sessionId) "client id = session id"
            Expect.equal (registry.TryFind client.ClientId) (Some client) "registered"
            let relaunched = registry.Register "ctl-secret-2" sessionId "http://127.0.0.1:9002/callback"
            Expect.equal (registry.TryFind client.ClientId) (Some relaunched) "relaunch replaces the registration"
            registry.RevokeByControlSecret "ctl-secret-2"
            Expect.equal (registry.TryFind client.ClientId) None "revoked with its launch's secret"

        testCase "authorize: unknown client and redirect_uri mismatch are 400s, NEVER redirects" <| fun () ->
            let registry, _, _ = makeProvider ()
            let client = registry.Register "s" sessionId "http://127.0.0.1:9001/callback"
            match Provider.authorize registry (queryOfList [ "client_id", "ghost" ]) with
            | Error (ClientError _) -> ()
            | other -> failwithf "expected ClientError for an unknown client, got %A" other
            match Provider.authorize registry (queryOfList [ "client_id", client.ClientId; "redirect_uri", "http://evil/" ]) with
            | Error (ClientError _) -> ()
            | other -> failwithf "expected ClientError for a redirect_uri mismatch, got %A" other

        testCase "authorize: wrong response_type redirects with unsupported_response_type; missing PKCE/state with invalid_request; plain is rejected" <| fun () ->
            let registry, _, _ = makeProvider ()
            let client = registry.Register "s" sessionId "http://127.0.0.1:9001/callback"
            let replace key value = goodAuthorize client |> List.map (fun (k, v) -> if k = key then k, value else k, v)
            let without key = goodAuthorize client |> List.filter (fun (k, _) -> k <> key)
            match Provider.authorize registry (queryOfList (replace "response_type" "token")) with
            | Error (RedirectableError (uri, "unsupported_response_type", Some "xyz")) ->
                Expect.equal uri client.RedirectUri "redirects to the validated URI"
            | other -> failwithf "expected unsupported_response_type, got %A" other
            for broken in [ without "state"; without "code_challenge"; replace "code_challenge_method" "plain" ] do
                match Provider.authorize registry (queryOfList broken) with
                | Error (RedirectableError (_, "invalid_request", _)) -> ()
                | other -> failwithf "expected invalid_request, got %A" other
            match Provider.authorize registry (queryOfList (goodAuthorize client)) with
            | Ok request ->
                Expect.equal request.Challenge challenge "the challenge is bound"
                Expect.equal request.State "xyz" "the state rides through"
            | other -> failwithf "expected a validated request, got %A" other

        testCase "codes are single-use, burn on failed redeem, expire at 60s, and bind client + redirect_uri + verifier" <| fun () ->
            let registry, codes, setNow = makeProvider ()
            let client = registry.Register "s" sessionId "http://127.0.0.1:9001/callback"
            // Happy path, then replay.
            let code = codes.Issue client challenge localIdentity None
            Expect.equal (codes.Redeem code client.ClientId client.RedirectUri verifier) (Ok (localIdentity, None)) "redeems to the identity"
            match codes.Redeem code client.ClientId client.RedirectUri verifier with
            | Error _ -> ()
            | Ok _ -> failwith "a code redeems at most once"
            // A failed check burns the code too.
            let burned = codes.Issue client challenge localIdentity None
            match codes.Redeem burned client.ClientId client.RedirectUri "wrong-verifier" with
            | Error _ -> ()
            | Ok _ -> failwith "a bad verifier must not redeem"
            match codes.Redeem burned client.ClientId client.RedirectUri verifier with
            | Error _ -> ()
            | Ok _ -> failwith "a failed redeem must burn the code"
            // Binding checks.
            let bound = codes.Issue client challenge localIdentity None
            match codes.Redeem bound "other-client" client.RedirectUri verifier with
            | Error _ -> ()
            | Ok _ -> failwith "a code is bound to its client"
            let bound2 = codes.Issue client challenge localIdentity None
            match codes.Redeem bound2 client.ClientId "http://evil/" verifier with
            | Error _ -> ()
            | Ok _ -> failwith "a code is bound to its redirect_uri"
            // Expiry.
            let stale = codes.Issue client challenge localIdentity None
            setNow 1061L
            match codes.Redeem stale client.ClientId client.RedirectUri verifier with
            | Error _ -> ()
            | Ok _ -> failwith "codes expire after 60s"

        testCase "token: the RFC 6749 §5.2 taxonomy — invalid_request, invalid_client, invalid_grant" <| fun () ->
            let registry, codes, _ = makeProvider ()
            let client = registry.Register "s" sessionId "http://127.0.0.1:9001/callback"
            let goodForm code =
                Map.ofList
                    [ "grant_type", "authorization_code"
                      "client_id", client.ClientId
                      "client_secret", client.ClientSecret
                      "code", code
                      "redirect_uri", client.RedirectUri
                      "code_verifier", verifier ]
            match Provider.token registry codes (=) (Map.ofList [ "grant_type", "password" ]) with
            | Error (InvalidRequest _) -> ()
            | other -> failwithf "expected invalid_request for a foreign grant type, got %A" other
            match Provider.token registry codes (=) (goodForm "c" |> Map.add "client_id" "ghost") with
            | Error InvalidClient -> ()
            | other -> failwithf "expected invalid_client for an unknown client, got %A" other
            match Provider.token registry codes (=) (goodForm "c" |> Map.add "client_secret" "stolen") with
            | Error InvalidClient -> ()
            | other -> failwithf "expected invalid_client for a wrong secret, got %A" other
            match Provider.token registry codes (=) (goodForm "never-issued") with
            | Error (InvalidGrant _) -> ()
            | other -> failwithf "expected invalid_grant for an unknown code, got %A" other
            let code = codes.Issue client challenge localIdentity None
            match Provider.token registry codes (=) (goodForm code) with
            | Ok grant -> Expect.equal grant.Identity.Subject "local" "the grant carries the subject"
            | other -> failwithf "expected a grant, got %A" other
    ]

// --- Pure: strategy, stores, wire, cookies/forms ------------------------------------

let private contextOf (address: string option) (headers: (string * string) list) : AuthenticationContext =
    { RemoteAddress = address
      Query = fun _ -> None
      Header = fun name -> headers |> List.tryPick (fun (k, v) -> if k = name then Some v else None) }

let private strategyTests =
    testList "Authentication strategy" [
        testCaseAsync "localhost grants unattributed loopback access only" <|
            async {
                let authAt address = Strategy.localhost.Authenticate (contextOf address [])
                for loopback in [ "127.0.0.1"; "::1"; "::ffff:127.0.0.1" ] do
                    match! authAt (Some loopback) with
                    | Unattributed subject -> Expect.equal subject "local" "the single local user"
                    | outcome -> failwithf "loopback %s must grant unattributed access, got %A" loopback outcome
                match! authAt (Some "192.168.1.50") with
                | Denied _ -> ()
                | outcome -> failwithf "a non-loopback address must be denied, got %A" outcome
                match! authAt None with
                | Denied _ -> ()
                | outcome -> failwithf "an unknown address must be denied, got %A" outcome
            }

        testCaseAsync "none denies everything" <|
            async {
                match! Strategy.none.Authenticate (contextOf (Some "127.0.0.1") [ Strategy.SubjectHeader, "nick" ]) with
                | Denied _ -> ()
                | outcome -> failwithf "the none strategy must deny, got %A" outcome
            }

        testCaseAsync "trusted-headers attributes the asserted user with all claim headers mapped" <|
            async {
                let headers =
                    [ Strategy.SubjectHeader, "nick@example.com"
                      Strategy.NameHeader, "Nick"
                      Strategy.EmailHeader, "nick@example.com"
                      Strategy.PictureHeader, "https://example.com/nick.png"
                      Strategy.ClaimsHeader, """{"caps":["admin"]}""" ]
                match! Strategy.trustedHeaders.Authenticate (contextOf None headers) with
                | Attributed claims ->
                    Expect.equal claims.Subject "nick@example.com" "subject from x-yession-user"
                    Expect.equal claims.DisplayName (Some "Nick") "display name mapped"
                    Expect.equal claims.Email (Some "nick@example.com") "email mapped"
                    Expect.equal claims.Picture (Some "https://example.com/nick.png") "picture mapped"
                    Expect.equal claims.Extra [ Strategy.ClaimsHeader, """{"caps":["admin"]}""" ] "extra claims carried verbatim"
                | outcome -> failwithf "expected an attributed user, got %A" outcome
            }

        testCaseAsync "trusted-headers denies without the subject header, even from loopback" <|
            async {
                match! Strategy.trustedHeaders.Authenticate (contextOf (Some "127.0.0.1") []) with
                | Denied _ -> ()
                | outcome -> failwithf "no identity header must deny, got %A" outcome
                match! Strategy.trustedHeaders.Authenticate (contextOf None [ Strategy.SubjectHeader, "   " ]) with
                | Denied _ -> ()
                | outcome -> failwithf "a blank identity header must deny, got %A" outcome
            }

        testCase "strategy names resolve exactly; unknown names are errors; no name is deny-everything" <| fun () ->
            Expect.equal (Strategy.ofName None |> Result.map (fun s -> s.Name)) (Ok "none") "no --auth is the none strategy"
            Expect.equal (Strategy.ofName (Some "localhost") |> Result.map (fun s -> s.Name)) (Ok "localhost") "localhost"
            Expect.equal (Strategy.ofName (Some "trusted-headers") |> Result.map (fun s -> s.Name)) (Ok "trusted-headers") "trusted-headers"
            Expect.isError (Strategy.ofName (Some "loopback")) "unknown names fail loudly"
    ]

let private storeTests =
    testList "Session auth stores" [
        testCase "pending logins are single-use and expire at 5 minutes" <| fun () ->
            let mutable now = 0L
            let logins = Yession.SessionProcess.PendingLogins (fun () -> now)
            logins.Add "state-1" "verifier-1"
            Expect.equal (logins.Take "state-1") (Some "verifier-1") "takes once"
            Expect.equal (logins.Take "state-1") None "single use"
            logins.Add "state-2" "verifier-2"
            now <- 301L
            Expect.equal (logins.Take "state-2") None "expired logins do not take"
            Expect.equal (logins.Take "never-added") None "unknown states do not take"

        testCase "cookie sessions map minted values to identities; peer tokens carry their minted attribution" <| fun () ->
            let mutable counter = 0
            let mint () = counter <- counter + 1; sprintf "value-%d" counter
            let user = UserId.create "nick@example.com" |> expect
            let cookies = Yession.SessionProcess.CookieSessions (mint)
            let localIdentity : Yession.SessionProcess.CookieIdentity =
                { Subject = "local"; DisplayName = None; Attribution = Yession.SessionProcess.UnattributedAccess }
            let value = cookies.Mint localIdentity
            Expect.equal (cookies.IdentityOf value) (Some localIdentity) "a minted cookie has its identity"
            Expect.equal (cookies.SubjectOf value) (Some "local") "and its subject"
            Expect.equal (cookies.IdentityOf "forged") None "a forged cookie has none"
            let tokens = Yession.SessionProcess.PeerTokens (mint)
            let unattributed = tokens.Mint Yession.SessionProcess.UnattributedAccess
            Expect.equal
                (tokens.Validate unattributed)
                (Some Yession.SessionProcess.UnattributedAccess)
                "a minted token validates with its attribution"
            let attributed = tokens.Mint (Yession.SessionProcess.AttributedUser user)
            Expect.equal
                (tokens.Validate attributed)
                (Some (Yession.SessionProcess.AttributedUser user))
                "an attributed token carries its user"
            Expect.equal (tokens.Validate "forged") None "a forged token does not validate"
    ]

let private wireTests =
    testList "Wire codecs" [
        testCase "DCR request/response and token response round-trip" <| fun () ->
            let request = { RegisterClientRequest.RedirectUri = "http://127.0.0.1:9001/callback" }
            Expect.equal (Wire.toString Wire.registerClientRequest request |> Wire.fromString Wire.registerClientRequest) (Ok request) "request"
            let response = { ClientId = "c"; ClientSecret = "s"; Issuer = "http://127.0.0.1:8321" }
            Expect.equal (Wire.toString Wire.registerClientResponse response |> Wire.fromString Wire.registerClientResponse) (Ok response) "response"
            let tokens = { AccessToken = "a"; TokenType = "Bearer"; IdToken = "jwt"; ExpiresIn = 600 }
            Expect.equal (Wire.toString Wire.tokenResponse tokens |> Wire.fromString Wire.tokenResponse) (Ok tokens) "token response"
            Expect.equal (Wire.toString Wire.tokenError "invalid_grant" |> Wire.fromString Wire.tokenError) (Ok "invalid_grant") "error body"

        testCase "the discovery document carries the OIDC Discovery §3 REQUIRED fields and the supported profiles" <| fun () ->
            let doc =
                { Issuer = "http://127.0.0.1:8321"
                  AuthorizationEndpoint = "http://127.0.0.1:8321/authorize"
                  TokenEndpoint = "http://127.0.0.1:8321/token"
                  JwksUri = "http://127.0.0.1:8321/jwks" }
            let json = Wire.toString Wire.discovery doc
            Expect.equal (Wire.fromString Wire.discovery json) (Ok doc) "round-trips"
            for required in
                [ "\"issuer\""; "\"authorization_endpoint\""; "\"token_endpoint\""; "\"jwks_uri\""
                  "\"response_types_supported\""; "\"subject_types_supported\""; "\"id_token_signing_alg_values_supported\"" ] do
                Expect.isTrue (json.Contains required) (sprintf "%s is REQUIRED by OIDC Discovery §3" required)
            Expect.isTrue (json.Contains "\"S256\"") "PKCE S256 is advertised"
            Expect.isTrue (json.Contains "\"EdDSA\"") "the signing alg is advertised"
            Expect.isFalse (json.Contains "\"plain\"") "the plain PKCE method is NOT advertised"

        testCase "cookie and form parsing tolerate hostile input" <| fun () ->
            Expect.equal (Cookies.parse "a=1; b=2=3;; c ;=x; d=") [ "a", "1"; "b", "2=3"; "d", "" ] "malformed segments drop; values keep ="
            Expect.equal (Cookies.tryFind "b" (Some "a=1; b=2")) (Some "2") "finds by name"
            Expect.equal (Cookies.tryFind "b" None) None "no header, no cookie"
            let name = Cookies.sessionCookieName sessionId
            Expect.equal name "yession_auth_op-session" "namespaced by session id (127.0.0.1 cookies are not port-scoped)"
            Expect.isTrue ((Cookies.set name "v").Contains "HttpOnly") "cookies are HttpOnly"
            Expect.equal (Form.parse "a=1&b=hello+world&c=%2Fpath&broken") (Map.ofList [ "a", "1"; "b", "hello world"; "c", "/path" ]) "urlencoded decode"
    ]

// --- Key non-extractability (the mechanism the provider relies on) ------------------

// jose's `exportJWK` rejects a non-extractable key by THROWING SYNCHRONOUSLY, and a synchronous
// throw while a Fable async computation is being built escapes the surrounding `try/with` — the
// trampoline only re-enters the protected continuation at bind boundaries, so whether it is caught
// depends on how many binds ran before, i.e. on which other tests exist. Deciding it in JS instead
// makes the assertion deterministic: the thunk is invoked inside the promise, so a sync throw and a
// rejection are the same observable outcome.
[<Emit("(async () => { try { await $0(); return false } catch { return true } })()")>]
let private refuses (attempt: unit -> JS.Promise<'a>) : JS.Promise<bool> = Fable.Core.Util.jsNative

let private keyTests =
    testList "Signing key non-extractability" [
        testCaseAsync "a jose keypair generated extractable=false refuses to export its private half" <|
            async {
                let! keys = Fable.Jose.generateKeyPair "EdDSA" (createObj [ "extractable" ==> false ]) |> Async.AwaitPromise
                Expect.isFalse keys.privateKey.extractable "the private key is non-extractable"
                let! publicRefused = refuses (fun () -> Fable.Jose.exportJWK keys.publicKey) |> Async.AwaitPromise
                Expect.isFalse publicRefused "the public half exports (JWKS depends on it)"
                let! privateRefused = refuses (fun () -> Fable.Jose.exportJWK keys.privateKey) |> Async.AwaitPromise
                Expect.isTrue privateRefused "exporting the private key must throw"
            }
    ]

// --- OP over HTTP ([Ports]): the real endpoint driven as a raw OAuth client ---------

[<Emit("new URL($0).searchParams.get($1)")>]
let private queryOfUrl (url: string) (name: string) : string option = Fable.Core.Util.jsNative

type private FormReply =
    abstract status : int
    abstract body : string

[<Emit("fetch($0, { method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' }, body: $1 }).then(async r => ({ status: r.status, body: await r.text() }))")>]
let private postFormRaw (url: string) (body: string) : JS.Promise<FormReply> = Fable.Core.Util.jsNative

let private opTests =
    testList "OP endpoint over HTTP" [
        testCaseAsync "authorize -> token issues a jose-verifiable ID token; replay, bad verifier, and bad secret are refused per spec" <|
            async {
                let mutable issuer = ""
                let! provider = ManagerOidc.create (fun () -> issuer) Strategy.localhost (fun _ _ _ _ _ -> ())
                let handler (req: Interop.IncomingMessage) (res: Interop.ServerResponse) =
                    if not (provider.TryHandle req res) then
                        res.writeHead (404, createObj [ "content-type", box "text/plain" ]) |> ignore
                        res.``end`` "not found"
                let server = Interop.createServer handler
                let! _ = Async.FromContinuations (fun (cont, _, _) -> server.listen (0, "127.0.0.1", fun () -> cont ()) |> ignore)
                issuer <- sprintf "http://127.0.0.1:%d" (Interop.serverPort server)

                let client = provider.RegisterClient "ctl-secret" sessionId "http://127.0.0.1:9/callback"
                let jar = OidcHttp.newJar ()

                // Discovery + JWKS: well-formed, and the JWKS carries ONLY the public key.
                let! discovery = OidcHttp.getWithJar jar (issuer + "/.well-known/openid-configuration")
                let decoded = Wire.fromString Wire.discovery discovery.Body |> expect
                Expect.equal decoded.Issuer issuer "issuer matches the serving origin"
                Expect.equal decoded.JwksUri (issuer + "/jwks") "jwks_uri points at the endpoint"
                let! jwks = OidcHttp.getWithJar jar decoded.JwksUri
                Expect.isTrue (jwks.Body.Contains "\"OKP\"" && jwks.Body.Contains "\"Ed25519\"" && jwks.Body.Contains "\"x\"") "an Ed25519 public JWK"
                Expect.isFalse (jwks.Body.Contains "\"d\"") "the private key parameter must NEVER appear"

                let authorizeUrl () =
                    sprintf
                        "%s?client_id=%s&redirect_uri=%s&response_type=code&state=st-1&code_challenge=%s&code_challenge_method=S256"
                        decoded.AuthorizationEndpoint
                        client.ClientId
                        (Uri.EscapeDataString "http://127.0.0.1:9/callback")
                        challenge
                let tokenForm (code: string) (secret: string) (codeVerifier: string) =
                    sprintf
                        "grant_type=authorization_code&client_id=%s&client_secret=%s&code=%s&redirect_uri=%s&code_verifier=%s"
                        client.ClientId
                        secret
                        code
                        (Uri.EscapeDataString "http://127.0.0.1:9/callback")
                        codeVerifier
                let issueCode () =
                    async {
                        let! reply = OidcHttp.getWithJar jar (authorizeUrl ())
                        Expect.equal reply.Status 302 "authorize redirects (trust-localhost strategy)"
                        return queryOfUrl reply.Location "code" |> Option.defaultValue ""
                    }

                // Unknown client: 400, no redirect.
                let! unknown = OidcHttp.getWithJar jar (decoded.AuthorizationEndpoint + "?client_id=ghost&redirect_uri=http://evil/")
                Expect.equal unknown.Status 400 "an unknown client is a 400, never a redirect"

                // The happy path, verified with jose against the served JWKS.
                let! code = issueCode ()
                Expect.isTrue (code.Length > 0) "a code is issued"
                let! tokens = postFormRaw decoded.TokenEndpoint (tokenForm code client.ClientSecret verifier) |> Async.AwaitPromise
                Expect.equal tokens.status 200 "the exchange succeeds"
                let tokenResponse = Wire.fromString Wire.tokenResponse tokens.body |> expect
                Expect.equal tokenResponse.TokenType "Bearer" "token_type per RFC 6749 §5.1"
                let! jwksReply = OidcHttp.getWithJar jar decoded.JwksUri
                let keySet = Fable.Jose.createLocalJWKSet (JS.JSON.parse jwksReply.Body)
                let! verified =
                    Fable.Jose.jwtVerify tokenResponse.IdToken keySet (createObj [ "issuer" ==> issuer; "audience" ==> client.ClientId ])
                    |> Async.AwaitPromise
                let subject : string = verified.payload?sub
                Expect.equal subject "local" "the ID token's subject is the local user"
                let attribution : string = verified.payload?yession_attribution
                Expect.equal attribution "unattributed" "localhost access is unattributed (docs/plans/07)"

                // Replay: the same code again -> invalid_grant.
                let! replay = postFormRaw decoded.TokenEndpoint (tokenForm code client.ClientSecret verifier) |> Async.AwaitPromise
                Expect.equal replay.status 400 "a replayed code is refused"
                Expect.equal (Wire.fromString Wire.tokenError replay.body) (Ok "invalid_grant") "as invalid_grant"

                // A wrong verifier burns its fresh code.
                let! code2 = issueCode ()
                let! badVerifier = postFormRaw decoded.TokenEndpoint (tokenForm code2 client.ClientSecret "wrong-verifier-wrong-verifier-wrong-verifier") |> Async.AwaitPromise
                Expect.equal badVerifier.status 400 "PKCE failure is refused"
                Expect.equal (Wire.fromString Wire.tokenError badVerifier.body) (Ok "invalid_grant") "as invalid_grant"

                // A wrong client secret is invalid_client (401).
                let! code3 = issueCode ()
                let! badSecret = postFormRaw decoded.TokenEndpoint (tokenForm code3 "stolen" verifier) |> Async.AwaitPromise
                Expect.equal badSecret.status 401 "a bad client secret is a 401"
                Expect.equal (Wire.fromString Wire.tokenError badSecret.body) (Ok "invalid_client") "as invalid_client"

                server.close ignore
            }
    ]

// --- The composed flow ([Ports]): real Manager + child Session Process --------------

[<Emit("process.execPath")>]
let private nodePath : string = Fable.Core.Util.jsNative

type private HeaderReply =
    abstract status : int
    abstract cacheControl : string

[<Emit("fetch($0).then(r => ({ status: r.status, cacheControl: r.headers.get('cache-control') || '' }))")>]
let private headRequest (url: string) : JS.Promise<HeaderReply> = Fable.Core.Util.jsNative

[<Emit("""fetch($0, { method: 'POST', headers: { 'x-yession-control': $1, 'content-type': 'application/json' }, body: $2 }).then(r => r.status)""")>]
let private postControl (url: string) (secret: string) (body: string) : JS.Promise<int> = Fable.Core.Util.jsNative

let private flowTests =
    testList "Composed authorization flow" [
        testCaseAsync "a real child session gates its data surfaces behind the manager's OIDC bounce; the shell stays cached and ungated" <|
            async {
                let dataDir =
                    sprintf "tests/Yession.Tests/out/.data/oidc-%d" (int (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) % 1000000)
                let! pm =
                    ProcessManager.create
                        { ProcessManager.Options.defaults dataDir nodePath [ "app/SessionMain.js" ] with
                            Strategy = Some Strategy.localhost }
                let managerUrl = sprintf "http://127.0.0.1:%d" pm.EndpointPort.Value
                let record = pm.CreateSession "oidc-child" "OIDC child" |> expect
                let! launched = pm.Launch record.SessionId
                let port = launched |> expect
                let sessionUrl = sprintf "http://127.0.0.1:%d" port

                // Offline-first invariant: the shell is ungated and cacheable for anyone.
                let! shell = headRequest (sessionUrl + "/") |> Async.AwaitPromise
                Expect.equal shell.status 200 "the shell serves without auth"
                Expect.equal shell.cacheControl "max-age=86400" "the shell keeps its offline cache window"

                // The data surfaces are gated bare.
                let! bareMe = headRequest (sessionUrl + "/me") |> Async.AwaitPromise
                Expect.equal bareMe.status 401 "bare /me is unauthorized"
                let! bareEvents = headRequest (sessionUrl + "/events/0") |> Async.AwaitPromise
                Expect.equal bareEvents.status 401 "bare /events is unauthorized"

                // /login begins the bounce.
                let probeJar = OidcHttp.newJar ()
                let! login = OidcHttp.getWithJar probeJar (sessionUrl + "/login")
                Expect.equal login.Status 302 "/login redirects into the authorize chain"
                Expect.isTrue (login.Location.StartsWith (managerUrl + "/authorize")) "to the manager's authorize endpoint"

                // No login has completed yet, so the Manager has bound no user (Plan 06).
                Expect.equal (pm.UsersOf record.SessionId) Set.empty "no user binding before a login"

                // The full chain: login -> authorize -> callback -> cookie -> /me token.
                let! opened = OidcHttp.openSession sessionUrl
                Expect.isTrue (opened.PeerToken.Length > 0) "a peer token is minted for the authorized user"
                let! events = OidcHttp.getWithJar opened.Jar (sessionUrl + "/events/0")
                Expect.equal events.Status 200 "the cookie authorizes the event log"

                // The token issuance recorded the subject↔session binding (Plan 06):
                // the composite identity is Manager-verified, never self-asserted.
                Expect.equal
                    (pm.UsersOf record.SessionId)
                    (Set.singleton (UserId.create "local" |> expect))
                    "the login bound the localhost strategy's user to the launch"

                // DCR with a forged control secret is refused at the door.
                let! forged =
                    postControl
                        (managerUrl + "/control/register-client")
                        "forged-secret"
                        (Wire.toString Wire.registerClientRequest { RedirectUri = "http://127.0.0.1:1/callback" })
                    |> Async.AwaitPromise
                Expect.equal forged 401 "a forged control secret cannot register a client"

                // Stopping the launch revokes its client registration: the authorize
                // endpoint no longer knows the client at all.
                do! pm.Stop record.SessionId |> Async.Ignore
                let! revoked =
                    OidcHttp.getWithJar
                        (OidcHttp.newJar ())
                        (sprintf "%s/authorize?client_id=oidc-child&redirect_uri=%s/callback&response_type=code&state=s&code_challenge=%s&code_challenge_method=S256" managerUrl sessionUrl challenge)
                Expect.equal revoked.Status 400 "a stopped launch's client is revoked"

                // The user binding died with the launch, exactly like the registration.
                Expect.equal (pm.UsersOf record.SessionId) Set.empty "bindings die with the launch"

                do! pm.StopAll ()
            }
    ]

// --- BYO trusted-header authorization ([Ports], Plan 07) ----------------------------

let private byoTests =
    testList "BYO trusted-header authorization" [
        testCaseAsync "trusted headers gate the UI, ride the bounce into ID-token claims, and bind user + peer to the launch" <|
            async {
                let dataDir =
                    sprintf "tests/Yession.Tests/out/.data/byo-%d" (int (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) % 1000000)
                let! pm =
                    ProcessManager.createWithUi
                        { ProcessManager.Options.defaults dataDir nodePath [ "app/SessionMain.js" ] with
                            Strategy = Some Strategy.trustedHeaders }
                        (Some ManagerUi.tryHandle)
                let managerUrl = sprintf "http://127.0.0.1:%d" pm.EndpointPort.Value
                let nick = [ Strategy.SubjectHeader, "nick@example.com"; Strategy.NameHeader, "Nick" ]

                // The management UI is gated by the strategy: header-less loopback
                // requests are 401 (localhost trust would have let them in), asserted
                // identity is let through.
                let! bare = OidcHttp.getWithJar (OidcHttp.newJar ()) (managerUrl + "/")
                Expect.equal bare.Status 401 "no identity header, no UI"
                let! asserted = OidcHttp.getWithJarAs nick (OidcHttp.newJar ()) (managerUrl + "/")
                Expect.equal asserted.Status 200 "the proxy-asserted user reaches the UI"

                // A real child session: the login bounce only completes when the
                // proxy-asserted headers ride the authorize hop.
                let record = pm.CreateSession "byo-child" "BYO child" |> expect
                let! launched = pm.Launch record.SessionId
                let sessionUrl = sprintf "http://127.0.0.1:%d" (launched |> expect)

                let probeJar = OidcHttp.newJar ()
                let! headerless = OidcHttp.followWithJar probeJar (sessionUrl + "/login")
                Expect.equal headerless.Status 401 "the bounce dies at /authorize without the identity header"

                // The full chain with headers (and the browser's stable peer id on
                // /login): cookie, attributed /me, and the Manager's launch bindings.
                let! opened = OidcHttp.openSessionVia nick "/login?peer_id=browser-abc" sessionUrl
                Expect.isTrue (opened.PeerToken.Length > 0) "a peer token is minted for the attributed user"
                let! me = OidcHttp.getWithJar opened.Jar (sessionUrl + "/me")
                Expect.isTrue (me.Body.Contains "\"sub\":\"nick@example.com\"") "/me carries the verified subject"
                Expect.isTrue (me.Body.Contains "\"attributed\":true") "/me marks the identity attributable"
                Expect.equal
                    (pm.UsersOf record.SessionId)
                    (Set.singleton (UserId.create "nick@example.com" |> expect))
                    "the login bound the asserted user to the launch"
                Expect.equal
                    (pm.PeersOf record.SessionId)
                    (Set.singleton (PeerId.create "browser-abc" |> expect))
                    "the peer id that rode the bounce is witnessed into the launch"

                // Bindings die with the launch, peers exactly like users (Plan 06 rule).
                do! pm.Stop record.SessionId |> Async.Ignore
                Expect.equal (pm.PeersOf record.SessionId) Set.empty "peer bindings die with the launch"
                do! pm.StopAll ()
            }

        testCaseAsync "the default none strategy denies the UI and the bounce outright" <|
            async {
                let dataDir =
                    sprintf "tests/Yession.Tests/out/.data/none-%d" (int (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) % 1000000)
                let! pm =
                    ProcessManager.createWithUi
                        (ProcessManager.Options.defaults dataDir nodePath [ "app/SessionMain.js" ])
                        (Some ManagerUi.tryHandle)
                let managerUrl = sprintf "http://127.0.0.1:%d" pm.EndpointPort.Value
                let! ui = OidcHttp.getWithJarAs [ Strategy.SubjectHeader, "nick@example.com" ] (OidcHttp.newJar ()) (managerUrl + "/")
                Expect.equal ui.Status 401 "no strategy was chosen: even asserted identity denies"
                do! pm.StopAll ()
            }
    ]

let tests =
    testList "Oidc" [
        pkceTests
        providerTests
        strategyTests
        storeTests
        wireTests
        keyTests
        Tag.needs "OP endpoint over HTTP" [ Tag.Ports ] (fun () -> opTests)
        Tag.needs "Composed authorization flow" [ Tag.Ports ] (fun () -> flowTests)
        Tag.needs "BYO trusted-header authorization" [ Tag.Ports ] (fun () -> byoTests)
    ]
