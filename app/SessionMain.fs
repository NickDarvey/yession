module Yession.Host.SessionMain

// The Session Process entry (Phase 4, Steps 23–24): runs exactly ONE session,
// configured from the environment — the Manager's spawn contract — over the session's
// own data directory. Once listening it prints exactly one JSON readiness line to
// stdout; everything else it writes is logging. Environment authority arrives as a
// control endpoint + per-launch secret: the capability calls cross back to the
// Manager, which owns the registry and the engines.

open Fable.Core
open Yession.Domain
open Yession.SessionProcess
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

// The control channel to the Manager (Step 24): supervision reports, secrets custody,
// AND this launch's OAuth client registration all authenticate with the same
// per-launch secret. Absent (a bare `yession-session` run), the session runs
// unsupervised and its HTTP surface is ungated.
let private controlChannel =
    match Interop.envOr "YESSION_CONTROL_URL" "", Interop.envOr "YESSION_CONTROL_SECRET" "" with
    | "", _
    | _, "" -> None
    | url, secret -> Some (url, secret)

// The session-owned WorkSandbox (the sandbox seam): the backend comes from
// `YESSION_WORK_SANDBOX`, parsed fail-closed at boot — a typo refuses the start rather
// than silently dropping isolation.
//
// The default is `srt`: agent-issued commands are confined unless an operator says
// otherwise. That is the point of the seam, and a default of `host` meant every
// deployment that never read the documentation ran them unconfined. `host` is still
// there, and still honest about what it is — it just has to be asked for now.
let private workBackend =
    match SandboxBackend.parse (Interop.envOr "YESSION_WORK_SANDBOX" "srt") with
    | Ok backend -> backend
    | Error e -> failwith e

// The AgentSandbox backend (`YESSION_AGENT_SANDBOX`): where the agent CLI process
// runs — host or srt, never docker (a work-sandbox-only backend). Both tiers go through
// the SDK's `spawnClaudeCodeProcess` seam with an allowlisted env and a scratch HOME
// (Agent.fs); srt adds the OS-level confinement around it. Defaults to `srt` for the
// same reason the WorkSandbox does. Parsed HERE, at boot, so a bad value fails the
// session at start rather than mid-turn. Fail closed, never a silent fallback.
let private agentBackend =
    match SandboxBackend.parseAgent (Interop.envOr "YESSION_AGENT_SANDBOX" "srt") with
    | Ok backend -> backend
    | Error e -> failwithf "agent sandbox: %s" e

// srt's own configuration — the confinement tools and how far the nesting can go — is
// parsed here too, whenever either sandbox will use it. It would otherwise first be read
// where the sandbox is created: for the WorkSandbox that is the agent's first
// `ensure_environment`, minutes into a session, which is no place to discover a typo.
do
    if agentBackend = SrtBackend || workBackend = SrtBackend then
        match Sandboxes.SrtSandbox.toolsFrom (Sandboxes.ambientEnv ()) with
        | Ok _ -> ()
        | Error e -> failwithf "sandbox: %s" e

// Secret references in the sandbox spec resolve over the control channel at sandbox
// spawn — the values go straight into the sandbox policy env and are dropped. Without
// a Manager there is nothing to resolve against; plain values still work.
let private resolveSecretRef : SecretName -> Async<Result<string, string>> =
    match controlChannel with
    | Some (url, secret) -> ControlClient.resolveSecret url secret
    | None -> fun name -> async { return Error (sprintf "no control channel to resolve secret '%s'" (SecretName.value name)) }

// The same control channel carries the collaborative title back to the Manager as the
// session's display name.
let private reportName =
    controlChannel |> Option.map (fun (url, secret) -> ControlClient.nameReporter url secret)

// ...and whether this session is in use, so the Manager can stop it when it is not
// (Plan 11). Absent without a control channel: a session with no Manager has nothing to
// report to, and nothing that would stop it.
let private reportActivity =
    controlChannel |> Option.map (fun (url, secret) -> ControlClient.activityReporter url secret)

