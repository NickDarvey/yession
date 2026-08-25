module Yession.Tests.RepoConfig

// The `yession.yaml` bridge (Plan 27): a real file on disk, through a real YAML parser, into
// the domain's decoder.
//
// The DECODER's own behaviour is pinned in `Domain.fs` from JSON literals, on both runtimes.
// What is left for this suite is only what a file can do that a JSON literal cannot — YAML
// syntax the parser has to be configured correctly to refuse or to allow, and the difference
// between a repo with no file and a repo with a broken one.

open Fable.Core
open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Agent
open Yession.Host

let private expect = function Ok v -> v | Error e -> failwithf "%A" e

[<ImportAll("node:fs")>]
let private nodeFs : obj = jsNative

[<ImportAll("node:os")>]
let private nodeOs : obj = jsNative

[<Emit("$0.mkdtempSync($1.tmpdir() + '/yession-config-')")>]
let private mkdtemp (fs: obj) (os: obj) : string = jsNative

[<Emit("$0.mkdirSync($1, { recursive: true })")>]
let private mkdirp (fs: obj) (path: string) : unit = jsNative

[<Emit("$0.readFileSync($1, 'utf8')")>]
let private readFile (fs: obj) (path: string) : string = jsNative

[<Emit("$0.writeFileSync($1, $2)")>]
let private writeFile (fs: obj) (path: string) (text: string) : unit = jsNative

let private repo (raw: string) = RepoRef.create raw |> expect

/// A repos directory holding one checkout, with `text` as its `yession.yaml` when given.
let private checkout (r: RepoRef) (text: string option) : string =
    let reposDir = mkdtemp nodeFs nodeOs
    mkdirp nodeFs (sprintf "%s/%s" reposDir (RepoRef.relativePath r))
    text |> Option.iter (writeFile nodeFs (RepoConfig.pathIn reposDir r))
    reposDir

// YAML fixtures live at module level: a triple-quoted string whose content starts at column 0
// is offside inside the list expression below.

let private devWithNet = """
version: 1
sandboxes:
  dev:
    container:
      image: node:24
    net:
      - registry.npmjs.org
    forward: [ github ]
"""

let private duplicateName = """
version: 1
sandboxes:
  dev:
    container: { image: node:24 }
  dev:
    container: { image: node:20 }
"""

let private anchored = """
version: 1
sandboxes:
  dev: &base
    net: [ registry.npmjs.org ]
  gate: *base
"""

