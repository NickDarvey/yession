namespace Yession.Oidc

open Yession.Domain

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// The provider's wire shapes: the DCR request/response riding the control channel, the
/// discovery document (OIDC Discovery §3 — snake_case on the wire), and the token
/// response (RFC 6749 §5.1). Hand-written codecs like every boundary (ControlWire
/// discipline).

type RegisterClientRequest =
    { RedirectUri : string }

type RegisterClientResponse =
    { ClientId : string
      ClientSecret : string
      /// The provider's issuer URL — everything else (endpoints, JWKS) is discovered
      /// from `<issuer>/.well-known/openid-configuration`.
      Issuer : string }

type Discovery =
    { Issuer : string
      AuthorizationEndpoint : string
      TokenEndpoint : string
      JwksUri : string }

type TokenResponse =
    { AccessToken : string
      TokenType : string
      IdToken : string
      ExpiresIn : int }

module Wire =

    let registerClientRequest : Codec<RegisterClientRequest> =
        { Encode = fun (r: RegisterClientRequest) -> Encode.object [ "redirectUri", Encode.string r.RedirectUri ]
          Decode =
            Decode.object (fun get ->
                { RegisterClientRequest.RedirectUri = get.Required.Field "redirectUri" Decode.string }) }

    let registerClientResponse : Codec<RegisterClientResponse> =
        { Encode =
            fun (r: RegisterClientResponse) ->
                Encode.object
                    [ "clientId", Encode.string r.ClientId
                      "clientSecret", Encode.string r.ClientSecret
                      "issuer", Encode.string r.Issuer ]
          Decode =
            Decode.object (fun get ->
                { RegisterClientResponse.ClientId = get.Required.Field "clientId" Decode.string
                  RegisterClientResponse.ClientSecret = get.Required.Field "clientSecret" Decode.string
                  RegisterClientResponse.Issuer = get.Required.Field "issuer" Decode.string }) }

    /// The discovery document. Encoding also asserts what the provider supports —
    /// `code` + PKCE `S256` + `EdDSA`, nothing else — per OIDC Discovery §3; the decoder
    /// reads only what the RP config needs.
    let discovery : Codec<Discovery> =
        { Encode =
            fun (d: Discovery) ->
                Encode.object
                    [ "issuer", Encode.string d.Issuer
                      "authorization_endpoint", Encode.string d.AuthorizationEndpoint
                      "token_endpoint", Encode.string d.TokenEndpoint
                      "jwks_uri", Encode.string d.JwksUri
                      "response_types_supported", Encode.list [ Encode.string "code" ]
                      "grant_types_supported", Encode.list [ Encode.string "authorization_code" ]
                      "code_challenge_methods_supported", Encode.list [ Encode.string "S256" ]
                      "id_token_signing_alg_values_supported", Encode.list [ Encode.string "EdDSA" ]
                      "subject_types_supported", Encode.list [ Encode.string "public" ]
                      "scopes_supported", Encode.list [ Encode.string "openid" ]
                      "token_endpoint_auth_methods_supported", Encode.list [ Encode.string "client_secret_post" ] ]
          Decode =
            Decode.object (fun get ->
                { Discovery.Issuer = get.Required.Field "issuer" Decode.string
                  Discovery.AuthorizationEndpoint = get.Required.Field "authorization_endpoint" Decode.string
                  Discovery.TokenEndpoint = get.Required.Field "token_endpoint" Decode.string
                  Discovery.JwksUri = get.Required.Field "jwks_uri" Decode.string }) }

    let tokenResponse : Codec<TokenResponse> =
        { Encode =
            fun (t: TokenResponse) ->
                Encode.object
                    [ "access_token", Encode.string t.AccessToken
                      "token_type", Encode.string t.TokenType
                      "id_token", Encode.string t.IdToken
                      "expires_in", Encode.int t.ExpiresIn ]
          Decode =
            Decode.object (fun get ->
                { TokenResponse.AccessToken = get.Required.Field "access_token" Decode.string
                  TokenResponse.TokenType = get.Required.Field "token_type" Decode.string
                  TokenResponse.IdToken = get.Required.Field "id_token" Decode.string
                  TokenResponse.ExpiresIn = get.Required.Field "expires_in" Decode.int }) }

    /// OAuth error bodies: `{"error": "..."}` (RFC 6749 §5.2).
    let tokenError : Codec<string> =
        { Encode = fun error -> Encode.object [ "error", Encode.string error ]
          Decode = Decode.field "error" Decode.string }

    let toString (codec: Codec<'a>) (value: 'a) : string = codec.Encode value |> Encode.toString 0

    let fromString (codec: Codec<'a>) (json: string) : Result<'a, string> = Decode.fromString codec.Decode json