// Secrets (Plan 06): the session's write/list/delete surface over the same channel,
// pre-bound to this session's own scope. Built after the session id parses (below).
let private secretsCapabilitiesFor (sessionId: SessionId) =
    controlChannel |> Option.map (fun (url, secret) -> ControlClient.secretsCapabilities url secret sessionId)

// The WorkSandbox composition: an unavailable backend (or one this build does not
// implement) refuses the boot with its reason. The environment itself stays lazy —
// nothing is created until the first signalled need.
// The session's repos directory (Plan 14): one host path both sandboxes see — the git
// verbs clone into it, the WorkSandbox reads and builds it. Created at boot so its
// existence is never a per-operation question, and living in the data dir so a checkout
// survives idle reaping and relaunch with the session.
let private reposDir = sprintf "%s/repos" dataDir
do Fs.ensureDir reposDir

/// The session's WorkSandboxes (Plan 15, stage 2), by name. `default` is the sandbox
/// every session has always had and keeps its workspace path, so nothing about an
/// existing session changes; a named one gets its own workspace under `sandboxes/<name>`,
/// because two sandboxes exist precisely so that what happens in one does not happen in
/// the other. The repos directory is shared by all of them — that is what it is for.
/// `credentials` is a parameter rather than a module value because resolving one is a
/// Plan 08 question answered further down this file (it needs the control channel and the
/// connection-status cache), and a composition root should not have to be read backwards.
let private makeSandboxes
    (credentials: WorkSandboxes.CredentialSource list)
    : Yession.SessionProcess.EventLog<SessionEvent> -> WorkSandboxes.WorkSandboxes =
    let name = SessionId.value sessionId
    let workSpec =
        // The docker backend cannot share a host path by policy, so the repos dir rides
        // the spec as a bind mount at /repos — beside the named workspace volume, not
        // replacing it. Host-family backends share it by write path below.
        match workBackend with
        | DockerBackend ->
            { EnvironmentSpec.defaults with
                Mounts = [ { Source = HostPath reposDir; Target = "/repos"; Mode = ReadWrite } ] }
        | HostBackend
        | SrtBackend -> EnvironmentSpec.defaults
    // Host-family sandboxes work under the session's own data directory; a docker
    // sandbox's workspace lives at the spec/backend default inside the container.
    let workspaceFor (sandbox: SandboxName) =
        match workBackend with
        | HostBackend
        | SrtBackend ->
            if sandbox = SandboxName.defaultName then Some (sprintf "%s/workspace" dataDir)
            else Some (sprintf "%s/sandboxes/%s/workspace" dataDir (SandboxName.value sandbox))
        | DockerBackend -> workSpec.WorkingDirectory
    let sharedRepos =
        match workBackend with
        | HostBackend
        | SrtBackend -> Some reposDir
        | DockerBackend -> None
    // The backend's own container/volume namespace has to differ per sandbox too, or two
    // named sandboxes under docker would fight over one container name.
    let backendNameFor (sandbox: SandboxName) =
        if sandbox = SandboxName.defaultName then name
        else sprintf "%s-%s" name (SandboxName.value sandbox)
    fun log ->
        let create (sandbox: SandboxName) (credentialEnv: Map<string, string>) =
            match Sandboxes.forBackend workBackend (backendNameFor sandbox) workSpec with
            | Error e -> Error e
            | Ok createSandbox ->
                let workspace = workspaceFor sandbox
                workspace |> Option.iter Fs.ensureDir
                let prepare =
                    Sandboxes.preparePolicy workBackend resolveSecretRef workspace sharedRepos workSpec
                Ok (
                    SessionEnvironment.create
                        log
                        createSandbox
                        // The forwarded credentials join the policy env HERE, at the last
                        // moment before the sandbox comes up — the same place a `SecretRef`
                        // resolves, and for the same reason: a value that exists earlier
                        // than it must is a value with more places to leak from.
                        (fun () ->
                            async {
                                match! prepare () with
                                | Error e -> return Error e
                                | Ok policy ->
                                    return Ok { policy with Env = Sandboxes.mergeEnv policy.Env credentialEnv }
                            })
                        (Sandboxes.summaryFor workBackend workSpec)
                        (sprintf "env-%s" (backendNameFor sandbox)))
        match WorkSandboxes.create
                { Backend = SandboxBackend.describe workBackend
                  // The credentials this session knows how to forward. GitHub is the one
                  // Plan 14 left deferred, and it is what makes `git push` from a terminal
                  // work; resolution is the Plan 08 precedence, unchanged.
                  Credentials = credentials
                  Create = create
                  Log = log
                  Clock = fun () -> System.DateTimeOffset.UtcNow } with
        | Ok sandboxes -> sandboxes
        | Error e -> failwithf "work sandboxes: %s" e

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

