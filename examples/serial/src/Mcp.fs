module SerialProvider.Mcp

// Serving MCP over Streamable HTTP.
//
// Deliberately small, and deliberately hand-written. One POST endpoint, JSON responses, a
// session id, and the three methods a tool server needs. No GET stream (this server pushes
// nothing), no resources, no prompts, no pagination — every one of those is machinery with no
// producer here, and MCP lets a server decline what it does not implement by simply not
// declaring the capability.
//
// If you are writing your own provider, this file is the part you could replace with an SDK.
// It is written out longhand because the whole claim of the example is that a provider is an
// ordinary HTTP server: ~150 lines, no framework, and the protocol is legible on the page.

open System
open System.Collections.Generic
open Fable.Core

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

open SerialProvider.Interop

/// One tool, as MCP declares it. `InputSchema` is raw JSON Schema text, passed through
/// untouched — a schema is a document, and re-encoding it through a typed model would only
/// create ways to lose parts of it.
///
/// Qualified because these are MCP's own field names, so anything else in scope that models
/// the same spec carries them too, and a bare construction would resolve to whichever type
/// was declared last with nothing said.
[<RequireQualifiedAccess>]
type Tool =
    { Name : string
      Title : string option
      Description : string
      InputSchema : string }

/// One `tools/call`, as it arrives. `Arguments` is the raw JSON object text, for the same
/// reason: the tool that owns the schema is the only thing that can read it properly.
type ToolCall =
    { Name : string
      Arguments : string }

/// What a tool answered. Two audiences, and they are not the same one.
///
/// `Text` is prose, because a model is what reads it. `Meta` is the result's `_meta` — the
/// place MCP reserves for data meant for the CLIENT — and it is how this server hands over a
/// stream a tool call cannot carry: the client dials it, the model never has to repeat a url
/// back, and a client that does not know the key ignores it and still gets the prose.
type ToolReply =
    { Text : string
      /// A JSON object, as text, or none. Raw because the keys in it belong to whoever reads
      /// them; this type is the courier, not the reader.
      Meta : string option }

module ToolReply =

    let text (value: string) : ToolReply = { Text = value; Meta = None }

/// What a server offers. `Invoke` is handed the CALLER's session id, so a server that owns a
/// resource can bind it to whoever is asking — which is exactly what this provider's device
/// claim needs, and cannot get from anywhere else.
type ServerDef =
    { Name : string
      Version : string
      Tools : Tool list
      Invoke : string -> ToolCall -> Async<Result<ToolReply, string>> }

type ServerHandler =
    { /// Handle one request; `true` when it claimed the path.
      TryHandle : IncomingMessage -> ServerResponse -> bool
      /// Sessions currently established — observability, and how a test asserts that a
      /// `DELETE` really ended one.
      Sessions : unit -> string list
      /// Register what to do when a session ends: by `DELETE`, or by a caller that goes
      /// away. A server with per-session state (a device claim) releases it here rather than
      /// leaking it.
      OnSessionEnded : (string -> unit) -> unit }

/// The revision spoken on `initialize`.
let ProtocolVersion = "2025-06-18"

/// JSON Schema for the common case: a flat object of typed, described fields. Enough for
/// every tool here, and small enough to read — a provider whose arguments need more than
/// this can write the schema out by hand, since `Tool.InputSchema` is raw text either way.
module Schema =

    type Field =
        { Name : string
          /// A JSON Schema primitive: "string", "integer", "number", "boolean".
          Type : string
          Description : string
          Required : bool }

    let required name ``type`` description : Field =
        { Name = name; Type = ``type``; Description = description; Required = true }

    let optional name ``type`` description : Field =
        { Name = name; Type = ``type``; Description = description; Required = false }

    /// `additionalProperties: false` on purpose: a model that invents an argument should be
    /// told, not silently ignored, because a silently-dropped argument shows up as the tool
    /// doing something subtly other than what was asked.
    let ofFields (fields: Field list) : string =
        Encode.object
            [ "type", Encode.string "object"
              "properties",
              Encode.object
                  [ for field in fields ->
                      field.Name,
                      Encode.object
                          [ "type", Encode.string field.Type
                            "description", Encode.string field.Description ] ]
              "required",
              fields
              |> List.filter (fun field -> field.Required)
              |> List.map (fun field -> Encode.string field.Name)
              |> Encode.list
              "additionalProperties", Encode.bool false ]
        |> Encode.toString 0

[<Emit("new URL($0, 'http://local').pathname")>]
let private pathnameOf (url: string) : string = jsNative

/// Raw JSON text, spliced into an encoded document without being re-encoded. Thoth has no
/// "already JSON" encoder, and round-tripping a foreign schema through `Decode.value` would
/// reorder and reformat somebody else's document for no reason.
[<Emit("JSON.parse($0)")>]
let private rawJson (json: string) : JsonValue = jsNative

let private readBody (req: IncomingMessage) (cont: string -> unit) =
    let mutable acc = ""
    req.on ("data", fun chunk -> acc <- acc + bufferToString chunk) |> ignore
    req.on ("end", fun _ -> cont acc) |> ignore

