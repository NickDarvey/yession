module Yession.Host.SessionMain

// The Session Process entry (Phase 4, Steps 23–24): runs exactly ONE session,
// configured from the environment — the Manager's spawn contract — over the session's
// own data directory. Once listening it prints exactly one JSON readiness line to
// stdout; everything else it writes is logging. Environment authority arrives as a
// control endpoint + per-launch secret: the capability calls cross back to the
// Manager, which owns the registry and the engines.

open Fable.Core
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Agent
open Yession.Domain.Link
open Yession.Domain.Tools
open Yession.Domain.Access
open Yession.SessionProcess
open Yession.Host

// This bin takes no options of its own — everything it needs arrives in the environment the
// Manager spawns it with. `--version` and `--help` still answer, before any configuration is
// read: no data directory, no ports, no Manager. They are the only things a Session Process
// will do without a session.
Cli.parseOrExit (Cli.spec "yession-session" []) Version.current |> ignore

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

/// What the Manager minted for this launch, decoded ONCE. Blank means nobody launched us —
/// `Launch.unlaunched`, a bare `yession-session` — and a malformed envelope fails the boot
/// rather than booting under a fabricated identity.
let private launch = Launch.parse (Interop.envOr Launch.Variable "") |> expect

let private sessionId = launch.Session
let private port = launch.Port
// ABSOLUTE, whatever it was given. `Launch.unlaunched`'s is relative, and so is what the
// test harness passes; every path this session stores, binds into a sandbox, or reports to a
// person is derived from it. A relative one still WORKS for anything resolved once — which
// is why it survived this long — and silently breaks whatever resolves it twice. `Repos`
// guards its own boundary too (see `Repos.create`): that is the same rule at the place a
// relative path becomes a wrong answer rather than the place it is born.
let private dataDir = Fs.absolute launch.DataDir

// The control channel to the Manager (Step 24): supervision reports, secrets custody,
// AND this launch's OAuth client registration all authenticate with the same
// per-launch secret. Absent (a bare `yession-session` run), the session runs
// unsupervised and its HTTP surface is ungated.
let private controlChannel = launch.Control |> Option.map (fun c -> c.Url, c.Secret)

// The session-owned WorkSandbox (the sandbox seam): the backend comes from
// `YESSION_SESSION_WORK_BACKEND`, parsed fail-closed at boot — a typo refuses the start rather
// than silently dropping isolation.
//
// The default is `srt`: agent-issued commands are confined unless an operator says
// otherwise. That is the point of the seam, and a default of `host` meant every
// deployment that never read the documentation ran them unconfined. `host` is still
// there, and still honest about what it is — it just has to be asked for now.
let private workBackend =
    match SandboxBackend.parse (Interop.envOr "YESSION_SESSION_WORK_BACKEND" "srt") with
    | Ok backend -> backend
    | Error e -> failwith e

// The operator's vocabulary (`YESSION_SESSION_RESOURCES`), read once at boot.
//
// Fail-closed like the backend above, and for a stronger reason: a profile is a statement
// about what this host will let a sandbox touch, and a deployment that started with a
// half-read one would be offering something nobody wrote. No profile at all is ordinary and
// declares nothing; a profile that cannot be read stops the session.
let private resourceProfile =
    match Interop.envOr "YESSION_SESSION_RESOURCES" "" with
    | "" -> None
    | path ->
        match OperatorResources.read path with
        | Ok profile -> profile
        | Error e -> failwith e

/// What the operator grants every work sandbox without it asking: the profile's `default`,
/// flattened once at boot.
///
/// Resolved here rather than per sandbox because it cannot differ between them — it is the
/// host's statement, not this sandbox's — and because a failure to resolve it is a failure of
/// the profile, which the decoder already refused. `[]` is a deployment that declared no
/// default, which is ordinary: the vocabulary exists and nothing is handed out.
let private grantedLeaves : ResourceLeaf list =
    match resourceProfile with
    | None -> []
    | Some file ->
        match ResourceProfile.resolve file.Resources file.Default with
        | Ok closure -> ResourceClosure.leaves closure |> Set.toList
        | Error e -> failwith e

