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
      Port = None }

let private twoSessions : ManagerState =
    { Version = ManagerState.currentVersion
      Sessions = [ record "alpha" "Alpha work"; record "beta" "Beta work" ] }

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
            Expect.equal decoded { Version = 1; Sessions = [ record "alpha" "Alpha work" ] } "known fields decode; unknown fields are ignored"

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
                    | ProcessManager.Running (p, pid) ->
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
                    | ProcessManager.Running (_, pid) -> pid
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

[<Emit("process.env[$0] = $1")>]
let private setEnv (name: string) (value: string) : unit = Fable.Core.Util.jsNative

[<Emit("delete process.env[$0]")>]
let private unsetEnv (name: string) : unit = Fable.Core.Util.jsNative

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

/// Start a bare control server over the given secret→(session, capabilities) table, plus
/// the real notification and MCP hubs wired to their SSE routes. Returns both hubs so a
/// test can push down the same wires the Manager uses.
let private startControlServer (secrets: (string * SessionId * SessionEnvironmentCapabilities) list) : Async<Interop.HttpServer * string * NotificationHub.NotificationHub<SessionNotification> * RetainedHub.RetainedHub<McpToolList>> =
    async {
        let table =
            secrets
            |> List.map (fun (secret, sessionId, capabilities) ->
                let caller : Control.ControlCaller = { SessionId = sessionId; Capabilities = Some capabilities; Users = Set.empty; Peers = Set.empty }
                secret, caller)
            |> Map.ofList
        let hub = NotificationHub.create ()
        let mcp = RetainedHub.create McpToolList.empty
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
    testList "Authority over the control RPC (Step 24)" [
        testCaseAsync "Step 11's rejections hold across the wire; output streams in order; rejections never reach the backend" <|
            async {
                let sessionA = SessionId.create "rpc-session-a" |> expect
                let sessionB = SessionId.create "rpc-session-b" |> expect
                let registry = Authority.ContainerRegistry ()
                let recorder = InMemoryBackend.Recorder ()
                let scriptedExec : CommandRequest -> (CommandOutputChunk -> unit) -> Async<CommandResult> =
                    fun command onChunk ->
                        async {
                            onChunk { CommandId = command.CommandId; Stream = Stdout; Text = "one" }
                            onChunk { CommandId = command.CommandId; Stream = Stderr; Text = "warn" }
                            onChunk { CommandId = command.CommandId; Stream = Stdout; Text = "two" }
                            return CommandSucceeded 0
                        }
                let backend = InMemoryBackend.create recorder scriptedExec
                let grant = Authority.grant registry backend
                let! server, url, _, _ = startControlServer [ "secret-a", sessionA, grant sessionA; "secret-b", sessionB, grant sessionB ]

                let capsA = ControlClient.capabilities url "secret-a"
                let capsB = ControlClient.capabilities url "secret-b"
                let request =
                    { CommandId = CommandId.create "rpc-cmd" |> expect
                      Executable = "echo"
                      Arguments = [ "ok" ]
                      WorkingDirectory = None
                      Environment = Map.empty
                      Timeout = None }

                // The happy path: start + execute over the wire, chunks in order.
                let! started = capsA.StartContainer EnvironmentSpec.localProcess
                let handle = match started with ContainerStarted h -> h | r -> failwithf "start failed: %A" r
                let mutable chunks : (OutputStream * string) list = []
                let! result = capsA.Execute handle request (fun c -> chunks <- chunks @ [ c.Stream, c.Text ])
                Expect.equal result (CommandSucceeded 0) "the command ran through the RPC"
                Expect.equal chunks [ Stdout, "one"; Stderr, "warn"; Stdout, "two" ] "chunks streamed in order across the wire"

                // A forged secret gets nothing.
                let mallory = ControlClient.capabilities url "stolen-secret"
                match! mallory.StartContainer EnvironmentSpec.localProcess with
                | ContainerStartFailed reason -> Expect.isTrue (reason.Contains "401") "rejected at the door"
                | ContainerStarted _ -> failwith "a forged secret must not start containers"

                // Cross-session use of A's handle through B's secret is rejected by the
                // registry — before the backend is reached.
                let executedBefore = recorder.Executed
                match! capsB.Execute handle request ignore with
                | CommandExecutionFailed reason -> Expect.isTrue (reason.Contains "session") "rejected as cross-session"
                | other -> failwithf "expected rejection, got %A" other
                Expect.equal recorder.Executed executedBefore "the backend was never reached"

                // A fabricated handle is unknown; a stopped container cannot exec.
                let forged = ContainerHandle.create sessionB "ctr-fabricated"
                match! capsB.Execute forged request ignore with
                | CommandExecutionFailed reason -> Expect.isTrue (reason.Contains "unknown") "fabricated handles are unknown"
                | other -> failwithf "expected rejection, got %A" other
                let! stopped = capsA.StopContainer handle
                Expect.equal stopped ContainerStopped "stop crossed the wire"
                match! capsA.Execute handle request ignore with
                | CommandExecutionFailed reason -> Expect.isTrue (reason.Contains "not running") "a stopped container cannot exec"
                | other -> failwithf "expected rejection, got %A" other

                server.close ignore
            }

        testCaseAsync "a child Session Process exercises the capability end to end (diagnostic agent across real processes)" <|
            async {
                let dataDir =
                    sprintf "tests/Yession.Tests/out/.data/rpc-%d" (int (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) % 1000000)
                let registry = Authority.ContainerRegistry ()
                let backend = Backends.LocalProcessBackend.create ()
                let! pm =
                    ProcessManager.create
                        { ProcessManager.Options.defaults dataDir nodePath [ "app/SessionMain.js" ] with
                            Strategy = Some Strategy.localhost
                            Grant = Some (Authority.grant registry backend) }
                let record = pm.CreateSession "rpc-child" "RPC child" |> expect

                // The child inherits our environment: run its built-in diagnostic agent.
                setEnv "YESSION_AGENT" "diagnostic"
                let! launched = pm.Launch record.SessionId
                unsetEnv "YESSION_AGENT"
                let port = launched |> expect

                let! opened = OidcHttp.openSession (sprintf "http://127.0.0.1:%d" port)
                let! a = connectClient (sprintf "http://127.0.0.1:%d/signal" port) opened.PeerToken "ada" "Ada"
                do! compose a a.Hello.PeerId "run the diagnostic"
                a.Connection.SendDraft a.Hello.PeerId

                // Everything below happened ACROSS process boundaries: the child asked
                // the Manager (this test process) over the control RPC; the Manager's
                // authority + engine ran the command; events streamed back to a client.
                do! a.Runner.WaitFor (fun m ->
                        (m.Conversation.Items
                         |> List.exists (fun i -> i.Author = ActorRef.Agent && i.Status = Complete && i.Body.Contains "diagnostic-ok"))
                        && (match m.Environment with EnvironmentRunning _ -> true | _ -> false)
                        && (m.Commands.Entries
                            |> List.exists (fun e ->
                                e.Status = CommandFinished (CommandSucceeded 0)
                                && (e.Output |> List.exists (fun (_, text) -> text.Contains "diagnostic-ok")))))

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
      Port = None }

let private uiRenderTests =
    testList "Management UI rendering (Step 25)" [
        testCase "a stopped session's row offers Launch; a running one offers Stop and the open link" <| fun () ->
            let stopped = ManagerUi.sessionRow PublicAccess.Loopback { Record = uiRecord; Status = ProcessManager.NotRunning }
            Expect.isTrue (stopped.Contains (Dom.attr Dom.Manager.launch "ui-render")) "stopped rows can launch (button carries the session id)"
            Expect.isTrue (stopped.Contains "UI &lt;Render&gt;") "display names are escaped"
            let running = ManagerUi.sessionRow PublicAccess.Loopback { Record = uiRecord; Status = ProcessManager.Running (8199, 42) }
            Expect.isTrue (running.Contains (Dom.attr Dom.Manager.stop "ui-render")) "running rows can stop"
            Expect.isTrue (running.Contains "href=\"http://127.0.0.1:8199/\"") "the open link is a plain URL to the child's port (no token — access is the OIDC bounce)"
            Expect.isTrue (running.Contains (Dom.attr Dom.Manager.session "ui-render")) "the row is a poll unit keyed by session id"
            let crashed = ManagerUi.sessionRow PublicAccess.Loopback { Record = uiRecord; Status = ProcessManager.Exited (Some 1) }
            Expect.isTrue (crashed.Contains (Dom.attr Dom.Manager.status Dom.Manager.statusExited)) "a crash is visible"
            Expect.isTrue (crashed.Contains (Dom.attr Dom.Manager.launch "ui-render")) "a crashed session can relaunch"

        testCase "the page is self-contained: an inline script drives it, no external sources" <| fun () ->
            let html = ManagerUi.page "app.css" PublicAccess.Loopback [ { Record = uiRecord; Status = ProcessManager.NotRunning } ]
            Expect.isTrue (html.Contains "<script>") "an inline script drives the UI (no bundle)"
            Expect.isTrue (html.Contains "/sessions/") "the inline script talks to the fragment routes"
            Expect.isFalse (html.Contains "src=\"http") "no external/CDN scripts (local-first)"
            Expect.isTrue (html.Contains Dom.Manager.createSession) "the create form renders"
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
            for fg in [ "ink"; "ink-dim"; "ink-faint"; "blue"; "green"; "err" ] do
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
[<Emit("fetch($0).then(async r => ({ status: r.status, cacheControl: '', body: await r.text() }))")>]
let private getReply (url: string) : JS.Promise<obj> = Fable.Core.Util.jsNative

[<Emit("$0.status")>]
let private statusOfReply (reply: obj) : int = Fable.Core.Util.jsNative

[<Emit("$0.body")>]
let private bodyOfReply (reply: obj) : string = Fable.Core.Util.jsNative

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
                let! css = Interop.getText (baseUrl + "/app.css") |> Async.AwaitPromise
                Expect.isTrue (css.Length > 500) "the shared local stylesheet serves from the endpoint (no CDN)"

                // Create over the form endpoint.
                let! created = postForm (baseUrl + "/sessions") "id=ui-1&name=UI+One" |> Async.AwaitPromise
                Expect.equal (statusOfReply created) 200 "created"
                Expect.isTrue ((bodyOfReply created).Contains (Dom.attr Dom.Manager.session "ui-1")) "the refreshed table holds the new session"
                let! duplicate = postForm (baseUrl + "/sessions") "id=ui-1&name=Again" |> Async.AwaitPromise
                Expect.equal (statusOfReply duplicate) 400 "duplicates are rejected"

                // Launch from the UI; the fragment reflects it and the child REALLY serves.
                let! launched = postForm (baseUrl + "/sessions/ui-1/launch") "" |> Async.AwaitPromise
                let row = bodyOfReply launched
                Expect.isTrue (row.Contains (Dom.attr Dom.Manager.status Dom.Manager.statusRunning)) "the row shows running"
                let sessionPort =
                    match (pm.TryFind (SessionId.create "ui-1" |> expect)).Value.Status with
                    | ProcessManager.Running (port, _) -> port
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
                    | ProcessManager.Running (_, pid) -> pid
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

                let! _ = postForm (baseUrl + "/sessions") "id=open-1&name=Open+One" |> Async.AwaitPromise

                // Stopped: /open has to start it before there is any address to give.
                Expect.equal (pm.TryFind sessionId).Value.Status ProcessManager.NotRunning "created, not launched"
                let! opened = Interop.getText (baseUrl + "/sessions/open-1/open") |> Async.AwaitPromise
                let launchedPort =
                    match (pm.TryFind sessionId).Value.Status with
                    | ProcessManager.Running (port, _) -> port
                    | other -> failwithf "expected /open to have launched it, got %A" other
                Expect.isTrue
                    (opened.Contains (sprintf "http://127.0.0.1:%d/" launchedPort))
                    "the landing page names the session's own address"

                // Already running: /open is not a relaunch — it hands back the same address,
                // which is what makes the URL safe to keep clicking.
                let! again = Interop.getText (baseUrl + "/sessions/open-1/open") |> Async.AwaitPromise
                match (pm.TryFind sessionId).Value.Status with
                | ProcessManager.Running (port, _) ->
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
        testCaseAsync "an idle session is reaped for the RIGHT reason, and comes back on the same pinned port" <|
            async {
                let dataDir =
                    sprintf "tests/Yession.Tests/out/.data/reap-%d" (int (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) % 1000000)
                // Every lifecycle event the Manager emits, so the reap's REASON is read from
                // the telemetry that operators read, not inferred from the session being gone.
                let events = ResizeArray<string * (string * obj) list> ()
                let! pm =
                    ProcessManager.createWithUi
                        { ProcessManager.Options.defaults dataDir nodePath [ "app/SessionMain.js" ] with
                            Strategy = Some Strategy.localhost
                            SessionPorts = SessionPorts.create 0 "8430-8439" |> expect
                            IdleTimeout = Some (TimeSpan.FromSeconds 3.0)
                            OnEvent = fun name attrs -> lock events (fun () -> events.Add (name, attrs)) }
                        (Some ManagerUi.tryHandle)
                let baseUrl = sprintf "http://127.0.0.1:%d" pm.EndpointPort.Value
                let sessionId = SessionId.create "reap-1" |> expect
                let! _ = postForm (baseUrl + "/sessions") "id=reap-1&name=Reap+One" |> Async.AwaitPromise

                // Pinned at creation, from the configured range.
                let pinned =
                    match (pm.TryFind sessionId).Value.Record.Port with
                    | Some port -> port
                    | None -> failwith "a pinned deployment must give the session an address"
                Expect.isTrue (pinned >= 8430 && pinned <= 8439) (sprintf "port %d should come from the range" pinned)

                let! launched = pm.Launch sessionId
                Expect.equal launched (Ok pinned) "the session listens on its pinned port"

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

                // And the way back returns it to the SAME address — the property the whole
                // pinning half exists for, since the browser partitions storage by origin.
                let! reopened = Interop.getText (baseUrl + "/sessions/reap-1/open") |> Async.AwaitPromise
                Expect.isTrue
                    (reopened.Contains (sprintf "http://127.0.0.1:%d/" pinned))
                    "reopening lands on the same origin the reaped session had"
                match (pm.TryFind sessionId).Value.Status with
                | ProcessManager.Running (port, _) -> Expect.equal port pinned "relaunched on its pinned port"
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
[<Emit("$0(process.execPath, [$1, '--auth', 'localhost'], { env: { ...process.env, YESSION_SESSION_MAIN: $3, ...Object.fromEntries($2) }, stdio: ['pipe', 'pipe', 'inherit'] })")>]
let private spawnBundle (spawn: obj) (managerJs: string) (env: (string * string) array) (sessionJs: string) : obj = Fable.Core.Util.jsNative

[<Emit("$0.stdout.on('data', $1)")>]
let private onStdout (child: obj) (handler: obj -> unit) : unit = Fable.Core.Util.jsNative

[<Emit("typeof $0 === 'string' ? $0 : $0.toString('utf8')")>]
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
                      "YESSION_MANAGER_PORT", "0"
                      // Children inherit this: the built-in diagnostic agent exercises
                      // the control RPC on the shipped binaries, credential-free.
                      "YESSION_AGENT", "diagnostic" ]

                let! manager = startPackagedManager env

                // Create and launch a session from the management UI.
                let! created = postForm (manager.UiUrl + "sessions") "id=composed&name=Composed" |> Async.AwaitPromise
                Expect.equal (statusOfReply created) 200 "created via the UI"
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
                        && (m.Commands.Entries |> List.exists (fun e -> e.Status = CommandFinished (CommandSucceeded 0))))
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
                setEnv "YESSION_AGENT" "usage-probe"
                setEnv "OTEL_LOGS_EXPORTER" "otlp"
                setEnv "OTEL_EXPORTER_OTLP_ENDPOINT" stub.Url
                let! launched = pm.Launch record.SessionId
                unsetEnv "YESSION_AGENT"
                unsetEnv "OTEL_LOGS_EXPORTER"
                unsetEnv "OTEL_EXPORTER_OTLP_ENDPOINT"
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

// A valid-but-inert capability set: notifications only need the secret to resolve past the
// control endpoint's 401 gate, not any real environment authority.
let private stubCapabilities : SessionEnvironmentCapabilities =
    { StartContainer = fun _ -> async { return ContainerStartFailed "stub" }
      StopContainer = fun _ -> async { return ContainerStopped }
      Execute = fun _ _ _ -> async { return CommandExecutionFailed "stub" } }

let private notificationStreamTests =
    testList "Manager→Session notifications over SSE (reverse control leg)" [
        testCaseAsync "a pushed notification reaches the subscribed session, is scoped to it, and stops on cancel" <|
            async {
                let! server, url, hub, _ =
                    startControlServer
                        [ "secret-a", (SessionId.create "sse-a" |> expect), stubCapabilities
                          "secret-b", (SessionId.create "sse-b" |> expect), stubCapabilities ]

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
    testList "MCP tool stream: codec & hub" [
        testCase "a tool list round-trips through the control wire codec, inputSchema intact" <| fun () ->
            let original = { Tools = [ searchTool; { Name = "noop"; Title = None; Description = None; InputSchema = "{}" } ] }
            let roundTripped =
                ControlWire.toString ControlWire.mcpToolList original
                |> ControlWire.fromString ControlWire.mcpToolList
                |> expect
            Expect.equal roundTripped original "tool list round-trip is identity (schema stays a JSON object, optionals preserved)"

        testCase "inputSchema is a real JSON object on the wire, not a quoted string" <| fun () ->
            let json = ControlWire.toString ControlWire.mcpToolList { Tools = [ searchTool ] }
            Expect.isTrue (json.Contains "\"inputSchema\":{") "the schema serialises as an embedded object"

        testCase "the hub hands a new subscriber the current list at once, then every change" <| fun () ->
            let hub = RetainedHub.create McpToolList.empty
            let mutable received : McpToolList list = []
            let _ = hub.Register (fun l -> received <- received @ [ l ])
            // The retained snapshot: an empty list arrives immediately on subscribe.
            Expect.equal received [ McpToolList.empty ] "the subscriber gets the current (empty) list at once"

            hub.Publish { Tools = [ searchTool ] }
            Expect.equal (List.last received) { Tools = [ searchTool ] } "a publish pushes the new list"
            Expect.equal (hub.Current ()) { Tools = [ searchTool ] } "the hub retains the latest list"

            // A LATER subscriber gets the retained list as its initial snapshot, no publish needed.
            let mutable late : McpToolList list = []
            let unsubLate = hub.Register (fun l -> late <- late @ [ l ])
            Expect.equal late [ { Tools = [ searchTool ] } ] "a late subscriber gets the retained list immediately"

            // Unsubscribe stops delivery to that sink only.
            unsubLate.Stop ()
            hub.Publish McpToolList.empty
            Expect.equal (List.last late) { Tools = [ searchTool ] } "the unsubscribed sink received nothing further"
            Expect.equal (List.last received) McpToolList.empty "the still-subscribed sink got the change"
    ]

let private mcpStreamTests =
    testList "MCP tool stream over SSE (reverse control leg)" [
        testCaseAsync "a subscriber gets the current list on connect, then every change, and stops on cancel" <|
            async {
                let! server, url, _, mcp =
                    startControlServer [ "secret-a", (SessionId.create "sse-mcp" |> expect), stubCapabilities ]
                // Seed a list before anyone subscribes: a connecting session must still see it.
                mcp.Publish { Tools = [ searchTool ] }

                let mutable received : McpToolList list = []
                let cancel = ControlClient.subscribeMcp url "secret-a" (fun l -> received <- received @ [ l ])

                // The retained snapshot arrives on connect without any further publish.
                let rec waitFor (remaining: int) =
                    async {
                        if not (List.isEmpty received) || remaining <= 0 then return ()
                        else
                            do! Async.Sleep 50
                            return! waitFor (remaining - 1)
                    }
                do! waitFor 60
                Expect.isTrue (not (List.isEmpty received)) "the initial (retained) list arrived on connect"
                Expect.equal (List.head received) { Tools = [ searchTool ] } "the initial snapshot is the current list, decoded across the wire"

                // A subsequent change is pushed to the live subscriber.
                let before = List.length received
                mcp.Publish { Tools = [ searchTool; { Name = "fetch"; Title = None; Description = None; InputSchema = "{}" } ] }
                let rec waitGrow (remaining: int) =
                    async {
                        if List.length received > before || remaining <= 0 then return ()
                        else
                            do! Async.Sleep 50
                            return! waitGrow (remaining - 1)
                    }
                do! waitGrow 60
                Expect.equal (List.last received |> fun l -> List.length l.Tools) 2 "the change was pushed to the live subscriber"

                // Cancel closes the stream; later changes never arrive.
                cancel.Stop ()
                do! Async.Sleep 200
                let settled = List.length received
                mcp.Publish McpToolList.empty
                do! Async.Sleep 200
                Expect.equal (List.length received) settled "after cancel, no further lists arrive"

                server.close ignore
            }
    ]

// -----------------------------------------------------------------------------
// Session registry stream (docs/plans/09) — the Running set as full-snapshot
// frames, published to whoever serves sessions (an operator's serving binding).
// Codec, hub, and public-origin assembly are cheap tier; the SSE stream over a
// real Manager + real children is verify tier.
// -----------------------------------------------------------------------------

let private registryEntry (id: string) (name: string) (port: int) (pid: int) : ControlWire.SessionRegistryEntry =
    { Id = SessionId.create id |> expect
      Name = name
      Port = port
      Pid = pid }

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
                [ { Record = record "alpha" "Alpha work"; Status = ProcessManager.Running (54321, 4242) }
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

        testCase "a running row's open link carries the configured public address" <| fun () ->
            let access = PublicAccess.create "https://home.example.ts.net" "http://home.example.ts.net:{port}" |> expect
            let running = ManagerUi.sessionRow access { Record = uiRecord; Status = ProcessManager.Running (8199, 42) }
            Expect.isTrue (running.Contains "href=\"http://home.example.ts.net:8199/\"") "the open link is followable from a remote browser"
    ]

// --- Public access: the deployment's two addresses as one value (docs/plans/09) ------------

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
            let fronted = PublicAccess.create "https://yession.example.com" "" |> expect
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

        testCase "a fronted Manager with loopback sessions is a legal deployment" <| fun () ->
            let access = PublicAccess.create "https://yession.example.com/" "" |> expect
            Expect.equal (PublicAccess.managerUrl access) (Some "https://yession.example.com") "trailing slash normalised"
            Expect.equal
                (PublicAccess.sessionAddress sessionId 54321 access)
                { Url = "http://127.0.0.1:54321"; Mount = "" }
                "manage remotely, use sessions on the host — sessions stay loopback"

        testCase "sessions fronted without the Manager is refused, not warned about" <| fun () ->
            // Always broken: the session bounces its users to the Manager to log in, so a
            // remote browser would be sent to 127.0.0.1. The one combination with no
            // constructor.
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
            Expect.isTrue
                ((errorOf "https://example.com/yession" "").Contains "origin root")
                "a path prefix on the Manager is refused"
            Expect.isTrue
                ((errorOf "https://{id}.example.com" "").Contains "placeholder")
                "the Manager is one address, so a placeholder means nothing"
            Expect.isTrue
                ((errorOf "example.com" "").Contains "http://")
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
                setEnv "YESSION_MANAGER_URL" managerUrl
                setEnv "YESSION_SESSION_URL" sessionUrl
                try
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
                finally
                    unsetEnv "YESSION_MANAGER_URL"
                    unsetEnv "YESSION_SESSION_URL"

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
        uiRenderTests
        publicAccessTests
        themeContrastTests
        sseTests
        notificationTests
        mcpTests
        registryTests
        Tag.needs "Session Process as an OS process (Step 23)" [ Tag.Ports; Tag.Native ] (fun () -> processTests)
        Tag.needs "Authority over the control RPC (Step 24)" [ Tag.Ports; Tag.Native ] (fun () -> controlRpcTests)
        Tag.needs "Manager→Session notifications over SSE (reverse control leg)" [ Tag.Ports ] (fun () -> notificationStreamTests)
        Tag.needs "MCP tool stream over SSE (reverse control leg)" [ Tag.Ports ] (fun () -> mcpStreamTests)
        Tag.needs "Session registry stream over SSE (Plan 09)" [ Tag.Ports; Tag.Native ] (fun () -> registryStreamTests)
        Tag.needs "Management UI flow (Step 25)" [ Tag.Ports; Tag.Native ] (fun () -> uiFlowTests)
        Tag.needs "Idle reaping over the process boundary (Plan 11)" [ Tag.Ports; Tag.Native ] (fun () -> reapingTests)
        Tag.needs "Executable composition (Step 27/28)" [ Tag.Ports; Tag.Native ] (fun () -> compositionTests)
        Tag.needs "Telemetry over the process boundary (Plan 04)" [ Tag.Ports; Tag.Native ] (fun () -> telemetryTests)
    ]
