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
                [ 0 .. 2 ]
                |> List.map (fun i ->
                    Map.find (sprintf "GIT_CONFIG_KEY_%d" i) env, Map.find (sprintf "GIT_CONFIG_VALUE_%d" i) env)
            Expect.equal (Map.tryFind "GIT_CONFIG_COUNT" env) (Some "3") "three forced configs without a token"
            Expect.isTrue (configs |> List.contains ("core.hooksPath", "/dev/null")) "hooks off"
            Expect.isTrue (configs |> List.contains ("core.fsmonitor", "false")) "fsmonitor off"
            Expect.isTrue (configs |> List.contains ("protocol.ext.allow", "never")) "ext transport off"

        testCase "a token rides as one extra header config, never a bare env value" <| fun () ->
            let entries = Repos.hardenedEnv "https" (Some "gho_secret")
            let env = Map.ofList entries
            Expect.equal (Map.tryFind "GIT_CONFIG_COUNT" env) (Some "4") "one more config"
            Expect.equal
                (Map.tryFind "GIT_CONFIG_KEY_3" env)
                (Some "http.https://github.com/.extraheader")
                "scoped to github.com over https"
            let value = Map.find "GIT_CONFIG_VALUE_3" env
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

let private serviceIn (root: string) (log: EventLog<SessionEvent>) : Repos.ReposService =
    let reposDir = sprintf "%s/repos" root
    let fixtures = fixturesIn root
    mkdir nodeFs reposDir
    mkdir nodeFs fixtures
    Repos.create
        { Backend = SrtBackend
          ReposDir = reposDir
          // Host-family: a terminal reaches the checkouts at the directory itself.
          VisibleAt = Sandboxes.reposVisibleAt SrtBackend reposDir
          ExtraReadPaths = [ fixtures ]
          AllowedDomains = []
          AllowProtocol = "file"
          CloneUrl = fun ref -> sprintf "file://%s/%s.git" fixtures (RepoRef.repo ref)
          ResolveToken = fun _ -> async { return None }
          Log = log }
    |> expect

let private freshLog () =
    InMemoryEventLog.create (SessionId.create "git-suite" |> expect) (fun () -> DateTimeOffset.UtcNow)

let private ada = PeerId.create "ada" |> expect
let private caller : Repos.RepoCaller = { Actor = ActorRef.Agent; Credential = PeerRef ada; ApprovedBy = None }

let private eventsOf (log: EventLog<SessionEvent>) : Async<SessionEvent list> =
    async {
        let! page = log.Read None Int32.MaxValue
        return page.Events |> List.map (fun envelope -> envelope.Event)
    }

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
            Expect.isTrue (exists nodeFs (sprintf "%s/repos/octo/hello/.git" root)) "checkout landed at owner/repo"
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
                [ { Repo = repo; Branch = "main"; Dirty = false; Path = sprintf "%s/repos/octo/hello" root } ]
                "the listing is the filesystem's answer, and it says where"
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
                (exists nodeFs (sprintf "%s/repos/octo/hello/.git/hooks" root))
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
            let checkout = sprintf "%s/repos/octo/hello" root
            let marker = sprintf "%s/repos/PWNED" root
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

        testCaseAsync "remove deletes the checkout and records who asked" <| async {
            let root = mkdtemp nodeFs nodeOs
            makeBareFixture root "hello" |> ignore
            let log = freshLog ()
            let service = serviceIn root log
            let repo = RepoRef.create "octo/hello" |> expect
            let! _ = service.AddRepo caller repo
            let human : Repos.RepoCaller = { Actor = PeerRef ada; Credential = PeerRef ada; ApprovedBy = None }
            let! removed = service.RemoveRepo human repo
            expect removed
            Expect.isFalse (exists nodeFs (sprintf "%s/repos/octo/hello" root)) "checkout gone"
            let! events = eventsOf log
            match events |> List.rev |> List.head with
            | SessionEvent.RepoRemoved r -> Expect.equal r.Actor (PeerRef ada) "the human is the acting party"
            | other -> failwithf "expected RepoRemoved last, got %A" other
            let! refetch = service.FetchRepo caller repo
            Expect.isError refetch "a removed repo is legibly not here"
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

[<Emit("process.execPath")>]
let private nodeExecutable : string = jsNative

[<Emit("(() => { try { return $0.readdirSync($1).join(', ') || '<empty>' } catch (e) { return '<' + (e.code || e.message) + '>' } })()")>]
let private listDir (fs: obj) (path: string) : string = jsNative

let private liveRepo = "octocat/Hello-World"

/// Ten times a passing run, and well inside the Node suite's shared 240s budget — so a
/// failure here is this case's report, never the runner killing every suite at once.
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
            let reposDir = sprintf "%s/repos" sessionDir
            let checkout = sprintf "%s/%s/.git" reposDir liveRepo

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
                    "CLONE: %s\n  environment: %A\n  conversation:\n%s\n  terminal blocks:\n%s\n  session dir: %s\n  repos dir: %s\n  owner dir: %s\n  checkout present: %b"
                    why
                    model.Environment
                    items
                    terminals
                    (listDir nodeFs sessionDir)
                    (listDir nodeFs reposDir)
                    (listDir nodeFs (sprintf "%s/octocat" reposDir))
                    (exists nodeFs checkout)

            do! compose ada ada.Hello.PeerId (sprintf "Clone %s" liveRepo)
            ada.Connection.SendDraft ada.Hello.PeerId

            // Settles on the checkout OR the turn ending, whichever comes first — then prints.
            let settled () =
                exists nodeFs checkout
                || (ada.Runner.Model ()).Conversation.Items
                   |> List.exists (fun i ->
                       i.Author = ActorRef.Agent
                       && (i.Status = Complete || i.Status = ConversationItemStatus.Failed))
            let rec waitFor (remaining: int) =
                async {
                    if settled () || remaining <= 0 then return ()
                    else
                        do! Async.Sleep 250
                        return! waitFor (remaining - 1)
                }
            do! waitFor (cloneDeadlineMs / 250)

            // Printed on the happy path too: a green run that says nothing teaches nothing, and
            // this is the only place a CI reader can see what the live session actually did.
            printfn "%s" (report (if exists nodeFs checkout then "the checkout landed" else "no checkout"))
            Expect.isTrue (exists nodeFs checkout) (report "the checkout never landed")

            do! ada.Channel.Close ()
            do! pm.StopAll ()
        }
    ]

let tests =
    testList "GitIntegration" [
        pureTests
        Tag.needs "Repo verbs (srt)" [ Tag.Srt ] (fun () -> srtTests)
        Tag.needs
            "add_repo (live model, real GitHub)"
            [ Tag.LiveAgent; Tag.Ports; Tag.Native; Tag.Srt ]
            (fun () -> liveClone)
    ]
