module Yession.Host.Sandboxes

// The sandbox backends behind the `CreateSandbox` seam. Every decision — backend
// choice, environment assembly, policy construction — is a pure F# function here,
// unit-testable in the cheap tier; the `[<Emit>]` blocks below stay dumb (spawn, wire
// streams, kill) and never decide anything.
//
// The host backend passes the policy env to the child VERBATIM: no backend ever merges
// `process.env` into a spawned command again — that merge (the old Manager-side
// LocalProcessBackend) is exactly how `ANTHROPIC_API_KEY` leaked into every
// agent-issued command.

open Fable.Core
open Fable.Core.JsInterop
open Yession.Domain
open Yession.Domain.Sandboxes

// --- Pure policy assembly ----------------------------------------------------------------

/// The variables a host-backend command may inherit from the Session Process's own
/// environment: what a shell needs to resolve and run programs, and nothing that
/// authenticates anything. An allowlist, so a variable is shared by decision, never by
/// default.
let hostBaselineNames : string list =
    [ "PATH"; "HOME"; "TMPDIR"; "TMP"; "TEMP"; "LANG"; "LC_ALL"; "LC_CTYPE"; "TZ"; "USER"; "LOGNAME"; "SHELL" ]

/// Filter an ambient environment down to the host baseline.
let hostBaseline (ambient: Map<string, string>) : Map<string, string> =
    ambient |> Map.filter (fun name _ -> List.contains name hostBaselineNames)

/// Right-biased merge: `overrides` win over `baseline`.
let mergeEnv (baseline: Map<string, string>) (overrides: Map<string, string>) : Map<string, string> =
    overrides |> Map.fold (fun acc key value -> Map.add key value acc) baseline

/// Resolve the spec's environment variables: plain values verbatim, secret references
/// through the injected resolver. Called fresh at every sandbox (re)creation — the
/// resolved plaintext goes into the policy env and nowhere else.
let resolveVariables
    (resolveSecret: SecretName -> Async<Result<string, string>>)
    (variables: Map<string, EnvironmentVariableRef>)
    : Async<Result<Map<string, string>, string>> =
    let rec walk acc entries =
        async {
            match entries with
            | [] -> return Ok (Map.ofList (List.rev acc))
            | (name, PlainValue value) :: rest -> return! walk ((name, value) :: acc) rest
            | (name, SecretRef secret) :: rest ->
                match! resolveSecret secret with
                | Error e -> return Error (sprintf "%s: %s" (SecretName.value secret) e)
                | Ok value -> return! walk ((name, value) :: acc) rest
        }
    walk [] (Map.toList variables)

/// Assemble a sandbox policy for the configured backend. Host (and srt) sandboxes get
/// the baseline allowlist under the spec's variables; a docker image supplies its own
/// base environment, so only the spec's variables inject there.
/// The domains the WORK sandbox may reach, as configured. `None` is unrestricted, which
/// is what the unconfining backends are and all srt can honestly report for them; srt
/// itself has no unrestricted mode, so a confined sandbox always carries a list —
/// `YESSION_SESSION_WORK_NET` (comma- or space-separated), the counterpart of the agent's
/// `YESSION_SESSION_AGENT_NET`.
///
/// Empty where the operator named none, and deliberately so: what hosts an agent's
/// commands legitimately need is not something this code can guess, and egress is the
/// side to fail closed on.
/// One operator statement, comma- or space-separated. Absent and empty are the same list,
/// which is what makes an unset variable and a blank one impossible to tell apart on
/// purpose: neither says anything.
let private listFrom (raw: string option) : string list =
    raw
    |> Option.map (fun text ->
        text.Split ([| ','; ' '; '\t'; '\n' |])
        |> Array.map (fun entry -> entry.Trim ())
        |> Array.filter (fun entry -> entry <> "")
        |> List.ofArray)
    |> Option.defaultValue []

/// Where a session's workspaces and its checkouts sit under its data directory.
///
/// One module because these are not independent facts. A terminal starts in the
/// workspace, so a checkout that is not under it is a checkout nobody sees without being
/// told its absolute path first — and an agent told to find one spends the turn looking.
/// Computed apart, in the composition root, they drifted into siblings: `ls` in
/// a fresh terminal showed an empty directory while the clones sat next door.
module SessionLayout =

    /// The workspace a host-family sandbox works in. `default` keeps the session's own
    /// `workspace` path, so nothing about an existing session changes; a named one gets
    /// its own, because two sandboxes exist precisely so that what happens in one does
    /// not happen in the other.
    let workspaceFor (dataDir: string) (sandbox: SandboxRef) : string =
        if sandbox = SandboxRef.defaultRef then sprintf "%s/workspace" dataDir
        else sprintf "%s/sandboxes/%s/workspace" dataDir (SandboxRef.slug sandbox)

    /// A work sandbox's own HOME, beside its workspace.
    ///
    /// Every toolchain keeps state under `$HOME` — dotnet a CLI home, npm a cache, uv and
    /// cargo their own — and a sandbox that inherits the operator's cannot write a byte of
    /// it. That is worse than having none: a tool with no HOME falls back, a tool with a
    /// HOME it cannot touch fails. dotnet reports it as "The user's home directory could not
    /// be determined."
    ///
    /// Not a new idea here — `agentHome` below has given the agent CLI exactly this since
    /// Plan 08, for exactly this reason. This is the same arrangement for the sandboxes a
    /// person's commands run in, and it is `workspaceFor`'s shape so the two cannot drift:
    /// `default` keeps the session's own directory, a named one gets its own.
    let homeFor (dataDir: string) (sandbox: SandboxRef) : string =
        if sandbox = SandboxRef.defaultRef then sprintf "%s/home" dataDir
        else sprintf "%s/sandboxes/%s/home" dataDir (SandboxRef.slug sandbox)

    /// Make a sandbox's home, and put in it whatever the sandbox asked to find there.
    ///
    /// ONE verb, because a caller that made the directory and forgot the seeding would
    /// leave a sandbox whose toolchain fails on first use in a way nothing here explains —
    /// which is the whole failure this exists to prevent. `prepareTmpDir` above is the same
    /// shape for the same reason.
    ///
    /// Seeds are written only where nothing is already there. A home outlives a sandbox
    /// restart, so a tool that has since written its own version of one of these files owns
    /// it: overwriting on every start would silently undo work the sandbox did, and a seed
    /// is a starting point rather than a policy.
    ///
    /// `HomePath` has already refused anything that could land outside the home, so there
    /// is no second check here — one rule, in the type, reachable by the cheap tier.
    let prepareHome (home: string) (files: Map<HomePath, string>) : unit =
        Fs.ensureDir home
        for KeyValue (path, content) in files do
            let target = sprintf "%s/%s" (home.TrimEnd '/') (HomePath.value path)
            if not (Fs.exists target) then Fs.writeTextAtomic target content

    /// The session's repos directory, INSIDE the workspace a terminal opens in. One
    /// directory for the whole session — the git verbs clone into it, every work sandbox
    /// reads and builds it — so a NAMED sandbox reaches it at this same absolute path,
    /// carried as a write path rather than as a child of its own workspace.
    let reposDir (dataDir: string) : string =
        sprintf "%s/repos" (workspaceFor dataDir SandboxRef.defaultRef)

    /// The agent CLI's per-session scratch HOME. The CLI writes `~/.claude` state, which
    /// lives — and dies — with the session's data directory rather than the real HOME.
    /// Here with the other paths derived from a data dir, so the layout has one owner.
    let agentHome (dataDir: string) : string = sprintf "%s/agent-home" dataDir

    /// The session's temporary directory, prepared and ANNOUNCED to srt.
    ///
    /// One verb, because the three things it does are one decision seen three ways and a
    /// caller that did two of them would leave a sandbox pointed at a directory nothing
    /// allows. srt reads `CLAUDE_CODE_TMPDIR` from THIS process at wrap time and bakes the
    /// result into the child's `TMPDIR`, so the path srt will use and the path the policy
    /// allows have to be settled together or not at all.
    ///
    /// Canonical, for the reason `OperatorResources` refuses a path that is not: srt's own
    /// default is `/tmp/claude`, `/tmp` is a symlink on macOS, and a sandbox holding exactly
    /// that write path is denied it — measured, `W DENY /tmp/claude` in a sandbox whose
    /// policy named it. The same fault, arriving through a path this code writes rather than
    /// one an operator did.
    ///
    /// Under the session's data directory rather than a shared `/tmp/claude`, which is the
    /// second thing wrong with the default: every session on a host shared one temp
    /// directory, so a name one of them chose was a name the others could read and collide
    /// with. Per PROCESS, not per sandbox — srt reads this once from the process env, and
    /// mutating it per wrap is a race — so both sandboxes of one session share it, and that
    /// is said out loud rather than implied by a path that looks per-sandbox.
    let prepareTmpDir (dataDir: string) : string =
        let path = sprintf "%s/tmp" dataDir
        Fs.ensureDir path
        let settled = Fs.canonical path |> Option.defaultValue path
        Interop.setEnv "CLAUDE_CODE_TMPDIR" settled
        settled

    /// Where the sandbox's temporary files go, read from the same place srt reads it.
    ///
    /// Not a copy of the decision — the decision is `prepareTmpDir` above, which is what puts
    /// the value there. This asks what it settled on, so the three readers of it (the write
    /// list, the sandbox's own `TMPDIR`, and the child srt wraps) cannot become three
    /// answers. They already did once: the policy inherited the MANAGER's `/tmp` as `TMPDIR`,
    /// srt overrode the child to the session's own, and the two disagreed silently until a
    /// start-up check asked whether the policy's `TMPDIR` was writable and it was not.
    ///
    /// The fallback is srt's own, for a process that never prepared one (a test building a
    /// policy directly). It is what srt would use in that case, so they still agree.
    let tmpDir () : string = Interop.envOr "CLAUDE_CODE_TMPDIR" "/tmp/claude"

    /// Where a session that predates the layout above left its checkouts: beside the
    /// workspace instead of inside it.
    let legacyReposDir (dataDir: string) : string = sprintf "%s/repos" dataDir

    /// The session's repos directory, ready to be cloned into: anything a pre-layout
    /// session left adopted, the directory itself created, the path returned.
    ///
    /// One verb rather than two calls, because the order between them is load-bearing and
    /// invisible — creating the directory first is exactly what would make the adoption
    /// find a live layout and decline — and the caller that gets that wrong strands
    /// somebody's work without any of this failing.
    ///
    /// The adoption is there because a session's data directory outlives its process, which
    /// is what makes a checkout survive idle reaping and relaunch. A session cloned into
    /// before this layout relaunches with its repos at the old path; left there they are
    /// not deleted, they are INVISIBLE — the listing scans the new path, reports nothing,
    /// and the agent re-clones over work that was never committed. It declines when both
    /// paths exist, because a rename onto a non-empty directory throws and this runs at
    /// boot: a session that somehow had both would fail to start instead. Delete the
    /// adoption once no live session predates the layout.
    let prepareReposDir (dataDir: string) : string =
        let legacy = legacyReposDir dataDir
        let current = reposDir dataDir
        if legacy <> current && Fs.exists legacy && not (Fs.exists current) then
            Fs.ensureDir (workspaceFor dataDir SandboxRef.defaultRef)
            Fs.rename legacy current
        Fs.ensureDir current
        current

/// Where the session's repos directory is reachable from INSIDE a work sandbox. The
/// host-family backends share the directory itself (srt binds the same absolute path, so
/// the name does not change); the docker backend cannot share a host path by policy and
/// carries it as a bind mount instead, where it is reachable under the target and nothing
/// else.
///
/// One function because three things have to name the same place and cannot be allowed to
/// drift: the mount, the sandbox's write path, and — the half that was missing — the
/// ANSWER a repo verb gives. A checkout the agent cannot name is a checkout it hunts for,
/// and hunting is what the turn gets spent on.
let reposVisibleAt (backend: SandboxBackend) (hostReposDir: string) : string =
    match backend with
    | HostBackend
    | SrtBackend -> hostReposDir
    | DockerBackend -> "/repos"

/// A repo's checkout as its OWN work sandbox will see it — the root that repo's
/// `workdir:` resolves against (`SandboxDecl.toRequest`). Repo-owned work runs under
/// `SandboxRuntime.repoWorkBackend`, so this is that backend's view of the repos
/// directory, never a terminal's: resolved against the terminal's view, `workdir: .`
/// produced a container whose working directory wore the HOST checkout path — an empty
/// volume mounted where nothing would ever look, while the checkout sat under the
/// /repos bind.
let workCheckoutAt (reposDir: string) (repo: RepoRef) : string =
    sprintf "%s/%s" (reposVisibleAt SandboxRuntime.repoWorkBackend reposDir) (RepoRef.relativePath repo)

