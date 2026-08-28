module Yession.Tests.Phase4

// Phase 4 verification, step by step.
//
// - Step 22: Manager state behind an explicit codec — the registry survives a Manager
//   restart via an atomically-written JSON file; unknown fields decode tolerantly
//   (the SQLite-migration posture); corruption fails loudly, never a silent reset.

open System
open Fable.Core
open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Link
open Yession.Domain.Terminals
open Yession.Domain.Tools
open Yession.Domain.Access
open Yession.Domain.Chat
open Yession.Manager
open Yession.Oidc
open Yession.App
open Yession.Host
open Yession.Tests.Support

[<ImportAll("node:fs")>]
let private nodeFs : obj = Fable.Core.Util.jsNative

[<Emit("$0.existsSync($1)")>]
let private existsSync (fs: obj) (path: string) : bool = Fable.Core.Util.jsNative

[<Emit("$0.writeFileSync($1, $2)")>]
let private writeFileSync (fs: obj) (path: string) (text: string) : unit = Fable.Core.Util.jsNative

let private statePath (name: string) =
    sprintf "tests/Yession.Tests/out/.data/%s-%d.manager.json" name (int (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) % 1000000)

let private record (id: string) (name: string) : SessionRecord =
    { SessionId = SessionId.create id |> expect
      DisplayName = name
      CreatedAt = DateTimeOffset (2026, 7, 15, 12, 0, 0, TimeSpan.Zero)
      DataDir = sprintf "sessions/%s" id
      ArchivedAt = None }

/// The moment an operator archived something, where a test needs one to be a value rather
/// than a fact about now.
let private archivedAt = DateTimeOffset (2026, 8, 1, 9, 30, 0, TimeSpan.Zero)

let private sessionA = SessionId.create "alpha" |> expect
let private sessionB = SessionId.create "beta" |> expect

let private serverRef (name: string) (url: string) (description: string) : McpServerRef =
    { Name = McpServerName.create name |> expect
      Transport = McpHttp url
      Description = description }

let private serialServer = serverRef "serial" "http://127.0.0.1:7333" "USB serial ports on this host"
let private printerServer = serverRef "printer" "http://127.0.0.1:7401" ""

let private twoSessions : ManagerState =
    { Version = ManagerState.currentVersion
      Sessions = [ record "alpha" "Alpha work"; record "beta" "Beta work" ]
      McpServers = [] }

let private stateTests =
    testList "Manager state & codec (Step 22)" [
        testCase "the state round-trips through the codec" <| fun () ->
            let decoded = ManagerCodec.toString twoSessions |> ManagerCodec.fromString |> expect
            Expect.equal decoded twoSessions "decode∘encode is the identity"

        testCase "unknown fields decode tolerantly (a newer schema's file still loads)" <| fun () ->
            // `token` here is a REMOVED field (the pre-OIDC shared session token): the
            // same tolerance that accepts future fields also lets an old file load.
            let withExtras =
                """{"version":1,"futureField":true,"sessions":[{"sessionId":"alpha","displayName":"Alpha work","token":"alpha-token","createdAt":"2026-07-15T12:00:00.0000000+00:00","dataDir":"sessions/alpha","colour":"teal"}]}"""
            let decoded = ManagerCodec.fromString withExtras |> expect
            Expect.equal
                decoded
                { Version = 1; Sessions = [ record "alpha" "Alpha work" ]; McpServers = [] }
                "known fields decode; unknown fields are ignored"

        testCase "adding a duplicate session id is rejected" <| fun () ->
            match ManagerState.addSession (record "alpha" "Again") twoSessions with
            | Error reason -> Expect.isTrue (reason.Contains "alpha") "named in the rejection"
            | Ok _ -> failwith "duplicate session ids must be rejected"

        testCase "setDisplayName renames the reported title in place, leaving others untouched" <| fun () ->
            let alpha = SessionId.create "alpha" |> expect
            let renamed = ManagerState.setDisplayName alpha "Launch plan" twoSessions
            Expect.equal
                (ManagerState.tryFind alpha renamed |> Option.map (fun s -> s.DisplayName))
                (Some "Launch plan")
                "alpha's display name is the reported title"
            Expect.equal
                (ManagerState.tryFind (SessionId.create "beta" |> expect) renamed |> Option.map (fun s -> s.DisplayName))
                (Some "Beta work")
                "beta is untouched"

        testCase "setDisplayName on an unknown session is a no-op" <| fun () ->
            let unchanged = ManagerState.setDisplayName (SessionId.create "ghost" |> expect) "Nope" twoSessions
            Expect.equal unchanged twoSessions "an unregistered session leaves the state unchanged"

        // Plan 17. The declarations are durable state like the registry, and the state file
        // is the ONLY way they touch storage — so the round trip is the contract.
        testCase "MCP declarations round-trip through the state codec" <| fun () ->
            let alpha = SessionId.create "alpha" |> expect
            let declared =
                twoSessions
                |> ManagerState.declareMcpServer
                    { Server =
                        { Name = McpServerName.create "printer" |> expect
                          Transport = McpHttp "http://127.0.0.1:7401"
                          Description = "the label printer" }
                      Audience = AnySession }
                |> expect
                |> ManagerState.declareMcpServer
                    { Server =
                        { Name = McpServerName.create "serial" |> expect
                          Transport = McpHttp "http://127.0.0.1:7333"
                          Description = "" }
                      Audience = OneSession alpha }
                |> expect
            let decoded = ManagerCodec.toString declared |> ManagerCodec.fromString |> expect
            Expect.equal decoded declared "decode∘encode is the identity, audiences and all"

        testCase "a state file written before Plan 17 loads with no declarations" <| fun () ->
            let old =
                """{"version":1,"sessions":[{"sessionId":"alpha","displayName":"Alpha work","createdAt":"2026-07-15T12:00:00.0000000+00:00","dataDir":"sessions/alpha"}]}"""
            let decoded = ManagerCodec.fromString old |> expect
            Expect.isEmpty decoded.McpServers "no declarations is the ordinary starting state, not a migration"

        testCase "a session resolves the servers that name it, and only those" <| fun () ->
            let alpha = SessionId.create "alpha" |> expect
            let beta = SessionId.create "beta" |> expect
            let declared =
                twoSessions
                |> ManagerState.declareMcpServer
                    { Server = serverRef "printer" "http://127.0.0.1:7401" ""; Audience = AnySession }
                |> expect
                |> ManagerState.declareMcpServer
                    { Server = serverRef "serial" "http://127.0.0.1:7333" ""; Audience = OneSession alpha }
                |> expect
            let namesFor id =
                (ManagerState.mcpServersFor id declared).Servers
                |> List.map (fun s -> McpServerName.value s.Name)
            Expect.equal (namesFor alpha) [ "printer"; "serial" ] "alpha was named by both"
            Expect.equal (namesFor beta) [ "printer" ] "beta only by the host-wide one"

        testCase "declaring a name a session would see twice is refused" <| fun () ->
            let alpha = SessionId.create "alpha" |> expect
            let declared =
                twoSessions
                |> ManagerState.declareMcpServer
                    { Server = serverRef "serial" "http://127.0.0.1:7333" ""; Audience = AnySession }
                |> expect
            match
                ManagerState.declareMcpServer
                    { Server = serverRef "serial" "http://127.0.0.1:9999" ""; Audience = OneSession alpha }
                    declared
            with
            | Ok _ -> failwith "a session was given two servers with one name"
            | Error e -> Expect.stringContains e "serial" "the refusal names the clash"

        testCase "withdrawing removes exactly one declaration" <| fun () ->
            let alpha = SessionId.create "alpha" |> expect
            let declared =
                twoSessions
                |> ManagerState.declareMcpServer
                    { Server = serverRef "printer" "http://127.0.0.1:7401" ""; Audience = AnySession }
                |> expect
                |> ManagerState.declareMcpServer
                    { Server = serverRef "serial" "http://127.0.0.1:7333" ""; Audience = OneSession alpha }
                |> expect
            let left =
                ManagerState.withdrawMcpServer (McpServerName.create "serial" |> expect) (OneSession alpha) declared
            Expect.equal
                ((ManagerState.mcpServersFor alpha left).Servers |> List.map (fun s -> McpServerName.value s.Name))
                [ "printer" ]
                "only the withdrawn one is gone"

        testCase "a missing state file is the empty state; the registry survives a restart" <| fun () ->
            let path = statePath "restart"
            Expect.equal (ManagerStore.load path) ManagerState.empty "first life starts empty"
            ManagerStore.save path twoSessions
            // Second life: a fresh load sees exactly what was saved.
            Expect.equal (ManagerStore.load path) twoSessions "the registry survives the restart"
            Expect.isFalse (existsSync nodeFs (path + ".tmp")) "the atomic-write temp file never lingers"
            // Saves replace the whole state — no accumulation, no merge surprises.
            let shrunk = { twoSessions with Sessions = [ record "alpha" "Alpha work" ] }
            ManagerStore.save path shrunk
            Expect.equal (ManagerStore.load path) shrunk "a save fully replaces the persisted state"

        testCase "a corrupt state file fails loudly, never a silent reset" <| fun () ->
            let path = statePath "corrupt"
            writeFileSync nodeFs path """{"version": 1, "sessions": [{"broken": tru"""
            let mutable failedLoudly = false
            try
                ManagerStore.load path |> ignore
            with _ -> failedLoudly <- true
            Expect.isTrue failedLoudly "corruption must not load as empty state"
    ]

// -----------------------------------------------------------------------------
// Archiving. A durable, reversible operator decision that retires a session from the
// working list AND from the set of things that can run. Manager-only: nothing here
// crosses the control channel, and nothing is deleted.
// -----------------------------------------------------------------------------

let private archiveTests =
    testList "Archiving a session" [
        testCase "archiving records when it happened" <| fun () ->
            let archived = ManagerState.archive sessionA archivedAt twoSessions |> expect
            Expect.equal
                (ManagerState.tryFind sessionA archived |> Option.bind (fun r -> r.ArchivedAt))
                (Some archivedAt)
                "the record carries the moment it was archived"

        testCase "archiving leaves every other session alone" <| fun () ->
            let archived = ManagerState.archive sessionA archivedAt twoSessions |> expect
            Expect.equal
                (ManagerState.tryFind sessionB archived)
                (ManagerState.tryFind sessionB twoSessions)
                "beta is untouched"

        // "When was this archived" has one answer, so a second click is not a new one.
        testCase "archiving twice keeps the first moment" <| fun () ->
            let again =
                twoSessions
                |> ManagerState.archive sessionA archivedAt
                |> expect
                |> ManagerState.archive sessionA (archivedAt.AddHours 5.0)
                |> expect
            Expect.equal
                (ManagerState.tryFind sessionA again |> Option.bind (fun r -> r.ArchivedAt))
                (Some archivedAt)
                "the first archival stands"

        testCase "archiving a session that is not registered is refused" <| fun () ->
            let ghost = SessionId.create "ghost" |> expect
            match ManagerState.archive ghost archivedAt twoSessions with
            | Ok _ -> failwith "nothing was archived, so this must not report success"
            | Error e -> Expect.stringContains e "ghost" "the refusal names the session"

        testCase "unarchiving clears it" <| fun () ->
            let back =
                twoSessions
                |> ManagerState.archive sessionA archivedAt
                |> expect
                |> ManagerState.unarchive sessionA
                |> expect
            Expect.equal back twoSessions "the registry is exactly as it was"

        testCase "unarchiving a session that was never archived is not an error" <| fun () ->
            let back = ManagerState.unarchive sessionA twoSessions |> expect
            Expect.equal back twoSessions "it is already in the asked-for state"

        // THE invariant the whole feature rests on. The lookup and this refusal are one
        // verb, so no caller can hold a record without having been told.
        testCase "an archived session cannot be launched" <| fun () ->
            let archived = ManagerState.archive sessionA archivedAt twoSessions |> expect
            match ManagerState.launchable sessionA archived with
            | Ok _ -> failwith "an archived session was handed out as launchable"
            | Error e -> Expect.stringContains e "archived" "the refusal says why, in words a human can act on"

        testCase "an active session is launchable" <| fun () ->
            let record = ManagerState.launchable sessionA twoSessions |> expect
            Expect.equal record.SessionId sessionA "the record a launch may use"

        testCase "an unregistered session is not launchable" <| fun () ->
            let ghost = SessionId.create "ghost" |> expect
            match ManagerState.launchable ghost twoSessions with
            | Ok _ -> failwith "a session that does not exist was handed out as launchable"
            | Error e -> Expect.stringContains e "ghost" "the refusal names the session"

        testCase "an archived session round-trips through the codec" <| fun () ->
            let archived = ManagerState.archive sessionA archivedAt twoSessions |> expect
            let decoded = ManagerCodec.toString archived |> ManagerCodec.fromString |> expect
            Expect.equal decoded archived "decode∘encode is the identity, archival and all"

        // The same reading `mcpServers` gets, and for the same reason: the absence of the
        // field is a true statement about that file, not a migration.
        testCase "a state file written before archiving loads with every session active" <| fun () ->
            let old =
                """{"version":1,"sessions":[{"sessionId":"alpha","displayName":"Alpha work","createdAt":"2026-07-15T12:00:00.0000000+00:00","dataDir":"sessions/alpha"}]}"""
            let decoded = ManagerCodec.fromString old |> expect
            Expect.equal
                (decoded.Sessions |> List.map (fun r -> r.ArchivedAt))
                [ None ]
                "a session nobody could have archived is active"

        // Without this, a downgrade decodes a newer file, drops every field it does not
        // recognise, and saves the loss back over the original.
        testCase "a state file from a newer schema version is refused, not read as this one" <| fun () ->
            let future =
                """{"version":99,"sessions":[],"mcpServers":[]}"""
            match ManagerCodec.fromString future with
            | Ok _ -> failwith "a schema this build has never seen was decoded anyway"
            | Error e -> Expect.stringContains e "99" "the refusal names the version it could not read"
    ]

// -----------------------------------------------------------------------------
// Reading the registry: which archive states to show, and which way to order. Pure
// rules over the durable registry, so they live with it and run in the cheap tier.
// -----------------------------------------------------------------------------

let private mixed : SessionRecord list =
    [ { record "alpha" "Alpha work" with CreatedAt = DateTimeOffset (2026, 7, 1, 12, 0, 0, TimeSpan.Zero) }
      { record "beta" "Beta work" with
          CreatedAt = DateTimeOffset (2026, 7, 2, 12, 0, 0, TimeSpan.Zero)
          ArchivedAt = Some archivedAt }
      { record "gamma" "Gamma work" with CreatedAt = DateTimeOffset (2026, 7, 3, 12, 0, 0, TimeSpan.Zero) } ]

