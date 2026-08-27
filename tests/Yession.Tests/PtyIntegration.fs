module Yession.Tests.PtyIntegration

// `SpawnPty` on the sandbox seam, driven for real (Plan 13, stage 2c).
//
// Everything here asserts the ONE thing a pipe can never do. A test that merely spawns a
// process and reads its output would pass over `Spawn` just as happily, and would therefore
// prove nothing about ptys at all — so the assertions are the properties a terminal device
// has and a pipe does not: `tty` names a device, `isatty` is true, a resize is visible to
// the program, and stdout and stderr arrive on one stream because a tty has one.
//
// The suite sits under `Tag.needs [Pty]`, and `check` probes the capability by OPENING a
// pty rather than by looking for the addon's file — a package that resolves is not a kernel
// that will hand out `/dev/pts`.

open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Agent
open Yession.Domain.Terminals
open Yession.Domain.Collab
open Yession.Host
open Yession.SessionProcess
open Yession.Tests.Support

// Host-side fixtures the pty is then pointed at (same shape as SrtIntegration's).
let private nodeFs : obj = importAll "node:fs"
let private nodeOs : obj = importAll "node:os"

[<Emit("$0.mkdtempSync($1.tmpdir() + '/yession-pty-')")>]
let private mkdtemp (fs: obj) (os: obj) : string = jsNative

[<Emit("$0.writeFileSync($1, $2)")>]
let private writeFile (fs: obj) (path: string) (content: string) : unit = jsNative

/// Everything a pty emits until it exits, plus how it ended. One string, not two: that is
/// the shape of a terminal.
let private runOnPty (executable: string) (arguments: string list) : Async<Result<string * SandboxRun, string>> =
    async {
        let policy =
            { ReadPaths = []
              WritePaths = []
              AllowedDomains = None
              Sockets = []
              Env = Sandboxes.hostBaseline (Sandboxes.ambientEnv ())
              WorkingDirectory = None
              Filesystem = Confined }
        match! Sandboxes.HostSandbox.create () policy with
        | Error e -> return Error e
        | Ok sandbox ->
            match sandbox.SpawnPty with
            | None -> return Error "the host backend reports no pty support"
            | Some spawnPty ->
                let output = System.Text.StringBuilder ()
                let exec =
                    { Executable = executable; Arguments = arguments; Env = Map.empty; WorkingDirectory = None }
                match! spawnPty exec 80 24 (fun data -> output.Append data |> ignore) with
                | Error e -> return Error e
                | Ok pty ->
                    let! ended = pty.Exited
                    do! sandbox.Dispose ()
                    return Ok (string output, ended)
    }

/// A real terminal manager over a real host sandbox: the production pty path with no
/// container and no Manager, which is a few lines because `SessionEnvironment` is a record
/// of functions. The emulator is the REAL headless one, so alt-screen detection is the
/// production mechanism rather than a stub agreeing with itself.
///
/// `body` receives the manager, the terminal it opened, the transcript records it wrote, the
/// event log, a count of how many times the drain was re-armed, and a way to ADVANCE the
/// manager's clock — which is how the idle timeout is driven without a test that waits five
/// real minutes.
/// `prepare` runs against the manager BEFORE the terminal is opened — the only place a
/// setting that decides how a terminal opens (Plan 25's shell profile) can be established.
/// It is a hook rather than a second fixture because a test that opened its own second
/// terminal would collide with the fixture's single minted id.
///
/// `shell` is which shell the manager composes. It is a parameter rather than a constant
/// because production composes `TerminalShell.posix` (dash on Debian/Ubuntu and this repo's
/// own container) while this fixture defaulted to `bash` — so the suite proved stage 2d's
/// properties under the one dialect nothing ships, and a `/bin/sh` that never emitted its
/// prompt mark went unnoticed through every green run.
let private withShellTerminal
    (shell: SessionTerminals.TerminalShell)
    (prepare: SessionTerminals.SessionTerminals -> Async<unit>)
    (name: string)
    (body: SessionTerminals.SessionTerminals
             -> TerminalId
             -> ResizeArray<TranscriptRecord>
             -> EventLog<SessionEvent>
             -> (unit -> int)
             -> (System.TimeSpan -> unit)
             -> Async<unit>)
    : Async<unit> =
    async {
        let policy =
            { ReadPaths = []
              WritePaths = []
              AllowedDomains = None
              Sockets = []
              Env = Sandboxes.hostBaseline (Sandboxes.ambientEnv ())
              WorkingDirectory = None
              Filesystem = Confined }
        match! Sandboxes.HostSandbox.create () policy with
        | Error e -> failwith e
        | Ok sandbox ->
            let environment : SessionEnvironment.SessionEnvironment =
                { Ensure = fun _ _ -> async { return EnvironmentAvailable }
                  Spawn = fun exec onChunk -> sandbox.Spawn exec onChunk
                  SpawnPty =
                    fun exec cols rows onOutput ->
                        async {
                            match sandbox.SpawnPty with
                            | None -> return Error "no pty"
                            | Some spawn -> return! spawn exec cols rows onOutput
                        }
                  Stop = fun () -> async { return () }
                  CurrentRef = fun () -> Some "host" }
            let mutable now = System.DateTimeOffset (2026, 8, 7, 0, 0, 0, System.TimeSpan.Zero)
            let at () = now
            let advance (by: System.TimeSpan) = now <- now + by
            let log = InMemoryEventLog.create (SessionId.create name |> expect) at
            let records = ResizeArray<TranscriptRecord> ()
            let transcript : Transcript =
                { Append = fun record -> records.Add record; records.Count - 1
                  NextSeq = fun () -> records.Count
                  // Keyframes (Plan 14, stage 3) are covered where they are read; what this
                  // fixture is about is the pty, so it takes them and says nothing.
                  Keyframe = ignore }
            let mutable reDrains = 0
            let terminals =
                SessionTerminals.create
                    log
                    (fun _ -> environment)
                    (fun _ _ -> transcript)
                    // The reader over the same records the writer above appends to. This
                    // fixture is about the pty, so it is the smallest honest one: a half-open
                    // range over what was recorded.
                    (fun _ fromSeq toSeq ->
                        records
                        |> Seq.indexed
                        |> Seq.filter (fun (index, _) ->
                            index >= fromSeq && (match toSeq with Some until -> index < until | None -> true))
                        |> Seq.map snd
                        |> List.ofSeq)
                    Yession.Host.Emulator.openEmulator
                    shell
                    at
                    (fun () -> TerminalId.create ("term-" + name) |> expect)
                    (let mutable n = 0 in fun () -> n <- n + 1; BlockId.create (sprintf "b-%d" n) |> expect)
                    (fun () -> name + "-nonce")
                    (let mutable n = 0 in fun () -> n <- n + 1; MessageId.create (sprintf "m-%d" n) |> expect)
                    (fun _ _ _ -> ())
                    // What a peer would be told; this fixture has none.
                    ignore
                    (fun () -> reDrains <- reDrains + 1)
                    AttachTerminal.unavailable
                    Classifier.approveAll
                    []
                    ShellProfileProjection.empty
            do! prepare terminals
            match! terminals.Open (PeerRef (PeerId.create "ada" |> expect)) (SandboxShell SandboxRef.defaultRef) (TerminalTitle.fromProse name) with
            | Error e -> failwith e
            | Ok id ->
                do! body terminals id records log (fun () -> reDrains) advance
                do! sandbox.Dispose ()
    }