/// What one sandbox holds: the host's `default`, plus whatever the sandbox itself selected.
///
/// A selection naming something this host does not declare is refused HERE, with the profile
/// in hand, and the sentence lists what there is instead. That refusal is the whole of the
/// old ceiling check — a repo cannot exceed what the operator offered because it can only
/// name what the operator offered, which is a stronger arrangement than comparing two lists
/// and hoping the comparison is right.
let private grantsFor (selection: ResourceName list) : Result<ResourceLeaf list, string> =
    match selection, resourceProfile with
    | [], _ -> Ok grantedLeaves
    | _, None ->
        Error (
            sprintf
                "this sandbox selects %s, and this host declares no resources at all — an operator sets YESSION_SESSION_RESOURCES to a profile naming them"
                (selection |> List.map ResourceName.value |> String.concat ", "))
    | selection, Some file ->
        ResourceProfile.resolve file.Resources selection
        |> Result.map (fun closure ->
            (ResourceClosure.leaves closure |> Set.toList) @ grantedLeaves |> List.distinct)

// The AgentSandbox backend (`YESSION_SESSION_AGENT_BACKEND`): where the agent CLI process
// runs — host or srt, never docker (a work-sandbox-only backend). Both tiers go through
// the SDK's `spawnClaudeCodeProcess` seam with an allowlisted env and a scratch HOME
// (Agent.fs); srt adds the OS-level confinement around it. Defaults to `srt` for the
// same reason the WorkSandbox does. Parsed HERE, at boot, so a bad value fails the
// session at start rather than mid-turn. Fail closed, never a silent fallback.
let private agentBackend =
    match SandboxBackend.parseAgent (Interop.envOr "YESSION_SESSION_AGENT_BACKEND" "srt") with
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
// survives idle reaping and relaunch with the session. WHERE under the data dir is
// `Sandboxes.SessionLayout`'s to say, because the answer is a statement about the
// workspace a terminal opens in and not something this file can decide alone.
let private reposDir = Sandboxes.SessionLayout.prepareReposDir dataDir

// Prepared HERE, at module scope beside the repos directory, because srt reads
// `CLAUDE_CODE_TMPDIR` off this process at every wrap: it has to be settled before the
// first sandbox is built, and every route to one goes through this file. Bound rather than
// discarded so the compiler keeps it, and so a reader can see the path the session's
// commands will write their temporary files to.
let private tmpDir = Sandboxes.SessionLayout.prepareTmpDir dataDir

/// Where a work sandbox works. Host-family sandboxes work under the session's own data
/// directory; a docker sandbox's workspace is the image's, which nothing here composes.
///
/// Module level because two things need it and they are not near each other: the sandboxes
/// themselves, and the path a repo verb ANSWERS with — which is relative to the terminal's
/// working directory or it is not relative to anything.
let private workspaceFor (sandbox: SandboxRef) =
    match workBackend with
    | HostBackend
    | SrtBackend -> Some (Sandboxes.SessionLayout.workspaceFor dataDir sandbox)
    | DockerBackend -> EnvironmentSpec.defaults.WorkingDirectory

/// The session's WorkSandboxes (Plan 15, stage 2), by name — each in the workspace
/// `SessionLayout` gives it, all of them sharing the one repos directory, which is what
/// it is for. `credentials` is a parameter rather than a module value because resolving
/// one is a Plan 08 question answered further down this file (it needs the control channel
/// and the connection-status cache), and a composition root should not have to be read
/// backwards.
let private makeSandboxes
    (credentials: WorkSandboxes.CredentialSource list)
    : Yession.SessionProcess.EventLog<SessionEvent> -> WorkSandboxes.WorkSandboxes =
    let name = SessionId.value sessionId
    /// What the SESSION adds to whatever was asked for. The checkouts are the session's,
    /// shared by every sandbox in it, so they are not part of anybody's ask: a repo that
    /// declared its own `dev` did not decline to see the repos directory.
    ///
    /// The docker backend cannot share a host path by policy, so it rides the spec as a
    /// bind mount at /repos — beside the named workspace volume, not replacing it.
    /// Host-family backends share it by write path instead (`sharedRepos`). The runtime
    /// union makes this branch structural rather than incidental: only the docker arm can
    /// name a mount, because only it has a container to mount into.
    let withSessionRepos (requested: EnvironmentSpec) =
        match workBackend with
        | DockerBackend ->
            let container =
                match requested.Runtime with
                | Container container -> container
                | Confinement -> ContainerSpec.defaults
            { requested with
                Runtime =
                    Container
                        { container with
                            Mounts =
                                container.Mounts
                                @ [ { Source = HostPath reposDir
                                      Target = Sandboxes.reposVisibleAt workBackend reposDir
                                      Mode = ReadWrite } ] } }
        | HostBackend
        | SrtBackend -> requested
    let sharedRepos =
        match workBackend with
        | HostBackend
        | SrtBackend -> Some reposDir
        | DockerBackend -> None
    fun log ->
        let create (sandbox: SandboxRef) (requested: EnvironmentSpec) (credentialEnv: Map<string, string>) =
            let workSpec = withSessionRepos requested
            // The backend's own container/volume namespace has to differ per sandbox, or two
            // of them under docker would fight over one container name — and now that a repo
            // can declare its own, two REPOS' same-named sandboxes would too. The rule lives
            // on `SandboxRef`, where a cheap test reaches it; this composition only asks.
            match Sandboxes.forBackend workBackend (SandboxRef.objectName sessionId sandbox) workSpec with
            | Error e -> Error e
            | Ok createSandbox ->
                let workspace = workspaceFor sandbox
                workspace |> Option.iter Fs.ensureDir
                // Its own HOME, created here beside its workspace. Docker's image brings a
                // home this process did not make, so only the host-family backends get one.
                let home =
                    match workBackend with
                    | HostBackend
                    | SrtBackend -> Some (Sandboxes.SessionLayout.homeFor dataDir sandbox)
                    | DockerBackend -> None
                home |> Option.iter Fs.ensureDir
                let prepare =
                    Sandboxes.preparePolicy
                        workBackend
                        resolveSecretRef
                        workspace
                        sharedRepos
                        home
                        grantsFor
                        workSpec
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
                        (sprintf "env-%s" (SandboxRef.objectName sessionId sandbox)))
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

