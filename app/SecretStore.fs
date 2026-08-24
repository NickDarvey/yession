module Yession.Host.SecretStore

// The Manager's secret store (Plan 06): the pure `SecretsFile` envelope behind the
// `SecretsCodec`, values as per-entry AES-GCM ciphertext under a KEK the OS credential
// manager holds (KeyStore). One code path serves both modes: `Some path` = durable
// (encrypted file in the Manager's data dir, ManagerStore-style atomic writes);
// `None` = ephemeral (no credential manager on this host — no file I/O at all, a
// per-boot random KEK, everything dies with the Manager). `Resolve` feeds environment
// injection only, and with session-owned sandboxes that injection happens at the
// SESSION'S sandbox spawn: a value crosses only the authenticated loopback control
// channel (`/control/secrets/resolve`, gated by the readable-scope walk below), only
// at sandbox spawn, and never reaches the agent loop.

open System
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Access
open Yession.Manager

type SecretStore =

    { List : SecretScope -> SecretMetadata list
      Set : SecretId -> string -> Async<Result<SecretMetadata, string>>
      /// false = did not exist.
      Delete : SecretId -> Async<Result<bool, string>>
      /// Decrypt-on-demand for env injection. Never exposed over the control channel.
      Resolve : SecretId -> Async<Result<string option, string>>
      /// True when backed by the encrypted file; false when in-memory only.
      Durable : bool
      /// True when the KEK was minted at this open (first run); false when loaded.
      KekMinted : bool
      /// Entries in the envelope at open (0 for a fresh or ephemeral store).
      EntriesAtOpen : int }

/// Why an open failed — structural, so the boot path can audit the KIND without
/// string-sniffing. `describe` renders exactly the wording the failures always had.
type OpenError =
    | KeyStoreFailure of detail: string
    | CorruptStore of path: string * detail: string
    | SealedByDifferentKey of path: string * fileKekId: string * hostKekId: string

module OpenError =
    let describe (error: OpenError) : string =
        match error with
        | KeyStoreFailure detail -> detail
        | CorruptStore (path, detail) -> sprintf "corrupt secrets store %s: %s" path detail
        | SealedByDifferentKey (path, fileKekId, hostKekId) ->
            sprintf "secrets store %s was sealed by a different key (%s, this host's is %s) — restore that key or delete the file" path fileKekId hostKekId

    let kind (error: OpenError) : string =
        match error with
        | KeyStoreFailure _ -> "keystore"
        | CorruptStore _ -> "corrupt"
        | SealedByDifferentKey _ -> "sealed"

let private now () : DateTimeOffset = DateTimeOffset.UtcNow

/// How a WorkSandbox's `SecretRef` env vars resolve to values, scoped to the session
/// whose sandbox is spawning. The ONLY consumer of `SecretStore.Resolve`; served to
/// sessions by the `/control/secrets/resolve` route.
type ResolveSecret = SessionId -> SecretName -> Async<Result<string, string>>