let private named (query: SessionQuery) =
    SessionQuery.apply query id mixed |> List.map (fun r -> SessionId.value r.SessionId)

let private queryTests =
    testList "Reading the session registry" [
        testCase "by default only active sessions are shown" <| fun () ->
            Expect.equal (named SessionQuery.defaults) [ "gamma"; "alpha" ] "beta is archived"

        testCase "the archived filter shows the archived ones" <| fun () ->
            Expect.equal (named { SessionQuery.defaults with Show = Set.singleton Archived }) [ "beta" ] "only beta"

        testCase "both states shown is the whole registry" <| fun () ->
            Expect.equal
                (named { SessionQuery.defaults with Show = Set.ofList [ Active; Archived ] })
                [ "gamma"; "beta"; "alpha" ]
                "everything, newest first"

        // Two cleared filters mean what they say. The surface renders this as an empty state
        // with the controls that caused it directly above, never as an empty registry.
        testCase "neither state shown is nothing" <| fun () ->
            Expect.isEmpty (named { SessionQuery.defaults with Show = Set.empty }) "nothing was asked for"

        testCase "newest first orders by when the session was created" <| fun () ->
            Expect.equal
                (named { Show = Set.ofList [ Active; Archived ]; Order = NewestFirst })
                [ "gamma"; "beta"; "alpha" ]
                "the most recently created leads"

        testCase "oldest first is the other way round" <| fun () ->
            Expect.equal
                (named { Show = Set.ofList [ Active; Archived ]; Order = OldestFirst })
                [ "alpha"; "beta"; "gamma" ]
                "the earliest leads"

        // A query string is what somebody bookmarked. Refusing one shows them no list at all,
        // so every axis falls back rather than failing.
        testCase "an absent query is the default one" <| fun () ->
            Expect.equal (SessionQuery.ofQueryString "") SessionQuery.defaults "nothing asked for is the default view"

        testCase "an unrecognised sort falls back to the default order" <| fun () ->
            Expect.equal (SessionQuery.ofQueryString "?sort=banana").Order NewestFirst "not a refusal"

        testCase "a query reads back the states it names" <| fun () ->
            Expect.equal
                (SessionQuery.ofQueryString "/?show=active&show=archived&sort=created-asc")
                { Show = Set.ofList [ Active; Archived ]; Order = OldestFirst }
                "both chips lit, oldest first"

        // The empty set needs a WORD, because an absent key already means "you did not
        // choose" — which is the default, not nothing.
        testCase "a query round-trips through its own query string" <| fun () ->
            for query in
                [ SessionQuery.defaults
                  { Show = Set.ofList [ Active; Archived ]; Order = OldestFirst }
                  { Show = Set.singleton Archived; Order = NewestFirst }
                  { Show = Set.empty; Order = OldestFirst } ] do
                Expect.equal
                    (SessionQuery.ofQueryString (SessionQuery.toQueryString query))
                    query
                    "what the surface links to is what it reads back"

        testCase "toggling a state flips that one and leaves the other alone" <| fun () ->
            Expect.equal
                (SessionQuery.toggling Archived SessionQuery.defaults)
                { SessionQuery.defaults with Show = Set.ofList [ Active; Archived ] }
                "archived joins active rather than replacing it"

        testCase "reversing an order changes nothing else" <| fun () ->
            let query = { Show = Set.singleton Archived; Order = NewestFirst }
            Expect.equal (SessionQuery.reversed query) { query with Order = OldestFirst } "only the direction moves"
    ]

// -----------------------------------------------------------------------------
// Step 23 — the Session Process as an OS process. Verify tier: these spawn REAL
// child processes over the Fable output (`app/SessionMain.js` — built
// by `verify` before the suite runs) and connect real WebRTC clients.
// -----------------------------------------------------------------------------

[<Emit("process.execPath")>]
let private nodePath : string = Fable.Core.Util.jsNative

[<Emit("process.kill($0, 'SIGKILL')")>]
let private sigkill (pid: int) : unit = Fable.Core.Util.jsNative

let private processTests =
    testList "Session Process as an OS process (Step 23)" [
        testCaseAsync "spawn contract: launch, serve, message, stop, resume with history, crash observation, manager restart" <|
            async {
                let dataDir =
                    sprintf "tests/Yession.Tests/out/.data/pm-%d" (int (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) % 1000000)
                let options =
                    { ProcessManager.Options.defaults dataDir nodePath [ "app/SessionMain.js" ] with
                        Strategy = Some Strategy.localhost }
                let! pm = ProcessManager.create options

                // Create: durable registration, not running.
                let record = pm.CreateSession "proc-1" "Process One" |> expect
                Expect.equal (pm.Sessions () |> List.map (fun v -> v.Status)) [ ProcessManager.NotRunning ] "registered, not running"
                match pm.CreateSession "proc-1" "Again" with
                | Error _ -> ()
                | Ok _ -> failwith "duplicate session ids must be rejected"

                // Launch: the child prints its readiness line; the port is real.
                let! launched = pm.Launch record.SessionId
                let port = launched |> expect
                let pid =
                    match (pm.TryFind record.SessionId).Value.Status with
                    | ProcessManager.Running (p, pid, _) ->
                        Expect.equal p port "the view reports the readiness port"
                        pid
                    | other -> failwithf "expected Running, got %A" other
                match! pm.Launch record.SessionId with
                | Error reason -> Expect.isTrue (reason.Contains "already running") "double launch rejected"
                | Ok _ -> failwith "a session launches at most once concurrently"

                // The child serves the real bootstrap (session id embedded) and a real
                // WebRTC client can message it.
                let! html = Interop.getText (sprintf "http://127.0.0.1:%d/" port) |> Async.AwaitPromise
                Expect.isTrue (html.Contains (Dom.sessionMetaName + "\" " + Dom.attr "content" "proc-1")) "the child serves ITS session's page"
                let signalUrl = sprintf "http://127.0.0.1:%d/signal" port
                // Access rides the OIDC bounce: login against the child, which round-trips
                // through this Manager's authorize endpoint and back.
                let! opened = OidcHttp.openSession (sprintf "http://127.0.0.1:%d" port)
                let! a = connectClient signalUrl opened.PeerToken "ada" "Ada"
                do! compose a a.Hello.PeerId "hello from another process"
                a.Connection.SendDraft a.Hello.PeerId
                do! a.Runner.WaitFor (fun m ->
                        m.Conversation.Items |> List.exists (fun i -> i.Body = "hello from another process"))
                do! a.Channel.Close ()

                // Stop is graceful and reflected; resume is just launch — over the same
                // data directory, so history replays into the fresh process.
                do! pm.Stop record.SessionId |> Async.Ignore
                Expect.equal (pm.TryFind record.SessionId).Value.Status ProcessManager.NotRunning "stopped"
                let! resumed = pm.Launch record.SessionId
                let resumedPort = resumed |> expect
                let! reopened = OidcHttp.openSession (sprintf "http://127.0.0.1:%d" resumedPort)
                let! b = connectClient (sprintf "http://127.0.0.1:%d/signal" resumedPort) reopened.PeerToken "grace" "Grace"
                do! b.Runner.WaitFor (fun m ->
                        not m.EventConsumer.IsCatchingUp
                        && (m.Conversation.Items |> List.exists (fun i -> i.Body = "hello from another process")))
                do! b.Channel.Close ()

                // A crash (killed outside the Manager) is observed, isolates to the
                // child, and the session relaunches cleanly.
                let crashPid =
                    match (pm.TryFind record.SessionId).Value.Status with
                    | ProcessManager.Running (_, pid, _) -> pid
                    | other -> failwithf "expected Running before the crash, got %A" other
                Expect.notEqual crashPid pid "resume spawned a fresh process"
                let exited = pm.WaitForExit record.SessionId
                sigkill crashPid
                do! exited
                match (pm.TryFind record.SessionId).Value.Status with
                | ProcessManager.Exited _ -> ()
                | other -> failwithf "expected Exited after the crash, got %A" other
                let! relaunched = pm.Launch record.SessionId
                relaunched |> expect |> ignore
                do! pm.StopAll ()

                // A restarted Manager keeps the registry (state file), reconciles
                // runtime state to stopped, and an unknown session cannot launch.
                let! pm2 = ProcessManager.create options
                Expect.equal
                    (pm2.Sessions () |> List.map (fun v -> v.Record.SessionId, v.Status))
                    [ record.SessionId, ProcessManager.NotRunning ]
                    "the registry survives a Manager restart; everything reconciles to stopped"
                match! pm2.Launch (SessionId.create "never-created" |> expect) with
                | Error reason -> Expect.isTrue (reason.Contains "unknown") "unknown sessions cannot launch"
                | Ok _ -> failwith "an unregistered session must not launch"
                do! pm2.StopAll ()
            }
    ]

// -----------------------------------------------------------------------------
// Step 24 — authority over the control RPC. The Step 11 rejection guarantees,
// re-verified ACROSS the process boundary: the capability calls travel over HTTP
// with a per-launch secret, and the Manager's registry still decides everything.
// -----------------------------------------------------------------------------

/// A port nothing is listening on, taken by binding :0 and releasing it. Needed where a
/// Manager's PUBLIC origin has to be known BEFORE it starts: that origin is its OIDC
/// issuer, and a launched session fetches discovery against it, so — exactly as in a real
/// fronted deployment — it has to be a URL that resolves from this host. A collision after
/// release fails the Manager's bind loudly; it can never produce a passing-but-wrong run.
let private freePort () : Async<int> =
    async {
        let server = Interop.createServer (fun _ res -> res.``end`` "")
        let! listening =
            Async.FromContinuations (fun (cont, _, _) -> server.listen (0, "127.0.0.1", fun () -> cont server) |> ignore)
        let port = Interop.serverPort listening
        do! Async.FromContinuations (fun (cont, _, _) -> listening.close (fun _ -> cont ()))
        return port
    }

/// Start a bare control server over the given secret→session table, plus the real
/// notification and MCP hubs wired to their SSE routes. Returns both hubs so a test
/// can push down the same wires the Manager uses.
let private startControlServer (secrets: (string * SessionId) list) : Async<Interop.HttpServer * string * NotificationHub.NotificationHub<SessionNotification> * KeyedRetainedHub.KeyedRetainedHub<McpServerSet>> =
    async {
        let table =
            secrets
            |> List.map (fun (secret, sessionId) ->
                let caller : Control.ControlCaller = { SessionId = sessionId; Users = Set.empty; Peers = Set.empty; Local = false }
                secret, caller)
            |> Map.ofList
        let hub = NotificationHub.create ()
        let mcp = KeyedRetainedHub.create McpServerSet.empty
        // This bare control server has no OIDC provider; the DCR route is not under test.
        let registerClient _ (sessionId: SessionId) _ : Yession.Oidc.RegisterClientResponse =
            { ClientId = SessionId.value sessionId; ClientSecret = "unused"; Issuer = "http://unused" }
        let handler (req: Interop.IncomingMessage) (res: Interop.ServerResponse) =
            if not (Control.tryHandle (fun secret -> Map.tryFind secret table) (fun _ _ -> async { return Ok () }) (fun _ _ -> async { return Ok () }) hub.Register mcp.Register registerClient None None (fun _ _ -> Subscription.none) ignore req res) then
                res.writeHead (404, Fable.Core.JsInterop.createObj [ "content-type", box "text/plain" ]) |> ignore
                res.``end`` "not found"
        let server = Interop.createServer handler
        let! listening =
            Async.FromContinuations (fun (cont, _, _) -> server.listen (0, "127.0.0.1", fun () -> cont server) |> ignore)
        return listening, sprintf "http://127.0.0.1:%d" (Interop.serverPort listening), hub, mcp
    }

let private controlRpcTests =
    testList "Session-owned environment across real processes (Step 24, reworked)" [
        testCaseAsync "a child Session Process runs its own WorkSandbox end to end (diagnostic agent across real processes)" <|
            async {
                let dataDir =
                    sprintf "tests/Yession.Tests/out/.data/rpc-%d" (int (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) % 1000000)
                let! pm =
                    ProcessManager.create
                        { ProcessManager.Options.defaults dataDir nodePath [ "app/SessionMain.js" ] with
                            Strategy = Some Strategy.localhost }
                let record = pm.CreateSession "rpc-child" "RPC child" |> expect

                // The child inherits our environment: run its built-in diagnostic agent.
                let! launched =
                    Support.withEnv [ "YESSION_SESSION_AGENT", Some "diagnostic" ] (fun () -> pm.Launch record.SessionId)
                let port = launched |> expect

                let! opened = OidcHttp.openSession (sprintf "http://127.0.0.1:%d" port)
                let! a = connectClient (sprintf "http://127.0.0.1:%d/signal" port) opened.PeerToken "ada" "Ada"
                do! compose a a.Hello.PeerId "run the diagnostic"
                a.Connection.SendDraft a.Hello.PeerId

                // Everything below happened ACROSS process boundaries: the child created
                // its own sandbox (no Manager grant exists any more), ran the command in
                // it, and streamed the events back to a client.
                //
                // The running environment's ref is `srt`, which is the DEFAULT asserted
                // rather than the configuration: nothing here sets YESSION_SESSION_WORK_BACKEND,
                // so this is what a session gets when nobody chose — and a revert to an
                // unconfined default would fail here instead of passing quietly.
                do! a.Runner.WaitFor (fun m ->
                        (m.Conversation.Items
                         |> List.exists (fun i -> i.Author = ActorRef.Agent && i.Status = Complete && i.Body.Contains "diagnostic-ok"))
                        && (match m.Environment with EnvironmentRunning ref -> ref = "srt" | _ -> false)
                        // The diagnostic agent's command is a terminal BLOCK now (Plan 13,
                        // stage 3b): the read-only command log retired with the merged tool.
                        && (m.Terminals.Terminals
                            |> List.exists (fun t ->
                                t.Blocks
                                |> List.exists (fun b ->
                                    b.Command.Contains "diagnostic-ok"
                                    && b.Status = BlockFinished (CommandSucceeded 0)))))

                do! a.Channel.Close ()
                do! pm.StopAll ()
            }
    ]

// -----------------------------------------------------------------------------
// Step 25 — the management UI (server-side rendered Lit, swapped by a tiny inline
// script — no htmx). Fragment rendering is pure (cheap tier); the flow over real HTTP +
// real child processes is verify tier.
// -----------------------------------------------------------------------------

let private uiRecord : SessionRecord =
    { SessionId = SessionId.create "ui-render" |> expect
      DisplayName = "UI <Render>"
      CreatedAt = DateTimeOffset (2026, 7, 15, 12, 0, 0, TimeSpan.Zero)
      DataDir = "sessions/ui-render"
      ArchivedAt = None }

