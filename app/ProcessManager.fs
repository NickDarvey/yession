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

/// The session registry's wire view (docs/plans/09): the RUNNING sessions only, each with the
/// port and pid a serving binding needs to reach it. A pure projection of the published views, so
/// `/sessions/stream` and its tests share one definition of what the registry announces — and the
/// Manager publishes the views once, whatever shape a given consumer wants.
let registryFrameOf (views: SessionView list) : ControlWire.SessionRegistryFrame =
    { Sessions =
        views
        |> List.choose (fun view ->
            match view.Status with
            | Running (port, pid) ->
                Some
                    { ControlWire.SessionRegistryEntry.Id = view.Record.SessionId
                      Name = view.Record.DisplayName
                      Port = port
                      Pid = pid }
            | NotRunning
            | Exited _ -> None) }

type ProcessManager =
    { /// Register a new session (durable): id, display name. Does not launch.
      CreateSession : string -> string -> Result<SessionRecord, string>
      /// Launch (or resume — same thing) a registered session; resolves with its port.
      Launch : SessionId -> Async<Result<int, string>>
      /// Stop a running session (SIGTERM, SIGKILL after a grace period).
      Stop : SessionId -> Async<Result<unit, string>>
      /// Every registered session with its runtime status.
      Sessions : unit -> SessionView list
      /// Subscribe to session changes: the sink receives every session with its status
      /// immediately, then a fresh full list on every launch, exit, and display-name change.
      /// The VIEWS, not a wire frame — `/sessions/stream` projects them to the registry's
      /// Running set (`registryFrameOf`), the management page renders them as its table, and
      /// one publish serves both. Returns an unsubscribe.
      SubscribeSessions : Subscribe<SessionView list>
      /// Set a session's display name (the reported collaborative title); durable, and a
      /// no-op for unknown sessions or unchanged names.
      SetDisplayName : SessionId -> string -> unit
      /// Resolves when the session's running child exits (immediately if none runs).
      WaitForExit : SessionId -> Async<unit>
      TryFind : SessionId -> SessionView option
      /// Push a notification down to a running session (the reverse leg of the control
      /// RPC): fans out over that session's live `/control/notifications` subscriptions.
      /// A no-op for a session that is not running or has no control channel.
      Notify : SessionId -> Sink<SessionNotification>
      /// Announce the current MCP tool list to every session (the `/control/mcp` reverse
      /// leg): replaces the retained list and pushes it to all live subscribers, and every
      /// session that subscribes later receives it as its initial snapshot.
      PublishMcpTools : Sink<McpToolList>
      /// Users the Manager verified into the session's live launch at ID-token
      /// issuance (Plan 06). Empty for a stopped session or before any login —
      /// bindings die with the launch.
      UsersOf : SessionId -> Set<UserId>
      /// Peers the Manager witnessed into the session's live launch at ID-token
      /// issuance (docs/plans/07): the browser's peer id rode the authorize bounce.
      /// Same lifetime as UsersOf.
      PeersOf : SessionId -> Set<PeerId>
      /// Session-scoped secret resolution for environment injection (Plan 06):
      /// store-backed precedence (session scope, then bound users' scopes, then the
      /// Manager's process env) when a store is configured; bare process env otherwise.
      /// Hand this to a container backend — values resolve at container start, never
      /// over the control channel.
      ResolveSecret : SecretStore.ResolveSecret
      /// The Manager's own HTTP endpoint (control RPC + management UI), when started.
      EndpointPort : int option
      /// How this deployment is reached from outside (docs/plans/09). The management UI
      /// reads it to render each session's open link, so the address a human clicks and
      /// the redirect URI that session registered come from one declaration.
      Public : PublicAccess
      /// Stop every running child and the Manager endpoint (Manager shutdown).
      StopAll : unit -> Async<unit> }