let private respond (res: ServerResponse) (status: int) (body: string) (extra: (string * obj) list) =
    let headers = [ "content-type", box "application/json"; "cache-control", box "no-store" ] @ extra
    res.writeHead (status, createObj headers) |> ignore
    res.``end`` body

// --- the JSON-RPC envelopes ---------------------------------------------------------------

type private Request =
    { Id : int
      Method : string
      Params : string option }

let private requestDecoder : Decoder<Request> =
    Decode.object (fun get ->
        { Id = get.Optional.Field "id" Decode.int |> Option.defaultValue 0
          Method = get.Required.Field "method" Decode.string
          Params =
            get.Optional.Field "params" Decode.value
            |> Option.map (fun value -> Encode.toString 0 value) })

let private result (id: int) (payload: JsonValue) =
    Encode.object [ "jsonrpc", Encode.string "2.0"; "id", Encode.int id; "result", payload ]
    |> Encode.toString 0

let private failure (id: int) (code: int) (message: string) =
    Encode.object
        [ "jsonrpc", Encode.string "2.0"
          "id", Encode.int id
          "error", Encode.object [ "code", Encode.int code; "message", Encode.string message ] ]
    |> Encode.toString 0

let private toolJson (tool: Tool) : JsonValue =
    Encode.object
        [ yield "name", Encode.string tool.Name
          match tool.Title with
          | Some title -> yield "title", Encode.string title
          | None -> ()
          yield "description", Encode.string tool.Description
          yield "inputSchema", rawJson tool.InputSchema ]

/// Serve one MCP server at `/mcp`.
let create (def: ServerDef) : ServerHandler =
    // Session ids we minted. A server MAY be stateless; being stateful is what lets a claim
    // be bound to a caller, and it is what makes a restart legible — every id we did not
    // mint gets a 404, which the spec's clients read as "it restarted" rather than as an
    // outage, and re-handshake.
    let sessions = HashSet<string> ()
    let mutable onSessionEnded : string -> unit = ignore

    let handle (req: IncomingMessage) (res: ServerResponse) : unit =
        let sent = headerOf req "mcp-session-id" |> Option.defaultValue ""
        readBody req (fun body ->
            let body = if String.IsNullOrWhiteSpace body then "{}" else body
            match Decode.fromString requestDecoder body with
            | Error _ ->
                // A notification carries no id and gets no reply — 202, per the spec, so a
                // client that sends `notifications/initialized` is not left waiting for a
                // frame that is never coming.
                if body.Contains "notifications/" then
                    res.writeHead (202, createObj [ "content-type", box "text/plain" ]) |> ignore
                    res.``end`` ""
                else respond res 400 """{"error":"not a JSON-RPC request"}""" []
            | Ok request ->
                match request.Method with
                | "initialize" ->
                    let id = randomId ()
                    sessions.Add id |> ignore
                    let payload =
                        Encode.object
                            [ "protocolVersion", Encode.string ProtocolVersion
                              "serverInfo",
                              Encode.object
                                  [ "name", Encode.string def.Name
                                    "version", Encode.string def.Version ] ]
                    respond res 200 (result request.Id payload) [ "mcp-session-id", box id ]
                // Every other method needs a session we minted.
                | _ when sent = "" || not (sessions.Contains sent) ->
                    respond res 404 """{"error":"no such session"}""" []
                | "tools/list" ->
                    let payload = Encode.object [ "tools", def.Tools |> List.map toolJson |> Encode.list ]
                    respond res 200 (result request.Id payload) []
                | "tools/call" ->
                    let callDecoder =
                        Decode.object (fun get ->
                            get.Required.Field "name" Decode.string,
                            get.Optional.Field "arguments" Decode.value
                            |> Option.map (Encode.toString 0)
                            |> Option.defaultValue "{}")
                    match Decode.fromString callDecoder (Option.defaultValue "{}" request.Params) with
                    | Error e -> respond res 200 (failure request.Id -32602 e) []
                    | Ok (name, arguments) ->
                        Async.StartImmediate (
                            async {
                                let! outcome = def.Invoke sent { Name = name; Arguments = arguments }
                                match outcome with
                                // A tool that RAN and went badly is a RESULT with `isError`,
                                // not a JSON-RPC error: the model is meant to read it and
                                // choose differently. Only a call that never reached a tool
                                // is an error frame.
                                | Ok reply ->
                                    let payload =
                                        Encode.object
                                            ([ "content",
                                               Encode.list
                                                   [ Encode.object
                                                         [ "type", Encode.string "text"
                                                           "text", Encode.string reply.Text ] ]
                                               "isError", Encode.bool false ]
                                             // Embedded as a real JSON node rather than a
                                             // quoted string. A `_meta` this server could
                                             // not parse is dropped: the prose is the part
                                             // the caller cannot do without.
                                             @ (match reply.Meta with
                                                | Some raw ->
                                                    match Decode.fromString Decode.value raw with
                                                    | Ok node -> [ "_meta", node ]
                                                    | Error _ -> []
                                                | None -> []))
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
                    // No GET stream: this server pushes nothing, so offering one would be a
                    // long-lived connection per client with nothing to carry.
                    respond res 405 """{"error":"POST or DELETE"}""" []
                    true
      Sessions = fun () -> sessions |> List.ofSeq |> List.sort
      OnSessionEnded = fun handler -> onSessionEnded <- handler }