let private withPreparedTerminal prepare name body =
    withShellTerminal SessionTerminals.TerminalShell.bash prepare name body

let private withLiveTerminal (name: string) body =
    withPreparedTerminal (fun _ -> async { return () }) name body

/// A terminal under the shell PRODUCTION composes (`app/Host.fs`), which on Debian and
/// Ubuntu — and in this repository's own container — is dash. Every other case here runs
/// under bash, which is the right fixture for the bash dialect's own hooks and the wrong one
/// for asking whether what ships works.
let private withPosixTerminal (name: string) body =
    withShellTerminal SessionTerminals.TerminalShell.posix (fun _ -> async { return () }) name body

/// A queue entry for a terminal, as the drain would hand one over.
let private queueEntry (terminal: TerminalId) (author: ActorRef) (n: string) : PendingAct =
    { QueueId = QueueId.create n |> expect
      Terminal = terminal
      Authority = Authority.ofAuthor author
      Order = 1.0
      Size = None
      Background = false }

/// The same, authored by the agent on a peer's credential — the only way an agent-authored
/// act can be built, and what makes these cases about the agent rather than about a peer
/// wearing its name.
let private agentEntry (terminal: TerminalId) (turnActor: ActorRef) (n: string) : PendingAct =
    { queueEntry terminal turnActor n with Authority = Authority.agentFor turnActor }

/// Poll until `condition` holds or the budget runs out. Bounded rather than a fixed sleep:
/// a shell's timing is not ours to predict, and a test that sleeps long enough to be safe is
/// a test that is slow every run.
let private until (budgetMs: int) (condition: unit -> bool) : Async<bool> =
    let rec go remaining =
        async {
            if condition () then return true
            elif remaining <= 0 then return false
            else
                do! Async.Sleep 50
                return! go (remaining - 50)
        }
    go budgetMs