type Options =
    { /// This Manager instance's data directory (state file + session stores).
      DataDir : string
      /// The command that runs a session process — the `yession-session` binary in the
      /// product, `node <SessionMain.js>` in development and tests.
      SessionCommand : string
      SessionArgs : string list
      /// How a launched session's port is chosen (Plan 11). `Ephemeral` is the default and
      /// the historical behaviour: the OS assigns one per launch. `Pinned` draws a port
      /// from a reserved range at CREATION and reuses it for the life of the session, so
      /// the session's origin — and the client-side storage a browser partitions by it —
      /// survives a stop, a reap, and a resume.
      SessionPorts : SessionPorts
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
      /// How this deployment is reached from outside (docs/plans/07, docs/plans/09): the
      /// Manager's public origin — its OIDC issuer and every URL derived from it — and
      /// where each session's port is reachable. `Loopback` is the single-machine
      /// default: the Manager is its own loopback endpoint URL and sessions answer at
      /// their loopback ports.
      Public : PublicAccess
      /// The Manager's own telemetry sink for session-lifecycle signals (launch/exit). The
      /// Manager is a direct OTel emitter; this is its `Log`. Default = ignore. Sessions emit
      /// their own telemetry directly — the Manager does not collect from them; it only passes
      /// the standard `OTEL_*` env through to each child (Spawn merges over `process.env`) and
      /// adapts the child's identity (service.name/instance.id).
      OnEvent : string -> (string * obj) list -> unit
      /// How the humans at this Manager's endpoint are authenticated: /authorize for
      /// the OIDC bounce, and every management-UI request (docs/plans/07). None = the
      /// deny-everything strategy — nothing authenticates until the operator chooses
      /// (`Strategy.localhost` for a single-machine deployment, `Strategy.trustedHeaders`
      /// behind an authenticating proxy).
      Strategy : AuthenticationStrategy option
      /// Secrets (Plan 06): how the Manager's secret store is keyed. None = the
      /// feature is off — the secrets routes answer 403 and injection sees only the
      /// process-env fallback (the pre-Plan-06 behaviour).
      Secrets : SecretsBacking option
      /// How long a session may go without being in use before the Manager stops it
      /// (Plan 11). None = never, the default: reaping is an explicit operator choice,
      /// because it trades a launch on the next visit for everything an idle session
      /// holds — and, on a deployment that tracks a fast-moving build, for sessions that
      /// come back on the new one without the Manager having to restart.
      IdleTimeout : TimeSpan option }

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
          SessionPorts = Ephemeral
          LaunchTimeoutMs = 15000
          StopGraceMs = 3000
          Grant = None
          ManagerPort = None
          Public = Loopback
          OnEvent = (fun _ _ -> ())
          Strategy = None
          Secrets = None
          IdleTimeout = None }

[<Fable.Core.Emit("setTimeout($1, $0)")>]
let private setTimeout (ms: int) (callback: unit -> unit) : obj = Fable.Core.Util.jsNative

[<Fable.Core.Emit("setInterval($1, $0)")>]
let private setInterval (ms: int) (callback: unit -> unit) : obj = Fable.Core.Util.jsNative

let private clock () = DateTimeOffset.UtcNow

/// How often to look for sessions to reap, derived from the window rather than configured
/// separately: a quarter of it, so the worst-case overshoot is a quarter of a window and
/// there is no second setting to hold a contradictory value. Clamped so a very short window
/// cannot spin and a very long one still checks within the minute.
let private sweepIntervalMsFor (timeout: TimeSpan) : int =
    int (max 5000.0 (min 60000.0 (timeout.TotalMilliseconds / 4.0)))

/// The secrets handlers (Plan 06): the ONLY place a verified AuthzSubject is built, so
/// the route arms stay policy-free. Every deny is logged (subject/action/scope — never
/// values); every permitted call goes straight to the store. Module-level so the
/// authorization matrix is testable over a bare control server.
let secretsApiFor (audit: SecretStore.Audit.Sink) (store: SecretStore.SecretStore) : Control.SecretsApi =
    let authorize (caller: Control.ControlCaller) (action: SecretAction) (resource: AuthzResource) =
        let request =
            { Subject = { Session = Some caller.SessionId; Users = caller.Users; Peers = caller.Peers }
              Action = SecretAction action
              Resource = resource }
        match Policy.authorize request with
        | Permit -> Ok ()
        | Deny reason ->
            audit (SecretStore.Audit.authzDeny caller.SessionId (SecretAction action) resource reason)
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

