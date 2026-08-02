module Yession.Host.ControlClient

// The Session Process side of the control RPC (Phase 4, Step 24): the supervision,
// secrets, and connections calls to the Manager's control endpoint, authenticated by
// this launch's secret. Failures are values — a transport error degrades to a failed
// result shape, never an exception, because the Session Process turns them into
// events.

open Fable.Core
open Yession.Domain
open Yession.Manager
open Yession.Oidc

[<Emit("""fetch($0, { method: 'POST', headers: { 'x-yession-control': $1, 'content-type': 'application/json' }, body: $2 })
  .then(r => { if (!r.ok) throw new Error('control rpc failed: ' + r.status); return r.text() })""")>]
let private postJson (url: string) (secret: string) (body: string) : JS.Promise<string> = jsNative

/// The control-leg case of `Sse.subscribe`: the per-launch secret is the whole authentication.
let private openEventStream (url: string) (secret: string) (onData: Sink<string>) : Subscription =
    Sse.subscribe url [ "x-yession-control", secret ] onData

/// Turn a typed sink into a frame sink: decode each frame with `codec`, and log-and-skip a
/// malformed one — a bad frame is never fatal to the stream, because the next one may be fine and
/// the stream is the only way the Manager can reach us. `what` names the stream in that log line.
let private decoding (what: string) (codec: Codec<'a>) (sink: Sink<'a>) : Sink<string> =
    fun data ->
        match ControlWire.fromString codec data with
        | Ok value -> sink value
        | Error e -> eprintfn "%s decode failed: %s" what e

/// Subscribe to the Manager's notification stream. Each decoded `SessionNotification` is
/// handed to `onNotification`; a malformed frame is logged and skipped (never fatal to the
/// stream). Returns a cancel that stops the subscription and closes the connection.
let subscribeNotifications (baseUrl: string) (secret: string) (onNotification: Sink<SessionNotification>) : Subscription =
    openEventStream
        (sprintf "%s/control/notifications" baseUrl)
        secret
        (decoding "notification" ControlWire.sessionNotification onNotification)

/// Subscribe to the Manager's MCP tool stream. The current list arrives immediately, then a
/// fresh `McpToolList` on every change; each is handed to `onList`. A malformed frame is
/// logged and skipped. Returns a cancel that stops the subscription and closes the connection.
let subscribeMcp (baseUrl: string) (secret: string) (onList: Sink<McpToolList>) : Subscription =
    openEventStream
        (sprintf "%s/control/mcp" baseUrl)
        secret
        (decoding "mcp list" ControlWire.mcpToolList onList)

/// Subscribe to the Manager's session registry stream (`/sessions/stream`,
/// docs/plans/09) — the management surface, not a control leg, so no secret rides the
/// request; `headers` carry whatever the Manager's authentication strategy wants (none
/// under `localhost`, an `x-yession-user` assertion under `trusted-headers`). The
/// current Running set arrives immediately, then a fresh full frame on every change;
/// what an operator's serving binding (and the Ports-tier tests) consume. Returns a
/// cancel that stops the subscription and closes the connection.
let subscribeSessionsAs (headers: (string * string) list) (baseUrl: string) (onFrame: Sink<ControlWire.SessionRegistryFrame>) : Subscription =
    Sse.subscribe
        (sprintf "%s/sessions/stream" baseUrl)
        headers
        (decoding "session registry" ControlWire.sessionRegistryFrame onFrame)

/// `subscribeSessionsAs` with no headers — the `localhost`-strategy case.
let subscribeSessions (baseUrl: string) (onFrame: Sink<ControlWire.SessionRegistryFrame>) : Subscription =
    subscribeSessionsAs [] baseUrl onFrame

/// Report the session's display name (its collaborative title) to the Manager, so the
/// session list reflects it. Best-effort: a transport failure is swallowed — a title that
/// fails to reach the list is cosmetic, never a reason to disturb the session.
let nameReporter (baseUrl: string) (secret: string) : string -> Async<unit> =
    fun name ->
        async {
            try
                let! _ =
                    postJson
                        (sprintf "%s/control/name" baseUrl)
                        secret
                        (ControlWire.toString ControlWire.sessionNameReport name)
                    |> Interop.awaitPromise
                return ()
            with _ -> return ()
        }

