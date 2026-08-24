module Yession.Host.ClaudeConnection

// Everything Claude-specific lives HERE, in the session — the Manager's broker is
// standards-only and never learns which service it brokered, and nothing above this file
// knows which provider answered. This module owns: the Anthropic OAuth endpoints and
// Claude Code's public client id (sent to the broker as data), the reserved storage name,
// pasted-token classification, the credential→env-var mapping the Agent SDK consumes, the
// models lookup behind the session's provider-neutral catalogue, and the browser-facing
// /claude* routes the client panel drives.

open Fable.Core
open Fable.Core.JsInterop
open Yession.Domain
open Yession.Domain.Access
open Yession.Manager
open Yession.SessionProcess
open Yession.App
open Yession.Host.Interop

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// The reserved storage name for the Claude credential, per scope. Opaque to the
/// Manager — Claude-ness lives in this session-side choice.
let secretName : SecretName =
    match SecretName.create "claude-code" with
    | Ok name -> name
    | Error e -> failwithf "claude secret name invariant violated: %s" e

/// Claude Code's public OAuth client against claude.ai — the same flow `claude /login`
/// drives. Its registered redirect URIs are Anthropic's own (this Manager's callback
/// cannot be registered, and the client rejects unregistered URIs), so the flow
/// redirects to Anthropic's code-display page and completion arrives as a pasted
/// `code#state`. `code=true` asks the consent page to display the code.
///
/// Anthropic's terms restrict a subscription OAuth token to Claude Code and claude.ai, so
/// driving the Agent SDK on one is the operator's call, made when they click Connect — not
/// something this repo asserts on their behalf. A Console API key through the same paste
/// surface is the sanctioned path, which is why `classifyPasted` accepts both kinds rather
/// than steering to the OAuth flow.
let private authorizeUrl = "https://claude.ai/oauth/authorize?code=true"
let private tokenUrl = "https://console.anthropic.com/v1/oauth/token"
let private clientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e"
let private redirectUri = "https://console.anthropic.com/oauth/code/callback"
let private scopes = "org:create_api_key user:profile user:inference"

/// The broker request for one sign-in flow: Claude's endpoints as data.
///
/// `JsonEncoded` is one of those facts, not a broker default: Anthropic's token endpoint
/// answers a standards-correct form body with
/// `invalid_request_error: "Invalid request format"`, and its own clients (the Anthropic
/// SDK's `userOAuthProvider`, `claude /login`) post JSON — with `state` replayed in the
/// body, which the dialect carries.
let beginRequest (target: SecretId) : ControlWire.ConnectionBeginRequest =
    { Target = target
      AuthorizeUrl = envOr "YESSION_CLAUDE_AUTHORIZE_URL" authorizeUrl
      TokenUrl = envOr "YESSION_CLAUDE_TOKEN_URL" tokenUrl
      ClientId = envOr "YESSION_CLAUDE_CLIENT_ID" clientId
      Scopes = scopes
      RedirectUri = Some (envOr "YESSION_CLAUDE_REDIRECT_URI" redirectUri)
      TokenDialect = JsonEncoded }

/// Validate a pasted static credential: a `claude setup-token` token
/// (`sk-ant-oat01-…`) or a Console API key (`sk-ant-…`). Anything else is a paste
/// mistake worth rejecting before it is stored.
let classifyPasted (raw: string) : Result<string, string> =
    let trimmed = (defaultArg (Option.ofObj raw) "").Trim ()
    if trimmed.StartsWith "sk-ant-" then Ok trimmed
    else Error "expected a Claude credential (sk-ant-oat01-… setup token or sk-ant-… API key)"

/// The environment variable a resolved credential rides into the Agent SDK's spawned
/// CLI: brokered OAuth access tokens and setup tokens go in `CLAUDE_CODE_OAUTH_TOKEN`;
/// Console API keys in `ANTHROPIC_API_KEY`.
let envVarFor (kind: ConnectionKind) (value: string) : string * string =
    match kind with
    | OAuthConnection -> "CLAUDE_CODE_OAUTH_TOKEN", value
    | StaticConnection ->
        if value.StartsWith "sk-ant-oat" then "CLAUDE_CODE_OAUTH_TOKEN", value
        else "ANTHROPIC_API_KEY", value

/// The two sign-in scopes the panel offers: this session only, or the signing actor's
/// own scope (usable from every session that actor is signed into).
let targetFor (sessionId: SessionId) (owner: CredentialOwner) (scopeChoice: string) : Result<SecretId, string> =
    match scopeChoice with
    | "session" -> Ok { Scope = SessionScope sessionId; Name = secretName }
    | "mine" -> Ok { Scope = CredentialOwner.scope owner; Name = secretName }
    | other -> Error (sprintf "unknown scope choice '%s' (expected 'session' or 'mine')" other)