// Where this session is reachable from outside, from the same two
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
/// `PublicAccess` alone has no answer. The launch's control url is the Manager's own endpoint
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
    launch.Control
    |> Option.map (fun c -> fun handler -> ControlClient.subscribeNotifications c.Url c.Secret handler)

// This session's MCP server set over the same control channel (Plan 17): the resolved set
// on subscribe, then a fresh whole set on every change. Absent a control channel there is
// nobody to declare a server, so the session runs with none — which is an ordinary session.
let private subscribeMcp =
    launch.Control
    |> Option.map (fun c -> fun handler -> ControlClient.subscribeMcp c.Url c.Secret handler)

/// A built-in diagnostic runner (`YESSION_SESSION_AGENT=diagnostic`): exercises the session's
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
            match! capabilities.Terminals.Execute (CommandRequest.ofCommand "node -e \"console.log('diagnostic-ok')\"") with
            | Error reason -> return AgentFailed (sprintf "diagnostic command failed: %s" reason, None)
            | Ok outcome ->
                match outcome.Status with
                | TerminalCommandRan (CommandSucceeded 0) ->
                    let output = outcome.OutputTail.Trim ()
                    onChunk { Text = output }
                    return AgentCompleted (sprintf "diagnostic: %s" output, None)
                | other -> return AgentFailed (sprintf "diagnostic command failed: %A" other, None)
        }

/// A built-in probe (`YESSION_SESSION_AGENT=usage-probe`, Plan 04): completes a turn with fixed,
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
//
// Read as the same `(envVar, value)` PAIR a connected credential resolves to, because two
// things need it now: the agent gate, which only asks whether there is one, and the models
// lookup, which has to present it. A second reading of the same two variables somewhere
// else is how the two would come to disagree about what "ambient" means.
let private ambientCredential () : (string * string) option =
    match Interop.envOr "ANTHROPIC_API_KEY" "" with
    | "" ->
        match Interop.envOr "CLAUDE_CODE_OAUTH_TOKEN" "" with
        | "" -> None
        | token -> Some ("CLAUDE_CODE_OAUTH_TOKEN", token)
    | key -> Some ("ANTHROPIC_API_KEY", key)

let private envCreds = (ambientCredential ()).IsSome

// The session's live view of connected credentials (Plan 08): fed by the Manager's
// connection-status stream, metadata only. Availability is DYNAMIC — a sign-in
// mid-session flips the agent gate without a relaunch.
let mutable private connectionStatus : Map<SecretId, ConnectionStatus> = Map.empty

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

// The fold over every checkout's `yession.yaml` (Plan 27). Filled once the Host exists,
// because it puts each declaration through the Host's own command gate — the same door the
// agent's `start_work_sandbox` goes through, which is the whole point.
let mutable private repoSandboxes : RepoSandboxes.RepoSandboxes = RepoSandboxes.none

