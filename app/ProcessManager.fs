module Yession.Host.ProcessManager

// The Manager as a process supervisor (Phase 4, Step 23): sessions are child OS
// processes, so a crashing session can never take the Manager or its siblings down.
// The durable registry lives behind ManagerStore (Step 22); runtime state (child
// handle, port) is memory-only and reconciled at boot — after a Manager restart every
// session is simply stopped, and RESUME IS JUST LAUNCH: spawning over the same data
// directory replays the event log and doc sidecar (Step 19).
//
// Not a singleton by assumption: everything lives under this instance's data
// directory and session ports default to OS-assigned. Two Managers over the SAME data
// directory are unsupported (documented; a lock arrives with SQLite).

open System
open Yession.Domain
open Yession.Manager
open Yession.Oidc

/// A session's runtime status — never persisted.
type SessionStatus =
    | NotRunning
    | Running of port: int * pid: int
    /// The child exited without the Manager stopping it (crash or self-exit).
    | Exited of code: int option

type SessionView =
    { Record : SessionRecord
      Status : SessionStatus }

type ProcessManager =
    { /// Register a new session (durable): id, display name. Does not launch.
      CreateSession : string -> string -> Result<SessionRecord, string>
      /// Launch (or resume — same thing) a registered session; resolves with its port.
      Launch : SessionId -> Async<Result<int, string>>
      /// Stop a running session (SIGTERM, SIGKILL after a grace period).
      Stop : SessionId -> Async<Result<unit, string>>
      /// Every registered session with its runtime status.
      Sessions : unit -> SessionView list
      /// Set a session's display name (the reported collaborative title); durable, and a
      /// no-op for unknown sessions or unchanged names.
      SetDisplayName : SessionId -> string -> unit
      /// Resolves when the session's running child exits (immediately if none runs).
      WaitForExit : SessionId -> Async<unit>
      TryFind : SessionId -> SessionView option
      /// Push a notification down to a running session (the reverse leg of the control
      /// RPC): fans out over that session's live `/control/notifications` subscriptions.
      /// A no-op for a session that is not running or has no control channel.
      Notify : SessionId -> SessionNotification -> unit
      /// Announce the current MCP tool list to every session (the `/control/mcp` reverse
      /// leg): replaces the retained list and pushes it to all live subscribers, and every
      /// session that subscribes later receives it as its initial snapshot.
      PublishMcpTools : McpToolList -> unit
      /// Users the Manager verified into the session's live launch at ID-token
      /// issuance (Plan 06). Empty for a stopped session or before any login —
      /// bindings die with the launch.
      UsersOf : SessionId -> Set<UserSubject>
      /// Session-scoped secret resolution for environment injection (Plan 06):
      /// store-backed precedence (session scope, then bound users' scopes, then the
      /// Manager's process env) when a store is configured; bare process env otherwise.
      /// Hand this to a container backend — values resolve at container start, never
      /// over the control channel.
      ResolveSecret : SecretStore.ResolveSecret
      /// The Manager's own HTTP endpoint (control RPC + management UI), when started.
      EndpointPort : int option
      /// Stop every running child and the Manager endpoint (Manager shutdown).
      StopAll : unit -> Async<unit> }