/// Where a client that has lost this session should ask for it back (Plan 11): the
/// Manager's public origin, baked into the shell.
///
/// Known synchronously, at boot, on EVERY deployment — including loopback, where
/// `PublicAccess` alone has no answer. `YESSION_CONTROL_URL` is the Manager's own endpoint
/// URL, and that endpoint is the same HTTP server as the management UI, so it is precisely
/// the origin that serves `/sessions/{id}/open`. Same precedence as the Manager's OIDC
/// issuer, and by construction the same value.
/// Whether this deployment's sessions keep their address across launches (Plan 13). The
/// shell carries the negative so the client can qualify its local-first promise — which is
/// otherwise a lie on any deployment addressing sessions by port, including the default.
let private ephemeralStorage = not (PublicAccess.sessionAddressIsStable publicAccess)

let private managerOrigin =
    PublicAccess.managerUrlOr (controlChannel |> Option.map fst) publicAccess

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

// This session's MCP server set over the same control channel (Plan 17): the resolved set
// on subscribe, then a fresh whole set on every change. Absent a control channel there is
// nobody to declare a server, so the session runs with none — which is an ordinary session.
let private subscribeMcp =
    match Interop.envOr "YESSION_CONTROL_URL" "", Interop.envOr "YESSION_CONTROL_SECRET" "" with
    | "", _
    | _, "" -> None
    | url, secret -> Some (fun handler -> ControlClient.subscribeMcp url secret handler)