/// Open the store. Durable when `persistPath` is set: KEK through the KeyStore
/// (created on first run, durable in the credential manager BEFORE first use), file
/// loaded through the codec (missing = empty; corrupt = loud Error; KekId mismatch =
/// distinct loud Error). Ephemeral when `None`: same semantics, zero file I/O.
let openStore (persistPath: string option) (keyStore: KeyStore.KeyStore) : Async<Result<SecretStore, OpenError>> =
    async {
        // Resolve (or mint) the KEK — the only moment the raw key bytes are in scope.
        let! stored = keyStore.Get ()
        match stored |> Result.bind (function
                        | Some raw -> KeyStore.splitPayload raw |> Result.map (fun p -> p, false)
                        | None ->
                            let fresh = Interop.randomSecret (), SecretsCipher.generateKek ()
                            Ok (fresh, true)) with
        | Error e -> return Error (KeyStoreFailure (sprintf "secrets key store (%s): %s" keyStore.Name e))
        | Ok ((kekId, kekB64u), minted) ->
            let persist (result: Result<unit, string>) =
                async {
                    if minted then
                        // Durable in the credential manager BEFORE any entry is sealed
                        // under it — a crash between the two loses nothing.
                        let! set = keyStore.Set (KeyStore.payload kekId kekB64u)
                        return set |> Result.bind (fun () -> result)
                    else
                        return result
                }
            let! persisted = persist (Ok ())
            match persisted with
            | Error e -> return Error (KeyStoreFailure (sprintf "secrets key store (%s): could not persist the key: %s" keyStore.Name e))
            | Ok () ->

            let! cipher = SecretsCipher.importKey kekB64u

            // Load (or initialise) the envelope.
            let initial : Result<SecretsFile, OpenError> =
                match persistPath with
                | None -> Ok (SecretsFile.empty kekId)
                | Some path when not (Fs.exists path) -> Ok (SecretsFile.empty kekId)
                | Some path ->
                    match SecretsCodec.fromString (Fs.readText path) with
                    | Error e -> Error (CorruptStore (path, e))
                    | Ok file when file.KekId <> kekId ->
                        Error (SealedByDifferentKey (path, file.KekId, kekId))
                    | Ok file -> Ok file

            match initial with
            | Error e -> return Error e
            | Ok loaded ->
                // The single mutable envelope. Mutations are read-modify-write-save on
                // the Node loop AFTER their (async) encryption completes, so
                // interleaved Sets compose instead of losing entries.
                let mutable file = loaded
                let save () =
                    persistPath |> Option.iter (fun path -> Fs.writeTextAtomic path (SecretsCodec.toString file))

                let store =
                    { List = fun scope -> SecretsFile.list scope file
                      Set =
                        fun id value ->
                            async {
                                let! iv, ciphertext = cipher.Encrypt (SecretsCipher.aadFor id.Scope id.Name) value
                                let timestamp = now ()
                                let entry : SecretEntry =
                                    { Scope = id.Scope
                                      Name = id.Name
                                      CreatedAt = timestamp
                                      UpdatedAt = timestamp
                                      Iv = iv
                                      Ciphertext = ciphertext }
                                file <- SecretsFile.upsert entry file
                                save ()
                                match SecretsFile.list id.Scope file |> List.tryFind (fun m -> m.Id = id) with
                                | Some metadata -> return Ok metadata
                                | None -> return Error "secret vanished during set"
                            }
                      Delete =
                        fun id ->
                            async {
                                let existed = SecretsFile.tryFind id file |> Option.isSome
                                if existed then
                                    file <- SecretsFile.remove id file
                                    save ()
                                return Ok existed
                            }
                      Resolve =
                        fun id ->
                            async {
                                match SecretsFile.tryFind id file with
                                | None -> return Ok None
                                | Some entry ->
                                    let! decrypted = cipher.Decrypt (SecretsCipher.aadFor entry.Scope entry.Name) entry.Iv entry.Ciphertext
                                    return decrypted |> Result.map Some
                            }
                      Durable = Option.isSome persistPath
                      KekMinted = minted
                      EntriesAtOpen = loaded.Entries.Length }
                return Ok store
    }