// The terminal manager (Plan 25 needs it here for the `shell_profile` query and the command
// that changes it). Filled from the Host beside the sandboxes above, and for the same
// reason: the Host owns the log both are built over.
let mutable private terminals : SessionTerminals.SessionTerminals = SessionTerminals.unavailable

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
/// The stored credential a repo verb would spend for this actor, if any.
///
/// Factored out so that spending one and reporting one refused cannot pick different
/// targets. Two copies of this precedence would eventually disagree, and the way they would
/// disagree is the worst one available: marking a credential nobody used, while the one that
/// actually failed goes on reading as healthy.
let private githubTargetFor (credentialActor: ActorRef) : SecretId option =
    GitHubConnection.turnTargets sessionId credentialActor
    |> List.filter (fun target -> Map.containsKey target connectionStatus)
    |> List.tryHead

let private resolveGitHubToken (credentialActor: ActorRef) : Async<string option> =
    async {
        let targets = githubTargetFor credentialActor |> Option.toList
        let ambient () =
            match Interop.envOr "GITHUB_TOKEN" "" with
            | "" -> None
            | token -> Some token
        match connectionsClient, targets with
        | Some client, target :: _ ->
            match! client.Resolve target with
            | Ok (_, value) -> return Some value
            // A connected credential that will not resolve is a FAULT, not an absence:
            // since the grant can now refresh, this is exactly what a refresh failure
            // looks like. Falling back to the ambient token here made that present as
            // "git is anonymous", which sends whoever debugs it at the wrong thing —
            // and could silently use a different identity than the one they connected.
            // The ambient token stays what it always was: the answer when nothing is
            // connected at all.
            | Error reason ->
                eprintfn "[session %s] the connected github credential did not resolve: %s" (SessionId.value sessionId) reason
                return None
        | _ -> return ambient ()
    }

/// A repo network verb failed while spending a connected credential.
///
/// git cannot say whether the credential is why: `Repository not found` is what github.com
/// answers BOTH for a token that may not see a private repo and for a repo that is not
/// there, so reading its stderr would confuse an expired sign-in with a typo. GitHub itself
/// can tell them apart, so ask it, and record a refusal only when it confirms one.
///
/// One extra request, only on a path that has already failed — and it is what turns four
/// days of a green panel over a dead credential into a panel that says "sign in again".
let private reportGitHubNetworkFailure (credentialActor: ActorRef) (_gitSaid: string) : Async<unit> =
    async {
        match connectionsClient, githubTargetFor credentialActor with
        | Some client, Some target ->
            match! client.Resolve target with
            // The resolve path already reported this one, and its refusal is a better
            // answer than anything a second opinion could add.
            | Error _ -> return ()
            | Ok (_, token) ->
                match! GitHubConnection.refused token with
                | None -> return ()
                | Some reason ->
                    match! client.Reject target reason with
                    | Ok _ -> return ()
                    | Error e ->
                        eprintfn
                            "[session %s] could not report the refused github credential: %s"
                            (SessionId.value sessionId)
                            e
        | _ -> return ()
    }

/// What the session's gated commands are given: the services they run against, read as
/// getters because both the table and the per-turn bindings are built from cells the boot
/// async fills. The commands themselves live in `Commands.fs` — both halves of each, the
/// proposing one and the carrying-out one, beside each other where a test can reach them.
let private commandServices : Commands.CommandServices =
    { Repos = fun () -> reposService
      Sandboxes = fun () -> workSandboxes
      Terminals = fun () -> terminals
      Invalidate = fun name -> queryRegistry.Invalidate name
      // What a repo verb does to the configuration. Handed as a function rather than the
      // fold itself because the cell is filled after this record is built — and because a
      // command's business is to say WHEN the configuration may have changed, never to know
      // what reading it involves.
      Refold =
        fun actor ->
            async {
                do! repoSandboxes.Fold actor
                queryRegistry.Invalidate RepoSandboxes.queryName
                queryRegistry.Invalidate WorkSandboxes.queryName
            } }

/// The credential a party's calls on the provider run on (Plan 08): the session's own
/// explicit credential first, then the actor's — fresh from the Manager, which lazily
/// refreshes a due OAuth grant. `Ok None` is the ambient env, the documented last resort;
/// `Error` is the message a person can act on, which always points at the Connections
/// panel because that is where the fix is.
///
/// One resolution, two callers: the turn dispatcher below and the models lookup. They ask
/// the same question — whose credential does this party get — and a second copy of the
/// precedence would be a second answer waiting to drift.
/// The stored Claude credential this actor's calls run on, if any. Named once, for the same
/// reason `githubTargetFor` is: spending a credential and reporting one refused must never
/// pick different targets.
let private claudeTargetFor (actor: ActorRef) : SecretId option =
    ClaudeConnection.turnTargets sessionId actor
    |> List.filter (fun target -> Map.containsKey target connectionStatus)
    |> List.tryHead