[<Emit("""fetch($0, { method: 'POST', headers: { 'x-yession-control': $1, 'content-type': 'application/json' }, body: $2 })
  .then(async r => ({ status: r.status, body: await r.text() }))""")>]
let private postJsonReply (url: string) (secret: string) (body: string) : JS.Promise<{| status: int; body: string |}> = jsNative

/// Report whether this session is in use (Plan 11).
///
/// Deliberately NOT `nameReporter`'s error handling, and it uses the reply-carrying post
/// for the same reason. A title that fails to arrive is cosmetic; a beat that fails to
/// arrive gets this session STOPPED once the Manager's idle window elapses. That outcome is
/// correct — a session which cannot reach its supervisor is already unsupervised, and the
/// repeat-while-busy cadence means no single delivery has to succeed — but it must never be
/// SILENT, or the reap minutes later has no visible cause.
let activityReporter (baseUrl: string) (secret: string) : bool -> Async<unit> =
    fun busy ->
        async {
            try
                let! reply =
                    postJsonReply
                        (sprintf "%s/control/activity" baseUrl)
                        secret
                        (ControlWire.toString ControlWire.sessionActivityReport busy)
                    |> Interop.awaitPromise
                if reply.status <> 200 then
                    eprintfn
                        "[activity] the manager refused an activity report (HTTP %d: %s) — this session will be stopped when its idle window elapses"
                        reply.status
                        (reply.body.Trim ())
            with e ->
                eprintfn
                    "[activity] could not reach the manager to report activity (%s) — this session will be stopped when its idle window elapses"
                    e.Message
        }

/// The session's secrets capability (Plan 06), pre-bound to ITS OWN session scope —
/// "capabilities are scoped, not ambient": this surface cannot even express another
/// scope. Write/list/delete only; there is no read — a stored secret is USED by
/// referencing its name in an EnvironmentSpec env var (SecretRef), resolved at sandbox
/// spawn straight into the sandbox env (`resolveSecret` below, which is
/// session-internal and never surfaces as an agent tool). Failures (including policy
/// 403s, with the Manager's reason) are values, never exceptions.
type SessionSecretsCapabilities =
    { SetSecret : SecretName -> string -> Async<Result<SecretMetadata, string>>
      ListSecrets : unit -> Async<Result<SecretMetadata list, string>>
      DeleteSecret : SecretName -> Async<Result<bool, string>> }