let private integrationLostTests =
    testList "Integration lost over a real pty (Plan 13, stage 2f)" [
        testCaseAsync "a shell replaced mid-session is detected, holds the queue, and is repaired by re-arming" <|
            withLiveTerminal "lost" (fun terminals id _ log reDrains _ ->
                async {
                    let ada = PeerRef (PeerId.create "ada" |> expect)
                    // A genuinely long-running command must NOT trip the detector — the `C`
                    // mark comes when the shell STARTS a command, so runtime is irrelevant.
                    // Run it in the background so the block does not hold this test open.
                    do! terminals.RunBlock id (queueEntry id ada "1") "sleep 5 &" ignore
                    Expect.isEmpty (terminals.Lost ()) "a slow command marks like any other"

                    // Now replace the instrumented shell while the pty stays open. `Exited`
                    // never fires and the marks simply stop — the exact failure this detects.
                    let before = reDrains ()
                    match! terminals.Take id ada with
                    | Error e -> failwith e
                    | Ok () ->
                        // A fresh, UNINSTRUMENTED shell — the case the plan describes: an
                        // image whose shell drops into another one. `--noprofile --norc` is
                        // what makes it uninstrumented rather than accidentally inheriting
                        // anything, and `exec` is what makes `Exited` never fire: the pty
                        // stays open around a process we never bootstrapped.
                        terminals.Input id ada "exec bash --noprofile --norc -i\r" |> ignore
                        // Let the shell ACT on what was just typed before typing the next
                        // thing. The drain never has to: it awaits a block's `D` before
                        // starting the next, so the previous command's `C` has always landed.
                        // Typing in live mode and draining immediately is the one ordering
                        // that has no such barrier, and without this the `C` bash emits for
                        // `exec cat` arrives inside the next block's window and looks like it.
                        do! Async.Sleep 1000
                        match! terminals.Release id ada with
                        | Error e -> failwith e
                        | Ok () ->
                            // A command written into the shell that is there now produces no
                            // `C`, because nothing instrumented it.
                            let running = terminals.RunBlock id (queueEntry id ada "2") "echo after-exec" ignore
                            Async.StartImmediate running
                            let! detected = until 8000 (fun () -> not (Set.isEmpty (terminals.Lost ())))
                            Expect.isTrue detected "the missing `C` is what gives it away"
                            Expect.isTrue (reDrains () > before) "and the drain is re-armed so the queue can be held"
                            let! page = log.Read None 1000
                            Expect.isTrue
                                (page.Events
                                 |> List.exists (fun e ->
                                     match e.Event with
                                     | SessionEvent.TerminalIntegrationLost l -> l.TerminalId = id
                                     | _ -> false))
                                "recorded, because it is a GAP in what the record can say"

                            // The re-arm control types the instrumentation into the shell that
                            // is actually there now — Warp's move, minus the rc-file edit.
                            match! terminals.Rearm id with
                            | Error e -> failwithf "re-arm failed: %s" e
                            | Ok () ->
                                Expect.isEmpty (terminals.Lost ()) "marking is back"
                                let! page = log.Read None 1000
                                Expect.isTrue
                                    (page.Events
                                     |> List.exists (fun e ->
                                         match e.Event with
                                         | SessionEvent.TerminalIntegrationRestored r -> r.TerminalId = id
                                         | _ -> false))
                                    "and every client is told, by the same route it was told it was lost"
                })
    ]

