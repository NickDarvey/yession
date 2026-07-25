module Yession.Host.TelemetryReceiver

// Plan 04, Step 31: the Manager-side OTLP/HTTP logs receiver — the OpenTelemetry *collector*
// role. Decodes the narrow slice of OTLP/HTTP JSON logs the session emitter produces
// (app/Telemetry.fs), aggregates per-session token totals, and logs each turn. `tryHandle`
// composes into the Manager's shared server exactly like `Control.tryHandle` (falls through
// on other paths), bearer-authenticated. NO session content is expected — counts + ids only.
//
// The same `Collector` (with its `Received` list) is reused as the in-test double, so one
// implementation serves both roles (Plan 04 § Test harness).

open Fable.Core
open Fable.Core.JsInterop
open Yession.Host.Interop

// --- OTLP/HTTP JSON logs decode (only the subset the emitter emits) ----------------------

type LogValue =
    | StringValue of string
    | IntValue of int

type ReceivedLog =
    { Body : string
      Attributes : Map<string, LogValue> }

[<Emit("$0[$1]")>]
let private prop (o: obj) (key: string) : obj = jsNative

[<Emit("Array.isArray($0) ? $0 : []")>]
let private asArray (o: obj) : obj array = jsNative

[<Emit("$0 == null")>]
let private isNullish (o: obj) : bool = jsNative

// OTLP JSON encodes int64 as a string; the JS exporter emits a bare number for small ints.
// Accept both.
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

let private logRecordOf (lr: obj) : ReceivedLog =
    let body =
        let b = prop lr "body"
        if isNullish b then ""
        else
            let s = prop b "stringValue"
            if isNullish s then "" else unbox<string> s
    { Body = body; Attributes = attributesOf lr }

module LogsWire =
    /// Decode an OTLP/HTTP JSON logs payload into the flat list of records it carries.
    /// Malformed JSON is an `Error`, never a throw.
    let decode (json: string) : Result<ReceivedLog list, string> =
        let root = tryParse json
        if isNullish root then Error "telemetry: body is not valid JSON"
        else
            asArray (prop root "resourceLogs")
            |> Array.collect (fun rl ->
                // Resource attributes (`service.name`, `service.version`, `service.instance.id`)
                // describe the EMITTER and are sent once per payload, not per record. Fold them
                // onto every record underneath so a consumer of one record still knows which
                // build produced it; a record-level attribute of the same name wins.
                let holder = prop rl "resource"
                let resource = if isNullish holder then Map.empty else attributesOf holder
                asArray (prop rl "scopeLogs")
                |> Array.collect (fun sl -> asArray (prop sl "logRecords"))
                |> Array.map (fun lr ->
                    let record = logRecordOf lr
                    { record with
                        Attributes = record.Attributes |> Map.fold (fun acc k v -> Map.add k v acc) resource }))
            |> List.ofArray
            |> Ok

// --- Interpreting a record as agent-turn usage -------------------------------------------

/// The agent-turn usage a log record carries, when it is one (the yession ids are present).
type TurnUsage =
    { SessionId : string
      TurnId : string
      InputTokens : int
      OutputTokens : int
      CacheReadTokens : int
      CacheCreationTokens : int
      Model : string option
      /// The emitting Session Process's build (`service.version` off its OTel resource).
      /// None from an emitter old enough not to send one.
      Version : string option }

module TurnUsage =
    let private stringAttr key (r: ReceivedLog) =
        match Map.tryFind key r.Attributes with
        | Some (StringValue s) -> Some s
        | _ -> None

    let private intAttr key (r: ReceivedLog) =
        match Map.tryFind key r.Attributes with
        | Some (IntValue i) -> Some i
        | _ -> None

    /// Interpret a received log as agent-turn usage; `None` for any non-usage record.
    let ofLog (r: ReceivedLog) : TurnUsage option =
        match stringAttr "yession.session.id" r, stringAttr "yession.agent.turn.id" r with
        | Some sessionId, Some turnId ->
            Some
                { SessionId = sessionId
                  TurnId = turnId
                  InputTokens = intAttr "gen_ai.usage.input_tokens" r |> Option.defaultValue 0
                  OutputTokens = intAttr "gen_ai.usage.output_tokens" r |> Option.defaultValue 0
                  CacheReadTokens = intAttr "anthropic.usage.cache_read_input_tokens" r |> Option.defaultValue 0
                  CacheCreationTokens = intAttr "anthropic.usage.cache_creation_input_tokens" r |> Option.defaultValue 0
                  Model = stringAttr "gen_ai.response.model" r
                  Version = stringAttr "service.version" r }
        | _ -> None