/// The same checkout in both its addresses (`CheckoutViews`): the sandbox's own view for
/// `workdir:`, the host's for a `build:` context the daemon client reads from THIS
/// filesystem. `SandboxDecl.toRequest` takes the pair so each path resolves against the
/// view its reader will use.
let checkoutViewsAt (reposDir: string) (repo: RepoRef) : CheckoutViews =
    { InSandbox = workCheckoutAt reposDir repo
      OnHost = sprintf "%s/%s" reposDir (RepoRef.relativePath repo) }

/// What an operator's granted leaves add to a policy.
///
/// The operator's side of the ceiling/grant split. `YESSION_SESSION_READ` was a bound AND an
/// unconditional grant, so an operator could not offer a path without forcing it on
/// everything, and a repo asking for one could never obtain it. A resource in the profile's
/// `default` is the grant half, said once, by name, and visible in the `resources` query.
///
/// Not a second policy engine: this only maps primitives onto fields a policy already has.
/// What may be granted at all was settled by the algebra before anything reached here.
[<Emit("process.platform")>]
let private platform () : string = jsNative

/// What each backend on this host can actually distinguish about a grant.
///
/// Here, beside the backends, because it is a statement a backend makes about ITSELF — the
/// algebra takes it and cannot invent one, which is why `HostLimits` is private to its
/// module. Wrong here is a lie the whole layer then tells accurately.
///
/// srt splits by platform, and that split IS the fault this exists for: it scopes a unix
/// socket by path on macOS, through a `network-outbound` rule on the path, and cannot on
/// Linux, where the filter is seccomp-bpf and cannot read a socket path out of user-space
/// memory. Egress it scopes on both, through its own proxy.
///
/// docker binds a socket into the container by path, so it scopes that; it filters no egress
/// at all, which until now was `AllowedDomains = None` and unsaid. Neither has a union mount,
/// which is why no backend claims `OverlayMounts` — it is in the vocabulary because it is a
/// real distinction and this host cannot make it, not because somebody might.
///
/// The host backend confines nothing, so nothing is scoped and nothing is coarsened: a policy
/// it cannot enforce is not a degradation, it is the absence of a sandbox, said elsewhere.
let limitsFor (backend: SandboxBackend) (platform: string) : HostLimits =
    match backend with
    // Every CONFINEMENT distinction and not `unlimited`: allowing everything answers a
    // question about bounds. The PROVISIONS stay out — `NamedVolumes` because the host
    // backend has no volumes to provide, and `OverlayMounts` because a union mount is
    // equally something to provide, not something to permit: claimed here, an overlay
    // grant realised as-asked and died downstream with an internal-bug sentence, where
    // unclaimed it is withheld with the one that tells the operator what to write.
    | HostBackend ->
        HostLimits.of' [ HostDistinction.SocketsByPath; HostDistinction.EgressByHost ]
    | SrtBackend ->
        if platform = "darwin" then
            HostLimits.of' [ HostDistinction.SocketsByPath; HostDistinction.EgressByHost ]
        else HostLimits.of' [ HostDistinction.EgressByHost ]
    | DockerBackend -> HostLimits.of' [ HostDistinction.SocketsByPath; HostDistinction.NamedVolumes ]

/// What a backend can distinguish on the host this process is running on.
///
/// The one place that asks the process what it is. `limitsFor` takes the platform as an
/// argument precisely so that every claim about it is checkable from either machine, and a
/// second caller reading `process.platform` for itself would be a second answer that only
/// ever agrees with the first on the box it was written on.
let limitsHere (backend: SandboxBackend) : HostLimits = limitsFor backend (platform ())

/// What one set of granted leaves comes to, each channel beside the others because they
/// are one fact read by different consumers: the host family closes path SETS over the
/// host's own spellings (`Reads`/`Writes`), the container backend materialises each
/// mount at its declared target (`Binds`, `Volumes`), and both read the rest alike.
type GrantedLeaves =
    { Reads : string list
      Writes : string list
      Domains : string list
      Sockets : string list
      Env : Map<string, string>
      Binds : ResourceMount list
      Volumes : (string * string) list }

let grantsFrom (leaves: ResourceLeaf list) : Result<GrantedLeaves, string> =
    let rec fold (acc: GrantedLeaves) remaining =
        match remaining with
        | [] ->
            Ok
                { acc with
                    Reads = List.rev acc.Reads
                    Writes = List.rev acc.Writes
                    Domains = List.rev acc.Domains
                    Sockets = List.rev acc.Sockets
                    Binds = List.rev acc.Binds
                    Volumes = List.rev acc.Volumes }
        | leaf :: rest ->
            match leaf with
            | Mount mount ->
                match mount.Mode with
                | ResourceMountMode.Read ->
                    fold { acc with Reads = mount.From :: acc.Reads; Binds = mount :: acc.Binds } rest
                | ResourceMountMode.Write ->
                    fold { acc with Writes = mount.From :: acc.Writes; Binds = mount :: acc.Binds } rest
                // Unreachable, and deliberately not a second refusal. An overlay is WITHHELD
                // by the realisation before it gets here — `HostDistinction.OverlayMounts` is
                // a distinction no backend on this host claims — so a leaf arriving in this
                // arm means something built a policy without realising it first, which is a
                // bug in the caller and not a configuration this can explain.
                | ResourceMountMode.Overlay ->
                    Error (
                        sprintf
                            "%s reached a policy without being realised against this host — an overlay is withheld, never granted"
                            mount.From)
            // Its own axis, not two file grants. Connecting to a unix socket is a
            // permission of its own — macOS wants `network-outbound` on the path — and the
            // file halves do not add up to it: measured, a sandbox holding this readable
            // and writable, with `test -S` passing, was still refused by nix with "could
            // not connect to any lix socket".
            //
            // Still read and written too, because talking to one does both, and the
            // node has to be reachable before it can be connected to.
            | Socket path ->
                fold
                    { acc with
                        Reads = path :: acc.Reads
                        Writes = path :: acc.Writes
                        Sockets = path :: acc.Sockets }
                    rest
            | Endpoint host -> fold { acc with Domains = host :: acc.Domains } rest
            | Variable (name, value) -> fold { acc with Env = Map.add name value acc.Env } rest
            | Exec path -> fold { acc with Reads = path :: acc.Reads } rest
            // Only the container backend can hold one — everywhere else the realisation
            // withheld it before this fold ran — so this channel is filled exactly where
            // it can be consumed.
            | Volume (name, at) -> fold { acc with Volumes = (name, at) :: acc.Volumes } rest
    fold
        { Reads = []; Writes = []; Domains = []; Sockets = []; Env = Map.empty; Binds = []; Volumes = [] }
        leaves

/// The directories of granted executables, for PATH.
let grantedPath (leaves: ResourceLeaf list) : string list =
    leaves
    |> List.choose (function
        | Exec path ->
            match path.LastIndexOf '/' with
            | index when index > 0 -> Some (path.Substring (0, index))
            | _ -> None
        | _ -> None)
    |> List.distinct


let policyFor
    (backend: SandboxBackend)
    /// What this host can express — `limitsFor backend (platform ())` in production, and the
    /// other platform's answer in a test.
    (limits: HostLimits)
    (ambient: Map<string, string>)
    (resolved: Map<string, string>)
    (workspace: string option)
    // The session's repos directory (Plan 14), shared into the WorkSandbox so a
    // bootstrap clone is visible the moment it lands. A write path in its own right
    // under the host-family backends, whether or not this sandbox's workspace already
    // contains it — the DEFAULT sandbox's does (`SessionLayout`), a named one's does
    // not, and a path named twice costs nothing while a path named never costs the
    // checkout. The docker backend carries it as a bind mount on the spec instead
    // (`SessionMain`), so it is None there.
    (reposDir: string option)
    // This sandbox's own HOME (`SessionLayout.homeFor`), which it may write and which is
    // what `$HOME` says inside it. `None` only where the backend supplies its own — docker,
    // whose image has a home of its own that this process did not create.
    (home: string option)
    // What the OPERATOR grants every sandbox on this host without it asking — the profile's
    // `default`, already flattened. Distinct from the ceiling below: this is given, that is
    // merely allowed, and `YESSION_SESSION_READ` being both at once is the defect this pair
    // exists to separate.
    (granted: ResourceLeaf list)
    // What this sandbox ASKED to reach. Reconciled here, and only here, because here is
    // where the operator's ambient environment and the (partly repo-authored) spec are both
    // in hand — a caller that reconciled first would be a second copy of the ceiling rule.
    (spec: EnvironmentSpec)
    : Result<SandboxPolicy, string> =
    // What THIS host can actually make of it, before anything is built from it. The third
    // narrowing, and the only one that can widen — so it happens here, once, rather than in
    // each backend where three copies could disagree.
    //
    // The limits are PASSED, not discovered: a function that reached for `process.platform`
    // could only ever be tested on the platform running it, and the one thing worth testing
    // here is the host that cannot do what this one can.
    let realised = RealisedClosure.of' limits (Set.ofList granted)
    let differences = RealisedClosure.differences realised
    // A leaf this host cannot give AT ALL refuses the sandbox, and a leaf it gives more
    // coarsely does not. That is the whole difference between a degradation and a refusal —
    // a sandbox that works differently against one that does not work — and it is decided
    // here rather than by each caller's judgement about which is which.
    match differences |> List.tryPick (fun (leaf, outcome) ->
            match outcome with
            | LeafRealisation.Withheld because -> Some (leaf, because)
            | _ -> None) with
    | Some (leaf, because) ->
        Error (sprintf "this host cannot grant %s: %s" (ResourceLeaf.describe leaf) because)
    | None ->

    // First, because everything below reads it: what the operator already gave, as this host
    // will actually give it.
    match grantsFrom (RealisedClosure.held realised |> Set.toList) with
    | Error e -> Error e
    | Ok granted' ->

    // The path lists in the SANDBOX's own spelling, which differs by family: the host
    // family confines the host's paths (a grant's `from`), while a container sees a
    // grant only where it was mounted (its `at`) plus its granted volumes. The start-up
    // checks probe these lists from INSIDE the sandbox, so the wrong spelling here
    // reports a healthy container broken — or proves nothing about a broken one.
    // (A granted executable stays host-family business: a host binary is not runnable
    // in a container's userland, so the docker arm does not list it.)
    let grantedReads, grantedWrites =
        match backend with
        | HostBackend
        | SrtBackend -> granted'.Reads, granted'.Writes
        | DockerBackend ->
            let at mode =
                granted'.Binds
                |> List.filter (fun b -> (b.Mode = ResourceMountMode.Read) = mode)
                |> List.map (fun b -> b.At)
            (at true) @ granted'.Sockets,
            (at false) @ granted'.Sockets @ (granted'.Volumes |> List.map snd)
    let grantedDomains = granted'.Domains
    let grantedSockets = granted'.Sockets
    let grantedEnv = granted'.Env

    // What every docker sandbox starts with before grants and its own spec: the
    // backend's own consequences, said by the backend rather than copied into every
    // repo's file (where this trio sat for a while, waiting to be cargo-culted).
    //
    // The trio is `safe.directory` in git's own env spelling. The session bind-mounts
    // its checkouts into the container, so they arrive owned by the operator's uid
    // while the container runs as the image's user — and git refuses to read a
    // repository its caller does not own. The `*` is wider than "the session's own
    // checkouts" now that mountPlan mounts operator-granted binds and volumes too: it
    // trusts a git repository ANYWHERE the sandbox can see, including one sitting in a
    // granted bind. That is still the right scope — a mount is granted by the operator
    // or declared by the repo, so everything visible was put there by somebody this
    // trio answers for — but the sentence it rests on is "every mount is consented",
    // not "every mount is a checkout". Baseline, so a spec that names any of these
    // keys still wins, like the rest of the baseline.
    let dockerBaseline =
        Map.ofList
            [ "GIT_CONFIG_COUNT", "1"
              "GIT_CONFIG_KEY_0", "safe.directory"
              "GIT_CONFIG_VALUE_0", "*" ]
    let env =
        // The sandbox's own home replaces the inherited one IN THE BASELINE, so a spec that
        // names `HOME` still wins — the rule that the spec beats the baseline is one rule,
        // and carving `HOME` out of it would make it two.
        //
        // What this displaces is the Session Process's own `HOME`, which is the operator's:
        // a directory the sandbox is denied. A tool given no home falls back; a tool given
        // one it cannot touch fails, which is the shape dotnet reports as "The user's home
        // directory could not be determined."
        let inherited =
            // `TMPDIR` beside `HOME`, and for the same reason: the inherited one is the
            // MANAGER's, which the sandbox cannot write. srt then overrides the child's to
            // the session's own, so leaving the manager's here made the policy state one
            // thing and the child receive another — and a policy that lies about what it
            // grants is what the start-up checks read.
            let baseline = Map.add "TMPDIR" (SessionLayout.tmpDir ()) (hostBaseline ambient)
            match home with
            | Some home -> Map.add "HOME" home baseline
            | None -> baseline
        // Granted variables sit UNDER the spec's, like the rest of the baseline: an operator
        // says what a sandbox starts with, and a repo may still say otherwise about its own.
        // Granted executables join PATH in FRONT of the inherited one, so a toolchain the
        // operator named is the one that answers.
        let fromGrants =
            match grantedPath granted with
            | [] -> grantedEnv
            | directories ->
                let combined =
                    match inherited |> Map.tryFind "PATH" with
                    | None | Some "" -> String.concat ":" directories
                    | Some existing -> String.concat ":" directories + ":" + existing
                Map.add "PATH" combined grantedEnv
        match backend with
        | HostBackend
        | SrtBackend -> mergeEnv (mergeEnv inherited fromGrants) resolved
        | DockerBackend -> mergeEnv (mergeEnv dockerBaseline fromGrants) resolved
    Ok
        { ReadPaths = grantedReads
          WritePaths =
            (workspace |> Option.toList)
            @ (reposDir |> Option.toList)
            @ (home |> Option.toList)
            @ grantedWrites
          // Every domain a sandbox may reach is one an operator named in a resource
          // this sandbox holds. There is no ceiling to reconcile against any more,
          // because there is nothing left to reconcile: a repo selects names, and a
          // name it was not offered does not resolve.
          //
          // `None` is the unconfining backends, which restrict this axis not at all.
          AllowedDomains =
            match backend with
            | HostBackend
            | DockerBackend -> None
            | SrtBackend -> Some (List.distinct grantedDomains)
          Sockets = List.distinct grantedSockets
          // Both container channels filled from the same fold as the lists above — one
          // fact, several consumers. Under the host family `Volumes` is structurally
          // empty (the leaf is withheld by realisation) and `Binds` is unread.
          Binds = granted'.Binds
          Volumes = granted'.Volumes
          Realisation = differences
          Env = env
          // What the sandbox ASKED to start in, RESOLVED — the workspace when it asked
          // for nothing, and otherwise the spec's path acquiring its meaning here.
          //
          // `toRequest` resolves a repo's `workdir` against the checkout as everything
          // OUTSIDE a sandbox says it, and that vocabulary is relative on purpose:
          // `SessionMain` hands out `repos/octo/hello` (via `SandboxPath.reachedFrom`)
          // so a person, `set_shell_profile` and the repo verbs all say one short
          // thing. What comes back is therefore relative, and this is the last place
          // holding the root it is relative TO — so this is where it stops being a
          // path in that vocabulary and becomes a directory.
          //
          // It used to say `Option.orElse workspace`, which honoured the relative form
          // verbatim: a repo declaring `workdir: .` produced a policy whose directory
          // was `repos/octo/hello`, the backend then `mkdir`'d it against the SESSION
          // PROCESS's cwd, and every repo-declared sandbox failed to start with
          // `ENOENT: no such file or directory, mkdir 'repos/octo/hello'`. It is also
          // the root `resolvedFrom` resolves each spawn's own directory against, so a
          // relative one made every later resolution relative too.
          //
          // Before that it was `workspace` outright, which dropped a repo's `workdir`
          // entirely — a file that read as applied and was not. `resolvedFrom` is both
          // fixes at once, and it is `reachedFrom`'s own inverse: the pair that made
          // the path short is the pair that makes it a directory again.
          //
          // The write root stays the workspace either way (`WritePaths` above): a
          // sandbox that starts in its checkout still writes where it always did.
          WorkingDirectory = SandboxPath.resolvedFrom workspace spec.WorkingDirectory
          Filesystem = Confined }

