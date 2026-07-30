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

/// Tokens from a standard authorization-code or refresh grant, plus the two facts a
/// later refresh needs (`TokenUrl`, `ClientId`) captured at exchange time, so refreshing
/// never depends on session-supplied config again.
type OAuthGrant =
    { AccessToken : string
      RefreshToken : string option
      ExpiresAt : DateTimeOffset option
      TokenUrl : string
      ClientId : string }

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
                      "tokenUrl", Encode.string g.TokenUrl
                      "clientId", Encode.string g.ClientId ]
          Decode =
            Decode.object (fun get ->
                { OAuthGrant.AccessToken = get.Required.Field "accessToken" Decode.string
                  OAuthGrant.RefreshToken = get.Required.Field "refreshToken" (Decode.option Decode.string)
                  OAuthGrant.ExpiresAt = get.Required.Field "expiresAt" (Decode.option Codec.timestamp.Decode)
                  OAuthGrant.TokenUrl = get.Required.Field "tokenUrl" Decode.string
                  OAuthGrant.ClientId = get.Required.Field "clientId" Decode.string }) }

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

    /// The `application/x-www-form-urlencoded` body of a standard authorization-code
    /// grant (RFC 6749 §4.1.3 + RFC 7636 §4.5).
    let exchangeBody (clientId: string) (redirectUri: string) (verifier: string) (code: string) : string =
        query
            [ "grant_type", "authorization_code"
              "code", code
              "redirect_uri", redirectUri
              "client_id", clientId
              "code_verifier", verifier ]

    /// The body of a standard refresh grant (RFC 6749 §6).
    let refreshBody (grant: OAuthGrant) (refreshToken: string) : string =
        query
            [ "grant_type", "refresh_token"
              "refresh_token", refreshToken
              "client_id", grant.ClientId ]

    /// Decode a standard token response (`access_token`, optional `refresh_token`,
    /// optional `expires_in` seconds) into a stored grant.
    let decodeTokenResponse
        (now: DateTimeOffset)
        (tokenUrl: string)
        (clientId: string)
        (json: string)
        : Result<OAuthGrant, string> =
        let decoder =
            Decode.object (fun get ->
                { OAuthGrant.AccessToken = get.Required.Field "access_token" Decode.string
                  OAuthGrant.RefreshToken = get.Optional.Field "refresh_token" Decode.string
                  OAuthGrant.ExpiresAt =
                    get.Optional.Field "expires_in" Decode.float
                    |> Option.map (fun seconds -> now.AddSeconds seconds)
                  OAuthGrant.TokenUrl = tokenUrl
                  OAuthGrant.ClientId = clientId })
        Decode.fromString decoder json

    /// A refresh response may omit `refresh_token`; the previous one stays valid then
    /// (providers that rotate always send the successor).
    let merged (previous: OAuthGrant) (fresh: OAuthGrant) : OAuthGrant =
        { fresh with
            RefreshToken =
                match fresh.RefreshToken with
                | Some _ -> fresh.RefreshToken
                | None -> previous.RefreshToken }

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
      RedirectUri : string }

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