let tests =
    testList "yession.yaml on disk (Plan 27)" [

        testCase "a real file decodes to what the repo asked for" <| fun () ->
            // The end-to-end claim: YAML on disk reaches the domain intact.
            let r = repo "octo/hello"
            let dir = checkout r (Some devWithNet)
            let file = RepoConfig.read dir r |> expect |> Option.get
            let dev = file.Sandboxes |> Map.find (SandboxName.create "dev" |> expect)
            Expect.equal (dev.Container |> Option.get).Image (Some { Name = "node"; Tag = Some "24" })
                "the image survived the round trip"
            Expect.equal dev.Net [ "registry.npmjs.org" ] "so did the egress it asks for"
            Expect.equal dev.Forward [ "github" ] "and the credential names"

        // Yession's own `yession.yaml`, decoded by the real thing. It is the acceptance test
        // for the schema: if this repo cannot say what a session working on it needs, the
        // schema is wrong — and a plausible-looking sample in a doc would never find out.
        testCase "the file this repo carries is one this build can read" <| fun () ->
            let r = repo "trinketworks/yession"
            let dir = mkdtemp nodeFs nodeOs
            mkdirp nodeFs (sprintf "%s/%s" dir (RepoRef.relativePath r))
            writeFile nodeFs (RepoConfig.pathIn dir r) (readFile nodeFs "yession.yaml")
            let file = RepoConfig.read dir r |> expect |> Option.get
            let dev = file.Sandboxes |> Map.find (SandboxName.create "dev" |> expect)
            let gate = file.Sandboxes |> Map.find (SandboxName.create "gate" |> expect)
            Expect.equal dev.Container None "this repo's work sandbox is srt, so it declares no container"
            Expect.isTrue (dev.Net |> List.contains "cache.nixos.org") "it can reach the cache devenv pulls from"
            Expect.equal dev.Forward [ "github" ] "and forwards the credential `git push` needs"
            Expect.equal gate.Net dev.Net "the anchor gave the gate the same reach"

        testCase "a repo with no file asks for nothing, and that is not an error" <| fun () ->
            // The ordinary case. Most repos will never carry one.
            let r = repo "octo/plain"
            Expect.equal (RepoConfig.read (checkout r None) r) (Ok None) "absent is absent, not broken"

        testCase "a repo with a broken file is refused, not treated as absent" <| fun () ->
            // Folding this into the case above is how a repo silently stops being configured
            // the day somebody mistypes a key.
            let r = repo "octo/broken"
            Expect.isError (RepoConfig.read (checkout r (Some "version: 1\nsandboxes:\n  dev:\n    workdirr: ./app\n")) r)
                "an unknown key fails the file rather than yielding an empty one"

        testCase "the refusal names the repo it came from" <| fun () ->
            // A session can hold several checkouts; "a config is broken" is not actionable.
            let r = repo "octo/broken"
            match RepoConfig.read (checkout r (Some "version: 99\n")) r with
            | Ok _ -> failwith "expected a refusal"
            | Error e -> Expect.isTrue (e.Contains "octo/broken") "it says whose file"

        testCase "a repeated sandbox name is refused rather than silently folded" <| fun () ->
            // This is what `uniqueKeys` buys, and it cannot be tested from a JSON literal:
            // JSON object semantics fold a duplicate to last-wins BEFORE any decoder sees it,
            // so without the option the second `dev` would quietly win.
            let r = repo "octo/hello"
            let dir = checkout r (Some duplicateName)
            Expect.isError (RepoConfig.read dir r) "one name declared twice fails the file"

        testCase "a YAML anchor is resolved before the decoder sees anything" <| fun () ->
            // Reuse inside a file costs the schema nothing — which is what lets a repo declare
            // a second sandbox on the same configuration without repeating it.
            let r = repo "octo/hello"
            let dir = checkout r (Some anchored)
            let file = RepoConfig.read dir r |> expect |> Option.get
            let gate = file.Sandboxes |> Map.find (SandboxName.create "gate" |> expect)
            Expect.equal gate.Net [ "registry.npmjs.org" ] "the alias carried the anchor's value"

        testCase "a custom tag cannot ask the parser for something the schema never agreed to" <| fun () ->
            // `schema: 'core'` is the whole mitigation, and a test that never exercises a tag
            // would not notice it being dropped.
            let r = repo "octo/hello"
            Expect.isError
                (RepoConfig.read (checkout r (Some "version: 1\nsandboxes: !!python/object:os.system {}\n")) r)
                "an unknown tag is refused"

        testCase "one repo's broken file does not cost the others their configuration" <| fun () ->
            // A session held hostage by whichever checkout happens to have a typo would be
            // worse than no file support at all.
            let good, bad = repo "octo/good", repo "octo/bad"
            let dir = mkdtemp nodeFs nodeOs
            for r in [ good; bad ] do mkdirp nodeFs (sprintf "%s/%s" dir (RepoRef.relativePath r))
            writeFile nodeFs (RepoConfig.pathIn dir good) "version: 1\nsandboxes:\n  dev: {}\n"
            writeFile nodeFs (RepoConfig.pathIn dir bad) "version: 1\nsandboxes:\n  dev:\n    nope: 1\n"
            let declared, refused = RepoConfig.readAll dir [ good; bad ]
            Expect.equal (Map.count declared) 1 "the good repo's sandbox survived"
            Expect.isTrue
                (declared |> Map.containsKey (SandboxRef.inScope good (SandboxName.create "dev" |> expect)))
                "and it is the good repo's, scoped to it"
            Expect.equal (List.length refused) 1 "the broken one is reported rather than dropped"
            Expect.equal (fst refused.[0]) bad "named, so somebody can fix it"
    ]

// --- The fold ----------------------------------------------------------------------------