/// The per-turn credential targets, most specific first: the session's own explicit
/// credential, then the turn actor's own, then the deployment's. Mirrors secret-injection
/// precedence.
///
/// `LocalScope` is named on EVERY turn, with no test of how this deployment authenticates
/// — the session does not know and does not need to. A launch only ever sees a target in
/// the Manager's status stream if the Manager holds it readable, and an attributed launch
/// is never granted local access, so the candidate filter drops it before anything is
/// resolved. The Manager's readable set is the single authority; a second copy of that
/// judgement here could only drift from it.
let turnTargets (sessionId: SessionId) (actor: ActorRef) : SecretId list =
    [ Some { SecretId.Scope = SessionScope sessionId; SecretId.Name = secretName }
      CredentialOwner.ofActor actor
      |> Option.map (fun owner -> { SecretId.Scope = CredentialOwner.scope owner; SecretId.Name = secretName })
      Some { SecretId.Scope = LocalScope; SecretId.Name = secretName } ]
    |> List.choose id

// --- the models lookup ------------------------------------------------------------------
// The one Claude-shaped thing behind the session's provider-neutral catalogue: an endpoint,
// two header dialects, and a paged reply. `AgentModel` is what comes out, so the route, the
// synced register and the picker never learn any of it.

/// Anthropic's model listing. Overridable for the same reason the OAuth endpoints are: a
/// test needs somewhere to point it that is not the live provider.
let private modelsUrl = "https://api.anthropic.com/v1/models"

type private ModelsOutcome =
    abstract ok : bool
    abstract reason : string
    /// The provider's status, or 0 when it never answered. Kept apart from `reason` because
    /// one number decides something no prose can: whether the CREDENTIAL was refused, or
    /// this lookup merely failed.
    abstract status : int
    abstract models : {| id: string; name: string |} array

/// Why a catalogue lookup produced nothing, and the one distinction its caller acts on.
///
/// `Refused` is a fact about the credential and belongs back at the Manager; everything else
/// is a fact about this request and belongs nowhere but the picker's note.
type ModelsFailure = { Message : string; Refused : bool }

/// GET the catalogue on one credential, following the API's paging.
///
/// The credential PAIR decides the dialect, which is why this takes the same
/// `(envVar, value)` `envVarFor` produces rather than a bare string: a Console API key
/// authenticates with `x-api-key`, and an OAuth access token with a bearer header plus the
/// beta opt-in Claude Code's own client sends. One value, one rule, no guessing at the
/// shape of a secret.
///
/// The page bound is a runaway guard, not a coverage cap: the API's own maximum page is
/// 1000, so ten pages is ten thousand models and no provider is near it.
[<Emit("""(async function (envVar, value, url) {
  try {
    const headers = { 'anthropic-version': '2023-06-01' }
    if (envVar === 'ANTHROPIC_API_KEY') headers['x-api-key'] = value
    else { headers['authorization'] = 'Bearer ' + value; headers['anthropic-beta'] = 'oauth-2025-04-20' }
    const models = []
    // Not `url`: Fable names the substituted argument after its F# parameter, so a local of
    // the same name shadows it into a temporal dead zone and every lookup throws.
    let next = url + '?limit=1000'
    for (let page = 0; page < 10; page++) {
      const r = await fetch(next, { headers })
      if (!r.ok) {
        const detail = (await r.text()).slice(0, 200)
        return { ok: false, reason: 'the provider answered ' + r.status + ': ' + detail, status: r.status, models: [] }
      }
      const body = await r.json()
      for (const m of (body.data || [])) models.push({ id: String(m.id || ''), name: String(m.display_name || '') })
      if (!body.has_more || !body.last_id) break
      next = url + '?limit=1000&after_id=' + encodeURIComponent(body.last_id)
    }
    return { ok: true, reason: '', status: 200, models }
  } catch (err) {
    return { ok: false, reason: String((err && err.message) || err), status: 0, models: [] }
  }
})($0, $1, $2)""")>]
let private fetchModels (envVar: string) (value: string) (url: string) : JS.Promise<ModelsOutcome> = jsNative