let private uiRenderTests =
    testList "Management UI rendering (Step 25)" [
        testCase "a stopped session's row offers Launch; a running one offers Stop and the open link" <| fun () ->
            let stopped = ManagerUi.sessionRow PublicAccess.Loopback { Record = uiRecord; Status = ProcessManager.NotRunning }
            Expect.isTrue (stopped.Contains (Dom.attr Dom.Manager.launch "ui-render")) "stopped rows can launch (button carries the session id)"
            Expect.isTrue (stopped.Contains "UI &lt;Render&gt;") "display names are escaped"
            let running = ManagerUi.sessionRow PublicAccess.Loopback { Record = uiRecord; Status = ProcessManager.Running (8199, 42, Some "1.2.3-beta.4") }
            Expect.isTrue (running.Contains (Dom.attr Dom.Manager.stop "ui-render")) "running rows can stop"
            Expect.isTrue (running.Contains "href=\"http://127.0.0.1:8199/\"") "the open link is a plain URL to the child's port (no token — access is the OIDC bounce)"
            Expect.isTrue (running.Contains (Dom.attr Dom.Manager.session "ui-render")) "the row is a poll unit keyed by session id"
            let crashed = ManagerUi.sessionRow PublicAccess.Loopback { Record = uiRecord; Status = ProcessManager.Exited (Some 1) }
            Expect.isTrue (crashed.Contains (Dom.attr Dom.Manager.status Dom.Manager.statusExited)) "a crash is visible"
            Expect.isTrue (crashed.Contains (Dom.attr Dom.Manager.launch "ui-render")) "a crashed session can relaunch"

        // Which build a live session is running had no answer anywhere. The Manager reads it
        // off the readiness line to refuse a major skew and then dropped it, so the one
        // process that knows what every session is executing published nothing — and a
        // session that outlives a promotion keeps running the old image silently. The rule is
        // the answer is ON the row, because a session runs the image it was spawned from and
        // only a row-by-row reading shows which ones have moved.
        testCase "a running row says which build it is running" <| fun () ->
            let running =
                ManagerUi.sessionRow
                    PublicAccess.Loopback
                    { Record = uiRecord; Status = ProcessManager.Running (8199, 42, Some "0.0.0-gf1ce52b") }
            Expect.isTrue (running.Contains Dom.Manager.build) "the row carries the hook that marks it"
            Expect.isTrue (running.Contains "0.0.0-gf1ce52b") "wearing the build the launch reported"

        // The other half, and the one that keeps the first honest: an older bundle reports no
        // version, and a placeholder there would read as an answer. AGENTS.md's version rule
        // is the same one — never invent a version-shaped value for a build that cannot state
        // one.
        testCase "a launch that reported no build has none invented for it" <| fun () ->
            let running =
                ManagerUi.sessionRow PublicAccess.Loopback { Record = uiRecord; Status = ProcessManager.Running (8199, 42, None) }
            Expect.isTrue (running.Contains "port 8199 · pid 42") "what is known is still said"
            Expect.isFalse (running.Contains "pid 42 · ") "and nothing stands in for what is not"

        // The Manager is the process that CANNOT roll forward on its own — it keeps the image
        // it exec'd until a restart, so it is routinely the oldest build on the page, and the
        // page that shows every session's build and not its own answers the question wrong.
        testCase "the page says the Manager's own build" <| fun () ->
            let html =
                ManagerUi.page
                    "app.css"
                    PublicAccess.Loopback
                    SessionQuery.defaults
                    [ { Record = uiRecord; Status = ProcessManager.NotRunning } ]
                    []
            Expect.isTrue (html.Contains Dom.Manager.managerBuild) "the header carries its own hook, distinct from a row's"
            Expect.isTrue (html.Contains Yession.Host.Version.current) "and its own version, from the same place --version reads"

        testCase "the page is self-contained: an inline script drives it, no external sources" <| fun () ->
            let html =
                ManagerUi.page
                    "app.css"
                    PublicAccess.Loopback
                    SessionQuery.defaults
                    [ { Record = uiRecord; Status = ProcessManager.NotRunning } ]
                    []
            Expect.isTrue (html.Contains "<script>") "an inline script drives the UI (no bundle)"
            Expect.isTrue (html.Contains "/sessions/") "the inline script talks to the fragment routes"
            Expect.isFalse (html.Contains "src=\"http") "no external/CDN scripts (local-first)"
            Expect.isTrue (html.Contains Dom.Manager.createSession) "the create form renders"

        // Archiving. What must hold however this table is redrawn: a session that cannot be
        // started is not offered a control that starts it, and the one act it CAN take is
        // reachable and named.
        testCase "an archived session's row offers Unarchive and not Launch" <| fun () ->
            let archived =
                ManagerUi.sessionRow
                    PublicAccess.Loopback
                    { Record = { uiRecord with ArchivedAt = Some archivedAt }; Status = ProcessManager.NotRunning }
            Expect.isTrue (archived.Contains (Dom.attr Dom.Manager.unarchive "ui-render")) "the way back is offered"
            Expect.isFalse
                (archived.Contains (Dom.attr Dom.Manager.launch "ui-render"))
                "a control whose only outcome is a refusal is not offered"

        testCase "an archived session says archived rather than stopped" <| fun () ->
            let archived =
                ManagerUi.sessionRow
                    PublicAccess.Loopback
                    { Record = { uiRecord with ArchivedAt = Some archivedAt }; Status = ProcessManager.NotRunning }
            Expect.isTrue
                (archived.Contains (Dom.attr Dom.Manager.status Dom.Manager.statusArchived))
                "the cell answers the question a reader is actually asking"

        // The downstream half of archiving-stops-it (AGENTS.md "Fixing bugs"): `Archive` stops
        // the child BEFORE marking the record, so this pairing should not arise. If some later
        // fault produces it anyway, the row must not hand somebody a live link into a session
        // its operator retired — a plausible wrong answer is worse than a missing one.
        testCase "an archived session is never offered as a link to open" <| fun () ->
            let both =
                ManagerUi.sessionRow
                    PublicAccess.Loopback
                    { Record = { uiRecord with ArchivedAt = Some archivedAt }
                      Status = ProcessManager.Running (8199, 42, Some "1.2.3-beta.4") }
            // The address is the discriminating assertion: the case above pins that a RUNNING
            // row carries exactly this href, so its absence here cannot pass vacuously.
            Expect.isFalse (both.Contains "http://127.0.0.1:8199/") "no address to reach it at"
            Expect.isFalse (both.Contains Dom.Manager.openLink) "and nothing marked as the way in"

        testCase "an active session can be archived, by a control with a name" <| fun () ->
            let active = ManagerUi.sessionRow PublicAccess.Loopback { Record = uiRecord; Status = ProcessManager.NotRunning }
            Expect.isTrue (active.Contains (Dom.attr Dom.Manager.archive "ui-render")) "the row can be archived"
            // WCAG: an icon-only control carries an accessible name, and it names WHICH
            // session — a column of "Archive" says nothing about which row you are on.
            Expect.isTrue (active.Contains "aria-label=\"Archive UI &lt;Render&gt;\"") "named, and escaped"

        testCase "the filter says which states are shown and links to the ones that are not" <| fun () ->
            let table =
                ManagerUi.sessionsTable
                    PublicAccess.Loopback
                    SessionQuery.defaults
                    [ { Record = uiRecord; Status = ProcessManager.NotRunning } ]
            Expect.isTrue (table.Contains (Dom.attr Dom.Manager.filter "show-active")) "the active filter is a control"
            Expect.isTrue (table.Contains (Dom.attr Dom.Manager.filter "show-archived")) "so is the archived one"
            Expect.isTrue (table.Contains "aria-current=\"true\"") "and the shown one says it is shown"
            Expect.isTrue
                (table.Contains "show=active&amp;show=archived")
                "the unlit chip links to the query that adds it, computed server-side"

        testCase "the created column declares which way it is sorted, and links to the reverse" <| fun () ->
            let newest =
                ManagerUi.sessionsTable
                    PublicAccess.Loopback
                    SessionQuery.defaults
                    [ { Record = uiRecord; Status = ProcessManager.NotRunning } ]
            Expect.isTrue (newest.Contains "aria-sort=\"descending\"") "the header states the sort"
            Expect.isTrue (newest.Contains "sort=created-asc") "and links to the other direction"
            let oldest =
                ManagerUi.sessionsTable
                    PublicAccess.Loopback
                    { SessionQuery.defaults with Order = OldestFirst }
                    [ { Record = uiRecord; Status = ProcessManager.NotRunning } ]
            Expect.isTrue (oldest.Contains "aria-sort=\"ascending\"") "and the other way round"

        // Saying "no sessions yet" over a registry that has three would send somebody
        // looking for a bug in the Manager.
        testCase "a filter that hides everything says so, rather than that there is nothing" <| fun () ->
            let hidden =
                ManagerUi.sessionsTable
                    PublicAccess.Loopback
                    { SessionQuery.defaults with Show = Set.singleton Archived }
                    [ { Record = uiRecord; Status = ProcessManager.NotRunning } ]
            Expect.isFalse (hidden.Contains "no sessions yet") "the registry is not empty"
            Expect.isTrue (hidden.Contains "1 hidden") "it says what it is hiding"
            let empty = ManagerUi.sessionsTable PublicAccess.Loopback SessionQuery.defaults []
            Expect.isTrue (empty.Contains "no sessions yet") "an empty registry still says the plain thing"

        // Plan 17. Declaring is the ONE act that names a url, and the only management
        // action that can be refused for a reason a human has to read.
        testCase "the MCP section renders its declarations, and the form that adds one" <| fun () ->
            let views = [ { ProcessManager.Record = uiRecord; ProcessManager.Status = ProcessManager.NotRunning } ]
            let empty = ManagerUi.mcpSection views []
            Expect.isTrue (empty.Contains "no MCP servers") "an empty registry says so rather than showing a bare table"
            Expect.isTrue (empty.Contains "data-declare-mcp") "and still offers the form"

            let declared =
                ManagerUi.mcpSection
                    views
                    [ { Server = serialServer; Audience = AnySession }
                      { Server = printerServer; Audience = OneSession uiRecord.SessionId } ]
            Expect.isTrue (declared.Contains "http://127.0.0.1:7333") "the address an operator typed is what is shown back"
            Expect.isTrue (declared.Contains "any session") "a host-wide declaration says who it reaches"
            Expect.isTrue (declared.Contains "UI &lt;Render&gt;") "a session-scoped one names the session (escaped), not its id"
            Expect.isTrue (declared.Contains "data-mcp-withdraw=\"serial\"") "each row can be withdrawn"
            // WCAG: every input has a label, and the refusal has somewhere to be announced.
            Expect.isTrue (declared.Contains "for=\"mcp-name\"") "the name input is labelled"
            Expect.isTrue (declared.Contains "for=\"mcp-url\"") "the address input is labelled"
            Expect.isTrue (declared.Contains "for=\"mcp-audience\"") "the audience select is labelled"
            Expect.isTrue (declared.Contains "aria-live") "a refusal is announced rather than only drawn"
    ]

// --- WCAG 2.0 AA floor (AGENTS.md "UI baseline"): theme contrast, pinned by test ---------
// The tokens live in app/tailwind.css (@theme); every text colour must keep >= 4.5:1
// against every surface it can sit on. Computed here exactly as WCAG 2.0 defines it.

[<Emit("$0.readFileSync($1, 'utf8')")>]
let private readFileSync (fs: obj) (path: string) : string = Fable.Core.Util.jsNative

[<Emit("parseInt($0, 16)")>]
let private parseHex (s: string) : float = Fable.Core.Util.jsNative

let private themeColour (css: string) (name: string) : string =
    let marker = sprintf "--color-%s:" name
    match css.IndexOf marker with
    | -1 -> failwithf "token --color-%s not found in app/tailwind.css" name
    | start ->
        let from = start + marker.Length
        css.Substring(from, css.IndexOf (';', from) - from).Trim ()

let private luminance (hex: string) : float =
    // Tokens use both #rgb and #rrggbb — expand the short form before slicing channels.
    let h =
        if hex.Length = 4 then
            sprintf "#%c%c%c%c%c%c" hex.[1] hex.[1] hex.[2] hex.[2] hex.[3] hex.[3]
        else hex
    let channel (i: int) =
        let c = parseHex (h.Substring (i, 2)) / 255.0
        if c <= 0.03928 then c / 12.92 else ((c + 0.055) / 1.055) ** 2.4
    0.2126 * channel 1 + 0.7152 * channel 3 + 0.0722 * channel 5

let private contrast (a: string) (b: string) : float =
    let la, lb = luminance a, luminance b
    (max la lb + 0.05) / (min la lb + 0.05)

let private themeContrastTests =
    testList "Theme contrast (WCAG 2.0 AA floor)" [
        testCase "every text colour keeps >= 4.5:1 on every surface" <| fun () ->
            let colour = themeColour (readFileSync nodeFs "app/tailwind.css")
            // The terminal palette (Plan 13) is text like any other: output sits on the
            // same surfaces, so it answers to the same floor. Listing all sixteen is the
            // point — raw ANSI would fail here, which is why the theme names its own.
            let terminalPalette =
                [ for name in [ "black"; "red"; "green"; "yellow"; "blue"; "magenta"; "cyan"; "white" ] do
                    yield "term-" + name
                    yield "term-" + name + "-bright" ]
            for fg in [ "ink"; "ink-dim"; "ink-faint"; "blue"; "green"; "err" ] @ terminalPalette do
                for bg in [ "bg"; "panel"; "surface"; "surface-2" ] do
                    let ratio = contrast (colour fg) (colour bg)
                    Expect.isTrue (ratio >= 4.5) (sprintf "--color-%s on --color-%s is %.2f:1 — the AA floor is 4.5:1" fg bg ratio)

        testCase "inverse text on filled (active) buttons keeps >= 4.5:1" <| fun () ->
            let colour = themeColour (readFileSync nodeFs "app/tailwind.css")
            for fill in [ "blue"; "green"; "err"; "ink" ] do
                let ratio = contrast (colour "bg") (colour fill)
                Expect.isTrue (ratio >= 4.5) (sprintf "text-bg on bg-%s is %.2f:1 — the AA floor is 4.5:1" fill ratio)
    ]

[<Emit("fetch($0, { method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' }, body: $1 }).then(async r => ({ status: r.status, cacheControl: '', body: await r.text() }))")>]
let private postForm (url: string) (body: string) : JS.Promise<obj> = Fable.Core.Util.jsNative