/// A built-in diagnostic runner (`YESSION_AGENT=diagnostic`): exercises the session's
/// command capability end to end — open a terminal, queue, drain, run, read the output back —
/// without model credentials. The verify suite drives it across real process boundaries; it doubles
/// as a field smoke test.
let private diagnosticAgent : RunAgent =
    fun _ capabilities _signal onChunk ->
        async {
            // One call, because after stage 3b there is one door: `execute_command` opens the
            // agent terminal (which starts the environment), queues the command where every
            // peer can see it, drains it and waits for the exit code. That the whole path
            // collapses to this is the point of the merge, and driving the real one across
            // process boundaries is what makes this a smoke test rather than a mock.
            match! capabilities.ExecuteCommand None "node -e \"console.log('diagnostic-ok')\"" with
            | Error reason -> return AgentFailed (sprintf "diagnostic command failed: %s" reason)
            | Ok outcome ->
                match outcome.Status with
                | TerminalCommandRan (CommandSucceeded 0) ->
                    let output = outcome.OutputTail.Trim ()
                    onChunk { Text = output }
                    return AgentCompleted (sprintf "diagnostic: %s" output, None)
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

// The repo manager (Plan 14): the agent's verbs, and — since Plan 15 — the `repos`
// query. Constructed once the event log exists (inside the boot async), so a
// module-level cell carries it to the per-turn dispatcher.
let mutable private reposService : Repos.ReposService option = None

// The query registry (Plan 15): every read-only view this session declares, surfaced to
// the agent as generated MCP tools and to the browser as one multiplexed SSE stream.
// Built beside the service that owns each query, for the same reason and in the same
// place.
let mutable private queryRegistry : Queries.QueryRegistry = Queries.empty

// The session's named WorkSandboxes (Plan 15, stage 2). Built by the Host (it owns the
// log the registry appends to), so this cell is filled once `startFull` resolves — before
// which no turn can run, because nothing is listening.
let mutable private workSandboxes : WorkSandboxes.WorkSandboxes = WorkSandboxes.unavailable

// The MCP servers this session was given (Plan 17). Composed HERE rather than by the Host,
// unlike the other reverse legs, because what arrives on that leg has two consumers: a
// turn's registry, which the Host builds, and the `mcp_servers` query, which is this
// module's. A session with no control channel gets `none` and never subscribes.
let private mcpServers =
    match subscribeMcp with
    | Some _ -> McpClient.create ()
    | None -> McpClient.McpConnections.none

/// The acting party's GitHub token for a repo network verb: the session's explicit
/// credential first, then the named actor's own, then the ambient `GITHUB_TOKEN` (the
/// same last-resort idiom as the agent credential). None = anonymous — public repos
/// still clone.
let private resolveGitHubToken (credentialActor: ActorRef) : Async<string option> =
    async {
        let targets =
            GitHubConnection.turnTargets sessionId credentialActor
            |> List.filter (fun target -> Map.containsKey target connectionStatus)
        match connectionsClient, targets with
        | Some client, target :: _ ->
            match! client.Resolve target with
            | Ok (_, value) -> return Some value
            | Error _ -> return (match Interop.envOr "GITHUB_TOKEN" "" with "" -> None | t -> Some t)
        | _ -> return (match Interop.envOr "GITHUB_TOKEN" "" with "" -> None | t -> Some t)
    }

/// The turn's repo capabilities: the service bound to the agent as acting party and
/// the TURN ACTOR as credential owner. Denials when the service could not start.
///
/// Every mutating verb also INVALIDATES the `repos` query on the way out, which is what
/// puts the change on the humans' screen without anyone refreshing: a command is the only
/// thing that can change a query's answer, so a command is the only thing that has to say
/// it did. Nothing polls.
let private encodeArgs (values: string list) : string = Codec.toString Codec.gatedArgs values

let private decodeArgs (raw: string) : string list =
    match Codec.fromString Codec.gatedArgs raw with
    | Ok values -> values
    | Error _ -> []

/// Invalidate a query once a command has actually changed its answer. A command is the only
/// thing that can, so a command is the only thing that has to say so — nothing polls.
let private andPublish (name: QueryName) (outcome: Async<Result<'a, string>>) : Async<Result<'a, string>> =
    async {
        let! result = outcome
        match result with
        | Ok _ -> queryRegistry.Invalidate name
        | Error _ -> ()
        return result
    }

/// How each gated command is carried out — the table the gate reads, INCLUDING for an act
/// some previous process parked (Plan 15, stage 3b). Everything it needs arrives in the
/// invocation, off the pending act: the arguments, whose credential to run on, and who
/// released it. Nothing is closed over from the turn that proposed it, which is exactly what
/// makes an approval outlive that turn.
///
/// A malformed invocation FAILS rather than guessing. These come out of a replicated doc,
/// and "run something adjacent to what was approved" is the one outcome an approval gate
/// must never produce.
let private commandDispatch () : CommandDispatch =
    let repoCaller (invocation: GatedInvocation) (fallback: ActorRef) =
        Repos.agentCaller (invocation.OnBehalfOf |> Option.defaultValue fallback) invocation.ApprovedBy
    let sandboxCaller (invocation: GatedInvocation) (fallback: ActorRef) : WorkSandboxes.SandboxCaller =
        { Actor = ActorRef.Agent
          Credential = invocation.OnBehalfOf |> Option.defaultValue fallback
          ApprovedBy = invocation.ApprovedBy }
    Map.ofList
        [ GatedCommands.addRepo.Tool,
          fun (invocation: GatedInvocation) ->
            async {
                match reposService, decodeArgs invocation.Args with
                | None, _ -> return Error "this session has no repos"
                | Some service, [ repo ] ->
                    match RepoRef.create repo with
                    | Error e -> return Error (sprintf "not a repo name: %s" e)
                    | Ok repo ->
                        return!
                            andPublish Repos.queryName (
                                async {
                                    match! service.AddRepo (repoCaller invocation ActorRef.Agent) repo with
                                    | Error e -> return Error e
                                    | Ok listing ->
                                        return
                                            Ok (
                                                sprintf
                                                    "added %s — the checkout is shared with everyone in this session and visible in the work environment"
                                                    (RepoListing.describe listing))
                                })
                | Some _, other -> return Error (sprintf "add_repo takes one repo, got %d arguments" (List.length other))
            }

          GatedCommands.switchBranch.Tool,
          fun (invocation: GatedInvocation) ->
            async {
                match reposService, decodeArgs invocation.Args with
                | None, _ -> return Error "this session has no repos"
                | Some service, [ repo; branch; create ] ->
                    match RepoRef.create repo with
                    | Error e -> return Error (sprintf "not a repo name: %s" e)
                    | Ok repo ->
                        return!
                            andPublish Repos.queryName (
                                async {
                                    match! service.SwitchBranch (repoCaller invocation ActorRef.Agent) repo branch (create = "true") with
                                    | Error e -> return Error e
                                    | Ok listing -> return Ok (sprintf "now on %s" (RepoListing.describe listing))
                                })
                | Some _, other ->
                    return Error (sprintf "switch_branch takes a repo, a branch and a flag, got %d arguments" (List.length other))
            }

          GatedCommands.startWorkSandbox.Tool,
          fun (invocation: GatedInvocation) ->
            async {
                match decodeArgs invocation.Args with
                | name :: forward ->
                    match SandboxName.create name with
                    | Error e -> return Error (sprintf "not a sandbox name: %s" e)
                    | Ok name ->
                        match! workSandboxes.Ensure (sandboxCaller invocation ActorRef.Agent) name forward with
                        | Error e -> return Error e
                        | Ok entry ->
                            queryRegistry.Invalidate WorkSandboxes.queryName
                            let forwarding =
                                match entry.Forwarded with
                                | [] -> "nothing forwarded into it"
                                | names -> "forwarding " + String.concat ", " names
                            return
                                Ok (
                                    sprintf
                                        "sandbox '%s' is up on %s, %s — run things in it with execute_command"
                                        (SandboxName.value entry.Name)
                                        entry.Backend
                                        forwarding)
                | [] -> return Error "start_work_sandbox takes a sandbox name"
            }

          GatedCommands.stopWorkSandbox.Tool,
          fun (invocation: GatedInvocation) ->
            async {
                match decodeArgs invocation.Args with
                | [ name ] ->
                    match SandboxName.create name with
                    | Error e -> return Error (sprintf "not a sandbox name: %s" e)
                    | Ok name ->
                        match! workSandboxes.Stop (sandboxCaller invocation ActorRef.Agent) name with
                        | Error e -> return Error e
                        | Ok () ->
                            queryRegistry.Invalidate WorkSandboxes.queryName
                            return Ok (sprintf "sandbox '%s' is stopped; anything running in it is gone" (SandboxName.value name))
                | other -> return Error (sprintf "stop_work_sandbox takes one sandbox name, got %d arguments" (List.length other))
            } ]

/// The turn's repo verbs (Plan 14), bound to the acting party. The MUTATING ones are now
/// three lines each: encode the arguments, render the summary, hand both to the gate. What
/// they used to do lives in `commandDispatch`, where a process that did not propose the act
/// can still reach it.
let private repoCapabilitiesFor (turnActor: ActorRef) (capabilities: AgentCapabilities) : AgentCapabilities =
    match reposService with
    | None -> capabilities
    | Some service ->
        // Takes a CATALOGUE value, not a name. A gated call site therefore cannot name a
        // command the settings surface does not render, or the boot configuration cannot
        // accept — the three read one list, so they cannot drift.
        let gated (command: GatedCommand) (args: string list) (summary: string) =
            capabilities.RunGated
                { Command = command
                  Args = encodeArgs args
                  Summary = summary
                  Author = ActorRef.Agent
                  OnBehalfOf = Some turnActor }
        { capabilities with
            AddRepo =
              fun repo ->
                gated GatedCommands.addRepo [ RepoRef.value repo ] (sprintf "add_repo %s" (RepoRef.value repo))
            SwitchRepoBranch =
              fun repo branch create ->
                let summary =
                    if create then sprintf "switch_branch %s -> new branch %s" (RepoRef.value repo) branch
                    else sprintf "switch_branch %s -> %s" (RepoRef.value repo) branch
                gated GatedCommands.switchBranch [ RepoRef.value repo; branch; (if create then "true" else "false") ] summary
            // The READS take no gate and no approver: they change nothing, so there is
            // nothing to approve and nothing to resume.
            FetchRepo = service.FetchRepo (Repos.agentCaller turnActor None)
            RepoStatus = service.RepoStatus
            RepoLog = service.RepoLog
            RepoDiff = service.RepoDiff }

/// The turn's sandbox commands (Plan 15, stage 2), bound to the acting party.
let private sandboxCapabilitiesFor (turnActor: ActorRef) (capabilities: AgentCapabilities) : AgentCapabilities =
    let gated (command: GatedCommand) (args: string list) (summary: string) =
        capabilities.RunGated
            { Command = command
              Args = encodeArgs args
              Summary = summary
              Author = ActorRef.Agent
              OnBehalfOf = Some turnActor }
    { capabilities with
        StartWorkSandbox =
          fun name forward ->
            let summary =
                match WorkSandboxes.normaliseForward forward with
                | [] -> sprintf "start_work_sandbox %s" (SandboxName.value name)
                | names ->
                    sprintf "start_work_sandbox %s forwarding %s" (SandboxName.value name) (String.concat ", " names)
            gated GatedCommands.startWorkSandbox (SandboxName.value name :: forward) summary
        StopWorkSandbox =
          fun name ->
            gated
                GatedCommands.stopWorkSandbox
                [ SandboxName.value name ]
                (sprintf "stop_work_sandbox %s" (SandboxName.value name)) }

/// Per-turn credential dispatch (Plan 08): resolve the credential the TURN ACTOR runs
/// on — the session's own explicit credential first, then the actor's — fresh from the
/// Manager (which lazily refreshes a due OAuth grant). Ambient env is the last resort;
/// with neither, the turn fails gracefully with a pointer at the Connections panel.
let private dispatching (inner: (string * string) option -> RunAgent) : RunAgent =
    fun context capabilities signal onChunk ->
        async {
            // The repo verbs are rebound to THIS turn's actor here (Plan 14): the acting
            // party on the events is the agent, the credential is the turn human's. The
            // query surface is bound in the same place for a duller reason — the registry
            // is built in the boot async, and this is where a turn first sees it.
            let capabilities = repoCapabilitiesFor context.CurrentMessage.Author capabilities
            let capabilities =
                { capabilities with
                    Queries = queryRegistry.Definitions
                    ReadQuery = queryRegistry.Read }
            // The sandbox commands are bound to THIS turn's actor for the repo verbs'
            // reason (Plan 15, stage 2): the acting party on the event is the agent, and
            // the credentials a forwarding start resolves are the turn human's.
            let capabilities = sandboxCapabilitiesFor context.CurrentMessage.Author capabilities
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
    // Only scopes a TURN can actually reach. A pre-`LocalScope` deployment can still hold
    // peer-scoped claude entries, and they remain readable (the peer is witnessed) — but
    // nothing dispatches on them any more, so counting them here would open the gate on a
    // credential every turn then fails to find. The gate has to promise what the
    // dispatcher can deliver.
    connectionStatus
    |> Map.exists (fun target _ ->
        target.Name = ClaudeConnection.secretName
        && (match target.Scope with
            | PeerScope _ -> false
            | SessionScope _ | UserScope _ | LocalScope -> true))

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
        // The repo manager (Plan 14), over the same log and the agent backend's sandbox
        // family. A backend that cannot host it fails the boot — the same fail-closed
        // stance as the WorkSandbox composition above.
        do
            match Repos.create
                    { Backend = agentBackend
                      ReposDir = reposDir
                      ExtraReadPaths = []
                      AllowedDomains = [ "github.com" ]
                      AllowProtocol = "https"
                      CloneUrl = RepoRef.cloneUrl
                      ResolveToken = resolveGitHubToken
                      Log = log } with
            | Ok service -> reposService <- Some service
            | Error e -> failwithf "repos: %s" e
        // The query registry (Plan 15): every read-only view this session declares, in
        // one place. A capability that could not start declares nothing rather than
        // declaring a query that always errors — an empty settings surface says "this
        // session has no repos capability" more honestly than a section that only ever
        // shows a failure.
        do
            let registrations =
                [ match reposService with
                  | Some service -> Repos.query service
                  | None -> ()
                  // Reads the cell rather than a value: the registry is the Host's, and
                  // the Host has not been started yet. By the time anyone reads it, it is.
                  WorkSandboxes.query (fun () -> workSandboxes)
                  McpClient.query (fun () -> mcpServers) ]
            match Queries.create registrations with
            | Ok registry -> queryRegistry <- registry
            | Error e -> failwithf "queries: %s" e
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
        // The GitHub connection surface (Plan 14) rides the same status cache and control
        // channel; the two panel handlers compose into the one extra-routes seam, each
        // claiming only its own paths.
        let connectionRoutes =
            let githubRoutes =
                match auth, connectionsClient with
                | Some a, Some client ->
                    Some (
                        GitHubConnection.routes
                            sessionId
                            a
                            client
                            (fun target -> Map.tryFind target connectionStatus)
                            sessionMount)
                | _ -> None
            // The read surface (Plan 15): one SSE stream carrying every registered query.
            // This replaced the Repos panel's `/repos*` routes — the listing became a
            // query and the write actions were retired, so a human asks the agent and
            // watches the timeline instead of driving a second interface.
            let queryRoutes =
                match auth with
                | Some a -> Some (Queries.routes a queryRegistry sessionMount)
                | None -> None
            [ claudeRoutes; githubRoutes; queryRoutes ]
            |> List.choose id
            |> function
               | [] -> None
               | handlers -> Some (fun req res -> handlers |> List.exists (fun handler -> handler req res))
        // Transcripts live beside the event log and the doc sidecar, one `.cast` file per
        // terminal — a durable, replayable record of everything its commands printed.
        let transcriptStore = TranscriptStore.openStore (sprintf "%s/terminals" dataDir)
        // The credentials this session can forward into a sandbox (Plan 15, stage 2).
        // GitHub is what Plan 14 deferred, and it is what makes `git push` from a terminal
        // work; the resolution is the Plan 08 precedence, unchanged.
        let forwardableCredentials : WorkSandboxes.CredentialSource list =
            [ { Name = "github"; EnvVar = "GITHUB_TOKEN"; Resolve = resolveGitHubToken } ]
        let! host = Host.startFull runAgent (Some (makeSandboxes forwardableCredentials)) (secretsCapabilitiesFor sessionId) (Some log) (Some docStore) (Some transcriptStore) reportName reportActivity telemetry.Emit subscribeNotifications mcpServers connectionRoutes sessionId auth sessionMount managerOrigin ephemeralStorage port
        // The Host built the sandbox registry (it owns the log), so the cell the turn
        // capabilities and the `work_sandboxes` query read is filled here — before the
        // readiness line, and therefore before any turn or any browser can ask.
        workSandboxes <- host.Sandboxes
        // How each gated command is carried out, handed to the gate the Host owns. Doing it
        // here — and not by closing over a turn — is what lets the gate honour an approval
        // for an act that a previous process parked: it drains the doc, and this is the only
        // thing it was missing.
        host.SetCommandDispatch (commandDispatch ())
        // The operator's gate configuration (Plan 15, stage 3b), SEEDED into the synced
        // register rather than consulted at decision time — which is what leaves exactly one
        // place a mode is ever read from, and lets a human change their mind mid-session
        // without a restart. Empty by default, so a session nobody configured behaves exactly
        // as it did before the gate existed. A restart re-asserts the operator's list: the
        // configuration is what the session BOOTS as, and a mid-session change is a change to
        // this run.
        match CommandGates.parseConfiguredGates (Interop.envOr "YESSION_GATED_COMMANDS" "") with
        // A name that is not a gated command is fatal, not skipped: an operator who believes
        // a command is gated and finds it silently is not has the blind spot this whole
        // stage exists to remove.
        | Error reason ->
            eprintfn "YESSION_GATED_COMMANDS: %s" reason
            Interop.exit 1
        | Ok [] -> ()
        | Ok commands ->
            for command in commands do
                SyncedStateSync.setGate host.Doc (GatedCommands.subject command) ApproveAgent
            eprintfn "[session %s] these commands need a human to approve them: %s"
                (SessionId.value sessionId) (commands |> List.map (fun c -> c.Tool) |> String.concat ", ")
        // The reverse leg starts LAST, after the query registry exists and the Host is up:
        // a set frame rebuilds a registry and invalidates a query, and both of those have
        // to be there before the first frame can arrive.
        //
        // Fire-and-forget on the frame: a handshake is a network round trip and the sink is
        // an SSE frame handler. A turn that begins mid-handshake gets the registry as it
        // stood — without the new server, never a half-built one — and the tools appear on
        // the next turn, which is what the invalidation below tells the panel too.
        match subscribeMcp with
        | Some subscribe ->
            let note (name: McpServerName) (make: McpServerNoted -> SessionEvent) =
                async {
                    match MessageId.create (string (System.Guid.NewGuid ())) with
                    | Error _ -> return ()
                    | Ok messageId ->
                        // `ActorRef.System`, because nobody in the session did this.
                        let! _ = log.Append ActorRef.System (make { MessageId = messageId; Name = name })
                        return ()
                }
            subscribe (fun set ->
                Async.StartImmediate (
                    async {
                        // The delta is computed against the LOG — what this session was
                        // last TOLD it had — so a boot, a reconnect and a process restart
                        // all emit nothing, and only a genuine change by the operator is
                        // loud. Read before applying: `Apply` is the slow part and the log
                        // does not move while it runs.
                        let! page = log.Read None System.Int32.MaxValue
                        let announced = McpNotes.announced (page.Events |> List.map (fun e -> e.Event))
                        let gained, lost = McpNotes.delta announced set
                        do! mcpServers.Apply set
                        for name in gained do
                            do! note name SessionEvent.McpServerAvailable
                        for name in lost do
                            do! note name SessionEvent.McpServerUnavailable
                        queryRegistry.Invalidate McpClient.queryName
                    }))
            |> ignore
            // ...and keep asking. A set frame says WHICH servers this session has; only
            // asking them says what they can do right now. A provider that starts after the
            // declaration, restarts, or grows tools as a device is plugged in is invisible
            // otherwise — the declaration never changed, so no frame is coming.
            Interop.setInterval McpClient.PollIntervalMs (fun () ->
                Async.StartImmediate (
                    async {
                        let! moved = mcpServers.Poll ()
                        if moved then queryRegistry.Invalidate McpClient.queryName
                    }))
            |> ignore
        | None -> ()
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
                        do! telemetry.Shutdown () |> Interop.awaitPromise
                        Interop.exit 0
                    }))
        // The one readiness line of the spawn contract — last, so the Manager can
        // treat everything before it as logs and everything after as a live session.
        // `version` lets the Manager notice it just launched a session from a different
        // release; a Manager old enough not to read the field simply ignores it.
        printfn """{"yession":"ready","port":%d,"version":"%s"}""" host.Port Version.current
    })