// --- The collector -----------------------------------------------------------------------

/// The Manager's own OTel identity in its collector role (Plan 04 § Resource attributes): the
/// `service.*` of the process that RECEIVED a record, as opposed to the session that emitted it.
/// Carried here for the `onRecord` re-export seam, which will need to stamp it on what it
/// forwards.
let serviceName = "yession-manager"
let serviceVersion = Version.current

/// Aggregates per-session token totals and logs each turn. `Received` exposes every decoded
/// record — the reuse point for the in-test double. `onRecord` is the seam where downstream
/// re-export / a real OTel Collector hand-off is added later (Plan 04 § Non-goals).
type Collector =
    { Record : ReceivedLog -> unit
      Received : unit -> ReceivedLog list }

module Collector =
    let private make (retain: bool) (onRecord: ReceivedLog -> unit) : Collector =
        let received = ResizeArray<ReceivedLog> ()
        let mutable totals : Map<string, int * int> = Map.empty
        { Received = fun () -> List.ofSeq received
          Record =
            fun r ->
                if retain then received.Add r
                match TurnUsage.ofLog r with
                | Some u ->
                    let inSoFar, outSoFar = Map.tryFind u.SessionId totals |> Option.defaultValue (0, 0)
                    let totalsNow = inSoFar + u.InputTokens, outSoFar + u.OutputTokens
                    totals <- Map.add u.SessionId totalsNow totals
                    printfn
                        "telemetry session=%s version=%s turn=%s in=%d out=%d cache_read=%d cache_create=%d model=%s (session totals in=%d out=%d)"
                        u.SessionId (Option.defaultValue "-" u.Version) u.TurnId u.InputTokens u.OutputTokens
                        u.CacheReadTokens u.CacheCreationTokens
                        (Option.defaultValue "-" u.Model) (fst totalsNow) (snd totalsNow)
                | None -> printfn "telemetry non-usage record: %s" r.Body
                onRecord r }

    /// A collector with a downstream seam that retains every record — the in-test double.
    let create (onRecord: ReceivedLog -> unit) : Collector = make true onRecord

    /// Retains every record (the in-test double).
    let inMemory () : Collector = create ignore

    /// The Manager's production collector: logs + aggregates per-session totals but retains
    /// no records (the Manager is long-lived — unbounded retention would leak).
    let logging () : Collector =
        printfn "Yession Manager: telemetry collector %s %s" serviceName serviceVersion
        make false ignore

// --- The route handler -------------------------------------------------------------------

[<Emit("new URL($0, 'http://local').pathname")>]
let private pathnameOf (url: string) : string = jsNative

let private bearerOf (req: IncomingMessage) : string option =
    match headerOf req "authorization" with
    | Some h when h.StartsWith "Bearer " -> Some (h.Substring 7)
    | _ -> None

let private readBody (req: IncomingMessage) (cont: string -> unit) =
    let mutable acc = ""
    req.on ("data", fun chunk -> acc <- acc + bufferToString chunk) |> ignore
    req.on ("end", fun _ -> cont acc) |> ignore

let private respond (res: ServerResponse) (status: int) (contentType: string) (bodyText: string) =
    res.writeHead (status, createObj [ "content-type", box contentType; "cache-control", box "no-store" ]) |> ignore
    res.``end`` bodyText

/// Handle a telemetry request. Returns false when the path is not `/v1/logs`, so a composing
/// HTTP server (the Manager shares its port with control + UI) falls through — exactly like
/// `Control.tryHandle`. Bearer-authenticated via `authorize`.
let tryHandle
    (authorize: string -> bool)
    (collector: Collector)
    (req: IncomingMessage)
    (res: ServerResponse)
    : bool =
    if pathnameOf req.url <> "/v1/logs" then false
    else
        match req.``method``, bearerOf req with
        | "POST", Some secret when authorize secret ->
            readBody req (fun body ->
                match LogsWire.decode body with
                | Ok logs ->
                    logs |> List.iter collector.Record
                    respond res 200 "application/json" "{}"
                | Error reason -> respond res 400 "text/plain" reason)
        | "POST", _ -> respond res 401 "text/plain" "invalid telemetry secret"
        | _ -> respond res 405 "text/plain" "method not allowed"
        true
