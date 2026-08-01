module Yession.Host.ClaudeConnection

// Everything Claude-specific about signing in (Plan 08) lives HERE, in the session —
// the Manager's broker is standards-only and never learns which service it brokered.
// This module owns: the Anthropic OAuth endpoints and Claude Code's public client id
// (sent to the broker as data), the reserved storage name, pasted-token
// classification, the credential→env-var mapping the Agent SDK consumes, and the
// browser-facing /claude* routes the client panel drives.

open Fable.Core.JsInterop
open Yession.Domain
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
/// credential, then the turn actor's. Mirrors secret-injection precedence.
let turnTargets (sessionId: SessionId) (actor: ActorRef) : SecretId list =
    [ Some { SecretId.Scope = SessionScope sessionId; Name = secretName }
      CredentialOwner.ofActor actor
      |> Option.map (fun owner -> { SecretId.Scope = CredentialOwner.scope owner; Name = secretName }) ]
    |> List.choose id

/// A human label for a turn actor, for the "not connected" failure message.
let actorLabel (actor: ActorRef) : string =
    match actor with
    | UserRef u -> UserId.value u
    | PeerRef p -> sprintf "peer %s" (PeerId.value p)
    | ActorRef.Agent -> "the agent"
    | ActorRef.SessionProcess -> "the session process"
    | ActorRef.System -> "the system"

// --- the browser-facing /claude* routes -----------------------------------------------
// Thin proxies over the Manager's broker, gated by the same cookie identity as /me.
// The browser's scope choice + identity become a target; the Manager's policy is the
// authority (a forged peer id outside the launch is denied there, exactly like the
// /login bounce's self-asserted peer_id).

type private ClaudeRequestBody =
    { Scope : string
      PeerId : string option
      Code : string option
      Token : string option }

let private bodyDecoder : Decoder<ClaudeRequestBody> =
    Decode.object (fun get ->
        { Scope = get.Optional.Field "scope" Decode.string |> Option.defaultValue "mine"
          PeerId = get.Optional.Field "peerId" Decode.string
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

/// The credential owner behind a browser request: the cookie's Manager-verified user,
/// or — for unattributed access — the browser's own stable peer id.
let private ownerOf (identity: CookieIdentity) (peerIdRaw: string option) : Result<CredentialOwner, string> =
    match identity.Attribution with
    | AttributedUser user -> Ok (UserOwner user)
    | UnattributedAccess ->
        match peerIdRaw with
        | Some raw -> PeerId.create raw |> Result.map PeerOwner
        | None -> Error "peer id required for an unattributed connection"

/// Build the /claude* route handler. `statusOf` reads the session's live status cache
/// (fed by the Manager's connection stream); `agentAvailable` is the agent gate's own
/// truth (any relevant credential OR the ambient env) — served so the client can say
/// "no agent in this session" honestly; `connections` is the control-channel broker
/// client. Composes into `Signalling.start` extra routes.
let routes
    (sessionId: SessionId)
    (auth: SessionAuth.Auth)
    (connections: ControlClient.SessionConnections)
    (statusOf: SecretId -> ConnectionKind option)
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
                    match ownerOf identity body.PeerId with
                    | Error e -> respondText res 400 e
                    | Ok owner ->
                        let kindLabel kind = match kind with OAuthConnection -> "oauth" | StaticConnection -> "static"
                        match routeOf () with
                        | Some ClaudeStatus ->
                            let statusJson (target: SecretId) =
                                match statusOf target with
                                | Some kind -> jsonString (kindLabel kind)
                                | None -> "null"
                            let sessionTarget : SecretId = { Scope = SessionScope sessionId; Name = secretName }
                            let mineTarget : SecretId = { Scope = CredentialOwner.scope owner; Name = secretName }
                            let ownerLabel =
                                match owner with
                                | UserOwner _ -> "user"
                                | PeerOwner _ -> "peer"
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
                          PeerId = Interop.queryParamOf req.url "peer_id"
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