/// A GET that keeps its status. `Interop.getText` discards it, and a route whose whole job
/// is to distinguish "here it is" from "no such session" needs the number.
[<Emit("fetch($0).then(async r => ({ status: r.status, cacheControl: '', contentType: r.headers.get('content-type') || '', body: await r.text() }))")>]
let private getReply (url: string) : JS.Promise<obj> = Fable.Core.Util.jsNative

[<Emit("$0.status")>]
let private statusOfReply (reply: obj) : int = Fable.Core.Util.jsNative

/// What the answer said it WAS. A route a browser navigates to is not fully described by its
/// status: `text/plain` is a message on a laptop and a downloaded `document.txt` on a phone.
[<Emit("$0.contentType")>]
let private contentTypeOfReply (reply: obj) : string = Fable.Core.Util.jsNative

[<Emit("$0.body")>]
let private bodyOfReply (reply: obj) : string = Fable.Core.Util.jsNative

/// A form POST that does NOT follow its redirect. Where the create route points is the
/// contract; an auto-following fetch would swallow it and assert the destination instead.
[<Emit("fetch($0, { method: 'POST', redirect: 'manual', headers: { 'content-type': 'application/x-www-form-urlencoded' }, body: $1 }).then(async r => ({ status: r.status, cacheControl: '', location: r.headers.get('location') || '', body: await r.text() }))")>]
let private postFormHere (url: string) (body: string) : JS.Promise<obj> = Fable.Core.Util.jsNative

[<Emit("$0.location")>]
let private locationOfReply (reply: obj) : string = Fable.Core.Util.jsNative

/// A Manager with its management endpoint up, over real child processes. Hoisted because
/// the archiving cases below each pin ONE invariant and each needs one of these; the setup
/// is what repeats, not the assertion.
let private managerWithUi (name: string) =
    let dataDir =
        sprintf "tests/Yession.Tests/out/.data/%s-%d" name (int (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) % 1000000)
    ProcessManager.createWithUi
        { ProcessManager.Options.defaults dataDir nodePath [ "app/SessionMain.js" ] with
            Strategy = Some Strategy.localhost }
        (Some ManagerUi.tryHandle)

/// A deployment's front door that has not mapped anything yet — and can be told it has.
///
/// It answers `404 not found`, because that is what a reverse proxy with no route for an
/// address says, and that is the whole hazard: an address that ANSWERS and a session that
/// answers are different facts. A reconciler driven by `/sessions/stream` reaches a
/// just-created session a few hundred milliseconds after the Manager has launched it, and
/// everything that arrives in that window meets this.
let private startFrontDoor () : Async<Interop.HttpServer * string * (unit -> unit)> =
    async {
        let mutable mapped = false
        let handler (_: Interop.IncomingMessage) (res: Interop.ServerResponse) =
            if mapped then
                res.writeHead (200, Fable.Core.JsInterop.createObj [ "content-type", box "text/html; charset=utf-8" ]) |> ignore
                res.``end`` "<!doctype html><title>a session</title>"
            else
                res.writeHead (404, Fable.Core.JsInterop.createObj [ "content-type", box "text/plain" ]) |> ignore
                res.``end`` "not found"
        let server = Interop.createServer handler
        let! listening =
            Async.FromContinuations (fun (cont, _, _) -> server.listen (0, "127.0.0.1", fun () -> cont server) |> ignore)
        return listening, sprintf "http://127.0.0.1:%d" (Interop.serverPort listening), (fun () -> mapped <- true)
    }

/// A Manager publishing its sessions at a front door this test owns, with one session created
/// and running. Hoisted because the two cases below need exactly this arrangement and differ
/// only in what the door is doing when they ask.
let private managerBehindFrontDoor (name: string) =
    async {
        let! door, doorOrigin, mapIt = startFrontDoor ()
        let dataDir =
            sprintf "tests/Yession.Tests/out/.data/%s-%d" name (int (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) % 1000000)
        let! managerPort = freePort ()
        let origin = sprintf "http://127.0.0.1:%d" managerPort
        let! pm =
            ProcessManager.createWithUi
                { ProcessManager.Options.defaults dataDir nodePath [ "app/SessionMain.js" ] with
                    Strategy = Some Strategy.localhost
                    ManagerPort = Some managerPort
                    // Two origins deliberately. The Manager keeps its own, because it is the
                    // OIDC issuer a launched session fetches discovery against and that has
                    // to be an address which really answers; the SESSIONS are published at
                    // the door, which is the half `/open` has to wait for.
                    Public = PublicAccess.create origin (doorOrigin + "/s/{id}") |> expect }
                (Some ManagerUi.tryHandle)
        pm.CreateSession name "" |> expect |> ignore
        let sessionId = SessionId.create name |> expect
        let! launched = pm.Launch sessionId
        Expect.isTrue (Result.isOk launched) "the session launches"
        return pm, origin, mapIt, door
    }

/// `/sessions/{id}/ready`: whether this deployment's front door reaches the session yet.
///
/// The question `/open`'s landing page asks before it hands the browser over, and the one it
/// used to answer by GUESSING — a `no-cors` fetch of the address, which settles for the front
/// door's 404 exactly as it settles for the session, so whoever pressed Create was redirected
/// into that 404 a beat before the mapping appeared. On a phone a `text/plain` body is not
/// even a message: it is a file called `document.txt`.
let private readinessTests =
    testList "Is the session reachable yet (Plan 11)" [
        testCaseAsync "a session the front door has not mapped yet is not ready" <|
            async {
                let! pm, baseUrl, _, door = managerBehindFrontDoor "ready-miss"
                let! answer = getReply (baseUrl + "/sessions/ready-miss/ready") |> Async.AwaitPromise
                Expect.equal (statusOfReply answer) 503 "the session is running and its published address is not"
                do! pm.StopAll ()
                door.close ignore
            }

        testCaseAsync "a session whose published address answers is ready" <|
            async {
                let! pm, baseUrl, mapIt, door = managerBehindFrontDoor "ready-hit"
                mapIt ()
                let! answer = getReply (baseUrl + "/sessions/ready-hit/ready") |> Async.AwaitPromise
                Expect.equal (statusOfReply answer) 200 "the door routes to it now, so it is reachable"
                do! pm.StopAll ()
                door.close ignore
            }

        // The downstream half of the same fault. Whatever goes wrong upstream, a browser is
        // the only thing that ever lands on `/open` — so what it lands on has to be readable
        // by one. `text/plain` reads as a download on a phone, which is how a refusal with
        // words in it reached its operator as a file called `document.txt`.
        testCaseAsync "an /open that cannot hand the browser over answers a page, not a download" <|
            async {
                let! pm = managerWithUi "open-page"
                let baseUrl = sprintf "http://127.0.0.1:%d" pm.EndpointPort.Value
                let! missing = getReply (baseUrl + "/sessions/no-such-session/open") |> Async.AwaitPromise
                Expect.equal (statusOfReply missing) 404 "an unknown session is still a 404"
                Expect.stringContains (contentTypeOfReply missing) "text/html" "and it is a page a browser can show"
                do! pm.StopAll ()
            }
    ]

/// The half of archiving that cannot be decided purely: it stops a real child, and the
/// refusal has to hold over the wire and not just over a value.
let private archiveFlowTests =
    testList "Archiving over the process boundary" [
        testCaseAsync "archiving a running session stops its child" <|
            async {
                let! pm = managerWithUi "arch-stop"
                let id = SessionId.create "arch-stop" |> expect
                pm.CreateSession "arch-stop" "Archive me" |> expect |> ignore
                let! _ = pm.Launch id
                Expect.isTrue
                    (match (pm.TryFind id).Value.Status with ProcessManager.Running _ -> true | _ -> false)
                    "it is running before we archive it"
                let! archived = pm.Archive id
                Expect.isTrue (Result.isOk archived) "archiving succeeded"
                Expect.equal (pm.TryFind id).Value.Status ProcessManager.NotRunning "the child is gone"
                do! pm.StopAll ()
            }

        testCaseAsync "an archived session refuses to launch, and says so as a conflict" <|
            async {
                let! pm = managerWithUi "arch-refuse"
                let baseUrl = sprintf "http://127.0.0.1:%d" pm.EndpointPort.Value
                let id = SessionId.create "arch-refuse" |> expect
                pm.CreateSession "arch-refuse" "Archive me" |> expect |> ignore
                let! archived = pm.Archive id
                Expect.isTrue (Result.isOk archived) "archived"
                // Over the wire, because `/open` is the URL a session client's reconnect card
                // points at: a bookmark landing there must read as a conflict it can resolve,
                // not as the Manager having broken.
                let! refused = getReply (baseUrl + "/sessions/arch-refuse/open") |> Async.AwaitPromise
                Expect.equal (statusOfReply refused) 409 "a durable-state conflict, not a server fault"
                Expect.stringContains (bodyOfReply refused) "archived" "and it says why"
                do! pm.StopAll ()
            }

        testCaseAsync "unarchiving lets a session launch again" <|
            async {
                let! pm = managerWithUi "arch-back"
                let id = SessionId.create "arch-back" |> expect
                pm.CreateSession "arch-back" "Archive me" |> expect |> ignore
                let! _ = pm.Archive id
                pm.Unarchive id |> expect
                let! launched = pm.Launch id
                Expect.isTrue (Result.isOk launched) "the way back is a real way back"
                do! pm.StopAll ()
            }
    ]

// -----------------------------------------------------------------------------
// Every registry write announces itself. `createSession` used to save the record and
// tell only the MCP hub, so the session hub retained a PRE-CREATE list until the next
// launch, exit or rename — SSR rendered the new session, then the rows stream handed
// any page connecting in that window a list without it and the row vanished in front
// of whoever had just made it. Invisible to the person creating it (their page swaps
// from the POST answer, and nothing published over it) and invisible to every test
// that launched straight afterwards, because the launch published a correct list.
//
// So both halves are asserted against the HUB — what a subscriber is actually handed —
// and neither launches anything: the gap only exists before the first lifecycle event.
// -----------------------------------------------------------------------------

let private managerAlone (name: string) =
    let dataDir =
        sprintf "tests/Yession.Tests/out/.data/%s-%d" name (int (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) % 1000000)
    ProcessManager.create
        { ProcessManager.Options.defaults dataDir nodePath [ "app/SessionMain.js" ] with
            Strategy = Some Strategy.localhost }

let private registryPublishTests =
    testList "Registry writes announce themselves (UX review P0)" [
        testCaseAsync "creating a session pushes it to a subscriber already listening" <|
            async {
                let! pm = managerAlone "pub-live"
                let frames = ResizeArray<ProcessManager.SessionView list> ()
                let cancel = pm.SubscribeSessions frames.Add
                let seen = frames.Count
                pm.CreateSession "pub-live" "Published live" |> expect |> ignore
                Expect.isTrue
                    (frames
                     |> Seq.skip seen
                     |> Seq.exists (fun f -> f |> List.exists (fun v -> SessionId.value v.Record.SessionId = "pub-live")))
                    "the create itself pushed a frame carrying the new session"
                cancel.Stop ()
                do! pm.StopAll ()
            }

        testCaseAsync "a subscriber connecting after a create is handed it" <|
            async {
                let! pm = managerAlone "pub-retained"
                pm.CreateSession "pub-retained" "Published retained" |> expect |> ignore
                let mutable first : ProcessManager.SessionView list option = None
                let cancel = pm.SubscribeSessions (fun f -> if first.IsNone then first <- Some f)
                Expect.equal
                    (first |> Option.map (List.map (fun v -> SessionId.value v.Record.SessionId)))
                    (Some [ "pub-retained" ])
                    "the retained snapshot is current, not the list from before the create"
                cancel.Stop ()
                do! pm.StopAll ()
            }
    ]