let private liveModeTests =
    testList "Live mode over a real pty (Plan 13, stage 2e)" [
        testCaseAsync "only the lease holder's keystrokes reach the shell, and none are recorded as input" <|
            withLiveTerminal "lease" (fun terminals id records _ _ _ ->
                async {
                    let ada = PeerRef (PeerId.create "ada" |> expect)
                    let bob = PeerRef (PeerId.create "bob" |> expect)
                    // Nobody holds it yet: even the peer who opened it is not typing into it.
                    Expect.isFalse (terminals.Input id ada "echo nope\r") "no lease, no input"
                    match! terminals.Take id ada with
                    | Error e -> failwith e
                    | Ok () ->
                        Expect.isFalse (terminals.Input id bob "echo stolen\r") "a non-holder is dropped"
                        Expect.isTrue (terminals.Input id ada "echo held\r") "the holder's keystrokes land"
                        let printed () =
                            records
                            |> Seq.filter (fun r -> r.Kind = TranscriptOutput)
                            |> Seq.map (fun r -> r.Data)
                            |> String.concat ""
                        let! landed = until 5000 (fun () -> (printed ()).Contains "held")
                        Expect.isTrue landed (sprintf "the shell ran it; transcript was: %s" (printed ()))
                        Expect.isFalse ((printed ()).Contains "stolen") "and bob's keystrokes never reached it"
                        // The narrowing: live-mode keystrokes are relayed and NEVER written as
                        // `"i"` records. Typing a password at an `ssh` prompt must not land in
                        // a durable file that replays.
                        Expect.isEmpty
                            (records |> Seq.filter (fun r -> r.Kind = TranscriptInput) |> List.ofSeq)
                            "no input record exists for anything typed in live mode"
                        // ...while the DRAIN's command line still is one, because the Process
                        // composed that and knows exactly what it wrote.
                        match! terminals.Release id ada with
                        | Error e -> failwith e
                        | Ok () ->
                            do! terminals.RunBlock id (queueEntry id ada "1") "echo drained" ignore
                            Expect.equal
                                (records
                                 |> Seq.filter (fun r -> r.Kind = TranscriptInput)
                                 |> Seq.map (fun r -> r.Data)
                                 |> List.ofSeq)
                                [ "echo drained\r\n" ]
                                "the drain's command line is recorded as input, and it alone"
                })

        testCaseAsync "a lease gates the drain and its release re-arms it" <|
            withLiveTerminal "gate" (fun terminals id _ _ reDrains _ ->
                async {
                    let ada = PeerRef (PeerId.create "ada" |> expect)
                    Expect.isEmpty (terminals.Leased ()) "nothing is held to begin with"
                    let before = reDrains ()
                    match! terminals.Take id ada with
                    | Error e -> failwith e
                    | Ok () ->
                        Expect.equal
                            (terminals.Leased ())
                            (Set.singleton (TerminalId.value id))
                            "the drain's `leased` set names it"
                        match! terminals.Release id ada with
                        | Error e -> failwith e
                        | Ok () ->
                            Expect.isEmpty (terminals.Leased ()) "and gives it back"
                            // Handing the terminal back must START whatever was waiting for
                            // it, exactly as block completion does — otherwise the queue sits
                            // there until some unrelated doc update happens to wake it.
                            Expect.isTrue (reDrains () > before) "the release re-armed the drain"
                })

        testCaseAsync "a block that takes the alternate screen hands its author the terminal, and gives it back" <|
            withLiveTerminal "altscreen" (fun terminals id _ log _ _ ->
                async {
                    let ada = PeerRef (PeerId.create "ada" |> expect)
                    // A real TUI's entry and exit, without depending on `vim` being installed:
                    // DECSET 1049 is exactly what one writes, and the emulator does not care
                    // who wrote it.
                    do!
                        terminals.RunBlock
                            id
                            (queueEntry id ada "1")
                            "printf '\\033[?1049h'; sleep 0.4; printf '\\033[?1049l'"
                            ignore
                    let leaseEvents () =
                        async {
                            let! page = log.Read None 1000
                            return
                                page.Events
                                |> List.choose (fun e ->
                                    match e.Event with
                                    | SessionEvent.TerminalLeaseTaken t -> Some ("taken", t.By)
                                    | SessionEvent.TerminalLeaseReleased r -> Some ("released", r.Was)
                                    | _ -> None)
                        }
                    // The exit is what closes the round trip, and the emulator applies writes
                    // asynchronously — so poll for it rather than assume it has landed.
                    let rec settle remaining =
                        async {
                            let! seen = leaseEvents ()
                            if List.length seen >= 2 || remaining <= 0 then return seen
                            else
                                do! Async.Sleep 50
                                return! settle (remaining - 50)
                        }
                    let! seen = settle 5000
                    Expect.equal
                        seen
                        [ "taken", ada; "released", ada ]
                        "entry gave ada the terminal; exit gave it back, because detection took it"
                })

        testCaseAsync "a dropped holder's lease is released, and the drain re-armed" <|
            withLiveTerminal "gone" (fun terminals id _ log reDrains _ ->
                async {
                    // What the Host runs from a peer's connection cleanup. Without it a
                    // crashed tab leaves the composer reading "bob is using this terminal" for
                    // ever, with the queue held behind a peer who cannot release it.
                    let bob = PeerId.create "bob" |> expect
                    match! terminals.Take id (PeerRef bob) with
                    | Error e -> failwith e
                    | Ok () ->
                        let before = reDrains ()
                        do! terminals.PeerGone bob
                        Expect.isEmpty (terminals.Leased ()) "the lease is gone with its holder"
                        Expect.isTrue (reDrains () > before) "and the queue behind it is re-armed"
                        let! page = log.Read None 1000
                        Expect.isTrue
                            (page.Events
                             |> List.exists (fun e ->
                                 match e.Event with
                                 | SessionEvent.TerminalLeaseReleased r ->
                                     r.Was = PeerRef bob && r.Reason = LeaseHolderGone
                                 | _ -> false))
                            "the record says nobody decided anything — the connection dropped"
                })

        testCaseAsync "an idle lease is reclaimed only when something is queued behind it" <|
            withLiveTerminal "idle" (fun terminals id _ log reDrains advance ->
                async {
                    let ada = PeerRef (PeerId.create "ada" |> expect)
                    let nothingQueued (_: TerminalId) = None
                    let queuedBehindIt (_: TerminalId) = Some TerminalQueueDrain.AwaitingTerminal
                    let stillHeld () = terminals.Leased () = Set.singleton (TerminalId.value id)
                    match! terminals.Take id ada with
                    | Error e -> failwith e
                    | Ok () ->
                        // Long past the window, but nobody is waiting. THE gate: a bare timer
                        // would take the terminal here, which is a worse behaviour than the
                        // starvation it prevents.
                        advance (TerminalLeaseIdle.window + System.TimeSpan.FromMinutes 10.0)
                        do! terminals.ReclaimIdle nothingQueued
                        Expect.isTrue (stillHeld ()) "nothing was waiting, so ada keeps it"

                        // Ada types: the window runs from the last keystroke, so the queue
                        // appearing now does not make her instantly idle.
                        Expect.isTrue (terminals.Input id ada "\r") "the holder's keystroke lands"
                        do! terminals.ReclaimIdle queuedBehindIt
                        Expect.isTrue (stillHeld ()) "queued, but she just typed"

                        // ...and once she has been silent through the window with that command
                        // still waiting, the lease is reclaimed and the queue re-armed.
                        let before = reDrains ()
                        advance (TerminalLeaseIdle.window + System.TimeSpan.FromSeconds 1.0)
                        do! terminals.ReclaimIdle queuedBehindIt
                        Expect.isEmpty (terminals.Leased ()) "the terminal goes back to block mode"
                        Expect.isTrue (reDrains () > before) "and whatever was queued starts now"
                        let! page = log.Read None 1000
                        Expect.isTrue
                            (page.Events
                             |> List.exists (fun e ->
                                 match e.Event with
                                 | SessionEvent.TerminalLeaseReleased r ->
                                     r.Was = ada && r.Reason = LeaseIdle
                                 | _ -> false))
                            "recorded under its own reason: she did not decide anything, she stopped"
                })

        testCaseAsync "a terminal with no shell refuses the lease rather than granting a dead one" <|
            async {
                // The degraded terminal: blocks still run as separate processes, and there is
                // no persistent stdin for anyone to hold. Refusing says so; granting would be
                // a lease that silently does nothing.
                let terminals = SessionTerminals.unavailable
                match! terminals.Take (TerminalId.create "term-x" |> expect) (PeerRef (PeerId.create "ada" |> expect)) with
                | Ok () -> failwith "a session with no terminals granted a lease"
                | Error _ -> Expect.isTrue true "refused"
            }
    ]


