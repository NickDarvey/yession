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
open Yession.Domain
open Yession.Host.Interop

// --- OTLP/HTTP JSON logs decode (only the subset the emitter emits) ----------------------

type LogValue =
    | StringValue of string
    | IntValue of int

type ReceivedLog =
    { Body : string
      /// OTel log-data-model severity number (9 INFO, 13 WARN, 17 ERROR). The session
      /// emitter has always sent `severityNumber`; decode defaults to INFO when absent.
      Severity : int
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
    let severity =
        let n = prop lr "severityNumber"
        if isNullish n then 9 else asInt n
    { Body = body; Severity = severity; Attributes = attributesOf lr }

module LogsWire =
    /// Decode an OTLP/HTTP JSON logs payload into the flat list of records it carries.
    /// Malformed JSON is an `Error`, never a throw.
    let decode (json: string) : Result<ReceivedLog list, string> =
        let root = tryParse json
        if isNullish root then Error "telemetry: body is not valid JSON"
        else
            asArray (prop root "resourceLogs")
            |> Array.collect (fun rl -> asArray (prop rl "scopeLogs"))
            |> Array.collect (fun sl -> asArray (prop sl "logRecords"))
            |> Array.map logRecordOf
            |> List.ofArray
            |> Ok

// --- Manager audit records (Plan 06 telemetry) -------------------------------------------

/// An audit record consumer. Built ONCE per Manager (createWithUi): the telemetry
/// collector's `Record` when a collector is configured, `Audit.stdout` otherwise —
/// either/or, never both, so a record is printed exactly once.
type Sink = ReceivedLog -> unit

/// OTel-log-shaped audit events the Manager emits IN-PROCESS about its own authority
/// decisions: secrets operations, policy denies, SecretRef injection, KEK/store
/// lifecycle, user↔launch bindings, and control-channel 401s. Identifiers and names
/// only — no constructor accepts a secret value, KEK material, or control secret, so
/// content cannot leak by type. Records carry `event.name` (`yession.*`) and
/// `service.name = yession-manager`, ready for the collector's `onRecord` re-export.
module Audit =

    module Severity =
        let info = 9
        let warn = 13
        let error = 17

    let private record (name: string) (severity: int) (attributes: (string * LogValue) list) (body: string) : ReceivedLog =
        { Body = body
          Severity = severity
          Attributes =
            Map.ofList
                ([ "event.name", StringValue name
                   "service.name", StringValue "yession-manager" ]
                 @ attributes) }

    let private scopeAttrs (scope: SecretScope) : (string * LogValue) list =
        match scope with
        | SessionScope sessionId ->
            [ "yession.secret.scope", StringValue "session"
              "yession.secret.scope_key", StringValue (SessionId.value sessionId) ]
        | UserScope user ->
            [ "yession.secret.scope", StringValue "user"
              "yession.secret.scope_key", StringValue (UserSubject.value user) ]

    let private session (sessionId: SessionId) = "yession.session.id", StringValue (SessionId.value sessionId)
    let private outcome ok = "yession.outcome", StringValue (if ok then "ok" else "failed")

    let private secretOp (op: string) (sessionId: SessionId) (id: SecretId) (ok: bool) : ReceivedLog =
        record
            (sprintf "yession.secret.%s" op)
            (if ok then Severity.info else Severity.warn)
            ([ session sessionId
               "yession.secret.name", StringValue (SecretName.value id.Name)
               outcome ok ]
             @ scopeAttrs id.Scope)
            (sprintf "secret %s %s" op (if ok then "ok" else "failed"))

    let secretSet (sessionId: SessionId) (id: SecretId) (ok: bool) : ReceivedLog = secretOp "set" sessionId id ok
    let secretDelete (sessionId: SessionId) (id: SecretId) (ok: bool) : ReceivedLog = secretOp "delete" sessionId id ok

    let secretList (sessionId: SessionId) (scope: SecretScope) (count: int) : ReceivedLog =
        record "yession.secret.list" Severity.info
            ([ session sessionId; "yession.secret.count", IntValue count ] @ scopeAttrs scope)
            "secret list"

    let authzDeny (sessionId: SessionId) (action: SecretAction) (resource: AuthzResource) (reason: string) : ReceivedLog =
        let resourceAttrs =
            match resource with
            | SecretResource id ->
                ("yession.secret.name", StringValue (SecretName.value id.Name)) :: scopeAttrs id.Scope
            | SecretCollection scope -> scopeAttrs scope
        record "yession.authz.deny" Severity.warn
            ([ session sessionId
               "yession.authz.action", StringValue (sprintf "%A" action)
               "yession.authz.reason", StringValue reason ]
             @ resourceAttrs)
            (sprintf "secrets: DENY %A for session %s: %s" action (SessionId.value sessionId) reason)

    let inject (sessionId: SessionId) (name: SecretName) (source: string) : ReceivedLog =
        record "yession.secret.inject" Severity.info
            [ session sessionId
              "yession.secret.name", StringValue (SecretName.value name)
              "yession.inject.source", StringValue source ]
            "secret injected into environment"

    let injectMiss (sessionId: SessionId) (name: SecretName) (reason: string) : ReceivedLog =
        record "yession.secret.inject" Severity.warn
            [ session sessionId
              "yession.secret.name", StringValue (SecretName.value name)
              "yession.inject.source", StringValue "none"
              "yession.authz.reason", StringValue reason ]
            "secret injection missed"

    let storeOpen (mode: string) (keystore: string) (kekMinted: bool) (entries: int) : ReceivedLog =
        record "yession.secrets.store_open" Severity.info
            [ "yession.secrets.mode", StringValue mode
              "yession.secrets.keystore", StringValue keystore
              "yession.secrets.kek", StringValue (if kekMinted then "created" else "loaded")
              "yession.secrets.entries", IntValue entries ]
            (sprintf "secrets store open (%s)" mode)

    let storeEphemeral : ReceivedLog =
        record "yession.secrets.store_open" Severity.warn
            [ "yession.secrets.mode", StringValue "ephemeral" ]
            "secrets: no OS credential manager available — secrets are IN-MEMORY ONLY and die with this Manager"

    let storeInaccessible (path: string) : ReceivedLog =
        record "yession.secrets.store_open" Severity.warn
            [ "yession.secrets.mode", StringValue "ephemeral"
              "yession.secrets.path", StringValue path ]
            (sprintf "secrets: %s exists but its key lives in a credential manager this host cannot reach — stored secrets stay inaccessible (and untouched) until one is available" path)

    let storeOpenFailed (kind: string) (detail: string) : ReceivedLog =
        record "yession.secrets.store_open_failed" Severity.error
            [ "yession.secrets.reason", StringValue kind ]
            detail

    let bindingRecorded (sessionId: SessionId) (subject: UserSubject) : ReceivedLog =
        record "yession.auth.binding_recorded" Severity.info
            [ session sessionId; "yession.auth.sub", StringValue (UserSubject.value subject) ]
            "user verified into launch"

    let bindingRevoked (sessionId: SessionId) : ReceivedLog =
        record "yession.auth.binding_revoked" Severity.info
            [ session sessionId ]
            "user bindings revoked with launch"

    let controlUnauthorized (path: string) : ReceivedLog =
        record "yession.control.unauthorized" Severity.warn
            [ "yession.http.path", StringValue path ]
            "invalid control secret"

    let private severityLabel (severity: int) : string =
        if severity = Severity.info then "INFO"
        elif severity = Severity.warn then "WARN"
        elif severity = Severity.error then "ERROR"
        else string severity

    /// One greppable line, deterministic (Map iterates key-sorted): severity, event
    /// name, attributes (minus the two that lead the line), then the human body.
    let format (r: ReceivedLog) : string =
        let name =
            match Map.tryFind "event.name" r.Attributes with
            | Some (StringValue n) -> n
            | _ -> "-"
        let attrs =
            r.Attributes
            |> Map.toList
            |> List.filter (fun (k, _) -> k <> "event.name" && k <> "service.name")
            |> List.map (fun (k, v) ->
                match v with
                | StringValue s -> sprintf "%s=%s" k s
                | IntValue i -> sprintf "%s=%d" k i)
            |> String.concat " "
        sprintf "audit %s %s %s :: %s" (severityLabel r.Severity) name attrs r.Body

    /// The prod sink when no telemetry collector is configured.
    let stdout : Sink = fun r -> printfn "%s" (format r)

    /// Map the injection walk's outcome (SecretStore.SecretResolution) to records.
    let injectObserver (sink: Sink) : SecretStore.SecretResolution.Observe =
        fun sessionId name outcome ->
            match outcome with
            | SecretStore.SecretResolution.InjectedFromScope (SessionScope _) -> sink (inject sessionId name "session")
            | SecretStore.SecretResolution.InjectedFromScope (UserScope _) -> sink (inject sessionId name "user")
            | SecretStore.SecretResolution.InjectedFromFallback -> sink (inject sessionId name "env")
            | SecretStore.SecretResolution.InjectMissed reason -> sink (injectMiss sessionId name reason)

// --- Interpreting a record as agent-turn usage -------------------------------------------

/// The agent-turn usage a log record carries, when it is one (the yession ids are present).
type TurnUsage =
    { SessionId : string
      TurnId : string
      InputTokens : int
      OutputTokens : int
      CacheReadTokens : int
      CacheCreationTokens : int
      Model : string option }

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
                  Model = stringAttr "gen_ai.response.model" r }
        | _ -> None

// --- The collector -----------------------------------------------------------------------

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
                        "telemetry session=%s turn=%s in=%d out=%d cache_read=%d cache_create=%d model=%s (session totals in=%d out=%d)"
                        u.SessionId u.TurnId u.InputTokens u.OutputTokens u.CacheReadTokens u.CacheCreationTokens
                        (Option.defaultValue "-" u.Model) (fst totalsNow) (snd totalsNow)
                | None ->
                    // Manager audit records (Plan 06) print through their formatter;
                    // anything else keeps the legacy non-usage line.
                    match Map.tryFind "event.name" r.Attributes with
                    | Some (StringValue name) when name.StartsWith "yession." -> printfn "%s" (Audit.format r)
                    | _ -> printfn "telemetry non-usage record: %s" r.Body
                onRecord r }

    /// A collector with a downstream seam that retains every record — the in-test double.
    let create (onRecord: ReceivedLog -> unit) : Collector = make true onRecord

    /// Retains every record (the in-test double).
    let inMemory () : Collector = create ignore

    /// The Manager's production collector: logs + aggregates per-session totals but retains
    /// no records (the Manager is long-lived — unbounded retention would leak).
    let logging () : Collector = make false ignore

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