/// A repos service that knows about `listed` and nothing else. Only `ListRepos` and
/// `CheckoutOf` are the fold's business; every other verb is somebody else's suite.
let private reposOver (reposDir: string) (listed: RepoRef list) : Repos.ReposService =
    let denied _ = async { return Error "not part of this test" }
    { AddRepo = fun _ _ -> denied ()
      ListRepos =
        fun () ->
            async {
                return
                    Ok (
                        listed
                        |> List.map (fun r ->
                            { Repo = r
                              Branch = "main"
                              Dirty = false
                              Path = sprintf "%s/%s" reposDir (RepoRef.relativePath r) })) }
      SwitchBranch = fun _ _ _ _ -> denied ()
      FetchRepo = fun _ _ -> denied ()
      RepoStatus = fun _ -> denied ()
      RepoLog = fun _ -> denied ()
      RepoDiff = fun _ -> denied ()
      RemoveRepo = fun _ _ _ -> denied ()
      CheckoutOf = fun r -> sprintf "%s/%s" reposDir (RepoRef.relativePath r) }

/// A gate that approves everything and records what it was asked to do, so a test can see
/// which declarations reached it and in what shape.
let private recordingGate (seen: ResizeArray<GatedCall>) : RunGatedCommand =
    fun call ->
        async {
            seen.Add call
            return Ok { Handle = None; Tool = call.Tool; Summary = call.Summary; Status = CommandRan "ok" }
        }

/// A gate that refuses everything, with a reason a row has to carry.
let private refusingGate (reason: string) : RunGatedCommand =
    fun call ->
        async {
            return
                Ok
                    { Handle = None
                      Tool = call.Tool
                      Summary = call.Summary
                      Status = CommandRefusedBy (ActorRef.System, Some reason) }
        }

