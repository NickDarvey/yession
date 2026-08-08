module Yession.Tests.GitIntegration

// Plan 14: the repo manager, driven for real against LOCAL bare fixtures — deterministic,
// no network — under the srt confinement the production git sandbox uses. The pure pieces
// (the hardened invocation env, branch-name validation, output capping) run in the cheap
// tier; the [Srt] suite proves the interesting property, which is not "clone works" but
// that repo-controlled execution stays OFF: a hook and an fsmonitor planted in the
// checkout (exactly what the WorkSandbox could write) must not fire through the verbs.

open System
open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto
open Yession.Domain
open Yession.Host
open Yession.SessionProcess

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
let private caller : Repos.RepoCaller = { Actor = ActorRef.Agent; Credential = PeerRef ada }

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
            Expect.equal (expect listed) [ { Repo = repo; Branch = "main"; Dirty = false } ] "the listing is the filesystem's answer"
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
            let human : Repos.RepoCaller = { Actor = PeerRef ada; Credential = PeerRef ada }
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

let tests =
    testList "GitIntegration" [
        pureTests
        Tag.needs "Repo verbs (srt)" [ Tag.Srt ] (fun () -> srtTests)
    ]