/// The agent's hand on a terminal that runs blocks (Plan 20, stage 6). Live mode used to be
/// human-only, and what that exception left behind was a wedge: an agent command that takes
/// the whole screen waits for a keystroke nobody was allowed to send, so its block never
/// finished, the terminal stayed busy, and the queue behind it never moved again.
let private agentLeaseTests =
    testList "The agent lease over a real pty (Plan 20, stage 6)" [
        testCaseAsync "an agent's block that takes the alternate screen hands the AGENT the terminal" <|
            withLiveTerminal "agentflip" (fun terminals id _ log _ _ ->
                async {
                    let ada = PeerRef (PeerId.create "ada" |> expect)
                    do!
                        terminals.RunBlock
                            id
                            (agentEntry id ada "1")
                            "printf '\\033[?1049h'; sleep 0.4; printf '\\033[?1049l'"
                            ignore
                    let leaseEvents () =
                        async {
                            let! page = log.Read None 1000
                            return
                                page.Events
                                |> List.choose (fun e ->
                                    match e.Event with
                                    | SessionEvent.TerminalLeaseTaken t -> Some ("taken", t.By)
                                    | SessionEvent.TerminalLeaseReleased r -> Some ("released", r.Was)
                                    | _ -> None)
                        }
                    let rec settle remaining =
                        async {
                            let! seen = leaseEvents ()
                            if List.length seen >= 2 || remaining <= 0 then return seen
                            else
                                do! Async.Sleep 50
                                return! settle (remaining - 50)
                        }
                    let! seen = settle 5000
                    Expect.equal
                        seen
                        [ "taken", ActorRef.Agent; "released", ActorRef.Agent ]
                        "the author of the command is who now needs the keyboard, agent or not"
                })

        testCaseAsync "the agent answers its own wedged block, and the block finishes" <|
            withLiveTerminal "agenttype" (fun terminals id records _ _ _ ->
                async {
                    let ada = PeerRef (PeerId.create "ada" |> expect)
                    // Enters the alternate screen and waits for a line, exactly as an editor
                    // or an installer prompt does — without depending on either being present.
                    let! block =
                        Async.StartChild (
                            terminals.RunBlock
                                id
                                (agentEntry id ada "1")
                                "printf '\\033[?1049h'; read -r answer; printf '\\033[?1049l'; echo \"answered $answer\""
                                ignore,
                            20000)
                    let! wedged = until 8000 (fun () -> terminals.Interactive id)
                    Expect.isTrue wedged "the flip handed the terminal over rather than leaving it wedged"
                    match! terminals.Write id ActorRef.Agent "yes\r" with
                    | Error e -> failwithf "the agent holds this terminal, so it may type into it: %s" e
                    | Ok () ->
                        do! block
                        Expect.isTrue
                            (records |> Seq.exists (fun r -> r.Data.Contains "answered yes"))
                            "the keystrokes reached the program, which then ran on to its end"
                })

        testCaseAsync "a shell terminal the agent does NOT hold still refuses raw bytes" <|
            withLiveTerminal "agentgate" (fun terminals id _ _ _ _ ->
                async {
                    // The refusal narrows to the lease; it does not go away. Typing into a
                    // shell nobody handed over would be the door around the classifier.
                    match! terminals.Write id ActorRef.Agent "rm -rf /\r" with
                    | Ok () -> failwith "raw bytes into an unheld shell would be the door around the classifier"
                    | Error reason -> Expect.stringContains reason "execute_command" "and it says where to go instead"
                })

        testCaseAsync "reading a shell terminal is admitted exactly while a block has the screen" <|
            withLiveTerminal "agentread" (fun terminals id _ _ _ _ ->
                async {
                    // `execute_command` answers with what a command printed — except for the
                    // one command that has not printed an answer and never will on its own.
                    match! terminals.Tail id None None with
                    | Ok _ -> failwith "a shell's output is its blocks', and reading it twice is two answers to one question"
                    | Error reason -> Expect.stringContains reason "execute_command" "so it is refused, and says where the answer is"

                    let! block =
                        Async.StartChild (
                            terminals.RunBlock
                                id
                                (agentEntry id (PeerRef (PeerId.create "ada" |> expect)) "1")
                                "printf '\\033[?1049h'; read -r answer; printf '\\033[?1049l'"
                                ignore,
                            20000)
                    let! wedged = until 8000 (fun () -> terminals.Interactive id)
                    Expect.isTrue wedged "the block took the screen"
                    match! terminals.Tail id None None with
                    | Error e -> failwithf "there is no command answer to read instead, so the screen is the only one: %s" e
                    | Ok _ ->
                        match! terminals.Write id ActorRef.Agent "yes\r" with
                        | Error e -> failwith e
                        | Ok () -> do! block
                })
    ]

