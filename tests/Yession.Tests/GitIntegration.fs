module Yession.Tests.GitIntegration

// Plan 14: the repo manager, driven for real against LOCAL bare fixtures — deterministic,
// no network — under the srt confinement the production git sandbox uses. The pure pieces
// (the hardened invocation env, branch-name validation, output capping) run in the cheap
// tier; the [Srt] suite proves the interesting property, which is not "clone works" but
// that repo-controlled execution stays OFF: a hook and an fsmonitor planted in the
// checkout (exactly what the WorkSandbox could write) must not fire through the verbs.
//
// What is still MISSING is the one thing neither tier can be: "somebody asked for a repo and
// got it". Both halves that failed in the session this came from are substituted here — a real
// model choosing the verb, and github.com over https — so every layer can be green while the
// errand a person actually types is the thing that does not work. A live suite for it was
// written and withdrawn (see docs/GAPS.md): it never passed, and the only tier that runs a
// live agent is the release gate, so every iteration on it costs a red master.

open System
open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto
open Yession.Domain
open Yession.Oidc
open Yession.App
open Yession.Host
open Yession.SessionProcess
open Yession.Tests.Support

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

// --- Pure: the hardened invocation env -----------------------------------------------------

let private pureTests =
    testList "git invocation hardening (pure)" [
        testCase "the hardened env disables global/system config, prompts, and repo-driven execution" <| fun () ->
            let env = Repos.hardenedEnv "https" None |> Map.ofList
            Expect.equal (Map.tryFind "GIT_CONFIG_GLOBAL" env) (Some "/dev/null") "no global config"
            Expect.equal (Map.tryFind "GIT_CONFIG_SYSTEM" env) (Some "/dev/null") "no system config"
            Expect.equal (Map.tryFind "GIT_TERMINAL_PROMPT" env) (Some "0") "never prompts"
            Expect.equal (Map.tryFind "GIT_ALLOW_PROTOCOL" env) (Some "https") "protocol pinned"
            let configs =
                [ 0 .. 3 ]
                |> List.map (fun i ->
                    Map.find (sprintf "GIT_CONFIG_KEY_%d" i) env, Map.find (sprintf "GIT_CONFIG_VALUE_%d" i) env)
            Expect.equal (Map.tryFind "GIT_CONFIG_COUNT" env) (Some "4") "four forced configs without a token"
            Expect.isTrue (configs |> List.contains ("core.hooksPath", "/dev/null")) "hooks off"
            Expect.isTrue (configs |> List.contains ("core.fsmonitor", "false")) "fsmonitor off"
            Expect.isTrue (configs |> List.contains ("protocol.ext.allow", "never")) "ext transport off"

        // A helper is never useful here (the token rides in as a header config), and the
        // fallback is worse than useless: Apple's git carries `credential.helper=osxkeychain`
        // in a config file the GLOBAL/SYSTEM nulling does not reach, and under launchd that
        // helper blocks forever on a keychain prompt nobody can answer — a rejected token
        // must FAIL the verb, not hang it.
        testCase "the hardened env clears every credential helper" <| fun () ->
            let env = Repos.hardenedEnv "https" None |> Map.ofList
            let configs =
                [ 0 .. 3 ]
                |> List.map (fun i ->
                    Map.find (sprintf "GIT_CONFIG_KEY_%d" i) env, Map.find (sprintf "GIT_CONFIG_VALUE_%d" i) env)
            Expect.isTrue (configs |> List.contains ("credential.helper", "")) "helper list reset to empty"

        testCase "a token rides as one extra header config, never a bare env value" <| fun () ->
            let entries = Repos.hardenedEnv "https" (Some "gho_secret")
            let env = Map.ofList entries
            Expect.equal (Map.tryFind "GIT_CONFIG_COUNT" env) (Some "5") "one more config"
            Expect.equal
                (Map.tryFind "GIT_CONFIG_KEY_4" env)
                (Some "http.https://github.com/.extraheader")
                "scoped to github.com over https"
            let value = Map.find "GIT_CONFIG_VALUE_4" env
            Expect.isTrue (value.StartsWith "AUTHORIZATION: basic ") "a basic auth header"
            Expect.isFalse (value.Contains "gho_secret") "the raw token is base64-wrapped, not pasted"
            Expect.isFalse (entries |> List.exists (fun (_, v) -> v = "gho_secret")) "no entry carries the bare token"

        testCase "branch names: real ones pass, option-injection and traversal shapes fail" <| fun () ->
            Expect.equal (Repos.validBranchName "  feature/log-in  ") (Ok "feature/log-in") "ordinary, trimmed"
            Expect.equal (Repos.validBranchName "v1.2-rc") (Ok "v1.2-rc") "dots and dashes"
            Expect.isError (Repos.validBranchName "-c") "leading dash would be an option"
            Expect.isError (Repos.validBranchName "a..b") "traversal"
            Expect.isError (Repos.validBranchName "a b") "space"
            Expect.isError (Repos.validBranchName "x.lock") "ref lock suffix"
            Expect.isError (Repos.validBranchName "a//b") "double slash"
            Expect.isError (Repos.validBranchName "") "empty"

        // The mount target, the sandbox's write path and the path a verb reports are three
        // uses of one answer. Pinned as a mapping so a fourth caller cannot invent a second.
        testCase "where a checkout is reachable from is decided by the work backend, once" <| fun () ->
            Expect.equal (Sandboxes.reposVisibleAt HostBackend "/data/repos") "/data/repos" "the directory itself"
            Expect.equal (Sandboxes.reposVisibleAt SrtBackend "/data/repos") "/data/repos" "srt binds the same path"
            Expect.equal (Sandboxes.reposVisibleAt DockerBackend "/data/repos") "/repos" "docker reaches its mount target"

        // The listing scan walks the repos directory treating every entry as an OWNER. The
        // staging area lives in there too (it must: the git sandbox may only write under the
        // repos dir), so what keeps a clone in progress out of the listing is that its
        // directory cannot be a repo NAME — not that the scan remembers to skip it.
        testCase "the staging directory can never be read back as a repo" <| fun () ->
            Expect.isError
                (RepoRef.create (sprintf "%s/anything" Repos.stagingDirName))
                "a clone in progress is unnameable as a repo, so the scan cannot surface one"

        // What PATH's git IS differs by platform, and on macOS it is not git: `/usr/bin/git`
        // is a shim that resolves a developer directory first, through a `/var/select`
        // symlink a scoped sandbox denies. Naming one is the same rule srt's own tools
        // follow, for the same reason.
        testCase "the git a verb runs is the one named, not whatever PATH resolves" <| fun () ->
            Expect.equal
                (Repos.gitExecutable (Map.ofList [ "YESSION_GIT_PATH", " /nix/store/x/bin/git " ]))
                "/nix/store/x/bin/git"
                "a named git is trimmed and used"
            Expect.equal
                (Repos.gitExecutable (Map.ofList [ "YESSION_GIT_PATH", "" ]))
                "git"
                "a blank one is unset, not an executable of empty string"
            Expect.equal
                (Repos.gitExecutable Map.empty)
                "git"
                "and unnamed falls back to PATH, so an off-Nix install does not regress"

        // The probe that gates every verb was the one git invocation built by hand, with an
        // EMPTY env — an env no verb ever runs with — so what it proved was never about the
        // git the verbs use. git resolves its global config path before it does anything at
        // all, `--version` included; it tolerates an EACCES there and treats every other
        // errno as fatal; and a sandbox that denies the operator's home answers EPERM. So
        // the probe died `fatal: unable to access '~/.config/git/config'` (exit 128) and
        // refused a working git for the sandbox's whole lifetime, naming two knobs that were
        // not the fault — one of which, followed, hands back the home the scope exists to
        // deny. Every git spawned here is now built by `gitExec`, which cannot be called
        // without the env.
        testCase "the probe runs the verbs' hardened environment, never an empty one" <| fun () ->
            let probe = Repos.gitExec "/nix/store/x/bin/git" "/repos" "https" None [ "--version" ]
            Expect.equal
                probe.Env
                (Repos.hardenedEnv "https" None |> Map.ofList)
                "a probe that runs a different environment gates verbs it cannot speak for"

        // The words an operator gets when git cannot run confined. What shipped instead was
        // the host binary's own parting line — `xcode-select: error: unable to read data
        // link ...` — which names neither the sandbox nor anything anyone can set, and was
        // read as a broken Xcode install on a machine whose Xcode was fine.
        testCase "an unusable git names the sandbox and the knobs, not the host binary's excuse" <| fun () ->
            let message = Repos.unusableGit "/usr/bin/git" "exit 2: xcode-select: error: unable to read data link"
            Expect.isTrue (message.Contains "/usr/bin/git") "which git could not run"
            Expect.isTrue (message.Contains "inside the sandbox") "where it could not run"
            Expect.isTrue (message.Contains "YESSION_GIT_PATH") "how to name a working one"
            Expect.isTrue (message.Contains "YESSION_SANDBOX_READ_PATHS") "how to let it read what it needs"
            Expect.isTrue (message.Contains "xcode-select") "and what it actually said, kept"

        testCase "capped output states its elision" <| fun () ->
            Expect.equal (Repos.capText 10 "short") "short" "under the cap, untouched"
            let capped = Repos.capText 5 "0123456789"
            Expect.isTrue (capped.StartsWith "01234") "the head is kept"
            Expect.isTrue (capped.Contains "5 more characters omitted") "the elision is stated"
    ]

