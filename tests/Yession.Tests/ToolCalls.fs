module Yession.Tests.ToolCalls

// A tool call, driven the way a TURN drives one, with no model in the loop.
//
// Everything between the model and the work has been testable for a while, one layer at a
// time: `Tools.fs` drives the registry over stub capabilities, `CommandGates.fs` drives the
// gate over a stub dispatch, `GitIntegration.fs` drives the repo service over local bare
// fixtures. Each of those is green while the CHAIN is broken, because the joins between them
// — the wire name, the encoded argument list, the rendered summary, and above all the wait —
// are exactly what none of them contains.
//
// This is that chain: the wire name a model would emit, through the registry the SDK adapter
// builds (`Agent.registryFor`), through the per-turn bindings a turn is given
// (`Commands.bindFor`), through the real command gate, into the real dispatch table
// (`Commands.dispatch`), and back out as the TEXT the model would read. Only the leaf — the
// service that touches git — is substituted, because what it does is somebody else's suite.
//
// What it caught on the first run is the reason it exists: an `add_repo` whose clone
// outlived a deadline meant for people answered "WAITING FOR A HUMAN TO APPROVE IT. It has
// NOT happened." — while it was happening, and while nobody had been asked anything. Every
// layer was individually right. The join was not.

open System
open Fable.Pyxpecto
open Yjs
open Yession.Domain
open Yession.Host
open Yession.SessionProcess

let private expect result =
    match result with
    | Ok v -> v
    | Error e -> failwithf "invariant: %A" e

let private sessionId = SessionId.create "sess-tool-calls" |> expect
let private ada = UserRef (UserId.create "ada" |> expect)

// --- the harness -------------------------------------------------------------------------

/// One session's tool surface, as an agent turn reaches it.
type ToolSession =
    { /// Call a tool by name with the JSON arguments a model would have written, and get
      /// back the text a model would have read. `Error` means the call never happened
      /// (no such tool, unreadable arguments) — the distinction the SDK carries as
      /// `isError`, kept here rather than flattened.
      Call : string -> string -> Async<Result<string, string>>
      /// The collaborative doc, for a test that needs to read what a peer would see.
      Doc : Y.Doc
      /// What the session recorded.
      Events : unit -> Async<SessionEvent list>
      /// Move the session's clock. The gate's deadline is measured against this, so a test
      /// crosses it without spending it, and without depending on how long anything took.
      Advance : TimeSpan -> unit }