/// The models one credential can see at one endpoint, as the provider-neutral pair the
/// rest of the session speaks. An id the smart constructor refuses is DROPPED rather than
/// failing the whole lookup: one malformed row in a provider's reply is not a reason to
/// leave somebody without a picker.
///
/// The endpoint is a parameter so a test can point it at a provider it wrote — which is
/// the only way the paging and the header dialects get exercised without a live account,
/// and the only way to do it without a suite writing the process environment.
let modelsAt (url: string) (credential: string * string) : Async<Result<AgentModel list, ModelsFailure>> =
    async {
        let envVar, value = credential
        let! outcome = fetchModels envVar value url |> Interop.awaitPromise
        if not outcome.ok then
            // Only 401. A 403 here is an org policy or a scope this key does not carry, both
            // of which happen to a credential that is otherwise perfectly alive, and a 5xx or
            // an unreachable host says nothing about the credential at all.
            return Error { Message = outcome.reason; Refused = outcome.status = 401 }
        else
            return
                outcome.models
                |> Array.toList
                |> List.choose (fun row ->
                    match ModelId.create row.id with
                    | Ok id -> Some (AgentModel.create id row.name)
                    | Error _ -> None)
                |> Ok
    }

/// The lookup as the session composes it: this provider's endpoint, overridable the way
/// its OAuth endpoints are.
let models (credential: string * string) : Async<Result<AgentModel list, ModelsFailure>> =
    modelsAt (envOr "YESSION_CLAUDE_MODELS_URL" modelsUrl) credential

/// A human label for a turn actor, for the "not connected" failure message.
let actorLabel (actor: ActorRef) : string =
    match actor with
    | UserRef u -> UserId.value u
    | PeerRef p -> sprintf "peer %s" (PeerId.value p)
    | ActorRef.Agent -> "the agent"
    | ActorRef.SessionProcess -> "the session process"
    // What `System` MEANS wherever this label is read: a deployment that attributes nobody,
    // acting as itself. "the system" is what the actor is called in the log; it is not what
    // a person reading "no Claude account connected for …" needs to be told.
    | ActorRef.System -> "this deployment"
    // Reached only if a fold ever asked for something needing a connection. It has none of
    // its own — see `CredentialOwner.ofActor` — so the sentence names the file, not a party.
    | ActorRef.Configured repo -> sprintf "%s's %s" (RepoRef.value repo) ConfigFile.FileName

// --- the browser-facing /claude* routes -----------------------------------------------
// Thin proxies over the Manager's broker, gated by the same cookie identity as /me.
// The browser's scope choice becomes a target; who owns it comes from the COOKIE, and the
// Manager's policy is the authority (a launch that was never granted local access, or a
// user never bound to it, is denied there).
//
// The browser asserts no identity here at all any more. It used to send its own peer id
// and have the credential owned by it — see `ownerOf`.

type private ClaudeRequestBody =
    { Scope : string
      Code : string option
      Token : string option }

let private bodyDecoder : Decoder<ClaudeRequestBody> =
    Decode.object (fun get ->
        { Scope = get.Optional.Field "scope" Decode.string |> Option.defaultValue "mine"
          Code = get.Optional.Field "code" Decode.string
          Token = get.Optional.Field "token" Decode.string })

let private readBody (req: IncomingMessage) (cont: string -> unit) =
    let mutable acc = ""
    req.on ("data", fun chunk -> acc <- acc + bufferToString chunk) |> ignore
    req.on ("end", fun _ -> cont acc) |> ignore

let private respondJson (res: ServerResponse) (status: int) (json: string) =
    res.writeHead (status, createObj [ "content-type", box "application/json"; "cache-control", box "no-store" ]) |> ignore
    res.``end`` json

let private respondText (res: ServerResponse) (status: int) (text: string) =
    res.writeHead (status, createObj [ "content-type", box "text/plain"; "cache-control", box "no-store" ]) |> ignore
    res.``end`` text

let private jsonString (raw: string) : string = Encode.toString 0 (Encode.string raw)

/// The credential owner behind a browser request: the cookie's Manager-verified user, or
/// — where this deployment attributes nobody — the deployment itself.
///
/// Total, and it asks the browser for nothing. It used to take the browser's self-asserted
/// peer id and own the credential by that; a peer id lives in origin-partitioned
/// localStorage, so it changed under the person holding it and stranded the credential
/// behind every new one. The cookie's attribution is Manager-minted and is the whole input.
let ownerOf (identity: CookieIdentity) : CredentialOwner =
    match identity.Attribution with
    | AttributedUser user -> UserOwner user
    | UnattributedAccess -> LocalOwner