// --- [Srt]: the verbs against local bare fixtures, confined for real ------------------------

let private nodeFs : obj = importAll "node:fs"
let private nodeOs : obj = importAll "node:os"
let private childProcess : obj = importAll "node:child_process"

[<Emit("$0.mkdtempSync($1.tmpdir() + '/yession-git-')")>]
let private mkdtemp (fs: obj) (os: obj) : string = jsNative

[<Emit("$0.mkdirSync($1, { recursive: true })")>]
let private mkdir (fs: obj) (path: string) : unit = jsNative

[<Emit("$0.writeFileSync($1, $2)")>]
let private writeFile (fs: obj) (path: string) (content: string) : unit = jsNative

[<Emit("$0.existsSync($1)")>]
let private exists (fs: obj) (path: string) : bool = jsNative

[<Emit("(() => { try { return $0.readdirSync($1) } catch { return [] } })()")>]
let private readDirSafe (fs: obj) (path: string) : string array = jsNative

/// A checkout that is FINISHED, rather than one that has started. `.git` appears within
/// milliseconds of a clone beginning and says nothing about whether there is a work tree
/// yet — which is the whole reason the clone now lands by rename. Anything beside `.git`
/// only exists once git has checked the work tree out.
let private checkoutWhole (fs: obj) (dir: string) : bool =
    exists fs (sprintf "%s/.git" dir)
    && readDirSafe fs dir |> Array.exists (fun entry -> entry <> ".git")

[<Emit("$0.chmodSync($1, 0o755)")>]
let private makeExecutable (fs: obj) (path: string) : unit = jsNative

/// Host-side git for FIXTURE SETUP only — the code under test never runs unconfined.
[<Emit("$0.execFileSync('git', $1, { cwd: $2, env: { ...process.env, GIT_CONFIG_GLOBAL: '/dev/null', GIT_CONFIG_SYSTEM: '/dev/null', GIT_AUTHOR_NAME: 'fixture', GIT_AUTHOR_EMAIL: 'f@x', GIT_COMMITTER_NAME: 'fixture', GIT_COMMITTER_EMAIL: 'f@x' }, stdio: 'pipe' })")>]
let private hostGit (cp: obj) (args: string array) (cwd: string) : unit = jsNative