/// Secret resolution for environment injection (Plan 06): which value a `SecretRef`
/// means for THIS session, in precedence order — the session's own scoped secret, then
/// a user-scoped secret of a user the Manager verified into the session's launch, then
/// the fallback. Every store step passes the same pure policy the control routes use
/// (InjectSecret); a Deny simply means "not this caller's secret — next scope", and
/// only a store FAILURE stops the walk.
module SecretResolution =

    /// What one SecretRef resolution came to — the feature's own observation type; the
    /// `Audit` submodule below adapts it to a log record (`Audit.injectObserver`).
    type InjectOutcome =
        | InjectedFromScope of SecretScope
        | InjectedFromFallback
        | InjectMissed of reason: string

    type Observe = SessionId -> SecretName -> InjectOutcome -> unit

    /// The explicit lowest-precedence fallback: the Manager's OWN process environment —
    /// the pre-Plan-06 "smallest local-dev store" (docs/GAPS.md), kept so existing
    /// flows work unseeded. Inside the Manager's trust boundary; any store entry
    /// shadows it.
    [<Fable.Core.Emit("process.env[$0]")>]
    let private processEnvRaw (name: string) : string = Fable.Core.Util.jsNative

    let processEnv : ResolveSecret =
        fun _sessionId name ->
            async {
                let value = processEnvRaw (SecretName.value name)
                if isNull (box value) then
                    return Error (sprintf "secret '%s' is not available" (SecretName.value name))
                else return Ok value
            }

    /// The scopes a launch may read from, most specific first: the session's own, then
    /// its bound users', then its witnessed peers', then — where the Manager granted it
    /// unattributed access — the deployment's own.
    ///
    /// `LocalScope` sits LAST because it is the least specific owner there is. Generic
    /// secret injection walks it and the policy denies (there is no local rule for
    /// `SecretAction`), so the walk simply moves on; it is here for the connection status
    /// listing, which derives a caller's readable set from this same list.
    let scopesFor (sessionId: SessionId) (users: Set<UserId>) (peers: Set<PeerId>) (local: bool) : SecretScope list =
        SessionScope sessionId
        :: (users |> Set.toList |> List.map UserScope)
        @ (peers |> Set.toList |> List.map PeerScope)
        @ (if local then [ LocalScope ] else [])

    let compose (observe: Observe) (store: SecretStore) (usersOf: SessionId -> Set<UserId>) (peersOf: SessionId -> Set<PeerId>) (localOf: SessionId -> bool) (fallback: ResolveSecret) : ResolveSecret =
        fun sessionId name ->
            async {
                let users = usersOf sessionId
                let peers = peersOf sessionId
                let local = localOf sessionId
                let subject : Subject = { Session = Some sessionId; Users = users; Peers = peers; Local = local }
                let rec walk scopes =
                    async {
                        match scopes with
                        | [] ->
                            // The walk fell through to the fallback: its outcome is the
                            // resolution's outcome. Intermediate deny/miss steps stay
                            // silent — that is the precedence working, not an event.
                            match! fallback sessionId name with
                            | Ok value ->
                                observe sessionId name InjectedFromFallback
                                return Ok value
                            | Error e ->
                                observe sessionId name (InjectMissed e)
                                return Error e
                        | scope :: rest ->
                            let id : SecretId = { Scope = scope; Name = name }
                            let request =
                                { Subject = subject
                                  Action = SecretAction InjectSecret
                                  Resource = SecretResource id }
                            match Policy.authorize request with
                            | Deny _ -> return! walk rest
                            | Permit ->
                                match! store.Resolve id with
                                | Ok (Some value) ->
                                    observe sessionId name (InjectedFromScope scope)
                                    return Ok value
                                | Ok None -> return! walk rest
                                | Error e -> return Error e
                    }
                return! walk (scopesFor sessionId users peers local)
            }

