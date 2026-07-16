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
// Routes (all POST, secret in the `x-yession-control` header):
//   /control/start    EnvironmentSpec        -> StartContainerResult
//   /control/stop     ContainerHandle        -> StopContainerResult
//   /control/execute  ExecuteRequest         -> NDJSON: chunk* then exactly one result
//   /control/name     { name }               -> "ok" (updates the registry display name)

open Fable.Core.JsInterop
open Yession.Domain
open Yession.Manager
open Yession.Host.Interop

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
    (resolve: string -> SessionEnvironmentCapabilities option)
    (reportName: string -> string -> Async<Result<unit, string>>)
    (req: IncomingMessage)
    (res: ServerResponse)
    : bool =
    let path = pathnameOf req.url
    if not (path.StartsWith "/control/") then false
    else
        let secret = headerOf req "x-yession-control"
        let capabilities = secret |> Option.bind resolve
        match capabilities with
        | None -> respond res 401 "invalid control secret"
        | Some capabilities ->
            let decodeAnd (decode: string -> Result<'a, string>) (handle: 'a -> unit) =
                readBody req (fun body ->
                    match decode body with
                    | Ok value -> handle value
                    | Error e -> respond res 400 (sprintf "malformed control request: %s" e))
            match req.``method``, path with
            | "POST", "/control/start" ->
                decodeAnd (ControlWire.fromString ControlWire.environmentSpec) (fun spec ->
                    Async.StartImmediate (
                        async {
                            let! result = capabilities.StartContainer spec
                            respondJson res (ControlWire.toString ControlWire.startContainerResult result)
                        }))
            | "POST", "/control/stop" ->
                decodeAnd (ControlWire.fromString ControlWire.containerHandle) (fun handle ->
                    Async.StartImmediate (
                        async {
                            let! result = capabilities.StopContainer handle
                            respondJson res (ControlWire.toString ControlWire.stopContainerResult result)
                        }))
            | "POST", "/control/execute" ->
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
                        }))
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
            | _ -> respond res 404 "not found"
        true
