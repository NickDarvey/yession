module Yession.Host.SessionMain

// The Session Process entry (Phase 4, Steps 23–24): runs exactly ONE session,
// configured from the environment — the Manager's spawn contract — over the session's
// own data directory. Once listening it prints exactly one JSON readiness line to
// stdout; everything else it writes is logging. Environment authority arrives as a
// control endpoint + per-launch secret: the capability calls cross back to the
// Manager, which owns the registry and the engines.

open Fable.Core
open Yession.Domain
open Yession.Host

// `--version` answers before any configuration is read: no data directory, no ports, no
// Manager. It is the one thing a Session Process will do without a session.
if Interop.versionFlag () then
    printfn "%s" Version.current
    Interop.exit 0

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

let private sessionId = SessionId.create (Interop.envOr "YESSION_SESSION" "local-session") |> expect
let private port = Interop.envOr "YESSION_PORT" "0" |> int
let private dataDir =
    Interop.envOr "YESSION_SESSION_DATA" (sprintf ".yession/sessions/%s" (SessionId.value sessionId))

// The control channel to the Manager (Step 24): environment capability calls, the
// display-name report, AND this launch's OAuth client registration all authenticate
// with the same per-launch secret. Absent (a bare `yession-session` run), the session
// is environment-less and its HTTP surface is ungated.
let private controlChannel =
    match Interop.envOr "YESSION_CONTROL_URL" "", Interop.envOr "YESSION_CONTROL_SECRET" "" with
    | "", _
    | _, "" -> None
    | url, secret -> Some (url, secret)

// Environment capabilities over the control RPC, when the Manager granted them. A
// launch without a grant still holds the channel (403 on environment routes).
let private environmentCapabilities =
    controlChannel |> Option.map (fun (url, secret) -> ControlClient.capabilities url secret)

// The same control channel carries the collaborative title back to the Manager as the
// session's display name.
let private reportName =
    controlChannel |> Option.map (fun (url, secret) -> ControlClient.nameReporter url secret)

// Secrets (Plan 06): the session's write/list/delete surface over the same channel,
// pre-bound to this session's own scope. Built after the session id parses (below).
let private secretsCapabilitiesFor (sessionId: SessionId) =
    controlChannel |> Option.map (fun (url, secret) -> ControlClient.secretsCapabilities url secret sessionId)

// Where this session is reachable from outside (docs/plans/09), from the same two
// variables the Manager parsed, inherited by plain env. Fails the boot on a combination
// that cannot work, rather than registering a redirect URI no browser can reach.
let private publicAccess =
    match Interop.publicAccess () with
    | Ok access -> access
    | Error e -> failwith e

/// The path this session is served under: `""` unless the deployment path-mounts its
/// sessions. Known HERE, before the port is bound, because everything fixed at boot
/// depends on it — the shell's `<base href>`, the auth cookie's `Path`, and the prefix
/// stripped off every incoming request. (That is why a template may not put `{port}` in
/// its path.)
let private sessionMount = PublicAccess.sessionMount sessionId publicAccess

// User authorization: with a Manager, this session is an OIDC client of it; the RP
// configuration completes after listen (the redirect URI needs the bound port).
let private auth =
    controlChannel |> Option.map (fun _ -> SessionAuth.create sessionId sessionMount)

// Telemetry: this session is a direct OTel emitter — one OTel log record per completed turn.
// Destination (stdout / a collector / both / off) comes from the standard OTEL_* env the
// Manager passes through; identity (service.name=yession-session, service.instance.id=<id>)
// the Manager adapts per child. No Manager-side collector, no bespoke endpoint.
let private telemetry = Telemetry.fromEnv sessionId

// The reverse leg over the same control channel: subscribe to the Manager's notification
// stream so an out-of-band change can reach this session. Absent a control channel, the
// session simply runs without it (nothing pushes notifications in-process).
let private subscribeNotifications =
    match Interop.envOr "YESSION_CONTROL_URL" "", Interop.envOr "YESSION_CONTROL_SECRET" "" with
    | "", _
    | _, "" -> None
    | url, secret -> Some (fun handler -> ControlClient.subscribeNotifications url secret handler)

// The MCP tool stream over the same control channel: the current tool list on subscribe, then
// updates as MCP services come and go. Absent a control channel, the session runs without it.
let private subscribeMcp =
    match Interop.envOr "YESSION_CONTROL_URL" "", Interop.envOr "YESSION_CONTROL_SECRET" "" with
    | "", _
    | _, "" -> None
    | url, secret -> Some (fun handler -> ControlClient.subscribeMcp url secret handler)

