module Yession.Tests.OtlpStub

// A localhost OTLP/HTTP JSON logs receiver for tests — the stand-in for a real OpenTelemetry
// Collector now that the Manager no longer collects. It decodes the narrow OTLP logs JSON the
// emitter produces (`resourceLogs[].scopeLogs[].logRecords[]`) and records the LogRecords for
// assertions. A real Collector would receive the identical payload. Accepts a POST on any path
// (the exporter posts to `<endpoint>/v1/logs`), so callers point either an explicit url or
// `OTEL_EXPORTER_OTLP_ENDPOINT` at it.

open Fable.Core
open Fable.Core.JsInterop
open Yession.Host

// --- Decode (the narrow OTLP/HTTP JSON logs subset the emitter emits) ---------------------

type LogValue =
    | StringValue of string
    | IntValue of int

type ReceivedLog =
    { Body : string
      Attributes : Map<string, LogValue>
      /// The attributes of the RESOURCE this record was emitted under (`service.name`,
      /// `service.version`, …). OTLP sends these once per payload, not per record, so they are
      /// folded onto each record here — but kept in their own field rather than merged into
      /// `Attributes`, so a test asserting a resource attribute cannot be satisfied by a
      /// record-level one of the same name.
      Resource : Map<string, LogValue> }

[<Emit("$0[$1]")>]
let private prop (o: obj) (key: string) : obj = jsNative

[<Emit("Array.isArray($0) ? $0 : []")>]
let private asArray (o: obj) : obj array = jsNative

[<Emit("$0 == null")>]
let private isNullish (o: obj) : bool = jsNative

// OTLP JSON encodes int64 as a string; the JS exporter emits a bare number for small ints.
[<Emit("typeof $0 === 'number' ? ($0 | 0) : (parseInt($0, 10) || 0)")>]
let private asInt (o: obj) : int = jsNative

[<Emit("(() => { try { return JSON.parse($0) } catch (e) { return null } })()")>]
let private tryParse (json: string) : obj = jsNative

let private valueOf (value: obj) : LogValue option =
    if isNullish value then None
    else
        let s = prop value "stringValue"
        if not (isNullish s) then Some (StringValue (unbox<string> s))
        else
            let i = prop value "intValue"
            if not (isNullish i) then Some (IntValue (asInt i))
            else None

let private attributesOf (holder: obj) : Map<string, LogValue> =
    asArray (prop holder "attributes")
    |> Array.fold
        (fun acc kv ->
            let key = prop kv "key"
            match (if isNullish key then None else Some (unbox<string> key)), valueOf (prop kv "value") with
            | Some k, Some v -> Map.add k v acc
            | _ -> acc)
        Map.empty

let private logRecordOf (resource: Map<string, LogValue>) (lr: obj) : ReceivedLog =
    let body =
        let b = prop lr "body"
        if isNullish b then ""
        else
            let s = prop b "stringValue"
            if isNullish s then "" else unbox<string> s
    { Body = body; Attributes = attributesOf lr; Resource = resource }

/// Decode an OTLP/HTTP JSON logs payload into the flat list of records it carries, each carrying
/// the attributes of the resource it was emitted under.
let decode (json: string) : ReceivedLog list =
    let root = tryParse json
    if isNullish root then []
    else
        asArray (prop root "resourceLogs")
        |> Array.collect (fun rl ->
            // A payload need not carry a resource block; that decodes to no attributes, not a throw.
            let holder = prop rl "resource"
            let resource = if isNullish holder then Map.empty else attributesOf holder
            asArray (prop rl "scopeLogs")
            |> Array.collect (fun sl -> asArray (prop sl "logRecords"))
            |> Array.map (logRecordOf resource))
        |> List.ofArray

// --- Accessors ---------------------------------------------------------------------------

let stringAttr (key: string) (r: ReceivedLog) : string option =
    match Map.tryFind key r.Attributes with Some (StringValue s) -> Some s | _ -> None

/// A string attribute of the record's RESOURCE — the emitting process's identity.
let resourceAttr (key: string) (r: ReceivedLog) : string option =
    match Map.tryFind key r.Resource with Some (StringValue s) -> Some s | _ -> None

let intAttr (key: string) (r: ReceivedLog) : int option =
    match Map.tryFind key r.Attributes with Some (IntValue i) -> Some i | _ -> None

/// The agent-turn usage a record carries, when it is one (the yession ids are present).
type TurnUsage =
    { SessionId : string
      TurnId : string
      InputTokens : int
      OutputTokens : int
      CacheReadTokens : int
      CacheCreationTokens : int
      Model : string option }

let turnUsage (r: ReceivedLog) : TurnUsage option =
    match stringAttr "yession.session.id" r, stringAttr "yession.agent.turn.id" r with
    | Some sessionId, Some turnId ->
        Some
            { SessionId = sessionId
              TurnId = turnId
              InputTokens = intAttr "gen_ai.usage.input_tokens" r |> Option.defaultValue 0
              OutputTokens = intAttr "gen_ai.usage.output_tokens" r |> Option.defaultValue 0
              CacheReadTokens = intAttr "anthropic.usage.cache_read_input_tokens" r |> Option.defaultValue 0
              CacheCreationTokens = intAttr "anthropic.usage.cache_creation_input_tokens" r |> Option.defaultValue 0
              Model = stringAttr "gen_ai.response.model" r }
    | _ -> None

// --- The server --------------------------------------------------------------------------

/// A running stub collector: its base URL, the decoded records so far, and a close.
type Stub =
    { Url : string
      Received : unit -> ReceivedLog list
      Close : unit -> unit }

let private readBody (req: Interop.IncomingMessage) (cont: string -> unit) =
    let mutable acc = ""
    req.on ("data", fun chunk -> acc <- acc + Interop.bufferToString chunk) |> ignore
    req.on ("end", fun _ -> cont acc) |> ignore

/// Start a stub OTLP collector on localhost, calling `onRecord` for each decoded record (so a
/// caller can await arrival without polling). Any POST body is decoded and its records appended.
let startWith (onRecord: ReceivedLog -> unit) : Async<Stub> =
    async {
        let received = ResizeArray<ReceivedLog> ()
        let server =
            Interop.createServer (fun req res ->
                match req.``method`` with
                | "POST" ->
                    readBody req (fun body ->
                        for r in decode body do
                            received.Add r
                            onRecord r
                        res.writeHead (200, createObj [ "content-type", box "application/json" ]) |> ignore
                        res.``end`` "{}")
                | _ ->
                    res.writeHead (405, createObj [ "content-type", box "text/plain" ]) |> ignore
                    res.``end`` "method not allowed")
        let! url =
            Async.FromContinuations (fun (cont, _, _) ->
                server.listen (0, "127.0.0.1", fun () ->
                    cont (sprintf "http://127.0.0.1:%d" (Interop.serverPort server)))
                |> ignore)
        return
            { Url = url
              Received = fun () -> List.ofSeq received
              Close = fun () -> server.close ignore }
    }

/// Start a stub OTLP collector on localhost (no arrival signal).
let start () : Async<Stub> = startWith ignore