/// The fixtures live in a SIBLING of the repos dir, never an ancestor of it. srt re-binds
/// an allowRead path over the write binds when both sit under a denyRead region (HOME —
/// which is where a CI runner's temp dir lives), so an allowRead ANCESTOR of the repos dir
/// lands on top of it read-only and every clone dies with "Read-only file system". A
/// sibling cannot cover it. Production never hits this: it passes no extra read paths, and
/// srt skips the re-bind of a path that is itself the write path.
let private fixturesIn (root: string) : string = sprintf "%s/fixtures" root

/// Where THIS harness's checkouts land — the session's own layout, asked for rather than
/// spelled out again at each of the ten places below that name a path under it.
let private reposIn (root: string) : string = Sandboxes.SessionLayout.reposDir root

/// A local bare repo with one commit on `main` — what the service clones from, over the
/// `file` protocol the test config allows.
let private makeBareFixture (root: string) (name: string) : string =
    let fixtures = fixturesIn root
    let work = sprintf "%s/work-%s" fixtures name
    mkdir nodeFs work
    hostGit childProcess [| "init"; "-b"; "main" |] work
    writeFile nodeFs (sprintf "%s/README.md" work) "fixture\n"
    hostGit childProcess [| "add"; "." |] work
    hostGit childProcess [| "commit"; "-m"; "seed" |] work
    let bare = sprintf "%s/%s.git" fixtures name
    hostGit childProcess [| "clone"; "--bare"; work; bare |] fixtures
    bare

/// The service, with the git it runs, the credential it spends, and somewhere to record a
/// network failure. The credential and the recorder are the caller's because whether a verb
/// spent a credential is exactly what decides whether a failure says anything about one.
let private serviceSpending
    (git: string)
    (token: string option)
    (failures: ResizeArray<ActorRef * string>)
    (root: string)
    (log: EventLog<SessionEvent>)
    : Repos.ReposService =
    // The session's own layout, not a second guess at it: these verbs run against a repos
    // directory nested inside a workspace in production, and a harness that flattens that
    // is a harness testing a shape nothing ships. The fixtures stay a SIBLING of it (see
    // `fixturesIn`) — that rule is about ancestry, which nesting does not change.
    let reposDir = reposIn root
    let fixtures = fixturesIn root
    mkdir nodeFs reposDir
    mkdir nodeFs fixtures
    Repos.create
        { Backend = SrtBackend
          ReposDir = reposDir
          // Host-family: a terminal reaches the checkouts at the directory itself.
          VisibleAt = Sandboxes.reposVisibleAt SrtBackend reposDir
          ExtraReadPaths = [ fixtures ]
          Git = git
          AllowedDomains = []
          AllowProtocol = "file"
          CloneUrl = fun ref -> sprintf "file://%s/%s.git" fixtures (RepoRef.repo ref)
          ResolveToken = fun _ -> async { return token }
          OnNetworkFailure = fun actor said -> async { failures.Add (actor, said) }
          Log = log }
    |> expect

/// This box's git, as a session would resolve it.
let private namedGit = Repos.gitExecutable (Sandboxes.ambientEnv ())

/// The service under a named git, anonymous. What the git-resolution cases drive.
let private serviceWith (git: string) (root: string) (log: EventLog<SessionEvent>) : Repos.ReposService =
    serviceSpending git None (ResizeArray ()) root log

/// The service as a session builds it: this box's named git, or PATH's, and anonymous with
/// nothing listening for failures. Every case that is not about credentials wants this one.
let private serviceIn (root: string) (log: EventLog<SessionEvent>) : Repos.ReposService =
    serviceWith namedGit root log

let private freshLog () =
    InMemoryEventLog.create (SessionId.create "git-suite" |> expect) (fun () -> DateTimeOffset.UtcNow)

let private ada = PeerId.create "ada" |> expect
let private caller : Repos.RepoCaller = { Actor = ActorRef.Agent; Credential = PeerRef ada }

let private eventsOf (log: EventLog<SessionEvent>) : Async<SessionEvent list> =
    async {
        let! page = log.Read None Int32.MaxValue
        return page.Events |> List.map (fun envelope -> envelope.Event)
    }

// --- Where a session puts its checkouts (cheap tier; fs only) ------------------------------
// The layout is a claim about a TERMINAL: it starts in the workspace, so this is what
// decides whether the agent's first `ls` shows the clones or an empty directory.