let tests =
    testList "Pty (Plan 13)" [
        testCaseAsync "the host backend offers a pty at all" <|
            async {
                let policy =
                    { ReadPaths = []
                      WritePaths = []
                      AllowedDomains = None
                      Sockets = []
                      Env = Map.empty
                      WorkingDirectory = None
                      Filesystem = Confined }
                match! Sandboxes.HostSandbox.create () policy with
                | Error e -> failwith e
                | Ok sandbox ->
                    Expect.isTrue (Option.isSome sandbox.SpawnPty) "SpawnPty is Some where the addon is present"
                    do! sandbox.Dispose ()
            }

        testCaseAsync "the process gets a real terminal device, which is the whole point" <|
            async {
                // `tty` prints the device name and exits 0 on a terminal; on a pipe it prints
                // "not a tty" and exits 1. This is the assertion that separates this seam from
                // the piped one, and the only one that could not be satisfied by `Spawn`.
                match! runOnPty "/bin/sh" [ "-c"; "tty" ] with
                | Error e -> failwith e
                | Ok (output, ended) ->
                    Expect.isTrue (output.Contains "/dev/pts/" || output.Contains "/dev/ttys")
                        (sprintf "tty names a terminal device, got: %s" output)
                    Expect.equal ended (SandboxExited 0) "and exits 0, which it cannot do on a pipe"
            }

        testCaseAsync "stdout and stderr arrive on ONE stream, because a tty has one device" <|
            async {
                // Not an omission in `PtyHandle` — a property of ptys. The piped handle keeps
                // the split because it genuinely has one; this must not pretend to.
                match! runOnPty "/bin/sh" [ "-c"; "echo out; echo err 1>&2" ] with
                | Error e -> failwith e
                | Ok (output, _) ->
                    Expect.isTrue (output.Contains "out") "stdout is there"
                    Expect.isTrue (output.Contains "err") "and so is stderr, on the same stream"
            }

        testCaseAsync "the size the pty is opened with is the size the program sees" <|
            async {
                match! runOnPty "/bin/sh" [ "-c"; "stty size" ] with
                | Error e -> failwith e
                | Ok (output, _) ->
                    // `stty size` prints "rows cols".
                    Expect.isTrue (output.Contains "24 80") (sprintf "opened 80x24, program saw: %s" (output.Trim ()))
            }

        testCaseAsync "a resize reaches the program, which is what SIGWINCH is for" <|
            async {
                let policy =
                    { ReadPaths = []
                      WritePaths = []
                      AllowedDomains = None
                      Sockets = []
                      Env = Sandboxes.hostBaseline (Sandboxes.ambientEnv ())
                      WorkingDirectory = None
                      Filesystem = Confined }
                match! Sandboxes.HostSandbox.create () policy with
                | Error e -> failwith e
                | Ok sandbox ->
                    let spawnPty = Option.get sandbox.SpawnPty
                    let output = System.Text.StringBuilder ()
                    // Report the size on SIGWINCH, then again at exit. A shell that never
                    // learns its size is the one that redraws wrongly, so this is the
                    // behaviour that matters rather than the call returning unit.
                    let script = "trap 'stty size' WINCH; sleep 2 & wait; stty size"
                    let exec = { Executable = "/bin/sh"; Arguments = [ "-c"; script ]; Env = Map.empty; WorkingDirectory = None }
                    match! spawnPty exec 80 24 (fun d -> output.Append d |> ignore) with
                    | Error e -> failwith e
                    | Ok pty ->
                        do! Async.Sleep 300
                        pty.Resize 120 40
                        let! _ = pty.Exited
                        do! sandbox.Dispose ()
                        Expect.isTrue ((string output).Contains "40 120")
                            (sprintf "the program saw the new size, got: %s" ((string output).Replace ("\n", " | ")))
            }

        testCaseAsync "writing to the pty reaches the program's stdin" <|
            async {
                let policy =
                    { ReadPaths = []
                      WritePaths = []
                      AllowedDomains = None
                      Sockets = []
                      Env = Sandboxes.hostBaseline (Sandboxes.ambientEnv ())
                      WorkingDirectory = None
                      Filesystem = Confined }
                match! Sandboxes.HostSandbox.create () policy with
                | Error e -> failwith e
                | Ok sandbox ->
                    let spawnPty = Option.get sandbox.SpawnPty
                    let output = System.Text.StringBuilder ()
                    let exec =
                        { Executable = "/bin/sh"
                          Arguments = [ "-c"; "read line; echo \"got:$line\"" ]
                          Env = Map.empty
                          WorkingDirectory = None }
                    match! spawnPty exec 80 24 (fun d -> output.Append d |> ignore) with
                    | Error e -> failwith e
                    | Ok pty ->
                        do! Async.Sleep 200
                        pty.Write "hello\r"
                        let! _ = pty.Exited
                        do! sandbox.Dispose ()
                        Expect.isTrue ((string output).Contains "got:hello") (sprintf "got: %s" (string output))
            }

        testCaseAsync "an instrumented shell emits marks our scanner reads back" <|
            async {
                // The keystone of stage 2d, and the one test the unit tests cannot stand in
                // for: they prove the scanner reads what the scanner's author thinks the
                // shell emits. This proves a REAL bash, launched the way the Process will
                // launch it, emits marks the real scanner recognises — with the real exit
                // codes. An emitter and a parser that agree only with each other would pass
                // every cheap-tier case and close no block at all in production.
                let nonce = "probe-nonce"
                let rc = (Marks.rcFor "bash" nonce |> Option.get).Rc
                let dir = mkdtemp nodeFs nodeOs
                let rcPath = dir + "/yrc"
                writeFile nodeFs rcPath rc
                let policy =
                    { ReadPaths = []
                      WritePaths = []
                      AllowedDomains = None
                      Sockets = []
                      Env = Sandboxes.hostBaseline (Sandboxes.ambientEnv ())
                      WorkingDirectory = None
                      Filesystem = Confined }
                match! Sandboxes.HostSandbox.create () policy with
                | Error e -> failwith e
                | Ok sandbox ->
                    let spawnPty = Option.get sandbox.SpawnPty
                    let marks = ResizeArray<Mark> ()
                    let mutable carry = ""
                    let mutable clean = ""
                    let exec =
                        { Executable = "/bin/bash"
                          Arguments = [ "--noprofile"; "--rcfile"; rcPath; "-i" ]
                          Env = Map.empty
                          WorkingDirectory = None }
                    match! spawnPty exec 80 24 (fun data ->
                              let found, output, rest = Marks.scan nonce carry data
                              marks.AddRange found
                              carry <- rest
                              clean <- clean + output) with
                    | Error e -> failwith e
                    | Ok pty ->
                        do! Async.Sleep 500
                        pty.Write "echo hello\r"
                        do! Async.Sleep 600
                        pty.Write "false\r"
                        do! Async.Sleep 600
                        pty.Kill ()
                        let! _ = pty.Exited
                        do! sandbox.Dispose ()
                        let completions =
                            marks |> Seq.choose (function MarkCommandDone c -> Some c | _ -> None) |> List.ofSeq
                        Expect.isTrue (List.contains 0 completions) "the successful command reported 0"
                        // The one that would silently break: `$?` must be read as the FIRST
                        // statement of the prompt hook, or every block reports the status of
                        // our own bookkeeping instead of the command's.
                        Expect.isTrue (List.contains 1 completions) "and the failing one reported 1, not 0"
                        Expect.isTrue (marks |> Seq.exists ((=) MarkCommandStart)) "starts are marked too"
                        // Stripping is not cosmetic: this is what keeps the nonce out of a
                        // transcript that is fetchable over HTTP.
                        Expect.isFalse (clean.Contains nonce) "no mark, and so no nonce, reaches the transcript"
                        Expect.isTrue (clean.Contains "hello") "while the command's real output is kept"
            }

        testCaseAsync "cd in one block moves the next one — the whole point of stage 2d" <|
            // THE property a per-block spawn structurally cannot have, and therefore the test
            // that proves blocks really moved onto one shared shell. Under stage 1 each block
            // was its own process, so `cd` died with it; here the second block is typed into
            // the same shell the first one changed.
            //
            // It also pins the bash startup race: block one must not complete on a leftover
            // rc prompt-cycle `D`, or block two runs before `cd` took effect. Block two prints
            // `IN:$PWD` — the shell expands `$PWD`, so the directory appears in block two's
            // OUTPUT but in no command line (block one's `cd <dir>` echo carries the path too,
            // which is why asserting on the bare path would pass on block one alone). Only block
            // two actually having run in the cd'd directory produces `IN:<dir>`. Polled with
            // `until`, because the record populates as the bytes arrive, not when RunBlock
            // returns — the same wait the profile case below uses.
            withLiveTerminal "cd" (fun terminals id records _ _ _ ->
                async {
                    let ada = PeerRef (PeerId.create "ada" |> expect)
                    let dir = mkdtemp nodeFs nodeOs
                    do! terminals.RunBlock id (queueEntry id ada "1") ("cd " + dir) ignore
                    do! terminals.RunBlock id (queueEntry id ada "2") "echo \"IN:$PWD\"" ignore
                    let printed () = records |> Seq.map (fun r -> r.Data) |> String.concat ""
                    let! saw = until 5000 (fun () -> (printed ()).Contains ("IN:" + dir))
                    Expect.isTrue saw
                        (sprintf "the second block ran in the first block's directory %s, got: %s" dir (printed ()))
                })

        testCaseAsync "cd moves the next block under the shell production actually composes" <|
            // The same invariant, under `TerminalShell.posix` — the shell `app/Host.fs`
            // composes, which is dash here and on every Debian/Ubuntu host. It was FALSE in
            // production for the whole of stage 2d: the sh dialect wrote its prompt mark as a
            // literal escape and relied on the shell expanding it, which dash does not, so the
            // open-probe timed out, every terminal fell back to a process per block, and `cd`
            // died with each one. Green above under bash, broken on every box that ships.
            withPosixTerminal "cdposix" (fun terminals id records _ _ _ ->
                async {
                    let ada = PeerRef (PeerId.create "ada" |> expect)
                    let dir = mkdtemp nodeFs nodeOs
                    do! terminals.RunBlock id (queueEntry id ada "1") ("cd " + dir) ignore
                    do! terminals.RunBlock id (queueEntry id ada "2") "echo \"IN:$PWD\"" ignore
                    let printed () = records |> Seq.map (fun r -> r.Data) |> String.concat ""
                    let! saw = until 5000 (fun () -> (printed ()).Contains ("IN:" + dir))
                    Expect.isTrue saw
                        (sprintf "the second block ran in the first block's directory %s, got: %s" dir (printed ()))
                })

        testCaseAsync "a slow command does not lose integration where there is no start mark" <|
            // The other half, and the reason the sh fix cannot ship without the dialect saying
            // `MarksCommandStart = false`. A POSIX sh has no preexec, so it never emits `C`;
            // arming the integration detector on that absence makes every command slower than
            // `integrationWindowMs` report a working shell as lost and holds its queue. This
            // runs a command past that window and asserts the terminal is not marked lost.
            withPosixTerminal "slowposix" (fun terminals id _ log _ _ ->
                async {
                    let ada = PeerRef (PeerId.create "ada" |> expect)
                    // Longer than the 2s window, short enough for the suite's budget.
                    do! terminals.RunBlock id (queueEntry id ada "1") "sleep 3; echo done" ignore
                    let! page = log.Read None 1000
                    Expect.isFalse
                        (page.Events
                         |> List.exists (fun e ->
                             match e.Event with
                             | SessionEvent.TerminalIntegrationLost l -> l.TerminalId = id
                             | _ -> false))
                        "a shell that never promised a start mark is not reported as having lost one"
                    Expect.isEmpty (terminals.Lost ()) "and its queue is not held behind the report"
                })

        testCaseAsync "a command is sized before it is written, not after" <|
            // The size rides the queue entry, and what has to be true is the ORDER: the pty
            // learns the width before the command reaches it. Asserted on the transcript
            // because that is where both facts are recorded — the `r` record `applySize`
            // writes, and the `i` record the command line is echoed as — and because their
            // order is the claim, not a number a shell happened to report.
            withLiveTerminal "sized" (fun terminals id records _ _ _ ->
                async {
                    let ada = PeerRef (PeerId.create "ada" |> expect)
                    let entry = { queueEntry id ada "1" with Size = Some { Cols = 132; Rows = 43 } }
                    do! terminals.RunBlock id entry "echo sized" ignore
                    let ordered = List.ofSeq records
                    let indexOf predicate = ordered |> List.tryFindIndex predicate
                    let resized = indexOf (fun r -> r.Kind = TranscriptResize && r.Data = "132x43")
                    let written = indexOf (fun r -> r.Kind = TranscriptInput && r.Data.Contains "echo sized")
                    Expect.isSome resized "the width the entry asked for reached the terminal"
                    Expect.isSome written "the command was written"
                    Expect.isTrue (resized < written) "and the resize came first, or the command ran at the old width"
                })

        testCaseAsync "a command that claims no width leaves the terminal at the one it had" <|
            // The agent's commands carry no size, and neither does a person whose terminals
            // column is shut. `None` has to mean "leave it alone": if it meant "use the
            // default" instead, every agent command would drag a shared terminal back to 80x24
            // under whoever had just widened it. The first block widens so the second can only
            // pass by resizing NOTHING — against a fresh terminal it would pass either way.
            withLiveTerminal "unsized" (fun terminals id records _ _ _ ->
                async {
                    let ada = PeerRef (PeerId.create "ada" |> expect)
                    let wide = { queueEntry id ada "1" with Size = Some { Cols = 120; Rows = 40 } }
                    do! terminals.RunBlock id wide "echo wide" ignore
                    do! terminals.RunBlock id { queueEntry id ada "2" with Size = None } "echo after" ignore
                    let resizes =
                        records |> Seq.filter (fun r -> r.Kind = TranscriptResize) |> List.ofSeq
                    Expect.equal
                        (resizes |> List.map (fun r -> r.Data))
                        [ "120x40" ]
                        "the sizeless command resized nothing, so the width the first one set stands"
                })

        testCaseAsync "killing the pty settles Exited rather than hanging" <|
            async {
                let policy =
                    { ReadPaths = []
                      WritePaths = []
                      AllowedDomains = None
                      Sockets = []
                      Env = Sandboxes.hostBaseline (Sandboxes.ambientEnv ())
                      WorkingDirectory = None
                      Filesystem = Confined }
                match! Sandboxes.HostSandbox.create () policy with
                | Error e -> failwith e
                | Ok sandbox ->
                    let spawnPty = Option.get sandbox.SpawnPty
                    let exec =
                        { Executable = "/bin/sh"; Arguments = [ "-c"; "sleep 30" ]; Env = Map.empty; WorkingDirectory = None }
                    match! spawnPty exec 80 24 ignore with
                    | Error e -> failwith e
                    | Ok pty ->
                        do! Async.Sleep 200
                        pty.Kill ()
                        // The assertion is that this RESOLVES. A handle whose Exited never
                        // settles would hang the drain that awaits it, for ever.
                        let! _ = pty.Exited
                        do! sandbox.Dispose ()
                        Expect.isTrue true "Exited resolved after Kill"
            }

        // The shell profile (Plan 25), end to end: the only tier that can prove the promise
        // as a person experiences it — a real instrumented shell, asked where it is.
        testCaseAsync "a shell opened under a profile really starts there" <|
            (let directory = mkdtemp nodeFs nodeOs
             withPreparedTerminal
                 (fun terminals ->
                     async {
                         match! terminals.SetProfile ActorRef.Agent SandboxRef.defaultRef (Some directory) with
                         | Error e -> failwithf "the profile would not set: %s" e
                         | Ok _ -> ()
                     })
                 "profile"
                 (fun terminals id records _ _ _ ->
                     async {
                         let ada = PeerRef (PeerId.create "ada" |> expect)
                         do! terminals.RunBlock id (queueEntry id ada "1") "pwd" ignore
                         let printed () = records |> Seq.map (fun r -> r.Data) |> String.concat ""
                         let! saw = until 5000 (fun () -> (printed ()).Contains directory)
                         let visible =
                             (printed ()).Replace("\u001b", "<ESC>").Replace("\r", "<CR>").Replace("\n", "<LF>").Replace("\u0007", "<BEL>")
                         Expect.isTrue saw (sprintf "the shell started in %s, got: %s" directory visible)
                     }))

        liveModeTests
        agentLeaseTests
        integrationLostTests
    ]
