namespace Yession.Manager

open System
open Yession.Domain

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// The connection broker's pure state (Plan 08): the credential envelope that becomes an
/// encrypted secret VALUE, the standards-only OAuth flow logic (RFC 6749/7636 — nothing
/// provider-specific; every endpoint and client id arrives from the session as data),
/// and the pending-flow bookkeeping. Refresh tokens exist in `OAuthGrant` and nowhere a
/// session process can reach — enforcement by placement.

/// How a provider's token endpoint wants a grant request encoded. RFC 6749 §4.1.3 says
/// `application/x-www-form-urlencoded`, and that stays the default — but real providers
/// deviate, and a broker that can only speak the standard cannot broker them. Anthropic's
/// `/v1/oauth/token` is one: a form body comes back
/// `invalid_request_error: "Invalid request format"`, and its own clients post JSON.
///
/// The SESSION declares which dialect its provider speaks, exactly like the endpoints and
/// client id it already supplies as data — so the broker stays service-agnostic (it still
/// never learns WHICH service it brokered) and provider knowledge stays session-side.
type TokenRequestDialect =
    | FormEncoded
    | JsonEncoded

module TokenRequestDialect =

    let describe (dialect: TokenRequestDialect) : string =
        match dialect with
        | FormEncoded -> "form"
        | JsonEncoded -> "json"

    let ofString (raw: string) : TokenRequestDialect =
        match raw with
        | "json" -> JsonEncoded
        | _ -> FormEncoded

/// One grant request, ready to POST: the dialect decides both halves, so a caller can
/// never pair a JSON body with a form content type.
type TokenRequest = { ContentType : string; Body : string }

/// Tokens from a standard authorization-code or refresh grant, plus the facts a later
/// refresh needs (`TokenUrl`, `ClientId`, and the `Dialect` the provider speaks) captured
/// at exchange time, so refreshing never depends on session-supplied config again.
type OAuthGrant =
    { AccessToken : string
      RefreshToken : string option
      ExpiresAt : DateTimeOffset option
      /// When the REFRESH token itself lapses, where a provider states one (GitHub gives
      /// them six months). Past it there is nothing to retry: the answer is to sign in
      /// again, and saying so beats a refresh that can only ever fail.
      RefreshExpiresAt : DateTimeOffset option
      TokenUrl : string
      ClientId : string
      Dialect : TokenRequestDialect }

/// What the broker stores: brokered OAuth tokens it can refresh, or a pasted static
/// token/key it returns verbatim and never touches.
type BrokeredCredential =
    | BrokeredOAuth of OAuthGrant
    | BrokeredStatic of value: string

