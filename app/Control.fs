module Yession.Host.Control

// The Manager's control endpoint (Phase 4, Step 24): the supervision + custody surface
// for its child Session Processes, across the process boundary. The child
// authenticates each call with its per-launch secret; 127.0.0.1 only. Environments and
// commands are session-owned (the sandbox seam) and never cross this channel — it
// carries secrets custody, connections, supervision reports, and ONE piece of session
// metadata: the session's self-assigned display name (a label, never conversation or
// event content), so the Manager's list reflects the title.
//
// Routes (secret in the `x-yession-control` header):
//   POST /control/name             { name }         -> "ok" (updates the registry display name)
//   POST /control/register-client  { redirectUri }  -> { clientId, clientSecret, issuer }
//   POST /control/secrets/set      { scope, name, value } -> secret metadata (never a value)
//   POST /control/secrets/list     { scope }        -> { secrets: metadata[] } (never values)
//   POST /control/secrets/delete   { scope, name }  -> { deleted }
//   POST /control/secrets/resolve  { name }         -> { value }
//        (the one value-returning SECRETS route: a session resolves the values its
//         sandbox spec references at sandbox spawn — gated by the caller's readable
//         scopes, the same walk Manager-side injection always used. The value crosses
//         only this authenticated loopback channel, only at sandbox spawn, and never
//         reaches the agent loop — there is still no agent-facing read capability.)
//   GET  /control/notifications                     -> text/event-stream (the reverse leg:
//        the Manager pushing notifications DOWN to this session, multiplexed as SSE frames
//        of `ControlWire.sessionNotification` JSON — see NotificationHub / SessionNotification)
//   GET  /control/mcp                               -> text/event-stream (a second reverse leg:
//        THIS session's resolved MCP server set on subscribe, then a fresh whole set on
//        every change, as SSE frames of `Codec.mcpServerSet` (Plan 17). The Manager says
//        WHERE the servers are; the session is the MCP client that talks to them.)
//   POST /control/connections/begin      ConnectionBeginRequest -> { authorizeUrl, state }
//   POST /control/connections/complete   { target, code }       -> "ok" (manual paste completion)
//   POST /control/connections/put        { target, value }      -> "ok" (static token)
//   POST /control/connections/put-grant  { target, accessToken, … } -> "ok" (refreshable)
//   POST /control/connections/disconnect { target }             -> { disconnected }
//   POST /control/connections/reject     { target, reason }     -> { recorded }
//   POST /control/connections/resolve    { target }             -> { kind, value }
//        (the ONE value-returning route (Plan 08): an agent turn needs the token
//         in-process; policy gates it to targets whose scope the caller is bound to)
//   POST /control/hooks/subscribe    { filter }     -> { id }
//   POST /control/hooks/unsubscribe  { id }         -> { dropped }
//        (the hook relay: what this session wants forwarded from the Manager's hook
//         endpoints. A filter is DATA — a conjunction of equalities over paths — because
//         code would make the Manager a version ceiling on the sessions it supervises.)
//   GET  /control/connections                       -> text/event-stream (a third reverse leg:
//        the caller's readable connection statuses on subscribe, then a fresh list on every
//        change — metadata frames of `ControlWire.connectionStatusList`, never values)
//
// Every launch can call the channel: its secret resolves to WHICH session is calling,
// and the secrets/connections handlers apply their own policy per call.

open Fable.Core.JsInterop
open Yession.Domain
open Yession.Domain.Tools
open Yession.Domain.Access
open Yession.Domain.Hooks
open Yession.Manager
open Yession.Oidc
open Yession.Host.Interop

/// What a control secret resolves to: WHICH launch is calling, and the users and peers
/// the Manager verified into the launch at ID-token issuance (empty until a login
/// completes). Manager-verified, never self-asserted — this is the ABAC composite
/// identity (Plan 06; peers per Plan 07).
type ControlCaller =
    { SessionId : SessionId
      Users : Set<UserId>
      Peers : Set<PeerId>
      /// Was any login into this launch UNATTRIBUTED — the strategy naming a subject with
      /// no user behind it (`--auth localhost`)? What makes `LocalScope` readable, and
      /// false under every attributed strategy.
      Local : bool }