let private resolveCredential (actor: ActorRef) : Async<Result<(string * string) option, string>> =
    async {
        let targets = claudeTargetFor actor |> Option.toList
        match connectionsClient, targets with
        | Some client, target :: _ ->
            match! client.Resolve target with
            | Ok (kind, value) -> return Ok (Some (ClaudeConnection.envVarFor kind value))
            | Error e ->
                if envCreds then return Ok None
                else return Error (sprintf "could not use the connected Claude account: %s" e)
        | _ ->
            if envCreds then return Ok None
            else
                return
                    Error (
                        sprintf
                            "no Claude account connected for %s — open Connections to sign in"
                            (ClaudeConnection.actorLabel actor))
    }

/// The models this session can run a turn on, looked up ONCE and kept for as long as the
/// session lives (`ModelCatalogue.cached`). The picker's whole supply, and the only place
/// the provider is asked what exists.
/// Ask the provider for its catalogue, and report a refusal of the credential we asked
/// with. This is the Claude counterpart of the GitHub check, and it is cheaper: the
/// catalogue request IS an authenticated call, so its status is a verdict already — no
/// second request, and nothing to parse.
///
/// It only ever runs where a lookup runs, and `ModelCatalogue.cached` keeps the first
/// SUCCESS for the session's life. So this catches a credential that was already dead, not
/// one that dies after a good lookup. For a brokered grant that gap is covered anyway — the
/// Manager sees the refresh refused — and it is only a pasted `sk-ant-` key, which cannot
/// refresh at all, that can die unseen mid-session.
let private askProvider
    (target: SecretId option)
    (credential: string * string)
    : Async<Result<AgentModel list, string>> =
    async {
        match! ClaudeConnection.models credential with
        | Ok models -> return Ok models
        | Error failure ->
            match target, connectionsClient with
            | Some target, Some client when failure.Refused ->
                let! _ = client.Reject target "the provider rejected this Claude sign-in"
                ()
            | _ -> ()
            return Error failure.Message
    }

let private listModels : ListModels =
    ModelCatalogue.cached (fun actor ->
        async {
            match! resolveCredential actor with
            | Error reason -> return Error reason
            | Ok (Some credential) -> return! askProvider (claudeTargetFor actor) credential
            | Ok None ->
                match ambientCredential () with
                // The ambient token is nobody's connection, so a refusal of it has no stored
                // credential to mark — only the picker's note to explain it.
                | Some credential -> return! askProvider None credential
                // Unreachable: `Ok None` means the ambient credential is what a turn would
                // run on, and that is exactly what `ambientCredential` has just answered.
                | None -> return Error "no credential to ask the provider with"
        })

/// Per-turn credential dispatch (Plan 08): the turn runs on `resolveCredential`'s answer
/// for its actor. With no credential at all the turn fails gracefully, saying so.
let private dispatching (inner: (string * string) option -> RunAgent) : RunAgent =
    fun context capabilities signal onChunk ->
        async {
            // The command verbs are rebound to THIS turn's actor here (Plan 14, Plan 15
            // stage 2): the acting party on the events is the agent, the credential is the
            // turn human's. The query surface is bound in the same place for a duller
            // reason — the registry is built in the boot async, and this is where a turn
            // first sees it.
            let capabilities = Commands.bindFor commandServices context.TurnActor capabilities
            let capabilities =
                { capabilities with
                    Queries =
                      { capabilities.Queries with
                          Declared = queryRegistry.Definitions
                          Read = queryRegistry.Read } }
            // A dispatch-level failure says nothing of its own: the reason is carried by
            // `AgentFailed`, and the conversation projection gives it an item where the turn
            // stopped. This used to stream the reason as a delta first, back when a turn that
            // said nothing failed into a silent red item — which by then meant every such
            // failure was printed twice, once as the body and once joined to it.
            let fail (reason: string) = AgentFailed (reason, None)
            match! resolveCredential context.TurnActor with
            | Ok credential -> return! inner credential context capabilities signal onChunk
            | Error reason -> return fail reason
        }