let private layoutTests =
    testList "the session's layout" [

        testCase "a clone lands inside the workspace a terminal starts in" <| fun () ->
            let dataDir = "/data/sessions/AAZ"
            let workspace = Sandboxes.SessionLayout.workspaceFor dataDir SandboxName.defaultName
            let repos = Sandboxes.SessionLayout.reposDir dataDir
            Expect.isTrue
                (repos.StartsWith (workspace + "/"))
                "a checkout beside the workspace is one nobody sees without being told its path"

        // The repos directory is shared by every sandbox — one parameter, the session's data
        // dir, so there can only be one — but a WORKSPACE is the thing two sandboxes exist to
        // keep apart.
        testCase "a named sandbox works somewhere the default one does not" <| fun () ->
            let dataDir = "/data/sessions/AAZ"
            let named = SandboxName.create "review" |> expect
            Expect.notEqual
                (Sandboxes.SessionLayout.workspaceFor dataDir named)
                (Sandboxes.SessionLayout.workspaceFor dataDir SandboxName.defaultName)
                "what happens in one sandbox does not happen in the other"

        // A data directory outlives its process, which is what makes a checkout survive a
        // relaunch. Left at the old path the checkouts are not deleted, they are INVISIBLE:
        // the listing scans the new path, reports nothing, and the agent re-clones over work
        // that was never committed.
        testCase "checkouts from the old layout are moved, not stranded" <| fun () ->
            let dataDir = mkdtemp nodeFs nodeOs
            mkdir nodeFs (sprintf "%s/octo/hello/.git" (Sandboxes.SessionLayout.legacyReposDir dataDir))
            let repos = Sandboxes.SessionLayout.prepareReposDir dataDir
            Expect.isTrue
                (exists nodeFs (sprintf "%s/octo/hello/.git" repos))
                "the checkout is where the listing now looks"

        // `renameSync` onto a non-empty directory throws, and this runs at boot: a session
        // that somehow had both would fail to start rather than decline the adoption.
        testCase "a session already on the current layout still boots" <| fun () ->
            let dataDir = mkdtemp nodeFs nodeOs
            mkdir nodeFs (sprintf "%s/octo/hello/.git" (Sandboxes.SessionLayout.legacyReposDir dataDir))
            mkdir nodeFs (sprintf "%s/octo/current/.git" (Sandboxes.SessionLayout.reposDir dataDir))
            let repos = Sandboxes.SessionLayout.prepareReposDir dataDir
            Expect.isTrue
                (exists nodeFs (sprintf "%s/octo/current/.git" repos))
                "the layout it already had is the one it keeps"
    ]