let secretsCapabilities (baseUrl: string) (secret: string) (sessionId: SessionId) : SessionSecretsCapabilities =
    let scope = SessionScope sessionId
    let post (route: string) (body: string) (decode: string -> Result<'a, string>) : Async<Result<'a, string>> =
        async {
            try
                let! reply = postJsonReply (sprintf "%s/control/secrets/%s" baseUrl route) secret body |> Interop.awaitPromise
                if reply.status = 200 then return decode reply.body
                else return Error (sprintf "secrets %s refused (%d): %s" route reply.status reply.body)
            with e ->
                return Error (sprintf "control unreachable: %s" e.Message)
        }
    { SetSecret =
        fun name value ->
            post "set"
                (ControlWire.toString ControlWire.setSecretRequest { Scope = scope; Name = name; Value = value })
                (ControlWire.fromString ControlWire.secretMetadata)
      ListSecrets =
        fun () ->
            post "list"
                (ControlWire.toString ControlWire.listSecretsRequest { Scope = scope })
                (ControlWire.fromString ControlWire.listSecretsResponse >> Result.map (fun r -> r.Secrets))
      DeleteSecret =
        fun name ->
            post "delete"
                (ControlWire.toString ControlWire.deleteSecretRequest { Scope = scope; Name = name })
                (ControlWire.fromString ControlWire.deleteSecretResponse >> Result.map (fun r -> r.Deleted)) }

/// Resolve one referenced secret value at sandbox spawn — the one value-returning
/// secrets call. Session-internal: the value goes into the sandbox's policy env and is
/// dropped; it never reaches the agent loop (no agent tool wraps this).
let resolveSecret (baseUrl: string) (secret: string) : SecretName -> Async<Result<string, string>> =
    fun name ->
        async {
            try
                let! reply =
                    postJsonReply
                        (sprintf "%s/control/secrets/resolve" baseUrl)
                        secret
                        (ControlWire.toString ControlWire.resolveSecretRequest { Name = name })
                    |> Interop.awaitPromise
                if reply.status = 200 then
                    return
                        ControlWire.fromString ControlWire.resolveSecretResponse reply.body
                        |> Result.map (fun r -> r.Value)
                else return Error (sprintf "secrets resolve refused (%d): %s" reply.status reply.body)
            with e ->
                return Error (sprintf "control unreachable: %s" e.Message)
        }

/// The session side of the Manager's connection broker (Plan 08). Service-agnostic like
/// the wire: callers supply targets and provider endpoints as data. `Resolve` is the one
/// value-returning call — an agent turn fetches the credential it runs on. Failures
/// (including policy 403s, with the Manager's reason) are values, never exceptions.
type SessionConnections =
    { Begin : ControlWire.ConnectionBeginRequest -> Async<Result<ControlWire.ConnectionBeginResponse, string>>
      Complete : SecretId -> string -> Async<Result<unit, string>>
      Put : SecretId -> string -> Async<Result<unit, string>>
      Disconnect : SecretId -> Async<Result<bool, string>>
      Resolve : SecretId -> Async<Result<ConnectionKind * string, string>> }

let connections (baseUrl: string) (secret: string) : SessionConnections =
    let post (route: string) (body: string) (decode: string -> Result<'a, string>) : Async<Result<'a, string>> =
        async {
            try
                let! reply = postJsonReply (sprintf "%s/control/connections/%s" baseUrl route) secret body |> Interop.awaitPromise
                if reply.status = 200 then return decode reply.body
                else return Error (sprintf "connections %s refused (%d): %s" route reply.status reply.body)
            with e ->
                return Error (sprintf "control unreachable: %s" e.Message)
        }
    { Begin =
        fun request ->
            post "begin"
                (ControlWire.toString ControlWire.connectionBeginRequest request)
                (ControlWire.fromString ControlWire.connectionBeginResponse)
      Complete =
        fun target code ->
            post "complete"
                (ControlWire.toString ControlWire.connectionCompleteRequest { Target = target; Code = code })
                (fun _ -> Ok ())
      Put =
        fun target value ->
            post "put"
                (ControlWire.toString ControlWire.connectionPutRequest { Target = target; Value = value })
                (fun _ -> Ok ())
      Disconnect =
        fun target ->
            post "disconnect"
                (ControlWire.toString ControlWire.connectionDisconnectRequest { Target = target })
                (ControlWire.fromString ControlWire.connectionDisconnectResponse >> Result.map (fun r -> r.Disconnected))
      Resolve =
        fun target ->
            post "resolve"
                (ControlWire.toString ControlWire.connectionResolveRequest { Target = target })
                (ControlWire.fromString ControlWire.connectionResolveResponse >> Result.map (fun r -> r.Kind, r.Value)) }

/// Subscribe to the Manager's connection-status stream (the third reverse leg). The
/// caller's current readable statuses arrive immediately, then a fresh list on every
/// change; metadata only, never values. A malformed frame is logged and skipped.
/// Returns a cancel that stops the subscription and closes the connection.
let subscribeConnections (baseUrl: string) (secret: string) (onList: Sink<ConnectionStatusList>) : Subscription =
    openEventStream
        (sprintf "%s/control/connections" baseUrl)
        secret
        (decoding "connection status" ControlWire.connectionStatusList onList)

/// Dynamic client registration: bind this launch's OAuth client to its control secret.
/// Called after the session's server listens — the redirect URI needs the OS-assigned
/// port. NOT best-effort: a session that cannot register cannot authorize users, so the
/// failure is returned for the caller to treat as fatal.
let registerClient (baseUrl: string) (secret: string) (redirectUri: string) : Async<Result<RegisterClientResponse, string>> =
    async {
        try
            let! text =
                postJson
                    (sprintf "%s/control/register-client" baseUrl)
                    secret
                    (Wire.toString Wire.registerClientRequest { RedirectUri = redirectUri })
                |> Interop.awaitPromise
            return Wire.fromString Wire.registerClientResponse text
        with e ->
            return Error (sprintf "control unreachable: %s" e.Message)
    }

