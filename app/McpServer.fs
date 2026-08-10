module Yession.Host.McpServer

// Serving MCP over Streamable HTTP — the other end of `McpClient.fs`.
//
// Written because we ship a provider (Plan 16, part E) and it has to be an ordinary MCP
// server: the whole argument for declaring servers by url is that anyone's can be declared,
// so ours had better hold to the same lifecycle a stranger's does. It is also the second
// independent implementation of that lifecycle in the repo, which is what makes the client's
// tests worth anything — a client tested only against its own server proves the two agreed.
//
// Deliberately small. One POST endpoint, JSON responses, a session id, and the three methods
// a tool server needs. No GET stream (we push nothing), no resources, no prompts, no
// pagination — every one of those is machinery with no producer here, and MCP lets a server
// decline what it does not implement by simply not declaring the capability.

open System
open System.Collections.Generic
open Yession.Domain
open Yession.Host.Interop

/// What a server offers. `Invoke` is handed the CALLER's session id so a server that owns a
/// resource can bind it to whoever is asking — which is exactly what the serial provider's
/// device claim needs, and it cannot get from anywhere else.
type McpServerDef =
    { Name : string
      Version : string
      Tools : ToolDescriptor list
      Invoke : string -> ToolCall -> Async<Result<string, string>> }

type McpServerHandler =
    { /// Handle one request; `true` when it claimed the path.
      TryHandle : IncomingMessage -> ServerResponse -> bool
      /// Sessions currently established (observability, and how a test asserts that a
      /// `DELETE` really ended one).
      Sessions : unit -> string list
      /// Register what to do when a session ends — by `DELETE`, or by a caller that goes
      /// away. A server with per-session state (a device claim) releases it here rather
      /// than leaking it. A function rather than a mutable field because the handler is
      /// captured by the request closure, and a field somebody reassigned would be read by
      /// nobody.
      OnSessionEnded : (string -> unit) -> unit }

[<Fable.Core.Emit("new URL($0, 'http://local').pathname")>]
let private pathnameOf (url: string) : string = Fable.Core.Util.jsNative

let private readBody (req: IncomingMessage) (cont: string -> unit) =
    let mutable acc = ""
    req.on ("data", fun chunk -> acc <- acc + bufferToString chunk) |> ignore
    req.on ("end", fun _ -> cont acc) |> ignore

let private respond (res: ServerResponse) (status: int) (body: string) (extra: (string * obj) list) =
    let headers =
        [ "content-type", box "application/json"; "cache-control", box "no-store" ] @ extra
    res.writeHead (status, Fable.Core.JsInterop.createObj headers) |> ignore
    res.``end`` body

/// Serve one MCP server at `/mcp`.
let create (def: McpServerDef) : McpServerHandler =
    // Session ids we minted. A server MAY be stateless; being stateful is what lets a claim
    // be bound to a caller, and it is what makes a restart legible — every id we did not
    // mint gets a 404, which the spec's clients read as "it restarted" rather than as an
    // outage.
    let sessions = HashSet<string> ()
    let mutable onSessionEnded : string -> unit = ignore

    let toolJson (descriptor: ToolDescriptor) : McpTool =
        { Name = descriptor.Name
          Title = descriptor.Title
          Description = Some descriptor.Description
          InputSchema = descriptor.InputSchema }

    let result (id: int) (payload: string) = Codec.toString Codec.jsonRpcResponse (JsonRpcResult (id, payload))
    let failure (id: int) (code: int) (message: string) =
        Codec.toString Codec.jsonRpcResponse (JsonRpcFailure (Some id, code, message))

    let handle (req: IncomingMessage) (res: ServerResponse) : unit =
        let sent = headerOf req "mcp-session-id" |> Option.defaultValue ""
        readBody req (fun body ->
            // A notification carries no id and gets no reply — 202, per the spec, so a
            // client that sends `notifications/initialized` is not left waiting for a frame
            // that is never coming.
            match Codec.fromString Codec.jsonRpcRequest body with
            | Error _ ->
                if body.Contains "notifications/" then
                    res.writeHead (202, Fable.Core.JsInterop.createObj [ "content-type", box "text/plain" ])
                    |> ignore
                    res.``end`` ""
                else respond res 400 """{"error":"not a JSON-RPC request"}""" []
            | Ok request ->
                match request.Method with
                | "initialize" ->
                    let id = randomSecret ()
                    sessions.Add id |> ignore
                    let payload =
                        Codec.toString
                            Codec.mcpHandshake
                            { ProtocolVersion = McpProtocol.Version
                              ServerName = def.Name
                              ServerVersion = def.Version }
                    respond res 200 (result request.Id payload) [ "mcp-session-id", box id ]
                // Every other method needs a session we minted. One we did not is a 404,
                // which is the spec's own way of saying "I restarted" — and the client
                // already knows to re-handshake once and retry.
                | _ when sent = "" || not (sessions.Contains sent) ->
                    respond res 404 """{"error":"no such session"}""" []
                | "tools/list" ->
                    let payload =
                        Codec.toString Codec.mcpToolList { Tools = def.Tools |> List.map toolJson }
                    respond res 200 (result request.Id payload) []
                | "tools/call" ->
                    match Codec.fromString Codec.mcpCallRequest (Option.defaultValue "{}" request.Params) with
                    | Error e -> respond res 200 (failure request.Id -32602 e) []
                    | Ok (name, arguments) ->
                        Async.StartImmediate (
                            async {
                                let! outcome =
                                    def.Invoke sent { Namespace = def.Name; Name = name; Arguments = arguments }
                                match outcome with
                                // A tool that RAN and went badly is a RESULT with `isError`,
                                // not a JSON-RPC error: the model is meant to read it and
                                // choose differently. Only a call that never reached a tool
                                // is an error frame.
                                | Ok text ->
                                    let payload =
                                        Codec.toString Codec.mcpCallResult { Text = text; IsError = false }
                                    respond res 200 (result request.Id payload) []
                                | Error reason -> respond res 200 (failure request.Id -32602 reason) []
                            })
                | other -> respond res 200 (failure request.Id -32601 (sprintf "no such method: %s" other)) [])

    { TryHandle =
        fun req res ->
            if pathnameOf req.url <> "/mcp" then false
            else
                match req.``method`` with
                | "POST" ->
                    handle req res
                    true
                | "DELETE" ->
                    // The spec's session teardown, and the only orderly way a claim is
                    // released when a client is finished rather than crashed.
                    let sent = headerOf req "mcp-session-id" |> Option.defaultValue ""
                    if sessions.Remove sent then onSessionEnded sent
                    respond res 200 """{"ok":true}""" []
                    true
                | _ ->
                    // No GET stream: we push nothing, so offering one would be a long-lived
                    // connection per client with nothing to carry.
                    respond res 405 """{"error":"POST or DELETE"}""" []
                    true
      Sessions = fun () -> sessions |> List.ofSeq |> List.sort
      OnSessionEnded = fun handler -> onSessionEnded <- handler }