/// A built-in probe (`YESSION_SESSION_AGENT=credential-probe`): completes immediately, naming
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
    match Interop.envOr "YESSION_SESSION_AGENT" "" with
    | "diagnostic" -> Some diagnosticAgent
    | "usage-probe" -> Some usageProbeAgent
    | "credential-probe" ->
        if envCreds || connectedSomewhere () then Some (dispatching credentialProbe) else None
    | _ ->
        if envCreds || connectedSomewhere () then Some (dispatching (Agent.runWith dataDir)) else None

[<Fable.Core.Emit("(function (handler) { return (process.stdin.on('close', handler), process.stdin.on('end', handler), process.stdin.resume()) })($0)")>]
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
                      // What the verbs SAY a checkout is at is the work sandbox's view of
                      // it, not the git sandbox's: nobody runs a build in the git sandbox.
                      // Relative to where a terminal starts, when it can be: `repos/…` is
                      // what anyone here can act on, and `set_shell_profile` resolves it
                      // against the same root. The absolute path stays the sandbox's.
                      VisibleAt =
                        SandboxPath.reachedFrom
                            (workspaceFor SandboxRef.defaultRef)
                            (Sandboxes.reposVisibleAt workBackend reposDir)
                      ExtraReadPaths = []
                      Git = Repos.gitExecutable (Sandboxes.ambientEnv ())
                      AllowedDomains = [ "github.com" ]
                      AllowProtocol = "https"
                      CloneUrl = RepoRef.cloneUrl
                      ResolveToken = resolveGitHubToken
                      OnNetworkFailure = reportGitHubNetworkFailure
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
                  OperatorResources.query (fun () -> resourceProfile)
                  ShellProfile.query (fun () -> terminals)
                  RepoSandboxes.query (fun () -> repoSandboxes)
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
                // The WHOLE status, not just its kind. What a session may read and whether
                // it still works arrive on the same frame, and keeping only half of it was
                // why a panel could show a green dot over a credential the Manager already
                // knew was finished.
                connectionStatus <-
                    list.Connections |> List.map (fun s -> s.Id, s) |> Map.ofList)
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
            // The model catalogue: gated by the same cookie identity, and — unlike the two
            // connection panels — needing no control channel of its own, because the
            // credential it asks with is resolved through the one they already use.
            let modelRoutes =
                match auth with
                | Some a -> Some (ModelRoutes.routes a listModels sessionMount)
                | None -> None
            [ claudeRoutes; githubRoutes; queryRoutes; modelRoutes ]
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
        terminals <- host.Terminals
        repoSandboxes <-
            RepoSandboxes.create
                reposDir
                (fun () -> reposService)
                (fun () -> workSandboxes)
                host.RunGated
                log
                // The same rendering the operator's `resources` surface uses, so what a repo
                // is said to ask for and what an operator reads about a name cannot become
                // two answers.
                (fun selection ->
                    match selection, resourceProfile with
                    | [], _ -> Ok { RepoSandboxes.Granted = []; RepoSandboxes.Sensitive = false }
                    | _, None -> grantsFor selection |> Result.map (fun _ -> { RepoSandboxes.Granted = []; RepoSandboxes.Sensitive = false })
                    | selection, Some file ->
                        ResourceProfile.resolve file.Resources selection
                        |> Result.map (fun closure ->
                            { RepoSandboxes.Granted = ResourceClosure.describe closure
                              RepoSandboxes.Sensitive = ResourceClosure.isSensitive closure }))
        // How each gated command is carried out, handed to the gate the Host owns. Here —
        // and not closed over a turn — because the table is built from the services, and the
        // services are composed here.
        host.SetCommandDispatch (Commands.dispatch commandServices)
        // Consent reaches the fold that knows what each repo asks for.
        host.SetApproveCapabilities (fun actor repo granted -> repoSandboxes.Approve actor repo granted)
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
            // The address is the configured public one, inherited from
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
        if launch.ParentGuard then
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
        // The first fold (Plan 27) — AFTER readiness, and fire-and-forget, for the same
        // reason the MCP handshake is: a declaration can be a container to pull, and a boot
        // that waited for one would look to the Manager like a session that failed to
        // start. Nobody triggered this one, so it runs on nothing's authority; what it made
        // of each file is the `repo_config` query's answer, live from the moment it lands.
        Async.StartImmediate (
            async {
                do! repoSandboxes.Fold None
                queryRegistry.Invalidate RepoSandboxes.queryName
                queryRegistry.Invalidate WorkSandboxes.queryName
            })
    })
