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

open System
open System.Collections.Generic

open Fable.Core
open Yession.Domain
open Yession.Domain.Access
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

/// Why a token request produced no grant, and the two distinctions callers act on.
///
/// `Final` means the provider said this authorization is DEAD, not that it could not answer
/// — so retrying cannot help and a person has to sign in again. Everything else (a 5xx, a
/// timeout, a socket refused) is "try later", and treating those alike would send someone to
/// re-authorize because their wifi dropped.
///
/// `Outcome` is the HTTP shape of the same failure, kept because "is this authorization
/// dead" and "is this call worth making again" are different questions with different
/// answers: a 400 that is not `invalid_grant` is worth no retry and says nothing about the
/// grant, and a socket that never connected is worth several and says nothing either.
type GrantFailure =
    { Message : string
      Final : bool
      Outcome : Resilience.Http.Outcome }

/// RFC 6749 §5.2: `invalid_grant` is the standard code for an authorization grant that is
/// "expired, revoked, malformed, or invalid" — the shape of a refresh token whose App was
/// revoked before its stated expiry, which no clock here can predict.
///
/// This stays STANDARDS-ONLY, like the rest of the broker: the code is from the RFC, not
/// from any provider's dialect, so the broker still never learns which service it brokered.
/// A provider that reports the same thing its own way is simply not final here, and reads as
/// "try later" — the safe direction to be wrong in.
let private isFinalRefusal (status: int) (body: string) : bool =
    status >= 400 && status < 500 && body.Contains "invalid_grant"

/// One request to a token endpoint, its outcome as a value. Takes its pair as a tuple
/// because that is the shape the resilience decorators compose over.
let private askFor ((tokenUrl, request): string * TokenRequest) : Async<Result<string, GrantFailure>> =
    async {
        try
            let! reply = postGrant tokenUrl request.ContentType request.Body |> Interop.awaitPromise
            if reply.status >= 200 && reply.status < 300 then return Ok reply.body
            else
                return
                    Error
                        { Message = sprintf "token endpoint refused (%d): %s" reply.status reply.body
                          Final = isFinalRefusal reply.status reply.body
                          Outcome = Resilience.Http.Answered (reply.status, None) }
        with e ->
            return
                Error
                    { Message = sprintf "token endpoint unreachable: %s" e.Message
                      Final = false
                      Outcome = Resilience.Http.Unreached }
    }

/// Whether asking again can help: the shared HTTP rule, and nothing else.
///
/// `Final` is deliberately NOT a second clause here. A grant the provider called dead is a
/// 4xx by `isFinalRefusal`'s own definition, and the shared rule already refuses to retry a
/// 4xx — so a `Final -> Fatal` clause beside it would be a rule that never fires and could
/// only ever come to disagree. `Final` answers the other question, which is this module's
/// alone: whether to tell the person their sign-in is finished.
let grantVerdict (failure: GrantFailure) : Resilience.Verdict = Resilience.Http.verdict failure.Outcome

/// How long one token request may take before it counts as no answer. Fifteen seconds
/// because a refresh runs INSIDE something a person is waiting on — a turn, a git verb — and
/// a hung fetch there is a turn that never starts and never says why.
let grantDeadline = TimeSpan.FromSeconds 15.0

let private grantTimedOut =
    { Message = "the token endpoint did not answer in time"
      Final = false
      Outcome = Resilience.Http.Unreached }

/// Asking a token endpoint, as everything below the composition root sees it: a settled body
/// or a failure, with whatever resilience was put behind it. A tuple because that is the
/// shape the decorators compose over.
type GrantLeg = string * TokenRequest -> Async<Result<string, GrantFailure>>

/// The unguarded leg — one request, no waiting. What the composition root wraps.
let asking : GrantLeg = askFor