/// A built-in diagnostic runner (`YESSION_AGENT=diagnostic`): exercises the session's
/// environment capability end to end — ensure, execute, stream — without model
/// credentials. The verify suite drives it across real process boundaries; it doubles
/// as a field smoke test.
let private diagnosticAgent : RunAgent =
    fun _ capabilities _signal onChunk ->
        async {
            match! capabilities.EnsureEnvironment "diagnostic run" with
            | EnvironmentUnavailable reason -> return AgentFailed (sprintf "environment unavailable: %s" reason)
            | EnvironmentAvailable ->
                let request =
                    { CommandId = CommandId.create (string (System.Guid.NewGuid ())) |> expect
                      Executable = "node"
                      Arguments = [ "-e"; "console.log('diagnostic-ok')" ]
                      WorkingDirectory = None
                      Environment = Map.empty
                      Timeout = Some (System.TimeSpan.FromSeconds 30.0) }
                let mutable output = ""
                let! result = capabilities.ExecuteCommand request (fun chunk -> output <- output + chunk.Text)
                match result with
                | CommandSucceeded 0 ->
                    onChunk { Text = output.Trim () }
                    return AgentCompleted (sprintf "diagnostic: %s" (output.Trim ()), None)
                | other -> return AgentFailed (sprintf "diagnostic command failed: %A" other)
        }

/// A built-in probe (`YESSION_AGENT=usage-probe`, Plan 04): completes a turn with fixed,
/// non-zero usage and no credentials, so the cross-process telemetry e2e can assert the
/// counts reach the Manager collector over the real spawn + OTLP path.
let private usageProbeAgent : RunAgent =
    fun _ _ _ _ ->
        async {
            return
                AgentCompleted (
                    "usage probe",
                    Some
                        { InputTokens = 111
                          OutputTokens = 22
                          CacheReadTokens = 3
                          CacheCreationTokens = 4
                          Model = Some "probe-model" })
        }

// Ambient credentials (the documented last resort, and how CI's LiveAgent tier feeds
// the agent): inherited from the Manager's shell, shared by every session and actor.
let private envCreds =
    Interop.envOr "ANTHROPIC_API_KEY" (Interop.envOr "CLAUDE_CODE_OAUTH_TOKEN" "") <> ""

// The session's live view of connected credentials (Plan 08): fed by the Manager's
// connection-status stream, metadata only. Availability is DYNAMIC — a sign-in
// mid-session flips the agent gate without a relaunch.
let mutable private connectionStatus : Map<SecretId, ConnectionKind> = Map.empty

let private connectionsClient =
    controlChannel |> Option.map (fun (url, secret) -> ControlClient.connections url secret)

/// Per-turn credential dispatch (Plan 08): resolve the credential the TURN ACTOR runs
/// on — the session's own explicit credential first, then the actor's — fresh from the
/// Manager (which lazily refreshes a due OAuth grant). Ambient env is the last resort;
/// with neither, the turn fails gracefully with a pointer at the Connections panel.
let private dispatching (inner: (string * string) option -> RunAgent) : RunAgent =
    fun context capabilities signal onChunk ->
        async {
            // A dispatch-level failure streams its reason as the message body first:
            // the turn's item is already open (AgentMessageStarted precedes the
            // runner), so this is what makes the reason VISIBLE in the timeline.
            let fail (reason: string) =
                onChunk { Text = reason }
                AgentFailed reason
            let targets =
                ClaudeConnection.turnTargets sessionId context.CurrentMessage.Author
                |> List.filter (fun target -> Map.containsKey target connectionStatus)
            match connectionsClient, targets with
            | Some client, target :: _ ->
                match! client.Resolve target with
                | Ok (kind, value) ->
                    return! inner (Some (ClaudeConnection.envVarFor kind value)) context capabilities signal onChunk
                | Error e ->
                    if envCreds then return! inner None context capabilities signal onChunk
                    else return fail (sprintf "could not use the connected Claude account: %s" e)
            | _ ->
                if envCreds then return! inner None context capabilities signal onChunk
                else
                    return
                        fail (
                            sprintf
                                "no Claude account connected for %s — open Connections to sign in"
                                (ClaudeConnection.actorLabel context.CurrentMessage.Author))
        }

/// A built-in probe (`YESSION_AGENT=credential-probe`): completes immediately, naming
/// the env var the dispatcher resolved (or `env` for the ambient fallback) — the
/// deterministic cross-process proof that per-actor credential dispatch worked, same
/// convention as `diagnostic`/`usage-probe`.
let private credentialProbe (credential: (string * string) option) : RunAgent =
    fun _ _ _ onChunk ->
        async {
            let body =
                match credential with
                | Some (name, _) -> sprintf "credential: %s" name
                | None -> "credential: env"
            onChunk { Text = body }
            return AgentCompleted (body, None)
        }