/// The secrets/ABAC feature's audit telemetry (Plan 06). Audit is a cross-cutting concern,
/// but its records are a vertical feature concern: the Manager emits OTel-log-shaped records
/// about its own authority decisions here, in the feature that owns them — secrets operations,
/// policy denies, SecretRef injection, KEK/store lifecycle, user↔launch bindings, and
/// control-channel 401s. Identifiers and names only — no constructor accepts a secret value,
/// KEK material, or control secret, so content cannot leak by type. Records carry `event.name`
/// (`yession.*`) and `service.name = yession-manager`.
///
/// The cross-cutting *convention* is small: a `Record` shape and a `Sink` (a record consumer)
/// any feature could reuse. It lives with secrets — the only feature emitting audit today —
/// rather than in a horizontal audit module; extract it when a second feature needs it.
module Audit =

    /// A log attribute value — string or int (the two the audit records use).
    type LogValue =
        | StringValue of string
        | IntValue of int

    /// An OTel-log-shaped audit record: a human `Body`, a severity number
    /// (9 INFO / 13 WARN / 17 ERROR), and identifier/name attributes. Never a secret value.
    type Record =
        { Body : string
          Severity : int
          Attributes : Map<string, LogValue> }

    /// An audit record consumer — the cross-cutting seam. Built ONCE per Manager (createWithUi).
    type Sink = Record -> unit

    module Severity =
        let info = 9
        let warn = 13
        let error = 17

    let private make (name: string) (severity: int) (attributes: (string * LogValue) list) (body: string) : Record =
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
              "yession.secret.scope_key", StringValue (UserId.value user) ]
        | PeerScope peer ->
            [ "yession.secret.scope", StringValue "peer"
              "yession.secret.scope_key", StringValue (PeerId.value peer) ]
        // No scope_key: the deployment is the owner, so there is no id to name — and an
        // empty key would read as a missing one.
        | LocalScope ->
            [ "yession.secret.scope", StringValue "local" ]

    let private session (sessionId: SessionId) = "yession.session.id", StringValue (SessionId.value sessionId)
    let private outcome ok = "yession.outcome", StringValue (if ok then "ok" else "failed")

    let private secretOp (op: string) (sessionId: SessionId) (id: SecretId) (ok: bool) : Record =
        make
            (sprintf "yession.secret.%s" op)
            (if ok then Severity.info else Severity.warn)
            ([ session sessionId
               "yession.secret.name", StringValue (SecretName.value id.Name)
               outcome ok ]
             @ scopeAttrs id.Scope)
            (sprintf "secret %s %s" op (if ok then "ok" else "failed"))

    let secretSet (sessionId: SessionId) (id: SecretId) (ok: bool) : Record = secretOp "set" sessionId id ok
    let secretDelete (sessionId: SessionId) (id: SecretId) (ok: bool) : Record = secretOp "delete" sessionId id ok

    let secretList (sessionId: SessionId) (scope: SecretScope) (count: int) : Record =
        make "yession.secret.list" Severity.info
            ([ session sessionId; "yession.secret.count", IntValue count ] @ scopeAttrs scope)
            "secret list"

    // Render the inner case name only, so existing secrets log lines keep their wording
    // now that connection actions deny through the same record.
    let private actionLabel (action: Action) : string =
        match action with
        | SecretAction a -> sprintf "%A" a
        | ConnectionAction a -> sprintf "%A" a

    let authzDeny (sessionId: SessionId) (action: Action) (resource: Resource) (reason: string) : Record =
        let resourceAttrs =
            match resource with
            | SecretResource id ->
                ("yession.secret.name", StringValue (SecretName.value id.Name)) :: scopeAttrs id.Scope
            | SecretCollection scope -> scopeAttrs scope
        make "yession.authz.deny" Severity.warn
            ([ session sessionId
               "yession.authz.action", StringValue (actionLabel action)
               "yession.authz.reason", StringValue reason ]
             @ resourceAttrs)
            (sprintf "secrets: DENY %s for session %s: %s" (actionLabel action) (SessionId.value sessionId) reason)

    // Connection-broker records (Plan 08): lifecycle of Manager-brokered external-service
    // credentials. Identifiers, scopes, and kinds only — the observation type these adapt
    // (`Broker.BrokerObservation`) cannot carry token material.
    let private connectionAttrs (id: SecretId) : (string * LogValue) list =
        ("yession.secret.name", StringValue (SecretName.value id.Name)) :: scopeAttrs id.Scope

    let connectionConnected (id: SecretId) (kind: string) : Record =
        make "yession.connection.connected" Severity.info
            (("yession.connection.kind", StringValue kind) :: connectionAttrs id)
            "connection credential stored"

    let connectionDisconnected (id: SecretId) : Record =
        make "yession.connection.disconnected" Severity.info (connectionAttrs id) "connection credential removed"

    let connectionResolved (id: SecretId) (kind: string) (refreshed: bool) : Record =
        make "yession.connection.resolved" Severity.info
            ([ "yession.connection.kind", StringValue kind
               "yession.connection.refreshed", StringValue (if refreshed then "true" else "false") ]
             @ connectionAttrs id)
            "connection credential resolved for an agent turn"

    let connectionRefreshFailed (id: SecretId) (reason: string) : Record =
        make "yession.connection.refresh_failed" Severity.warn
            (("yession.connection.reason", StringValue reason) :: connectionAttrs id)
            "connection credential refresh failed"

    /// The provider refused a credential we hold, as reported by whoever spent it. `warn`
    /// like a failed refresh, and for the same reason: nothing is broken here, but a person
    /// has to do something before the next turn that needs this can run.
    let connectionRejected (id: SecretId) (reason: string) : Record =
        make "yession.connection.rejected" Severity.warn
            (("yession.connection.reason", StringValue reason) :: connectionAttrs id)
            "connection credential refused by the provider"

    let inject (sessionId: SessionId) (name: SecretName) (source: string) : Record =
        make "yession.secret.inject" Severity.info
            [ session sessionId
              "yession.secret.name", StringValue (SecretName.value name)
              "yession.inject.source", StringValue source ]
            "secret injected into environment"

    let injectMiss (sessionId: SessionId) (name: SecretName) (reason: string) : Record =
        make "yession.secret.inject" Severity.warn
            [ session sessionId
              "yession.secret.name", StringValue (SecretName.value name)
              "yession.inject.source", StringValue "none"
              "yession.authz.reason", StringValue reason ]
            "secret injection missed"

    let storeOpen (mode: string) (keystore: string) (kekMinted: bool) (entries: int) : Record =
        make "yession.secrets.store_open" Severity.info
            [ "yession.secrets.mode", StringValue mode
              "yession.secrets.keystore", StringValue keystore
              "yession.secrets.kek", StringValue (if kekMinted then "created" else "loaded")
              "yession.secrets.entries", IntValue entries ]
            (sprintf "secrets store open (%s)" mode)

    /// An in-memory store, and WHY. The severity is the point: a store the operator asked
    /// to be ephemeral (`--secrets ephemeral`) is the deliberate, safer posture and reads
    /// as INFO; one that fell back because this host offered no credential manager is a
    /// degraded deployment and reads as WARN. Logging both as warnings would invert the
    /// signal and train an operator to ignore the line that matters.
    let storeEphemeral (chosen: bool) : Record =
        make "yession.secrets.store_open" (if chosen then Severity.info else Severity.warn)
            [ "yession.secrets.mode", StringValue "ephemeral"
              "yession.secrets.reason", StringValue (if chosen then "operator" else "no-credential-manager") ]
            (if chosen then
                "secrets: --secrets ephemeral — secrets are IN-MEMORY ONLY and die with this Manager"
             else
                "secrets: no OS credential manager available — secrets are IN-MEMORY ONLY and die with this Manager")

    /// A durable file this run will not open. Says only what is true either way — the
    /// CAUSE is carried by the `storeEphemeral` record printed immediately before it, and
    /// the pair reads as one story.
    let storeInaccessible (path: string) : Record =
        make "yession.secrets.store_open" Severity.warn
            [ "yession.secrets.mode", StringValue "ephemeral"
              "yession.secrets.path", StringValue path ]
            (sprintf "secrets: %s exists but this run's store is in-memory — its entries stay unread (and untouched)" path)

    let storeOpenFailed (kind: string) (detail: string) : Record =
        make "yession.secrets.store_open_failed" Severity.error
            [ "yession.secrets.reason", StringValue kind ]
            detail

    let bindingRecorded (sessionId: SessionId) (subject: UserId) : Record =
        make "yession.auth.binding_recorded" Severity.info
            [ session sessionId; "yession.auth.sub", StringValue (UserId.value subject) ]
            "user verified into launch"

    let bindingRevoked (sessionId: SessionId) : Record =
        make "yession.auth.binding_revoked" Severity.info
            [ session sessionId ]
            "user bindings revoked with launch"

    let controlUnauthorized (path: string) : Record =
        make "yession.control.unauthorized" Severity.warn
            [ "yession.http.path", StringValue path ]
            "invalid control secret"

    let private severityLabel (severity: int) : string =
        if severity = Severity.info then "INFO"
        elif severity = Severity.warn then "WARN"
        elif severity = Severity.error then "ERROR"
        else string severity

    /// One greppable line, deterministic (Map iterates key-sorted): severity, event
    /// name, attributes (minus the two that lead the line), then the human body.
    let format (r: Record) : string =
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

    /// Map an injection walk's outcome (SecretResolution.InjectOutcome) to a record — the
    /// feature's own observation type, adapted to an audit record right here beside it.
    let injectObserver (sink: Sink) : SecretResolution.Observe =
        fun sessionId name outcome ->
            match outcome with
            | SecretResolution.InjectedFromScope (SessionScope _) -> sink (inject sessionId name "session")
            | SecretResolution.InjectedFromScope (UserScope _) -> sink (inject sessionId name "user")
            | SecretResolution.InjectedFromScope (PeerScope _) -> sink (inject sessionId name "peer")
            // Unreachable while the policy has no local rule for `SecretAction` — generic
            // injection never resolves at this scope. Recorded honestly rather than
            // silently, so the day a rule is added the audit already says so.
            | SecretResolution.InjectedFromScope LocalScope -> sink (inject sessionId name "local")
            | SecretResolution.InjectedFromFallback -> sink (inject sessionId name "env")
            | SecretResolution.InjectMissed reason -> sink (injectMiss sessionId name reason)