let private srtTests =
    testList "repo verbs under srt (local fixtures)" [
        testCaseAsync "add clones into the repos dir, records the fact, and re-add is a quiet no-op" <| async {
            let root = mkdtemp nodeFs nodeOs
            makeBareFixture root "hello" |> ignore
            let log = freshLog ()
            let service = serviceIn root log
            let repo = RepoRef.create "octo/hello" |> expect
            let! listing = service.AddRepo caller repo
            let listing = expect listing
            Expect.equal listing.Branch "main" "on the fixture's default branch"
            Expect.isFalse listing.Dirty "clean checkout"
            Expect.isTrue (exists nodeFs (sprintf "%s/octo/hello/.git" (reposIn root))) "checkout landed at owner/repo"
            let! events = eventsOf log
            match events with
            | [ SessionEvent.RepoAdded added ] ->
                Expect.equal added.Repo repo "the event names the repo"
                Expect.equal added.Branch "main" "and its branch"
                Expect.equal added.Actor ActorRef.Agent "the agent is the acting party"
            | other -> failwithf "expected exactly one RepoAdded, got %A" other
            let! again = service.AddRepo caller repo
            Expect.equal (expect again).Branch "main" "re-add answers with current state"
            let! events = eventsOf log
            Expect.equal (List.length events) 1 "and records nothing new"

            let! listed = service.ListRepos ()
            Expect.equal
                (expect listed)
                [ { Repo = repo; Branch = "main"; Dirty = false; Path = sprintf "%s/octo/hello" (reposIn root) } ]
                "the listing is the filesystem's answer, and it says where"
        }

        // Every case here uses an absolute mkdtemp; a SESSION's data directory need not be
        // one — the unset-`YESSION_SESSION_DATA` default is relative, and so is what the
        // live suite passes. That difference was invisible and total: the clone landed and
        // then every verb, `add_repo`'s own listing included, reported `cannot change to
        // ...: No such file or directory` about the checkout it had just made, because
        // `git -C <relative>` runs in a sandbox whose cwd is already that directory and so
        // resolves it twice. A repo on disk that no verb would admit to.
        testCaseAsync "a relative repos directory is still one, however git is asked about it" <| async {
            let fixtures = mkdtemp nodeFs nodeOs
            makeBareFixture fixtures "hello" |> ignore
            // Relative on purpose. Fixtures stay absolute so the only variable is this.
            let relRepos =
                Sandboxes.SessionLayout.reposDir
                    (sprintf "tests/Yession.Tests/out/.data/relative-%s" (string (Guid.NewGuid ())))
            mkdir nodeFs relRepos
            let log = freshLog ()
            let service =
                Repos.create
                    { Backend = SrtBackend
                      ReposDir = relRepos
                      VisibleAt = Sandboxes.reposVisibleAt SrtBackend relRepos
                      ExtraReadPaths = [ fixturesIn fixtures ]
                      Git = namedGit
                      AllowedDomains = []
                      AllowProtocol = "file"
                      CloneUrl = fun ref -> sprintf "file://%s/%s.git" (fixturesIn fixtures) (RepoRef.repo ref)
                      ResolveToken = fun _ -> async { return None }
                      OnNetworkFailure = fun _ _ -> async { return () }
                      Log = log }
                |> expect
            let repo = RepoRef.create "octo/hello" |> expect
            let! listing = service.AddRepo caller repo
            Expect.equal (expect listing).Branch "main" "the verb reports the checkout it made"
        }

        testCaseAsync "a git that cannot run inside the sandbox refuses the verb in words that name a knob" <| async {
            // The shape that shipped: on macOS the verbs ran PATH's `/usr/bin/git`, a shim
            // that resolves a developer directory through files the read scope denies, so
            // every verb failed with `xcode-select: error: ...` — read, reasonably, as a
            // broken Xcode install on a machine whose Xcode was fine. The probe turns any
            // unrunnable git into one sentence about the sandbox, whatever the reason.
            let root = mkdtemp nodeFs nodeOs
            makeBareFixture root "hello" |> ignore
            let service = serviceWith (sprintf "%s/not-a-git" root) root (freshLog ())
            let! added = service.AddRepo caller (RepoRef.create "octo/hello" |> expect)
            match added with
            | Ok _ -> failwith "a verb ran with a git that does not exist"
            | Error reason ->
                Expect.isTrue (reason.Contains "cannot run inside the sandbox") "the sandbox is named as the place"
                Expect.isTrue (reason.Contains "YESSION_GIT_PATH") "and the knob that fixes it"
        }

        // The complement, and the fault the case above was written blind to: a git that CAN
        // run, in a session whose operator has a global git config. The probe read it — it
        // was the one git spawn built without the hardened env — so every verb was refused
        // for a fault that lives in this repository, in words blaming the host's binary and
        // the read scope.
        //
        // Why it stayed invisible until somebody hit it: the config has to be READABLE to
        // break anything. Linux denies a read by mounting emptiness over the path, so git
        // meets ENOENT and shrugs; Seatbelt answers EPERM, which git treats as fatal. A home
        // outside the read scope would therefore pass this case on the platform CI runs
        // while production stayed broken — so the config goes somewhere the sandbox may read
        // (the fixtures dir, already an extra read path) and every platform runs the fault
        // the way macOS ran it.
        testCaseAsync "a global git config in the operator's home never reaches a verb" <| async {
            let root = mkdtemp nodeFs nodeOs
            makeBareFixture root "hello" |> ignore
            let home = sprintf "%s/home" (fixturesIn root)
            mkdir nodeFs home
            // Malformed on purpose. A config git PARSES is a fault it reports as its own,
            // which is exactly what an unhardened spawn hands back to whoever asked.
            writeFile nodeFs (sprintf "%s/.gitconfig" home) "this is not a config\n"
            let! added =
                withEnv [ "HOME", Some home ] (fun () ->
                    async {
                        let service = serviceIn root (freshLog ())
                        return! service.AddRepo caller (RepoRef.create "octo/hello" |> expect)
                    })
            Expect.isOk added "no git spawned here reads the operator's global config, the probe included"
        }

        // A verb that failed while spending somebody's credential is the only moment this
        // session gets to find out that credential stopped working — nothing else here can
        // tell. What the failure MEANS is not decided here (git cannot tell an expired token
        // from a repo that is not there); this only has to say that one was spent.
        testCaseAsync "a network verb that fails while spending a credential says so" <| async {
            let root = mkdtemp nodeFs nodeOs
            let failures = ResizeArray<ActorRef * string> ()
            // No fixture made, so the clone has nothing to clone.
            let service = serviceSpending namedGit (Some "ghu_whatever") failures root (freshLog ())
            let! added = service.AddRepo caller (RepoRef.create "octo/missing" |> expect)
            Expect.isError added "the clone fails"
            Expect.equal (List.ofSeq failures |> List.map fst) [ caller.Credential ] "reported once, against the credential's actor"
            Expect.isTrue (failures |> Seq.forall (fun (_, said) -> said <> "")) "and carries what git said"
        }

        testCaseAsync "a network verb that fails anonymously says nothing about anyone's sign-in" <| async {
            let root = mkdtemp nodeFs nodeOs
            let failures = ResizeArray<ActorRef * string> ()
            let service = serviceSpending namedGit None failures root (freshLog ())
            let! added = service.AddRepo caller (RepoRef.create "octo/missing" |> expect)
            Expect.isError added "the clone still fails"
            Expect.isEmpty failures "no credential was spent, so none can have been refused"
        }

        testCaseAsync "a clone brings no hook templates with it" <| async {
            let root = mkdtemp nodeFs nodeOs
            makeBareFixture root "hello" |> ignore
            let service = serviceIn root (freshLog ())
            let repo = RepoRef.create "octo/hello" |> expect
            let! added = service.AddRepo caller repo
            expect added |> ignore
            // git's default templates are `.git/hooks/*.sample` files, and srt's macOS
            // profile denies every write under `**/.git/hooks/**` — so a clone that copies
            // them dies there. Linux denies only what EXISTS when the spawn is wrapped, so
            // this suite cannot see that failure; what it can see is the flag that avoids
            // it, which is the absence of the directory the copy would have filled.
            Expect.isFalse
                (exists nodeFs (sprintf "%s/octo/hello/.git/hooks" (reposIn root)))
                "no hooks directory to populate (the clone asks for no templates)"
        }

        testCaseAsync "switch creates and moves branches, and the events say so" <| async {
            let root = mkdtemp nodeFs nodeOs
            makeBareFixture root "hello" |> ignore
            let log = freshLog ()
            let service = serviceIn root log
            let repo = RepoRef.create "octo/hello" |> expect
            let! _ = service.AddRepo caller repo
            let! switched = service.SwitchBranch caller repo "feature/x" true
            Expect.equal (expect switched).Branch "feature/x" "created and moved"
            let! back = service.SwitchBranch caller repo "main" false
            Expect.equal (expect back).Branch "main" "moved back"
            let! events = eventsOf log
            let switches =
                events |> List.choose (function SessionEvent.RepoBranchSwitched s -> Some (s.Branch, s.Created) | _ -> None)
            Expect.equal switches [ "feature/x", true; "main", false ] "both switches recorded"
            let! refused = service.SwitchBranch caller repo "-c" false
            Expect.isError refused "an option-shaped name never reaches git"
        }

        testCaseAsync "a hook and an fsmonitor planted in the checkout do not fire through the verbs" <| async {
            let root = mkdtemp nodeFs nodeOs
            makeBareFixture root "hello" |> ignore
            let log = freshLog ()
            let service = serviceIn root log
            let repo = RepoRef.create "octo/hello" |> expect
            let! _ = service.AddRepo caller repo
            let checkout = sprintf "%s/octo/hello" (reposIn root)
            let marker = sprintf "%s/PWNED" (reposIn root)
            // What a poisoned WorkSandbox could plant: an executable hook, and a config
            // pointing fsmonitor at it. Both are inside the sandbox's own write set, so
            // only the per-invocation GIT_CONFIG_* overrides stand between them and
            // execution.
            let hook = sprintf "#!/bin/sh\ntouch %s\n" marker
            mkdir nodeFs (sprintf "%s/.git/hooks" checkout)
            writeFile nodeFs (sprintf "%s/.git/hooks/post-checkout" checkout) hook
            makeExecutable nodeFs (sprintf "%s/.git/hooks/post-checkout" checkout)
            writeFile nodeFs (sprintf "%s/.git/evil.sh" checkout) hook
            makeExecutable nodeFs (sprintf "%s/.git/evil.sh" checkout)
            hostGit childProcess [| "config"; "core.fsmonitor"; sprintf "%s/.git/evil.sh" checkout |] checkout
            hostGit childProcess [| "config"; "core.hooksPath"; sprintf "%s/.git/hooks" checkout |] checkout
            let! _ = service.SwitchBranch caller repo "probe" true
            let! status = service.RepoStatus repo
            expect status |> ignore
            let! diff = service.RepoDiff repo
            expect diff |> ignore
            Expect.isFalse (exists nodeFs marker) "no planted code ran (hooksPath/fsmonitor forced off per invocation)"
        }

        // The rename is what makes the visible path binary, and these are its two halves:
        // a clone that never finished is not there at all, and a clone still running is not
        // a second clone. Before this, `.git` existing was the only test of presence, and
        // git creates that in the first milliseconds — so a concurrent reader met a repo
        // with no HEAD and was told `fatal: ambiguous argument 'HEAD'`.
        testCaseAsync "a checkout is never visible half-made: the path appears whole, or not at all" <| async {
            let root = mkdtemp nodeFs nodeOs
            makeBareFixture root "hello" |> ignore
            let service = serviceIn root (freshLog ())
            let repo = RepoRef.create "octo/hello" |> expect
            let checkout = sprintf "%s/octo/hello" (reposIn root)
            let mutable halfMade = false
            let mutable running = true
            // Watch the visible path for as long as the clone runs. There are only two
            // states it may ever be seen in: not there, or finished. A `.git` with no work
            // tree beside it is the state that told a second reader "ambiguous argument
            // 'HEAD'" and had it conclude the repo was empty.
            //
            // A sampler can only ever MISS, never cry wolf — and against a local fixture it
            // gets a few hundred looks at a window that used to last the whole clone. It was
            // confirmed by regressing the rename and watching this go red.
            let watch =
                async {
                    while running do
                        if exists nodeFs checkout && not (checkoutWhole nodeFs checkout) then halfMade <- true
                        do! Async.Sleep 1
                }
            let add =
                async {
                    let! added = service.AddRepo caller repo
                    running <- false
                    expect added |> ignore
                }
            let! _ = Async.Parallel [ add; watch ]
            Expect.isFalse halfMade "the path was never seen with a .git and no work tree"
            Expect.isTrue (checkoutWhole nodeFs checkout) "and it is whole once the clone answers"
        }

        testCaseAsync "a clone that fails leaves nothing behind — not at the repo's path, not staged" <| async {
            let root = mkdtemp nodeFs nodeOs
            // No fixture is made, so the clone URL names a bare repo that is not there.
            let log = freshLog ()
            let service = serviceIn root log
            let repo = RepoRef.create "octo/missing" |> expect
            let! added = service.AddRepo caller repo
            Expect.isError added "a clone of something that is not there fails"
            Expect.isFalse (exists nodeFs (sprintf "%s/octo/missing" (reposIn root))) "nothing at the visible path"
            Expect.equal
                (readDirSafe nodeFs (sprintf "%s/%s" (reposIn root) Repos.stagingDirName))
                [||]
                "and nothing left in the staging area"
            let! events = eventsOf log
            Expect.isEmpty events "a clone that did not happen records nothing"
        }

        testCaseAsync "a checkout left behind by an interrupted clone is named, with the way out" <| async {
            let root = mkdtemp nodeFs nodeOs
            makeBareFixture root "hello" |> ignore
            let service = serviceIn root (freshLog ())
            let repo = RepoRef.create "octo/hello" |> expect
            // Exactly what a clone that died partway used to leave at the visible path: a
            // repository with no commit in it. Nothing can produce this any more, but a
            // machine that ran the old code still has one.
            let checkout = sprintf "%s/octo/hello" (reposIn root)
            mkdir nodeFs checkout
            hostGit childProcess [| "init"; "-b"; "main" |] checkout
            let! added = service.AddRepo caller repo
            match added with
            | Ok _ -> failwith "expected an unreadable checkout to be reported, not described"
            | Error reason ->
                Expect.isTrue (reason.Contains "octo/hello") "the answer names the repo"
                Expect.isTrue (reason.Contains "remove_repo") "and how to clear it"
        }

        testCaseAsync "the same repo asked for twice at once is cloned once, and recorded once" <| async {
            let root = mkdtemp nodeFs nodeOs
            makeBareFixture root "hello" |> ignore
            let log = freshLog ()
            let service = serviceIn root log
            let repo = RepoRef.create "octo/hello" |> expect
            let! both = Async.Parallel [ service.AddRepo caller repo; service.AddRepo caller repo ]
            let listings = both |> Array.map expect
            Expect.equal listings.[0] listings.[1] "both callers are answered with the same listing"
            let! events = eventsOf log
            Expect.equal (List.length events) 1 "one clone happened, and it was recorded once"
        }

        testCaseAsync "remove deletes the checkout and records who asked" <| async {
            let root = mkdtemp nodeFs nodeOs
            makeBareFixture root "hello" |> ignore
            let log = freshLog ()
            let service = serviceIn root log
            let repo = RepoRef.create "octo/hello" |> expect
            let! _ = service.AddRepo caller repo
            let human : Repos.RepoCaller = { Actor = PeerRef ada; Credential = PeerRef ada }
            let! removed = service.RemoveRepo human repo
            expect removed
            Expect.isFalse (exists nodeFs (sprintf "%s/octo/hello" (reposIn root))) "checkout gone"
            let! events = eventsOf log
            match events |> List.rev |> List.head with
            | SessionEvent.RepoRemoved r -> Expect.equal r.Actor (PeerRef ada) "the human is the acting party"
            | other -> failwithf "expected RepoRemoved last, got %A" other
            let! refetch = service.FetchRepo caller repo
            Expect.isError refetch "a removed repo is legibly not here"
        }
    ]