let private uiFlowTests =
    testList "Management UI flow (Step 25)" [
        testCaseAsync "create -> launch -> open -> stop -> resume -> crash, all over the management endpoint, with live status pushed on the rows stream" <|
            async {
                let dataDir =
                    sprintf "tests/Yession.Tests/out/.data/ui-%d" (int (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) % 1000000)
                let! pm =
                    ProcessManager.createWithUi
                        { ProcessManager.Options.defaults dataDir nodePath [ "app/SessionMain.js" ] with
                            Strategy = Some Strategy.localhost }
                        (Some ManagerUi.tryHandle)
                let baseUrl = sprintf "http://127.0.0.1:%d" pm.EndpointPort.Value

                // The page serves, self-contained.
                let! page = Interop.getText (baseUrl + "/") |> Async.AwaitPromise
                Expect.isTrue (page.Contains Dom.Manager.createSession) "the create form is served"
                // Fetch the stylesheet the PAGE names rather than a path spelled here: it is
                // addressed by a digest of its own bytes, so a hardcoded URL would be a second
                // spelling of it, wrong the moment the stylesheet changes. Following the link
                // is also the stronger assertion — it proves the page and the route agree.
                let styleSheetUrl =
                    System.Text.RegularExpressions.Regex.Match(page, "href=\"(assets/[^\"]+/app\\.css)\"").Groups.[1].Value
                Expect.notEqual styleSheetUrl "" "the page names a stylesheet"
                let! css = Interop.getText (baseUrl + "/" + styleSheetUrl) |> Async.AwaitPromise
                Expect.isTrue (css.Length > 500) "the shared local stylesheet serves from the endpoint (no CDN)"

                // Create over the form endpoint. The answer is where the session now IS —
                // creating one is asking to work in it, and `/open` is the stable route that
                // launches it and lands you there. Not followed here: this case still wants
                // it stopped, and what /open does with it is /open's own case below.
                let! created = postFormHere (baseUrl + "/sessions") "id=ui-1&name=UI+One" |> Async.AwaitPromise
                Expect.equal (statusOfReply created) 303 "creating hands the browser onward, rather than a table to look at"
                Expect.equal (locationOfReply created) "/sessions/ui-1/open" "onward is the session's stable open route"
                let! duplicate = postFormHere (baseUrl + "/sessions") "id=ui-1&name=Again" |> Async.AwaitPromise
                Expect.equal (statusOfReply duplicate) 400 "duplicates are rejected"

                // Launch from the UI; the fragment reflects it and the child REALLY serves.
                let! launched = postForm (baseUrl + "/sessions/ui-1/launch") "" |> Async.AwaitPromise
                let row = bodyOfReply launched
                Expect.isTrue (row.Contains (Dom.attr Dom.Manager.status Dom.Manager.statusRunning)) "the row shows running"
                let sessionPort =
                    match (pm.TryFind (SessionId.create "ui-1" |> expect)).Value.Status with
                    | ProcessManager.Running (port, _, _) -> port
                    | other -> failwithf "expected Running, got %A" other
                Expect.isTrue (row.Contains (sprintf "href=\"http://127.0.0.1:%d/\"" sessionPort)) "the open link is live (plain URL, no token)"
                let! shell = Interop.getText (sprintf "http://127.0.0.1:%d/" sessionPort) |> Async.AwaitPromise
                Expect.isTrue (shell.Contains (Dom.sessionMetaName + "\" " + Dom.attr "content" "ui-1")) "the opened session serves its shell"

                // Live status, pushed: the rows stream's first frame is the current table (the
                // hub's retained snapshot), so a page that just loaded agrees without polling.
                let tables = ResizeArray<string> ()
                let cancelRows = Sse.subscribe (baseUrl + "/sessions/rows") [] tables.Add
                do! waitUntil "the rows snapshot" (fun () -> tables.Count > 0)
                Expect.isTrue
                    (tables.[0].Contains (Dom.attr Dom.Manager.status Dom.Manager.statusRunning))
                    "the snapshot table shows the running session"

                // Stop, resume — each transition pushes a fresh table on the open stream.
                let beforeStop = tables.Count
                let! stopped = postForm (baseUrl + "/sessions/ui-1/stop") "" |> Async.AwaitPromise
                Expect.isTrue ((bodyOfReply stopped).Contains (Dom.attr Dom.Manager.status Dom.Manager.statusStopped)) "stopped from the UI"
                do! waitUntil "the stop frame" (fun () ->
                        tables
                        |> Seq.skip beforeStop
                        |> Seq.exists (fun t -> t.Contains (Dom.attr Dom.Manager.status Dom.Manager.statusStopped)))
                let beforeResume = tables.Count
                let! resumed = postForm (baseUrl + "/sessions/ui-1/launch") "" |> Async.AwaitPromise
                Expect.isTrue ((bodyOfReply resumed).Contains (Dom.attr Dom.Manager.status Dom.Manager.statusRunning)) "resume is just launch"
                do! waitUntil "the resume frame" (fun () ->
                        tables
                        |> Seq.skip beforeResume
                        |> Seq.exists (fun t -> t.Contains (Dom.attr Dom.Manager.status Dom.Manager.statusRunning)))

                // A crash reaches the page as EXITED, in the frame the exit itself pushes: the
                // Manager records the exit code before announcing it, so a subscriber that
                // renders on the frame never shows a crash as a plain stop.
                let crashPid =
                    match (pm.TryFind (SessionId.create "ui-1" |> expect)).Value.Status with
                    | ProcessManager.Running (_, pid, _) -> pid
                    | other -> failwithf "expected Running before the crash, got %A" other
                let beforeCrash = tables.Count
                let exited = pm.WaitForExit (SessionId.create "ui-1" |> expect)
                sigkill crashPid
                do! exited
                do! waitUntil "the crash frame" (fun () ->
                        tables
                        |> Seq.skip beforeCrash
                        |> Seq.exists (fun t -> t.Contains (Dom.attr Dom.Manager.status Dom.Manager.statusExited)))
                cancelRows.Stop ()

                do! pm.StopAll ()
            }

        // Plan 11's stable way back in. A session's own address changes on every relaunch,
        // and under idle reaping that is routine — so this route, on the Manager's fixed
        // port, is what a bookmark and the client's reconnect offer point at.
        testCaseAsync "GET /sessions/{id}/open launches a stopped session and lands on its address" <|
            async {
                let dataDir =
                    sprintf "tests/Yession.Tests/out/.data/open-%d" (int (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) % 1000000)
                let! pm =
                    ProcessManager.createWithUi
                        { ProcessManager.Options.defaults dataDir nodePath [ "app/SessionMain.js" ] with
                            Strategy = Some Strategy.localhost }
                        (Some ManagerUi.tryHandle)
                let baseUrl = sprintf "http://127.0.0.1:%d" pm.EndpointPort.Value
                let sessionId = SessionId.create "open-1" |> expect

                // Registered in-process: creating over the form endpoint now LANDS you in the
                // session, and what this case is about is `/open` finding one stopped.
                pm.CreateSession "open-1" "Open One" |> expect |> ignore

                // Stopped: /open has to start it before there is any address to give.
                Expect.equal (pm.TryFind sessionId).Value.Status ProcessManager.NotRunning "created, not launched"
                let! opened = Interop.getText (baseUrl + "/sessions/open-1/open") |> Async.AwaitPromise
                let launchedPort =
                    match (pm.TryFind sessionId).Value.Status with
                    | ProcessManager.Running (port, _, _) -> port
                    | other -> failwithf "expected /open to have launched it, got %A" other
                Expect.isTrue
                    (opened.Contains (sprintf "http://127.0.0.1:%d/" launchedPort))
                    "the landing page names the session's own address"

                // Already running: /open is not a relaunch — it hands back the same address,
                // which is what makes the URL safe to keep clicking.
                let! again = Interop.getText (baseUrl + "/sessions/open-1/open") |> Async.AwaitPromise
                match (pm.TryFind sessionId).Value.Status with
                | ProcessManager.Running (port, _, _) ->
                    Expect.equal port launchedPort "the running session was not restarted"
                    Expect.isTrue (again.Contains (sprintf "http://127.0.0.1:%d/" port)) "same address"
                | other -> failwithf "expected it to still be running, got %A" other

                // An unknown session is a 404, not a launch attempt.
                let! missing = getReply (baseUrl + "/sessions/nope-nope/open") |> Async.AwaitPromise
                Expect.equal (statusOfReply missing) 404 "unknown sessions are not created by asking to open them"

                do! pm.StopAll ()
            }
    ]

// -----------------------------------------------------------------------------
// Plan 11 — reaping across the process boundary.
//
// `Reaper.plan` is unit-tested over virtual time and `POST /control/activity` has a codec
// test, but neither can see the thing that actually broke: a session reports from inside
// its own boot, so its first report reaches the Manager BEFORE `Spawn.launch` resolves.
// The Manager recorded the launch after that, dropped the early report, and reaped a
// session that had reported as `never-reported` — the reap was right and its reason was a
// lie. Only a real child process shows that, so this is the one E2E the feature needs.
// -----------------------------------------------------------------------------

let private reapingTests =
    testList "Idle reaping over the process boundary (Plan 11)" [
        testCaseAsync "an idle session is reaped for the RIGHT reason, and comes back at the same address" <|
            async {
                let dataDir =
                    sprintf "tests/Yession.Tests/out/.data/reap-%d" (int (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) % 1000000)
                // Every lifecycle event the Manager emits, so the reap's REASON is read from
                // the telemetry that operators read, not inferred from the session being gone.
                let events = ResizeArray<string * (string * obj) list> ()
                // A free port rather than a fixed one, like the fronted registry test below:
                // the Manager must actually ANSWER on the origin it declares, because a
                // launched session fetches OIDC discovery against it (Plan 10).
                // Declaring one it does not answer on fails the launch, not the assertion.
                let! managerPort = freePort ()
                let origin = sprintf "http://127.0.0.1:%d" managerPort
                let! pm =
                    ProcessManager.createWithUi
                        { ProcessManager.Options.defaults dataDir nodePath [ "app/SessionMain.js" ] with
                            Strategy = Some Strategy.localhost
                            // Path-mounted: a session's address is derived from its ID, so it
                            // is the same string before and after a reap however the OS
                            // reassigns ports. That is the guarantee that replaced pinning.
                            ManagerPort = Some managerPort
                            Public = PublicAccess.create origin (origin + "/s/{id}") |> expect
                            IdleTimeout = Some (TimeSpan.FromSeconds 3.0)
                            OnEvent = fun name attrs -> lock events (fun () -> events.Add (name, attrs)) }
                        (Some ManagerUi.tryHandle)
                let baseUrl = sprintf "http://127.0.0.1:%d" pm.EndpointPort.Value
                let sessionId = SessionId.create "reap-1" |> expect
                pm.CreateSession "reap-1" "Reap One" |> expect |> ignore

                // The address this deployment publishes for the session, before it has ever
                // run: derived from the id alone, which is why it needs no port to compute.
                let address () = (PublicAccess.sessionAddress sessionId 0 pm.Public).Url
                Expect.equal (address ()) (origin + "/s/reap-1") "addressed by id, not by port"

                let! launched = pm.Launch sessionId
                Expect.isTrue (Result.isOk launched) "the session launches"

                // Nobody connects, so it is idle from the moment it boots. It reports that,
                // and the Manager stops it once the window elapses.
                do! waitUntil "the idle session to be reaped" (fun () ->
                        match (pm.TryFind sessionId).Value.Status with
                        | ProcessManager.NotRunning -> true
                        | _ -> false)

                // THE assertion. `idle` means the session's report crossed the control
                // channel and was recorded against this launch; `never-reported` would mean
                // it was dropped — which is exactly what used to happen, and which no unit
                // test can see because it is an ordering fact about two processes.
                let reason =
                    lock events (fun () ->
                        events
                        |> Seq.filter (fun (name, _) -> name = "session exited")
                        |> Seq.tryLast
                        |> Option.bind (fun (_, attrs) ->
                            attrs |> List.tryPick (fun (k, v) -> if k = "yession.session.stop_reason" then Some (string v) else None)))
                Expect.equal reason (Some "idle") "a session that reports must be reaped as idle, never as never-reported"

                // And the way back returns it to the SAME address. This is the property that
                // replaced port pinning: the browser partitions storage by origin, so an
                // address that survives a reap is what lets a client keep what it wrote.
                // Under a `{id}` template that holds by construction rather than by
                // bookkeeping — the address never mentioned the port to begin with.
                let! reopened = Interop.getText (baseUrl + "/sessions/reap-1/open") |> Async.AwaitPromise
                Expect.isTrue
                    (reopened.Contains (origin + "/s/reap-1/"))
                    "reopening lands on the address the reaped session had"
                Expect.equal (address ()) (origin + "/s/reap-1") "and it is unchanged by the reap"
                match (pm.TryFind sessionId).Value.Status with
                | ProcessManager.Running _ -> ()
                | other -> failwithf "expected /open to have relaunched it, got %A" other

                do! pm.StopAll ()
            }
    ]

// -----------------------------------------------------------------------------
// Step 27/28 — the composition E2E: the SHIPPED npm bundles (`dist/npm/manager.js`
// + `session.js`, produced by `dotnet fsi tasks.fsx` inside `verify`), composed for
// real — the packaged manager spawns the packaged session,
// the management UI drives them, a real WebRTC client talks to the child, the
// control RPC exercises authority, and crash-resume + a manager restart preserve
// everything. This is what gates a release.
// -----------------------------------------------------------------------------

[<Fable.Core.Import("spawn", "node:child_process")>]
let private spawnRaw : obj = Fable.Core.Util.jsNative

// Run the packaged manager bundle on this Node, pointing it at the packaged session
// bundle (what the `yession` bin shim does in an install). `--auth localhost` mirrors a
// single-machine operator's choice — the shipped default (`none`) denies everything.
[<Emit("$0(process.execPath, [$1, '--auth', 'localhost'], { env: { ...process.env, YESSION_SPAWN_MAIN: $3, ...Object.fromEntries($2) }, stdio: ['pipe', 'pipe', 'inherit'] })")>]
let private spawnBundle (spawn: obj) (managerJs: string) (env: (string * string) array) (sessionJs: string) : obj = Fable.Core.Util.jsNative

[<Emit("$0.stdout.on('data', $1)")>]
let private onStdout (child: obj) (handler: obj -> unit) : unit = Fable.Core.Util.jsNative

[<Emit("(function (chunk) { return typeof chunk === 'string' ? chunk : chunk.toString('utf8') })($0)")>]
let private chunkToString (chunk: obj) : string = Fable.Core.Util.jsNative

[<Emit("$0.kill('SIGKILL')")>]
let private killBinary (child: obj) : unit = Fable.Core.Util.jsNative

[<Emit("$0.on('exit', $1)")>]
let private onBinaryExit (child: obj) (handler: obj -> unit) : unit = Fable.Core.Util.jsNative

/// A running packaged manager: its two announced URLs and a kill that resolves once
/// the process is gone.
type private PackagedManager =
    { SessionUrl : string
      UiUrl : string
      Shutdown : unit -> Async<unit> }

let private startPackagedManager (env: (string * string) list) : Async<PackagedManager> =
    Async.FromContinuations (fun (cont, econt, _) ->
        let child =
            spawnBundle spawnRaw "dist/npm/manager.js" (Array.ofList env) "dist/npm/session.js"
        let mutable sessionUrl = None
        let mutable uiUrl = None
        let mutable settled = false
        let urlIn (line: string) =
            let m = System.Text.RegularExpressions.Regex.Match (line, "http://[0-9.:]+/")
            if m.Success then Some m.Value else None
        onBinaryExit child (fun _ ->
            if not settled then
                settled <- true
                econt (Exception "packaged manager exited before announcing its endpoints"))
        // A missing/unrunnable binary is a loud test failure, not a crashed runner.
        Fable.Core.JsInterop.emitJsExpr (child, (fun (e: obj) ->
            if not settled then
                settled <- true
                econt (Exception (sprintf "packaged manager failed to start: %A" e)))) "$0.on('error', $1)"
        let mutable buffer = ""
        onStdout child (fun chunk ->
            buffer <- buffer + chunkToString chunk
            let parts = buffer.Split '\n'
            buffer <- parts.[parts.Length - 1]
            for line in parts.[0 .. parts.Length - 2] do
                if line.Contains "launched at" then sessionUrl <- urlIn line
                if line.Contains "management UI at" then uiUrl <- urlIn line
                match sessionUrl, uiUrl, settled with
                | Some s, Some u, false ->
                    settled <- true
                    cont
                        { SessionUrl = s
                          UiUrl = u
                          Shutdown =
                            fun () ->
                                Async.FromContinuations (fun (kcont, _, _) ->
                                    onBinaryExit child (fun _ -> kcont ())
                                    killBinary child) }
                | _ -> ()))

let private portOfRow (row: string) : int =
    let m = System.Text.RegularExpressions.Regex.Match (row, "port (\\d+)")
    if m.Success then int m.Groups.[1].Value else failwithf "no port in row: %s" row