/// A secrets-route failure: a policy Deny (403, with the policy's reason) or a store
/// failure (500). Distinct so the route arms stay thin and policy-free — only the
/// Manager can build a verified Subject, so authorization happens in its handlers.
type SecretsError =
    | SecretsDenied of reason: string
    | SecretsFailed of reason: string

/// The Manager's secrets handlers (Plan 06), pre-composed with authorization. `Resolve`
/// is the one operation that returns a value: it feeds env injection at the SESSION'S
/// sandbox spawn, gated by the caller's readable scopes (the same precedence walk
/// Manager-side injection always used). Set answers metadata, list metadata, delete a
/// flag — the agent-facing surface still has no read.
type SecretsApi =
    { Set : ControlCaller -> ControlWire.SetSecretRequest -> Async<Result<SecretMetadata, SecretsError>>
      List : ControlCaller -> ControlWire.ListSecretsRequest -> Async<Result<SecretMetadata list, SecretsError>>
      Delete : ControlCaller -> ControlWire.DeleteSecretRequest -> Async<Result<bool, SecretsError>>
      Resolve : ControlCaller -> ControlWire.ResolveSecretRequest -> Async<Result<string, SecretsError>> }

/// The Manager's connection-broker handlers (Plan 08), pre-composed with authorization
/// (`ConnectionAction` × the request's target scope). `Resolve` is the one operation in
/// the whole control surface that returns a secret value; `Status` serves the SSE leg's
/// snapshot and can never carry one.
type ConnectionsApi =
    { Begin : ControlCaller -> ControlWire.ConnectionBeginRequest -> Async<Result<ControlWire.ConnectionBeginResponse, SecretsError>>
      Complete : ControlCaller -> ControlWire.ConnectionCompleteRequest -> Async<Result<unit, SecretsError>>
      Put : ControlCaller -> ControlWire.ConnectionPutRequest -> Async<Result<unit, SecretsError>>
      PutGrant : ControlCaller -> ControlWire.ConnectionPutGrantRequest -> Async<Result<unit, SecretsError>>
      Disconnect : ControlCaller -> ControlWire.ConnectionDisconnectRequest -> Async<Result<ControlWire.ConnectionDisconnectResponse, SecretsError>>
      Reject : ControlCaller -> ControlWire.ConnectionRejectRequest -> Async<Result<ControlWire.ConnectionRejectResponse, SecretsError>>
      Resolve : ControlCaller -> ControlWire.ConnectionResolveRequest -> Async<Result<ControlWire.ConnectionResolveResponse, SecretsError>>
      Status : ControlCaller -> Async<ConnectionStatusList> }

let private readBody (req: IncomingMessage) (cont: string -> unit) =
    let mutable acc = ""
    req.on ("data", fun chunk -> acc <- acc + bufferToString chunk) |> ignore
    req.on ("end", fun _ -> cont acc) |> ignore

[<Fable.Core.Emit("new URL($0, 'http://local').pathname")>]
let private pathnameOf (url: string) : string = Fable.Core.Util.jsNative

let private respondJson (res: ServerResponse) (json: string) =
    res.writeHead (200, createObj [ "content-type", box "application/json"; "cache-control", box "no-store" ]) |> ignore
    res.``end`` json

let private respond (res: ServerResponse) (status: int) (text: string) =
    res.writeHead (status, createObj [ "content-type", box "text/plain"; "cache-control", box "no-store" ]) |> ignore
    res.``end`` text

