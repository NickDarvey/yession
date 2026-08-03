module Yession.Host.Broker

// The Manager's connection broker (Plan 08): standards-only OAuth over the encrypted
// secret store. Sessions drive it over the control channel with the provider's
// endpoints AS DATA (authorize/token URL, client id, scopes) — the broker never learns
// which service it brokered; its one owned constant is its own public redirect URI on
// the Manager's fixed port (session ports are OS-assigned, nothing stable to pin a
// provider's registered redirect URI to). PKCE, the code exchange, and refresh all
// happen HERE: refresh tokens never reach a session process, and the single Manager
// refresher means providers that rotate refresh tokens on use never see two racing
// refreshes for one stored credential.

open Fable.Core
open Yession.Domain
open Yession.Manager

/// 32 random bytes, base64url — a PKCE code verifier (RFC 7636 §4.1: 43 chars).
[<Emit("Buffer.from(crypto.getRandomValues(new Uint8Array(32))).toString('base64url')")>]
let private randomVerifier () : string = jsNative

/// The S256 code challenge for a verifier: BASE64URL(SHA256(ASCII(verifier))).
[<Emit("crypto.subtle.digest('SHA-256', Buffer.from($0, 'ascii')).then(d => Buffer.from(d).toString('base64url'))")>]
let private s256Challenge (verifier: string) : JS.Promise<string> = jsNative

/// POST a grant to a token endpoint in the dialect the flow declared (the content type
/// travels with the body, so the two can never disagree). A non-2xx answer becomes an
/// Error with the provider's body (OAuth error JSON is designed to be shown).
[<Emit("""fetch($0, { method: 'POST', headers: { 'content-type': $1, 'accept': 'application/json' }, body: $2 })
  .then(async r => ({ status: r.status, body: await r.text() }))""")>]
let private postGrant (url: string) (contentType: string) (body: string) : JS.Promise<{| status: int; body: string |}> = jsNative

let private grantAt (tokenUrl: string) (request: TokenRequest) : Async<Result<string, string>> =
    async {
        try
            let! reply = postGrant tokenUrl request.ContentType request.Body |> Interop.awaitPromise
            if reply.status >= 200 && reply.status < 300 then return Ok reply.body
            else return Error (sprintf "token endpoint refused (%d): %s" reply.status reply.body)
        with e ->
            return Error (sprintf "token endpoint unreachable: %s" e.Message)
    }

/// What the broker observed — identifiers only, adapted to audit records by the caller.
type BrokerObservation =
    | Connected of SecretId * ConnectionKind
    | Disconnected of SecretId
    | Resolved of SecretId * ConnectionKind * refreshed: bool
    | RefreshFailed of SecretId * reason: string

type BrokerService =
    { /// Mint state+PKCE for a flow and return the provider authorize URL to open.
      Begin : ControlWire.ConnectionBeginRequest -> Async<Result<ControlWire.ConnectionBeginResponse, string>>
      /// Redirect completion (the public callback), state then code: the single-use
      /// state IS the authorization — minted only for a target the policy permitted
      /// at begin.
      CompleteCallback : string -> string -> Async<Result<SecretId, string>>
      /// Manual completion (paste): the payload is `code#state`; the pended target
      /// must be the caller-authorized one.
      Complete : SecretId -> string -> Async<Result<unit, string>>
      /// Store a pasted static token verbatim.
      Put : SecretId -> string -> Async<Result<unit, string>>
      Disconnect : SecretId -> Async<Result<bool, string>>
      /// Metadata for whichever of `targets` exist — never values.
      StatusOf : SecretId list -> Async<ConnectionStatusList>
      /// The credential's current value, refreshing a due OAuth grant first (standard
      /// refresh grant at the envelope's own token URL). The ONLY value-returning path.
      Resolve : SecretId -> Async<Result<ConnectionKind * string, string>> }

