module Yession.Host.TelemetryReceiver

// The Manager's audit-telemetry records (Plan 06). Session telemetry no longer flows through
// the Manager — every process is a direct OTel emitter (app/Telemetry.fs) — so the OTLP `/v1/logs`
// receiver and per-session collector that once lived here are gone. What remains is the Manager
// emitting OTel-log-shaped audit events about its OWN authority decisions: secrets operations,
// policy denies, SecretRef injection, KEK/store lifecycle, user↔launch bindings, and control-channel
// 401s. Identifiers and names only — no constructor accepts a secret value, KEK material, or control
// secret, so content cannot leak by type.
//
// (The module keeps its historical name so the Plan 06 call sites in ProcessManager are undisturbed;
// it is now an audit emitter, not a receiver.)

open Yession.Domain

/// A log attribute value — string or int (the two the audit records use).
type LogValue =
    | StringValue of string
    | IntValue of int

/// An OTel-log-shaped audit record: a human `Body`, a severity number (9 INFO / 13 WARN / 17 ERROR),
/// and identifier/name attributes. Never carries a secret value.
type ReceivedLog =
    { Body : string
      Severity : int
      Attributes : Map<string, LogValue> }

/// An audit record consumer. Built ONCE per Manager (createWithUi).
type Sink = ReceivedLog -> unit

/// OTel-log-shaped audit events the Manager emits IN-PROCESS about its own authority
/// decisions. Records carry `event.name` (`yession.*`) and `service.name = yession-manager`.
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

    /// The prod sink: one greppable audit line to stdout. (Forwarding audit to an OTel
    /// collector alongside session telemetry is a documented follow-up.)
    let stdout : Sink = fun r -> printfn "%s" (format r)

    /// Map the injection walk's outcome (SecretStore.SecretResolution) to records.
    let injectObserver (sink: Sink) : SecretStore.SecretResolution.Observe =
        fun sessionId name outcome ->
            match outcome with
            | SecretStore.SecretResolution.InjectedFromScope (SessionScope _) -> sink (inject sessionId name "session")
            | SecretStore.SecretResolution.InjectedFromScope (UserScope _) -> sink (inject sessionId name "user")
            | SecretStore.SecretResolution.InjectedFromFallback -> sink (inject sessionId name "env")
            | SecretStore.SecretResolution.InjectMissed reason -> sink (injectMiss sessionId name reason)
