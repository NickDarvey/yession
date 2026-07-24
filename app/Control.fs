module Yession.Host.Control

// The Manager's control endpoint (Phase 4, Step 24): the environment capability
// surface for its child Session Processes, across the process boundary. Authority
// stays IN the Manager — the child authenticates each call with its per-launch secret,
// the secret resolves to the capabilities the Manager granted that launch (the RPC
// equivalent of the Step 11 closure), and every handle re-validates against the
// Manager's registry. 127.0.0.1 only. The channel carries environment and command
// traffic, plus ONE piece of session metadata — the session's self-assigned display name
// (a label, never conversation or event content) — so the Manager's list reflects the title.
//
// Routes (secret in the `x-yession-control` header):
//   POST /control/start            EnvironmentSpec  -> StartContainerResult
//   POST /control/stop             ContainerHandle  -> StopContainerResult
//   POST /control/execute          ExecuteRequest   -> NDJSON: chunk* then exactly one result
//   POST /control/name             { name }         -> "ok" (updates the registry display name)
//   POST /control/register-client  { redirectUri }  -> { clientId, clientSecret, issuer }
//   POST /control/secrets/set      { scope, name, value } -> secret metadata (never a value)
//   POST /control/secrets/list     { scope }        -> { secrets: metadata[] } (never values)
//   POST /control/secrets/delete   { scope, name }  -> { deleted }
//        (there is deliberately NO /control/secrets/get: values leave the Manager only
//         by environment injection at container start — see Plan 06)
//   GET  /control/notifications                     -> text/event-stream (the reverse leg:
//        the Manager pushing notifications DOWN to this session, multiplexed as SSE frames
//        of `ControlWire.sessionNotification` JSON — see NotificationHub / SessionNotification)
//   GET  /control/mcp                               -> text/event-stream (a second reverse leg:
//        the current MCP tool list on subscribe, then a fresh list on every change, as SSE
//        frames of `ControlWire.mcpToolList` — the standard `ListToolsResult` — see McpHub)
//
// Every launch can call the channel (its secret always resolves to a caller); whether
// the caller also holds ENVIRONMENT capabilities is a separate grant — a session
// without one still registers its OAuth client here, it just gets 403 on the
// environment routes.

open Fable.Core.JsInterop
open Yession.Domain
open Yession.Manager
open Yession.Oidc
open Yession.Host.Interop

// SSE keep-alive: a comment frame on an interval so an idle stream is not reaped by an
// HTTP idle timeout (the child also reconnects on drop, so this is belt-and-braces).
[<Fable.Core.Emit("setInterval($1, $0)")>]
let private setInterval (ms: int) (callback: unit -> unit) : obj = Fable.Core.Util.jsNative

[<Fable.Core.Emit("clearInterval($0)")>]
let private clearInterval (handle: obj) : unit = Fable.Core.Util.jsNative

/// What a control secret resolves to: WHICH launch is calling, the environment
/// capabilities that launch was granted (None = environment-less session), and the
/// users the Manager verified into the launch at ID-token issuance (empty until a
/// login completes). Manager-verified, never self-asserted — this is the ABAC
/// composite identity (Plan 06).
type ControlCaller =
    { SessionId : SessionId
      Capabilities : SessionEnvironmentCapabilities option
      Users : Set<UserSubject> }

/// A secrets-route failure: a policy Deny (403, with the policy's reason) or a store
/// failure (500). Distinct so the route arms stay thin and policy-free — only the
/// Manager can build a verified AuthzSubject, so authorization happens in its handlers.
type SecretsError =
    | SecretsDenied of reason: string
    | SecretsFailed of reason: string