let private compositionTests =
    testList "Executable composition (Step 27/28)" [
        testCaseAsync "the shipped npm bundles compose: manage, message, authority, crash-resume, manager restart" <|
            async {
                let dataDir =
                    sprintf "tests/Yession.Tests/out/.data/composed-%d" (int (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) % 1000000)
                let env =
                    [ "YESSION_DATA_DIR", dataDir
                      "YESSION_PORT", "0"
                      // Children inherit this: the built-in diagnostic agent exercises
                      // the control RPC on the shipped binaries, credential-free.
                      "YESSION_SESSION_AGENT", "diagnostic" ]

                let! manager = startPackagedManager env

                // Create and launch a session from the management UI. The redirect is not
                // followed: creating now launches and opens, and this case wants the launch
                // to be its own act so the fragment it answers with names the port below.
                let! created = postFormHere (manager.UiUrl + "sessions") "id=composed&name=Composed" |> Async.AwaitPromise
                Expect.equal (statusOfReply created) 303 "created via the UI"
                let! launched = postForm (manager.UiUrl + "sessions/composed/launch") "" |> Async.AwaitPromise
                let row = bodyOfReply launched
                Expect.isTrue (row.Contains (Dom.attr Dom.Manager.status Dom.Manager.statusRunning)) "launched via the UI"
                let sessionPort = portOfRow row

                // A real client messages the packaged child; access rides the OIDC bounce
                // through the packaged manager; the diagnostic agent runs a real command
                // through the packaged manager's control RPC.
                let! openedA = OidcHttp.openSession (sprintf "http://127.0.0.1:%d" sessionPort)
                let! a = connectClient (sprintf "http://127.0.0.1:%d/signal" sessionPort) openedA.PeerToken "ada" "Ada"
                do! compose a a.Hello.PeerId "built binaries talking"
                a.Connection.SendDraft a.Hello.PeerId
                do! a.Runner.WaitFor (fun m ->
                        (m.Conversation.Items |> List.exists (fun i -> i.Body = "built binaries talking"))
                        && (m.Conversation.Items
                            |> List.exists (fun i -> i.Author = ActorRef.Agent && i.Status = Complete && i.Body.Contains "diagnostic-ok"))
                        && (match m.Environment with EnvironmentRunning _ -> true | _ -> false)
                        && (m.Terminals.Terminals
                            |> List.exists (fun t ->
                                t.Blocks |> List.exists (fun b -> b.Status = BlockFinished (CommandSucceeded 0)))))
                do! a.Channel.Close ()

                // Stop and resume from the UI; history replays into the fresh child.
                let! stopped = postForm (manager.UiUrl + "sessions/composed/stop") "" |> Async.AwaitPromise
                Expect.isTrue ((bodyOfReply stopped).Contains (Dom.attr Dom.Manager.status Dom.Manager.statusStopped)) "stopped via the UI"
                let! resumed = postForm (manager.UiUrl + "sessions/composed/launch") "" |> Async.AwaitPromise
                let resumedPort = portOfRow (bodyOfReply resumed)
                let! openedB = OidcHttp.openSession (sprintf "http://127.0.0.1:%d" resumedPort)
                let! b = connectClient (sprintf "http://127.0.0.1:%d/signal" resumedPort) openedB.PeerToken "grace" "Grace"
                do! b.Runner.WaitFor (fun m ->
                        not m.EventConsumer.IsCatchingUp
                        && (m.Conversation.Items |> List.exists (fun i -> i.Body = "built binaries talking")))
                do! b.Channel.Close ()

                // Kill the manager (its children die with it), restart over the same
                // data directory: the registry survives, and resume still works.
                do! manager.Shutdown ()
                let! manager2 = startPackagedManager env
                let! page = Interop.getText manager2.UiUrl |> Async.AwaitPromise
                Expect.isTrue (page.Contains (Dom.attr Dom.Manager.session "composed")) "the registry survived the manager restart"
                let! relaunched = postForm (manager2.UiUrl + "sessions/composed/launch") "" |> Async.AwaitPromise
                let relaunchedPort = portOfRow (bodyOfReply relaunched)
                let! openedC = OidcHttp.openSession (sprintf "http://127.0.0.1:%d" relaunchedPort)
                let! c = connectClient (sprintf "http://127.0.0.1:%d/signal" relaunchedPort) openedC.PeerToken "carol" "Carol"
                do! c.Runner.WaitFor (fun m ->
                        not m.EventConsumer.IsCatchingUp
                        && (m.Conversation.Items |> List.exists (fun i -> i.Body = "built binaries talking")))
                do! c.Channel.Close ()
                do! manager2.Shutdown ()
            }
    ]

// -----------------------------------------------------------------------------
// Telemetry over the process boundary. Every process is a DIRECT OTel emitter — the
// Manager does not collect. The Manager passes the standard OTEL_* env through to the
// child (Spawn merges over process.env) and adapts its identity; the child (the
// credential-free `usage-probe` agent) exports its token/cache counts straight to a stub
// OTLP collector (the stand-in for a real OTel Collector), over real OTLP HTTP — the
// Manager is not in the telemetry path. The Manager emits its own lifecycle signals via
// OnEvent. Verify tier: a real child process + a real WebRTC client trigger the turn.
// -----------------------------------------------------------------------------

let private telemetryTests =
    testList "Telemetry over the process boundary" [
        testCaseAsync "a real child session's turn usage reaches a collector directly over OTLP" <|
            async {
                let dataDir =
                    sprintf "tests/Yession.Tests/out/.data/tel-%d" (int (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) % 1000000)

                // A stub OTLP collector with an arrival signal so the test awaits the record
                // instead of polling the async batch export.
                let mutable fired = false
                let mutable waiter : (unit -> unit) option = None
                let! stub =
                    OtlpStub.startWith (fun _ ->
                        fired <- true
                        match waiter with
                        | Some resume -> waiter <- None; resume ()
                        | None -> ())

                // The Manager is itself a direct emitter: capture its lifecycle signals.
                let managerEvents = ResizeArray<string> ()
                let! pm =
                    ProcessManager.create
                        { ProcessManager.Options.defaults dataDir nodePath [ "app/SessionMain.js" ] with
                            Strategy = Some Strategy.localhost
                            OnEvent = fun body _ -> managerEvents.Add body }
                let record = pm.CreateSession "tel-child" "Tel child" |> expect

                // The child inherits our environment: run its usage-probe agent and export OTLP
                // straight to the stub collector (no Manager receiver).
                let! launched =
                    Support.withEnv
                        [ "YESSION_SESSION_AGENT", Some "usage-probe"
                          "OTEL_LOGS_EXPORTER", Some "otlp"
                          "OTEL_EXPORTER_OTLP_ENDPOINT", Some stub.Url ]
                        (fun () -> pm.Launch record.SessionId)
                let port = launched |> expect

                // A real client messages the child; access rides the OIDC bounce; the probe
                // turn runs and emits usage directly to the collector.
                let! openedTel = OidcHttp.openSession (sprintf "http://127.0.0.1:%d" port)
                let! a = connectClient (sprintf "http://127.0.0.1:%d/signal" port) openedTel.PeerToken "ada" "Ada"
                do! compose a a.Hello.PeerId "probe a turn"
                a.Connection.SendDraft a.Hello.PeerId

                do! Async.FromContinuations (fun (cont, _, _) -> if fired then cont () else waiter <- Some cont)

                match stub.Received () |> List.choose OtlpStub.turnUsage with
                | u :: _ ->
                    Expect.equal u.SessionId "tel-child" "the record is tagged with the child's session id"
                    Expect.equal (u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheCreationTokens) (111, 22, 3, 4) "the probe's counts crossed the process boundary"
                    Expect.equal u.Model (Some "probe-model") "the model crossed the boundary"
                | [] -> failwith "no usage record reached the collector"

                // The emitting build survives the real spawn + OTLP path, on the child's own
                // resource. It cannot discriminate parent from child here — the suite spawns the
                // unbundled `app/SessionMain.js`, so both processes are the same build (`dev`).
                // That each process reports ITS OWN build rests on `service.version` living in
                // the code default and never in the OTEL_RESOURCE_ATTRIBUTES the Manager injects.
                match stub.Received () with
                | r :: _ ->
                    Expect.equal (OtlpStub.resourceAttr "service.version" r) (Some Version.current)
                        "the child's record names the build it came from"
                    Expect.equal (OtlpStub.resourceAttr "service.name" r) (Some "yession-session")
                        "under the identity the Manager adapted for it"
                | [] -> failwith "no record reached the collector"

                Expect.isTrue (managerEvents.Contains "session launched") "the Manager emitted its own launch lifecycle signal"

                do! a.Channel.Close ()
                stub.Close ()
                do! pm.StopAll ()
            }
    ]

// -----------------------------------------------------------------------------
// SSE framing — the wire format every push stream in the Manager shares. Pure
// string functions, so the cheap tier pins them; the routes that use them are
// exercised over real sockets in the tiers below.
// -----------------------------------------------------------------------------

let private sseTests =
    testList "SSE framing" [
        testCase "a single-line payload is one data line, terminated by a blank line" <| fun () ->
            Expect.equal (Sse.frame """{"sessions":[]}""") "data: {\"sessions\":[]}\n\n" "the control legs' JSON shape"

        testCase "a multi-line payload becomes one data line per line, and parses back whole" <| fun () ->
            // Rendered markup (the management page's table) is multi-line, and blank lines inside
            // it must NOT end the event — that is the bug a naive `data: %s\n\n` would ship.
            let markup = "<table>\n  <tr>\n\n    <td>ada</td>\n  </tr>\n</table>"
            let framed = Sse.frame markup
            Expect.equal (framed.Split '\n' |> Array.filter (fun l -> l.StartsWith "data:") |> Array.length) 6
                "every line of the payload carries its own data prefix"
            Expect.isTrue (framed.EndsWith "\n\n") "the event ends with the blank line that dispatches it"
            Expect.equal (Sse.dataOf (framed.TrimEnd '\n')) (Some markup) "and it parses back to exactly the payload"

        testCase "a comment-only event carries no data" <| fun () ->
            Expect.equal (Sse.dataOf ": ping") None "a keep-alive is not a message"
            Expect.equal (Sse.dataOf ": subscribed") None "neither is the stream's opening comment"
    ]

// -----------------------------------------------------------------------------
// Manager→Session notifications — the reverse leg of the control RPC. The wire
// codec and the subscriber hub's fan-out are cheap-tier; the SSE stream end to
// end (real sockets, real client parser) is verify-tier.
// -----------------------------------------------------------------------------

let private notificationTests =
    testList "Manager→Session notifications: codec & hub" [
        testCase "a notification round-trips through the control wire codec" <| fun () ->
            let original = EnvironmentChanged ()
            let roundTripped =
                ControlWire.toString ControlWire.sessionNotification original
                |> ControlWire.fromString ControlWire.sessionNotification
                |> expect
            Expect.equal roundTripped original "notification round-trip is identity"

        testCase "an unknown notification kind decodes to an error, never a crash" <| fun () ->
            Expect.isError
                (ControlWire.fromString ControlWire.sessionNotification """{"kind":"someFutureThing"}""")
                "unknown kinds are a decode error (older decoders reject a newer case)"

        testCase "the hub fans a notification out to a secret's sinks, and only that secret's" <| fun () ->
            let hub = NotificationHub.create ()
            let mutable a1 = 0
            let mutable a2 = 0
            let mutable b = 0
            let _ = hub.Register "secret-a" (fun _ -> a1 <- a1 + 1)
            let unsubA2 = hub.Register "secret-a" (fun _ -> a2 <- a2 + 1)
            let _ = hub.Register "secret-b" (fun _ -> b <- b + 1)

            hub.NotifySecret "secret-a" (EnvironmentChanged ())
            Expect.equal (a1, a2, b) (1, 1, 0) "both A sinks fired; B's did not (per-secret scoping)"

            // Unsubscribe removes exactly one sink; the sibling keeps receiving.
            unsubA2.Stop ()
            hub.NotifySecret "secret-a" (EnvironmentChanged ())
            Expect.equal (a1, a2, b) (2, 1, 0) "the unsubscribed sink stopped; the other continued"

            // Dropping the secret (its launch ended) silences everything under it.
            hub.Drop "secret-a"
            hub.NotifySecret "secret-a" (EnvironmentChanged ())
            Expect.equal (a1, a2, b) (2, 1, 0) "a dropped secret receives nothing"

            // Notifying an unknown secret is a no-op, never a throw.
            hub.NotifySecret "secret-unknown" (EnvironmentChanged ())
    ]

let private notificationStreamTests =
    testList "Manager→Session notifications over SSE (reverse control leg)" [
        testCaseAsync "a pushed notification reaches the subscribed session, is scoped to it, and stops on cancel" <|
            async {
                let! server, url, hub, _ =
                    startControlServer
                        [ "secret-a", (SessionId.create "sse-a" |> expect)
                          "secret-b", (SessionId.create "sse-b" |> expect) ]

                let mutable receivedA : SessionNotification list = []
                let mutable receivedB : SessionNotification list = []
                let cancelA = ControlClient.subscribeNotifications url "secret-a" (fun n -> receivedA <- receivedA @ [ n ])
                let cancelB = ControlClient.subscribeNotifications url "secret-b" (fun n -> receivedB <- receivedB @ [ n ])

                // The subscription connects asynchronously and notifications are not
                // buffered, so push until the first arrives (or a generous timeout).
                let rec pump (remaining: int) =
                    async {
                        if not (List.isEmpty receivedA) || remaining <= 0 then return ()
                        else
                            hub.NotifySecret "secret-a" (EnvironmentChanged ())
                            do! Async.Sleep 50
                            return! pump (remaining - 1)
                    }
                do! pump 60

                Expect.isTrue (not (List.isEmpty receivedA)) "A received the notification pushed to its secret"
                Expect.equal (List.head receivedA) (EnvironmentChanged ()) "the notification decoded correctly across the wire"
                Expect.isTrue (List.isEmpty receivedB) "B never received a notification pushed to A's secret (per-session scoping)"

                // Cancel closes the stream; the server unsubscribes the sink, so further
                // pushes never arrive.
                cancelA.Stop ()
                do! Async.Sleep 200
                let settled = List.length receivedA
                hub.NotifySecret "secret-a" (EnvironmentChanged ())
                do! Async.Sleep 200
                Expect.equal (List.length receivedA) settled "after cancel, no further notifications arrive"

                cancelB.Stop ()
                server.close ignore
            }
    ]

// -----------------------------------------------------------------------------
// MCP tool stream — the second reverse leg of the control RPC. The wire codec
// (standard ListToolsResult, incl. raw-JSON inputSchema passthrough) and the
// hub's retained-snapshot fan-out are cheap-tier; the SSE stream end to end is
// verify-tier (Ports — real sockets, no native addon).
// -----------------------------------------------------------------------------

// A tool with a real JSON-Schema input. The schema is written compact so the codec's
// re-serialisation round-trips it byte-for-byte (Thoth renders with no spaces at indent 0).
let private searchTool : McpTool =
    { Name = "search"
      Title = Some "Search"
      Description = Some "Full-text search"
      InputSchema = """{"type":"object","properties":{"q":{"type":"string"}},"required":["q"]}""" }