/// The connection-broker handlers (Plan 08): same discipline as `secretsApiFor` — the
/// verified AuthzSubject is built here and nowhere else, every deny audited, every
/// permitted call delegated to the broker. `Status` needs no policy check: its targets
/// are DERIVED from the caller's own bound scopes, so it can only ever list what the
/// caller could read. Module-level so the authorization matrix is testable over a bare
/// control server.
let connectionsApiFor
    (audit: SecretStore.Audit.Sink)
    (store: SecretStore.SecretStore)
    (broker: Broker.BrokerService)
    : Control.ConnectionsApi =
    let authorize (caller: Control.ControlCaller) (action: ConnectionAction) (target: SecretId) =
        let request =
            { Subject = { Session = Some caller.SessionId; Users = caller.Users; Peers = caller.Peers }
              Action = ConnectionAction action
              Resource = SecretResource target }
        match Policy.authorize request with
        | Permit -> Ok ()
        | Deny reason ->
            audit (SecretStore.Audit.authzDeny caller.SessionId (ConnectionAction action) (SecretResource target) reason)
            Error (Control.SecretsDenied reason)
    let run (outcome: Async<Result<'a, string>>) : Async<Result<'a, Control.SecretsError>> =
        async {
            match! outcome with
            | Ok value -> return Ok value
            | Error e -> return Error (Control.SecretsFailed e)
        }
    { Control.ConnectionsApi.Begin =
        fun caller request ->
            async {
                match authorize caller ConnectCredential request.Target with
                | Error e -> return Error e
                | Ok () -> return! run (broker.Begin request)
            }
      Complete =
        fun caller request ->
            async {
                match authorize caller ConnectCredential request.Target with
                | Error e -> return Error e
                | Ok () -> return! run (broker.Complete request.Target request.Code)
            }
      Put =
        fun caller request ->
            async {
                match authorize caller ConnectCredential request.Target with
                | Error e -> return Error e
                | Ok () -> return! run (broker.Put request.Target request.Value)
            }
      Disconnect =
        fun caller request ->
            async {
                match authorize caller DisconnectCredential request.Target with
                | Error e -> return Error e
                | Ok () ->
                    let! outcome = run (broker.Disconnect request.Target)
                    return outcome |> Result.map (fun existed -> { ControlWire.ConnectionDisconnectResponse.Disconnected = existed })
            }
      Resolve =
        fun caller request ->
            async {
                match authorize caller ResolveCredential request.Target with
                | Error e -> return Error e
                | Ok () ->
                    let! outcome = run (broker.Resolve request.Target)
                    return
                        outcome
                        |> Result.map (fun (kind, value) ->
                            { ControlWire.ConnectionResolveResponse.Kind = kind
                              ControlWire.ConnectionResolveResponse.Value = value })
            }
      Status =
        fun caller ->
            // Every connection in the caller's own readable scopes (its session, its
            // bound users, its witnessed peers) — the same walk injection uses. Entries
            // that do not decode as broker envelopes are generic secrets and stay out.
            SecretStore.SecretResolution.scopesFor caller.SessionId caller.Users caller.Peers
            |> List.collect (fun scope -> store.List scope |> List.map (fun m -> m.Id))
            |> broker.StatusOf }

/// Create the Manager. `ui` is the management surface (Step 25): a route handler that
/// closes over the Manager itself, sharing the control endpoint's server. It receives
/// the Manager's per-request authenticator (the configured strategy, docs/plans/07) so
/// every UI route is gated by the same trust rule as /authorize.
let createWithUi
    (options: Options)
    (ui: (ProcessManager -> (Interop.IncomingMessage -> Async<AuthenticationOutcome>) -> Interop.IncomingMessage -> Interop.ServerResponse -> bool) option)
    : Async<ProcessManager> =
  async {
    let statePath = sprintf "%s/manager.json" options.DataDir
    let mutable state = ManagerStore.load statePath

    // Plan 11, before anything can launch. Two sessions holding one port is a registry
    // that cannot work — the second to launch fails to bind, and until then the first
    // answers for both — so it fails the boot where the cause is still visible, rather
    // than at whichever launch happens to come second.
    match ManagerState.duplicatePorts state with
    | [] -> ()
    | clashes ->
        let describe (port, ids) =
            sprintf "%d (%s)" port (ids |> List.map SessionId.value |> String.concat ", ")
        failwithf
            "manager state %s claims one port for several sessions: %s"
            statePath
            (clashes |> List.map describe |> String.concat "; ")

    // Give any session that has no address one. Covers both the registry written before
    // this Manager pinned ports and a deployment that has just turned pinning on. Durable
    // before anything uses it, exactly like every other registry write.
    match ManagerState.assignMissingPorts options.SessionPorts state with
    | Error e -> failwithf "session ports: %s" e
    | Ok (_, []) -> ()
    | Ok (assigned, allocations) ->
        state <- assigned
        ManagerStore.save statePath state
        for sessionId, port in allocations do
            options.OnEvent
                "session port assigned"
                [ "yession.session.id", box (SessionId.value sessionId); "yession.session.port", box port ]

    // Runtime-only: the child handle per running session, and the last observed exit
    // for sessions that died without a Stop.
    let mutable children : Map<string, Spawn.RunningChild * int> = Map.empty
    let mutable lastExit : Map<string, int option> = Map.empty
    // Stops in flight: their exits are expected, not crashes.
    let mutable stopping : Set<string> = Set.empty

    // What each RUNNING launch has told us about being in use (Plan 11). Runtime-only and
    // keyed like `children`, so it is born at launch and dies at exit — a stopped session
    // has no idle clock, and a relaunch starts a fresh one rather than inheriting the
    // staleness of the launch before it.
    let mutable activity : Map<string, LaunchActivity> = Map.empty
    // Reaps in flight, so the exit that follows can say why it happened. Cleared on that
    // exit, and cleared again if the stop fails — a reason must never outlive its attempt
    // and mislabel the next ordinary stop.
    let mutable reaping : Map<string, ReapReason> = Map.empty

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
    let mutable launchUsers : Map<string, Set<UserId>> = Map.empty
    // Peers the Manager witnessed into a LAUNCH (docs/plans/07): the browser's peer id
    // rides the authorize bounce and is recorded at ID-token issuance, exactly like
    // launchUsers — keyed by the per-launch control secret, dying with the launch.
    let mutable launchPeers : Map<string, Set<PeerId>> = Map.empty

    // Manager→Session notifications (the reverse leg): live subscriber sinks keyed by the
    // same per-launch secret, so a session's stream dies exactly when its launch does.
    let notifications = NotificationHub.create ()

    // The MCP tool stream (the second reverse leg): a Manager-level retained list broadcast to
    // every subscribed session, so all sessions see the same available MCP services.
    let mcp = RetainedHub.create McpToolList.empty

    // Session changes (docs/plans/09), published on every launch, exit, and display-name
    // change: the full session list with each status, retained so a new subscriber is current
    // at once. Consumers project it — `/sessions/stream` to the registry's Running set for an
    // operator's serving binding, the management page to its rendered table.
    let sessions = RetainedHub.create ([] : SessionView list)

    let statusOf (record: SessionRecord) : SessionStatus =
        let key = SessionId.value record.SessionId
        match Map.tryFind key children with
        | Some (child, port) -> Running (port, child.Pid)
        | None ->
            match Map.tryFind key lastExit with
            | Some code -> Exited code
            | None -> NotRunning

    let viewsNow () : SessionView list =
        state.Sessions |> List.map (fun r -> { Record = r; Status = statusOf r })

    /// Publish the current session list. Call AFTER the runtime bookkeeping a change implies
    /// (`children`, `lastExit`): the value is computed here, so what a subscriber renders is
    /// whatever was true at this instant — announcing an exit before recording its code would
    /// show a crashed session as merely stopped.
    let publishSessions () = sessions.Publish (viewsNow ())

    // The connection-status stream (the third reverse leg, Plan 08): per-launch sinks,
    // dying with the launch like notifications. Each launch receives ITS OWN readable
    // snapshot, so the fan-out below recomputes per subscriber rather than broadcasting
    // one shared list.
    let connectionsHub : NotificationHub.NotificationHub<ConnectionStatusList> = NotificationHub.create ()
    // Re-send every live launch its (recomputed) connection snapshot. Assigned below,
    // once the caller resolution it needs exists; a ref because the broker (created
    // before that) and `recordTokenIssued` both fire it.
    let broadcastConnections : (unit -> unit) ref = ref ignore

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
            publishSessions ()
        | _ -> ()

    // The control channel's activity report (Plan 11): the secret identifies the reporting
    // session, exactly like the name report. `LastBusyAt` moves only while the session says
    // it is BUSY — an idle report is not a denial of service to itself, it is the session
    // starting its own clock — so the field means what it is called.
    let reportActivity (secret: string) (busy: bool) : Async<Result<unit, string>> =
        async {
            match Map.tryFind secret secretSessions with
            | Some sessionId ->
                let key = SessionId.value sessionId
                match Map.tryFind key activity with
                | Some launch ->
                    activity <-
                        Map.add
                            key
                            { launch with
                                LastBusyAt = (if busy then clock () else launch.LastBusyAt)
                                EverReported = true }
                            activity
                // A report from a launch the Manager is not tracking (its exit raced this
                // request) is not an error worth failing: the launch it described is gone,
                // and there is nothing left to reap.
                | None -> ()
                return Ok ()
            | None -> return Error "invalid control secret"
        }

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
    let issuerOf () =
        match PublicAccess.managerUrl options.Public with
        | Some url -> url
        | None -> defaultArg endpointUrl ""
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

    // Last-seen verified claims per user (memory-only): what the strategy asserted at
    // the most recent token issuance. Display and audit material — never policy input.
    let mutable userClaims : Map<UserId, UserClaims> = Map.empty

    let recordTokenIssued (controlSecret: string) (sessionId: SessionId) (subject: UserId) (claims: UserClaims option) (peer: PeerId option) : unit =
        // Guarded by the live secret: a token redeemed in the same instant a launch
        // dies must not resurrect its authority.
        if Map.containsKey controlSecret secretSessions then
            let existing = Map.tryFind controlSecret launchUsers |> Option.defaultValue Set.empty
            launchUsers <- Map.add controlSecret (Set.add subject existing) launchUsers
            claims |> Option.iter (fun c -> userClaims <- Map.add subject c userClaims)
            peer
            |> Option.iter (fun p ->
                let witnessed = Map.tryFind controlSecret launchPeers |> Option.defaultValue Set.empty
                launchPeers <- Map.add controlSecret (Set.add p witnessed) launchPeers)
            audit (SecretStore.Audit.bindingRecorded sessionId subject)
            // A new binding grows the launch's readable connection set — refresh its
            // status stream so a sign-in surfaces already-connected credentials.
            broadcastConnections.Value ()
    // The strategy gates both the OIDC bounce and (below) the management UI. The default
    // is deny-everything: authenticating anyone is an explicit operator choice.
    let strategy = defaultArg options.Strategy Strategy.none
    let! provider = ManagerOidc.create issuerOf strategy recordTokenIssued

    // What a control secret resolves to: the launch's session plus its (optional)
    // environment grant. Registration works for every launch; environment routes 403
    // without a grant.
    let resolveCaller (secret: string) : Control.ControlCaller option =
        Map.tryFind secret secretSessions
        |> Option.map (fun sessionId ->
            { Control.ControlCaller.SessionId = sessionId
              Capabilities = Map.tryFind secret secrets
              Users = Map.tryFind secret launchUsers |> Option.defaultValue Set.empty
              Peers = Map.tryFind secret launchPeers |> Option.defaultValue Set.empty })

    let secretsApi : Control.SecretsApi option = secretStore |> Option.map (secretsApiFor audit)

    // The connection broker (Plan 08): exists exactly when the secret store does — its
    // envelopes are ordinary encrypted entries. Standards-only; its one owned constant
    // is the Manager's public callback URL (stable because the Manager port is fixed;
    // session ports are OS-assigned and cannot anchor a provider's redirect URI).
    let broker : Broker.BrokerService option =
        secretStore
        |> Option.map (fun store ->
            Broker.create
                (fun () -> issuerOf () + "/connections/callback")
                store
                (fun observation ->
                    match observation with
                    | Broker.Connected (id, kind) ->
                        audit (SecretStore.Audit.connectionConnected id (sprintf "%A" kind))
                        broadcastConnections.Value ()
                    | Broker.Disconnected id ->
                        audit (SecretStore.Audit.connectionDisconnected id)
                        broadcastConnections.Value ()
                    | Broker.Resolved (id, kind, refreshed) ->
                        audit (SecretStore.Audit.connectionResolved id (sprintf "%A" kind) refreshed)
                    | Broker.RefreshFailed (id, reason) ->
                        audit (SecretStore.Audit.connectionRefreshFailed id reason)))

    let connectionsApi : Control.ConnectionsApi option =
        match secretStore, broker with
        | Some store, Some b -> Some (connectionsApiFor audit store b)
        | _ -> None

    broadcastConnections.Value <-
        fun () ->
            match connectionsApi with
            | None -> ()
            | Some api ->
                secretSessions
                |> Map.iter (fun secret _ ->
                    match resolveCaller secret with
                    | None -> ()
                    | Some caller ->
                        Async.StartImmediate (
                            async {
                                let! snapshot = api.Status caller
                                connectionsHub.NotifySecret secret snapshot
                            }))

    // The union of user bindings across a session's live launches (in practice one).
    let usersOf (sessionId: SessionId) : Set<UserId> =
        secretSessions
        |> Map.fold
            (fun acc secret sid ->
                if sid = sessionId then
                    Set.union acc (Map.tryFind secret launchUsers |> Option.defaultValue Set.empty)
                else acc)
            Set.empty

    // The union of witnessed-peer bindings across a session's live launches (Plan 07).
    let peersOf (sessionId: SessionId) : Set<PeerId> =
        secretSessions
        |> Map.fold
            (fun acc secret sid ->
                if sid = sessionId then
                    Set.union acc (Map.tryFind secret launchPeers |> Option.defaultValue Set.empty)
                else acc)
            Set.empty

    // The per-request authenticator the UI routes gate on: the same strategy value that
    // authenticates /authorize, applied to any Manager request.
    let identify (req: Interop.IncomingMessage) : Async<AuthenticationOutcome> =
        strategy.Authenticate
            { RemoteAddress = Interop.remoteAddressOf req
              Query = (fun name -> Interop.queryParamOf req.url name)
              Header = fun name -> Interop.headerOf req name }

    // The one PUBLIC broker route (Plan 08): where a provider's redirect lands. Not
    // under /control — a browser arrives here, and the single-use `state` (minted only
    // for a target the policy permitted at begin) is the whole authorization. A tiny
    // self-contained page; the session's panel picks the outcome up via its status
    // stream, so this tab only needs to say "done".
    let connectionsCallbackPage (title: string) (detail: string) : string =
        sprintf
            """<!doctype html>
<html><head><meta charset="utf-8"><title>%s</title>
<style>body{font-family:system-ui,sans-serif;max-width:32rem;margin:4rem auto;padding:0 1rem}p{color:#444}</style>
</head><body><h1>%s</h1><p>%s</p></body></html>"""
            title title detail
    let handleConnectionsCallback (req: Interop.IncomingMessage) (res: Interop.ServerResponse) : bool =
        let path = req.url.Split('?').[0]
        if not (req.``method`` = "GET" && path = "/connections/callback") then false
        else
            let respondHtml (status: int) (html: string) =
                res.writeHead (status, Fable.Core.JsInterop.createObj [ "content-type", box "text/html; charset=utf-8"; "cache-control", box "no-store" ]) |> ignore
                res.``end`` html
            match broker with
            | None -> respondHtml 404 (connectionsCallbackPage "Not available" "This Manager has no secrets store, so connections are disabled.")
            | Some b ->
                match Interop.queryParamOf req.url "error" with
                | Some providerError ->
                    let detail = Interop.queryParamOf req.url "error_description" |> Option.defaultValue providerError
                    respondHtml 400 (connectionsCallbackPage "Sign-in failed" detail)
                | None ->
                    match Interop.queryParamOf req.url "code", Interop.queryParamOf req.url "state" with
                    | Some code, Some state ->
                        Async.StartImmediate (
                            async {
                                match! b.CompleteCallback state code with
                                | Ok _ ->
                                    respondHtml 200 (connectionsCallbackPage "Connected" "You can close this tab and return to your session.")
                                | Error e ->
                                    respondHtml 400 (connectionsCallbackPage "Sign-in failed" e)
                            })
                    | _ -> respondHtml 400 (connectionsCallbackPage "Sign-in failed" "The provider's redirect is missing its code or state.")
            true

    let! controlServer =
        async {
            let handler (req: Interop.IncomingMessage) (res: Interop.ServerResponse) =
                let handled =
                    Control.tryHandle resolveCaller reportName reportActivity notifications.Register mcp.Register provider.RegisterClient secretsApi connectionsApi connectionsHub.Register (fun path -> audit (SecretStore.Audit.controlUnauthorized path)) req res
                    || handleConnectionsCallback req res
                    || provider.TryHandle req res
                    || (match ui, self with
                        | Some handle, Some pm -> handle pm identify req res
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

    let createSession (sessionId: string) (displayName: string) : Result<SessionRecord, string> =
        match SessionId.create sessionId with
        | Error e -> Error e
        | Ok id ->
            // The address is decided HERE, once, and never again for this session — that is
            // the whole point of pinning. A range with nothing left is a refused creation:
            // a session with no address is worse than no session.
            let allocated =
                match options.SessionPorts with
                | Ephemeral -> Ok None
                | Pinned range -> ManagerState.nextPort range state |> Result.map Some
            allocated
            |> Result.bind (fun port ->
                let record =
                    { SessionId = id
                      DisplayName = if displayName.Trim().Length > 0 then displayName.Trim () else sessionId
                      CreatedAt = clock ()
                      DataDir = sprintf "sessions/%s" (SessionId.value id)
                      Port = port }
                match ManagerState.addSession record state with
                | Error e -> Error e
                | Ok next ->
                    // Durable before visible: the registry write precedes any use.
                    ManagerStore.save statePath next
                    state <- next
                    Ok record)

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
                      // The session's own pinned address, or 0 for "ask the OS" under
                      // `Ephemeral`. Read off the RECORD, not the options: the port belongs
                      // to the session, not to this launch.
                      "YESSION_PORT", string (defaultArg record.Port 0)
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
                         connectionsHub.Drop secret
                         provider.RevokeByControlSecret secret
                         launchUsers <- Map.remove secret launchUsers
                         launchPeers <- Map.remove secret launchPeers
                     | None -> ())
                match! Spawn.launch options.SessionCommand options.SessionArgs env options.LaunchTimeoutMs with
                | Error reason ->
                    revokeSecret ()
                    // Name the port when one was pinned. A child that cannot bind exits
                    // before readiness, and "exited before ready" alone sends the reader
                    // hunting through session logs for what is nearly always something
                    // else holding the address. Never fall back to an OS-assigned port:
                    // moving the session is precisely the data-losing behaviour pinning
                    // exists to prevent.
                    return
                        Error (
                            match record.Port with
                            | Some port -> sprintf "%s (pinned port %d)" reason port
                            | None -> reason
                        )
                | Ok (child, port) ->
                    children <- Map.add key (child, port) children
                    lastExit <- Map.remove key lastExit
                    // Plan 11: a session is in use the moment it starts, so the idle clock
                    // begins here. A launch therefore always gets the whole window before
                    // it can be reaped, whether or not it ever reports.
                    activity <-
                        Map.add
                            key
                            { SessionId = record.SessionId; LastBusyAt = clock (); EverReported = false }
                            activity
                    publishSessions ()
                    // The Manager emits its own lifecycle telemetry directly (session launched).
                    options.OnEvent "session launched"
                        [ "yession.session.id", box key; "yession.session.port", box port ]
                    child.OnExit (fun code ->
                        children <- Map.remove key children
                        activity <- Map.remove key activity
                        // The launch's authority dies with it.
                        revokeSecret ()
                        // A stop's exit is the expected outcome, not a crash to report.
                        let stopped = Set.contains key stopping
                        if stopped then stopping <- Set.remove key stopping
                        else lastExit <- Map.add key code lastExit
                        // Published only once the exit is RECORDED: a subscriber renders the
                        // session's status when the frame arrives (the management page's rows
                        // stream does exactly that), so announcing before `lastExit` was set
                        // would show a crashed session as merely stopped until the next change.
                        publishSessions ()
                        // A reap is a stop, but not every stop is a reap — an operator's
                        // click and an elapsed idle window look identical in the exit code,
                        // and only one of them is something to go and look at.
                        let reapReason = Map.tryFind key reaping
                        reaping <- Map.remove key reaping
                        options.OnEvent "session exited"
                            ([ "yession.session.id", box key
                               "yession.session.exit_code", box (defaultArg code -1)
                               "yession.session.stopped", box stopped ]
                             @ (match reapReason with
                                | Some reason -> [ "yession.session.stop_reason", box (ReapReason.describe reason) ]
                                | None -> [])))
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

    // The reaper's sweep (Plan 11). Every rule about WHEN a session may be stopped lives in
    // `Reaper.plan`, which is pure and decided without a clock or a process; this is the
    // loop over its answer, and it is deliberately the whole of the impure part.
    match options.IdleTimeout with
    | None -> ()
    | Some timeout ->
        let sweep () =
            // `activity` holds running launches only, so nothing here can name a session
            // that is already stopped. The `stopping` guard covers the narrower race: a
            // stop in flight whose exit has not landed yet must not be asked for twice.
            Reaper.plan (clock ()) timeout (activity |> Map.toList |> List.map snd)
            |> List.iter (fun (sessionId, reason) ->
                let key = SessionId.value sessionId
                if Map.containsKey key children && not (Set.contains key stopping) then
                    reaping <- Map.add key reason reaping
                    Async.StartImmediate (
                        async {
                            match! stop sessionId with
                            | Ok () -> ()
                            | Error e ->
                                // Leave the launch in `activity`: it is still running, still
                                // idle, and the next sweep will try again. Saying so is the
                                // point — a reaper that silently gives up looks exactly like
                                // one that had nothing to do.
                                reaping <- Map.remove key reaping
                                eprintfn "[reaper] could not stop idle session %s: %s" key e
                        }))
        setInterval (sweepIntervalMsFor timeout) sweep |> ignore

    let pm =
        { CreateSession = createSession
          Launch = launch
          Stop = stop
          Sessions = viewsNow
          SubscribeSessions = sessions.Register
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
          PeersOf = peersOf
          ResolveSecret =
            match secretStore with
            | Some store -> SecretStore.SecretResolution.compose (SecretStore.Audit.injectObserver audit) store usersOf peersOf SecretStore.SecretResolution.processEnv
            | None -> SecretStore.SecretResolution.processEnv
          EndpointPort = controlServer |> Option.map Interop.serverPort
          Public = options.Public
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