[<Emit("process.execPath")>]
let private nodeExecutable : string = jsNative

// --- [Ports; Native; Srt]: the session's own composition, without a model -------------------
//
// The verbs above are driven against a config this file writes. What that cannot reach is the
// one the SESSION writes — and that is where the fault lived: a relative data directory
// flowed from the composition root into `Repos.create`, `git -C` resolved every path twice,
// and every verb reported a failure about a checkout sitting right there.
//
// The live case below could not catch it either. It settles the moment the checkout appears,
// which is while the agent is still streaming, so it never observes what the VERB said. This
// one does, deterministically: a real Session Process over its real composition, a data
// directory that is relative on purpose, a checkout planted where the session will look, and
// the answer read off the `repos` query — the same projection a person's settings surface
// shows. No model, so nothing here turns on what a model decides to call.

let private reposQueryDeadlineMs = 30_000

let private compositionTests =
    testList "the repos the session itself reports" [
        testCaseAsync "a session reads back a checkout in its own repos directory" <| async {
            // RELATIVE on purpose: this is the condition the harness above never had, and the
            // one that broke every verb. `Fs.absolute` at the root and at `Repos.create` is
            // what this is holding down.
            let dataDir =
                sprintf "tests/Yession.Tests/out/.data/repos-query-%s" (string (Guid.NewGuid ()))
            let! pm =
                ProcessManager.create
                    { ProcessManager.Options.defaults dataDir nodeExecutable [ "app/SessionMain.js" ] with
                        Strategy = Some Strategy.localhost }
            let record = pm.CreateSession "repos-query" "Repos" |> expect

            // Planted BEFORE the launch, so the session finds it rather than races it. A real
            // repository, because the answer under test is git's: the branch it is on.
            let sessionDir = sprintf "%s/%s" dataDir record.DataDir
            let checkout = sprintf "%s/octo/hello" (Sandboxes.SessionLayout.reposDir sessionDir)
            mkdir nodeFs checkout
            hostGit childProcess [| "init"; "-b"; "main" |] checkout
            writeFile nodeFs (sprintf "%s/README.md" checkout) "planted\n"
            hostGit childProcess [| "add"; "." |] checkout
            hostGit childProcess [| "commit"; "-m"; "planted" |] checkout

            let! launched = pm.Launch record.SessionId
            let port = launched |> expect
            let sessionUrl = sprintf "http://127.0.0.1:%d" port
            let! opened = OidcHttp.openSession sessionUrl

            let frames = ResizeArray<QueryFrame> ()
            let subscription =
                Sse.subscribe
                    (sessionUrl + "/queries")
                    [ "cookie", OidcHttp.cookieHeader opened.Jar ]
                    (fun data ->
                        match Codec.fromString Codec.queryFrame data with
                        | Ok frame -> frames.Add frame
                        | Error _ -> ())

            let reposRows () =
                frames
                |> Seq.tryPick (fun frame ->
                    match frame with
                    | QueryValued (name, RowsOf rows) when QueryName.value name = "repos" -> Some rows
                    | _ -> None)

            let! _ = settledWithin reposQueryDeadlineMs (fun () -> (reposRows ()).IsSome)

            // A query that FAILS to read sends nothing — there is no error frame — so a red
            // here is a silence, and silence has three causes worth telling apart: the stream
            // never connected, the repos capability never started (no declaration), or it
            // started and git could not read the checkout (declared, never valued). That last
            // one is the fault this case exists for, and without this line all three print
            // the same timeout.
            let declared =
                frames
                |> Seq.tryPick (fun frame ->
                    match frame with
                    | QueriesDeclared defs -> Some (defs |> List.map (fun d -> QueryName.value d.Name))
                    | _ -> None)
            let arrived =
                sprintf
                    "%d frame(s); declared: %s; repos valued: %b"
                    frames.Count
                    (match declared with
                     | Some names -> String.concat ", " names
                     | None -> "(the stream never declared anything)")
                    (reposRows ()).IsSome

            match reposRows () with
            | Some [ row ] ->
                let cell key = row |> List.tryPick (fun (k, v) -> if k = key then Some v else None)
                Expect.equal (cell "repo") (Some (CellText "octo/hello")) "the checkout it was given"
                // The assertion the fault would have failed: this is git's answer about the
                // checkout, and getting it means `git -C` reached it.
                Expect.equal (cell "branch") (Some (CellText "main")) "on the branch git says it is on"
            | Some other -> failwithf "expected exactly the planted repo, got %A (%s)" other arrived
            | None ->
                failwithf
                    "the repos query never said what is checked out here — %s. A declared query that never values is one whose read failed, which is what a relative repos directory does to every verb."
                    arrived

            subscription.Stop ()
            do! pm.StopAll ()
        }
    ]