/// Build the /claude* route handler. `statusOf` reads the session's live status cache
/// (fed by the Manager's connection stream); `agentAvailable` is the agent gate's own
/// truth (any relevant credential OR the ambient env) — served so the client can say
/// "no agent in this session" honestly; `connections` is the control-channel broker
/// client. Composes into `Signalling.start` extra routes.
let routes
    (sessionId: SessionId)
    (auth: SessionAuth.Auth)
    (connections: ControlClient.SessionConnections)
    (statusOf: SecretId -> ConnectionStatus option)
    (agentAvailable: unit -> bool)
    /// The path this session is served under (`""` at an origin root), stripped off the
    /// request the same way the rest of the session's surface strips it.
    (mount: string)
    : IncomingMessage -> ServerResponse -> bool =
    fun req res ->
        let routeOf () = SessionRoute.parseUnder mount req.``method`` (req.url.Split('?').[0])
        // The session's Claude routes, claimed through the same `SessionRoute` contract the
        // rest of its surface uses — so a route added there is unhandled here until this
        // match accounts for it.
        match routeOf () with
        | Some ClaudeStatus
        | Some (Claude _) ->
            match auth.IdentityOf req with
            | None -> respondText res 401 "unauthorized"
            | Some identity ->
                let handle (body: ClaudeRequestBody) : unit =
                    let owner = ownerOf identity
                    let kindLabel kind = match kind with OAuthConnection -> "oauth" | StaticConnection -> "static"
                    match routeOf () with
                    | Some ClaudeStatus ->
                        // One connection as the panel reads it: which kind of credential it
                        // is, and — when something has established that it no longer works —
                        // why a person has to sign in again. `null` for a scope with nothing
                        // connected. The GitHub panel reads the same shape from its own route;
                        // both are pinned by their route suites.
                        let statusJson (target: SecretId) =
                            match statusOf target with
                            | None -> "null"
                            | Some (status: ConnectionStatus) ->
                                let signInRequired =
                                    match status.Health with
                                    | ConnectionUsable -> "null"
                                    | SignInRequired reason -> jsonString reason
                                sprintf
                                    """{"kind":%s,"signInRequired":%s}"""
                                    (jsonString (kindLabel status.Kind))
                                    signInRequired
                        let sessionTarget : SecretId = { Scope = SessionScope sessionId; Name = secretName }
                        let mineTarget : SecretId = { Scope = CredentialOwner.scope owner; Name = secretName }
                        // What "mine" MEANS here, so the panel can say it honestly:
                        // one person's credential, or this whole deployment's.
                        let ownerLabel =
                            match owner with
                            | UserOwner _ -> "user"
                            | LocalOwner -> "local"
                        respondJson res 200
                            (sprintf """{"session":%s,"mine":%s,"owner":"%s","agent":%b}"""
                                (statusJson sessionTarget) (statusJson mineTarget) ownerLabel (agentAvailable ()))
                    | Some (Claude action) ->
                        match targetFor sessionId owner body.Scope with
                        | Error e -> respondText res 400 e
                        | Ok target ->
                            let respondOutcome (outcome: Result<string, string>) =
                                match outcome with
                                | Ok json -> respondJson res 200 json
                                | Error e -> respondText res 400 e
                            Async.StartImmediate (
                                async {
                                    match action with
                                    | ClaudeAction.Begin ->
                                        let! outcome = connections.Begin (beginRequest target)
                                        respondOutcome (
                                            outcome
                                            |> Result.map (fun r ->
                                                sprintf """{"authorizeUrl":%s,"state":%s}"""
                                                    (jsonString r.AuthorizeUrl) (jsonString r.State)))
                                    | ClaudeAction.Complete ->
                                        match body.Code with
                                        | None -> respondText res 400 "missing code"
                                        | Some code ->
                                            let! outcome = connections.Complete target code
                                            respondOutcome (outcome |> Result.map (fun () -> """{"ok":true}"""))
                                    | ClaudeAction.Token ->
                                        match body.Token |> Option.map classifyPasted with
                                        | None -> respondText res 400 "missing token"
                                        | Some (Error e) -> respondText res 400 e
                                        | Some (Ok token) ->
                                            let! outcome = connections.Put target token
                                            respondOutcome (outcome |> Result.map (fun () -> """{"ok":true}"""))
                                    | ClaudeAction.Disconnect ->
                                        let! outcome = connections.Disconnect target
                                        respondOutcome (
                                            outcome
                                            |> Result.map (fun existed ->
                                                sprintf """{"disconnected":%b}""" existed))
                                })
                    // Unreachable: this handler only runs for the two cases above.
                    | Some _
                    | None -> respondText res 404 "not found"
                match req.``method`` with
                | "GET" ->
                    handle
                        { Scope = "mine"
                          Code = None
                          Token = None }
                | _ ->
                    readBody req (fun raw ->
                        match Decode.fromString bodyDecoder (if raw.Trim () = "" then "{}" else raw) with
                        | Ok body -> handle body
                        | Error e -> respondText res 400 (sprintf "malformed request: %s" e))
            true
        // Not this handler's path: the composing server falls through (to its 404).
        | Some _
        | None -> false