type Options =
    { /// This Manager instance's data directory (state file + session stores).
      DataDir : string
      /// The command that runs a session process — the `yession-session` binary in the
      /// product, `node <SessionMain.js>` in development and tests.
      SessionCommand : string
      SessionArgs : string list
      /// Fixed port for a launched session; None = OS-assigned per launch.
      SessionPort : int option
      /// How long a child may take to print its readiness line.
      LaunchTimeoutMs : int
      /// SIGTERM → SIGKILL escalation grace.
      StopGraceMs : int
      /// Environment authority (Step 24): grants session-scoped capabilities, served
      /// to children over the control endpoint with a per-launch secret. None =
      /// sessions run environment-less.
      Grant : (SessionId -> SessionEnvironmentCapabilities) option
      /// Fixed port for the Manager's own endpoint (control + management UI);
      /// None = OS-assigned. A management UI wants a bookmarkable address, so the
      /// product default is fixed — a second Manager instance must choose its own
      /// (the bind fails loudly on conflict, never a silent fallback).
      ManagerPort : int option
      /// The Manager's own telemetry sink for session-lifecycle signals (launch/exit). The
      /// Manager is a direct OTel emitter; this is its `Log`. Default = ignore. Sessions emit
      /// their own telemetry directly — the Manager does not collect from them; it only passes
      /// the standard `OTEL_*` env through to each child (Spawn merges over `process.env`) and
      /// adapts the child's identity (service.name/instance.id).
      OnEvent : string -> (string * obj) list -> unit
      /// How the OIDC provider authenticates the human at /authorize. None = the
      /// built-in trust-localhost strategy; an upstream OIDC integration is a
      /// different strategy value, not a different Manager.
      Strategy : AuthenticationStrategy option
      /// Secrets (Plan 06): how the Manager's secret store is keyed. None = the
      /// feature is off — the secrets routes answer 403 and injection sees only the
      /// process-env fallback (the pre-Plan-06 behaviour).
      Secrets : SecretsBacking option }

/// How the secret store is keyed on this host.
and SecretsBacking =
    /// A usable OS credential manager holds the KEK; the encrypted store lives at
    /// <DataDir>/secrets.json.
    | DurableSecrets of KeyStore.KeyStore
    /// No credential manager: the store runs in memory under a per-boot random key
    /// and dies with the Manager. Loudly logged at boot; never a plaintext key file.
    | EphemeralSecrets

module Options =
    let defaults (dataDir: string) (sessionCommand: string) (sessionArgs: string list) : Options =
        { DataDir = dataDir
          SessionCommand = sessionCommand
          SessionArgs = sessionArgs
          SessionPort = None
          LaunchTimeoutMs = 15000
          StopGraceMs = 3000
          Grant = None
          ManagerPort = None
          OnEvent = (fun _ _ -> ())
          Strategy = None
          Secrets = None }

[<Fable.Core.Emit("setTimeout($1, $0)")>]
let private setTimeout (ms: int) (callback: unit -> unit) : obj = Fable.Core.Util.jsNative

let private clock () = DateTimeOffset.UtcNow

/// The secrets handlers (Plan 06): the ONLY place a verified AuthzSubject is built, so
/// the route arms stay policy-free. Every deny is logged (subject/action/scope — never
/// values); every permitted call goes straight to the store. Module-level so the
/// authorization matrix is testable over a bare control server.
let secretsApiFor (audit: SecretStore.Audit.Sink) (store: SecretStore.SecretStore) : Control.SecretsApi =
    let authorize (caller: Control.ControlCaller) (action: SecretAction) (resource: AuthzResource) =
        let request =
            { Subject = { Session = Some caller.SessionId; Users = caller.Users }
              Action = SecretAction action
              Resource = resource }
        match Policy.authorize request with
        | Permit -> Ok ()
        | Deny reason ->
            audit (SecretStore.Audit.authzDeny caller.SessionId action resource reason)
            Error (Control.SecretsDenied reason)
    { Control.SecretsApi.Set =
        fun caller request ->
            async {
                let id : SecretId = { Scope = request.Scope; Name = request.Name }
                match authorize caller SetSecret (SecretResource id) with
                | Error e -> return Error e
                | Ok () ->
                    match! store.Set id request.Value with
                    | Ok metadata ->
                        audit (SecretStore.Audit.secretSet caller.SessionId id true)
                        return Ok metadata
                    | Error e ->
                        audit (SecretStore.Audit.secretSet caller.SessionId id false)
                        return Error (Control.SecretsFailed e)
            }
      List =
        fun caller request ->
            async {
                match authorize caller ListSecrets (SecretCollection request.Scope) with
                | Error e -> return Error e
                | Ok () ->
                    let listed = store.List request.Scope
                    audit (SecretStore.Audit.secretList caller.SessionId request.Scope listed.Length)
                    return Ok listed
            }
      Delete =
        fun caller request ->
            async {
                let id : SecretId = { Scope = request.Scope; Name = request.Name }
                match authorize caller DeleteSecret (SecretResource id) with
                | Error e -> return Error e
                | Ok () ->
                    match! store.Delete id with
                    | Ok existed ->
                        audit (SecretStore.Audit.secretDelete caller.SessionId id true)
                        return Ok existed
                    | Error e ->
                        audit (SecretStore.Audit.secretDelete caller.SessionId id false)
                        return Error (Control.SecretsFailed e)
            } }