/// A one-line description of the backend + spec for the start-requested event.
let summaryFor (backend: SandboxBackend) (spec: EnvironmentSpec) : string =
    match spec.Runtime with
    | Container { Image = Some image } -> sprintf "docker:%s" (ContainerImage.render image)
    | Container _
    | Confinement -> SandboxBackend.describe backend

[<Emit("Object.entries(process.env).filter(([, v]) => typeof v === 'string')")>]
let private ambientEntries () : (string * string) array = jsNative

/// The Session Process's own environment, as data (the input `policyFor` filters).
let ambientEnv () : Map<string, string> = ambientEntries () |> Map.ofArray

/// The per-(re)creation policy thunk `SessionEnvironment.create` consumes: resolve the
/// spec's secret references fresh, then assemble the policy.
let preparePolicy
    (backend: SandboxBackend)
    (resolveSecret: SecretName -> Async<Result<string, string>>)
    (workspace: string option)
    (reposDir: string option)
    (home: string option)
    // What this sandbox holds, resolved: the operator's `default` together with whatever the
    // sandbox itself selected. ONE function rather than a list and a resolver, because the
    // two are the same question asked about different names, and a caller that could resolve
    // one without the other would be a caller that could forget half.
    (grantsFor: ResourceName list -> ResourceName list -> Result<ResourceLeaf list, string>)
    (spec: EnvironmentSpec)
    : unit -> Async<Result<SandboxPolicy, string>> =
    fun () ->
        async {
            match! resolveVariables resolveSecret spec.EnvironmentVariables with
            | Error e -> return Error e
            | Ok resolved ->
                match grantsFor spec.Uses spec.Wants with
                | Error e -> return Error e
                | Ok granted ->
                    return policyFor backend (limitsHere backend) (ambientEnv ()) resolved workspace reposDir home granted spec
        }

// --- A buffered one-shot: settle once, deliver to every (even late) awaiter --------------

