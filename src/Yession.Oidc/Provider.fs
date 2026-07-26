namespace Yession.Oidc

open Yession.Domain

// The OIDC provider core: dynamic client registration, the authorization-code + PKCE
// state machine, and the /authorize + /token request validators. Pure — clock, secret
// minting, and hashing are injected — so the conformance suite runs in the cheap tier.
//
// Lifetimes mirror the control channel's "a secret dies with its launch" rule: a client
// registration is bound to the per-launch control secret that made it and is revoked
// with it. Codes are single-use and short-lived. Everything is in-memory: every child
// session dies with the Manager (parent guard), so nothing can outlive this state.

/// A dynamically-registered OAuth client — one per session launch.
type RegisteredClient =
    { ClientId : string
      ClientSecret : string
      RedirectUri : string
      /// The per-launch control secret this registration rode in on; revoking that
      /// secret revokes the client.
      ControlSecret : string }

/// Registered clients, keyed by client id (the session id — sessions are the only
/// clients, and re-registration on relaunch replaces the previous launch's client).
type ClientRegistry (mintSecret: unit -> string) =
    let mutable clients : Map<string, RegisteredClient> = Map.empty

    member _.Register (controlSecret: string) (sessionId: SessionId) (redirectUri: string) : RegisteredClient =
        let client =
            { ClientId = SessionId.value sessionId
              ClientSecret = mintSecret ()
              RedirectUri = redirectUri
              ControlSecret = controlSecret }
        clients <- Map.add client.ClientId client clients
        client

    member _.TryFind (clientId: string) : RegisteredClient option =
        Map.tryFind clientId clients

    member _.RevokeByControlSecret (controlSecret: string) : unit =
        clients <- clients |> Map.filter (fun _ c -> c.ControlSecret <> controlSecret)

/// An issued, not-yet-redeemed authorization code and everything it is bound to.
type AuthorizationCode =
    { ClientId : string
      RedirectUri : string
      /// The PKCE S256 challenge; the redeeming verifier must hash to it.
      Challenge : string
      /// The authenticated identity the code will redeem for: subject plus the verified
      /// claims when the strategy attributed a real user.
      Identity : GrantedIdentity
      /// The browser peer that rode the authorize bounce (docs/plans/07), when the
      /// client sent one — what the Manager witnesses into the launch at issuance.
      Peer : PeerId option
      IssuedAtUnix : int64 }

/// Issued authorization codes. Single-use: a code is burned on FIRST redeem attempt,
/// successful or not (RFC 6749 §4.1.2 — a code that fails validation must not be
/// retryable). 60-second expiry.
type CodeStore (mintCode: unit -> string, nowUnix: unit -> int64, sha256Base64Url: string -> string) =
    let lifetimeSeconds = 60L
    let mutable codes : Map<string, AuthorizationCode> = Map.empty

    member _.Issue (client: RegisteredClient) (challenge: string) (identity: GrantedIdentity) (peer: PeerId option) : string =
        let code = mintCode ()
        codes <-
            Map.add
                code
                { ClientId = client.ClientId
                  RedirectUri = client.RedirectUri
                  Challenge = challenge
                  Identity = identity
                  Peer = peer
                  IssuedAtUnix = nowUnix () }
                codes
        code

    /// Redeem a code for its identity (and witnessed peer, when one rode the bounce).
    /// Every check failure is a distinct `invalid_grant` description; the code is
    /// removed before any check runs.
    member _.Redeem (code: string) (clientId: string) (redirectUri: string) (verifier: string) : Result<GrantedIdentity * PeerId option, string> =
        match Map.tryFind code codes with
        | None -> Error "unknown or already used code"
        | Some issued ->
            codes <- Map.remove code codes
            if nowUnix () - issued.IssuedAtUnix > lifetimeSeconds then Error "code expired"
            elif issued.ClientId <> clientId then Error "code was issued to another client"
            elif issued.RedirectUri <> redirectUri then Error "redirect_uri mismatch"
            elif sha256Base64Url verifier <> issued.Challenge then Error "PKCE verification failed"
            else Ok (issued.Identity, issued.Peer)

