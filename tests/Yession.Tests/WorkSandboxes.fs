module Yession.Tests.WorkSandboxes

// The session's named WorkSandboxes (Plan 15, stage 2). What is worth pinning is the
// ENSURE contract and the credential-handling rules, because those are what the
// declarative form and the shared trust boundary respectively rest on:
//
//   * the same ask twice is the same sandbox, and records nothing — otherwise folding a
//     file into these commands at every boot accumulates instead of converging;
//   * a DIFFERENT ask is refused rather than silently recreated, because recreating kills
//     whatever is running inside;
//   * a forwarded credential reaches the sandbox's environment and NOTHING else — the
//     event carries the names and whose, and cannot carry a value.

open System
open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Chat
open Yession.Host
open Yession.SessionProcess

let private expect result =
    match result with
    | Ok v -> v
    | Error e -> failwithf "invariant: %A" e

let private sessionId = SessionId.create "sess-sandboxes" |> expect
let private ada = UserRef (UserId.create "ada" |> expect)
let private fixedClock () = DateTimeOffset (2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
let private newLog () : EventLog<SessionEvent> = InMemoryEventLog.create sessionId fixedClock

let private sandbox (raw: string) = SandboxRef.parse raw |> expect

/// The ask most of these cases make: nothing in particular about the sandbox, some
/// credentials forwarded into it. The spec half has its own cases below.
let private forwarding (names: string list) : SandboxRequest =
    { SandboxRequest.defaults with Forward = names }

/// An ask that differs from the default in the SPEC rather than the forwarding — the half
/// only a declared sandbox has ever been able to name.
let private workingIn (dir: string) : SandboxRequest =
    { SandboxRequest.defaults with
        Spec = { EnvironmentSpec.defaults with WorkingDirectory = Some dir } }

/// A stand-in environment that records nothing but whether it is up. The registry's
/// contract is about WHICH environments exist and what they were built with, so a real
/// sandbox would only add a process to the test.
let private fakeEnvironment () =
    let mutable running = false
    ({ Ensure = fun _ _ -> async { running <- true; return EnvironmentAvailable }
       Spawn = fun _ _ -> async { return Error "not under test" }
       SpawnPty = fun _ _ _ _ -> async { return Error "not under test" }
       Stop = fun () -> async { running <- false }
       CurrentRef = fun () -> if running then Some "fake" else None }
     : SessionEnvironment.SessionEnvironment)

/// A registry over fake environments, plus the record of what each was BUILT with — which
/// is where a forwarded credential would have to appear, and the only place it may.
let private registryWithSpecs (log: EventLog<SessionEvent>) (credentials: WorkSandboxes.CredentialSource list) =
    let built = ResizeArray<string * Map<string, string>> ()
    let specs = ResizeArray<string * string option> ()
    let sandboxes =
        WorkSandboxes.create
            { Backend = "fake"
              Credentials = credentials
              Create =
                fun name spec env ->
                    built.Add (SandboxRef.render name, env)
                    specs.Add (SandboxRef.render name, spec.WorkingDirectory)
                    Ok (fakeEnvironment ())
              Log = log
              Clock = fixedClock }
        |> expect
    sandboxes, built, specs

/// The same, for the cases that only care about the environment a sandbox was built with.
let private registry (log: EventLog<SessionEvent>) (credentials: WorkSandboxes.CredentialSource list) =
    let sandboxes, built, _ = registryWithSpecs log credentials
    sandboxes, built

let private caller : WorkSandboxes.SandboxCaller = { Actor = ActorRef.Agent; Credential = ada }

let private githubCredential (value: string option) : WorkSandboxes.CredentialSource =
    { Name = "github"; EnvVar = "GITHUB_TOKEN"; Resolve = fun _ -> async { return value } }

let private eventsOf (log: EventLog<SessionEvent>) =
    async {
        let! page = log.Read None 1000
        return page.Events |> List.map (fun e -> e.Event)
    }

let private startedEvents (events: SessionEvent list) =
    events |> List.choose (function SessionEvent.WorkSandboxStarted s -> Some s | _ -> None)

// --- names --------------------------------------------------------------------------------

let private nameTests =
    testList "a sandbox name" [
        testCase "accepts what a container name, a directory and a label all survive" <| fun () ->
            for raw in [ "default"; "test"; "build-2"; "a_b"; "9" ] do
                Expect.isTrue (Result.isOk (SandboxName.create raw)) (sprintf "'%s' is a name" raw)
            // Leading punctuation, uppercase, spaces and separators are refused because a
            // name reaches a docker object name and a filesystem path, and escaping at
            // three call sites is three chances to forget.
            for raw in [ ""; "-test"; "_test"; "Test"; "my sandbox"; "a/b"; "a.b"; "../x" ] do
                Expect.isTrue (Result.isError (SandboxName.create raw)) (sprintf "'%s' is not a name" raw)

        testCase "the default is the one every session has always had" <| fun () ->
            Expect.equal (SandboxRef.render SandboxRef.defaultRef) "default" "named, not implicit"
    ]

let private normaliseTests =
    testList "forwarding lists" [
        // Two asks that MEAN the same thing must compare equal, or the second is refused
        // as a configuration change for a difference nobody made.
        testCase "normalise so an equivalent ask is an equal ask" <| fun () ->
            Expect.equal
                (WorkSandboxes.normaliseForward [ " GitHub "; "github"; ""; "aws" ])
                [ "aws"; "github" ]
                "trimmed, lowercased, deduped, sorted"
            Expect.equal (WorkSandboxes.normaliseForward []) [] "nothing stays nothing"
    ]

// --- the ensure contract --------------------------------------------------------------------

let private ensureTests =
    testList "ensure semantics" [

        // THE property the declarative form rests on. Ask twice, get the same sandbox, and
        // the timeline does not claim anything happened the second time.
        testCaseAsync "the same ask twice is the same sandbox, recorded once" <|
            async {
                let log = newLog ()
                let sandboxes, built = registry log []
                let! first = sandboxes.Ensure caller (sandbox "test") (forwarding [])
                let! second = sandboxes.Ensure caller (sandbox "test") (forwarding [])
                let one = expect first
                let two = expect second
                Expect.equal one.Ref two.Ref "the same sandbox comes back"
                Expect.equal
                    (built |> Seq.filter (fun (name, _) -> name = "test") |> Seq.length)
                    1
                    "it was built once"
                let! events = eventsOf log
                Expect.equal (List.length (startedEvents events)) 1 "the second ask recorded nothing"
            }

        // Equivalent-but-differently-spelled forwarding is the SAME ask, which is what
        // normalisation is for; this pins that the registry uses it.
        testCaseAsync "an equivalent forwarding list is the same ask" <|
            async {
                let log = newLog ()
                let sandboxes, _ = registry log [ githubCredential (Some "tok") ]
                let! _ = sandboxes.Ensure caller (sandbox "test") (forwarding [ "github" ])
                let! again = sandboxes.Ensure caller (sandbox "test") (forwarding [ " GitHub "; "github" ])
                Expect.isTrue (Result.isOk again) "it is not a configuration change"
                let! events = eventsOf log
                Expect.equal (List.length (startedEvents events)) 1 "still recorded once"
            }

        // The refusal, and why it is a refusal: a sandbox has processes in it. Converging
        // by killing somebody's build is not convergence.
        testCaseAsync "a different forwarding is refused, naming both sides" <|
            async {
                let log = newLog ()
                let sandboxes, built = registry log [ githubCredential (Some "tok") ]
                let! _ = sandboxes.Ensure caller (sandbox "test") (forwarding [])
                match! sandboxes.Ensure caller (sandbox "test") (forwarding [ "github" ]) with
                | Ok _ -> failwith "expected a refusal"
                | Error e ->
                    Expect.isTrue (e.Contains "github") "it names what was asked for"
                    Expect.isTrue (e.Contains "nothing") "and what is running"
                    Expect.isTrue (e.Contains "stop_work_sandbox") "and how to actually change it"
                Expect.equal
                    (built |> Seq.filter (fun (name, _) -> name = "test") |> Seq.length)
                    1
                    "nothing was recreated behind the refusal"
            }

        // The forwarding was only ever the FIRST field of an ask. Once a repo can declare
        // what a sandbox is, "the same ask" has to mean the same everywhere, or a file that
        // changed its workdir would be silently answered with the old sandbox.
        testCaseAsync "a different spec is refused too, naming which part differs" <|
            async {
                let log = newLog ()
                let sandboxes, built = registry log []
                let! _ = sandboxes.Ensure caller (sandbox "test") (workingIn "/a")
                match! sandboxes.Ensure caller (sandbox "test") (workingIn "/b") with
                | Ok _ -> failwith "expected a refusal"
                | Error e ->
                    Expect.isTrue (e.Contains "/a") "it names what is running"
                    Expect.isTrue (e.Contains "/b") "and what was asked for"
                    Expect.isTrue (e.Contains "stop_work_sandbox") "and how to actually change it"
                Expect.equal
                    (built |> Seq.filter (fun (name, _) -> name = "test") |> Seq.length)
                    1
                    "nothing was recreated behind the refusal"
            }

        // The seam a declared sandbox rides: what was asked for has to reach the thing that
        // BUILDS it, or the file would be read, recorded, refused against — and ignored.
        testCaseAsync "the spec that was asked for is what the sandbox is built from" <|
            async {
                let log = newLog ()
                let sandboxes, _, specs = registryWithSpecs log []
                let! _ = sandboxes.Ensure caller (sandbox "test") (workingIn "/somewhere")
                Expect.equal
                    (specs |> Seq.filter (fun (name, _) -> name = "test") |> Seq.map snd |> List.ofSeq)
                    [ Some "/somewhere" ]
                    "the working directory reached the factory"
            }

        // Stop-then-start is the documented way to change forwarding, so it has to work.
        testCaseAsync "stopping releases the name, and the next start may differ" <|
            async {
                let log = newLog ()
                let sandboxes, _ = registry log [ githubCredential (Some "tok") ]
                let! _ = sandboxes.Ensure caller (sandbox "test") (forwarding [])
                let! stopped = sandboxes.Stop caller (sandbox "test")
                Expect.isTrue (Result.isOk stopped) "it stops"
                let! restarted = sandboxes.Ensure caller (sandbox "test") (forwarding [ "github" ])
                Expect.equal (expect restarted).Request.Forward [ "github" ] "the new configuration takes"
                let! events = eventsOf log
                Expect.equal (List.length (startedEvents events)) 2 "both starts are recorded"
            }

        // `default` is the sandbox a terminal that names nothing lands in, so it must
        // survive being stopped as a NAME even though its environment goes down.
        testCaseAsync "stopping default keeps the name reachable" <|
            async {
                let log = newLog ()
                let sandboxes, _ = registry log []
                let! _ = sandboxes.Ensure caller SandboxRef.defaultRef (forwarding [])
                let! _ = sandboxes.Stop caller SandboxRef.defaultRef
                match! (sandboxes.EnvironmentFor SandboxRef.defaultRef).Ensure None "a terminal was opened" with
                | EnvironmentAvailable -> ()
                | EnvironmentUnavailable reason -> failwithf "default should still be reachable: %s" reason
            }

        testCaseAsync "stopping a sandbox the session does not have is an error, not a no-op" <|
            async {
                let log = newLog ()
                let sandboxes, _ = registry log []
                match! sandboxes.Stop caller (sandbox "nope") with
                | Ok () -> failwith "expected an error"
                | Error e -> Expect.isTrue (e.Contains "nope") "it names what was asked for"
            }

        // A terminal has to be told no in the same shape it is told anything else, so an
        // unknown name resolves to an environment that refuses with the reason rather than
        // to a null the caller has to handle differently.
        testCaseAsync "an unknown name resolves to an environment that refuses with the reason" <|
            async {
                let log = newLog ()
                let sandboxes, _ = registry log []
                match! (sandboxes.EnvironmentFor (sandbox "ghost")).Ensure None "a terminal was opened" with
                | EnvironmentAvailable -> failwith "expected a refusal"
                | EnvironmentUnavailable reason ->
                    Expect.isTrue (reason.Contains "ghost") "it names the sandbox"
                    Expect.isTrue (reason.Contains "start_work_sandbox") "and how to get one"
            }
    ]

// --- credentials ----------------------------------------------------------------------------

let private credentialTests =
    testList "named credential forwarding" [

        // The rule the shared trust boundary rests on: the VALUE goes into the sandbox's
        // environment and nowhere else; the EVENT carries the names and whose.
        testCaseAsync "the value reaches the sandbox env, and the event carries names only" <|
            async {
                let log = newLog ()
                let sandboxes, built = registry log [ githubCredential (Some "ghp_secret") ]
                let! started = sandboxes.Ensure caller (sandbox "test") (forwarding [ "github" ])
                Expect.equal (expect started).Request.Forward [ "github" ] "it forwards what was asked"

                let _, env = built |> Seq.find (fun (name, _) -> name = "test")
                Expect.equal (Map.tryFind "GITHUB_TOKEN" env) (Some "ghp_secret") "the value is in the sandbox env"

                let! events = eventsOf log
                match startedEvents events with
                | [ e ] ->
                    Expect.equal e.Forwarded [ "github" ] "the event names the credential"
                    Expect.equal e.CredentialOwner (Some ada) "and whose it is — the turn human's, not the agent's"
                    Expect.equal e.Actor ActorRef.Agent "while the acting party is the agent"
                | other -> failwithf "expected one start, got %A" other

                // The load-bearing negative: nothing anywhere in the log is the token.
                // The event TYPE cannot carry one, and this is what keeps it that way.
                let rendered = events |> List.map (sprintf "%A") |> String.concat "\n"
                Expect.isFalse (rendered.Contains "ghp_secret") "no rendering of the log contains the value"
            }

        testCaseAsync "nothing forwarded means nobody's credentials are named" <|
            async {
                let log = newLog ()
                let sandboxes, _ = registry log [ githubCredential (Some "ghp_secret") ]
                let! _ = sandboxes.Ensure caller (sandbox "test") (forwarding [])
                let! events = eventsOf log
                match startedEvents events with
                | [ e ] ->
                    Expect.equal e.Forwarded [] "nothing forwarded"
                    Expect.equal e.CredentialOwner None "so there is no credential owner to name"
                | other -> failwithf "expected one start, got %A" other
            }

        // A sandbox asked to forward `github` that quietly came up without it is a sandbox
        // whose `git push` fails much later, somewhere far less informative.
        testCaseAsync "a credential the caller does not have refuses the start" <|
            async {
                let log = newLog ()
                let sandboxes, built = registry log [ githubCredential None ]
                match! sandboxes.Ensure caller (sandbox "test") (forwarding [ "github" ]) with
                | Ok _ -> failwith "expected a refusal"
                | Error e ->
                    Expect.isTrue (e.Contains "github") "it names the credential"
                    Expect.isTrue (e.Contains "sign in") "and how to get one"
                Expect.isFalse (built |> Seq.exists (fun (name, _) -> name = "test")) "nothing was built"
                let! events = eventsOf log
                Expect.equal (startedEvents events) [] "and nothing was recorded"
            }

        testCaseAsync "a credential this session does not know is refused, naming the ones it does" <|
            async {
                let log = newLog ()
                let sandboxes, _ = registry log [ githubCredential (Some "tok") ]
                match! sandboxes.Ensure caller (sandbox "test") (forwarding [ "gitlab" ]) with
                | Ok _ -> failwith "expected a refusal"
                | Error e ->
                    Expect.isTrue (e.Contains "gitlab") "it names what was asked for"
                    Expect.isTrue (e.Contains "github") "and what is available"
            }
    ]

// --- the query -------------------------------------------------------------------------------

let private queryTests =
    testList "the work_sandboxes query" [

        // Read from the RUNNING sandbox, not from what the registry was told — the rule
        // the repos listing follows, for the same reason.
        testCaseAsync "reports process truth, and the forwarding by name" <|
            async {
                let log = newLog ()
                let sandboxes, _ = registry log [ githubCredential (Some "ghp_secret") ]
                let registration = WorkSandboxes.query (fun () -> sandboxes)

                match! registration.Read () with
                | Error e -> failwithf "the query failed: %s" e
                | Ok (RowsOf [ row ]) ->
                    Expect.equal (row |> List.tryFind (fst >> (=) "name") |> Option.map snd) (Some (CellText "default")) "default is there from boot"
                    Expect.equal (row |> List.tryFind (fst >> (=) "state") |> Option.map snd) (Some (CellText "not started")) "and it has not started"
                | Ok other -> failwithf "expected one row, got %A" other

                let! _ = sandboxes.Ensure caller (sandbox "test") (forwarding [ "github" ])
                match! registration.Read () with
                | Error e -> failwithf "the query failed: %s" e
                | Ok (RowsOf rows) ->
                    Expect.equal (List.length rows) 2 "both sandboxes are listed"
                    let test = rows |> List.find (fun row -> row |> List.contains ("name", CellText "test"))
                    Expect.equal
                        (test |> List.tryFind (fst >> (=) "forwarding") |> Option.map snd)
                        (Some (CellText "github"))
                        "the forwarding is named"
                    let rendered = sprintf "%A" rows
                    Expect.isFalse (rendered.Contains "ghp_secret") "and the value is not in the answer"
                | Ok other -> failwithf "expected rows, got %A" other
            }

        // The listing is where a person finds out that a sandbox in their session was asked
        // for by a repository rather than by them — which is the whole reason to look at
        // what it runs.
        testCaseAsync "a repo's sandbox says which repo declared it, and the session's says nothing" <|
            async {
                let log = newLog ()
                let sandboxes, _ = registry log []
                let registration = WorkSandboxes.query (fun () -> sandboxes)
                let declared =
                    SandboxRef.inScope (RepoRef.create "octo/hello" |> expect) (SandboxName.create "dev" |> expect)
                let! _ = sandboxes.Ensure caller declared SandboxRequest.defaults
                match! registration.Read () with
                | Error e -> failwithf "the query failed: %s" e
                | Ok (RowsOf rows) ->
                    let cell name row = row |> List.tryFind (fst >> (=) name) |> Option.map snd
                    let repos =
                        rows |> List.find (fun row -> cell "name" row = Some (CellText "octo/hello:dev"))
                    let own = rows |> List.find (fun row -> cell "name" row = Some (CellText "default"))
                    Expect.equal (cell "declared_by" repos) (Some (CellText "octo/hello")) "the repo that asked"
                    Expect.equal (cell "declared_by" own) (Some CellAbsent) "the session's own was asked for by nobody"
                | Ok other -> failwithf "expected rows, got %A" other
            }

        testCaseAsync "the answer fits the shape the query declares" <|
            async {
                let log = newLog ()
                let sandboxes, _ = registry log []
                let registration = WorkSandboxes.query (fun () -> sandboxes)
                match! registration.Read () with
                | Error e -> failwithf "the query failed: %s" e
                | Ok value ->
                    Expect.isTrue (QueryValue.fits registration.Def.Shape value) "the registry would accept it"
            }
    ]

// --- the timeline ------------------------------------------------------------------------------

let private timelineTests =
    testList "sandbox act-lines" [

        // The person whose credential was forwarded finds out HERE. So the line has to say
        // what was forwarded and whose, and it must never say what the value was.
        testCase "a forwarding start reads as a sentence naming the credential and its owner" <| fun () ->
            let messageId = MessageId.create "msg-1" |> expect
            let envelope : EventEnvelope<SessionEvent> =
                { EventId = EventId.fresh ()
                  SessionId = sessionId
                  Offset = EventOffset.create 1L |> expect
                  Actor = ActorRef.Agent
                  Timestamp = fixedClock ()
                  Event =
                    SessionEvent.WorkSandboxStarted
                        { MessageId = messageId
                          Sandbox = sandbox "test"
                          Backend = "srt"
                          Forwarded = [ "github" ]
                          CredentialOwner = Some ada
                          Actor = ActorRef.Agent } }
            let proj, _ = ConversationProjection.applyEvents None [ envelope ] ConversationProjection.empty
            match proj.Items with
            | [ item ] ->
                Expect.equal item.Body "started sandbox test (srt), forwarding github from user:ada" "it reads as a sentence"
                Expect.equal item.Kind ConversationItemKind.ActNote "and it is an act, not a message"
                Expect.equal item.Author ActorRef.Agent "attributed to whoever acted"
            | other -> failwithf "expected one note, got %A" other

        testCase "a start with nothing forwarded says nothing about credentials" <| fun () ->
            let envelope : EventEnvelope<SessionEvent> =
                { EventId = EventId.fresh ()
                  SessionId = sessionId
                  Offset = EventOffset.create 2L |> expect
                  Actor = ada
                  Timestamp = fixedClock ()
                  Event =
                    SessionEvent.WorkSandboxStarted
                        { MessageId = MessageId.create "msg-2" |> expect
                          Sandbox = sandbox "test"
                          Backend = "host"
                          Forwarded = []
                          CredentialOwner = None
                          Actor = ada } }
            let proj, _ = ConversationProjection.applyEvents None [ envelope ] ConversationProjection.empty
            match proj.Items with
            | [ item ] -> Expect.equal item.Body "started sandbox test (host)" "no forwarding clause"
            | other -> failwithf "expected one note, got %A" other
    ]

let tests =
    testList "WorkSandboxes" [
        nameTests
        normaliseTests
        ensureTests
        credentialTests
        queryTests
        timelineTests
    ]
