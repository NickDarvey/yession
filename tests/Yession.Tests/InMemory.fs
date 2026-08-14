module Yession.Tests.InMemory

// Cheap-tier end-to-end coverage that drives the REAL Session Process host over an
// in-memory channel pair (`host.Connect` + `App.connect`) instead of a WebRTC data channel.
// Same production code path — the peer handshake, the doc State relay, the queue drain, the
// cursor-presence relay, and the title→Manager report — but with no WebRTC, no HTTP, and no
// native addon, so it runs on every PR. Event-driven throughout via model waiters; the one
// host-side signal (the Manager report) is awaited through a one-shot continuation.
//
// These close the coverage gap left by the collaborative-title feature: the presence relay
// and the title report were previously reachable only through the verify tier.

open Fable.Pyxpecto
open Ylmish
open Yession.Domain
open Yession.App
open Yession.Host
open Yession.SessionProcess
open Yession.Tests.Support

let private sid () = SessionId.create "in-memory-session" |> expect

let tests =
    testList "In-memory transport (cheap E2E through the real Host)" [
        testCaseAsync "two clients converge on the title through the Host's State relay" <|
            async {
                let! host = Host.start (sid ()) 0
                let! a = connectInMemoryClient host "ada" "Ada"
                let! b = connectInMemoryClient host "bob" "Bob"
                // Ada names the session; the edit rides a State frame through the Host to Bob.
                a.Runner.Dispatch (user (EditTitleMsg (Text.insert 0 "launch plan" (a.Runner.Model ()).Synced.Title)))
                do! b.Runner.WaitFor (fun m -> Text.toString m.Synced.Title = "launch plan")
                Expect.equal (Text.toString (b.Runner.Model ()).Synced.Title) "launch plan" "B sees A's title via the Host relay"
                do! host.Stop ()
            }

        testCaseAsync "a sent rich draft drains through the Host into both timelines as its markdown body" <|
            async {
                let! host = Host.start (sid ()) 0
                let! a = connectInMemoryClient host "ada" "Ada"
                let! b = connectInMemoryClient host "bob" "Bob"
                let ada = a.Hello.PeerId
                // Ada composes a rich body and sends; the idle Host drains it to exactly one
                // MessageSent, snapshotted as MARKDOWN — visible in BOTH timelines (the sender's
                // too), queue cleared. Pins the send atomicity through the real drain: a split
                // send would either snapshot an empty body OR let the drain's removal Set clobber
                // the sender's freshly-consumed conversation (the top-level-root regression the
                // WebRTC E2E only caught in the verify tier).
                do! compose a ada "# ship it"
                a.Connection.SendDraft ada
                let settled (m: ClientModel) =
                    Map.isEmpty m.Synced.Queue
                    && (m.Conversation.Items |> List.map (fun i -> i.Body)) = [ "# ship it" ]
                do! a.Runner.WaitFor settled
                do! b.Runner.WaitFor settled
                do! host.Stop ()
            }

        testCaseAsync "two peers co-edit one draft and either can send it — once" <|
            async {
                let! host = Host.start (sid ()) 0
                let! a = connectInMemoryClient host "ada" "Ada"
                let! b = connectInMemoryClient host "bob" "Bob"
                let ada = a.Hello.PeerId
                // Ada starts a message; Bob's composer opens on it rather than on a rival blank.
                do! compose a ada "we should ask it to"
                do! b.Runner.WaitFor (fun m -> Map.containsKey ada m.Synced.Drafts)
                Expect.equal
                    (ClientModel.composerTarget (b.Runner.Model ())) ada
                    "the draft already in flight IS Bob's composer"

                // Bob edits ADA's draft — the co-edit the read-only mirror used to forbid — and
                // the words converge on both replicas.
                do! compose b ada "we should ask it to re-run the migration"
                do! a.Runner.WaitFor (fun _ -> draftBody a ada = Some "we should ask it to re-run the migration")

                // Bob sends Ada's draft. One message, attributed to Ada, and the slot clears
                // everywhere: the send key came from the draft, not from Bob.
                b.Connection.SendDraft ada
                let settled (m: ClientModel) =
                    Map.isEmpty m.Synced.Queue
                    && not (Map.containsKey ada m.Synced.Drafts)
                    && (m.Conversation.Items |> List.map (fun i -> i.Body)) = [ "we should ask it to re-run the migration" ]
                do! a.Runner.WaitFor settled
                do! b.Runner.WaitFor settled
                let! page = host.Log.Read None System.Int32.MaxValue
                let sent =
                    page.Events
                    |> List.choose (fun e -> match e.Event with MessageSent m -> Some m | _ -> None)
                match sent with
                | [ message ] ->
                    Expect.equal message.Author (PeerRef ada) "attributed to the peer whose draft it was"
                    Expect.equal message.Body "we should ask it to re-run the migration" "the co-written body"
                | other -> failwithf "expected exactly one MessageSent, got %A" other
                do! host.Stop ()
            }

        testCaseAsync "a draft box appears only for a peer that has typed, and goes when its composer empties" <|
            async {
                let! host = Host.start (sid ()) 0
                let! a = connectInMemoryClient host "ada" "Ada"
                let! b = connectInMemoryClient host "bob" "Bob"
                let ada = a.Hello.PeerId
                // Ada types: her slot publishes and reaches Bob through the Host relay. Bob's
                // composer renders one box per slot, so his slot map IS what he shows.
                do! compose a ada "thinking out loud"
                do! b.Runner.WaitFor (fun m -> Map.containsKey ada m.Synced.Drafts)
                // Bob has been connected the whole time and never typed. Before publication
                // followed the body, merely mounting a composer published a slot — so every peer
                // that had ever opened the session showed an empty draft box on everyone's
                // composer, forever.
                Expect.equal
                    ((b.Runner.Model ()).Synced.Drafts |> Map.toList |> List.map fst)
                    [ ada ]
                    "the idle peer contributes no draft box"
                Expect.equal
                    ((a.Runner.Model ()).Synced.Drafts |> Map.toList |> List.map fst)
                    [ ada ]
                    "and none for itself on its own replica"
                // Ada empties her composer: the box goes away on Bob too.
                do! clearComposer a ada
                do! b.Runner.WaitFor (fun m -> not (Map.containsKey ada m.Synced.Drafts))
                do! host.Stop ()
            }

        testCaseAsync "discarding a draft empties the composer, not just the slot" <|
            async {
                let! host = Host.start (sid ()) 0
                let! a = connectInMemoryClient host "ada" "Ada"
                let! b = connectInMemoryClient host "bob" "Bob"
                let ada = a.Hello.PeerId
                do! compose a ada "never mind, wrong session"
                do! b.Runner.WaitFor (fun m -> Map.containsKey ada m.Synced.Drafts)

                // Discard is what the composer's ✕ does. It has to reach the BODY: the slot is
                // republished by the publication rule from whatever the body holds, so removing
                // the slot alone left the words on screen and the next keystroke put the draft
                // straight back — the button changed nothing a person could see.
                a.Connection.DiscardDraft ada
                do! a.Runner.WaitFor (fun m -> not (Map.containsKey ada m.Synced.Drafts))
                do! b.Runner.WaitFor (fun m -> not (Map.containsKey ada m.Synced.Drafts))
                Expect.equal (draftBody a ada) (Some "") "the composer is empty on the author's replica"
                Expect.equal (draftBody b ada) (Some "") "and on everyone else's"

                // And it stays discarded: with the body empty, the rule has nothing to publish.
                do! compose a ada "a fresh thought"
                Expect.equal (draftBody a ada) (Some "a fresh thought") "typing again starts a new draft, not the discarded one"
                do! host.Stop ()
            }

        testCaseAsync "a peer's cursor presence relays to others in any field and clears on disconnect" <|
            async {
                let! host = Host.start (sid ()) 0
                let! a = connectInMemoryClient host "ada" "Ada"
                let! b = connectInMemoryClient host "bob" "Bob"
                // Ada's caret is in the title; the Host relays the presence frame to Bob.
                let titleFocus : Focus = { Field = Title; Pos = { Anchor = "AQI="; Head = "AwQ=" } }
                a.Connection.ReportPresence (Some titleFocus)
                do! b.Runner.WaitFor (fun m -> Map.containsKey a.Hello.PeerId m.Presence)
                Expect.equal
                    (Map.tryFind a.Hello.PeerId (b.Runner.Model ()).Presence |> Option.map (fun c -> c.Focus))
                    (Some titleFocus)
                    "B sees A's title caret with the reported anchor/head"
                // Ada moves into her own draft body: the field changes, and it still relays.
                let bodyFocus : Focus = { Field = DraftBody a.Hello.PeerId; Pos = { Anchor = "BQY="; Head = "BQY=" } }
                a.Connection.ReportPresence (Some bodyFocus)
                do! b.Runner.WaitFor (fun m ->
                        Map.tryFind a.Hello.PeerId m.Presence |> Option.map (fun c -> c.Focus) = Some bodyFocus)
                Expect.equal
                    (Map.tryFind a.Hello.PeerId (b.Runner.Model ()).Presence |> Option.map (fun c -> c.Focus.Field))
                    (Some (DraftBody a.Hello.PeerId))
                    "B sees A's caret move into the draft-body field"
                // Ada leaves; the Host clears her cursor on the remaining peer.
                do! a.Channel.Close ()
                do! b.Runner.WaitFor (fun m -> not (Map.containsKey a.Hello.PeerId m.Presence))
                Expect.isFalse (Map.containsKey a.Hello.PeerId (b.Runner.Model ()).Presence) "A's caret vanishes when she disconnects"
                do! host.Stop ()
            }

        // The door into a NAMED sandbox (Plan 15, stage 2), end to end through the real
        // Host. Starting a sandbox is only worth anything if something can run in it, and
        // `execute_command` is the one door — so this is the test that makes named
        // sandboxes a feature rather than a listing: a command targeted at `test` runs in
        // the `test` sandbox's environment and NOT in `default`.
        testCaseAsync "a command targeted at a named sandbox runs in that sandbox, not the default one" <|
            async {
                let spawnedIn = ResizeArray<string> ()
                let environmentNamed (name: string) : SessionEnvironment.SessionEnvironment =
                    { Ensure = fun _ _ -> async { return EnvironmentAvailable }
                      Spawn =
                        fun _ onChunk ->
                            async {
                                spawnedIn.Add name
                                onChunk (Stdout, "ran in " + name + "\n")
                                return
                                    Ok
                                        { WriteStdin = ignore
                                          CloseStdin = ignore
                                          Kill = ignore
                                          Exited = async { return SandboxExited 0 } }
                            }
                      SpawnPty = fun _ _ _ _ -> async { return Error "no pty in this fixture" }
                      Stop = fun () -> async { return () }
                      CurrentRef = fun () -> Some name }
                let makeSandboxes log =
                    WorkSandboxes.create
                        { Backend = "scripted"
                          Credentials = []
                          Create = fun name _ -> Ok (environmentNamed (SandboxName.value name))
                          Log = log
                          Clock = fun () -> System.DateTimeOffset (2026, 1, 1, 0, 0, 0, System.TimeSpan.Zero) }
                    |> expect
                let! host = Host.startWithEnvironment None (Some makeSandboxes) None (sid ()) 0

                let caller : WorkSandboxes.SandboxCaller = { Actor = ActorRef.Agent; Credential = ActorRef.Agent; ApprovedBy = None }
                let test = SandboxName.create "test" |> expect
                let! started = host.Sandboxes.Ensure caller test []
                Expect.isTrue (Result.isOk started) "the sandbox starts"

                match! host.TerminalCommands.Execute (Some (InSandbox test)) "echo hi" false with
                | Error e -> failwithf "the command did not run: %s" e
                | Ok outcome ->
                    Expect.equal outcome.Status (TerminalCommandRan (CommandSucceeded 0)) "it ran"
                    Expect.isTrue (outcome.OutputTail.Contains "ran in test") "and it ran in the named sandbox"
                Expect.equal (List.ofSeq spawnedIn) [ "test" ] "nothing was spawned in default"

                // And the terminal it opened records which sandbox it belongs to, so a
                // replayed log brings it back up in the same one rather than in default.
                let! page = host.Log.Read None 200
                match page.Events |> List.choose (fun e -> match e.Event with SessionEvent.TerminalOpened t -> Some t | _ -> None) with
                | [ opened ] -> Expect.equal opened.Sandbox (Some test) "the terminal is recorded as belonging to the named sandbox"
                | other -> failwithf "expected exactly one terminal to have opened, got %A" other
                do! host.Stop ()
            }

        // Terminals (Plan 13), end to end through the real Host: the command a peer types
        // in the composer, the `OpenTerminal` command frame, the drain, the block events,
        // and the transcript records broadcast back as `Terminal` frames. The sandbox is
        // scripted (a `SessionEnvironment` record), so this stays in the cheap tier while
        // exercising every seam between the browser client and the Session Process.
        testCaseAsync "a peer opens a terminal, runs a command, and both peers see the block and its output" <|
            async {
                let environment : SessionEnvironment.SessionEnvironment =
                    { Ensure = fun _ _ -> async { return EnvironmentAvailable }
                      Spawn =
                        fun _ onChunk ->
                            async {
                                onChunk (Stdout, "hello from the sandbox\n")
                                return
                                    Ok
                                        { WriteStdin = ignore
                                          CloseStdin = ignore
                                          Kill = ignore
                                          Exited = async { return SandboxExited 0 } }
                            }
                      SpawnPty = fun _ _ _ _ -> async { return Error "no pty in this fixture" }
                      Stop = fun () -> async { return () }
                      CurrentRef = fun () -> Some "scripted" }
                let! host = Host.startWithEnvironment None (Some (fun _ -> WorkSandboxes.singleton "scripted" environment)) None (sid ()) 0
                let! a = connectInMemoryClient host "ada" "Ada"
                let! b = connectInMemoryClient host "bob" "Bob"

                // Opening is a COMMAND — the terminal's id is the Process's to mint — and it
                // arrives at every peer as an event, which is why B learns about it too.
                a.Connection.OpenTerminal "build"
                let hasOpenTerminal (m: ClientModel) = not (List.isEmpty (TerminalProjection.openTerminals m.Terminals))
                do! a.Runner.WaitFor hasOpenTerminal
                do! b.Runner.WaitFor hasOpenTerminal
                let terminal =
                    (TerminalProjection.openTerminals (a.Runner.Model ()).Terminals |> List.head).TerminalId

                // The publication rule, wired per open terminal exactly as the browser wires
                // it: Ada's slot appears when her command line has content and goes when it
                // empties. Without it a composer would never publish, and nobody could send.
                TerminalDraftSlot.follow a.Doc a.Texts terminal a.Hello.PeerId (user >> a.Runner.Dispatch) |> ignore

                // Ada types a command. The composer is a `Y.Text` root, exactly as the
                // browser's input writes it, and the slot publishes off its content.
                TerminalText.setTo a.Texts (BodyKey.terminalDraft terminal a.Hello.PeerId) "echo hello"
                do! b.Runner.WaitFor (fun m -> not (List.isEmpty (ClientModel.terminalDrafts terminal m)))
                Expect.equal
                    (TerminalText.read b.Texts (BodyKey.terminalDraft terminal a.Hello.PeerId))
                    "echo hello"
                    "Bob watches Ada write the command, exactly as he watches her write a message"

                // Sending enqueues it; a human's command needs no approval under the default
                // mode, so the drain runs it straight away.
                a.Connection.SendTerminalDraft terminal a.Hello.PeerId
                let ranSuccessfully (m: ClientModel) =
                    match TerminalProjection.tryFind terminal m.Terminals with
                    | Some view ->
                        view.Blocks
                        |> List.exists (fun b -> b.Command = "echo hello" && b.Status = BlockFinished (CommandSucceeded 0))
                    | None -> false
                do! a.Runner.WaitFor ranSuccessfully
                do! b.Runner.WaitFor ranSuccessfully

                // The OUTPUT arrived over the terminal frames, keyed by transcript sequence —
                // and the block's recorded range is what selects it, on both peers.
                let outputOf (client: Client) =
                    let m = client.Runner.Model ()
                    let view = TerminalProjection.tryFind terminal m.Terminals |> Option.get
                    let block = view.Blocks |> List.find (fun b -> b.Command = "echo hello")
                    TerminalFeed.outputText block.FromSeq (Option.defaultValue 0 block.ToSeq) (ClientModel.terminalFeed terminal m)
                Expect.equal (outputOf a) "hello from the sandbox\n" "Ada sees the output"
                Expect.equal (outputOf b) "hello from the sandbox\n" "and so does Bob"

                // The composer emptied on send, and the queue is clear again.
                Expect.equal
                    (TerminalText.read a.Texts (BodyKey.terminalDraft terminal a.Hello.PeerId))
                    ""
                    "the composer clears on send"
                Expect.isTrue (Map.isEmpty (a.Runner.Model ()).Synced.Pending) "and the entry was consumed"
                do! host.Stop ()
            }

        testCaseAsync "the agent chains: it runs a command, reads the real output, and runs a second on it" <|
            async {
                // THE property PR 1 gave up and stage 3b bought back. `queue_terminal_command`
                // returned before anything had happened, so an agent that needed an answer
                // took `execute_command` — the ungated door — and `ApproveAgent` was
                // unenforceable as a result. One door only works if that door answers.
                let ran = ResizeArray<string> ()
                let environment : SessionEnvironment.SessionEnvironment =
                    { Ensure = fun _ _ -> async { return EnvironmentAvailable }
                      Spawn =
                        fun exec onChunk ->
                            async {
                                let line = exec.Arguments |> List.tryLast |> Option.defaultValue ""
                                ran.Add line
                                onChunk (Stdout, sprintf "ran<%s>" line)
                                return
                                    Ok
                                        { WriteStdin = ignore
                                          CloseStdin = ignore
                                          Kill = ignore
                                          Exited = async { return SandboxExited 0 } }
                            }
                      SpawnPty = fun _ _ _ _ -> async { return Error "no pty in this fixture" }
                      Stop = fun () -> async { return () }
                      CurrentRef = fun () -> Some "scripted" }
                let! host = Host.startWithEnvironment None (Some (fun _ -> WorkSandboxes.singleton "scripted" environment)) None (sid ()) 0
                // No terminal is open, so the FIRST call opens the agent's own — titled with
                // what it is for, and in `AutoRun`, which is what keeps the agent's autonomy
                // exactly what it was before the tool changed.
                match! host.TerminalCommands.Execute None "first" false with
                | Error reason -> failwith reason
                | Ok first ->
                    Expect.equal first.Status (TerminalCommandRan (CommandSucceeded 0)) "it ran, inside the call"
                    Expect.isTrue (first.OutputTail.Contains "ran<first>") "and its real output came back"
                    // Conditioned on what the first one printed — the whole point of chaining.
                    let next = if first.OutputTail.Contains "ran<first>" then "second" else "wrong"
                    match! host.TerminalCommands.Execute (Some (InTerminal first.Terminal)) next false with
                    | Error reason -> failwith reason
                    | Ok second ->
                        Expect.equal second.Status (TerminalCommandRan (CommandSucceeded 0)) "the second ran too"
                        Expect.equal (List.ofSeq ran) [ "first"; "second" ] "both, in order, in that terminal"
                do! host.Stop ()
            }

        testCaseAsync "ApproveAgent is now ENFORCEABLE: the agent's call yields, and resumes on the approval" <|
            async {
                // The defect stage 3b closes. A session could set every terminal to require
                // approval and the agent routed around it, because it had a second door with
                // no gate on it. With one door the gate is real — and the tool still does not
                // deadlock the turn, because the approval wait is bounded and yields a handle.
                let spawns = ref 0
                let environment : SessionEnvironment.SessionEnvironment =
                    { Ensure = fun _ _ -> async { return EnvironmentAvailable }
                      Spawn =
                        fun _ onChunk ->
                            async {
                                spawns.Value <- spawns.Value + 1
                                onChunk (Stdout, "approved output")
                                return
                                    Ok
                                        { WriteStdin = ignore
                                          CloseStdin = ignore
                                          Kill = ignore
                                          Exited = async { return SandboxExited 0 } }
                            }
                      SpawnPty = fun _ _ _ _ -> async { return Error "no pty in this fixture" }
                      Stop = fun () -> async { return () }
                      CurrentRef = fun () -> Some "scripted" }
                let! host = Host.startWithEnvironment None (Some (fun _ -> WorkSandboxes.singleton "scripted" environment)) None (sid ()) 0
                let! a = connectInMemoryClient host "ada" "Ada"
                // A terminal ADA opened keeps its own mode — the asymmetry that makes this
                // useful: the agent's own scratch terminal is auto, a human's is gated by the
                // policy that human set.
                a.Connection.OpenTerminal "build"
                do! a.Runner.WaitFor (fun m -> not (List.isEmpty (TerminalProjection.openTerminals m.Terminals)))
                let terminal =
                    (TerminalProjection.openTerminals (a.Runner.Model ()).Terminals |> List.head).TerminalId
                // ApproveAgent is the default, so this is the out-of-the-box case.
                let! outcome = host.TerminalCommands.Execute (Some (InTerminal terminal)) "rm -rf build" false
                match outcome with
                | Error reason -> failwith reason
                | Ok outcome ->
                    Expect.equal outcome.Status TerminalCommandAwaitingApproval "the gate held it"
                    Expect.equal spawns.Value 0 "and NOTHING ran — there is no second door"
                    Expect.isNone outcome.Block "there is no block yet, so the handle is the request"

                    // The approval is a plain CRDT write from the client. Resuming the handle
                    // picks the same command up: nothing was lost by the deadline.
                    do! a.Runner.WaitFor (fun m -> not (List.isEmpty (ClientModel.terminalQueue terminal m)))
                    let entry = ClientModel.terminalQueue terminal (a.Runner.Model ()) |> List.head
                    Expect.equal entry.QueueId outcome.Handle "the handle IS the queue entry a human is looking at"
                    a.Runner.Dispatch (user (ApprovePendingMsg (entry.QueueId, a.Hello.PeerId)))
                    match! host.TerminalCommands.Read outcome.Handle with
                    | Error reason -> failwith reason
                    | Ok resumed ->
                        Expect.equal resumed.Status (TerminalCommandRan (CommandSucceeded 0)) "it ran on the approval"
                        Expect.isTrue (resumed.OutputTail.Contains "approved output") "with its output"
                        Expect.equal spawns.Value 1 "exactly once"
                do! host.Stop ()
            }

        testCaseAsync "the agent's queued command waits for a human, and running it is one approval away" <|
            async {
                let spawns = ref 0
                let environment : SessionEnvironment.SessionEnvironment =
                    { Ensure = fun _ _ -> async { return EnvironmentAvailable }
                      Spawn =
                        fun _ _ ->
                            async {
                                spawns.Value <- spawns.Value + 1
                                return
                                    Ok
                                        { WriteStdin = ignore
                                          CloseStdin = ignore
                                          Kill = ignore
                                          Exited = async { return SandboxExited 0 } }
                            }
                      SpawnPty = fun _ _ _ _ -> async { return Error "no pty in this fixture" }
                      Stop = fun () -> async { return () }
                      CurrentRef = fun () -> Some "scripted" }
                let! host = Host.startWithEnvironment None (Some (fun _ -> WorkSandboxes.singleton "scripted" environment)) None (sid ()) 0
                let! a = connectInMemoryClient host "ada" "Ada"
                a.Connection.OpenTerminal "build"
                do! a.Runner.WaitFor (fun m -> not (List.isEmpty (TerminalProjection.openTerminals m.Terminals)))
                let terminal =
                    (TerminalProjection.openTerminals (a.Runner.Model ()).Terminals |> List.head).TerminalId

                // The agent runs through its ONE capability (Plan 13, stage 3b), which writes
                // the same doc state a person's send writes — so Ada sees it appear in her
                // queue, awaiting her. Started in the background because it WAITS: that is the
                // whole change, and the point of this test is that an approval arriving inside
                // the grace is answered in the same call rather than yielding a handle.
                let! running = Async.StartChild (host.TerminalCommands.Execute (Some (InTerminal terminal)) "rm -rf build" false)
                do! a.Runner.WaitFor (fun m -> not (List.isEmpty (ClientModel.terminalQueue terminal m)))
                let entry = ClientModel.terminalQueue terminal (a.Runner.Model ()) |> List.head
                Expect.isTrue (ClientModel.awaitsApproval entry (a.Runner.Model ())) "and Ada sees it waiting"
                Expect.equal spawns.Value 0 "nothing has run"

                // Approving is a plain CRDT write from the client — no command, no round trip.
                a.Runner.Dispatch (user (ApprovePendingMsg (entry.QueueId, a.Hello.PeerId)))
                let ran (m: ClientModel) =
                    match TerminalProjection.tryFind terminal m.Terminals with
                    | Some view -> view.Blocks |> List.exists (fun b -> b.Command = "rm -rf build")
                    | None -> false
                do! a.Runner.WaitFor ran
                Expect.equal spawns.Value 1 "the approval is what let it run"
                // ...and the agent's own call, still waiting, answers with the outcome — not
                // with "queued". The old tool returned before anything had happened, which is
                // why an agent that needed an answer took the ungated path instead.
                let! outcome = running
                match outcome with
                | Error reason -> failwith reason
                | Ok outcome ->
                    Expect.equal outcome.Status (TerminalCommandRan (CommandSucceeded 0)) "it ran, and says so"
                    Expect.isSome outcome.Block "with the block it became"
                do! host.Stop ()
            }

        testCaseAsync "the settled title is reported to the Manager hook" <|
            async {
                // One-shot: the report continuation is registered (via StartChild) BEFORE the
                // edit is dispatched, so the host-side report can never fire before we listen.
                let mutable reportCont : (string -> unit) option = None
                let awaitReport = Async.FromContinuations (fun (cont, _, _) -> reportCont <- Some cont)
                let report (name: string) = async { match reportCont with Some c -> reportCont <- None; c name | None -> () }

                let! host = Host.startFull (fun () -> None) None None None None None (Some report) None (fun _ _ -> ()) None McpClient.McpConnections.none None (sid ()) None "" None false 0
                let! a = connectInMemoryClient host "ada" "Ada"
                let! reportWaiter = Async.StartChild awaitReport
                a.Runner.Dispatch (user (EditTitleMsg (Text.insert 0 "ship it" (a.Runner.Model ()).Synced.Title)))
                let! reported = reportWaiter
                Expect.equal reported "ship it" "the Host reports the settled title to the Manager hook"
                do! host.Stop ()
            }

        // Plan 11's dangerous direction. Reaping an idle session costs a relaunch; reaping a
        // session someone is USING costs their turn and their connection, so the property
        // that has to hold is that a connected peer keeps the session busy. `Reaper.plan`
        // covers the Manager's half (it never reaps a launch reported busy); this covers the
        // half only the Host knows — that a peer arriving and leaving is what moves the
        // report.
        //
        // Synchronised on the Host's OWN completion points, not on the reports under test:
        // `connectInMemoryClient` returns once the peer is accepted, and
        // `WaitForNextSessionEnd` resolves once a peer session has torn down — and the busy
        // report is made inside each of those, before they complete. So every assertion here
        // reads a list that is already final.
        //
        // That distinction is what makes this diagnose rather than merely detect. An earlier
        // version awaited the REPORT itself; break the busy signal and it hung until the
        // suite's 240s timeout, which reads as infrastructure trouble rather than as the
        // property having broken. Waiting on something independent of the thing under test
        // means a regression fails an assertion, by name, immediately.
        testCaseAsync "a connected peer holds the session busy; the LAST one leaving releases it" <|
            async {
                let reports = ResizeArray<bool> ()
                let report (busy: bool) = async { reports.Add busy }

                let! host =
                    Host.startFull (fun () -> None) None None None None None None (Some report) (fun _ _ -> ()) None McpClient.McpConnections.none None (sid ()) None "" None false 0

                // A session nobody has attached to is idle from the moment it boots — which
                // is what lets the Manager's window start at launch rather than at first
                // visitor, and what makes an abandoned launch collectable at all.
                Expect.equal (List.ofSeq reports) [ false ] "a freshly booted session reports itself idle"

                let! ada = connectInMemoryClient host "ada" "Ada"
                Expect.equal (Seq.last reports) true "a peer joining makes the session report busy"

                // A second peer joins and the first leaves: still occupied, so the session
                // must NOT release. This is the case a naive "someone disconnected, so we are
                // idle" gets wrong, and it is exactly how a reap lands on a live user.
                let! bob = connectInMemoryClient host "bob" "Bob"
                let departed = host.WaitForNextSessionEnd ()
                do! ada.Channel.Close ()
                do! departed
                Expect.isFalse
                    (Seq.contains false (Seq.skip 1 reports))
                    "with a peer still attached, a departure must never report idle"

                // The last one leaves, and only now does it release.
                let lastDeparted = host.WaitForNextSessionEnd ()
                do! bob.Channel.Close ()
                do! lastDeparted
                Expect.equal (Seq.last reports) false "the last peer leaving reports the session idle"

                do! host.Stop ()
            }
    ]