/// A validated /authorize request, ready for user authentication + code issuance.
type AuthorizeRequest =
    { Client : RegisteredClient
      State : string
      Challenge : string
      /// The browser's peer id, when the login carried one (docs/plans/07).
      Peer : PeerId option }

type AuthorizeError =
    /// The client or redirect_uri could not be validated — respond 400, NEVER redirect
    /// (RFC 6749 §4.1.2.1: redirecting to an unvalidated URI is an open redirector).
    | ClientError of message: string
    /// The request is otherwise malformed — redirect back to the VALIDATED redirect_uri
    /// with `error` (+ state when present).
    | RedirectableError of redirectUri: string * error: string * state: string option

type TokenGrant =
    { Client : RegisteredClient
      Identity : GrantedIdentity
      /// The peer witnessed at /authorize, riding through to issuance recording.
      Peer : PeerId option }

type TokenError =
    /// 401 + `{"error":"invalid_client"}` — unknown client or bad secret.
    | InvalidClient
    /// 400 + `{"error":"invalid_grant"}` — the code failed redemption.
    | InvalidGrant of detail: string
    /// 400 + `{"error":"invalid_request"}` — missing/malformed parameters.
    | InvalidRequest of detail: string

module Provider =

    /// Validate an /authorize request's query parameters (RFC 6749 §4.1.1 + RFC 7636).
    /// `state` and `code_challenge` are required here even though OAuth marks state
    /// RECOMMENDED: the only clients are Session Processes using openid-client, which
    /// always sends both, and requiring them keeps every login CSRF- and
    /// injection-protected.
    let authorize (registry: ClientRegistry) (query: string -> string option) : Result<AuthorizeRequest, AuthorizeError> =
        match query "client_id" |> Option.bind registry.TryFind with
        | None -> Error (ClientError "unknown client_id")
        | Some client ->
            match query "redirect_uri" with
            | Some uri when uri = client.RedirectUri ->
                let state = query "state"
                if query "response_type" <> Some "code" then
                    Error (RedirectableError (uri, "unsupported_response_type", state))
                else
                    match state, query "code_challenge", query "code_challenge_method" with
                    | Some state, Some challenge, Some "S256" ->
                        // Optional and self-asserted at this hop: the value only ever
                        // scopes the asserting browser's own secrets (Plan 07 policy).
                        let peer =
                            query "peer_id"
                            |> Option.bind (fun raw ->
                                match PeerId.create raw with
                                | Ok peer -> Some peer
                                | Error _ -> None)
                        Ok { Client = client; State = state; Challenge = challenge; Peer = peer }
                    | _ -> Error (RedirectableError (uri, "invalid_request", state))
            | _ -> Error (ClientError "redirect_uri mismatch")

    /// Validate a /token request's form body (RFC 6749 §4.1.3 + §5.2) and redeem the
    /// code. `secretsEqual` is injected so the adapter can compare in constant time.
    let token
        (registry: ClientRegistry)
        (codes: CodeStore)
        (secretsEqual: string -> string -> bool)
        (form: Map<string, string>)
        : Result<TokenGrant, TokenError> =
        if Map.tryFind "grant_type" form <> Some "authorization_code" then
            Error (InvalidRequest "grant_type must be authorization_code")
        else
            match Map.tryFind "client_id" form |> Option.bind registry.TryFind with
            | None -> Error InvalidClient
            | Some client ->
                match Map.tryFind "client_secret" form with
                | Some secret when secretsEqual secret client.ClientSecret ->
                    match Map.tryFind "code" form, Map.tryFind "redirect_uri" form, Map.tryFind "code_verifier" form with
                    | Some code, Some redirectUri, Some verifier ->
                        codes.Redeem code client.ClientId redirectUri verifier
                        |> Result.map (fun (identity, peer) -> { Client = client; Identity = identity; Peer = peer })
                        |> Result.mapError InvalidGrant
                    | _ -> Error (InvalidRequest "code, redirect_uri and code_verifier are required")
                | _ -> Error InvalidClient