let private mcpTests =
    testList "MCP server set: codec & hub" [
        testCase "a tool list round-trips, inputSchema intact" <| fun () ->
            // MCP's own `Tool`, which the SESSION decodes a provider's `tools/list` into
            // (Plan 17). It no longer crosses the control leg, but the schema must survive
            // a round trip wherever it is carried.
            let original =
                { Tools = [ searchTool; { Name = "noop"; Title = None; Description = None; InputSchema = "{}" } ] }
            let roundTripped =
                ControlWire.toString Codec.mcpToolList original
                |> ControlWire.fromString Codec.mcpToolList
                |> expect
            Expect.equal roundTripped original "round-trip is identity (schema stays an object, optionals preserved)"

        testCase "inputSchema is a real JSON object on the wire, not a quoted string" <| fun () ->
            let json = ControlWire.toString Codec.mcpToolList { Tools = [ searchTool ] }
            Expect.isTrue (json.Contains "\"inputSchema\":{") "the schema serialises as an embedded object"

        testCase "a server set round-trips, and carries no audience" <| fun () ->
            let original = { Servers = [ serialServer ] }
            let json = ControlWire.toString Codec.mcpServerSet original
            Expect.equal
                (ControlWire.fromString Codec.mcpServerSet json |> expect)
                original
                "decode∘encode is the identity"
            // Resolution already happened. A session that could read who ELSE reaches a
            // server would be reading the Manager's configuration rather than its own set.
            Expect.isFalse (json.Contains "audience") "the frame carries the servers, not the declarations"

        testCase "the hub retains PER SESSION, so two sessions see different sets" <| fun () ->
            let hub = KeyedRetainedHub.create McpServerSet.empty
            let mutable toA : McpServerSet list = []
            let mutable toB : McpServerSet list = []
            let _ = hub.Register sessionA "secret-a" (fun s -> toA <- toA @ [ s ])
            let _ = hub.Register sessionB "secret-b" (fun s -> toB <- toB @ [ s ])
            Expect.equal toA [ McpServerSet.empty ] "A gets its current (empty) set at once"
            Expect.equal toB [ McpServerSet.empty ] "and so does B"

            hub.Publish sessionA { Servers = [ serialServer ] }
            Expect.equal (List.last toA) { Servers = [ serialServer ] } "A got the change"
            Expect.equal toB [ McpServerSet.empty ] "B did not — it is not A"
            Expect.equal (hub.Current sessionA) { Servers = [ serialServer ] } "the hub retains A's set"

        testCase "a RELAUNCH under a new secret is handed the retained set" <| fun () ->
            // The reason retention is keyed by session and delivery by secret. A session
            // that restarts subscribes under a secret the hub has never seen; it must find
            // its servers waiting, not an empty set.
            let hub = KeyedRetainedHub.create McpServerSet.empty
            hub.Publish sessionA { Servers = [ serialServer ] }
            let mutable relaunched : McpServerSet list = []
            let _ = hub.Register sessionA "a-second-launch" (fun s -> relaunched <- relaunched @ [ s ])
            Expect.equal relaunched [ { Servers = [ serialServer ] } ] "the new launch is current at once"

        testCase "dropping a secret kills its sinks and NOTHING else" <| fun () ->
            let hub = KeyedRetainedHub.create McpServerSet.empty
            let mutable dying : McpServerSet list = []
            let mutable surviving : McpServerSet list = []
            let _ = hub.Register sessionA "old-launch" (fun s -> dying <- dying @ [ s ])
            let _ = hub.Register sessionB "other-launch" (fun s -> surviving <- surviving @ [ s ])

            hub.Drop "old-launch"
            hub.Publish sessionA { Servers = [ serialServer ] }
            hub.Publish sessionB { Servers = [ serialServer ] }
            Expect.equal dying [ McpServerSet.empty ] "the dropped launch's sink received nothing further"
            Expect.equal (List.last surviving) { Servers = [ serialServer ] } "the other launch is untouched"
            // The DECLARATION outlives the launch — that is the whole point of keying
            // retention by session rather than by secret.
            Expect.equal (hub.Current sessionA) { Servers = [ serialServer ] } "A's set survived its launch ending"
    ]

let private mcpStreamTests =
    testList "MCP server set over SSE (reverse control leg)" [
        testCaseAsync "a session gets its own set on connect, then every change, and stops on cancel" <|
            async {
                let sseSession = SessionId.create "sse-mcp" |> expect
                let! server, url, _, mcp = startControlServer [ "secret-a", sseSession ]
                // Seeded before anyone subscribes: a connecting session must still see it.
                mcp.Publish sseSession { Servers = [ serialServer ] }

                let mutable received : McpServerSet list = []
                let cancel = ControlClient.subscribeMcp url "secret-a" (fun s -> received <- received @ [ s ])

                let rec waitFor (remaining: int) =
                    async {
                        if not (List.isEmpty received) || remaining <= 0 then return ()
                        else
                            do! Async.Sleep 50
                            return! waitFor (remaining - 1)
                    }
                do! waitFor 60
                Expect.equal
                    (List.tryHead received)
                    (Some { Servers = [ serialServer ] })
                    "the retained set arrived on connect, decoded across the wire"

                let before = List.length received
                mcp.Publish sseSession { Servers = [ serialServer; printerServer ] }
                let rec waitGrow (remaining: int) =
                    async {
                        if List.length received > before || remaining <= 0 then return ()
                        else
                            do! Async.Sleep 50
                            return! waitGrow (remaining - 1)
                    }
                do! waitGrow 60
                Expect.equal
                    (List.last received |> fun s -> List.length s.Servers)
                    2
                    "the change was pushed to the live subscriber"

                cancel.Stop ()
                do! Async.Sleep 200
                let settled = List.length received
                mcp.Publish sseSession McpServerSet.empty
                do! Async.Sleep 200
                Expect.equal (List.length received) settled "after cancel, no further sets arrive"

                server.close ignore
            }

        // The declarations are durable and the hub is not, so a restart is the one moment
        // the two can disagree — and they did: a provider declared, working, and silently
        // gone after a version promotion restarted the Manager under it. The declaration was
        // still in the state file and still on the management page the whole time, which is
        // what made it invisible. Asserted against the hub's retained value, because that is
        // what a reconnecting session is HANDED; resolving the declarations again here would
        // pass with the bug in place.
        testCaseAsync "a Manager restarted over a declaration hands a session that declaration, not an empty set" <|
            async {
                let dataDir =
                    sprintf
                        "tests/Yession.Tests/out/.data/mcp-restart-%d"
                        (int (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) % 1000000)
                let options =
                    { ProcessManager.Options.defaults dataDir nodePath [ "app/SessionMain.js" ] with
                        Strategy = Some Strategy.localhost }

                let! pm = ProcessManager.create options
                let record = pm.CreateSession "mcp-restart" "MCP restart" |> expect
                pm.DeclareMcpServer { Server = serialServer; Audience = AnySession } |> expect
                Expect.equal
                    (pm.McpSetFor record.SessionId)
                    { Servers = [ serialServer ] }
                    "declaring publishes to the session's retained set"
                do! pm.StopAll ()

                // A restart: same data dir, nothing declared this time round.
                let! restarted = ProcessManager.create options
                Expect.equal
                    (restarted.McpSetFor record.SessionId)
                    { Servers = [ serialServer ] }
                    "the durable declaration is waiting for the session, without anyone re-declaring it"
                do! restarted.StopAll ()
            }
    ]

// -----------------------------------------------------------------------------
// Session registry stream (Plan 09) — the Running set as full-snapshot
// frames, published to whoever serves sessions (an operator's serving binding).
// Codec, hub, and public-origin assembly are cheap tier; the SSE stream over a
// real Manager + real children is verify tier.
// -----------------------------------------------------------------------------

let private registryEntry (id: string) (name: string) (port: int) (pid: int) : ControlWire.SessionRegistryEntry =
    { Id = SessionId.create id |> expect
      Name = name
      Port = port
      Pid = pid
      Build = Some "1.2.3-beta.4" }

let private registryTests =
    testList "Session registry: codec, projection & public origin (Plan 09)" [
        testCase "a registry frame round-trips through the control wire codec" <| fun () ->
            let original : ControlWire.SessionRegistryFrame =
                { Sessions = [ registryEntry "alpha" "Alpha work" 54321 4242; registryEntry "beta" "" 54322 1 ] }
            let roundTripped =
                ControlWire.toString ControlWire.sessionRegistryFrame original
                |> ControlWire.fromString ControlWire.sessionRegistryFrame
                |> expect
            Expect.equal roundTripped original "frame round-trip is identity"

        // The Manager publishes session VIEWS once; each consumer projects them. The registry's
        // projection is what a serving binding reconciles against, so it is pinned here — the
        // retained-hub mechanics it rides on are pinned once, with the MCP list.
        testCase "the registry frame is the Running sessions only, with the port and pid to reach them" <| fun () ->
            let views : ProcessManager.SessionView list =
                [ { Record = record "alpha" "Alpha work"; Status = ProcessManager.Running (54321, 4242, Some "1.2.3-beta.4") }
                  { Record = record "beta" "Beta work"; Status = ProcessManager.NotRunning }
                  { Record = record "gamma" "Gamma work"; Status = ProcessManager.Exited (Some 1) } ]
            Expect.equal
                (ProcessManager.registryFrameOf views)
                { Sessions = [ registryEntry "alpha" "Alpha work" 54321 4242 ] }
                "only what is running is reachable, so only that is announced"
            Expect.equal
                (ProcessManager.registryFrameOf [])
                { Sessions = [] }
                "no sessions is the empty frame, which is the boot state a subscriber starts from"

        // The registry is what a serving binding and the deployment's own tracker read, so the
        // build belongs on the wire and not only on the page: "which build is this session
        // running" is a question asked by scripts as much as by people.
        testCase "the registry announces each running session's build" <| fun () ->
            let views : ProcessManager.SessionView list =
                [ { Record = record "alpha" "Alpha work"; Status = ProcessManager.Running (54321, 4242, Some "0.0.0-gf1ce52b") }
                  { Record = record "beta" "Beta work"; Status = ProcessManager.Running (54322, 4243, None) } ]
            Expect.equal
                (ProcessManager.registryFrameOf views |> fun f -> f.Sessions |> List.map (fun e -> e.Build))
                [ Some "0.0.0-gf1ce52b"; None ]
                "what each launch reported, and nothing where it reported nothing"

        // Back-compat both ways: a frame written before the field decodes rather than failing,
        // and an absent build is absent on the wire rather than encoded as null.
        testCase "a frame without the build field decodes, and an absent build is not written" <| fun () ->
            let older =
                """{"sessions":[{"id":"alpha","name":"Alpha work","port":54321,"pid":4242}]}"""
                |> ControlWire.fromString ControlWire.sessionRegistryFrame
                |> expect
            Expect.equal (older.Sessions |> List.map (fun e -> e.Build)) [ None ] "an older peer is readable"
            let written =
                ControlWire.toString
                    ControlWire.sessionRegistryFrame
                    { Sessions = [ { registryEntry "alpha" "Alpha work" 54321 4242 with Build = None } ] }
            Expect.isFalse (written.Contains "build") "nothing known, nothing said"

        testCase "a running row's open link carries the configured public address" <| fun () ->
            let access = PublicAccess.create "https://home.example.ts.net" "http://home.example.ts.net:{port}" |> expect
            let running = ManagerUi.sessionRow access { Record = uiRecord; Status = ProcessManager.Running (8199, 42, Some "1.2.3-beta.4") }
            Expect.isTrue (running.Contains "href=\"http://home.example.ts.net:8199/\"") "the open link is followable from a remote browser"
    ]

// --- Public access: the deployment's two addresses as one value (Plan 09) ----------------