/// Compose a session's tool surface over the services it runs against. The composition is
/// the production one — the gate the Host builds, the dispatch table and per-turn bindings
/// `SessionMain` hands it, the registry the SDK adapter assembles — with nothing standing in
/// but the clock and whatever the caller substituted at the leaves.
let openToolSession (services: Commands.CommandServices) : ToolSession =
    let doc = Y.Doc.Create ()
    let log = InMemoryEventLog.create sessionId (fun () -> DateTimeOffset.UtcNow)

    let mutable clock = DateTimeOffset (2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

    // The Host's own change seam: one signal for an appended event and a doc update alike,
    // because a waiter does not care which happened — it re-reads and decides.
    let mutable listeners : Map<int, unit -> unit> = Map.empty
    let mutable nextListener = 0
    let subscribeToChanges (listener: unit -> unit) : unit -> unit =
        let id = nextListener
        nextListener <- nextListener + 1
        listeners <- Map.add id listener listeners
        fun () -> listeners <- Map.remove id listeners
    let notifyChanged () = listeners |> Map.iter (fun _ listener -> listener ())
    DocSync.onAnyUpdate doc notifyChanged

    let mutable minted = 0
    let mint (prefix: string) () =
        minted <- minted + 1
        sprintf "%s-%d" prefix minted

    let gate =
        CommandGates.create
            Classifier.approveAll
            (fun () -> Commands.dispatch services)
            (fun actor event ->
                async {
                    let! _ = log.Append actor event
                    notifyChanged ()
                    return ()
                })
            (fun () -> QueueId.create (mint "q" ()) |> expect)
            (fun () -> MessageId.create (mint "msg" ()) |> expect)
            (fun () -> clock)
            subscribeToChanges

    // What the Host leaves as denials plus the one capability it owns here, then the
    // per-turn binding that turns the rest into commands — the same two steps, in the same
    // order, that a turn goes through.
    let capabilities =
        { AgentCapabilities.none with RunGated = gate.Run }
        |> Commands.bindFor services ada

    let registry = Agent.registryFor capabilities

    { Call =
        fun name args ->
            async {
                match! registry.Invoke { Namespace = AgentTools.Namespace; Name = name; Arguments = args } with
                | Ok answer -> return Ok answer.Text
                | Error reason -> return Error reason
            }
      Doc = doc
      Events =
        fun () ->
            async {
                let! page = log.Read None 1000
                return page.Events |> List.map (fun e -> e.Event)
            }
      Advance =
        fun span ->
            clock <- clock + span
            notifyChanged () }

/// A repo service that answers `add_repo` with whatever the test says, and refuses
/// everything else — the leaf substituted, and nothing above it.
let private reposAnswering (add: RepoRef -> Async<Result<RepoListing, string>>) : Repos.ReposService =
    let denied _ = async { return Error "not part of this test" }
    { AddRepo = fun _ repo -> add repo
      ListRepos = fun () -> async { return Ok [] }
      SwitchBranch = fun _ _ _ _ -> async { return Error "not part of this test" }
      FetchRepo = fun _ _ -> async { return Error "not part of this test" }
      RepoStatus = denied
      RepoLog = denied
      RepoDiff = denied
      RemoveRepo = fun _ _ _ -> async { return Error "not part of this test" } }

let private servicesOver (service: Repos.ReposService) : Commands.CommandServices =
    { Repos = fun () -> Some service
      Sandboxes = fun () -> WorkSandboxes.unavailable
      Terminals = fun () -> SessionTerminals.unavailable
      Invalidate = ignore }

/// A session whose `add_repo` succeeds at once.
let private cloningAt (branch: string) =
    openToolSession (
        servicesOver (
            reposAnswering (fun repo ->
                async { return Ok { Repo = repo; Branch = branch; Dirty = false; Path = "/repos/octo/hello" } })))

/// A session whose `add_repo` does not come back until the test says so — a clone in
/// progress, which is what every first `add_repo` is for its first several seconds.
let private slowlyCloning () : ToolSession * (unit -> unit) =
    let mutable finish : unit -> unit = ignore
    let cloning = Async.FromContinuations (fun (cont, _, _) -> finish <- fun () -> cont ())
    let session =
        openToolSession (
            servicesOver (
                reposAnswering (fun repo ->
                    async {
                        do! cloning
                        return Ok { Repo = repo; Branch = "main"; Dirty = false; Path = "/repos/octo/hello" }
                    })))
    session, fun () -> finish ()

let private addRepo (session: ToolSession) (repo: string) =
    session.Call "add_repo" (sprintf """{"repo":"%s"}""" repo)

let private answered (result: Result<string, string>) : string =
    match result with
    | Ok text -> text
    | Error reason -> failwithf "the call did not happen: %s" reason

let private tests' =
    testList "A tool call, end to end" [

        // The whole chain in one case: the arguments a model writes are decoded by the
        // registry, encoded by the per-turn binding, carried through the gate, decoded by the
        // dispatch table and handed to the service — and what comes back is what the model
        // reads. Every join in that sentence is a place a rename goes unnoticed.
        testCaseAsync "an ungated command runs inside the call, and answers with what it did" <|
            async {
                let session = cloningAt "main"
                let! answer = addRepo session "octo/hello"
                let text = answered answer
                Expect.stringContains text "added octo/hello" "the service's own words came back"
                Expect.isFalse (text.Contains "WAITING") "nobody was waiting on anything"
            }

        // The answer has to carry the one fact the next step needs. "added octo/hello" on
        // its own leaves the path to be guessed, and a guess is spent as a TERMINAL COMMAND
        // — `ls ~/repos`, and the next guess after that — off a turn that has a ceiling.
        testCaseAsync "the answer names the path a terminal reaches the checkout at" <|
            async {
                let session = cloningAt "main"
                let! answer = addRepo session "octo/hello"
                Expect.stringContains (answered answer) "/repos/octo/hello" "where to cd, not just what was cloned"
            }

        // A repo name the domain refuses never reaches the gate, and the model is told what
        // to fix rather than that something failed.
        testCaseAsync "an argument the domain refuses is an answer the model can act on" <|
            async {
                let session = cloningAt "main"
                let! answer = addRepo session "not a repo"
                Expect.stringContains (answered answer) "not a repo name" "it says which argument, and why"
            }

        // THE case this file was written for, surviving the manual gate it was written
        // against. A slow command must never be reported as anybody's decision: a clone is
        // seconds of WORK, and an agent told a person is being waited on goes and asks them
        // for a decision they were never offered.
        testCaseAsync "a slow command waits for the WORK, never for a person" <|
            async {
                let session, finish = slowlyCloning ()
                let! call = Async.StartChild (addRepo session "octo/hello")
                do! Async.Sleep 50
                session.Advance (TimeSpan.FromSeconds 30.0)
                do! Async.Sleep 50
                finish ()
                let! answer = call
                let text = answered answer
                Expect.stringContains text "added octo/hello" "the call carried the outcome back"
                Expect.isFalse (text.Contains "APPROVE") "nobody was ever asked to approve it"
            }

        // The yield is still there — a command that runs for minutes must not hold a turn
        // open — but it is bounded by the PROCESS deadline and says what it is: going, not
        // waiting on anybody.
        testCaseAsync "a command still going at the process deadline yields, saying it is running" <|
            async {
                let session, finish = slowlyCloning ()
                let! call = Async.StartChild (addRepo session "octo/hello")
                do! Async.Sleep 50
                session.Advance (TimeSpan.FromSeconds 600.0)
                let! answer = call
                let text = answered answer
                Expect.stringContains text "STILL RUNNING" "what is true: it is going, and it has not finished"
                Expect.stringContains text "check_pending" "with the way to pick it up"
                Expect.isFalse (text.Contains "APPROVE") "and still nobody to approve anything"
                finish ()
            }

    ]

let tests = testList "Tool calls" [ tests' ]