module BrokeredCredentialCodec =

    let private grant : Codec<OAuthGrant> =
        { Encode =
            fun (g: OAuthGrant) ->
                Encode.object
                    [ "accessToken", Encode.string g.AccessToken
                      "refreshToken", Encode.option Encode.string g.RefreshToken
                      "expiresAt", Encode.option Codec.timestamp.Encode g.ExpiresAt
                      "refreshExpiresAt", Encode.option Codec.timestamp.Encode g.RefreshExpiresAt
                      "tokenUrl", Encode.string g.TokenUrl
                      "clientId", Encode.string g.ClientId
                      "dialect", Encode.string (TokenRequestDialect.describe g.Dialect) ]
          Decode =
            Decode.object (fun get ->
                { OAuthGrant.AccessToken = get.Required.Field "accessToken" Decode.string
                  OAuthGrant.RefreshToken = get.Required.Field "refreshToken" (Decode.option Decode.string)
                  OAuthGrant.ExpiresAt = get.Required.Field "expiresAt" (Decode.option Codec.timestamp.Decode)
                  // Optional for the same reason `dialect` is: an envelope stored before
                  // refresh-token lifetimes were recorded simply does not know one.
                  OAuthGrant.RefreshExpiresAt =
                    get.Optional.Field "refreshExpiresAt" (Decode.option Codec.timestamp.Decode)
                    |> Option.flatten
                  OAuthGrant.TokenUrl = get.Required.Field "tokenUrl" Decode.string
                  OAuthGrant.ClientId = get.Required.Field "clientId" Decode.string
                  // Optional: envelopes stored before dialects existed are form-encoded
                  // by construction, and must keep refreshing rather than fail to decode.
                  OAuthGrant.Dialect =
                    get.Optional.Field "dialect" Decode.string
                    |> Option.map TokenRequestDialect.ofString
                    |> Option.defaultValue FormEncoded }) }

    let credential : Codec<BrokeredCredential> =
        { Encode =
            (fun c ->
                match c with
                | BrokeredOAuth g -> Encode.object [ "kind", Encode.string "oauth"; "grant", grant.Encode g ]
                | BrokeredStatic value -> Encode.object [ "kind", Encode.string "static"; "value", Encode.string value ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "oauth" -> Decode.field "grant" grant.Decode |> Decode.map BrokeredOAuth
                | "static" -> Decode.field "value" Decode.string |> Decode.map BrokeredStatic
                | other -> Decode.fail (sprintf "Unknown credential kind: %s" other)) }

    let toString (c: BrokeredCredential) : string = credential.Encode c |> Encode.toString 0

    let fromString (json: string) : Result<BrokeredCredential, string> = Decode.fromString credential.Decode json

/// Pure, standards-only OAuth flow logic. The session supplies the provider's authorize
/// URL (which may already carry provider-specific query params — the broker only appends
/// the standard ones), token URL, client id, and scopes; the broker owns state, PKCE,
/// and grant wire shapes.
module BrokerFlow =

    let private query (parameters: (string * string) list) : string =
        parameters
        |> List.map (fun (k, v) -> sprintf "%s=%s" k (Uri.EscapeDataString v))
        |> String.concat "&"

    /// The provider authorize URL for one flow. `authorizeUrlBase` may already contain
    /// a query string; standard params are appended either way.
    let authorizeUrl
        (authorizeUrlBase: string)
        (clientId: string)
        (redirectUri: string)
        (scopes: string)
        (state: string)
        (challenge: string)
        : string =
        let separator = if authorizeUrlBase.Contains "?" then "&" else "?"
        authorizeUrlBase
        + separator
        + query
            [ "response_type", "code"
              "client_id", clientId
              "redirect_uri", redirectUri
              "scope", scopes
              "state", state
              "code_challenge", challenge
              "code_challenge_method", "S256" ]

    /// The same parameters in whichever dialect the provider speaks. The parameter NAMES
    /// are the standard ones in both — only the envelope differs.
    let private encoded (dialect: TokenRequestDialect) (parameters: (string * string) list) : TokenRequest =
        match dialect with
        | FormEncoded ->
            { ContentType = "application/x-www-form-urlencoded"
              Body = query parameters }
        | JsonEncoded ->
            { ContentType = "application/json"
              Body =
                parameters
                |> List.map (fun (k, v) -> k, Encode.string v)
                |> Encode.object
                |> Encode.toString 0 }

    /// An authorization-code grant (RFC 6749 §4.1.3 + RFC 7636 §4.5).
    ///
    /// `state` is NOT a standard token-request parameter and never rides the form dialect.
    /// The JSON dialect carries it because the providers that need JSON also expect it
    /// replayed here — it is why their code-display pages hand the human `code#state` at
    /// all — and a JSON provider is already off-standard by definition.
    let exchangeRequest
        (dialect: TokenRequestDialect)
        (clientId: string)
        (redirectUri: string)
        (verifier: string)
        (state: string)
        (code: string)
        : TokenRequest =
        encoded
            dialect
            [ yield "grant_type", "authorization_code"
              yield "code", code
              yield "redirect_uri", redirectUri
              yield "client_id", clientId
              yield "code_verifier", verifier
              match dialect with
              | JsonEncoded -> yield "state", state
              | FormEncoded -> () ]

    /// A refresh grant (RFC 6749 §6), in the dialect the grant recorded at exchange time —
    /// a provider that would not parse the exchange will not parse the refresh either.
    let refreshRequest (grant: OAuthGrant) (refreshToken: string) : TokenRequest =
        encoded
            grant.Dialect
            [ "grant_type", "refresh_token"
              "refresh_token", refreshToken
              "client_id", grant.ClientId ]

    /// Decode a standard token response (`access_token`, optional `refresh_token`,
    /// optional `expires_in` seconds) into a stored grant.
    let decodeTokenResponse
        (now: DateTimeOffset)
        (tokenUrl: string)
        (clientId: string)
        (dialect: TokenRequestDialect)
        (json: string)
        : Result<OAuthGrant, string> =
        let decoder =
            Decode.object (fun get ->
                { OAuthGrant.AccessToken = get.Required.Field "access_token" Decode.string
                  OAuthGrant.RefreshToken = get.Optional.Field "refresh_token" Decode.string
                  OAuthGrant.ExpiresAt =
                    get.Optional.Field "expires_in" Decode.float
                    |> Option.map (fun seconds -> now.AddSeconds seconds)
                  OAuthGrant.RefreshExpiresAt =
                    get.Optional.Field "refresh_token_expires_in" Decode.float
                    |> Option.map (fun seconds -> now.AddSeconds seconds)
                  OAuthGrant.TokenUrl = tokenUrl
                  OAuthGrant.ClientId = clientId
                  OAuthGrant.Dialect = dialect })
        Decode.fromString decoder json

    /// A refresh response may omit `refresh_token`; the previous one stays valid then
    /// (providers that rotate always send the successor).
    let merged (previous: OAuthGrant) (fresh: OAuthGrant) : OAuthGrant =
        match fresh.RefreshToken with
        | Some _ -> fresh
        // The lifetime travels with the token it describes: keeping the old refresh token
        // while adopting a `None` lifetime beside it would say "this one never lapses",
        // which is the opposite of what was recorded.
        | None -> { fresh with RefreshToken = previous.RefreshToken; RefreshExpiresAt = previous.RefreshExpiresAt }

    /// Refresh margin: treat a token as due five minutes before its recorded expiry, so
    /// a turn never starts on a token about to lapse mid-flight.
    let private marginSeconds = 300.0

    /// Whether a stored credential is due for a refresh NOW. Only a refreshable OAuth
    /// grant with a known expiry ever is; static tokens and grants without expiry are
    /// used as-is until the provider rejects them.
    let needsRefresh (now: DateTimeOffset) (credential: BrokeredCredential) : bool =
        match credential with
        | BrokeredStatic _ -> false
        | BrokeredOAuth grant ->
            match grant.RefreshToken, grant.ExpiresAt with
            | Some _, Some expiresAt -> now >= expiresAt.AddSeconds (-marginSeconds)
            | _ -> false

    /// Whether the refresh token itself has lapsed — a state no retry escapes, and the
    /// one refusal that has to say "sign in again" rather than "try later". Unknown
    /// lifetimes are not expired: a provider that states none is saying it does not
    /// expire on a clock we can read.
    let refreshExpired (now: DateTimeOffset) (credential: BrokeredCredential) : bool =
        match credential with
        | BrokeredStatic _ -> false
        | BrokeredOAuth grant ->
            match grant.RefreshExpiresAt with
            | Some expiresAt -> now >= expiresAt
            | None -> false

    /// Why this credential cannot be repaired by refreshing, if it cannot — the whole
    /// "sign in again" rule in one place, so a resolve and a status answer it identically
    /// rather than each deciding for itself.
    ///
    /// It is deliberately NOT `refreshExpired` with a nicer return type. That predicate
    /// answers one question; this composes it with the OTHER way a grant reaches the same
    /// dead end — an access token past its expiry with no refresh token behind it, which
    /// `needsRefresh` says `false` for (nothing to refresh WITH) and which therefore used
    /// to resolve happily right up to the moment the provider refused it.
    ///
    /// A static token is never beyond refresh HERE, and that is not an oversight: it has no
    /// expiry model at all, so nothing on this side can know. Only a provider refusing it
    /// can say, which is why a rejection has to be reportable from outside.
    let beyondRefresh (now: DateTimeOffset) (credential: BrokeredCredential) : string option =
        match credential with
        | BrokeredStatic _ -> None
        | BrokeredOAuth grant ->
            if refreshExpired now credential then Some "the refresh token has expired"
            elif
                grant.RefreshToken.IsNone
                && grant.ExpiresAt |> Option.exists (fun expiresAt -> now >= expiresAt)
            then Some "the access token has expired and there is nothing to refresh it with"
            else None

    let kindOf (credential: BrokeredCredential) : ConnectionKind =
        match credential with
        | BrokeredOAuth _ -> OAuthConnection
        | BrokeredStatic _ -> StaticConnection

    /// The value a resolve releases: the current access token, or the static value.
    let valueOf (credential: BrokeredCredential) : string =
        match credential with
        | BrokeredOAuth grant -> grant.AccessToken
        | BrokeredStatic value -> value

/// One begun-but-uncompleted flow, keyed by its single-use `state`. `RedirectUri` is
/// the one the authorize URL carried — the exchange must repeat it exactly (RFC 6749
/// §4.1.3), so it is pended with the flow, not re-derived.
type PendingFlow =
    { Verifier : string
      Target : SecretId
      TokenUrl : string
      ClientId : string
      Scopes : string
      RedirectUri : string
      /// The dialect the session declared at begin; the exchange (and every later
      /// refresh, through the grant) speaks it.
      Dialect : TokenRequestDialect }

/// Flows redirected to a provider and not yet called back. Single-use and short-lived
/// (10 minutes — the human is clicking through a consent screen, not parking a tab);
/// clock injected so the cheap tier covers the lifecycle deterministically. Mirrors
/// `Yession.SessionProcess.PendingLogins`.
type PendingFlows (nowUnix: unit -> int64) =
    let lifetimeSeconds = 600L
    let mutable pending : Map<string, PendingFlow * int64> = Map.empty

    member _.Add (state: string) (flow: PendingFlow) : unit =
        pending <- Map.add state (flow, nowUnix ()) pending

    member _.Take (state: string) : PendingFlow option =
        match Map.tryFind state pending with
        | None -> None
        | Some (flow, issuedAt) ->
            pending <- Map.remove state pending
            if nowUnix () - issuedAt > lifetimeSeconds then None else Some flow