// --- [LiveAgent]: the clone path, as somebody actually asks for it -------------------------
//
// The verbs above are driven directly, against local fixtures. This one is the whole path a
// person uses: a real model, a real turn, `add_repo` chosen by the agent rather than called by
// the test, and a real checkout from GitHub landing on disk.
//
// It took four attempts to get here, and each failure is why a piece of this looks the way it
// does. The first died on `Runner.WaitFor`'s 30s hang detector (sized for sub-9s tests), so
// this case owns its deadline. The second set that deadline to 180s and blew the Node suite's
// shared 240s budget, killing every suite before a word was printed — hence 90s, which is more
// than ten times the 6.9s a passing run takes. The third reported "no checkout" with an empty
// conversation, and the report below is the only reason that was diagnosable: `Phase2` was
// deleting `ANTHROPIC_API_KEY` from the process env on its way out, so this session started
// with no credential and therefore no agent at all. `Support.withEnv` is what stopped that.
//
// So the report stays, printed on green as well as red. A red here means the clone path is
// broken, and the report is what says which way: no turn ran at all (no agent item), a turn ran
// and the clone was refused (the agent's words carry git's), or the clone hung (a turn in
// flight, nothing on disk).

[<Emit("(() => { try { return $0.readdirSync($1).join(', ') || '<empty>' } catch (e) { return '<' + (e.code || e.message) + '>' } })()")>]
let private listDir (fs: obj) (path: string) : string = jsNative