/// Create the Manager. `ui` is the management surface (Step 25): a route handler that
/// closes over the Manager itself, sharing the control endpoint's server.
let createWithUi
    (options: Options)
    (ui: (ProcessManager -> Interop.IncomingMessage -> Interop.ServerResponse -> bool) option)
    : Async<ProcessManager> =
  async {
    let statePath = sprintf "%s/manager.json" options.DataDir
    let mutable state = ManagerStore.load statePath

    // Runtime-only: the child handle per running session, and the last observed exit
    // for sessions that died without a Stop.
    let mutable children : Map<string, Spawn.RunningChild * int> = Map.empty
    let mutable lastExit : Map<string, int option> = Map.empty
    // Stops in flight: their exits are expected, not crashes.
    let mutable stopping : Set<string> = Set.empty

    // The control endpoint (Step 24): per-launch secrets resolve to the capabilities
    // the Manager granted that launch — the RPC equivalent of the Step 11 closure. A
    // secret dies with its launch.
    let mutable secrets : Map<string, SessionEnvironmentCapabilities> = Map.empty
    // The same per-launch secret also names which session is reporting its display name
    // (the collaborative title); this map lives and dies with the secret.
    let mutable secretSessions : Map<string, SessionId> = Map.empty
    // Users the Manager verified into a LAUNCH (Plan 06): recorded at ID-token issuance,
    // keyed by the per-launch control secret so the binding dies with the launch, exactly
    // like the client registration it derives from. Durable secrets, per-login access.
    let mutable launchUsers : Map<string, Set<UserSubject>> = Map.empty

    // Manager→Session notifications (the reverse leg): live subscriber sinks keyed by the
    // same per-launch secret, so a session's stream dies exactly when its launch does.
    let notifications = NotificationHub.create ()

    // The MCP tool stream (the second reverse leg): a Manager-level retained list broadcast to
    // every subscribed session, so all sessions see the same available MCP services.
    let mcp = McpHub.create ()

    // Push a notification to a session: fan out over every live secret that names it
    // (in practice one — a running session has a single launch). Inert for a session
    // that is not running or whose launch granted no control channel.
    let notify (sessionId: SessionId) (notification: SessionNotification) : unit =
        secretSessions
        |> Map.iter (fun secret sid -> if sid = sessionId then notifications.NotifySecret secret notification)

    // Update a session's display name (the reported title). Idempotent: unknown sessions and
    // no-op renames are skipped, and the registry write is durable before it is visible.
    let setDisplayName (sessionId: SessionId) (displayName: string) : unit =
        match ManagerState.tryFind sessionId state with
        | Some record when record.DisplayName <> displayName ->
            state <- ManagerState.setDisplayName sessionId displayName state
            ManagerStore.save statePath state
        | _ -> ()

    // The control channel's name report: the secret identifies the reporting session; a
    // blank name is ignored (the list keeps the registered name until a real title arrives).
    let reportName (secret: string) (name: string) : Async<Result<unit, string>> =
        async {
            match Map.tryFind secret secretSessions with
            | Some sessionId ->
                let trimmed = name.Trim ()
                if trimmed <> "" then setDisplayName sessionId trimmed
                return Ok ()
            | None -> return Error "invalid control secret"
        }

    // The UI handler closes over the Manager record, which exists only after this
    // function returns — route through a slot the record fills in below. Requests
    // cannot arrive before then in practice; a too-early one gets a 503.
    let mutable self : ProcessManager option = None

    // The Manager's endpoint always runs: beyond environment authority it now carries
    // the OIDC provider every session needs to authorize its users, so there is no
    // endpoint-less mode. The port is only known once the server listens, and the
    // provider reads the issuer lazily, so the mutable slot resolves cleanly.
    let mutable endpointUrl : string option = None
    let issuerOf () = defaultArg endpointUrl ""
    // The Manager's audit sink (Plan 06 telemetry): one greppable audit line to stdout for
    // each authority decision. (Session telemetry is emitted directly by each process now —
    // there is no Manager-side collector; forwarding audit to a collector too is a follow-up.)
    let audit : SecretStore.Audit.Sink = SecretStore.Audit.stdout

    // The secret store (Plan 06). A corrupt durable store fails the boot loudly — it
    // must never look empty. The ephemeral mode warns loudly and leaves any durable
    // file from a previous run untouched (inaccessible, never deleted).
    let secretsPath = sprintf "%s/secrets.json" options.DataDir
    let! secretStore =
        async {
            match options.Secrets with
            | None -> return None
            | Some (DurableSecrets keyStore) ->
                match! SecretStore.openStore (Some secretsPath) keyStore with
                | Ok store ->
                    audit (SecretStore.Audit.storeOpen "durable" keyStore.Name store.KekMinted store.EntriesAtOpen)
                    return Some store
                | Error e ->
                    audit (SecretStore.Audit.storeOpenFailed (SecretStore.OpenError.kind e) (SecretStore.OpenError.describe e))
                    return failwithf "secrets store: %s" (SecretStore.OpenError.describe e)
            | Some EphemeralSecrets ->
                audit SecretStore.Audit.storeEphemeral
                if Fs.exists secretsPath then
                    audit (SecretStore.Audit.storeInaccessible secretsPath)
                match! SecretStore.openStore None (KeyStore.random ()) with
                | Ok store ->
                    audit (SecretStore.Audit.storeOpen "ephemeral" "in-memory" store.KekMinted store.EntriesAtOpen)
                    return Some store
                | Error e -> return failwithf "secrets store (ephemeral): %s" (SecretStore.OpenError.describe e)
        }

    let recordTokenIssued (controlSecret: string) (sessionId: SessionId) (subject: UserSubject) : unit =
        // Guarded by the live secret: a token redeemed in the same instant a launch
        // dies must not resurrect its authority.
        if Map.containsKey controlSecret secretSessions then
            let existing = Map.tryFind controlSecret launchUsers |> Option.defaultValue Set.empty
            launchUsers <- Map.add controlSecret (Set.add subject existing) launchUsers
            audit (SecretStore.Audit.bindingRecorded sessionId subject)
    let! provider = ManagerOidc.create issuerOf (defaultArg options.Strategy Strategy.localhost) recordTokenIssued

    // What a control secret resolves to: the launch's session plus its (optional)
    // environment grant. Registration works for every launch; environment routes 403
    // without a grant.
    let resolveCaller (secret: string) : Control.ControlCaller option =
        Map.tryFind secret secretSessions
        |> Option.map (fun sessionId ->
            { Control.ControlCaller.SessionId = sessionId
              Capabilities = Map.tryFind secret secrets
              Users = Map.tryFind secret launchUsers |> Option.defaultValue Set.empty })

    let secretsApi : Control.SecretsApi option = secretStore |> Option.map (secretsApiFor audit)

    // The union of user bindings across a session's live launches (in practice one).
    let usersOf (sessionId: SessionId) : Set<UserSubject> =
        secretSessions
        |> Map.fold
            (fun acc secret sid ->
                if sid = sessionId then
                    Set.union acc (Map.tryFind secret launchUsers |> Option.defaultValue Set.empty)
                else acc)
            Set.empty

    let! controlServer =
        async {
            let handler (req: Interop.IncomingMessage) (res: Interop.ServerResponse) =
                let handled =
                    Control.tryHandle resolveCaller reportName notifications.Register mcp.Register provider.RegisterClient secretsApi (fun path -> audit (SecretStore.Audit.controlUnauthorized path)) req res
                    || provider.TryHandle req res
                    || (match ui, self with
                        | Some handle, Some pm -> handle pm req res
                        | Some _, None ->
                            res.writeHead (503, Fable.Core.JsInterop.createObj [ "content-type", box "text/plain" ]) |> ignore
                            res.``end`` "starting"
                            true
                        | None, _ -> false)
                if not handled then
                    res.writeHead (404, Fable.Core.JsInterop.createObj [ "content-type", box "text/plain" ]) |> ignore
                    res.``end`` "not found"
            let server = Interop.createServer handler
            let! listening =
                Async.FromContinuations (fun (cont, _, _) ->
                    server.listen (defaultArg options.ManagerPort 0, "127.0.0.1", fun () -> cont server) |> ignore)
            endpointUrl <- Some (sprintf "http://127.0.0.1:%d" (Interop.serverPort listening))
            return Some listening
        }
    let controlUrl () = endpointUrl

    let statusOf (record: SessionRecord) : SessionStatus =
        let key = SessionId.value record.SessionId
        match Map.tryFind key children with
        | Some (child, port) -> Running (port, child.Pid)
        | None ->
            match Map.tryFind key lastExit with
            | Some code -> Exited code
            | None -> NotRunning

    let createSession (sessionId: string) (displayName: string) : Result<SessionRecord, string> =
        match SessionId.create sessionId with
        | Error e -> Error e
        | Ok id ->
            let record =
                { SessionId = id
                  DisplayName = if displayName.Trim().Length > 0 then displayName.Trim () else sessionId
                  CreatedAt = clock ()
                  DataDir = sprintf "sessions/%s" (SessionId.value id) }
            match ManagerState.addSession record state with
            | Error e -> Error e
            | Ok next ->
                // Durable before visible: the registry write precedes any use.
                ManagerStore.save statePath next
                state <- next
                Ok record

    let launch (sessionId: SessionId) : Async<Result<int, string>> =
        async {
            let key = SessionId.value sessionId
            match ManagerState.tryFind sessionId state with
            | None -> return Error (sprintf "unknown session %s" key)
            | Some _ when Map.containsKey key children -> return Error (sprintf "session %s is already running" key)
            | Some record ->
                // Step 24: mint the per-launch secret — every launch gets one (it now
                // authenticates OAuth client registration as well as environment calls).
                // The capability grant stays separate: only granted launches enter
                // `secrets`; the session scope is established HERE, by the Manager.
                let controlEnv =
                    match controlUrl () with
                    | Some url ->
                        let secret = Interop.randomSecret ()
                        options.Grant
                        |> Option.iter (fun grant -> secrets <- Map.add secret (grant record.SessionId) secrets)
                        secretSessions <- Map.add secret record.SessionId secretSessions
                        [ "YESSION_CONTROL_URL", url; "YESSION_CONTROL_SECRET", secret ], Some secret
                    | None -> [], None
                // Telemetry: the child is a direct OTel emitter. The Manager does NOT collect;
                // it passes its own OTEL_* environment through (Spawn merges over `process.env`,
                // so OTEL_LOGS_EXPORTER / OTEL_EXPORTER_OTLP_* flow to the child unchanged) and
                // overrides only the child's IDENTITY — service.name=yession-session plus
                // service.instance.id=<sessionId>, merged over any operator OTEL_RESOURCE_ATTRIBUTES
                // so the child never inherits the Manager's own service.name.
                let telemetryIdentityEnv =
                    let sid = SessionId.value record.SessionId
                    let inherited = Interop.envOr "OTEL_RESOURCE_ATTRIBUTES" ""
                    let sessionAttrs =
                        sprintf "service.instance.id=%s,service.namespace=yession,yession.session.id=%s" sid sid
                    let resourceAttrs =
                        if inherited.Trim().Length = 0 then sessionAttrs else inherited + "," + sessionAttrs
                    [ "OTEL_SERVICE_NAME", "yession-session"
                      "OTEL_RESOURCE_ATTRIBUTES", resourceAttrs ]
                let env =
                    [ "YESSION_SESSION", SessionId.value record.SessionId
                      "YESSION_SESSION_DATA", sprintf "%s/%s" options.DataDir record.DataDir
                      "YESSION_PORT", string (defaultArg options.SessionPort 0)
                      // The child watches its stdin and exits when this Manager dies.
                      "YESSION_PARENT_GUARD", "1" ]
                    @ fst controlEnv
                    @ telemetryIdentityEnv
                let revokeSecret () =
                    (match snd controlEnv with
                     | Some secret ->
                         // Audit the binding teardown only when a binding existed (an
                         // ordinary stop of a never-logged-in session is not an event).
                         (match Map.tryFind secret secretSessions with
                          | Some sessionId when Map.containsKey secret launchUsers ->
                              audit (SecretStore.Audit.bindingRevoked sessionId)
                          | _ -> ())
                         secrets <- Map.remove secret secrets
                         secretSessions <- Map.remove secret secretSessions
                         // The launch's notification subscriptions, OAuth client
                         // registration, and user bindings die with its authority.
                         notifications.Drop secret
                         provider.RevokeByControlSecret secret
                         launchUsers <- Map.remove secret launchUsers
                     | None -> ())
                match! Spawn.launch options.SessionCommand options.SessionArgs env options.LaunchTimeoutMs with
                | Error reason ->
                    revokeSecret ()
                    return Error reason
                | Ok (child, port) ->
                    children <- Map.add key (child, port) children
                    lastExit <- Map.remove key lastExit
                    // The Manager emits its own lifecycle telemetry directly (session launched).
                    options.OnEvent "session launched"
                        [ "yession.session.id", box key; "yession.session.port", box port ]
                    child.OnExit (fun code ->
                        children <- Map.remove key children
                        // The launch's authority dies with it.
                        revokeSecret ()
                        // A stop's exit is the expected outcome, not a crash to report.
                        let stopped = Set.contains key stopping
                        if stopped then stopping <- Set.remove key stopping
                        else lastExit <- Map.add key code lastExit
                        options.OnEvent "session exited"
                            [ "yession.session.id", box key
                              "yession.session.exit_code", box (defaultArg code -1)
                              "yession.session.stopped", box stopped ])
                    return Ok port
        }

    let stop (sessionId: SessionId) : Async<Result<unit, string>> =
        async {
            let key = SessionId.value sessionId
            match Map.tryFind key children with
            | None -> return Error (sprintf "session %s is not running" key)
            | Some (child, _) ->
                stopping <- Set.add key stopping
                return!
                    Async.FromContinuations (fun (cont, _, _) ->
                        child.OnExit (fun _ -> cont (Ok ()))
                        child.Terminate ()
                        setTimeout options.StopGraceMs (fun () ->
                            if not (child.HasExited ()) then child.Kill ())
                        |> ignore)
        }

    let pm =
        { CreateSession = createSession
          Launch = launch
          Stop = stop
          Sessions = fun () -> state.Sessions |> List.map (fun r -> { Record = r; Status = statusOf r })
          SetDisplayName = setDisplayName
          WaitForExit =
            fun sessionId ->
                match Map.tryFind (SessionId.value sessionId) children with
                | None -> async { return () }
                | Some (child, _) ->
                    Async.FromContinuations (fun (cont, _, _) -> child.OnExit (fun _ -> cont ()))
          TryFind =
            fun sessionId ->
                ManagerState.tryFind sessionId state
                |> Option.map (fun r -> { Record = r; Status = statusOf r })
          Notify = notify
          PublishMcpTools = mcp.Publish
          UsersOf = usersOf
          ResolveSecret =
            match secretStore with
            | Some store -> SecretStore.SecretResolution.compose (SecretStore.Audit.injectObserver audit) store usersOf SecretStore.SecretResolution.processEnv
            | None -> SecretStore.SecretResolution.processEnv
          EndpointPort = controlServer |> Option.map Interop.serverPort
          StopAll =
            fun () ->
                async {
                    for record in state.Sessions do
                        if Map.containsKey (SessionId.value record.SessionId) children then
                            let! _ = stop record.SessionId
                            ()
                    controlServer |> Option.iter (fun s -> s.close ignore)
                } }
    self <- Some pm
    return pm
  }

/// `createWithUi` without a management surface.
let create (options: Options) : Async<ProcessManager> = createWithUi options None