/// The shipped policy for a token endpoint: three retries, backed off from 300ms to a 4s
/// ceiling, jittered — every session refreshes against the same few providers, and a Manager
/// restart brings them all due at once.
///
/// It earns its place on the REFRESH rather than on the sign-in: a refresh happens where
/// nobody is looking, per turn and per network verb, and its failure reads as a broken
/// credential. Before this, one dropped packet was a turn that could not run and a panel
/// that said so.
let grantPolicy
    (sleep: TimeSpan -> Async<unit>)
    (random: unit -> float)
    : Resilience.Policy<GrantFailure> =
    { Schedule =
        Resilience.Schedule.exponential (TimeSpan.FromMilliseconds 300.0) 2.0 (TimeSpan.FromSeconds 4.0) 3
        |> Resilience.Schedule.jittered 0.5 random
      Classify = grantVerdict
      Sleep = sleep
      Observe = ignore }

/// The leg as it ships: each attempt bounded, handled failures retried.
///
/// `waiting` is the DEADLINE's clock and is deliberately not the policy's. They look like one
/// parameter and are not: a suite that spends its backoff in zero real time would, sharing a
/// clock, spend the deadline in zero time too — and then every attempt times out before it is
/// made, which is a green-looking policy that never asks anybody anything. That is not
/// hypothetical; it is what this function was written to stop being possible.
let resilient (waiting: TimeSpan -> Async<unit>) (policy: Resilience.Policy<GrantFailure>) : GrantLeg =
    asking
    |> Resilience.Policy.deadline waiting grantDeadline grantTimedOut
    |> Resilience.Policy.guard policy

/// What the broker observed — identifiers only, adapted to audit records by the caller.
type BrokerObservation =
    | Connected of SecretId * ConnectionKind
    | Disconnected of SecretId
    | Resolved of SecretId * ConnectionKind * refreshed: bool
    | RefreshFailed of SecretId * reason: string
    /// The provider refused this credential, as reported by whoever spent it.
    | Rejected of SecretId * reason: string