let private liveRepo = "octocat/Hello-World"

/// Ten times a passing run, and well inside what the `LiveAgent` tier's budget buys
/// (`tasks.fsx` `nodeBudgetMs`) — so a failure here is this case's report, never the runner
/// killing every suite at once. `settledWithin` refuses a deadline the run cannot afford, so
/// this number can no longer quietly outgrow the run it is spent from.
let private cloneDeadlineMs = 90_000

let private liveClone =
    testList "add_repo, as somebody asks for it" [
        testCaseAsync "a person asks the agent for a repo, and the checkout lands" <| async {
            let dataDir =
                sprintf
                    "tests/Yession.Tests/out/.data/clone-live-%d"
                    (int (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) % 1000000)
            let! pm =
                ProcessManager.create
                    { ProcessManager.Options.defaults dataDir nodeExecutable [ "app/SessionMain.js" ] with
                        Strategy = Some Strategy.localhost }
            let record = pm.CreateSession "clone-live" "Clone" |> expect
            let! launched = pm.Launch record.SessionId
            let port = launched |> expect
            let! opened = OidcHttp.openSession (sprintf "http://127.0.0.1:%d" port)
            let! ada = connectClient (sprintf "http://127.0.0.1:%d/signal" port) opened.PeerToken "ada" "Ada"

            let sessionDir = sprintf "%s/%s" dataDir record.DataDir
            // ASKED FOR, never re-derived: this is the session's own answer for where its
            // checkouts live, and a second `sprintf` here is exactly the drift `SessionLayout`
            // exists to prevent. It had one — the repos directory moved inside the workspace
            // and this line kept looking beside it, so the case reported "no checkout" about a
            // checkout sitting one directory away. Only the release gate runs this tier, so the
            // stale copy could not be caught anywhere it would have been cheap to catch.
            let reposDir = Sandboxes.SessionLayout.reposDir sessionDir
            let checkout = sprintf "%s/%s" reposDir liveRepo

            /// Everything a reader needs to tell the three faults apart, printed whatever happens.
            let report (why: string) =
                let model = ada.Runner.Model ()
                let items =
                    match model.Conversation.Items with
                    | [] -> "    (no conversation items at all)"
                    | items ->
                        items
                        |> List.map (fun i -> sprintf "    %A [%A] %s" i.Author i.Status (i.Body.Replace ("\n", " ")))
                        |> String.concat "\n"
                let terminals =
                    match model.Terminals.Terminals with
                    | [] -> "    (none opened)"
                    | ts ->
                        ts
                        |> List.collect (fun t ->
                            t.Blocks |> List.map (fun b -> sprintf "    %s -> %A" b.Command b.Status))
                        |> function [] -> "    (terminals, no blocks)" | ls -> String.concat "\n" ls
                sprintf
                    "CLONE: %s\n  environment: %A\n  conversation:\n%s\n  terminal blocks:\n%s\n  session dir: %s\n  repos dir: %s\n  owner dir: %s\n  checkout whole: %b"
                    why
                    model.Environment
                    items
                    terminals
                    (listDir nodeFs sessionDir)
                    (listDir nodeFs reposDir)
                    (listDir nodeFs (sprintf "%s/octocat" reposDir))
                    (checkoutWhole nodeFs checkout)

            do! compose ada ada.Hello.PeerId (sprintf "Clone %s" liveRepo)
            ada.Connection.SendDraft ada.Hello.PeerId

            // Settles on the checkout OR the turn ending, whichever comes first. `settledWithin`
            // rather than `waitUntilWithin` because not settling is not this case's verdict —
            // the report below is, and a throw here would take it with it.
            let settled () =
                checkoutWhole nodeFs checkout
                || (ada.Runner.Model ()).Conversation.Items
                   |> List.exists (fun i ->
                       i.Author = ActorRef.Agent
                       && (i.Status = Complete || i.Status = ConversationItemStatus.Failed))
            do! settledWithin cloneDeadlineMs settled |> Async.Ignore

            // Printed on the happy path too: a green run that says nothing teaches nothing, and
            // this is the only place a CI reader can see what the live session actually did.
            printfn "%s" (report (if checkoutWhole nodeFs checkout then "the checkout landed" else "no checkout"))
            Expect.isTrue (checkoutWhole nodeFs checkout) (report "the checkout never landed")

            do! ada.Channel.Close ()
            do! pm.StopAll ()
        }
    ]

let tests =
    testList "GitIntegration" [
        pureTests
        layoutTests
        Tag.needs "Repo verbs (srt)" [ Tag.Srt ] (fun () -> srtTests)
        Tag.needs
            "Repos the session reports"
            [ Tag.Ports; Tag.Native; Tag.Srt ]
            (fun () -> compositionTests)
        Tag.needs
            "add_repo (live model, real GitHub)"
            [ Tag.LiveAgent; Tag.Ports; Tag.Native; Tag.Srt ]
            (fun () -> liveClone)
    ]