// The redirect URI arrives as a thunk: it derives from the Manager's public origin,
// which is only known once its server listens — flows begin well after that.
let create
    (redirectUri: unit -> string)
    (store: SecretStore.SecretStore)
    (observe: BrokerObservation -> unit)
    : BrokerService =

    let pending = PendingFlows (fun () -> System.DateTimeOffset.UtcNow.ToUnixTimeSeconds ())
    let now () = System.DateTimeOffset.UtcNow

    let storeCredential (target: SecretId) (credential: BrokeredCredential) : Async<Result<unit, string>> =
        async {
            match! store.Set target (BrokeredCredentialCodec.toString credential) with
            | Ok _ ->
                observe (Connected (target, BrokerFlow.kindOf credential))
                return Ok ()
            | Error e -> return Error e
        }

    let exchange (flow: PendingFlow) (state: string) (code: string) : Async<Result<unit, string>> =
        async {
            // RFC 6749 §4.1.3: the exchange repeats EXACTLY the redirect_uri the
            // authorize URL carried — the flow's pended one, never re-derived.
            let request =
                BrokerFlow.exchangeRequest flow.Dialect flow.ClientId flow.RedirectUri flow.Verifier state code
            match! grantAt flow.TokenUrl request with
            | Error e -> return Error e
            | Ok json ->
                match BrokerFlow.decodeTokenResponse (now ()) flow.TokenUrl flow.ClientId flow.Dialect json with
                | Error e -> return Error (sprintf "token response malformed: %s" e)
                | Ok grant -> return! storeCredential flow.Target (BrokeredOAuth grant)
        }

    let loadCredential (target: SecretId) : Async<Result<BrokeredCredential option, string>> =
        async {
            match! store.Resolve target with
            | Error e -> return Error e
            | Ok None -> return Ok None
            | Ok (Some raw) ->
                match BrokeredCredentialCodec.fromString raw with
                | Error e -> return Error (sprintf "stored credential malformed: %s" e)
                | Ok credential -> return Ok (Some credential)
        }

    { Begin =
        fun request ->
            async {
                let verifier = randomVerifier ()
                let state = randomVerifier ()
                let! challenge = s256Challenge verifier |> Interop.awaitPromise
                // Where the provider sends the code: the Manager's own public callback
                // by default; a session-supplied URI when the provider's registered
                // redirect set cannot include this Manager (completion arrives as a
                // paste then — e.g. a provider-hosted code-display page).
                let flowRedirect = defaultArg request.RedirectUri (redirectUri ())
                pending.Add
                    state
                    { Verifier = verifier
                      Target = request.Target
                      TokenUrl = request.TokenUrl
                      ClientId = request.ClientId
                      Scopes = request.Scopes
                      RedirectUri = flowRedirect
                      Dialect = request.TokenDialect }
                return
                    Ok
                        { ControlWire.ConnectionBeginResponse.AuthorizeUrl =
                            BrokerFlow.authorizeUrl request.AuthorizeUrl request.ClientId flowRedirect request.Scopes state challenge
                          ControlWire.ConnectionBeginResponse.State = state }
            }
      CompleteCallback =
        fun state code ->
            async {
                match pending.Take state with
                | None -> return Error "unknown or expired sign-in flow"
                | Some flow ->
                    match! exchange flow state code with
                    | Ok () -> return Ok flow.Target
                    | Error e -> return Error e
            }
      Complete =
        fun target pasted ->
            async {
                // The paste payload is `code` or `code#state` (providers that show the
                // code for manual copy append the state so the flow can be re-keyed).
                match pasted.Split '#' |> Array.toList with
                | [ code; state ] | [ code; state; _ ] when state <> "" ->
                    match pending.Take state with
                    | Some flow when flow.Target = target -> return! exchange flow state code
                    | Some _ -> return Error "pasted code belongs to a different sign-in"
                    | None -> return Error "unknown or expired sign-in flow"
                | _ -> return Error "expected a pasted code of the form code#state"
            }
      Put =
        fun target value ->
            async {
                if System.String.IsNullOrWhiteSpace value then return Error "token cannot be empty"
                else return! storeCredential target (BrokeredStatic (value.Trim ()))
            }
      Disconnect =
        fun target ->
            async {
                match! store.Delete target with
                | Ok existed ->
                    if existed then observe (Disconnected target)
                    return Ok existed
                | Error e -> return Error e
            }
      StatusOf =
        fun targets ->
            async {
                let mutable statuses = []
                for target in targets do
                    match! loadCredential target with
                    | Ok (Some credential) ->
                        let updatedAt =
                            store.List target.Scope
                            |> List.tryFind (fun m -> m.Id = target)
                            |> Option.map (fun m -> m.UpdatedAt)
                            |> Option.defaultValue (now ())
                        statuses <-
                            statuses
                            @ [ { ConnectionStatus.Id = target
                                  Kind = BrokerFlow.kindOf credential
                                  UpdatedAt = updatedAt } ]
                    | Ok None | Error _ -> ()
                return { Connections = statuses }
            }
      Resolve =
        fun target ->
            async {
                match! loadCredential target with
                | Error e -> return Error e
                | Ok None -> return Error "no credential connected"
                | Ok (Some credential) ->
                    if not (BrokerFlow.needsRefresh (now ()) credential) then
                        observe (Resolved (target, BrokerFlow.kindOf credential, false))
                        return Ok (BrokerFlow.kindOf credential, BrokerFlow.valueOf credential)
                    else
                        match credential with
                        | BrokeredStatic _ ->
                            // Unreachable (needsRefresh is false for static) but total.
                            observe (Resolved (target, StaticConnection, false))
                            return Ok (StaticConnection, BrokerFlow.valueOf credential)
                        | BrokeredOAuth grant ->
                            match grant.RefreshToken with
                            | None ->
                                observe (Resolved (target, OAuthConnection, false))
                                return Ok (OAuthConnection, grant.AccessToken)
                            | Some refreshToken ->
                                match! grantAt grant.TokenUrl (BrokerFlow.refreshRequest grant refreshToken) with
                                | Error e ->
                                    // Keep the old entry: the user can reconnect, and the
                                    // stale token may still be honored briefly.
                                    observe (RefreshFailed (target, e))
                                    return Error (sprintf "token refresh failed: %s" e)
                                | Ok json ->
                                    match BrokerFlow.decodeTokenResponse (now ()) grant.TokenUrl grant.ClientId grant.Dialect json with
                                    | Error e ->
                                        observe (RefreshFailed (target, e))
                                        return Error (sprintf "token refresh response malformed: %s" e)
                                    | Ok fresh ->
                                        let merged = BrokerFlow.merged grant fresh
                                        match! store.Set target (BrokeredCredentialCodec.toString (BrokeredOAuth merged)) with
                                        | Error e -> return Error e
                                        | Ok _ ->
                                            observe (Resolved (target, OAuthConnection, true))
                                            return Ok (OAuthConnection, merged.AccessToken)
            } }