let private cell (value: 'a) = fun () -> value

/// A log for the fold to read its own history from and append its notes to. Fresh per case:
/// what a fold says is a delta against what the session was already told, so a shared log
/// would make one case's notes another's silence.
let private foldLog () =
    Yession.SessionProcess.InMemoryEventLog.create
        (SessionId.create "fold" |> expect)
        (fun () -> System.DateTimeOffset.UtcNow)

let foldTests =
    testList "the yession.yaml fold (Plan 27)" [

        // The fold's whole job: a declaration becomes one of the commands the session
        // already has, through the same gate the agent's goes through.
        testCaseAsync "every declaration reaches the gate as a start_work_sandbox" <|
            async {
                let one, two = repo "octo/one", repo "octo/two"
                let dir = mkdtemp nodeFs nodeOs
                for r in [ one; two ] do mkdirp nodeFs (sprintf "%s/%s" dir (RepoRef.relativePath r))
                writeFile nodeFs (RepoConfig.pathIn dir one) "version: 1\nsandboxes:\n  dev: {}\n"
                writeFile nodeFs (RepoConfig.pathIn dir two) "version: 1\nsandboxes:\n  dev: {}\n  gate: {}\n"
                let seen = ResizeArray<GatedCall> ()
                let folded =
                    RepoSandboxes.create
                        dir
                        (cell (Some (reposOver dir [ one; two ])))
                        (cell WorkSandboxes.unavailable)
                        (recordingGate seen)
                        (foldLog ())
                do! folded.Fold None
                Expect.equal (seen |> Seq.map (fun c -> c.Tool) |> Set.ofSeq) (Set.ofList [ "start_work_sandbox" ]) "one verb, no other"
                Expect.equal (seen.Count) 3 "every declaration in every file, and nothing else"
            }

        // Attribution is the point of `ActorRef.Configured`: freshly-cloned code is less
        // trusted than the agent, so the timeline has to say which file asked.
        testCaseAsync "each ask is authored by the file that made it" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 1\nsandboxes:\n  dev: {}\n")
                let seen = ResizeArray<GatedCall> ()
                let folded =
                    RepoSandboxes.create dir (cell (Some (reposOver dir [ r ]))) (cell WorkSandboxes.unavailable) (recordingGate seen) (foldLog ())
                do! folded.Fold None
                Expect.equal (Authority.author seen.[0].Authority) (ActorRef.Configured r) "the repo's own file"
                Expect.equal (Authority.onBehalfOf seen.[0].Authority) None "and a boot fold borrows nobody's authority"
            }

        testCaseAsync "a triggered fold runs on the authority of whoever triggered it" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 1\nsandboxes:\n  dev:\n    forward: [ github ]\n")
                let seen = ResizeArray<GatedCall> ()
                let folded =
                    RepoSandboxes.create dir (cell (Some (reposOver dir [ r ]))) (cell WorkSandboxes.unavailable) (recordingGate seen) (foldLog ())
                let ada = UserRef (UserId.create "ada" |> expect)
                do! folded.Fold (Some ada)
                Expect.equal (Authority.effective seen.[0].Authority) ada "whose credential a forward: resolves against"
            }

        // The row is the fold's only surface for a declaration that did not become a
        // sandbox, so a refusal that produced no row would be a silent one.
        testCaseAsync "a refused declaration is a row saying which, and why" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 1\nsandboxes:\n  dev: {}\n")
                let folded =
                    RepoSandboxes.create
                        dir
                        (cell (Some (reposOver dir [ r ])))
                        (cell WorkSandboxes.unavailable)
                        (refusingGate "registry.npmjs.org is not in this session's egress")
                        (foldLog ())
                do! folded.Fold None
                match folded.Outcomes () with
                | [ outcome ] ->
                    Expect.equal outcome.Repo r "the repo whose file asked"
                    Expect.equal (outcome.Sandbox |> Option.map SandboxRef.render) (Some "octo/hello:dev") "the sandbox it asked for"
                    Expect.equal outcome.Problem (Some "registry.npmjs.org is not in this session's egress") "and the reason it did not get it"
                | other -> failwithf "expected one row, got %A" other
            }

        testCaseAsync "a declaration that became a sandbox is a row with no problem" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 1\nsandboxes:\n  dev: {}\n")
                let folded =
                    RepoSandboxes.create
                        dir
                        (cell (Some (reposOver dir [ r ])))
                        (cell WorkSandboxes.unavailable)
                        (recordingGate (ResizeArray<GatedCall> ()))
                        (foldLog ())
                do! folded.Fold None
                Expect.equal (folded.Outcomes () |> List.map (fun o -> o.Problem)) [ None ] "nothing to report is nothing to report"
            }

        // A file nobody can read is fixed by whoever wrote the YAML; a sandbox that would
        // not start is fixed by whoever wrote the sandbox. Different people, so the row
        // distinguishes them.
        testCaseAsync "an unreadable file is a row about the file, not about a sandbox" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 1\nsandboxes:\n  dev:\n    nope: 1\n")
                let folded =
                    RepoSandboxes.create
                        dir
                        (cell (Some (reposOver dir [ r ])))
                        (cell WorkSandboxes.unavailable)
                        (recordingGate (ResizeArray<GatedCall> ()))
                        (foldLog ())
                do! folded.Fold None
                match folded.Outcomes () with
                | [ outcome ] ->
                    Expect.equal outcome.Sandbox None "there is no sandbox to name — the file did not parse"
                    Expect.isTrue (outcome.Problem |> Option.exists (fun p -> p.Contains "nope")) "the key that broke it"
                | other -> failwithf "expected one row, got %A" other
            }

        // Re-folding is what makes the trigger cheap enough to run after every repo verb,
        // and the property that buys it is `ensure`'s, not this module's.
        testCaseAsync "a repo with no file contributes nothing and refuses nothing" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r None
                let seen = ResizeArray<GatedCall> ()
                let folded =
                    RepoSandboxes.create dir (cell (Some (reposOver dir [ r ]))) (cell WorkSandboxes.unavailable) (recordingGate seen) (foldLog ())
                do! folded.Fold None
                Expect.equal seen.Count 0 "nothing was asked for"
                Expect.equal (folded.Outcomes ()) [] "and nothing is wrong"
            }

        // The query answers "what became of every declaration" to whoever asks. This is the
        // other half: a refusal SAYS so, once, where a person is already looking — because a
        // person who has just broken their own file has no reason to suspect a question.
        testCaseAsync "a refused declaration says so on the timeline, attributed to the file" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 1\nsandboxes:\n  dev: {}\n")
                let log = foldLog ()
                let folded =
                    RepoSandboxes.create
                        dir
                        (cell (Some (reposOver dir [ r ])))
                        (cell WorkSandboxes.unavailable)
                        (refusingGate "registry.npmjs.org is not in this session's egress")
                        log
                do! folded.Fold None
                let! page = log.Read None System.Int32.MaxValue
                match page.Events |> List.choose (fun e -> match e.Event with SessionEvent.RepoConfigRefused n -> Some n | _ -> None) with
                | [ note ] ->
                    Expect.equal note.Repo r "the repo whose file asked"
                    Expect.equal (note.Sandbox |> Option.map SandboxRef.render) (Some "octo/hello:dev") "the declaration that was refused"
                    Expect.equal note.Reason "registry.npmjs.org is not in this session's egress" "said whole, in the refusal's own words"
                    Expect.equal note.Actor (ActorRef.Configured r) "the file is the party that asked, so it is the party that was refused"
                | other -> failwithf "expected one note, got %A" other
            }

        // The fold runs after every repo verb. A note per outcome would rebuild exactly the
        // accumulation the query was chosen to avoid — on the surface it was avoided for.
        testCaseAsync "the same refusal folded twice is said once" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 1\nsandboxes:\n  dev: {}\n")
                let log = foldLog ()
                let folded =
                    RepoSandboxes.create
                        dir
                        (cell (Some (reposOver dir [ r ])))
                        (cell WorkSandboxes.unavailable)
                        (refusingGate "the ceiling is closed")
                        log
                do! folded.Fold None
                do! folded.Fold None
                do! folded.Fold None
                let! page = log.Read None System.Int32.MaxValue
                let notes =
                    page.Events |> List.choose (fun e -> match e.Event with SessionEvent.RepoConfigRefused n -> Some n.Reason | _ -> None)
                Expect.equal notes [ "the ceiling is closed" ] "three folds, one thing to say"
            }

        // The suppression is on the REASON, so a refusal that moved is news. Without this
        // the note would be a cache of the first thing that ever went wrong.
        testCaseAsync "a refusal that changed is said again" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 1\nsandboxes:\n  dev: {}\n")
                let log = foldLog ()
                let mutable why = "the ceiling is closed"
                let moving : RunGatedCommand =
                    fun call ->
                        async {
                            return Ok { Handle = None; Tool = call.Tool; Summary = call.Summary
                                        Status = CommandRefusedBy (ActorRef.System, Some why) }
                        }
                let folded =
                    RepoSandboxes.create dir (cell (Some (reposOver dir [ r ]))) (cell WorkSandboxes.unavailable) moving log
                do! folded.Fold None
                why <- "no credential to forward"
                do! folded.Fold None
                let! page = log.Read None System.Int32.MaxValue
                let notes =
                    page.Events |> List.choose (fun e -> match e.Event with SessionEvent.RepoConfigRefused n -> Some n.Reason | _ -> None)
                Expect.equal notes [ "the ceiling is closed"; "no credential to forward" ] "a different reason is a different thing to say"
            }

        // A declaration that worked has always announced itself, as a WorkSandboxStarted.
        // This must not add a second voice for the same outcome.
        testCaseAsync "a declaration that became a sandbox says nothing here" <|
            async {
                let r = repo "octo/hello"
                let dir = checkout r (Some "version: 1\nsandboxes:\n  dev: {}\n")
                let log = foldLog ()
                let folded =
                    RepoSandboxes.create
                        dir
                        (cell (Some (reposOver dir [ r ])))
                        (cell WorkSandboxes.unavailable)
                        (recordingGate (ResizeArray<GatedCall> ()))
                        log
                do! folded.Fold None
                let! page = log.Read None System.Int32.MaxValue
                Expect.equal
                    (page.Events |> List.filter (fun e -> match e.Event with SessionEvent.RepoConfigRefused _ -> true | _ -> false))
                    []
                    "nothing went wrong, so nothing is said"
            }

        testCaseAsync "a session with no repos service folds nothing rather than failing" <|
            async {
                let folded =
                    RepoSandboxes.create "/nowhere" (cell None) (cell WorkSandboxes.unavailable) (recordingGate (ResizeArray<GatedCall> ())) (foldLog ())
                do! folded.Fold None
                Expect.equal (folded.Outcomes ()) [] "no repos is not a fault"
            }

    ]
