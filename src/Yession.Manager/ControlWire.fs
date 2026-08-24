namespace Yession.Manager

open System
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Access

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// The control-RPC wire shapes (Phase 4, Step 24): the supervision and custody traffic
/// between a Session Process and its Manager. Hand-written codecs like every boundary;
/// NO session content crosses this channel — environments and commands are
/// session-owned (the sandbox seam) and never appear here.
module ControlWire =

    let private secretName : Codec<SecretName> =
        { Encode = SecretName.value >> Encode.string
          Decode =
            Decode.string
            |> Decode.andThen (fun raw ->
                match SecretName.create raw with
                | Ok v -> Decode.succeed v
                | Error e -> Decode.fail e) }

    /// A session's self-assigned display name, reported child→Manager. The one piece of
    /// non-environment traffic on the control channel: a label (metadata), never
    /// conversation or event content.
    let sessionNameReport : Codec<string> =
        { Encode = fun name -> Encode.object [ "name", Encode.string name ]
          Decode = Decode.field "name" Decode.string }

    /// Whether a session is IN USE, reported child→Manager (Plan 11). Like the name report
    /// this is metadata — one boolean, never who is connected or what they are doing.
    ///
    /// The session is the only process that can answer: peers hold data channels straight
    /// to it, and the running turn lives in its scheduler. The Manager supplies the policy
    /// (how long idle is too long) and the CLOCK — it timestamps each report on arrival, so
    /// a child's idea of the time never enters the decision.
    let sessionActivityReport : Codec<bool> =
        { Encode = fun busy -> Encode.object [ "busy", Encode.bool busy ]
          Decode = Decode.field "busy" Decode.bool }

    /// A Manager→Session notification (the reverse leg, Manager-pushed): the payload of the
    /// `/control/notifications` SSE stream. Tagged by `kind` like every other control shape,
    /// so new cases extend it without breaking older decoders. See `SessionNotification`.
    let sessionNotification : Codec<SessionNotification> =
        { Encode =
            (fun n ->
                match n with
                | EnvironmentChanged () -> Encode.object [ "kind", Encode.string "environmentChanged" ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "environmentChanged" -> Decode.succeed (EnvironmentChanged ())
                | other -> Decode.fail (sprintf "Unknown notification: %s" other)) }

    // --- secrets (Plan 06; resolve reworked with session-owned sandboxes) -----------------
    // Requests carry an explicit scope: the wire is self-describing and deny paths are
    // testable, even though v1 policy only ever permits a session's own scope for writes.
    // `set` answers with metadata, `list` with metadata, `delete` with a flag. `resolve`
    // is the ONE value-returning secrets shape: with sandboxes spawned by the session,
    // env injection happens there, so the referenced values cross at sandbox spawn over
    // this authenticated channel — gated by the same readable-scope walk injection always
    // used — and never reach the agent loop or the transcript.

    type SetSecretRequest = { Scope : SecretScope; Name : SecretName; Value : string }
    type ListSecretsRequest = { Scope : SecretScope }
    [<RequireQualifiedAccess>]
    type DeleteSecretRequest = { Scope : SecretScope; Name : SecretName }
    type ResolveSecretRequest = { Name : SecretName }
    type ListSecretsResponse = { Secrets : SecretMetadata list }
    type DeleteSecretResponse = { Deleted : bool }
    type ResolveSecretResponse = { Value : string }

    let secretMetadata : Codec<SecretMetadata> = SecretsCodec.secretMetadata

    let setSecretRequest : Codec<SetSecretRequest> =
        { Encode =
            fun (r: SetSecretRequest) ->
                Encode.object
                    [ "scope", SecretsCodec.secretScope.Encode r.Scope
                      "name", secretName.Encode r.Name
                      "value", Encode.string r.Value ]
          Decode =
            Decode.object (fun get ->
                { SetSecretRequest.Scope = get.Required.Field "scope" SecretsCodec.secretScope.Decode
                  SetSecretRequest.Name = get.Required.Field "name" secretName.Decode
                  SetSecretRequest.Value = get.Required.Field "value" Decode.string }) }

    let listSecretsRequest : Codec<ListSecretsRequest> =
        { Encode = fun (r: ListSecretsRequest) -> Encode.object [ "scope", SecretsCodec.secretScope.Encode r.Scope ]
          Decode =
            Decode.object (fun get ->
                { ListSecretsRequest.Scope = get.Required.Field "scope" SecretsCodec.secretScope.Decode }) }

    let deleteSecretRequest : Codec<DeleteSecretRequest> =
        { Encode =
            fun (r: DeleteSecretRequest) ->
                Encode.object
                    [ "scope", SecretsCodec.secretScope.Encode r.Scope
                      "name", secretName.Encode r.Name ]
          Decode =
            Decode.object (fun get ->
                { DeleteSecretRequest.Scope = get.Required.Field "scope" SecretsCodec.secretScope.Decode
                  DeleteSecretRequest.Name = get.Required.Field "name" secretName.Decode }) }

    let listSecretsResponse : Codec<ListSecretsResponse> =
        { Encode =
            fun (r: ListSecretsResponse) ->
                Encode.object [ "secrets", r.Secrets |> List.map SecretsCodec.secretMetadata.Encode |> Encode.list ]
          Decode =
            Decode.object (fun get ->
                { ListSecretsResponse.Secrets =
                    get.Required.Field "secrets" (Decode.list SecretsCodec.secretMetadata.Decode) }) }

    let deleteSecretResponse : Codec<DeleteSecretResponse> =
        { Encode = fun (r: DeleteSecretResponse) -> Encode.object [ "deleted", Encode.bool r.Deleted ]
          Decode =
            Decode.object (fun get ->
                { DeleteSecretResponse.Deleted = get.Required.Field "deleted" Decode.bool }) }

    let resolveSecretRequest : Codec<ResolveSecretRequest> =
        { Encode = fun (r: ResolveSecretRequest) -> Encode.object [ "name", secretName.Encode r.Name ]
          Decode =
            Decode.object (fun get ->
                { ResolveSecretRequest.Name = get.Required.Field "name" secretName.Decode }) }

    let resolveSecretResponse : Codec<ResolveSecretResponse> =
        { Encode = fun (r: ResolveSecretResponse) -> Encode.object [ "value", Encode.string r.Value ]
          Decode =
            Decode.object (fun get ->
                { ResolveSecretResponse.Value = get.Required.Field "value" Decode.string }) }

    // --- connections (Plan 08) -----------------------------------------------------------
    // The Manager-brokered external-service credentials. The wire is service-agnostic BY
    // DESIGN: a begin request carries the provider's endpoints as data, so the Manager
    // never learns which service it brokered. Exactly ONE response shape can carry a
    // credential value — `ConnectionResolveResponse`, the answer to the per-turn resolve —
    // and NO shape can carry a refresh token (that type lives in `BrokerState`, which has
    // no codec here).

    /// Everything the standards need to begin an authorization-code + PKCE flow, as data.
    /// `AuthorizeUrl` may already carry provider-specific query params; the broker appends
    /// only the standard ones. `RedirectUri` is where the provider sends the code:
    /// `None` = the Manager's own public callback (providers that can register it);
    /// `Some` = a provider-hosted code-display page (e.g. a client whose registered
    /// URIs cannot include this Manager) — completion then arrives as a paste.
    /// `TokenDialect` is how this provider's token endpoint wants its grant requests
    /// encoded — another provider fact the session supplies, defaulting to the standard
    /// form encoding for every provider that keeps to RFC 6749.
    type ConnectionBeginRequest =
        { Target : SecretId
          AuthorizeUrl : string
          TokenUrl : string
          ClientId : string
          Scopes : string
          RedirectUri : string option
          TokenDialect : TokenRequestDialect }

    type ConnectionBeginResponse = { AuthorizeUrl : string; State : string }

    /// Manual completion (the paste path): the pasted payload is `code` or `code#state`.
    /// The target names which begun flow this completes — the broker checks it matches.
    type ConnectionCompleteRequest = { Target : SecretId; Code : string }

    /// Store a pasted static token/key verbatim.
    type ConnectionPutRequest = { Target : SecretId; Value : string }

    /// Store a grant the SESSION obtained itself, as a grant — the device flow (RFC 8628)
    /// is a whole authorization the session runs end to end, and there is no code for the
    /// broker to exchange by the time it finishes. Put next to `ConnectionPutRequest`
    /// rather than instead of it, because the difference is the point: a pasted token is
    /// static and cannot rotate, and this one can.
    ///
    /// It carries what a later refresh needs and what the session is the only one to know:
    /// the provider's token endpoint, the client id the grant was minted for, and the
    /// dialect that endpoint speaks. The lifetimes arrive as the provider states them
    /// (seconds from now), because that is what an OAuth token response says and the clock
    /// that matters for storing an absolute expiry is the Manager's.
    type ConnectionPutGrantRequest =
        { Target : SecretId
          AccessToken : string
          RefreshToken : string option
          ExpiresIn : int option
          RefreshTokenExpiresIn : int option
          TokenUrl : string
          ClientId : string
          TokenDialect : TokenRequestDialect }

    [<RequireQualifiedAccess>]
    type ConnectionDisconnectRequest = { Target : SecretId }
    type ConnectionDisconnectResponse = { Disconnected : bool }

    [<RequireQualifiedAccess>]
    type ConnectionResolveRequest = { Target : SecretId }
    type ConnectionResolveResponse = { Kind : ConnectionKind; Value : string }

    /// A session reporting that the PROVIDER refused this credential — the one fact about a
    /// credential's health that the Manager can never work out for itself. A static token
    /// carries no expiry at all, so "it stopped working" only ever arrives from whoever
    /// spent it. `Reason` is what to show a person, not a status code.
    type ConnectionRejectRequest = { Target : SecretId; Reason : string }
    /// Whether this changed anything — false if the credential was already known refused,
    /// so a verb retried three times does not report three fresh faults.
    type ConnectionRejectResponse = { Recorded : bool }

    let private secretId : Codec<SecretId> =
        { Encode =
            fun (id: SecretId) ->
                Encode.object
                    [ "scope", SecretsCodec.secretScope.Encode id.Scope
                      "name", secretName.Encode id.Name ]
          Decode =
            Decode.object (fun get ->
                { SecretId.Scope = get.Required.Field "scope" SecretsCodec.secretScope.Decode
                  SecretId.Name = get.Required.Field "name" secretName.Decode }) }

    let connectionKind : Codec<ConnectionKind> =
        { Encode =
            (fun k ->
                match k with
                | OAuthConnection -> Encode.string "oauth"
                | StaticConnection -> Encode.string "static")
          Decode =
            Decode.string
            |> Decode.andThen (function
                | "oauth" -> Decode.succeed OAuthConnection
                | "static" -> Decode.succeed StaticConnection
                | other -> Decode.fail (sprintf "Unknown connection kind: %s" other)) }

    /// Health rides as an OPTIONAL `signInRequired` reason rather than a tagged union:
    /// present means `SignInRequired`, absent means usable. A frame minted before this
    /// field existed therefore decodes as healthy, which is what it meant — the same
    /// additive move `refreshExpiresAt` and `dialect` made in the credential envelope.
    let connectionStatus : Codec<ConnectionStatus> =
        { Encode =
            fun (s: ConnectionStatus) ->
                Encode.object
                    [ "id", secretId.Encode s.Id
                      "kind", connectionKind.Encode s.Kind
                      "signInRequired",
                        (match s.Health with
                         | ConnectionUsable -> Encode.nil
                         | SignInRequired reason -> Encode.string reason)
                      "updatedAt", Codec.timestamp.Encode s.UpdatedAt ]
          Decode =
            Decode.object (fun get ->
                { ConnectionStatus.Id = get.Required.Field "id" secretId.Decode
                  ConnectionStatus.Kind = get.Required.Field "kind" connectionKind.Decode
                  ConnectionStatus.Health =
                    get.Optional.Field "signInRequired" Decode.string
                    |> Option.map SignInRequired
                    |> Option.defaultValue ConnectionUsable
                  ConnectionStatus.UpdatedAt = get.Required.Field "updatedAt" Codec.timestamp.Decode }) }

    /// The `/control/connections` SSE frame: every connection the receiving launch may
    /// currently read. Metadata only — the type cannot carry a value.
    let connectionStatusList : Codec<ConnectionStatusList> =
        { Encode =
            fun (l: ConnectionStatusList) ->
                Encode.object [ "connections", l.Connections |> List.map connectionStatus.Encode |> Encode.list ]
          Decode =
            Decode.object (fun get ->
                { ConnectionStatusList.Connections =
                    get.Required.Field "connections" (Decode.list connectionStatus.Decode) }) }

    let connectionBeginRequest : Codec<ConnectionBeginRequest> =
        { Encode =
            fun (r: ConnectionBeginRequest) ->
                Encode.object
                    [ "target", secretId.Encode r.Target
                      "authorizeUrl", Encode.string r.AuthorizeUrl
                      "tokenUrl", Encode.string r.TokenUrl
                      "clientId", Encode.string r.ClientId
                      "scopes", Encode.string r.Scopes
                      "redirectUri", Encode.option Encode.string r.RedirectUri
                      "tokenDialect", Encode.string (TokenRequestDialect.describe r.TokenDialect) ]
          Decode =
            Decode.object (fun get ->
                { ConnectionBeginRequest.Target = get.Required.Field "target" secretId.Decode
                  ConnectionBeginRequest.AuthorizeUrl = get.Required.Field "authorizeUrl" Decode.string
                  ConnectionBeginRequest.TokenUrl = get.Required.Field "tokenUrl" Decode.string
                  ConnectionBeginRequest.ClientId = get.Required.Field "clientId" Decode.string
                  ConnectionBeginRequest.Scopes = get.Required.Field "scopes" Decode.string
                  ConnectionBeginRequest.RedirectUri = get.Optional.Field "redirectUri" Decode.string
                  // Optional: a session built before dialects existed speaks the standard,
                  // so an older session keeps working against a newer Manager.
                  ConnectionBeginRequest.TokenDialect =
                    get.Optional.Field "tokenDialect" Decode.string
                    |> Option.map TokenRequestDialect.ofString
                    |> Option.defaultValue FormEncoded }) }

    let connectionBeginResponse : Codec<ConnectionBeginResponse> =
        { Encode =
            fun (r: ConnectionBeginResponse) ->
                Encode.object
                    [ "authorizeUrl", Encode.string r.AuthorizeUrl
                      "state", Encode.string r.State ]
          Decode =
            Decode.object (fun get ->
                { ConnectionBeginResponse.AuthorizeUrl = get.Required.Field "authorizeUrl" Decode.string
                  ConnectionBeginResponse.State = get.Required.Field "state" Decode.string }) }

    let connectionCompleteRequest : Codec<ConnectionCompleteRequest> =
        { Encode =
            fun (r: ConnectionCompleteRequest) ->
                Encode.object [ "target", secretId.Encode r.Target; "code", Encode.string r.Code ]
          Decode =
            Decode.object (fun get ->
                { ConnectionCompleteRequest.Target = get.Required.Field "target" secretId.Decode
                  ConnectionCompleteRequest.Code = get.Required.Field "code" Decode.string }) }

    let connectionPutRequest : Codec<ConnectionPutRequest> =
        { Encode =
            fun (r: ConnectionPutRequest) ->
                Encode.object [ "target", secretId.Encode r.Target; "value", Encode.string r.Value ]
          Decode =
            Decode.object (fun get ->
                { ConnectionPutRequest.Target = get.Required.Field "target" secretId.Decode
                  ConnectionPutRequest.Value = get.Required.Field "value" Decode.string }) }

    let connectionPutGrantRequest : Codec<ConnectionPutGrantRequest> =
        { Encode =
            fun (r: ConnectionPutGrantRequest) ->
                Encode.object
                    [ "target", secretId.Encode r.Target
                      "accessToken", Encode.string r.AccessToken
                      "refreshToken", Encode.option Encode.string r.RefreshToken
                      "expiresIn", Encode.option Encode.int r.ExpiresIn
                      "refreshTokenExpiresIn", Encode.option Encode.int r.RefreshTokenExpiresIn
                      "tokenUrl", Encode.string r.TokenUrl
                      "clientId", Encode.string r.ClientId
                      "tokenDialect", Encode.string (TokenRequestDialect.describe r.TokenDialect) ]
          Decode =
            Decode.object (fun get ->
                { ConnectionPutGrantRequest.Target = get.Required.Field "target" secretId.Decode
                  ConnectionPutGrantRequest.AccessToken = get.Required.Field "accessToken" Decode.string
                  // Every lifetime is optional because a provider states them only when it
                  // means them: a GitHub App with user-token expiration turned off answers
                  // an access token and nothing else, and that grant is simply one that
                  // never comes due.
                  ConnectionPutGrantRequest.RefreshToken = get.Optional.Field "refreshToken" Decode.string
                  ConnectionPutGrantRequest.ExpiresIn = get.Optional.Field "expiresIn" Decode.int
                  ConnectionPutGrantRequest.RefreshTokenExpiresIn =
                    get.Optional.Field "refreshTokenExpiresIn" Decode.int
                  ConnectionPutGrantRequest.TokenUrl = get.Required.Field "tokenUrl" Decode.string
                  ConnectionPutGrantRequest.ClientId = get.Required.Field "clientId" Decode.string
                  ConnectionPutGrantRequest.TokenDialect =
                    get.Optional.Field "tokenDialect" Decode.string
                    |> Option.map TokenRequestDialect.ofString
                    |> Option.defaultValue FormEncoded }) }

    let connectionDisconnectRequest : Codec<ConnectionDisconnectRequest> =
        { Encode = fun (r: ConnectionDisconnectRequest) -> Encode.object [ "target", secretId.Encode r.Target ]
          Decode =
            Decode.object (fun get ->
                { ConnectionDisconnectRequest.Target = get.Required.Field "target" secretId.Decode }) }

    let connectionDisconnectResponse : Codec<ConnectionDisconnectResponse> =
        { Encode = fun (r: ConnectionDisconnectResponse) -> Encode.object [ "disconnected", Encode.bool r.Disconnected ]
          Decode =
            Decode.object (fun get ->
                { ConnectionDisconnectResponse.Disconnected = get.Required.Field "disconnected" Decode.bool }) }

    let connectionResolveRequest : Codec<ConnectionResolveRequest> =
        { Encode = fun (r: ConnectionResolveRequest) -> Encode.object [ "target", secretId.Encode r.Target ]
          Decode =
            Decode.object (fun get ->
                { ConnectionResolveRequest.Target = get.Required.Field "target" secretId.Decode }) }

    let connectionRejectRequest : Codec<ConnectionRejectRequest> =
        { Encode =
            fun (r: ConnectionRejectRequest) ->
                Encode.object
                    [ "target", secretId.Encode r.Target
                      "reason", Encode.string r.Reason ]
          Decode =
            Decode.object (fun get ->
                { ConnectionRejectRequest.Target = get.Required.Field "target" secretId.Decode
                  ConnectionRejectRequest.Reason = get.Required.Field "reason" Decode.string }) }

    let connectionRejectResponse : Codec<ConnectionRejectResponse> =
        { Encode = fun (r: ConnectionRejectResponse) -> Encode.object [ "recorded", Encode.bool r.Recorded ]
          Decode =
            Decode.object (fun get ->
                { ConnectionRejectResponse.Recorded = get.Required.Field "recorded" Decode.bool }) }

    let connectionResolveResponse : Codec<ConnectionResolveResponse> =
        { Encode =
            fun (r: ConnectionResolveResponse) ->
                Encode.object
                    [ "kind", connectionKind.Encode r.Kind
                      "value", Encode.string r.Value ]
          Decode =
            Decode.object (fun get ->
                { ConnectionResolveResponse.Kind = get.Required.Field "kind" connectionKind.Decode
                  ConnectionResolveResponse.Value = get.Required.Field "value" Decode.string }) }

    /// One Running session in the registry stream: what an operator's
    /// serving binding needs to expose it — the OS-assigned port — plus identity for
    /// display and the pid for supervision-side correlation.
    type SessionRegistryEntry =
        { Id : SessionId
          Name : string
          Port : int
          Pid : int
          /// The build this launch reported on its readiness line. Optional on the wire as
          /// well as in the Manager: a consumer that meets a session from a bundle older
          /// than the field reads `None` and says nothing, rather than a placeholder it
          /// would then have to explain.
          Build : string option }

    /// A `/sessions/stream` SSE frame: the FULL current set of Running sessions.
    /// Snapshot semantics, never deltas — a consumer applies each frame wholesale, so
    /// reconnecting (whose first frame is the current snapshot) is the recovery path.
    type SessionRegistryFrame = { Sessions : SessionRegistryEntry list }

    let private sessionRegistryEntry : Codec<SessionRegistryEntry> =
        { Encode =
            fun (e: SessionRegistryEntry) ->
                Encode.object
                    [ yield "id", Codec.sessionId.Encode e.Id
                      yield "name", Encode.string e.Name
                      yield "port", Encode.int e.Port
                      yield "pid", Encode.int e.Pid
                      match e.Build with
                      | Some build -> yield "build", Encode.string build
                      | None -> () ]
          Decode =
            Decode.object (fun get ->
                { SessionRegistryEntry.Id = get.Required.Field "id" Codec.sessionId.Decode
                  SessionRegistryEntry.Name = get.Required.Field "name" Decode.string
                  SessionRegistryEntry.Port = get.Required.Field "port" Decode.int
                  SessionRegistryEntry.Pid = get.Required.Field "pid" Decode.int
                  SessionRegistryEntry.Build = get.Optional.Field "build" Decode.string }) }

    let sessionRegistryFrame : Codec<SessionRegistryFrame> =
        { Encode =
            fun (f: SessionRegistryFrame) ->
                Encode.object [ "sessions", f.Sessions |> List.map sessionRegistryEntry.Encode |> Encode.list ]
          Decode =
            Decode.object (fun get ->
                { SessionRegistryFrame.Sessions =
                    get.Required.Field "sessions" (Decode.list sessionRegistryEntry.Decode) }) }

    let toString (codec: Codec<'a>) (value: 'a) : string = codec.Encode value |> Encode.toString 0

    let fromString (codec: Codec<'a>) (json: string) : Result<'a, string> = Decode.fromString codec.Decode json
