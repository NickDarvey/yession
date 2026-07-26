module Fable.OpenIdClient

// Fable bindings to the `openid-client` npm package (v6, functional API) — the
// OpenID-Foundation-CERTIFIED relying-party implementation. The Session Process uses it
// for the whole client side of the flow: discovery, PKCE, the authorization-code
// exchange, and ID-token validation against the provider's JWKS. Using the certified
// client instead of hand-rolled protocol code means every full-flow test doubles as a
// conformance check of our provider.
//
// Binding layer only (Fable.Dockerode pattern): the slice the Session Process uses.
// Fable-only: `dotnet build` type-checks it; Fable emits the JS that runs on Node.

open Fable.Core

/// Opaque client configuration: issuer metadata + client credentials, from `discovery`.
type [<AllowNullLiteral>] Configuration =
    interface end

/// Validated ID-token claims, exposed by the token response's `claims()` helper.
/// The optional members are the profile claims Yession's provider adds when its
/// strategy attributed a real user (docs/plans/07); absent otherwise.
type [<AllowNullLiteral>] IdTokenClaims =
    abstract iss : string
    abstract sub : string
    abstract aud : obj
    abstract exp : float
    abstract iat : float
    abstract name : string option
    abstract yession_attribution : string option

type [<AllowNullLiteral>] TokenEndpointResponse =
    abstract access_token : string
    abstract token_type : string
    abstract id_token : string
    /// The ID-token claims — already validated (signature via the discovered JWKS,
    /// iss/aud/exp) by `authorizationCodeGrant` before the promise resolves.
    abstract claims : unit -> IdTokenClaims

[<Import("discovery", "openid-client")>]
let private discoveryRaw : obj = jsNative

// The real signature is (server, clientId, metadata, clientAuthentication, options) —
// options is the FIFTH argument; `undefined` keeps the default client authentication
// (client_secret_post from the string secret).
[<Emit("$0($1, $2, $3, undefined, $4)")>]
let private callDiscovery (fn: obj) (server: obj) (clientId: string) (clientSecret: string) (options: obj) : JS.Promise<Configuration> = jsNative

/// Fetch `<server>/.well-known/openid-configuration` and bind client credentials.
/// `clientSecret` as a string selects the default `client_secret_post` authentication.
/// `options.execute = [allowInsecureRequests]` permits a plain-HTTP issuer.
let discovery (server: obj) (clientId: string) (clientSecret: string) (options: obj) : JS.Promise<Configuration> =
    callDiscovery discoveryRaw server clientId clientSecret options

/// Execute-option for `discovery`: allow `http://` (non-TLS) requests. Required here
/// because the issuer is loopback HTTP (`http://127.0.0.1:<port>`) — the RFC 8252
/// native-app pattern; the whole deployment is single-machine loopback by design.
[<Import("allowInsecureRequests", "openid-client")>]
let allowInsecureRequests : obj = jsNative

[<Import("randomPKCECodeVerifier", "openid-client")>]
let randomPKCECodeVerifier () : string = jsNative

/// S256 challenge for a verifier (async: WebCrypto digest).
[<Import("calculatePKCECodeChallenge", "openid-client")>]
let calculatePKCECodeChallenge (verifier: string) : JS.Promise<string> = jsNative

[<Import("randomState", "openid-client")>]
let randomState () : string = jsNative

/// Build the authorization-endpoint URL from `{ redirect_uri; scope; state; code_challenge;
/// code_challenge_method }` parameters. Returns a WHATWG `URL`.
[<Import("buildAuthorizationUrl", "openid-client")>]
let buildAuthorizationUrl (config: Configuration) (parameters: obj) : obj = jsNative

/// Redeem the code carried by `currentUrl` (the callback URL, a WHATWG `URL`) with
/// `checks = { pkceCodeVerifier; expectedState }`. Performs the token-endpoint exchange
/// AND validates the ID token; rejects on any protocol or validation failure.
[<Import("authorizationCodeGrant", "openid-client")>]
let authorizationCodeGrant (config: Configuration) (currentUrl: obj) (checks: obj) : JS.Promise<TokenEndpointResponse> = jsNative

/// A WHATWG `URL` from an absolute href (openid-client's URL-typed inputs).
[<Emit("new URL($0)")>]
let newUrl (href: string) : obj = jsNative

/// A WHATWG `URL` from a possibly-relative href resolved against a base — how the
/// session rebuilds its absolute callback URL from Node's request-relative `req.url`.
[<Emit("new URL($0, $1)")>]
let newUrlWithBase (href: string) (baseHref: string) : obj = jsNative

[<Emit("$0.href")>]
let urlHref (url: obj) : string = jsNative