/// Whether this observation changes what a session may currently READ — which is the only
/// question a status broadcast asks.
///
/// It lives here, beside the observations, rather than at whichever composition root
/// happens to subscribe: the answer is a fact about the observation, and a subscriber that
/// decided for itself is a subscriber that can be wrong on its own. There were two of them
/// already — the Manager and the tests' control server — and they agreed only because
/// somebody kept them in step by hand.
///
/// `RefreshFailed` is in because a credential that stopped working changes what is readable
/// exactly as disconnecting one does. It used to be out, which is why a grant could die
/// mid-session while every panel watching went on showing the status it was last sent.
/// `Resolved` is out: a turn spending a credential changes nothing about it, and a frame
/// per turn would be a broadcast storm saying nothing.
let changesReadableStatus (observation: BrokerObservation) : bool =
    match observation with
    | Connected _
    | Disconnected _
    | RefreshFailed _
    | Rejected _ -> true
    | Resolved _ -> false

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
      /// Store a grant the SESSION obtained itself (the device flow, RFC 8628) as a
      /// refreshable grant. `Put`'s sibling, and the difference is the whole point: a
      /// pasted token cannot rotate and this one can, so the two must not share a path.
      PutGrant : ControlWire.ConnectionPutGrantRequest -> Async<Result<unit, string>>
      Disconnect : SecretId -> Async<Result<bool, string>>
      /// Record that the PROVIDER refused this credential. The one fact about a
      /// credential's health that cannot be worked out here: a static token carries no
      /// expiry, so "it stopped working" only ever arrives from whoever spent it.
      /// Answers whether this changed anything.
      Reject : SecretId -> string -> Async<Result<bool, string>>
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
    (ask: GrantLeg)
    : BrokerService =

    // Every token request goes through the leg the composition root handed in, so no call
    // site below here holds a notion of retrying, waiting, or giving up.
    let grantAt (tokenUrl: string) (request: TokenRequest) = ask (tokenUrl, request)

    let pending = PendingFlows (fun () -> System.DateTimeOffset.UtcNow.ToUnixTimeSeconds ())
    let now () = System.DateTimeOffset.UtcNow

    // What a provider has told us about a credential we hold, keyed by target.
    //
    // In MEMORY, deliberately, and not in the encrypted envelope. Writing it there would
    // rewrite `secrets.json` — re-encrypting a credential — every time a 401 came back, for
    // a fact the next use re-learns anyway. It sits beside `inFlight` because it is the same
    // kind of thing: what this Manager currently knows, not what it stores. A restart
    // forgets, and the first verb after one is told again.
    let rejected = Dictionary<SecretId, string> ()

    /// Every write to a target goes through here, so forgetting the old rejection is part of
    /// storing rather than something each caller has to remember: a credential that was just
    /// connected is by definition not the one the provider refused, and a stale mark on it
    /// would send someone to sign in again immediately after they just did.
    let storeCredential (target: SecretId) (credential: BrokeredCredential) : Async<Result<unit, string>> =
        async {
            match! store.Set target (BrokeredCredentialCodec.toString credential) with
            | Ok _ ->
                rejected.Remove target |> ignore
                observe (Connected (target, BrokerFlow.kindOf credential))
                return Ok ()
            | Error e -> return Error e
        }

    let reject (target: SecretId) (reason: string) : Async<Result<bool, string>> =
        async {
            match! store.Resolve target with
            | Error e -> return Error e
            // Nothing connected here, so there is no credential for a provider to have
            // refused. Recording one would leave a health state outliving the thing it
            // describes — and the next connect would find it already marked.
            | Ok None -> return Ok false
            | Ok (Some _) ->
                let alreadyKnown =
                    match rejected.TryGetValue target with
                    | true, existing -> existing = reason
                    | _ -> false
                // A verb that retries reports the same refusal each time; saying it once is
                // what keeps that from being three faults in the audit and three frames on
                // the wire.
                if alreadyKnown then return Ok false
                else
                    rejected.[target] <- reason
                    observe (Rejected (target, reason))
                    return Ok true
        }

    let exchange (flow: PendingFlow) (state: string) (code: string) : Async<Result<unit, string>> =
        async {
            // RFC 6749 §4.1.3: the exchange repeats EXACTLY the redirect_uri the
            // authorize URL carried — the flow's pended one, never re-derived.
            let request =
                BrokerFlow.exchangeRequest flow.Dialect flow.ClientId flow.RedirectUri flow.Verifier state code
            match! grantAt flow.TokenUrl request with
            // Nothing is stored yet, so there is no credential to mark — a first exchange
            // that fails leaves the target exactly as empty as it was.
            | Error failure -> return Error failure.Message
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

    // One refresh per target at a time, and everyone waits on it.
    //
    // A provider that rotates hands back a NEW refresh token and spends the old one, so
    // two concurrent resolves of the same due grant are not merely wasteful: the second
    // presents a token the first already spent, and whichever writes last stores a grant
    // built from a dead one. Sessions resolve per turn and per network verb, so two at
    // once is ordinary rather than exotic.
    //
    // The in-flight map holds a PROMISE, because that is the only thing here that can be
    // awaited twice — an `Async` re-runs on each await, which is exactly the duplicate
    // this exists to prevent. Cleared in both directions, so a failed refresh does not
    // wedge the target forever.
    let inFlight = Dictionary<SecretId, JS.Promise<Result<ConnectionKind * string, string>>> ()


    let refreshGrant (target: SecretId) (grant: OAuthGrant) (refreshToken: string) =
        async {
            match! grantAt grant.TokenUrl (BrokerFlow.refreshRequest grant refreshToken) with
            | Error failure ->
                // Keep the old entry either way: the user can reconnect, and the
                // stale token may still be honored briefly.
                //
                // But a FINAL refusal is a different report from a failed one. A refresh
                // token can be revoked long before the expiry it stated — somebody removes
                // the App at the provider — and no clock here can see that coming, so the
                // provider saying `invalid_grant` is the only way this is ever known.
                // Without it a revoked grant retried forever behind a green panel.
                if failure.Final then
                    let! _ = reject target "the provider refused this sign-in — it may have been revoked"
                    ()
                else observe (RefreshFailed (target, failure.Message))
                return Error (sprintf "token refresh failed: %s" failure.Message)
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
                        // A refresh that worked clears whatever the last one was told.
                        rejected.Remove target |> ignore
                        observe (Resolved (target, OAuthConnection, true))
                        return Ok (OAuthConnection, merged.AccessToken)
        }

    let refreshOnce (target: SecretId) (grant: OAuthGrant) (refreshToken: string) =
        async {
            let pending =
                match inFlight.TryGetValue target with
                | true, running -> running
                | _ ->
                    let running =
                        refreshGrant target grant refreshToken
                        |> Async.StartAsPromise
                    inFlight.[target] <- running
                    running.``then`` (fun outcome -> inFlight.Remove target |> ignore; outcome)
                    |> ignore
                    running
            return! Interop.awaitPromise pending
        }

    let resolve (target: SecretId) : Async<Result<ConnectionKind * string, string>> =
        async {
            match! loadCredential target with
            | Error e -> return Error e
            | Ok None -> return Error "no credential connected"
            | Ok (Some credential) ->
                // Beyond-refresh is asked FIRST, and unconditionally. It used to be reached
                // only inside the `needsRefresh` branch, which made it a question about the
                // ACCESS token's clock — so a grant whose refresh token had lapsed behind a
                // still-live access token resolved happily and died at the provider instead,
                // where the answer arrives as an opaque 401 rather than "sign in again".
                // Nothing to retry means nothing to ask the provider, so this costs no round
                // trip and refuses the same way whatever the other clock says.
                match BrokerFlow.beyondRefresh (now ()) credential with
                | Some why ->
                    let reason = why + " — sign in again"
                    observe (RefreshFailed (target, reason))
                    return Error reason
                | None ->
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
                            | Some refreshToken -> return! refreshOnce target grant refreshToken
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
      PutGrant =
        fun request ->
            async {
                if System.String.IsNullOrWhiteSpace request.AccessToken then
                    return Error "token cannot be empty"
                else
                    // The lifetimes arrive as the provider stated them — seconds from now
                    // — and become absolute against the MANAGER's clock, which is the one
                    // every later refresh decision is made on.
                    let issued = now ()
                    let grant : OAuthGrant =
                        { AccessToken = request.AccessToken.Trim ()
                          RefreshToken = request.RefreshToken
                          ExpiresAt = request.ExpiresIn |> Option.map (fun s -> issued.AddSeconds (float s))
                          RefreshExpiresAt =
                            request.RefreshTokenExpiresIn |> Option.map (fun s -> issued.AddSeconds (float s))
                          TokenUrl = request.TokenUrl
                          ClientId = request.ClientId
                          Dialect = request.TokenDialect }
                    return! storeCredential request.Target (BrokeredOAuth grant)
            }
      Disconnect =
        fun target ->
            async {
                match! store.Delete target with
                | Ok existed ->
                    // Same rule as storing one: the mark describes a credential, and this
                    // target no longer has that credential.
                    rejected.Remove target |> ignore
                    if existed then observe (Disconnected target)
                    return Ok existed
                | Error e -> return Error e
            }
      Reject = fun target reason -> reject target reason
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
                                  // What a provider SAID outranks what we can work out: a
                                  // refusal is evidence, and `beyondRefresh` is inference.
                                  // For a static token the inference is always `None`, so
                                  // the report is the only thing there is.
                                  Health =
                                    match rejected.TryGetValue target with
                                    | true, reason -> SignInRequired reason
                                    | _ ->
                                        // Otherwise the same rule a resolve refuses by, so a
                                        // panel and a turn never disagree about whether this
                                        // credential still works.
                                        BrokerFlow.beyondRefresh (now ()) credential
                                        |> Option.map SignInRequired
                                        |> Option.defaultValue ConnectionUsable
                                  UpdatedAt = updatedAt } ]
                    | Ok None | Error _ -> ()
                return { Connections = statuses }
            }
      Resolve = fun target -> resolve target } 