let private publicAccessTests =
    let sessionId = SessionId.create "ui-render" |> expect
    let addressOf managerUrl sessionUrl =
        PublicAccess.create managerUrl sessionUrl
        |> Result.map (PublicAccess.sessionAddress sessionId 54321)
    let errorOf managerUrl sessionUrl =
        match PublicAccess.create managerUrl sessionUrl with
        | Error e -> e
        | Ok _ -> "expected an error, got Ok"
    testList "Public access (Plan 09)" [
        testCase "unset means loopback: the Manager is its own endpoint, sessions their own ports" <| fun () ->
            let access = PublicAccess.create "" "" |> expect
            Expect.equal access Loopback "neither variable set"
            Expect.equal (PublicAccess.managerUrl access) None "the caller substitutes its own endpoint URL"
            Expect.equal
                (PublicAccess.sessionAddress sessionId 54321 access)
                { Url = "http://127.0.0.1:54321"; Mount = "" }
                "the pre-Plan-09 loopback address, at an origin root"

        // Plan 11. One statement of the precedence, reused by the Manager's OIDC issuer and
        // by the origin a session publishes to its clients — so the two provably agree, and
        // a client's offer to reopen a stopped session cannot point somewhere the login
        // bounce does not.
        testCase "managerUrlOr: a configured public origin always beats the caller's endpoint" <| fun () ->
            let fronted = PublicAccess.create "https://yession.example.com" "https://{id}.example.com" |> expect
            Expect.equal
                (PublicAccess.managerUrlOr (Some "http://127.0.0.1:8321") fronted)
                (Some "https://yession.example.com")
                "a loopback endpoint is unreachable from a browser that is not on this machine"

        testCase "managerUrlOr: loopback falls back to the endpoint the caller knows" <| fun () ->
            let loopback = PublicAccess.create "" "" |> expect
            Expect.equal
                (PublicAccess.managerUrlOr (Some "http://127.0.0.1:8321") loopback)
                (Some "http://127.0.0.1:8321")
                "on a single machine the Manager's own endpoint IS its public origin"

        testCase "managerUrlOr: with neither there is nothing to offer" <| fun () ->
            let loopback = PublicAccess.create "" "" |> expect
            Expect.equal (PublicAccess.managerUrlOr None loopback) None "a session with no Manager has nowhere to send anyone"

        testCase "a fronted Manager without fronted sessions is refused, not half-deployed" <| fun () ->
            // The mirror of the case below: a public issuer would register loopback
            // session addresses and OAuth callbacks nobody remote can reach.
            let message = errorOf "https://yession.example.com/" ""
            Expect.isTrue (message.Contains "YESSION_SESSION_URL") "the message names the missing variable"
            Expect.isTrue (message.Contains "127.0.0.1") "and why it cannot work"

        testCase "sessions fronted without the Manager is refused, not warned about" <| fun () ->
            // Always broken: the session bounces its users to the Manager to log in, so a
            // remote browser would be sent to 127.0.0.1. A half-set pair has no
            // constructor in either direction.
            let message = errorOf "" "https://home.example.ts.net:{port}"
            Expect.isTrue (message.Contains "YESSION_MANAGER_URL") "the message names the missing variable"
            Expect.isTrue (message.Contains "127.0.0.1") "and why it cannot work"

        testCase "a template describes whichever topology the operator's proxy implements" <| fun () ->
            let manager = "https://example.com"
            Expect.equal
                (addressOf manager "https://home.example.ts.net:{port}")
                (Ok { Url = "https://home.example.ts.net:54321"; Mount = "" })
                "port mirroring: a shared host, a port per session, at an origin root"
            Expect.equal
                (addressOf manager "https://{id}.sessions.example.com")
                (Ok { Url = "https://ui-render.sessions.example.com"; Mount = "" })
                "a subdomain per session: still an origin root, so no prefix"
            Expect.equal
                (addressOf manager "https://example.com/s/{id}")
                (Ok { Url = "https://example.com/s/ui-render"; Mount = "/s/ui-render" })
                "a path per session: the mount is the path component, derived from the same string"
            Expect.equal
                (addressOf manager "https://example.com/s/{id}/")
                (Ok { Url = "https://example.com/s/ui-render"; Mount = "/s/ui-render" })
                "a trailing slash is normalised away, so callers append their own path"

        testCase "a session knows its mount before it has a port" <| fun () ->
            // Everything a session fixes at boot depends on the mount — the shell's
            // `<base href>`, the cookie's `Path`, the prefix stripped off requests — and
            // its port is only assigned when it binds. So the mount derives from the id
            // alone, and a template that puts {port} in its PATH is refused.
            let mountOf sessionUrl =
                PublicAccess.create "https://example.com" sessionUrl
                |> Result.map (PublicAccess.sessionMount sessionId)
            Expect.equal (mountOf "https://example.com/s/{id}") (Ok "/s/ui-render") "path-mounted"
            Expect.equal (mountOf "https://{id}.example.com") (Ok "") "a subdomain is an origin root"
            Expect.equal (mountOf "https://example.com:{port}") (Ok "") "so is a port mirror"
            Expect.equal (PublicAccess.sessionMount sessionId Loopback) "" "and so is loopback"
            Expect.isTrue
                ((errorOf "https://example.com" "https://example.com/p/{port}").Contains "{port} in its path")
                "a mount that needed the port would not be knowable at boot"

        // Plan 13. This one predicate decides whether the client may promise that local
        // work survives a restart, so it has to answer for every template shape rather than
        // for the two anyone had in mind.
        testCase "a session keeps its address exactly when the template never names a port" <| fun () ->
            let stableOf sessionUrl =
                PublicAccess.create "https://example.com" sessionUrl
                |> Result.map PublicAccess.sessionAddressIsStable
            Expect.equal (stableOf "https://example.com/s/{id}") (Ok true) "path-mounted: derived from the id"
            Expect.equal (stableOf "https://{id}.example.com") (Ok true) "a subdomain per session is stable too"
            Expect.equal (stableOf "https://example.com:{port}") (Ok false) "port mirroring moves every launch"
            Expect.equal (stableOf "https://example.com:{port}/s/{id}") (Ok false) "and naming both is still a moving port"
            // The zero-config default is the one that most needs to say so, because it is
            // what someone gets without having thought about addressing at all.
            Expect.isFalse (PublicAccess.sessionAddressIsStable Loopback) "loopback is 127.0.0.1:{port}"

        testCase "the auth cookie is scoped to the path the session is served under" <| fun () ->
            // A real narrowing where sessions share a host: a path-mounted session's
            // cookie is no longer sent to its siblings. At an origin root, unchanged.
            Expect.isTrue ((Cookies.set "yession_auth_x" "/s/ui-render" "v").Contains "Path=/s/ui-render/") "scoped to the mount"
            Expect.isTrue ((Cookies.set "yession_auth_x" "" "v").Contains "Path=/") "root-mounted is unchanged"

        testCase "a session template without a placeholder is refused" <| fun () ->
            // Before this type the port was appended implicitly, so a bare origin was the
            // documented spelling and `http://host:8443` silently produced
            // `http://host:8443:54321`. Now the template says where the port goes.
            let message = errorOf "https://example.com" "http://home.example.ts.net:8443"
            Expect.isTrue (message.Contains "{id} or {port}") "the message names the placeholders"

        testCase "an unknown placeholder is refused rather than passed through literally" <| fun () ->
            let message = errorOf "https://example.com" "https://example.com/s/{name}"
            Expect.isTrue (message.Contains "{name}") "the message quotes the offending token"

        testCase "a Manager origin is scheme + host, never a path or a placeholder" <| fun () ->
            // Its routes are origin-anchored and its issuer is a concatenation base, so a
            // path would work only if the proxy stripped it again — refused rather than
            // shipped as an unverified maybe.
            // A session URL rides along so the ORIGIN error is the one under test, not
            // the half-set refusal.
            let sessions = "https://{id}.example.com"
            Expect.isTrue
                ((errorOf "https://example.com/yession" sessions).Contains "origin root")
                "a path prefix on the Manager is refused"
            Expect.isTrue
                ((errorOf "https://{id}.example.com" sessions).Contains "placeholder")
                "the Manager is one address, so a placeholder means nothing"
            Expect.isTrue
                ((errorOf "example.com" sessions).Contains "http://")
                "a scheme is required"
    ]

let private registryStreamTests =
    testList "Session registry stream over SSE (Plan 09)" [
        testCaseAsync "the stream snapshots on subscribe, follows launch/stop, and the public origin reaches link + redirect URI" <|
            async {
                let dataDir =
                    sprintf "tests/Yession.Tests/out/.data/reg-%d" (int (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) % 1000000)
                // The two mechanisms a real deployment uses, both driven by the same
                // declaration: the Manager holds the parsed value (its open links), and a
                // spawned session parses the same variables from the env it inherits (its
                // redirect URI).
                //
                // The Manager's public origin is a loopback one HERE because it is also the
                // OIDC issuer the launched session fetches discovery against — the standing
                // requirement that a fronted Manager's URL resolve from its own host, which
                // a made-up hostname would not.
                let! managerPort = freePort ()
                let managerUrl = sprintf "http://127.0.0.1:%d" managerPort
                let sessionUrl = "http://home.example.ts.net:{port}"
                let access = PublicAccess.create managerUrl sessionUrl |> expect
                let! pm =
                    ProcessManager.createWithUi
                        { ProcessManager.Options.defaults dataDir nodePath [ "app/SessionMain.js" ] with
                            Strategy = Some Strategy.localhost
                            ManagerPort = Some managerPort
                            Public = access }
                        (Some ManagerUi.tryHandle)
                let baseUrl = sprintf "http://127.0.0.1:%d" pm.EndpointPort.Value

                let frames = ResizeArray<ControlWire.SessionRegistryFrame> ()
                let cancel = ControlClient.subscribeSessions baseUrl (fun f -> frames.Add f)

                // The retained snapshot arrives on connect: nothing runs yet.
                do! waitUntil "the empty snapshot" (fun () -> frames |> Seq.exists (fun f -> List.isEmpty f.Sessions))

                let record = pm.CreateSession "reg-1" "Registry One" |> expect
                do! Support.withEnv
                        [ "YESSION_MANAGER_URL", Some managerUrl
                          "YESSION_SESSION_URL", Some sessionUrl ]
                        (fun () -> async {
                            let! launched = pm.Launch record.SessionId
                            let port = expect launched
                            do! waitUntil "the launch frame" (fun () ->
                                    frames
                                    |> Seq.exists (fun f ->
                                        f.Sessions
                                        |> List.exists (fun e -> SessionId.value e.Id = "reg-1" && e.Port = port && e.Name = "Registry One")))

                            // A reconnect's FIRST frame is the current snapshot — reconnecting IS
                            // the recovery protocol, and one connect-read-disconnect is a poll.
                            let mutable second : ControlWire.SessionRegistryFrame option = None
                            let cancelSecond = ControlClient.subscribeSessions baseUrl (fun f -> if second.IsNone then second <- Some f)
                            do! waitUntil "the second subscription's snapshot" (fun () -> second.IsSome)
                            cancelSecond.Stop ()
                            Expect.isTrue
                                (second.Value.Sessions |> List.exists (fun e -> e.Port = port))
                                "a fresh subscriber's first frame is the current snapshot"

                            // The public address reaches both browser-facing URLs: the open link
                            // and the session's registered OAuth redirect URI (via the env the
                            // child inherited at spawn).
                            let! rendered = Interop.getText (baseUrl + "/") |> Async.AwaitPromise
                            Expect.isTrue
                                (rendered.Contains (sprintf "href=\"http://home.example.ts.net:%d/\"" port))
                                "the open link carries the public origin"
                            let! login = OidcHttp.getWithJar (OidcHttp.newJar ()) (sprintf "http://127.0.0.1:%d/login" port)
                            Expect.equal login.Status 302 "/login redirects into the authorize chain"
                            Expect.isTrue
                                (login.Location.Contains (sprintf "home.example.ts.net%%3A%d%%2Fcallback" port))
                                "the registered redirect URI carries the public origin"

                            // A stop pushes a fresh frame without the session.
                            let seen = frames.Count
                            let! stopped = pm.Stop record.SessionId
                            expect stopped
                            do! waitUntil "the stop frame" (fun () ->
                                    frames |> Seq.skip seen |> Seq.exists (fun f -> List.isEmpty f.Sessions))
                        })

                cancel.Stop ()
                do! pm.StopAll ()
            }

        testCaseAsync "the stream is gated by the Manager's strategy: the deny-everything default refuses it" <|
            async {
                let dataDir =
                    sprintf "tests/Yession.Tests/out/.data/reg-deny-%d" (int (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) % 1000000)
                let! pm =
                    ProcessManager.createWithUi
                        (ProcessManager.Options.defaults dataDir nodePath [ "app/SessionMain.js" ])
                        (Some ManagerUi.tryHandle)
                let! reply = OidcHttp.getWithJar (OidcHttp.newJar ()) (sprintf "http://127.0.0.1:%d/sessions/stream" pm.EndpointPort.Value)
                Expect.equal reply.Status 401 "no strategy, no registry — same gate as every management route"
                do! pm.StopAll ()
            }

        testCaseAsync "under trusted-headers, only a request asserting an identity reaches the stream" <|
            async {
                let dataDir =
                    sprintf "tests/Yession.Tests/out/.data/reg-th-%d" (int (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) % 1000000)
                let! pm =
                    ProcessManager.createWithUi
                        { ProcessManager.Options.defaults dataDir nodePath [ "app/SessionMain.js" ] with
                            Strategy = Some Strategy.trustedHeaders }
                        (Some ManagerUi.tryHandle)
                let url = sprintf "http://127.0.0.1:%d/sessions/stream" pm.EndpointPort.Value
                let! bare = OidcHttp.getWithJar (OidcHttp.newJar ()) url
                Expect.equal bare.Status 401 "a header-less request is refused (a serving binding must assert itself)"
                // The SSE response never ends, so probe the status only: register a
                // subscriber through the client and expect its snapshot to arrive.
                let mutable snapshot : ControlWire.SessionRegistryFrame option = None
                let cancel =
                    ControlClient.subscribeSessionsAs
                        [ Strategy.SubjectHeader, "serving-binding" ]
                        (sprintf "http://127.0.0.1:%d" pm.EndpointPort.Value)
                        (fun f -> if snapshot.IsNone then snapshot <- Some f)
                let rec waitSnapshot (remaining: int) =
                    async {
                        if snapshot.IsSome || remaining <= 0 then return ()
                        else
                            do! Async.Sleep 50
                            return! waitSnapshot (remaining - 1)
                    }
                do! waitSnapshot 100
                cancel.Stop ()
                Expect.equal snapshot (Some { ControlWire.SessionRegistryFrame.Sessions = [] }) "an asserted identity gets the snapshot"
                do! pm.StopAll ()
            }
    ]

let tests =
    testList "Phase4" [
        stateTests
        archiveTests
        queryTests
        uiRenderTests
        publicAccessTests
        themeContrastTests
        sseTests
        notificationTests
        mcpTests
        registryTests
        Tag.needs "Session Process as an OS process (Step 23)" [ Tag.Ports; Tag.Native ] (fun () -> processTests)
        // `Srt` because this is the one suite that lets a real child session pick its own
        // sandbox DEFAULT, and that default has been srt since "confine by default" (#83).
        // Untagged, on a box that cannot build the nested sandbox, the child's environment
        // never reaches Running and the suite's `WaitFor` waits out the whole run's budget —
        // taking every suite after it down with a timeout, which is the least legible way a
        // missing capability could possibly report itself.
        Tag.needs "Session-owned environment across real processes" [ Tag.Ports; Tag.Native; Tag.Srt ] (fun () -> controlRpcTests)
        Tag.needs "Manager→Session notifications over SSE (reverse control leg)" [ Tag.Ports ] (fun () -> notificationStreamTests)
        Tag.needs "MCP server set over SSE (reverse control leg)" [ Tag.Ports ] (fun () -> mcpStreamTests)
        Tag.needs "Session registry stream over SSE (Plan 09)" [ Tag.Ports; Tag.Native ] (fun () -> registryStreamTests)
        Tag.needs "Management UI flow (Step 25)" [ Tag.Ports; Tag.Native ] (fun () -> uiFlowTests)
        Tag.needs "Archiving over the process boundary" [ Tag.Ports; Tag.Native ] (fun () -> archiveFlowTests)
        Tag.needs "Is the session reachable yet (Plan 11)" [ Tag.Ports; Tag.Native ] (fun () -> readinessTests)
        // `Ports` only: a ProcessManager binds its control endpoint on creation, but nothing
        // here launches a child — the invariant is about the moment BEFORE the first launch.
        Tag.needs "Registry writes announce themselves (UX review P0)" [ Tag.Ports ] (fun () -> registryPublishTests)
        Tag.needs "Idle reaping over the process boundary (Plan 11)" [ Tag.Ports; Tag.Native ] (fun () -> reapingTests)
        // `Srt` for the same reason: the packaged child picks the sandbox DEFAULT, and this
        // suite waits on an environment that reached Running and a command that exited 0 —
        // neither of which happens on a box that cannot build the nested sandbox.
        Tag.needs "Executable composition (Step 27/28)" [ Tag.Ports; Tag.Native; Tag.Srt ] (fun () -> compositionTests)
        Tag.needs "Telemetry over the process boundary (Plan 04)" [ Tag.Ports; Tag.Native ] (fun () -> telemetryTests)
    ]