// The agent gate, read at every drain: built-in probes are always on; the real agent
// (and the probe below) runs when ambient credentials exist OR a relevant connection
// is live. Without either the session still works as a human-only collaborative
// session — messages drain to `MessageSent` with no turn.
let private connectedSomewhere () =
    connectionStatus |> Map.exists (fun target _ -> target.Name = ClaudeConnection.secretName)

let private runAgent () : RunAgent option =
    match Interop.envOr "YESSION_AGENT" "" with
    | "diagnostic" -> Some diagnosticAgent
    | "usage-probe" -> Some usageProbeAgent
    | "credential-probe" ->
        if envCreds || connectedSomewhere () then Some (dispatching credentialProbe) else None
    | _ ->
        if envCreds || connectedSomewhere () then Some (dispatching Agent.runWith) else None

[<Fable.Core.Emit("(process.stdin.on('close', $0), process.stdin.on('end', $0), process.stdin.resume())")>]
let private onStdinClosed (handler: unit -> unit) : unit = Fable.Core.Util.jsNative

Async.StartImmediate (
    async {
        let log =
            EventStore.openLog (sprintf "%s/events.jsonl" dataDir) sessionId (fun () -> System.DateTimeOffset.UtcNow)
        let docStore = DocStore.openStore (sprintf "%s/doc.jsonl" dataDir)
        // The connection-status stream (Plan 08): each frame replaces the whole cache
        // (snapshot semantics), flipping the agent gate and the /claude status as
        // credentials connect and disconnect. Best-effort like the other reverse legs.
        match controlChannel with
        | Some (url, secret) ->
            ControlClient.subscribeConnections url secret (fun list ->
                connectionStatus <-
                    list.Connections |> List.map (fun s -> s.Id, s.Kind) |> Map.ofList)
            |> ignore
        | None -> ()
        // The browser-facing Claude connection surface: only meaningful with both a
        // login surface (cookie identity) and a control channel to broker through.
        let claudeRoutes =
            match auth, connectionsClient with
            | Some a, Some client ->
                Some (
                    ClaudeConnection.routes
                        sessionId
                        a
                        client
                        (fun target -> Map.tryFind target connectionStatus)
                        (fun () -> envCreds || connectedSomewhere ())
                        sessionMount)
            | _ -> None
        let! host = Host.startFull runAgent environmentCapabilities (secretsCapabilitiesFor sessionId) (Some log) (Some docStore) reportName telemetry.Emit subscribeNotifications subscribeMcp claudeRoutes sessionId auth sessionMount port
        // Register this launch's OAuth client with the Manager — HERE, after listen
        // (the redirect URI needs the OS-assigned port) and BEFORE the readiness line
        // (readiness implies the login surface works). A session that cannot register
        // cannot authorize users, so failure is fatal, never a half-open session.
        match controlChannel, auth with
        | Some (url, secret), Some auth ->
            // The address is the configured public one (docs/plans/09), inherited from
            // the Manager's env: behind a proxy the browser must land on a reachable
            // callback. Loopback when unset (the RFC 8252 default).
            let redirectUri =
                sprintf "%s/callback" (PublicAccess.sessionAddress sessionId host.Port publicAccess).Url
            match! ControlClient.registerClient url secret redirectUri with
            | Error e ->
                eprintfn "client registration with the manager failed: %s" e
                Interop.exit 1
            | Ok registration ->
                match! auth.Configure registration.Issuer registration.ClientId registration.ClientSecret redirectUri with
                | Error e ->
                    eprintfn "%s" e
                    Interop.exit 1
                | Ok () -> ()
        | _ -> ()
        // Sessions never outlive their Manager: spawned under the guard, the Manager's
        // death closes our stdin (the kernel does this even on SIGKILL) and we exit.
        if Interop.envOr "YESSION_PARENT_GUARD" "" = "1" then
            // Flush buffered telemetry before exiting (the Manager's death closes stdin).
            onStdinClosed (fun () ->
                Async.StartImmediate (
                    async {
                        do! telemetry.Shutdown () |> Async.AwaitPromise
                        Interop.exit 0
                    }))
        // The one readiness line of the spawn contract — last, so the Manager can
        // treat everything before it as logs and everything after as a live session.
        // `version` lets the Manager notice it just launched a session from a different
        // release; a Manager old enough not to read the field simply ignores it.
        printfn """{"yession":"ready","port":%d,"version":"%s"}""" host.Port Version.current
    })