type private OneShot<'a> () =
    let mutable outcome : 'a option = None
    let mutable waiters : ('a -> unit) list = []
    member _.Settle (value: 'a) =
        match outcome with
        | Some _ -> ()
        | None ->
            outcome <- Some value
            let pending = waiters
            waiters <- []
            pending |> List.iter (fun w -> w value)
    member _.Await : Async<'a> =
        Async.FromContinuations (fun (cont, _, _) ->
            match outcome with
            | Some value -> cont value
            | None -> waiters <- cont :: waiters)

// --- Child processes: the one place this module spawns anything --------------------------

/// `node:child_process` behind a handle-shaped surface. Both unconfined backends run
/// their processes through here — the host backend spawns the command itself, the srt
/// backend spawns the argv srt wrapped it in — so process-group kill, stream wiring and
/// exit reporting have one implementation and behave identically under both.
module private Children =

    let private childProcess : obj = importAll "node:child_process"

    // `detached: true` makes the child its own process group leader, so `Kill` can take
    // the whole tree with one signal to `-pid`.
    [<Emit("$0.spawn($1, $2, { cwd: $3 || undefined, env: Object.fromEntries($4), stdio: ['pipe', 'pipe', 'pipe'], detached: true })")>]
    let private spawnChild (cp: obj) (executable: string) (args: string array) (cwd: string) (env: (string * string) array) : obj = jsNative

    [<Emit("$0.stdout.on('data', (d) => $1(String(d)))")>]
    let private onStdout (child: obj) (handler: string -> unit) : unit = jsNative

    [<Emit("$0.stderr.on('data', (d) => $1(String(d)))")>]
    let private onStderr (child: obj) (handler: string -> unit) : unit = jsNative

    [<Emit("$0.on('error', (e) => $1(String((e && e.message) || e)))")>]
    let private onError (child: obj) (handler: string -> unit) : unit = jsNative

    [<Emit("$0.on('close', (c) => $1(c == null ? -1 : c))")>]
    let private onClose (child: obj) (handler: int -> unit) : unit = jsNative

    [<Emit("(() => { try { $0.stdin.write($1) } catch {} })()")>]
    let private writeStdin (child: obj) (text: string) : unit = jsNative

    [<Emit("(() => { try { $0.stdin.end() } catch {} })()")>]
    let private endStdin (child: obj) : unit = jsNative

    [<Emit("(function (child) { try { process.kill(-child.pid, 'SIGKILL') } catch { try { child.kill('SIGKILL') } catch {} } })($0)")>]
    let private killTree (child: obj) : unit = jsNative

    /// A live registry of the children a sandbox spawned, so `Dispose` can take down
    /// whatever is still running when the session's environment stops.
    type Registry () =
        let mutable live : (int * (unit -> unit)) list = []
        let mutable next = 0

        member _.Spawn
            (executable: string, arguments: string list, cwd: string, env: Map<string, string>)
            (onChunk: OutputStream * string -> unit)
            : SandboxProcessHandle =
            let child = spawnChild childProcess executable (List.toArray arguments) cwd (Map.toArray env)
            let id = next
            next <- next + 1
            live <- (id, fun () -> killTree child) :: live
            let forget () = live <- live |> List.filter (fun (other, _) -> other <> id)
            let ended = OneShot<SandboxRun> ()
            onStdout child (fun text -> onChunk (Stdout, text))
            onStderr child (fun text -> onChunk (Stderr, text))
            onError child (fun reason -> forget (); ended.Settle (SandboxRunFailed reason))
            onClose child (fun code -> forget (); ended.Settle (SandboxExited code))
            { WriteStdin = writeStdin child
              CloseStdin = fun () -> endStdin child
              Kill = fun () -> killTree child
              Exited = ended.Await }

        member _.KillAll () =
            live |> List.iter (fun (_, kill) -> kill ())
            live <- []

// --- Resolving packages by name at runtime -----------------------------------------------
//
// `createRequire`, not a bare `require`. Fable emits ESM and the test bundle runs as ESM,
// where `require` is simply not defined — so a bare one throws ReferenceError, which the
// tries below would swallow into "the thing is absent" on a box that has it. That is exactly
// what happened: the standalone node-pty probe passed (`node -e` runs as CJS) while every pty
// test reported no pty support.
//
// A static `import` is the other option and is worse here: node-pty is CJS-only and a missing
// package would fail the whole module's load rather than this one lookup, which would turn "no
// addon" from an answer into a crash.
//
// Typed `obj`, not `string -> (string -> obj)`: Fable sees a curried arrow and wraps the
// import in `uncurry2`, which rewrites the call into `createRequire(url, name)` — one call
// where two were meant. It fails, the try catches it, and the addon reports absent on a box
// that has it. Opaque here, applied in the emits that use it — the pty's lookup below, and
// srt's own installation further down.
[<Import("createRequire", "node:module")>]
let private createRequire : obj = jsNative

// --- Pseudo-terminals: the one place this module opens a pty -----------------------------
//
// `node-pty`, built from source into the Nix `nodeModules` derivation (nix/node-pty.nix).
// It is loaded LAZILY and through a try, because its absence is a first-class answer rather
// than a failure: off Nix the addon may not be there, and a backend that cannot open a pty
// reports `SpawnPty = None` instead of throwing when someone runs `vim`.
module private Pty =

    [<Emit("(() => { try { return $0(import.meta.url)('node-pty') } catch { return null } })()")>]
    let private tryRequireWith (mk: obj) : obj = jsNative

    let private tryRequire () : obj = tryRequireWith createRequire

    /// Resolved once. `require` is not free and the answer cannot change within a process.
    let private modul = lazy (tryRequire ())

    let available () : bool = not (isNull (modul.Force ()))

    [<Emit("$0.spawn($1, $2, { name: 'xterm-256color', cols: $3, rows: $4, cwd: $5 || undefined, env: Object.fromEntries($6) })")>]
    let private ptySpawn
        (m: obj) (file: string) (args: string[]) (cols: int) (rows: int) (cwd: string) (env: (string * string)[]) : obj =
        jsNative

    [<Emit("$0.onData($1)")>]
    let private onData (p: obj) (cb: string -> unit) : unit = jsNative

    /// node-pty reports an exit code AND a signal; a process killed by a signal has no
    /// meaningful code, so it is reported the way `SandboxExited` reports one everywhere
    /// else — -1, "the OS gave us none".
    [<Emit("$0.onExit(({ exitCode, signal }) => $1(signal ? -1 : exitCode))")>]
    let private onExit (p: obj) (cb: int -> unit) : unit = jsNative

    [<Emit("$0.write($1)")>]
    let private write (p: obj) (data: string) : unit = jsNative

    [<Emit("$0.resize($1, $2)")>]
    let private resize (p: obj) (cols: int) (rows: int) : unit = jsNative

    [<Emit("$0.kill()")>]
    let private kill (p: obj) : unit = jsNative

    /// Open a pty running `executable args`. Output is one stream, not two: a tty has a
    /// single device and stdout/stderr are indistinguishable on it by construction.
    let spawn
        (executable: string)
        (arguments: string list)
        (cwd: string)
        (env: Map<string, string>)
        (cols: int)
        (rows: int)
        (onOutput: string -> unit)
        : Result<PtyHandle, string> =
        match modul.Force () with
        | null -> Error "node-pty is not available on this host"
        | m ->
            try
                let exited = OneShot<SandboxRun> ()
                let proc =
                    ptySpawn m executable (Array.ofList arguments) cols rows cwd (Map.toArray env)
                onData proc onOutput
                onExit proc (fun code -> exited.Settle (SandboxExited code))
                Ok
                    { Write = write proc
                      Resize = resize proc
                      Kill = fun () -> kill proc
                      Exited = exited.Await }
            with ex -> Error ex.Message

// --- Host: explicitly unsandboxed --------------------------------------------------------

/// Plain child processes of the Session Process. No confinement — chosen deliberately
/// now that srt is the default — but the env discipline still holds: the child sees
/// exactly the policy env plus the request's, never the parent's.
module HostSandbox =

    let create () : CreateSandbox =
        fun policy ->
            async {
                policy.WorkingDirectory |> Option.iter Fs.ensureDir
                let children = Children.Registry ()
                let spawn (exec: SandboxExec) (onChunk: OutputStream * string -> unit) =
                    async {
                        let env = mergeEnv policy.Env exec.Env
                        let cwd =
                            SandboxPath.resolvedFrom policy.WorkingDirectory exec.WorkingDirectory
                            |> Option.toObj
                        return Ok (children.Spawn (exec.Executable, exec.Arguments, cwd, env) onChunk)
                    }
                return
                    Ok
                        { Ref = "host"
                          Spawn = spawn
                          SpawnPty =
                            if not (Pty.available ()) then None
                            else
                                Some (fun exec cols rows onOutput ->
                                    async {
                                        let env = mergeEnv policy.Env exec.Env
                                        let cwd =
                                            SandboxPath.resolvedFrom policy.WorkingDirectory exec.WorkingDirectory
                                            |> Option.toObj
                                        return Pty.spawn exec.Executable exec.Arguments cwd env cols rows onOutput
                                    })
                          Dispose = fun () -> async { children.KillAll () } }
            }

// --- Docker: a full isolated userland ----------------------------------------------------

/// `exec.resize({ h, w })` — the Engine API's exec-resize endpoint, which is what raises
/// SIGWINCH in the program on the other side. Fire-and-forget: a resize that loses a race
/// with the process exiting is not an error worth failing a terminal over.
[<Emit("$0.resize({ h: $1, w: $2 }).catch(() => {})")>]
let private execResize (exec: obj) (rows: int) (cols: int) : unit = jsNative

/// Docker-backed sandboxes through the `Fable.Dockerode` bindings (the Engine API over
/// the local socket — no `docker` CLI). The container and its workspace volume are
/// named by the sandbox name (the session id — always a valid Docker object name) and
/// labelled `yession-session=<name>`, so the existing cleanup sweep keeps working.
module DockerSandbox =

    module DK = Fable.Dockerode

    [<Emit("$0 == null")>]
    let private isNil (o: obj) : bool = jsNative

    [<Emit("$0.toString('utf8')")>]
    let private bufToStr (b: obj) : string = jsNative

    [<Emit("(function (inspect) { return (inspect.ExitCode == null ? -1 : inspect.ExitCode) })($0)")>]
    let private exitCodeOf (inspect: obj) : int = jsNative

    let private nodeFs : obj = importAll "node:fs"

    [<Emit("$0.readdirSync($1)")>]
    let private readdirSync (fs: obj) (dir: string) : string array = jsNative

    /// Resolve the container's image (default `alpine:3`) to a `name:tag` string.
    let private imageRef (container: ContainerSpec) : string =
        match container.Image with
        | Some i -> i.Name + (i.Tag |> Option.map ((+) ":") |> Option.defaultValue "")
        | None -> "alpine:3"

    /// One `HostConfig.Mounts` entry from a typed mount.
    let private mountObj (name: string) (m: ContainerMount) : obj =
        let source, kind =
            match m.Source with
            | HostPath p -> p, "bind"
            | NamedVolume v -> v, "volume"
            | SessionWorkspace -> name, "volume"
        createObj
            [ "Type", box kind
              "Source", box source
              "Target", box m.Target
              "ReadOnly", box (m.Mode = ReadOnly) ]

    /// Drain a build/pull progress stream; resolves when Docker signals completion.
    let private drainProgress (client: DK.Docker) (stream: DK.Stream) : Async<Result<unit, string>> =
        Async.FromContinuations(fun (cont, _, _) ->
            client.modem.followProgress (stream, fun err _ ->
                if isNil err then cont (Ok ()) else cont (Error (bufToStr err))))

    /// Count containers (running or not) carrying a `yession-session` label value — lets
    /// tests assert a stopped session leaves nothing behind.
    let countByLabel (label: string) : Async<int> =
        async {
            let client = DK.create ()
            let! arr =
                client.listContainers (createObj [ "all", box true; "filters", box (createObj [ "label", box [| label |] ]) ])
                |> Interop.awaitPromise
            return arr.Length
        }

    /// Every mount this container gets, decided in ONE place: what the spec declared,
    /// what the operator granted (host binds, socket paths, named volumes), and — only
    /// where none of those already provides the workspace path — the sandbox's own
    /// workspace volume. The granted binds are what makes a docker grant TRUE: until
    /// this existed the policy carried them, `limitsFor` claimed docker scopes a socket
    /// by path, and the backend dropped every one on the floor — a grant that read as
    /// held and reached nothing.
    ///
    /// A named volume mounts empty-or-warm and dockerd seeds an empty one from the
    /// image's content at that path on first use, so a volume over /nix does not shadow
    /// the image's own store.
    let mountPlan (workspaceTarget: string) (declared: ContainerMount list) (policy: SandboxPolicy) : ContainerMount list =
        let granted =
            (policy.Binds
             |> List.map (fun b ->
                 { Source = HostPath b.From
                   Target = b.At
                   Mode = if b.Mode = ResourceMountMode.Read then ReadOnly else ReadWrite }))
            @ (policy.Sockets
               |> List.distinct
               |> List.map (fun path -> { Source = HostPath path; Target = path; Mode = ReadWrite }))
            @ (policy.Volumes |> List.map (fun (volume, at) -> { Source = NamedVolume volume; Target = at; Mode = ReadWrite }))
        let all = declared @ granted
        let workspaceProvided = all |> List.exists (fun m -> ContainerMount.provides m.Target workspaceTarget)
        (if workspaceProvided then []
         else [ { Source = SessionWorkspace; Target = workspaceTarget; Mode = ReadWrite } ])
        @ all

    /// The container's own process — the declared command, or the idle that exists to be
    /// exec'd into — wrapped in the one fix every nix-built image needs on today's
    /// daemons. Docker 29 resolves an exec's user through Go's os.Root guard, which
    /// refuses the ABSOLUTE symlinks such images use for /etc/passwd and /etc/group: so
    /// `docker run` works and every `docker exec` dies with "path escapes from parent".
    /// Materialising the files at start (the run path, which works) makes every later
    /// exec resolve against real files. Measured on 29.2.1; the regression is
    /// containerd's (https://github.com/containerd/containerd/issues/12683, Go 1.24's
    /// stricter os.Root) — drop the wrap when a fix ships in the daemons people run.
    /// Harmless elsewhere — a real file is left alone, and a missing one skipped.
    ///
    /// The wrap COSTS three things, stated here because nothing else will say them: the
    /// image must carry `sh` and `cat`/`rm`/`mv` (a distroless image with a declared
    /// command cannot start — the daemon reports whatever exec'ing `sh` fails as); a
    /// materialised /etc/shadow is re-created with the shell's umask, not the
    /// symlink target's mode (container root reads it either way, but an image that
    /// added an unprivileged user changes observably); and the declared command is
    /// pasted after the fixes without `exec`, so sh stays PID 1 and signals reach the
    /// command only as sh forwards them — the idle arm execs, a declared one does not,
    /// because a declared string may be a sequence.
    ///
    /// The daemon's regression, so the backend carries the workaround: it sat in
    /// yession.yaml for a while, where every repo adopting a nix-built image would have
    /// had to copy it and keep copying it after the daemon is fixed.
    let startCommand (declared: string option) : string array =
        let materialiseEtc =
            "for f in passwd group shadow; do if [ -L /etc/$f ]; then cat /etc/$f > /etc/.$f && rm /etc/$f && mv /etc/.$f /etc/$f; fi; done"
        // The OTHER reader of the bind-mounted checkouts' ownership. The docker policy's
        // GIT_CONFIG env trio covers the git CLI, which reads git's env spelling —
        // libgit2 does not, and nix's flake fetcher IS libgit2, so `nix develop` in a
        // checkout died with "repository path … is not owned by current user (libgit2
        // error code = 7)" on any daemon that does not mask ownership (a real Linux
        // dockerd; Colima presents everything as root, which is how every Mac lap was
        // green while CI's Dogfood run went straight to the wall). NOT belt-and-braces:
        // the two mechanisms have disjoint readers, each measured — libgit2 honours
        // /etc/gitconfig and ignores the env; the nixpkgs-built git CLI honours the env
        // and never reads /etc/gitconfig, because its compiled sysconfdir is in the
        // store. Delete either and one reader goes back to refusing.
        let trustMounts =
            "grep -qs 'directory = \\*' /etc/gitconfig 2>/dev/null || printf '[safe]\\n\\tdirectory = *\\n' >> /etc/gitconfig"
        [| "sh"
           "-c"
           sprintf "%s\n%s\n%s" materialiseEtc trustMounts (declared |> Option.defaultValue "exec tail -f /dev/null") |]

    let create (name: string) (spec: EnvironmentSpec) (container: ContainerSpec) : CreateSandbox =
        fun policy ->
            async {
                try
                    let client = DK.create ()
                    // Resolve the image: build it from the context, or pull the named image.
                    let! imageResult =
                        async {
                            match container.Build with
                            | Some build ->
                                let tag = "yession-build-" + name.ToLower ()
                                let src = readdirSync nodeFs build.ContextPath
                                let opts =
                                    [ "t", box tag ]
                                    @ (match build.DockerfilePath with Some d -> [ "dockerfile", box d ] | None -> [])
                                    |> createObj
                                let! stream =
                                    client.buildImage (createObj [ "context", box build.ContextPath; "src", box src ], opts)
                                    |> Interop.awaitPromise
                                let! drained = drainProgress client stream
                                return drained |> Result.map (fun () -> tag)
                            | None ->
                                let image = imageRef container
                                // Pull only when the image is absent locally — matches
                                // `docker run`, and avoids re-hitting the registry (and its
                                // transient failures) on every container start.
                                let! present =
                                    async {
                                        try
                                            do! client.getImage(image).inspect () |> Interop.awaitPromise |> Async.Ignore
                                            return true
                                        with _ -> return false
                                    }
                                if present then return Ok image
                                else
                                    let! stream = client.pull image |> Interop.awaitPromise
                                    let! drained = drainProgress client stream
                                    return drained |> Result.map (fun () -> image)
                        }
                    match imageResult with
                    | Error reason -> return Error reason
                    | Ok image ->
                        // `policyFor` has already folded the spec's working directory in,
                        // so the policy is the single answer to "where does this start".
                        // This used to ask the spec again when the policy said nothing —
                        // which was the only thing keeping `workdir` alive under docker,
                        // and by accident: docker is the backend that passes no workspace.
                        let workspaceTarget =
                            policy.WorkingDirectory |> Option.defaultValue "/workspace"
                        let mounts = mountPlan workspaceTarget container.Mounts policy |> List.map (mountObj name)
                        let env =
                            policy.Env |> Map.toList |> List.map (fun (k, v) -> sprintf "%s=%s" k v) |> List.toArray
                        // The named volume persists across container restarts by design;
                        // the label lets cleanup find it (see the workflow teardown).
                        do! client.createVolume (createObj [ "Name", box name; "Labels", box (createObj [ "yession-session", box name ]) ]) |> Interop.awaitPromise |> Async.Ignore
                        // Clear a same-named crash leftover so `createContainer` can reuse the name.
                        try do! client.getContainer(name).remove (createObj [ "force", box true ]) |> Interop.awaitPromise |> Async.Ignore
                        with _ -> ()
                        let! container =
                            client.createContainer (
                                createObj
                                    [ "name", box name
                                      "Image", box image
                                      "Labels", box (createObj [ "yession-session", box name ])
                                      "Env", box env
                                      "WorkingDir", box workspaceTarget
                                      // The sandbox's own process when it declared one, and
                                      // otherwise the idle command that has always kept the
                                      // container up for `exec` to reach — either way behind
                                      // the daemon workaround (`startCommand`).
                                      "Cmd", box (startCommand container.Command)
                                      "HostConfig",
                                      box (
                                          createObj
                                              [ "Mounts", box (List.toArray mounts)
                                                // Everything dropped, then the file-ownership
                                                // capabilities added back. "Nothing it runs
                                                // legitimately needs a capability" was measured
                                                // false: a nix source build's tar unpack
                                                // preserves the archive's uid/gid (CHOWN), later
                                                // chmods the result (FOWNER), and root then
                                                // reads files it just gave away (DAC_OVERRIDE).
                                                // All three verified against a raw container;
                                                // still far below docker's own default set — no
                                                // NET_RAW, no SETUID/SETGID, no MKNOD — and
                                                // no-new-privileges stays.
                                                "CapDrop", box [| "ALL" |]
                                                "CapAdd", box [| "CHOWN"; "FOWNER"; "DAC_OVERRIDE" |]
                                                "SecurityOpt", box [| "no-new-privileges" |] ]) ])
                            |> Interop.awaitPromise
                        do! container.start () |> Interop.awaitPromise |> Async.Ignore

                        let spawn (exec: SandboxExec) (onChunk: OutputStream * string -> unit) =
                            async {
                                try
                                    let execOpts =
                                        [ "Cmd", box (List.toArray (exec.Executable :: exec.Arguments))
                                          "AttachStdin", box true
                                          "AttachStdout", box true
                                          "AttachStderr", box true
                                          "Env", box (exec.Env |> Map.toList |> List.map (fun (k, v) -> sprintf "%s=%s" k v) |> List.toArray) ]
                                        // Resolved against the container's own working
                                        // directory, which is what it was CREATED with — so
                                        // an exec naming nothing lands exactly where it
                                        // always did, and one naming a relative directory
                                        // means the same thing here as it does everywhere
                                        // else in the session.
                                        @ (match SandboxPath.resolvedFrom (Some workspaceTarget) exec.WorkingDirectory with
                                           | Some w -> [ "WorkingDir", box w ]
                                           | None -> [])
                                        |> createObj
                                    let! started = container.exec execOpts |> Interop.awaitPromise
                                    // Hijack the connection so stdin rides the same socket the
                                    // output is demuxed from.
                                    let! stream = started.start (createObj [ "hijack", box true; "stdin", box true ]) |> Interop.awaitPromise
                                    let stdout = DK.createPassThrough ()
                                    let stderr = DK.createPassThrough ()
                                    client.modem.demuxStream (stream, stdout, stderr)
                                    stdout.on ("data", fun d -> onChunk (Stdout, bufToStr d)) |> ignore
                                    stderr.on ("data", fun d -> onChunk (Stderr, bufToStr d)) |> ignore
                                    let ended = OneShot<SandboxRun> ()
                                    let finish () =
                                        Async.StartImmediate (
                                            async {
                                                try
                                                    let! inspect = started.inspect () |> Interop.awaitPromise
                                                    ended.Settle (SandboxExited (exitCodeOf inspect))
                                                with ex -> ended.Settle (SandboxRunFailed ex.Message)
                                            })
                                    stream.on ("end", fun _ -> finish ()) |> ignore
                                    stream.on ("error", fun e -> ended.Settle (SandboxRunFailed (string e))) |> ignore
                                    return
                                        Ok
                                            { WriteStdin = fun text -> stream.write (box text) |> ignore
                                              CloseStdin = fun () -> stream.``end`` ()
                                              // The Engine API cannot signal an exec's process;
                                              // closing our side of the stream is the most a
                                              // caller's Kill can do (the container itself dies
                                              // with Dispose).
                                              Kill = fun () -> stream.``end`` ()
                                              Exited = ended.Await }
                                with ex -> return Error ex.Message
                            }

                        // The same exec, on a terminal. Three things differ from the piped
                        // spawn above, and all three are what a tty IS rather than options.
                        //
                        // `Tty: true` gives the process a controlling terminal, so it can ask
                        // whether it is interactive and take the alternate screen. There is NO
                        // demux: docker multiplexes stdout and stderr only when there is no
                        // tty, because a terminal has one device and the two streams are
                        // indistinguishable on it — running the demuxer here would parse
                        // ordinary output as though it were frame headers. And the exec-resize
                        // endpoint is what makes SIGWINCH reach the program.
                        let spawnPty (exec: SandboxExec) (cols: int) (rows: int) (onOutput: string -> unit) =
                            async {
                                try
                                    let execOpts =
                                        [ "Cmd", box (List.toArray (exec.Executable :: exec.Arguments))
                                          "AttachStdin", box true
                                          "AttachStdout", box true
                                          "AttachStderr", box true
                                          "Tty", box true
                                          "Env", box (exec.Env |> Map.toList |> List.map (fun (k, v) -> sprintf "%s=%s" k v) |> List.toArray) ]
                                        @ (match SandboxPath.resolvedFrom (Some workspaceTarget) exec.WorkingDirectory with
                                           | Some w -> [ "WorkingDir", box w ]
                                           | None -> [])
                                        |> createObj
                                    let! started = container.exec execOpts |> Interop.awaitPromise
                                    let! stream = started.start (createObj [ "hijack", box true; "stdin", box true ]) |> Interop.awaitPromise
                                    stream.on ("data", fun d -> onOutput (bufToStr d)) |> ignore
                                    // Size it before anything runs: a program that reads its
                                    // dimensions at startup must not read 80x24 and then be
                                    // told the truth afterwards.
                                    execResize started rows cols
                                    let ended = OneShot<SandboxRun> ()
                                    let finish () =
                                        Async.StartImmediate (
                                            async {
                                                try
                                                    let! inspect = started.inspect () |> Interop.awaitPromise
                                                    ended.Settle (SandboxExited (exitCodeOf inspect))
                                                with ex -> ended.Settle (SandboxRunFailed ex.Message)
                                            })
                                    stream.on ("end", fun _ -> finish ()) |> ignore
                                    stream.on ("error", fun e -> ended.Settle (SandboxRunFailed (string e))) |> ignore
                                    return
                                        Ok
                                            // Written whole. Docker's double-PTY proxy is
                                            // known to drop data on large writes, which Warp
                                            // answers by chunking container exec writes at
                                            // 4KB with delays. It does not bite here yet
                                            // because instrumentation goes in at launch
                                            // rather than by typing, so the only large write
                                            // is a very long command line — but a caller that
                                            // starts streaming bytes through this should
                                            // chunk rather than assume the write survives.
                                            { Write = fun text -> stream.write (box text) |> ignore
                                              Resize = fun c r -> execResize started r c
                                              // As with the piped exec: the Engine API cannot
                                              // signal an exec's process, so closing our side
                                              // of the stream is the most Kill can do.
                                              Kill = fun () -> stream.``end`` ()
                                              Exited = ended.Await }
                                with ex -> return Error ex.Message
                            }
                        return
                            Ok
                                { Ref = container.id
                                  Spawn = spawn
                                  SpawnPty = Some spawnPty
                                  Dispose =
                                    fun () ->
                                        async {
                                            try
                                                do! client.getContainer(container.id).remove (createObj [ "force", box true ]) |> Interop.awaitPromise |> Async.Ignore
                                            with ex ->
                                                eprintfn "[sandbox %s] docker remove failed: %s" name ex.Message
                                        } }
                with ex -> return Error (sprintf "docker sandbox failed: %s" ex.Message)
            }

// --- srt: OS-level confinement -----------------------------------------------------------

/// How strongly a Linux srt sandbox nests. The strict profile mounts a fresh `/proc` and
/// drops every capability, which costs a NESTED user namespace — and an unprivileged
/// container (this repo's own dev container, and any deployment that runs a session
/// inside one) is not allowed to create one. `Weak` is srt's documented answer there:
/// the host's `/proc` stays visible and caps are not dropped. It is a real reduction in
/// confinement, so it is a configured choice, never a fallback the runtime picks when
/// the strict profile fails.
type SandboxNesting =
    | StrictNesting
    | WeakNesting

/// How this host must be driven to confine: the tools srt shells out to, named rather
/// than looked up on PATH (srt treats a named path as a directive and reports it missing;
/// a PATH lookup would silently take someone else's build), how far the nesting can go,
/// and what stays readable once everything else is denied. Both tool paths are `None` on
/// macOS, where Seatbelt ships with the OS and needs neither.
type SrtTools =
    { Bwrap : string option
      Socat : string option
      /// srt scans for the files it must deny outright (keys, shell rc files, git hooks)
      /// with ripgrep. It is as much a dependency as bubblewrap — a sandbox does not start
      /// without one — and naming it is what stops a host's incidental `rg` from deciding
      /// how a session confines.
      Ripgrep : string option
      Nesting : SandboxNesting
      /// The host files every sandbox on this box may read whatever its policy says: the
      /// interpreter its commands run, the libraries that interpreter links, the trust
      /// store a TLS client opens, and srt's own vendored helper. A property of the HOST,
      /// not of a policy — which is why it sits beside the tool paths rather than on
      /// `SandboxPolicy`, and why both sandboxes take the same list.
      Runtime : string list }

/// The srt runtime configuration a policy becomes. Assembled as data so the mapping is
/// checkable without a sandbox: what is denied, what is re-allowed, and where egress may
/// go are decisions, and decisions belong in F#.
type SrtConfig =
    { /// Regions read access is removed from, before `AllowRead` opens holes in them.
      DenyRead : string list
      AllowRead : string list
      AllowWrite : string list
      /// The egress allowlist. Empty means no egress: srt has no "unrestricted", so a
      /// policy that names no domains gets none.
      AllowedDomains : string list
      /// Unix sockets a spawn may connect to (srt's `network.allowUnixSockets`). Empty
      /// means none, which is srt's default — a socket nobody named is not reachable.
      AllowUnixSockets : string list
      /// srt's `network.allowAllUnixSockets`: every unix socket, because this host cannot
      /// scope one. The materialisation of a `Coarsened` socket — on Linux the path list
      /// above is not read at all, so this is the only way a granted socket works there, and
      /// it is set only when the policy already SAID it would be.
      AllowAllUnixSockets : bool
      Bwrap : string option
      Socat : string option
      Ripgrep : string option
      WeakNesting : bool
      /// srt denies writes to `.git/config` unless told otherwise, and a `git clone`
      /// writes one. So this is on — and it is on for EVERY sandbox, because srt reads
      /// the flag from the session config the first sandbox initialized the manager with
      /// and ignores the per-spawn one, which makes any per-sandbox answer a lie about
      /// which sandbox got it. What that costs is stated in docs/GAPS.md; the hooks deny
      /// (the execution vector) is separate and stays. Undo when srt reads the flag from
      /// the per-spawn config: then only the git sandbox asks for it.
      AllowGitConfig : bool
      /// srt's `filesystem.disabled`: no read or write rules, and none of the mandatory
      /// denies either — the only way to write a checkout carrying a name srt refuses.
      /// All-or-nothing by construction: srt scopes it per SPAWN, never per path.
      /// Egress and credential env scrubbing are untouched. Undo when it can be a path.
      FilesystemDisabled : bool }

/// What a manager start that FAILED actually settled — and so whether the sandbox after
/// it is handed that refusal or starts one of its own.
///
/// srt's dependency check is not a stat. `whichSync` forks `which` — a shell script on a
/// Debian-derived host, so two execs — under a one-second timeout, and reports EVERY way
/// that fork can fail as `<tool> not found`: no `which` on this process's PATH, a box too
/// busy to hand out a fork inside a second, EMFILE, ENOMEM. A NAMED bwrap or socat escapes
/// that (srt checks those with `accessSync(X_OK)`); only ripgrep's named path goes through
/// `which`. Which is why the whole tier fails on that one line, about a file sitting right
/// there, executable, while the other two tools it needs are never mentioned.
type StartFailure =
    /// A tool the config names cannot be executed by this process either. Nothing about
    /// that changes while the process lives, so the refusal is kept and every later
    /// sandbox is given it instead of paying to rediscover it.
    | HostCannotConfine
    /// Every tool the config names is executable right here, so a refusal that blames one
    /// of them settled nothing about this host: srt's probe did not run. Forgotten, and
    /// the next sandbox asks again.
    | NothingSettled

/// OS-level confinement via `@anthropic-ai/sandbox-runtime` (bubblewrap on Linux,
/// Seatbelt on macOS): a wrapped spawn, no container, and egress that is ENFORCED —
/// the network namespace is unshared, so the only route out is srt's filtering proxy.
module SrtSandbox =


    // --- What stays readable once everything else is denied -----------------------------
    //
    // A sandbox that denies every read denies the interpreter its own commands run on, so
    // the deny is only half of a read scope: the other half is the host runtime, named
    // here. It is deployment-specific by nature — the interpreter can be anywhere — so it
    // is assembled from three sources rather than guessed at: the platform's ordinary
    // locations, the prefix whatever is ALREADY running was installed under, and an
    // operator's override for a toolchain neither of those finds.

    /// The ordinary locations a Linux host keeps its runtime in. Coarse for executables and
    /// their libraries — those are the files every PATH lookup already reaches — and fine
    /// for `/etc`, which is the one region here that also holds an operator's secrets
    /// (`/etc/shadow` stays denied). srt skips an allow path that does not exist, so this
    /// is the union across distributions rather than a guess at which one is underneath.
    let linuxRuntimePaths : string list =
        [ "/usr"; "/bin"; "/sbin"; "/lib"; "/lib32"; "/lib64"; "/libx32"
          // Certificates: a command that cannot verify TLS cannot use the egress it was
          // granted. `/etc/static` is where NixOS keeps the real files /etc points at.
          "/etc/ssl"; "/etc/pki"; "/etc/ca-certificates"; "/etc/ca-certificates.conf"
          "/etc/static"
          // Users, hosts, resolution, time: what libc reads before `main`.
          "/etc/passwd"; "/etc/group"; "/etc/hosts"; "/etc/hostname"; "/etc/resolv.conf"
          "/etc/nsswitch.conf"; "/etc/localtime"
          // The loader, and the symlink farm a distribution's binaries resolve through.
          "/etc/ld.so.cache"; "/etc/ld.so.conf"; "/etc/ld.so.conf.d"; "/etc/alternatives"
          // A terminal's capabilities, a shell's startup files, git's system config.
          "/etc/terminfo"; "/etc/inputrc"; "/etc/profile"; "/etc/profile.d"
          "/etc/bash.bashrc"; "/etc/shells"; "/etc/gitconfig" ]

    /// The same for macOS. UNVERIFIED by any run here: no job in this repository executes a
    /// suite on darwin (pr.yaml's macos job builds the package and enters the dev shell),
    /// and the box that verified the Linux list is Linux. It is what a Seatbelt profile
    /// conventionally allows back; if it is short, the failure is loud and local — a
    /// command cannot find its interpreter — and `YESSION_SESSION_READ` is the answer.
    let darwinRuntimePaths : string list =
        [ "/usr"; "/bin"; "/sbin"; "/System"; "/Library"; "/opt/homebrew"; "/nix"
          "/private/etc"; "/private/var/db"; "/private/var/select" ]

    /// Where a runtime installed at `path` has to be readable FROM.
    ///
    /// Nix gives every dependency its own store path, so the executable's own prefix is not
    /// enough — the store is the unit. An npm install puts a package's siblings beside it
    /// under one `node_modules`, so the tree that owns it is. Anywhere else it is the prefix
    /// above `bin`.
    ///
    /// A one-segment answer (`/usr`, `/opt`, `/home`) is dropped rather than returned:
    /// either the platform list already names it, or it is a region vastly larger than the
    /// thing installed in it — which is how an allow-back quietly hands back the filesystem
    /// the deny just took.
    let installPrefix (path: string) : string option =
        let store = "/nix/store/"
        let modules = "/node_modules/"
        let candidate =
            if path.StartsWith store then Some "/nix/store"
            elif path.Contains modules then Some (path.Substring (0, path.IndexOf modules))
            else
                match path.LastIndexOf '/' with
                | -1 -> None
                | last ->
                    let dir = path.Substring (0, last)
                    match dir.LastIndexOf '/' with
                    | -1 -> None
                    | up -> Some (dir.Substring (0, up))
        candidate
        |> Option.filter (fun prefix ->
            prefix.Split '/' |> Array.filter (fun segment -> segment <> "") |> Array.length >= 2)

    /// What a confined sandbox may read beyond the paths its own policy names. `installed`
    /// is what is already running on this host — the interpreter, srt's own files — read
    /// back through `installPrefix`.
    ///
    /// An install prefix that IS the operator's home is dropped, and this is the one place
    /// the home is still named. `npm i yession` run in a home directory puts the tree at
    /// `~/node_modules`, whose owning prefix is the home itself — so the allow-back would
    /// hand back the entire region this scope exists to deny, and it would do it silently.
    /// Refusing it fails the other way instead: that layout cannot start a command until an
    /// operator names a narrower path in `YESSION_SESSION_READ`. An explicitly
    /// configured path is never dropped — an operator naming their home means it.
    let runtimeReadPaths (platform: string) (installed: string list) (ambient: Map<string, string>) : string list =
        let platformPaths =
            match platform with
            | "darwin" -> darwinRuntimePaths
            | _ -> linuxRuntimePaths
        let home =
            ambient
            |> Map.tryFind "HOME"
            |> Option.map (fun path -> path.TrimEnd '/')
            |> Option.filter (fun path -> path <> "")
        let prefixes =
            installed
            |> List.choose installPrefix
            |> List.filter (fun prefix -> Some prefix <> home)
        // The platform's own paths and the runtimes this process names, and nothing else.
        //
        // `YESSION_SESSION_READ` used to be folded in here as well as being the ceiling a
        // repo's `read:` was held to — one variable that was a bound AND an unconditional
        // grant, so a repo's ask could never obtain anything and an operator could not offer
        // a path without forcing it on everything. What an operator hands out is now a
        // resource somebody selects, and it arrives through the policy like every other
        // grant rather than through this list.
        platformPaths @ prefixes |> List.distinct

    /// Quote one argument for the shell srt wraps the command in. srt's Linux and macOS
    /// wrappers take a COMMAND STRING (the profile ends in `<shell> -c <wrapped>`), so an
    /// argv has to survive a shell round-trip: single-quote everything, and close/escape/
    /// reopen around any embedded quote. Nothing else is interpreted inside single quotes.
    let quoteArg (arg: string) : string =
        "'" + arg.Replace ("'", "'\\''") + "'"

    let commandLine (executable: string) (arguments: string list) : string =
        executable :: arguments |> List.map quoteArg |> String.concat " "

    /// A policy as an srt configuration.
    ///
    /// Reads are deny-then-allow, and what is denied is EVERYTHING. srt starts readable
    /// everywhere, so anything short of a root deny leaves whatever nobody thought to name
    /// — another session's data directory, a checkout this session was never given, `/etc`
    /// — readable by every command the agent issues. Denying only the invoking user's home
    /// was exactly that, and measurably so. srt expands a root deny into the children of
    /// `/` at each spawn, so a region that appears after this session started is denied
    /// too, without anything here being re-configured.
    ///
    /// The holes in it are three, and each is a thing without which the sandbox is not a
    /// place to work: what the policy names, everything the policy may WRITE (a workspace
    /// that could be written but not read is no workspace), and the host runtime — the
    /// interpreter, its libraries, srt's own vendored helper — which is a property of the
    /// box rather than of the policy, and so arrives on `SrtTools`.
    ///
    /// Writes are unchanged: allow-only, exactly the policy's paths, plus the temp
    /// directory srt points the sandbox at and the standard streams a process expects to
    /// be able to write.
    ///
    /// Measured, not assumed: wrap+spawn of a trivial command is indistinguishable with the
    /// root deny on and off (~20ms wrap either way), and the same egress probes behave
    /// identically — srt's proxy plumbing does not live anywhere the deny hides. So there is
    /// no performance argument for trading this back for a curated list of denied roots,
    /// which would fail open at the first root nobody thought to name.
    let configFor (tools: SrtTools) (policy: SandboxPolicy) : SrtConfig =
        let distinct (paths: string list) = paths |> List.distinct
        // Everything writable, named once — and the readable set is DERIVED from it rather
        // than listed beside it.
        //
        // A path that can be written and not read is not a path. Under `DenyRead = ["/"]`
        // a process can create a file in a directory it cannot stat, and that is not a
        // theoretical shape: `tmpDir` and the three devices below were in the write list
        // and nowhere in the read one, so the temp dir srt redirects TMPDIR to was
        // writable and invisible at once — which is what a runtime that stats its own temp
        // directory before using it (.NET's named mutexes do) reports as a bare EPERM.
        //
        // Derived, so the containment is structural. Two lists that have to agree by
        // inspection are two lists that drift, and this pair already had.
        let writable = distinct (policy.WritePaths @ [ SessionLayout.tmpDir (); "/dev/stdout"; "/dev/stderr"; "/dev/null" ])
        { DenyRead = [ "/" ]
          AllowRead = distinct (policy.ReadPaths @ writable @ tools.Runtime)
          AllowWrite = writable
          AllowedDomains = policy.AllowedDomains |> Option.defaultValue []
          AllowUnixSockets = policy.Sockets
          // The other half of what the policy REPORTED. A host that said it could not scope
          // a socket has to then give the wider thing it said it would give, or the report is
          // a warning about something that did not happen and the sandbox holds less than the
          // person approved. srt's own knob is all-or-nothing here, which is exactly the
          // distinction it could not make.
          AllowAllUnixSockets =
            policy.Realisation
            |> List.exists (fun (leaf, outcome) ->
                match leaf, outcome with
                | Socket _, LeafRealisation.Coarsened _ -> true
                | _ -> false)
          Bwrap = tools.Bwrap
          Socat = tools.Socat
          Ripgrep = tools.Ripgrep
          WeakNesting = (tools.Nesting = WeakNesting)
          AllowGitConfig = true
          FilesystemDisabled = (policy.Filesystem = Unconfined) }

    [<Emit("process.execPath")>]
    let private execPath () : string = jsNative

    /// Where srt itself is installed. The wrapped argv execs a vendored helper
    /// (`vendor/seccomp/<arch>/apply-seccomp`) from INSIDE the sandbox, so a profile that
    /// hides srt's own files fails every command with exit 127 before the command runs.
    /// Empty when it cannot be resolved, which `toolsFrom` turns into a refused boot: a
    /// session that cannot find it would confine nothing, because nothing would run.
    [<Emit("(() => { try { return $0(import.meta.url).resolve('@anthropic-ai/sandbox-runtime/package.json') } catch { return '' } })()")>]
    let private resolveSrtWith (mk: obj) : string = jsNative

    /// How this host confines, as configured. A blank tool path is an absent one: the dev
    /// shell and the installable set these per platform, and on macOS they are empty.
    /// The nesting is parsed fail-closed — an unrecognised value is a loud error rather
    /// than a guess at which way the operator meant to err.
    ///
    /// Not pure, unlike the rest of this module: the read scope's allow-back has to know
    /// what is already running on this box (`process.execPath`, srt's own installation),
    /// and a caller that had to look those up first is a caller that can forget to. The
    /// DECISIONS underneath it — `installPrefix`, `runtimeReadPaths` — stay pure and are
    /// where the cheap tier pins this.
    let toolsFrom (ambient: Map<string, string>) : Result<SrtTools, string> =
        let named name =
            ambient
            |> Map.tryFind name
            |> Option.map (fun value -> value.Trim ())
            |> Option.filter (fun value -> value <> "")
        let nesting =
            match ambient |> Map.tryFind "YESSION_NESTED_SANDBOX" |> Option.defaultValue "strict" with
            | value ->
                match value.Trim().ToLowerInvariant () with
                | ""
                | "strict" -> Ok StrictNesting
                | "weak" -> Ok WeakNesting
                | other ->
                    Error (sprintf "unknown sandbox nesting '%s' (expected strict, or weak for an unprivileged container)" other)
        nesting
        |> Result.bind (fun nesting ->
            match resolveSrtWith createRequire with
            | "" ->
                Error
                    "cannot locate @anthropic-ai/sandbox-runtime's own files, which every confined command execs"
            | srtPackage ->
                Ok
                    { Bwrap = named "YESSION_BIN_BWRAP"
                      Socat = named "YESSION_BIN_SOCAT"
                      Ripgrep = named "YESSION_BIN_RIPGREP"
                      Nesting = nesting
                      // The binaries a confined spawn execs but the platform list cannot
                      // know the location of: the claude CLI the AgentSandbox starts, and
                      // the git the repo verbs run.
                      Runtime =
                        runtimeReadPaths
                            (platform ())
                            ([ execPath (); srtPackage ]
                             @ ([ "YESSION_BIN_CLAUDE"; "YESSION_BIN_GIT" ] |> List.choose named))
                            ambient })

    [<Emit("(function (allowedDomains, denyRead, allowRead, allowWrite, bwrap, socat, ripgrep, weakNesting, allowGitConfig, filesystemDisabled, allowUnixSockets, allowAllUnixSockets) { return ({ network: { allowedDomains: allowedDomains, deniedDomains: [], strictAllowlist: true, allowUnixSockets: allowUnixSockets, ...(allowAllUnixSockets ? { allowAllUnixSockets: true } : {}) }, filesystem: { denyRead: denyRead, allowRead: allowRead, allowWrite: allowWrite, denyWrite: [], allowGitConfig: allowGitConfig, disabled: filesystemDisabled }, ...(bwrap ? { bwrapPath: bwrap } : {}), ...(socat ? { socatPath: socat } : {}), ...(ripgrep ? { ripgrep: { command: ripgrep } } : {}), ...(weakNesting ? { enableWeakerNestedSandbox: true } : {}) }) })($0, $1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)")>]
    let private configObject
        (allowedDomains: string array)
        (denyRead: string array)
        (allowRead: string array)
        (allowWrite: string array)
        (bwrap: string)
        (socat: string)
        (ripgrep: string)
        (weakNesting: bool)
        (allowGitConfig: bool)
        (filesystemDisabled: bool)
        (allowUnixSockets: string array)
        (allowAllUnixSockets: bool)
        : obj = jsNative

    let private toJs (config: SrtConfig) : obj =
        configObject
            (List.toArray config.AllowedDomains)
            (List.toArray config.DenyRead)
            (List.toArray config.AllowRead)
            (List.toArray config.AllowWrite)
            (config.Bwrap |> Option.toObj)
            (config.Socat |> Option.toObj)
            (config.Ripgrep |> Option.toObj)
            config.WeakNesting
            config.AllowGitConfig
            config.FilesystemDisabled
            (List.toArray config.AllowUnixSockets)
            config.AllowAllUnixSockets

    // The package is loaded on demand: it pulls a proxy stack and a TLS library, and a
    // session on the host backend must not pay for either. Dynamic `import` (not
    // `createRequire`) because it is ESM-only.
    [<Emit("import('@anthropic-ai/sandbox-runtime')")>]
    let private importSrt () : JS.Promise<obj> = jsNative

    [<Emit("$0.SandboxManager.isSupportedPlatform()")>]
    let private supportedPlatform (srt: obj) : bool = jsNative

    [<Emit("$0.SandboxManager.initialize($1)")>]
    let private initialize (srt: obj) (config: obj) : JS.Promise<unit> = jsNative

    [<Emit("$0.SandboxManager.reset()")>]
    let private resetManager (srt: obj) : JS.Promise<unit> = jsNative

    [<Emit("$0.SandboxManager.wrapWithSandboxArgv($1, undefined, $2, undefined, $3 || undefined)")>]
    let private wrapArgv (srt: obj) (command: string) (custom: obj) (cwd: string) : JS.Promise<obj> = jsNative

    [<Emit("$0.argv")>]
    let private argvOf (wrapped: obj) : string array = jsNative

    [<Emit("(function (srt, allowedDomains, allowUnixSockets) { return srt.SandboxManager.updateConfig({ ...srt.SandboxManager.getConfig(), network: { ...srt.SandboxManager.getConfig().network, allowedDomains: allowedDomains, allowUnixSockets: allowUnixSockets } }) })($0, $1, $2)")>]
    let private widenAllowlist (srt: obj) (allowedDomains: string array) (allowUnixSockets: string array) : unit = jsNative

    // srt's manager is a PROCESS-WIDE singleton: one filtering proxy pair, one egress
    // allowlist, initialized once. Filesystem policy is per-spawn (it rides `customConfig`
    // into the bwrap profile), so two sandboxes in a session confine their files exactly;
    // their egress allowlists, though, can only be the union — see docs/GAPS.md.
    let mutable private starting : JS.Promise<obj> option = None
    let mutable private allowed : Set<string> = Set.empty
    /// The sockets every sandbox of this session may connect to. Session-scoped for the
    /// same reason `allowed` is: srt reads it from the manager's config, not the spawn's.
    let mutable private sockets : Set<string> = Set.empty

    /// The tools this config names srt must run. macOS names none — Seatbelt ships with
    /// the OS — so the list is empty there, and so is what a failed start there settles.
    let namedTools (config: SrtConfig) : string list =
        [ config.Bwrap; config.Socat; config.Ripgrep ] |> List.choose id

    /// What a failed start settled, read against what this process can see of the tools it
    /// named. Pure in the predicate, so the decision is pinned in the cheap tier — without
    /// a sandbox, and without a box that has to be having a bad minute.
    let startFailure (executable: string -> bool) (config: SrtConfig) : StartFailure =
        if namedTools config |> List.forall executable then NothingSettled else HostCannotConfine

    /// How many times a start that settled nothing is asked again, and how long it waits in
    /// between. A fork that could not be taken is a condition that passes — but only for
    /// somebody who waits and asks again, and srt asks exactly once.
    let private startAttempts = 3
    let private startBackoffMs = 250

    /// What a start that asked and never got an answer says. srt's own sentence — `ripgrep
    /// (/nix/store/…-ripgrep-15.2.0/bin/rg) not found` — sends whoever reads it to look for
    /// a tool that is right there, so it is quoted rather than passed on, next to the thing
    /// that contradicts it.
    let probeDidNotRun (config: SrtConfig) (attempts: int) (reason: string) : string =
        sprintf
            "srt refused to start %d times running: %s. Every tool it needs is executable in this process (%s), so what failed is srt's own dependency probe — a forked `which` under a one-second timeout, which reports a fork it could not take as a tool that is not there — and not the tool it names."
            attempts
            reason
            (namedTools config |> String.concat ", ")

    /// Forget the process-wide manager: this module's memo AND srt's own, which the memo
    /// only fronts. Both, because `initialize` returns early once srt has a manager — the
    /// dependency probe included — so forgetting one half is not a fresh start, it is a
    /// start that skips the very thing a fresh one exists to redo. Called when a start
    /// settled nothing, and by tests that drive a fresh session in the same process;
    /// production starts once and keeps it until the process ends.
    let forgetManager () : Async<unit> =
        async {
            match starting with
            | None -> ()
            | Some promise ->
                starting <- None
                allowed <- Set.empty
                sockets <- Set.empty
                try
                    let! srt = Interop.awaitPromise promise
                    do! Interop.awaitPromise (resetManager srt)
                with _ -> ()
        }

    [<Emit("$0.catch($1)")>]
    let private whenRejected (promise: JS.Promise<obj>) (handler: obj -> unit) : unit = jsNative

    let private managerFor (config: SrtConfig) : Async<obj> =
        match starting with
        | Some promise ->
            async {
                let! srt = Interop.awaitPromise promise
                // Sockets widen with the domains, and for the same reason: srt reads BOTH
                // from the session config the manager was initialized with and ignores the
                // per-spawn one (`getAllowUnixSockets` reads the module-level config). A
                // second sandbox that named a socket the first did not would otherwise hold
                // a grant that exists only in its own config object — which is exactly what
                // happened: a policy carrying the nix daemon socket, a `test -S` that
                // passed, and "could not connect to any lix socket".
                //
                // The union is the same compromise docs/GAPS.md records for egress: every
                // sandbox of a session can reach what any of them may. Undo when srt reads
                // the network config per spawn.
                let widerDomains = Set.union allowed (Set.ofList config.AllowedDomains)
                let widerSockets = Set.union sockets (Set.ofList config.AllowUnixSockets)
                if widerDomains <> allowed || widerSockets <> sockets then
                    allowed <- widerDomains
                    sockets <- widerSockets
                    widenAllowlist srt (Set.toArray widerDomains) (Set.toArray widerSockets)
                return srt
            }
        | None ->
            allowed <- Set.ofList config.AllowedDomains
            sockets <- Set.ofList config.AllowUnixSockets
            // Started once, here, and memoized as its promise — including a start that
            // failed BECAUSE THIS HOST CANNOT CONFINE, so every later sandbox reports the
            // reason instead of rediscovering it. A start that settled nothing is not
            // that: it is asked again below, and forgotten if it still cannot answer.
            let promise =
                Async.StartAsPromise (
                    async {
                        let! srt = Interop.awaitPromise (importSrt ())
                        if not (supportedPlatform srt) then
                            return failwith "this platform has no srt sandbox"
                        let rec attempt (left: int) =
                            async {
                                try
                                    // Always CONFINED, whichever sandbox got here first: srt
                                    // reads the session config for anything a spawn does not
                                    // name, so an exempt one initializing the manager would
                                    // hand its exemption to the session. The exemption rides
                                    // `customConfig` per spawn, which wins outright over this.
                                    do! Interop.awaitPromise (initialize srt (toJs { config with FilesystemDisabled = false }))
                                    return srt
                                with ex ->
                                    match startFailure Fs.executable config with
                                    | HostCannotConfine -> return raise ex
                                    | NothingSettled when left > 1 ->
                                        do! Async.Sleep startBackoffMs
                                        return! attempt (left - 1)
                                    | NothingSettled -> return failwith (probeDidNotRun config startAttempts ex.Message)
                            }
                        return! attempt startAttempts
                    })
            starting <- Some promise
            // The forgetting rides the PROMISE rather than a caller: attached before anyone
            // else can be handed it, it runs once and ahead of every awaiter's continuation,
            // so no caller can clear a start that is not the one it watched fail.
            whenRejected promise (fun _ ->
                match startFailure Fs.executable config with
                | HostCannotConfine -> ()
                | NothingSettled -> forgetManager () |> Async.StartImmediate)
            Interop.awaitPromise promise

    let create (tools: SrtTools) : CreateSandbox =
        fun policy ->
            async {
                try
                    policy.WorkingDirectory |> Option.iter Fs.ensureDir
                    Fs.ensureDir (SessionLayout.tmpDir ())
                    let config = configFor tools policy
                    let! srt = managerFor config
                    let children = Children.Registry ()
                    let spawn (exec: SandboxExec) (onChunk: OutputStream * string -> unit) =
                        async {
                            try
                                let env = mergeEnv policy.Env exec.Env
                                let cwd =
                                    SandboxPath.resolvedFrom policy.WorkingDirectory exec.WorkingDirectory
                                    |> Option.toObj
                                // This sandbox's own filesystem policy rides every spawn:
                                // the manager was initialized by whichever sandbox came
                                // first, and `customConfig` is what makes the profile this
                                // one's rather than that one's.
                                let! wrapped =
                                    Interop.awaitPromise (wrapArgv srt (commandLine exec.Executable exec.Arguments) (toJs config) cwd)
                                match List.ofArray (argvOf wrapped) with
                                | [] -> return Error "srt returned an empty argv"
                                | executable :: arguments ->
                                    return Ok (children.Spawn (executable, arguments, cwd, env) onChunk)
                            with ex -> return Error ex.Message
                        }
                    return
                        Ok
                            { Ref = "srt"
                              Spawn = spawn
                              // srt confines by REWRITING the argv, so a pty costs nothing
                              // extra here: wrap exactly as `spawn` does, then open the pty
                              // on what came back. The confinement is in the argv, not in
                              // how the process is attached to a terminal.
                              SpawnPty =
                                if not (Pty.available ()) then None
                                else
                                    Some (fun exec cols rows onOutput ->
                                        async {
                                            try
                                                let env = mergeEnv policy.Env exec.Env
                                                let cwd =
                                                    SandboxPath.resolvedFrom policy.WorkingDirectory exec.WorkingDirectory
                                                    |> Option.toObj
                                                let! wrapped =
                                                    Interop.awaitPromise
                                                        (wrapArgv srt (commandLine exec.Executable exec.Arguments) (toJs config) cwd)
                                                match List.ofArray (argvOf wrapped) with
                                                | [] -> return Error "srt returned an empty argv"
                                                | executable :: arguments ->
                                                    return Pty.spawn executable arguments cwd env cols rows onOutput
                                            with ex -> return Error ex.Message
                                        })
                              // The manager stays up: it is process-wide, and a sibling
                              // sandbox may still be running under it. Its proxies die
                              // with the Session Process, which is the lifetime they are
                              // scoped to anyway.
                              Dispose = fun () -> async { children.KillAll () } }
                with ex -> return Error (sprintf "srt sandbox failed: %s" ex.Message)
            }

    /// Wrap one command under a policy, yielding the argv that runs it confined. The
    /// agent CLI's spawner needs exactly this and nothing else around it: it is handed a
    /// command by the SDK and has to produce a confined process from it.
    let wrapperFor (tools: SrtTools) (policy: SandboxPolicy) : string -> string list -> string -> Async<string list> =
        let config = configFor tools policy
        fun executable arguments cwd ->
            async {
                let! srt = managerFor config
                let! wrapped = Interop.awaitPromise (wrapArgv srt (commandLine executable arguments) (toJs config) cwd)
                match List.ofArray (argvOf wrapped) with
                | [] -> return failwith "srt returned an empty argv"
                | argv -> return argv
            }

// --- The AgentSandbox: where the agent CLI process runs ----------------------------------

/// Extra names the agent CLI may inherit beyond the host baseline: outbound-proxy
/// configuration, without which a proxied deployment's CLI cannot reach the API.
let agentPassthroughNames : string list =
    [ "HTTP_PROXY"; "HTTPS_PROXY"; "NO_PROXY"; "http_proxy"; "https_proxy"; "no_proxy"
      "NODE_EXTRA_CA_CERTS"; "SSL_CERT_FILE" ]

/// The agent CLI's confinement (PR 2 of the sandboxing plan): the SDK stays in-process;
/// the CLI it spawns goes through the `spawnClaudeCodeProcess` seam with a policy env.
/// Host tier here; the srt wrap arrives with the srt backend.
module AgentSandbox =

    /// The spawned CLI's WHOLE environment: the host baseline + proxy passthrough, a
    /// per-session scratch HOME (the CLI writes `~/.claude` state there), and exactly
    /// one credential — the turn's resolved credential displaces both ambient
    /// credential variables by construction (the allowlist never admits them), and the
    /// documented ambient last resort passes exactly those two through.
    let envFor (ambient: Map<string, string>) (home: string) (credential: (string * string) option) : Map<string, string> =
        let baseline =
            ambient
            |> Map.filter (fun name _ ->
                List.contains name hostBaselineNames || List.contains name agentPassthroughNames)
        let credentials =
            match credential with
            | Some (name, value) -> Map.ofList [ name, value ]
            | None ->
                [ "ANTHROPIC_API_KEY"; "CLAUDE_CODE_OAUTH_TOKEN" ]
                |> List.choose (fun name -> ambient |> Map.tryFind name |> Option.map (fun value -> name, value))
                |> Map.ofList
        mergeEnv baseline (Map.add "HOME" home credentials)

    /// Where the confined CLI may reach. The agent's egress needs are known — the API it
    /// talks to, and the console it refreshes an OAuth credential against — so unlike the
    /// work sandbox's, this allowlist has a real default. `YESSION_SESSION_AGENT_NET` replaces
    /// it wholesale for a deployment that fronts the API somewhere else.
    let defaultDomains : string list =
        [ "api.anthropic.com"; "console.anthropic.com"; "claude.ai" ]

    let domainsFrom (ambient: Map<string, string>) : string list =
        match ambient |> Map.tryFind "YESSION_SESSION_AGENT_NET" with
        | None -> defaultDomains
        | Some raw ->
            raw.Split ([| ','; ' '; '\t'; '\n' |])
            |> Array.map (fun domain -> domain.Trim ())
            |> Array.filter (fun domain -> domain <> "")
            |> List.ofArray

    /// The agent's own sandbox policy: it reads and writes its scratch HOME and nothing
    /// else of the operator's, and reaches only the API hosts above. `WorkingDirectory`
    /// is left to the SDK — the CLI is spawned wherever the session runs.
    let policyFor (ambient: Map<string, string>) (home: string) (env: Map<string, string>) : SandboxPolicy =
        { ReadPaths = [ home ]
          WritePaths = [ home ]
          AllowedDomains = Some (domainsFrom ambient)
          // The agent CLI talks to no local daemon. A socket nobody named is unreachable,
          // which is what this says.
          Sockets = []
          Binds = []
          Volumes = []
          Realisation = []
          Env = env
          WorkingDirectory = None
          Filesystem = Confined }

    let private childProcess : obj = importAll "node:child_process"
    let private nodeStream : obj = importAll "node:stream"
    let private nodeEvents : obj = importAll "node:events"

    // The SDK's `spawnClaudeCodeProcess` seam. The env arriving in `options.env` IS the
    // policy env (it flows from the query's `env` option), so the spawner passes it
    // verbatim. `detached: true` makes the CLI a process-group leader, and the kill on
    // `options.signal` takes the whole tree — that signal is the SDK's FORWARDED one,
    // firing only after its stdin-EOF + grace window, so the force-kill never pre-empts
    // the CLI's graceful shutdown.
    [<Emit("""(function (cp) { return (
((cp) => (options) => {
  const child = cp.spawn(options.command, options.args, { cwd: options.cwd || undefined, env: options.env, stdio: ['pipe', 'pipe', 'pipe'], detached: true })
  const killTree = () => { try { process.kill(-child.pid, 'SIGKILL') } catch { try { child.kill('SIGKILL') } catch {} } }
  if (options.signal) {
    if (options.signal.aborted) killTree()
    else options.signal.addEventListener('abort', killTree, { once: true })
  }
  return child
})(cp)
) })($0)""")>]
    let private hostSpawnerOver (cp: obj) : obj = jsNative

    /// The host-backend agent spawner, handed to the SDK as `spawnClaudeCodeProcess`.
    let hostClaudeSpawner () : obj = hostSpawnerOver childProcess

    // The srt spawner has one problem the host spawner does not: the SDK's seam is
    // SYNCHRONOUS (`options => process`) and srt's wrap is asynchronous (it resolves the
    // profile and waits on the proxy). So the process the SDK gets back is a stand-in
    // whose streams are already live — writes queue in `stdin`, reads wait on `stdout` —
    // and the real child is joined to them the moment the wrap resolves. Nothing here
    // decides anything: it buffers, pipes, forwards the two events the interface has, and
    // remembers a kill that arrived before there was anything to kill.
    [<Emit("""(function (cp, stream, events, wrap) { return (
((cp, stream, events, wrap) => (options) => {
  const emitter = new events.EventEmitter()
  const stdin = new stream.PassThrough()
  const stdout = new stream.PassThrough()
  const stderr = new stream.PassThrough()
  const state = { child: null, killed: false, exitCode: null, pending: null }
  const killTree = (child, signal) => {
    try { process.kill(-child.pid, signal) } catch { try { child.kill(signal) } catch {} }
  }
  wrap(options.command, options.args, options.cwd || '')
    .then((argv) => {
      const child = cp.spawn(argv[0], argv.slice(1), { cwd: options.cwd || undefined, env: options.env, stdio: ['pipe', 'pipe', 'pipe'], detached: true })
      state.child = child
      stdin.pipe(child.stdin)
      child.stdout.pipe(stdout)
      child.stderr.pipe(stderr)
      child.on('exit', (code, signal) => { state.exitCode = code; emitter.emit('exit', code, signal) })
      child.on('error', (error) => emitter.emit('error', error))
      if (state.pending) killTree(child, state.pending)
    })
    .catch((error) => emitter.emit('error', error instanceof Error ? error : new Error(String(error))))
  const proxy = {
    stdin, stdout, stderr,
    get killed() { return state.killed },
    get exitCode() { return state.exitCode },
    kill(signal) {
      const chosen = signal || 'SIGTERM'
      state.killed = true
      if (state.child) killTree(state.child, chosen)
      else state.pending = chosen
      return true
    },
    on: (event, listener) => { emitter.on(event, listener) },
    once: (event, listener) => { emitter.once(event, listener) },
    off: (event, listener) => { emitter.off(event, listener) },
  }
  if (options.signal) {
    const abort = () => proxy.kill('SIGKILL')
    if (options.signal.aborted) abort()
    else options.signal.addEventListener('abort', abort, { once: true })
  }
  return proxy
})(cp, stream, events, wrap)
) })($0, $1, $2, $3)""")>]
    let private srtSpawnerOver (cp: obj) (stream: obj) (events: obj) (wrap: System.Func<string, string array, string, JS.Promise<string array>>) : obj = jsNative

    /// The srt-backend agent spawner: the same seam, with the CLI coming up inside the
    /// sandbox `wrap` describes.
    let srtClaudeSpawner (wrap: string -> string list -> string -> Async<string list>) : obj =
        srtSpawnerOver
            childProcess
            nodeStream
            nodeEvents
            (System.Func<_, _, _, _> (fun executable arguments cwd ->
                Async.StartAsPromise (
                    async {
                        let! argv = wrap executable (List.ofArray arguments) cwd
                        return List.toArray argv
                    })))

    /// The spawner for the configured agent backend. Docker is not one: `parseAgent`
    /// refused it at boot.
    let claudeSpawnerFor (backend: SandboxBackend) (ambient: Map<string, string>) (home: string) (env: Map<string, string>) : obj =
        match backend with
        | SrtBackend ->
            // The tools parse fail-closed, and SessionMain has already had this value
            // accepted at boot — a bad one cannot first appear here, mid-turn.
            match SrtSandbox.toolsFrom ambient with
            | Error reason -> failwithf "agent sandbox: %s" reason
            | Ok tools ->
                let policy = policyFor ambient home env
                srtClaudeSpawner (SrtSandbox.wrapperFor tools policy)
        | HostBackend
        | DockerBackend -> hostClaudeSpawner ()

    /// Everything the SDK needs to run one turn's CLI: the environment to give it, and the
    /// spawner that puts it behind the seam.
    ///
    /// One verb, because those two are one decision seen twice. The map the SDK is handed
    /// and the map the sandbox POLICY confines have to be the SAME map — a caller that
    /// built one and passed the other would run the CLI with a credential its own policy
    /// never admitted. The scratch HOME belongs to the same decision: the policy names it,
    /// so a caller that skipped creating it would confine the CLI to a directory that is
    /// not there.
    ///
    /// The backend arrives as a VALUE, settled once at session boot, and is deliberately
    /// NOT read from the environment here. It used to be, and that second reader carried a
    /// second default that disagreed with the first: a deployment setting nothing ran the
    /// CLI unconfined while its own session had already accepted, and validated the tools
    /// for, srt. `YES008` is what keeps it to one reader.
    let prepare
        (backend: SandboxBackend)
        (dataDir: string)
        (credential: (string * string) option)
        : {| Env : Map<string, string>; Spawner : obj |} =
        let home = SessionLayout.agentHome dataDir
        Fs.ensureDir home
        let ambient = ambientEnv ()
        let env = envFor ambient home credential
        {| Env = env; Spawner = claudeSpawnerFor backend ambient home env |}

// --- Backend selection --------------------------------------------------------------------

/// The session's `CreateSandbox` for its configured backend. `Error` fails the session
/// at boot — fail closed, never a silent fallback to a weaker backend.
let forBackend (backend: SandboxBackend) (name: string) (spec: EnvironmentSpec) : Result<CreateSandbox, string> =
    let ambient = ambientEnv ()
    match backend, spec.Runtime with
    // The one question the union cannot settle: whether this BACKEND hosts containers. The
    // backend is the operator's and the spec is partly the repo's, so they are authored by
    // different people and meet only here. Refused rather than ignored — a sandbox that
    // silently did not run what it was asked to run is worse than one that says so.
    | (HostBackend | SrtBackend), Container container ->
        Error (
            sprintf
                "this session's work sandbox is %s, which runs no container%s — a container needs the docker backend"
                (SandboxBackend.describe backend)
                (match container.Command with
                 | Some command -> sprintf " and so has nothing to run '%s' as" command
                 | None -> ""))
    | HostBackend, Confinement -> Ok (HostSandbox.create ())
    // Docker always IS a container; a spec that asked for nothing in particular gets the
    // defaults rather than a second, container-less docker path.
    | DockerBackend, Container container -> Ok (DockerSandbox.create name spec container)
    | DockerBackend, Confinement -> Ok (DockerSandbox.create name spec ContainerSpec.defaults)
    | SrtBackend, Confinement ->
        SrtSandbox.toolsFrom ambient |> Result.map SrtSandbox.create