/// Handle a control request. Returns false when the path is not a control route, so a
/// composing HTTP server (the management UI shares the port) falls through.
let tryHandle
    (resolve: string -> ControlCaller option)
    (reportName: string -> string -> Async<Result<unit, string>>)
    // Plan 11: the session's own report of whether it is in use, keyed by the per-launch
    // secret exactly like the name report — so the report dies with the launch it describes.
    (reportActivity: string -> bool -> Async<Result<unit, string>>)
    (subscribeNotifications: string -> Subscribe<SessionNotification>)
    // Plan 17: keyed by session (what it resolves to) AND by launch secret (whose sink it
    // is), because the retained set outlives a launch and the sink must not.
    (subscribeMcp: SessionId -> string -> Subscribe<McpServerSet>)
    (registerClient: string -> SessionId -> string -> RegisterClientResponse)
    (secretsApi: SecretsApi option)
    (connectionsApi: ConnectionsApi option)
    (subscribeConnections: string -> Subscribe<ConnectionStatusList>)
    // The hook relay: a session declares a filter over the deliveries it wants forwarded
    // from the Manager's hook endpoints, keyed by its launch secret so the declaration dies
    // with the launch. The Manager verifies a delivery's signature and matches these
    // filters; it never reads one — see `WebhookRelay`.
    (subscribeHook: string -> DeliveryFilter -> string)
    (unsubscribeHook: string -> string -> bool)
    // Audit hook (Plan 06 telemetry): called with the request path whenever a control
    // secret fails to resolve — the one place the path and the failure meet.
    (onUnauthorized: string -> unit)
    (req: IncomingMessage)
    (res: ServerResponse)
    : bool =
    let path = pathnameOf req.url
    if not (path.StartsWith "/control/") then false
    else
        let secret = headerOf req "x-yession-control"
        // Resolve carries the secret STRING out beside the caller, so the branch below holds a
        // `string`, not a `string option`. The old shape kept the option and wrote
        // `Option.defaultValue "" secret` at each keyed handler — a default that could never
        // fire here (a `None` secret cannot resolve to a caller) but read as though a missing
        // secret were an empty one, and would have fed that empty launch key to the privileged
        // handlers the moment this gate's invariant drifted.
        let resolved =
            match secret with
            | Some launchSecret ->
                match resolve launchSecret with
                | Some caller -> Some (launchSecret, caller)
                | None -> None
            | None -> None
        match resolved with
        | None ->
            onUnauthorized path
            respond res 401 "invalid control secret"
        | Some (secret, caller) ->
            let decodeAnd (decode: string -> Result<'a, string>) (handle: 'a -> unit) =
                readBody req (fun body ->
                    match decode body with
                    | Ok value -> handle value
                    | Error e -> respond res 400 (sprintf "malformed control request: %s" e))
            match req.``method``, path with
            | "POST", "/control/name" ->
                // Session metadata, not environment authority: the secret only names WHICH
                // session is reporting; the Manager updates that session's display name.
                decodeAnd (ControlWire.fromString ControlWire.sessionNameReport) (fun name ->
                    Async.StartImmediate (
                        async {
                            match! reportName secret name with
                            | Ok () -> respond res 200 "ok"
                            | Error e -> respond res 400 e
                        }))
            | "POST", "/control/activity" ->
                // Plan 11. Same shape as the name report — the secret names the session, the
                // body is one fact about it — and the same discipline: no session content
                // crosses the control channel, only supervision traffic.
                decodeAnd (ControlWire.fromString ControlWire.sessionActivityReport) (fun busy ->
                    Async.StartImmediate (
                        async {
                            match! reportActivity secret busy with
                            | Ok () -> respond res 200 "ok"
                            | Error e -> respond res 400 e
                        }))
            | "POST", "/control/hooks/subscribe" ->
                // A filter, not a predicate: the Manager stores what the session said and
                // compares, never interprets. Taking the session's word here is deliberate —
                // it is a child this Manager spawned, calling over the authenticated
                // channel, and it already holds whatever credential it would act on.
                decodeAnd (ControlWire.fromString ControlWire.subscribeHookRequest) (fun request ->
                    let id = subscribeHook secret request.Filter
                    respondJson
                        res
                        (ControlWire.toString ControlWire.subscribeHookResponse { ControlWire.SubscribeHookResponse.Id = id }))
            | "POST", "/control/hooks/unsubscribe" ->
                decodeAnd (ControlWire.fromString ControlWire.unsubscribeHookRequest) (fun request ->
                    let dropped = unsubscribeHook secret request.Id
                    respondJson
                        res
                        (ControlWire.toString ControlWire.unsubscribeHookResponse { Dropped = dropped }))
            | "POST", "/control/register-client" ->
                // Dynamic client registration (the OIDC RP side of this launch). The
                // secret names the registering session; the redirect URI arrives here —
                // not at spawn — because the session's port is OS-assigned and only
                // known once it listens.
                decodeAnd (Wire.fromString Wire.registerClientRequest) (fun request ->
                    let response = registerClient secret caller.SessionId request.RedirectUri
                    respondJson res (Wire.toString Wire.registerClientResponse response))
            | "POST", ("/control/secrets/set" | "/control/secrets/list" | "/control/secrets/delete" | "/control/secrets/resolve" as secretsPath) ->
                // Secrets (Plan 06). The arms stay thin: decode, hand the verified
                // caller to the Manager's pre-authorized handlers, map the outcome.
                // No store configured -> a clean 403; a policy Deny -> 403 with its
                // reason; a store failure -> 500.
                match secretsApi with
                | None -> respond res 403 "no secrets store configured"
                | Some api ->
                    let respondWith (encode: 'ok -> string) (outcome: Result<'ok, SecretsError>) =
                        match outcome with
                        | Ok value -> respondJson res (encode value)
                        | Error (SecretsDenied reason) -> respond res 403 reason
                        | Error (SecretsFailed reason) -> respond res 500 reason
                    match secretsPath with
                    | "/control/secrets/set" ->
                        decodeAnd (ControlWire.fromString ControlWire.setSecretRequest) (fun request ->
                            Async.StartImmediate (
                                async {
                                    let! outcome = api.Set caller request
                                    respondWith (ControlWire.toString ControlWire.secretMetadata) outcome
                                }))
                    | "/control/secrets/list" ->
                        decodeAnd (ControlWire.fromString ControlWire.listSecretsRequest) (fun request ->
                            Async.StartImmediate (
                                async {
                                    let! outcome = api.List caller request
                                    respondWith
                                        (fun secretsList ->
                                            ControlWire.toString ControlWire.listSecretsResponse { Secrets = secretsList })
                                        outcome
                                }))
                    | "/control/secrets/resolve" ->
                        decodeAnd (ControlWire.fromString ControlWire.resolveSecretRequest) (fun request ->
                            Async.StartImmediate (
                                async {
                                    let! outcome = api.Resolve caller request
                                    respondWith
                                        (fun value ->
                                            ControlWire.toString ControlWire.resolveSecretResponse { Value = value })
                                        outcome
                                }))
                    | _ ->
                        decodeAnd (ControlWire.fromString ControlWire.deleteSecretRequest) (fun request ->
                            Async.StartImmediate (
                                async {
                                    let! outcome = api.Delete caller request
                                    respondWith
                                        (fun deleted -> ControlWire.toString ControlWire.deleteSecretResponse { Deleted = deleted })
                                        outcome
                                }))
            | "POST", ("/control/connections/begin" | "/control/connections/complete" | "/control/connections/put" | "/control/connections/put-grant" | "/control/connections/disconnect" | "/control/connections/reject" | "/control/connections/resolve" as connectionsPath) ->
                // Connections (Plan 08). Same thin-arm shape as secrets: decode, hand the
                // verified caller to the pre-authorized handlers, map the outcome.
                match connectionsApi with
                | None -> respond res 403 "no secrets store configured"
                | Some api ->
                    let respondWith (encode: 'ok -> string) (outcome: Result<'ok, SecretsError>) =
                        match outcome with
                        | Ok value -> respondJson res (encode value)
                        | Error (SecretsDenied reason) -> respond res 403 reason
                        | Error (SecretsFailed reason) -> respond res 500 reason
                    match connectionsPath with
                    | "/control/connections/begin" ->
                        decodeAnd (ControlWire.fromString ControlWire.connectionBeginRequest) (fun request ->
                            Async.StartImmediate (
                                async {
                                    let! outcome = api.Begin caller request
                                    respondWith (ControlWire.toString ControlWire.connectionBeginResponse) outcome
                                }))
                    | "/control/connections/complete" ->
                        decodeAnd (ControlWire.fromString ControlWire.connectionCompleteRequest) (fun request ->
                            Async.StartImmediate (
                                async {
                                    let! outcome = api.Complete caller request
                                    respondWith (fun () -> "\"ok\"") outcome
                                }))
                    | "/control/connections/put" ->
                        decodeAnd (ControlWire.fromString ControlWire.connectionPutRequest) (fun request ->
                            Async.StartImmediate (
                                async {
                                    let! outcome = api.Put caller request
                                    respondWith (fun () -> "\"ok\"") outcome
                                }))
                    | "/control/connections/put-grant" ->
                        decodeAnd (ControlWire.fromString ControlWire.connectionPutGrantRequest) (fun request ->
                            Async.StartImmediate (
                                async {
                                    let! outcome = api.PutGrant caller request
                                    respondWith (fun () -> "\"ok\"") outcome
                                }))
                    | "/control/connections/disconnect" ->
                        decodeAnd (ControlWire.fromString ControlWire.connectionDisconnectRequest) (fun request ->
                            Async.StartImmediate (
                                async {
                                    let! outcome = api.Disconnect caller request
                                    respondWith (ControlWire.toString ControlWire.connectionDisconnectResponse) outcome
                                }))
                    | "/control/connections/reject" ->
                        decodeAnd (ControlWire.fromString ControlWire.connectionRejectRequest) (fun request ->
                            Async.StartImmediate (
                                async {
                                    let! outcome = api.Reject caller request
                                    respondWith (ControlWire.toString ControlWire.connectionRejectResponse) outcome
                                }))
                    | _ ->
                        decodeAnd (ControlWire.fromString ControlWire.connectionResolveRequest) (fun request ->
                            Async.StartImmediate (
                                async {
                                    let! outcome = api.Resolve caller request
                                    respondWith (ControlWire.toString ControlWire.connectionResolveResponse) outcome
                                }))
            | "GET", "/control/connections" ->
                // The connections reverse leg: the caller's current readable statuses as
                // the first frame (so a subscriber needs no separate snapshot call), then
                // a fresh list whenever one changes. Metadata only, never values.
                match connectionsApi with
                | None -> respond res 403 "no secrets store configured"
                | Some api ->
                    // The snapshot is awaited, so it cannot ride the subscription: `stream` hands
                    // back its sink and this route writes the first frame itself.
                    let sink =
                        Sse.stream req res
                            (ControlWire.toString ControlWire.connectionStatusList)
                            (subscribeConnections secret)
                    Async.StartImmediate (
                        async {
                            let! snapshot = api.Status caller
                            sink snapshot
                        })
            | "GET", "/control/notifications" ->
                // The reverse leg: a long-lived SSE stream the Manager pushes notifications
                // down. The secret already resolved to capabilities above, so it is valid.
                Sse.stream req res
                    (ControlWire.toString ControlWire.sessionNotification)
                    (subscribeNotifications secret)
                |> ignore
            | "GET", "/control/mcp" ->
                // The MCP reverse leg (Plan 17): the servers THIS session may reach, resolved
                // by the Manager and streamed whole. The secret already resolved to a caller
                // above, so both the session and the launch are known. Subscribing writes the
                // current set at once (the hub's retained snapshot); every later change writes
                // a fresh whole set, so a reconnect is the entire recovery protocol.
                //
                // With no attach step this is the ONLY way a session learns a server exists.
                // Reactivity is the feature, not a refinement of one.
                Sse.stream req res
                    (ControlWire.toString Codec.mcpServerSet)
                    (subscribeMcp caller.SessionId secret)
                |> ignore
            | _ -> respond res 404 "not found"
        true