/// The Manager's secrets handlers (Plan 06), pre-composed with authorization. NO
/// operation returns a secret value — set answers metadata, list metadata, delete a
/// flag; injection is the only read and it happens Manager-side at container start.
type SecretsApi =
    { Set : ControlCaller -> ControlWire.SetSecretRequest -> Async<Result<SecretMetadata, SecretsError>>
      List : ControlCaller -> ControlWire.ListSecretsRequest -> Async<Result<SecretMetadata list, SecretsError>>
      Delete : ControlCaller -> ControlWire.DeleteSecretRequest -> Async<Result<bool, SecretsError>> }

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
    (registerNotificationSink: string -> (SessionNotification -> unit) -> (unit -> unit))
    (registerMcpSink: (McpToolList -> unit) -> (unit -> unit))
    (registerClient: string -> SessionId -> string -> RegisterClientResponse)
    (secretsApi: SecretsApi option)
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
        match secret |> Option.bind resolve with
        | None ->
            onUnauthorized path
            respond res 401 "invalid control secret"
        | Some caller ->
            let decodeAnd (decode: string -> Result<'a, string>) (handle: 'a -> unit) =
                readBody req (fun body ->
                    match decode body with
                    | Ok value -> handle value
                    | Error e -> respond res 400 (sprintf "malformed control request: %s" e))
            // The environment routes need the launch's capability grant; a session
            // launched without one gets a clean 403, not a crash.
            let withCapabilities (handle: SessionEnvironmentCapabilities -> unit) =
                match caller.Capabilities with
                | Some capabilities -> handle capabilities
                | None -> respond res 403 "no environment capabilities granted"
            match req.``method``, path with
            | "POST", "/control/start" ->
                withCapabilities (fun capabilities ->
                    decodeAnd (ControlWire.fromString ControlWire.environmentSpec) (fun spec ->
                        Async.StartImmediate (
                            async {
                                let! result = capabilities.StartContainer spec
                                respondJson res (ControlWire.toString ControlWire.startContainerResult result)
                            })))
            | "POST", "/control/stop" ->
                withCapabilities (fun capabilities ->
                    decodeAnd (ControlWire.fromString ControlWire.containerHandle) (fun handle ->
                        Async.StartImmediate (
                            async {
                                let! result = capabilities.StopContainer handle
                                respondJson res (ControlWire.toString ControlWire.stopContainerResult result)
                            })))
            | "POST", "/control/execute" ->
                withCapabilities (fun capabilities ->
                    decodeAnd (ControlWire.fromString ControlWire.executeRequest) (fun request ->
                        // Chunks stream as NDJSON lines the moment they arrive; the final
                        // line is the command result. Ordering rides the response stream.
                        res.writeHead (200, createObj [ "content-type", box "application/x-ndjson"; "cache-control", box "no-store" ]) |> ignore
                        Async.StartImmediate (
                            async {
                                let! result =
                                    capabilities.Execute request.Handle request.Command (fun chunk ->
                                        res.write (ControlWire.toString ControlWire.executeLine (ControlWire.OutputLine chunk) + "\n")
                                        |> ignore)
                                res.``end`` (ControlWire.toString ControlWire.executeLine (ControlWire.ResultLine result) + "\n")
                            })))
            | "POST", "/control/name" ->
                // Session metadata, not environment authority: the secret only names WHICH
                // session is reporting; the Manager updates that session's display name.
                decodeAnd (ControlWire.fromString ControlWire.sessionNameReport) (fun name ->
                    Async.StartImmediate (
                        async {
                            match! reportName (Option.defaultValue "" secret) name with
                            | Ok () -> respond res 200 "ok"
                            | Error e -> respond res 400 e
                        }))
            | "POST", "/control/register-client" ->
                // Dynamic client registration (the OIDC RP side of this launch). The
                // secret names the registering session; the redirect URI arrives here —
                // not at spawn — because the session's port is OS-assigned and only
                // known once it listens.
                decodeAnd (Wire.fromString Wire.registerClientRequest) (fun request ->
                    let response = registerClient (Option.defaultValue "" secret) caller.SessionId request.RedirectUri
                    respondJson res (Wire.toString Wire.registerClientResponse response))
            | "POST", ("/control/secrets/set" | "/control/secrets/list" | "/control/secrets/delete" as secretsPath) ->
                // Secrets (Plan 06). The arms stay thin: decode, hand the verified
                // caller to the Manager's pre-authorized handlers, map the outcome.
                // No store configured -> a clean 403 (the withCapabilities shape);
                // a policy Deny -> 403 with its reason; a store failure -> 500.
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
                    | _ ->
                        decodeAnd (ControlWire.fromString ControlWire.deleteSecretRequest) (fun request ->
                            Async.StartImmediate (
                                async {
                                    let! outcome = api.Delete caller request
                                    respondWith
                                        (fun deleted -> ControlWire.toString ControlWire.deleteSecretResponse { Deleted = deleted })
                                        outcome
                                }))
            | "GET", "/control/notifications" ->
                // The reverse leg: a long-lived SSE stream the Manager pushes notifications
                // down. The secret already resolved to capabilities above, so it is valid —
                // register a sink that encodes each notification as one SSE `data:` frame.
                res.writeHead (200, createObj [ "content-type", box "text/event-stream"; "cache-control", box "no-store"; "connection", box "keep-alive" ]) |> ignore
                // Open the stream with a comment so the client sees headers immediately.
                res.write ": subscribed\n\n" |> ignore
                let sink (n: SessionNotification) =
                    res.write (sprintf "data: %s\n\n" (ControlWire.toString ControlWire.sessionNotification n)) |> ignore
                let unsubscribe = registerNotificationSink (Option.defaultValue "" secret) sink
                let heartbeat = setInterval 15000 (fun () -> res.write ": ping\n\n" |> ignore)
                // The subscription lives until the client disconnects: tear down both.
                req.on ("close", fun _ ->
                    clearInterval heartbeat
                    unsubscribe ()) |> ignore
            | "GET", "/control/mcp" ->
                // The MCP reverse leg: the standard tool list, streamed. The secret already
                // resolved to capabilities above, so it is valid. Registering the sink
                // immediately writes the current list (McpHub's retained snapshot); every
                // later change writes a fresh list. Headers first, so that snapshot write lands
                // after them.
                res.writeHead (200, createObj [ "content-type", box "text/event-stream"; "cache-control", box "no-store"; "connection", box "keep-alive" ]) |> ignore
                res.write ": subscribed\n\n" |> ignore
                let sink (list: McpToolList) =
                    res.write (sprintf "data: %s\n\n" (ControlWire.toString ControlWire.mcpToolList list)) |> ignore
                let unsubscribe = registerMcpSink sink
                let heartbeat = setInterval 15000 (fun () -> res.write ": ping\n\n" |> ignore)
                req.on ("close", fun _ ->
                    clearInterval heartbeat
                    unsubscribe ()) |> ignore
            | _ -> respond res 404 "not found"
        true
