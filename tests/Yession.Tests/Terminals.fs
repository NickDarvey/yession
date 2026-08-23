module Yession.Tests.Terminals

// Terminals on the WorkSandbox (Plan 12). Everything here runs in the CHEAP tier:
// the sandbox is a scripted `SessionEnvironment` record, so a block's whole lifecycle —
// spawn, streamed output, exit code, transcript, events — is exercised deterministically
// with no process, port, or native addon in the loop. What a real sandbox adds is covered
// where sandboxes already are (`Ports`/`Docker`), and is not what these tests are about.

open System
open Fable.Pyxpecto
open Yjs
open Yession.Domain
open Yession.App
open Yession.SessionProcess
open Yession.Tests.Support

// Reading a transcript back as BYTES, for the one assertion that is about the file itself
// rather than about what the store returns from it.
let private nodeFs : obj = Fable.Core.JsInterop.importAll "node:fs"

[<Fable.Core.Emit("$0.readFileSync($1, 'utf8')")>]
let private readFileSync (fs: obj) (path: string) : string = Fable.Core.Util.jsNative

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

/// The structural doc read, which fails with a codec error list rather than a string.
let private syncedOf (doc: Y.Doc) : SyncedSessionState =
    match SyncedStateSync.ofDoc doc with
    | Ok synced -> synced
    | Error e -> failwithf "the doc would not decode: %A" e

let private sessionId = SessionId.create "terminal-tests" |> expect
let private terminalA = TerminalId.create "term-a" |> expect
let private terminalB = TerminalId.create "term-b" |> expect
let private ada = PeerId.create "ada" |> expect
let private bob = PeerId.create "bob" |> expect

let private queue (n: string) = QueueId.create ("q-" + n) |> expect
let private block (n: string) = BlockId.create ("b-" + n) |> expect

let private fixedClock () = DateTimeOffset (2026, 8, 2, 0, 0, 0, TimeSpan.Zero)
let private newLog () : EventLog<SessionEvent> = InMemoryEventLog.create sessionId fixedClock

let private eventsOf (log: EventLog<SessionEvent>) =
    async {
        let! page = log.Read None Int32.MaxValue
        return page.Events |> List.map (fun e -> e.Event)
    }

// --- Writing the doc as a remote peer would -------------------------------------------------
// A test driving the Session Process's own doc makes writes the way a peer's merged update
// would arrive. These are the only Yjs calls in this file, and they exist so no production
// API has to grow a setter that only a test would call.

[<Fable.Core.Emit("(function (doc, id, field, value) { const e = doc.getMap('pending').get(id); if (e) e.set(field, value) })($0, $1, $2, $3)")>]
let private setQueuedField (doc: Y.Doc) (id: string) (field: string) (value: obj) : unit = Fable.Core.Util.jsNative

/// A queue entry field, set to whatever a peer we do not control might have written.
let private setQueuedFieldInDoc (doc: Y.Doc) (id: QueueId) (field: string) (value: string) : unit =
    setQueuedField doc (QueueId.value id) field (box value)

/// A raw pending entry, written the way a build we no longer ship would have written one.
/// No production writer has this shape any more — that is the point — so the only way to
/// test tolerance of it is to write it as that build did.
[<Fable.Core.Emit("(function (doc, yjs, id, subject, author) { const q = doc.getMap('pending'); const e = new yjs.Map(); q.set(id, e); e.set('subject', subject); e.set('author', author); e.set('order', 1) })($0, $1, $2, $3, $4)")>]
let private legacyPendingInDoc (doc: Y.Doc) (yjs: obj) (id: string) (subject: string) (author: string) : unit =
    Fable.Core.Util.jsNative

[<Fable.Core.Import("*", "yjs")>]
let private yjsModule : obj = Fable.Core.Util.jsNative


// --- The drain's decision ------------------------------------------------------------------

let private entry (id: string) (terminal: TerminalId) (author: ActorRef) (order: float) =
    { QueueId = queue id
      Terminal = terminal
      Authority = Authority.ofAuthor author
      Order = order
      Background = false }

let private queueOf entries =
    entries |> List.map (fun (e: PendingAct) -> e.QueueId, e) |> Map.ofList

let private allOpen (_: TerminalId) = true
/// No lane cap in play. These cases are about the drain's other holds; the cap has its own.
let private noLaneCap (_: TerminalId) = false
let private planWith consumed busy isOpen entries =
    TerminalQueueDrain.plan consumed busy Set.empty Set.empty isOpen (queueOf entries)

/// The same plan with a lease in play (Plan 13, stage 2e).
let private planLeased consumed busy leased isOpen entries =
    TerminalQueueDrain.plan consumed busy leased Set.empty isOpen (queueOf entries)

/// ...and with the shell's marks gone (Plan 13, stage 2f).
let private planLost consumed lost isOpen entries =
    TerminalQueueDrain.plan consumed Set.empty Set.empty lost isOpen (queueOf entries)

let private drainTests =
    testList "Terminal drain plan" [
        testCase "one command per terminal, and terminals do not block each other" <| fun () ->
            let plan =
                planWith Set.empty Set.empty allOpen
                    [ entry "a1" terminalA (PeerRef ada) 1.0
                      entry "a2" terminalA (PeerRef ada) 2.0
                      entry "b1" terminalB (PeerRef bob) 1.0 ]
            Expect.equal
                (plan.Ready |> List.map (fun (_, e) -> QueueId.value e.QueueId))
                [ "q-a1"; "q-b1" ]
                "the head of each terminal runs; the second in a terminal waits its turn"

        testCase "a terminal with a block already running is skipped" <| fun () ->
            let plan =
                planWith Set.empty (Set.singleton (TerminalId.value terminalA)) allOpen
                    [ entry "a1" terminalA (PeerRef ada) 1.0
                      entry "b1" terminalB (PeerRef bob) 1.0 ]
            Expect.equal
                (plan.Ready |> List.map (fun (_, e) -> QueueId.value e.QueueId))
                [ "q-b1" ]
                "a busy terminal runs nothing more; its sibling is unaffected"

        testCase "a closed terminal runs nothing" <| fun () ->
            let plan =
                planWith Set.empty Set.empty (fun _ -> false)
                    [ entry "a1" terminalA (PeerRef ada) 1.0 ]
            Expect.isEmpty plan.Ready "nothing runs in a terminal that is not open"

        testCase "an entry already named by a started block is repaired away, never re-run" <| fun () ->
            // The crash window: the block event was appended and the doc removal was not.
            let plan =
                planWith (Set.singleton "q-a1") Set.empty allOpen
                    [ entry "a1" terminalA (PeerRef ada) 1.0
                      entry "a2" terminalA (PeerRef ada) 2.0 ]
            Expect.equal (plan.Removals |> List.map QueueId.value) [ "q-a1" ] "the consumed entry is removed"
            Expect.equal
                (plan.Ready |> List.map (fun (_, e) -> QueueId.value e.QueueId))
                [ "q-a2" ]
                "and the next one becomes the head"

        testCase "exactly-once is anchored on the block event, not the doc" <| fun () ->
            let started =
                SessionEvent.TerminalBlockStarted
                    { TerminalId = terminalA
                      BlockId = block "1"
                      QueueId = Some (queue "a1")
                      Authority = Authority.ofAuthor (PeerRef ada)
                      Command = "ls"
                      FromSeq = 0
                      Background = false }
            Expect.equal (TerminalQueueDrain.consumedOf started) (Some "q-a1") "a started block consumes its entry"
            Expect.equal
                (TerminalQueueDrain.consumedOf (SessionEvent.TerminalClosed { TerminalId = terminalA; Reason = "x" }))
                None
                "nothing else consumes anything"
    ]

// --- The projection --------------------------------------------------------------------

let private opened (id: TerminalId) (title: string) =
    SessionEvent.TerminalOpened { TerminalId = id; OpenedBy = PeerRef ada; Title = title; Sandbox = Some SandboxName.defaultName; Renewable = false }

let private started (id: TerminalId) (b: string) (command: string) (fromSeq: int) =
    SessionEvent.TerminalBlockStarted
        { TerminalId = id
          BlockId = block b
          QueueId = None
          Authority = Authority.ofAuthor (PeerRef ada)
          Command = command
          FromSeq = fromSeq
          Background = false }

let private completed (id: TerminalId) (b: string) (result: CommandResult) (toSeq: int) =
    SessionEvent.TerminalBlockCompleted { TerminalId = id; BlockId = block b; Result = result; ToSeq = toSeq }

let private fold events =
    events |> List.fold TerminalProjection.applyEvent TerminalProjection.empty

let private projectionTests =
    testList "Terminal projection" [
        testCase "terminals and their blocks project in order" <| fun () ->
            let proj =
                fold
                    [ opened terminalA "build"
                      opened terminalB "logs"
                      started terminalA "1" "make" 0
                      completed terminalA "1" (CommandSucceeded 0) 4 ]
            Expect.equal (proj.Terminals |> List.map (fun t -> t.Title)) [ "build"; "logs" ] "open order is kept"
            let a = TerminalProjection.tryFind terminalA proj |> Option.get
            Expect.equal (a.Blocks |> List.map (fun b -> b.Command)) [ "make" ] "the block is there"
            Expect.equal a.Blocks.Head.Status (BlockFinished (CommandSucceeded 0)) "with its exit code"
            Expect.equal a.Blocks.Head.ToSeq (Some 4) "and the transcript range it produced"

        testCase "a closed terminal keeps its blocks — the audit outlives the process" <| fun () ->
            let proj =
                fold
                    [ opened terminalA "build"
                      started terminalA "1" "make" 0
                      completed terminalA "1" (CommandFailed 2) 9
                      SessionEvent.TerminalClosed { TerminalId = terminalA; Reason = "session restarted" } ]
            let a = TerminalProjection.tryFind terminalA proj |> Option.get
            Expect.isFalse a.IsOpen "it is closed"
            Expect.equal a.ClosedReason (Some "session restarted") "with the reason recorded"
            Expect.equal (List.length a.Blocks) 1 "and its history intact"
            Expect.isEmpty (TerminalProjection.openTerminals proj) "it is not in the open list"

        testCase "the fold is idempotent, so overlapping event pages are safe" <| fun () ->
            let events = [ opened terminalA "build"; started terminalA "1" "make" 0 ]
            let once = fold events
            let twice = fold (events @ events)
            Expect.equal (List.length twice.Terminals) 1 "a replayed open is not a second terminal"
            Expect.equal
                (List.length (TerminalProjection.tryFind terminalA twice |> Option.get).Blocks)
                (List.length (TerminalProjection.tryFind terminalA once |> Option.get).Blocks)
                "and a replayed block start is not a second block"

        testCase "a truncation is a stated gap in the record" <| fun () ->
            let proj =
                fold
                    [ opened terminalA "build"
                      SessionEvent.TerminalTranscriptTruncated
                          { TerminalId = terminalA; BlockId = Some (block "1"); DroppedBytes = 512 } ]
            Expect.equal (TerminalProjection.tryFind terminalA proj |> Option.get).DroppedBytes 512 "the loss is counted"
    ]

// --- OSC 133 marks and their integrity (Plan 13, stage 2d) -------------------------------

let private nonce = "n0nce"

/// A mark as our shell hooks emit it.
let private mark (body: string) = "]133;" + body + ";y=" + nonce + ""

/// A mark as anything ELSE emits it — a nested shell's own integration, or a file being
/// printed. Same bytes, no nonce.
let private foreign (body: string) = "]133;" + body + ""

let private scan1 (data: string) = TerminalMarks.scan nonce "" data

let private markTests =
    testList "Terminal marks" [
        testCase "our marks are recognised and taken out of the output" <| fun () ->
            let marks, output, carry = scan1 (mark "C" + "hello" + mark "D;0")
            Expect.equal marks [ MarkCommandStart; MarkCommandDone 0 ] "both marks are read"
            Expect.equal output "hello" "and neither reaches the transcript"
            Expect.equal carry "" "nothing is left over"

        testCase "a mark WITHOUT our nonce is output, not a mark" <| fun () ->
            // The forgery case, and the reason the nonce exists. These bytes arrive from a
            // file someone printed, a build log, a filename — anything the terminal displays
            // that we did not write. Treating them as marks would close the running block
            // early with an exit code nobody produced.
            let marks, output, _ = scan1 (foreign "D;0")
            Expect.isEmpty marks "no mark is taken from it"
            Expect.equal output (foreign "D;0") "the bytes pass through verbatim, as output"

        testCase "a mark with the WRONG nonce is output too" <| fun () ->
            let marks, output, _ = scan1 ("]133;D;0;y=guessed")
            Expect.isEmpty marks "a guessed nonce is not our nonce"
            Expect.equal output "]133;D;0;y=guessed" "so it is just bytes"

        testCase "`cat` of a crafted file cannot forge a completion" <| fun () ->
            // The end-to-end shape of the attack: real output around a plausible-looking
            // mark. The block must not close, and the file's contents must survive intact
            // for whoever reads the transcript afterwards.
            let crafted = "build ok\n" + foreign "D;0" + "\nmore output"
            let marks, output, _ = scan1 crafted
            Expect.isEmpty marks "nothing is taken as a completion"
            Expect.equal output crafted "and the file reads back exactly as it was printed"

        testCase "a mark split across two chunks is still one mark" <| fun () ->
            // A pty delivers whatever the kernel had, so a mark can and will arrive in two
            // reads. Scanning each chunk alone would both miss the mark and leave half an
            // escape sequence in the transcript.
            let whole = mark "D;7"
            let first = whole.Substring (0, 8)
            let second = whole.Substring 8
            let marks1, out1, carry1 = TerminalMarks.scan nonce "" ("x" + first)
            Expect.isEmpty marks1 "the first half is not a mark yet"
            Expect.equal out1 "x" "and the fragment is not emitted as output"
            let marks2, out2, carry2 = TerminalMarks.scan nonce carry1 (second + "y")
            Expect.equal marks2 [ MarkCommandDone 7 ] "the halves join into one mark"
            Expect.equal out2 "y" "with only the real output around it"
            Expect.equal carry2 "" "and nothing left hanging"

        testCase "a bare prefix at the end of a chunk is carried, not printed" <| fun () ->
            let marks, output, carry = scan1 "done]133"
            Expect.isEmpty marks "not a mark yet"
            Expect.equal output "done" "the fragment is held back"
            Expect.equal carry "]133" "to be finished by the next chunk"

        testCase "both terminators are accepted, because a shell may print either" <| fun () ->
            let withSt = "]133;D;3;y=" + nonce + "\\"
            let marks, output, _ = scan1 withSt
            Expect.equal marks [ MarkCommandDone 3 ] "ST terminates a mark as well as BEL"
            Expect.equal output "" "and is stripped with it"

        testCase "an unreadable exit code still closes the block" <| fun () ->
            // Better than leaving a block open for ever over an unparseable integer: the
            // shell said the command ended and it held the nonce, so it ended.
            let marks, _, _ = scan1 (mark "D;notanumber")
            Expect.equal marks [ MarkCommandDone -1 ] "reported as 'the OS gave us none'"

        testCase "the prompt mark is what the open-probe waits for" <| fun () ->
            let marks, _, _ = scan1 (mark "A")
            Expect.equal marks [ MarkPromptStart ] "A is the handshake that instrumentation took"

        testCase "the rc payload carries the nonce and reads $? first" <| fun () ->
            // Two properties of the emitted shell, both of which are silent when wrong.
            for shell in [ "bash"; "zsh" ] do
                let rc = TerminalMarks.rcFor shell nonce |> Option.get
                Expect.isTrue (rc.Contains ("y=" + nonce)) (sprintf "%s marks carry the nonce" shell)
                Expect.isTrue (rc.Contains "__y_code=$?") (sprintf "%s captures $? as the first statement" shell)
                Expect.isTrue (rc.Contains "command -p") (sprintf "%s resolves binaries off a clobbered PATH" shell)
                for line in rc.Split '\n' do
                    Expect.isTrue (line.StartsWith " ") (sprintf "%s keeps its bootstrap out of history: %s" shell line)

        testCase "a shell we cannot instrument says so rather than guessing" <| fun () ->
            Expect.isNone (TerminalMarks.rcFor "fish" nonce) "fish is not one of the three yet"
            Expect.isNone (TerminalMarks.rcFor "" nonce) "and neither is nothing"
            Expect.isSome (TerminalMarks.rcFor "sh" nonce) "a POSIX sh rides its marks in PS1"
    ]

// --- The headless emulator (Plan 13, stage 2b) -------------------------------------------

let private emulatorTests =
    testList "Terminal emulator" [
        testCaseAsync "folding a transcript through a fresh emulator reproduces the screen" <|
            async {
                // THE property stage 2d rests on. A joining peer is sent a snapshot instead
                // of every byte the terminal ever printed, and that is only sound if the
                // screen is a pure function of the output records — same bytes, same order,
                // same screen. If this ever fails, a snapshot and a replay show two
                // different terminals and the transcript stops being the authority.
                let output = [ "hello\r\n"; "[31mred[0m "; "and\tmore\r\n"; "[1mbold[0m" ]
                let live = Yession.Host.Emulator.openEmulator 80 24
                for chunk in output do live.Write chunk
                let fresh = Yession.Host.Emulator.openEmulator 80 24
                for chunk in output do fresh.Write chunk
                let! liveScreen = live.Serialize ()
                let! freshScreen = fresh.Serialize ()
                // Asserted non-empty first, because the interesting way for this test to
                // fail is to pass: `write` is asynchronous, so serializing without waiting
                // compares one blank screen to another and proves nothing at all.
                Expect.isTrue (liveScreen.Contains "bold") "the screen was actually drawn before it was read"
                Expect.equal freshScreen liveScreen "same bytes, same screen"
                live.Dispose ()
                fresh.Dispose ()
            }

        testCaseAsync "the screen is a projection, and the transcript is not" <|
            async {
                // Why both records exist. A carriage return overwrites what was printed, so
                // the SCREEN loses it while the transcript still has every byte — which is
                // exactly why the audit trail is the stream and never the rendered buffer.
                let emulator = Yession.Host.Emulator.openEmulator 80 24
                emulator.Write "secret\rpublic"
                let! screen = emulator.Serialize ()
                Expect.isFalse (screen.Contains "secret") "the overwritten text is gone from the screen"
                Expect.isTrue (screen.Contains "public") "what remains is what was drawn last"
                emulator.Dispose ()
            }

        testCaseAsync "cursor movement is applied, not recorded literally" <|
            async {
                let emulator = Yession.Host.Emulator.openEmulator 80 24
                emulator.Write "abc[2DX"
                let! screen = emulator.Serialize ()
                Expect.isTrue (screen.Contains "aXc") "the cursor moved back two and overwrote"
                emulator.Dispose ()
            }

        testCaseAsync "a resize keeps the screen usable" <|
            async {
                let emulator = Yession.Host.Emulator.openEmulator 80 24
                emulator.Write "hello"
                emulator.Resize 120 40
                let! screen = emulator.Serialize ()
                Expect.isTrue (screen.Contains "hello") "content survives a resize"
                emulator.Dispose ()
            }

        testCase "a terminal with no size register is 80x24" <| fun () ->
            // The default IS the absence: a terminal nobody has resized carries no register
            // restating what every terminal has defaulted to since the VT100.
            let size = SyncedSessionState.sizeOf terminalA SyncedSessionState.empty
            Expect.equal size TerminalSize.default' "absent means default"
            Expect.equal (size.Cols, size.Rows) (80, 24) "and the default is 80x24"

        testCase "an unusable size reads back as the default, never as a broken terminal" <| fun () ->
            // The doc is shared with peers we do not control, and a zero-column terminal is
            // not a small terminal — it is one nothing can render. Same direction the
            // approval mode fails in: absent means the default, and the default always works.
            Expect.isFalse (TerminalSize.isValid { Cols = 0; Rows = 24 }) "no columns is not a size"
            Expect.isFalse (TerminalSize.isValid { Cols = 80; Rows = -1 }) "nor are negative rows"
            Expect.isTrue (TerminalSize.isValid TerminalSize.default') "the default is always valid"

        testCase "a size round-trips through the record a transcript writes it as" <| fun () ->
            // The two halves of this run in different processes — the Session Process writes
            // the record when it resizes a pty, a browser reads it to reshape the emulator
            // composing that terminal's screen — so the format is only ever right if one of
            // them cannot drift from the other.
            let size = { Cols = 132; Rows = 43 }
            Expect.equal (TerminalSize.format size) "132x43" "the asciicast `r` payload"
            Expect.equal (TerminalSize.parse (TerminalSize.format size)) (Some size) "and it reads back"

        testCase "a resize payload that is not a size is skipped, never guessed at" <| fun () ->
            // A transcript is replayed by clients that did not write it, so an unreadable
            // record is one to step over — the alternative is a screen reshaped to a number
            // nobody wrote.
            Expect.equal (TerminalSize.parse "") None "nothing is not a size"
            Expect.equal (TerminalSize.parse "80") None "one dimension is not a size"
            Expect.equal (TerminalSize.parse "80x24x2") None "nor are three"
            Expect.equal (TerminalSize.parse "eighty x twenty-four") None "nor words"
            Expect.equal (TerminalSize.parse "0x24") None "nor a dimension nothing can render"

        testCaseAsync "the no-op emulator answers everything without keeping a screen" <|
            async {
                // A host with no emulator still runs terminals; only the join snapshot is
                // missing. Same declare-and-skip honesty a backend without a pty gets.
                Emulator.none.Write "anything"
                Emulator.none.Resize 10 10
                let! screen = Emulator.none.Serialize ()
                Expect.equal screen "" "it keeps nothing, and says so"
                Emulator.none.Dispose ()
            }
    ]

// --- Rejection as an answer (Plan 13, stage 2a) ------------------------------------------

let private rejectedEvent (id: TerminalId) (q: string) (b: string) (by: PeerId) (reason: string option) =
    SessionEvent.TerminalCommandRejected
        { TerminalId = id
          QueueId = queue q
          BlockId = block b
          Author = ActorRef.Agent
          RejectedBy = PeerRef by
          Command = "rm -rf /"
          Reason = reason }

let private rejectionTests =
    testList "Terminal rejection" [
        // The verdict itself comes from the classifier now (Plan 23), inside the run —
        // what these pin is the RECORD: the event anchors exactly-once, folds legibly,
        // and survives its wire form, whoever the rejecter was.

        testCase "a rejected QueueId folds into the consumed set, so it can never run after" <| fun () ->
            let consumed = rejectedEvent terminalA "a1" "1" bob None |> TerminalQueueDrain.consumedOf
            Expect.equal consumed (Some "q-a1") "the rejection is the exactly-once anchor"
            let plan =
                planWith (Set.singleton "q-a1") Set.empty allOpen
                    [ entry "a1" terminalA ActorRef.Agent 1.0 ]
            Expect.isEmpty plan.Ready "an entry already refused in the log never runs"
            Expect.equal
                (plan.Removals |> List.map QueueId.value)
                [ "q-a1" ]
                "it is simply swept out of the doc"

        testCase "the projection shows the refusal in line, with who and why" <| fun () ->
            let proj =
                fold
                    [ opened terminalA "build"
                      started terminalA "1" "make" 0
                      completed terminalA "1" (CommandSucceeded 0) 3
                      rejectedEvent terminalA "a1" "2" bob (Some "not on prod") ]
            let view = TerminalProjection.tryFind terminalA proj |> Option.get
            Expect.equal (view.Blocks |> List.map (fun b -> b.Command)) [ "make"; "rm -rf /" ] "beside what did run"
            let refusal = view.Blocks |> List.last
            Expect.equal refusal.Status (BlockRejected (PeerRef bob, Some "not on prod")) "named, with the reason"
            Expect.equal (Authority.author refusal.Authority) ActorRef.Agent "and whose command it was"
            Expect.equal (refusal.FromSeq, refusal.ToSeq) (0, Some 0) "an empty range, because it produced nothing"

        testCase "the rejection fold is idempotent, so overlapping pages are safe" <| fun () ->
            let events = [ opened terminalA "build"; rejectedEvent terminalA "a1" "2" bob None ]
            let twice = fold (events @ events)
            Expect.equal
                (List.length (TerminalProjection.tryFind terminalA twice |> Option.get).Blocks)
                1
                "a replayed refusal is not a second block"

        testCase "the event round-trips" <| fun () ->
            let event = rejectedEvent terminalA "a1" "2" bob (Some "not on prod")
            let encoded = Codec.toString Codec.sessionEvent event
            match Codec.fromString Codec.sessionEvent encoded with
            | Ok decoded -> Expect.equal decoded event "actor, reason and command all survive"
            | Error e -> failwith e
    ]

// --- The agent's terminal digest (Plan 13, stage 3a) -------------------------------------

// --- Live mode: leases, the flip, and the drain gate (Plan 13, stage 2e) -----------------

/// A fixed instant for the lease transitions: none of them reads the clock for a decision —
/// only the idle timeout does, and it gets its own tests.
let private at0 = System.DateTimeOffset (2026, 8, 7, 0, 0, 0, System.TimeSpan.Zero)

let private leaseTests =
    testList "Terminal leases" [
        testCase "taking an unheld terminal writes one event; re-taking it writes none" <| fun () ->
            let events, leases = TerminalLeases.take terminalA (PeerRef ada) false at0 0 TerminalLeases.empty
            Expect.equal (List.length events) 1 "one take"
            Expect.equal (TerminalLeases.holderOf terminalA leases) (Some (PeerRef ada)) "ada holds it"
            // Not a steal from yourself. A client re-sending the frame must not fill the log.
            let again, _ = TerminalLeases.take terminalA (PeerRef ada) false at0 0 leases
            Expect.isEmpty again "re-taking your own lease records nothing"

        testCase "a steal ENDS the old lease before it starts the new one" <| fun () ->
            let _, held = TerminalLeases.take terminalA (PeerRef ada) false at0 0 TerminalLeases.empty
            let events, leases = TerminalLeases.take terminalA (PeerRef bob) false at0 7 held
            Expect.equal
                events
                [ SessionEvent.TerminalLeaseReleased
                    { TerminalId = terminalA; Was = PeerRef ada; Reason = LeaseStolen (PeerRef bob); ToSeq = 7 }
                  SessionEvent.TerminalLeaseTaken { TerminalId = terminalA; By = PeerRef bob; FromSeq = 7 } ]
                "the release names who took it, and comes first; the two stretches abut at one seq"
            Expect.equal (TerminalLeases.holderOf terminalA leases) (Some (PeerRef bob)) "bob holds it now"
            // Folded in that order, a reader never sees two holders — and never none.
            let proj = fold [ opened terminalA "build"; yield! events ]
            Expect.equal
                (TerminalProjection.tryFind terminalA proj |> Option.bind (fun t -> t.Lease))
                (Some (PeerRef bob))
                "the projection agrees"

        testCase "only the holder can release; a non-holder's release is not an event" <| fun () ->
            let _, held = TerminalLeases.take terminalA (PeerRef ada) false at0 0 TerminalLeases.empty
            let events, leases = TerminalLeases.release terminalA (PeerRef bob) 9 held
            Expect.isEmpty events "bob cannot release ada's lease"
            Expect.equal (TerminalLeases.holderOf terminalA leases) (Some (PeerRef ada)) "and ada still holds it"
            let events, leases = TerminalLeases.release terminalA (PeerRef ada) 9 held
            Expect.equal
                events
                [ SessionEvent.TerminalLeaseReleased
                    { TerminalId = terminalA; Was = PeerRef ada; Reason = LeaseReleased; ToSeq = 9 } ]
                "the holder's release is recorded as one, and bounds the stretch it ends"
            Expect.isEmpty (TerminalLeases.held leases) "and the terminal is free"

        testCase "a dropped peer's leases end, and only that peer's" <| fun () ->
            let _, leases = TerminalLeases.take terminalA (PeerRef ada) false at0 0 TerminalLeases.empty
            let _, leases = TerminalLeases.take terminalB (PeerRef bob) false at0 0 leases
            let events, leases = TerminalLeases.peerGone ada (fun _ -> 4) leases
            Expect.equal
                events
                [ SessionEvent.TerminalLeaseReleased
                    { TerminalId = terminalA; Was = PeerRef ada; Reason = LeaseHolderGone; ToSeq = 4 } ]
                "the reason says nobody decided anything"
            Expect.equal (TerminalLeases.held leases) (Set.singleton (TerminalId.value terminalB)) "bob's is untouched"

        testCase "a stale release does not clear the lease it names someone else holding" <| fun () ->
            // The guard that makes the fold independent of which order a steal's two events
            // are appended in: acting on this would drop the lease the take beside it granted.
            let proj =
                fold
                    [ opened terminalA "build"
                      SessionEvent.TerminalLeaseTaken { TerminalId = terminalA; By = PeerRef bob; FromSeq = 0 }
                      SessionEvent.TerminalLeaseReleased
                        { TerminalId = terminalA; Was = PeerRef ada; Reason = LeaseStolen (PeerRef bob); ToSeq = 0 } ]
            Expect.equal
                (TerminalProjection.tryFind terminalA proj |> Option.bind (fun t -> t.Lease))
                (Some (PeerRef bob))
                "bob still holds it"

        testCase "closing a terminal clears its lease" <| fun () ->
            let proj =
                fold
                    [ opened terminalA "build"
                      SessionEvent.TerminalLeaseTaken { TerminalId = terminalA; By = PeerRef ada; FromSeq = 0 }
                      SessionEvent.TerminalClosed { TerminalId = terminalA; Reason = "closed by a peer" } ]
            Expect.equal
                (TerminalProjection.tryFind terminalA proj |> Option.bind (fun t -> t.Lease))
                None
                "a closed terminal has no stdin to hold"
    ]

let private retentionTests =
    testList "Transcript retention (Plan 13, stage 3d; Plan 14, stage 0)" [
        testCase "output is kept up to the cap, then dropped — never renumbered away" <| fun () ->
            // A line index IS a sequence number, so nothing may ever be removed from the front
            // or the middle: that would renumber every block range in the log and every cached
            // chunk. What a ceiling gives up is the NEWEST output, which renumbers nothing.
            let cap = TranscriptRetention.outputCap
            Expect.equal
                (TranscriptRetention.admit 0 "hello")
                { Keep = "hello"; Dropped = 0 }
                "well under the cap, everything is kept"
            Expect.equal
                (TranscriptRetention.admit cap "hello")
                { Keep = ""; Dropped = 5 }
                "at the cap, nothing is kept and the loss is counted"

        testCase "the record that meets the cap is kept in PART, and says how much it lost" <| fun () ->
            // A `Result` could carry the kept part or the dropped count; the boundary record
            // needs both, and reporting only one would be a lie about the other.
            let admission = TranscriptRetention.admit (TranscriptRetention.outputCap - 3) "abcde"
            Expect.equal admission { Keep = "abc"; Dropped = 2 } "three kept, two dropped"

        testCase "closing a terminal takes nothing away from its recording" <| fun () ->
            // Plan 14, stage 0: a recording lives as long as its session does. There is no
            // age at which a closed terminal's transcript is deleted, because the chat now
            // carries a permanent, tappable item for every block — and a chip whose recording
            // a timer deleted underneath it is a dead end rather than an audit trail.
            let dir = sprintf "tests/Yession.Tests/out/.data/retention-%s" (string (System.Guid.NewGuid ()))
            let store = Yession.Host.TranscriptStore.openStore dir
            let transcript = store.Open terminalA { Width = 80; Height = 24; Timestamp = 0L }
            transcript.Append { At = 0.0; Kind = TranscriptOutput; Data = "still here" } |> ignore
            let proj = fold [ opened terminalA "build"; SessionEvent.TerminalClosed { TerminalId = terminalA; Reason = "closed by a peer" } ]
            Expect.equal
                (TerminalProjection.tryFind terminalA proj |> Option.map (fun t -> t.IsOpen))
                (Some false)
                "the terminal is closed"
            Expect.isSome (store.BoundsAfter terminalA None) "and its recording is still served"
            Expect.equal
                (store.ReadRange terminalA 0 None |> List.map (fun r -> r.Data))
                [ "still here" ]
                "with every record it held"
            Expect.equal
                (TerminalProjection.tryFind terminalA proj |> Option.map (fun t -> t.DroppedBytes))
                (Some 0)
                "and nothing counted as lost"

        testCase "the stated gap is the SAME event the live cap writes" <| fun () ->
            // One mechanism for "the transcript did not keep this", not two. The projection
            // already surfaces it, so a client that could render a truncated terminal renders
            // a forgotten one with no new case.
            let proj =
                fold
                    [ opened terminalA "build"
                      SessionEvent.TerminalTranscriptTruncated
                        { TerminalId = terminalA; BlockId = None; DroppedBytes = 10 }
                      SessionEvent.TerminalClosed { TerminalId = terminalA; Reason = "closed by a peer" }
                      SessionEvent.TerminalTranscriptTruncated
                        { TerminalId = terminalA; BlockId = None; DroppedBytes = 4096 } ]
            Expect.equal
                (TerminalProjection.tryFind terminalA proj |> Option.map (fun t -> t.DroppedBytes))
                (Some 4106)
                "both losses accumulate on the one number a reader looks at"
    ]

let private integrationTests =
    testList "Integration lost (Plan 13, stage 2f)" [
        testCase "a terminal that stopped marking holds its queue" <| fun () ->
            // Draining into an unmarked shell would produce blocks that never close: the
            // Process would not know when the command started or finished. Holding says so.
            let entries = [ entry "a1" terminalA (PeerRef ada) 1.0 ]
            let lost = Set.singleton (TerminalId.value terminalA)
            Expect.isEmpty (planLost Set.empty lost allOpen entries).Ready "nothing runs"
            Expect.equal
                (TerminalQueueDrain.holdOf Set.empty Set.empty Set.empty lost allOpen (queueOf entries) terminalA)
                (Some TerminalQueueDrain.AwaitingIntegration)
                "and the hold names the repair, not a person"

        testCase "re-arming yields the entry Ready, unchanged" <| fun () ->
            let entries = [ entry "a1" terminalA (PeerRef ada) 1.0 ]
            Expect.equal
                ((planLost Set.empty Set.empty allOpen entries).Ready
                 |> List.map (fun (_, e) -> QueueId.value e.QueueId))
                [ "q-a1" ]
                "the command that was held runs once marking is back"

        testCase "the agent is told the terminal is not free, and does not wait on it" <| fun () ->
            // Only a person re-arming brings marking back — an unbounded wait, so it
            // returns at once rather than burning a deadline.
            Expect.equal
                (TerminalCommandWait.step
                    false
                    { TerminalCommandWait.Observation.Block = None
                      TerminalCommandWait.Observation.InQueue = true
                      TerminalCommandWait.Observation.IsHead = true
                      TerminalCommandWait.Observation.Hold = Some TerminalQueueDrain.AwaitingIntegration
                      TerminalCommandWait.Observation.Interactive = false })
                (TerminalCommandWait.Return TerminalCommandAwaitingTerminal)
                "no waiting at all"

        testCase "the projection remembers, and forgets on repair" <| fun () ->
            let lost =
                fold
                    [ opened terminalA "build"
                      SessionEvent.TerminalIntegrationLost { TerminalId = terminalA; BlockId = Some (block "1") } ]
            Expect.isTrue
                (TerminalProjection.tryFind terminalA lost |> Option.map (fun t -> t.IntegrationLost) |> Option.defaultValue false)
                "every client sees it, because it is an event rather than a screen"
            let repaired =
                fold
                    [ opened terminalA "build"
                      SessionEvent.TerminalIntegrationLost { TerminalId = terminalA; BlockId = None }
                      SessionEvent.TerminalIntegrationRestored { TerminalId = terminalA } ]
            Expect.isFalse
                (TerminalProjection.tryFind terminalA repaired |> Option.map (fun t -> t.IntegrationLost) |> Option.defaultValue true)
                "and stops seeing it once somebody re-armed"
    ]

let private idleLeaseTests =
    testList "The idle-lease timeout" [
        let idle (minutes: float) = System.TimeSpan.FromMinutes minutes
        let waiting = Some TerminalQueueDrain.AwaitingTerminal

        testCase "an idle lease with something queued behind it is reclaimed" <| fun () ->
            Expect.isTrue
                (TerminalLeaseIdle.shouldReclaim (idle 6.0) false waiting)
                "the bound on starvation, and the only case it fires in"

        testCase "an idle lease with NOTHING queued is left alone, however long it idles" <| fun () ->
            // The whole gate. A bare timer would take a terminal away from someone the moment
            // they stopped typing whether or not anything was waiting — a worse behaviour than
            // the starvation it prevents. It also dissolves the question a bare timer forces:
            // whether to reclaim from a peer reading a man page in `less` for ten minutes.
            for hold in [ None; Some TerminalQueueDrain.NotWaiting ] do
                Expect.isFalse
                    (TerminalLeaseIdle.shouldReclaim (idle 600.0) false hold)
                    "nothing is waiting on this terminal, so there is nothing to buy"

        testCase "a holder who is still typing keeps it" <| fun () ->
            Expect.isFalse (TerminalLeaseIdle.shouldReclaim (idle 0.0) false waiting) "just typed"
            Expect.isFalse
                (TerminalLeaseIdle.shouldReclaim (TerminalLeaseIdle.window - idle 0.1) false waiting)
                "and a moment short of the window is still inside it"

        testCase "a running block is never interrupted" <| fun () ->
            // It may be the holder's own long build. A busy terminal is a different wait with a
            // different answer — `AwaitingBlock`, bounded by the block's own deadline.
            Expect.isFalse
                (TerminalLeaseIdle.shouldReclaim (idle 600.0) true waiting)
                "typing stops while you watch a build; that is not abandoning the terminal"

        testCase "a reclaim ends the lease under its own reason" <| fun () ->
            let _, held = TerminalLeases.take terminalA (PeerRef ada) false at0 0 TerminalLeases.empty
            let events, leases = TerminalLeases.reclaimIdle terminalA 5 held
            Expect.equal
                events
                [ SessionEvent.TerminalLeaseReleased
                    { TerminalId = terminalA; Was = PeerRef ada; Reason = LeaseIdle; ToSeq = 5 } ]
                "not `LeaseReleased` — the holder decided nothing, they stopped"
            Expect.isEmpty (TerminalLeases.held leases) "and the terminal is free"

        testCase "typing resets the clock; a non-holder's keystroke does not" <| fun () ->
            let _, held = TerminalLeases.take terminalA (PeerRef ada) false at0 0 TerminalLeases.empty
            let later = at0.AddMinutes 4.0
            let touched = TerminalLeases.touch terminalA (PeerRef ada) later held
            Expect.equal
                (TerminalLeases.idleFor terminalA (later.AddMinutes 1.0) touched)
                (Some (idle 1.0))
                "the window runs from the last keystroke, not from when the lease was taken"
            // Bob's keystrokes are DROPPED by the lease check, so they must not keep ada's
            // lease alive either — a lease kept warm by input nobody accepted is a lease held
            // by nobody.
            let untouched = TerminalLeases.touch terminalA (PeerRef bob) (later.AddMinutes 10.0) touched
            Expect.equal
                (TerminalLeases.idleFor terminalA (later.AddMinutes 1.0) untouched)
                (Some (idle 1.0))
                "a non-holder cannot refresh it"
    ]

let private flipTests =
    testList "Alt-screen flip policy" [
        testCase "a peer's block entering the alt screen hands them the terminal" <| fun () ->
            Expect.equal
                (TerminalFlip.propose true None false (Some (PeerRef ada)))
                (FlipToLive (PeerRef ada))
                "the author of the command is the person who now needs the keyboard"

        testCase "an agent's block entering the alt screen hands the AGENT the terminal" <| fun () ->
            // The rule is the author's, not the human's (Plan 20, stage 6). Refusing here left
            // a wedge: a block waiting for a keystroke nobody was allowed to send never
            // finishes, so the terminal is busy for ever and its queue never moves.
            Expect.equal
                (TerminalFlip.propose true None false (Some ActorRef.Agent))
                (FlipToLive ActorRef.Agent)
                "the agent wrote the command, so the agent needs the keyboard"

        testCase "nothing flips to a party that cannot type" <| fun () ->
            // Not a policy: there is no surface anywhere that sends keystrokes as the process
            // or as the system, so a lease here would be held by nobody.
            Expect.equal (TerminalFlip.propose true None false (Some ActorRef.SessionProcess)) FlipNothing "nor the process"
            Expect.equal (TerminalFlip.propose true None false (Some ActorRef.System)) FlipNothing "nor the system"
            Expect.equal (TerminalFlip.propose true None false None) FlipNothing "nor with no block at all"

        testCase "detection never overrides a lease somebody is holding" <| fun () ->
            Expect.equal
                (TerminalFlip.propose true (Some (PeerRef bob)) false (Some (PeerRef ada)))
                FlipNothing
                "ada's command does not take the terminal out from under bob"

        testCase "detection only gives back what detection took" <| fun () ->
            // Leaving `vim` must not yank the keyboard from a peer who took the terminal by
            // hand and happened to run an editor in it.
            Expect.equal (TerminalFlip.propose false (Some (PeerRef ada)) true None) FlipToBlock "auto-held goes back"
            Expect.equal (TerminalFlip.propose false (Some (PeerRef ada)) false None) FlipNothing "asked-for does not"
            Expect.equal (TerminalFlip.propose false None false None) FlipNothing "and an unheld terminal is a no-op"
    ]

let private leaseGateTests =
    testList "The lease as a drain gate" [
        testCase "an approved entry in a leased terminal with NO block running is held" <| fun () ->
            // The case `busy` does not cover: someone pressed "take terminal" and is typing at
            // the shell. Nothing is running, the terminal is open, the entry is approved.
            let entries = [ entry "a1" terminalA (PeerRef ada) 1.0 ]
            let leased = Set.singleton (TerminalId.value terminalA)
            let plan = planLeased Set.empty Set.empty leased allOpen entries
            Expect.isEmpty plan.Ready "the queue waits for the terminal"
            Expect.equal
                (TerminalQueueDrain.holdOf Set.empty Set.empty leased Set.empty allOpen (queueOf entries) terminalA)
                (Some TerminalQueueDrain.AwaitingTerminal)
                "and the hold names the terminal, not an approval"

        testCase "releasing the lease yields the entry Ready, unchanged" <| fun () ->
            let entries = [ entry "a1" terminalA (PeerRef ada) 1.0 ]
            let plan = planLeased Set.empty Set.empty Set.empty allOpen entries
            Expect.equal (plan.Ready |> List.map (fun (_, e) -> QueueId.value e.QueueId)) [ "q-a1" ] "it runs on release"
            Expect.equal
                (TerminalQueueDrain.holdOf Set.empty Set.empty Set.empty Set.empty allOpen (queueOf entries) terminalA)
                None
                "nothing is holding it"

        testCase "the holds are told apart" <| fun () ->
            let entries = [ entry "a1" terminalA ActorRef.Agent 1.0 ]
            let hold busy leased =
                TerminalQueueDrain.holdOf Set.empty busy leased Set.empty allOpen (queueOf entries) terminalA
            let busyA = Set.singleton (TerminalId.value terminalA)
            Expect.equal (hold busyA Set.empty) (Some TerminalQueueDrain.AwaitingBlock) "a block is running"
            Expect.equal (hold Set.empty busyA) (Some TerminalQueueDrain.AwaitingTerminal) "a peer is typing"
    ]

// --- The two waits (Plan 13, stage 3b) ---------------------------------------------------

let private blockOf (status: TerminalBlockStatus) : TerminalBlock =
    { BlockId = block "1"
      QueueId = Some (queue "a1")
      Authority = Authority.agentFor (PeerRef ada)
      Command = "make"
      Background = false
      FromSeq = 0
      ToSeq = None
      Status = status }

/// The observation of a request that is the head of its terminal's queue, held for `hold`.
let private waitingOn (hold: TerminalQueueDrain.TerminalHold option) : TerminalCommandWait.Observation =
    { Block = None; InQueue = true; IsHead = true; Hold = hold; Interactive = false }

/// The observation of a request whose block exists — running or finished — with nothing left
/// in the queue. `interactive` is whether detection holds the terminal.
let private observing (status: TerminalBlockStatus) (interactive: bool) : TerminalCommandWait.Observation =
    { Block = Some (blockOf status); InQueue = false; IsHead = false; Hold = None; Interactive = interactive }

let private waitTests =
    testList "The command wait" [
        testCase "a held terminal returns at once rather than burning a deadline" <| fun () ->
            // A peer with a terminal open is mid-task and will not be done soon.
            Expect.equal
                (TerminalCommandWait.step false (waitingOn (Some TerminalQueueDrain.AwaitingTerminal)))
                (TerminalCommandWait.Return TerminalCommandAwaitingTerminal)
                "no waiting at all"

        testCase "waiting on a PROCESS gets the process deadline" <| fun () ->
            // A command running ahead of ours in the same terminal, and our own command once
            // it starts: both are waits on a process, so a quick one still chains.
            for observation in
                [ waitingOn (Some TerminalQueueDrain.AwaitingBlock)
                  observing BlockRunning false ] do
                Expect.equal
                    (TerminalCommandWait.step false observation)
                    TerminalCommandWait.KeepWaiting
                    "inside the deadline it keeps waiting"
            Expect.equal
                (TerminalCommandWait.step true (waitingOn (Some TerminalQueueDrain.AwaitingBlock)))
                (TerminalCommandWait.Return TerminalCommandAwaitingTerminal)
                "the terminal was never free"
            Expect.equal
                (TerminalCommandWait.step true (observing BlockRunning false))
                (TerminalCommandWait.Return TerminalCommandRunning)
                "and a running block yields — the deadline is a yield, not a cancellation"

        testCase "a block that has taken the screen is reported at once, deadline or no" <| fun () ->
            // The one running block that will never finish on its own: it is waiting for the
            // caller. Burning two minutes and then saying "still running" would spend the
            // deadline telling the only party who can end it to be patient.
            Expect.equal
                (TerminalCommandWait.step false (observing BlockRunning true))
                (TerminalCommandWait.Return TerminalCommandInteractive)
                "no waiting at all"

        testCase "an entry BEHIND another waits for the queue, whatever the head waits for" <| fun () ->
            // Reporting the head's reason as ours would misattribute somebody else's wait.
            let behind : TerminalCommandWait.Observation =
                { Block = None; InQueue = true; IsHead = false; Hold = Some TerminalQueueDrain.AwaitingBlock; Interactive = false }
            Expect.equal (TerminalCommandWait.step false behind) TerminalCommandWait.KeepWaiting "still queued"
            Expect.equal
                (TerminalCommandWait.step true behind)
                (TerminalCommandWait.Return TerminalCommandAwaitingTerminal)
                "and it names the queue"

        testCase "an outcome ends the wait, deadline or no" <| fun () ->
            for status, expected in
                [ BlockFinished (CommandSucceeded 0), TerminalCommandRan (CommandSucceeded 0)
                  BlockFinished (CommandFailed 3), TerminalCommandRan (CommandFailed 3)
                  BlockRejected (PeerRef bob, Some "not on prod"), TerminalCommandRefused (PeerRef bob, Some "not on prod") ] do
                Expect.equal
                    (TerminalCommandWait.step false (observing status false))
                    (TerminalCommandWait.Return expected)
                    "an answer is returned the moment it exists"

        testCase "a withdrawn request is an absence, not an outcome" <| fun () ->
            // Deleting a queued entry is withdrawal and has no event. Reporting it as any
            // status would be inventing one.
            let withdrawn : TerminalCommandWait.Observation =
                { Block = None; InQueue = false; IsHead = false; Hold = None; Interactive = false }
            Expect.equal
                (TerminalCommandWait.step false withdrawn)
                TerminalCommandWait.Gone
                "the caller is told the request is gone"

        testCase "every status the agent can be handed says which state it is in" <| fun () ->
            // The wording is the mechanism, not decoration: told "queued" when it is blocked
            // on something else, a model concludes after a silent pause that the command
            // failed and tries something else. Every case must be distinguishable, so none
            // may collapse into another.
            let statuses =
                [ TerminalCommandRan (CommandSucceeded 0)
                  TerminalCommandRan (CommandFailed 1)
                  TerminalCommandRunning
                  TerminalCommandInteractive
                  TerminalCommandAwaitingTerminal
                  TerminalCommandRefused (PeerRef bob, None) ]
            Expect.equal (List.distinct statuses |> List.length) (List.length statuses) "no two are the same value"
    ]

let private leaseCommandTests =
    testList "Lease commands" [
        testCaseAsync "take and release route to the lease, attributed to the peer who asked" <|
            async {
                let calls = ResizeArray<string> ()
                let handle =
                    SessionCommands.handle
                        (fun _ _ -> Ok ())
                        (fun _ _ _ -> async { return Error "not this test" })
                        (fun _ _ -> async { return Error "not this test" })
                        (fun id by -> async { calls.Add (sprintf "take:%s:%A" (TerminalId.value id) by); return Ok () })
                        (fun id by ->
                            async {
                                calls.Add (sprintf "release:%s:%A" (TerminalId.value id) by)
                                return Error "another peer holds this terminal"
                            })
                        (fun id ->
                            async {
                                calls.Add (sprintf "rearm:%s" (TerminalId.value id))
                                return Ok ()
                            })
                        (fun id ->
                            async {
                                calls.Add (sprintf "reattach:%s" (TerminalId.value id))
                                return Ok id
                            })
                        PeerRef
                let! taken = handle ada (TakeTerminalLease terminalA)
                Expect.equal taken CommandAccepted "a take always succeeds — it steals rather than asks"
                let! released = handle ada (ReleaseTerminalLease terminalA)
                Expect.equal
                    released
                    (CommandRejected "another peer holds this terminal")
                    "and a release you are not entitled to is refused, with the reason"
                // Re-arming carries no actor at all: it repairs a terminal rather than taking
                // anything from anyone, so who pressed it decides nothing.
                let! rearmed = handle bob (RearmTerminal terminalA)
                Expect.equal rearmed CommandAccepted "any peer may re-arm"
                Expect.equal
                    (List.ofSeq calls)
                    [ sprintf "take:%s:%A" (TerminalId.value terminalA) (PeerRef ada)
                      sprintf "release:%s:%A" (TerminalId.value terminalA) (PeerRef ada)
                      sprintf "rearm:%s" (TerminalId.value terminalA) ]
                    "the lease commands carry the asking peer's actor; the repair carries none"
            }
    ]

let private turnStarted (n: string) =
    SessionEvent.AgentTurnStarted
        { AgentTurnId = AgentTurnId.create ("t-" + n) |> expect
          TriggeredByMessageId = Some (MessageId.create ("m-" + n) |> expect); Woke = None }

/// A reader that answers from a per-terminal string, so the digest's own slicing is what
/// is under test rather than a transcript store's.
let private readsBack (text: string) : TerminalId -> int -> int option -> string =
    fun _ _ _ -> text

let private digestOf events =
    fold events |> TerminalDigest.build (readsBack "") (TerminalDigest.window events)

let private digestTests =
    testList "Terminal digest" [
        testCase "the window is everything since the PREVIOUS turn began" <| fun () ->
            let events =
                [ opened terminalA "build"
                  started terminalA "1" "old" 0
                  completed terminalA "1" (CommandSucceeded 0) 2
                  turnStarted "1"
                  started terminalA "2" "new" 2
                  completed terminalA "2" (CommandSucceeded 0) 5 ]
            Expect.equal
                (digestOf events |> List.map (fun d -> d.Command))
                [ "new" ]
                "a block that both started and finished before the last turn is old news"

        testCase "a block that finishes DURING the turn is reported, though it started before" <| fun () ->
            // The case the agent is actually waiting on: it queued something, the turn
            // ended, the command finished afterwards. Keying the window on starts alone
            // would drop precisely the outcome it asked for.
            let events =
                [ opened terminalA "build"
                  started terminalA "1" "make" 0
                  turnStarted "1"
                  completed terminalA "1" (CommandFailed 2) 7 ]
            let digest = digestOf events
            Expect.equal (digest |> List.map (fun d -> d.Command)) [ "make" ] "it is in the digest"
            Expect.equal digest.Head.Status (BlockFinished (CommandFailed 2)) "with the exit code it ended on"

        testCase "a still-running block is reported as running, not omitted" <| fun () ->
            let events = [ opened terminalA "build"; turnStarted "1"; started terminalA "1" "sleep 60" 0 ]
            let digest = digestOf events
            Expect.equal digest.Head.Status BlockRunning "the agent is told it has not finished"

        testCase "the digest carries who wrote the command" <| fun () ->
            let events =
                [ opened terminalA "build"
                  turnStarted "1"
                  SessionEvent.TerminalBlockStarted
                      { TerminalId = terminalA
                        BlockId = block "1"
                        QueueId = None
                        Authority = Authority.agentFor (PeerRef ada)
                        Command = "rm -rf build"
                        FromSeq = 0
                        Background = false }
                  completed terminalA "1" (CommandSucceeded 0) 3 ]
            let entry = (digestOf events).Head
            Expect.equal entry.Author ActorRef.Agent "the agent's own command"
            Expect.equal entry.Title "build" "named by its terminal, not an opaque id"

        testCase "output is capped from the FRONT, and the loss is stated" <| fun () ->
            // Keeping the tail is the whole point: a build's verdict is its last lines,
            // and a cap that kept the head would hand the agent the part it can guess.
            let long = String.replicate (TerminalDigest.tailCap + 500) "x"
            let events = [ opened terminalA "build"; turnStarted "1"; started terminalA "1" "make" 0 ]
            let digest = fold events |> TerminalDigest.build (readsBack long) (TerminalDigest.window events)
            Expect.equal digest.Head.OutputTail.Length TerminalDigest.tailCap "the tail is capped"
            Expect.equal digest.Head.Elided 500 "and what was dropped is counted, not silently elided"

        testCase "output that fits is not elided at all" <| fun () ->
            let events = [ opened terminalA "build"; turnStarted "1"; started terminalA "1" "make" 0 ]
            let digest = fold events |> TerminalDigest.build (readsBack "ok\n") (TerminalDigest.window events)
            Expect.equal digest.Head.OutputTail "ok\n" "it arrives whole"
            Expect.equal digest.Head.Elided 0 "with nothing claimed to be missing"

        testCase "blocks across several terminals all appear, in the order they ran" <| fun () ->
            let events =
                [ opened terminalA "build"
                  opened terminalB "logs"
                  turnStarted "1"
                  started terminalA "1" "make" 0
                  started terminalB "2" "tail -f log" 0 ]
            Expect.equal
                (digestOf events |> List.map (fun d -> d.Command))
                [ "make"; "tail -f log" ]
                "a terminal is not a filter — the agent sees the session's work"

        testCase "what a block PRINTED excludes what was typed into it" <| fun () ->
            // The command already rides on the block. Echoing the input record back into
            // its own output would have a reader count the command twice.
            let printed =
                Transcript.printed
                    [ { At = 0.0; Kind = TranscriptInput; Data = "make\n" }
                      { At = 0.1; Kind = TranscriptOutput; Data = "building" }
                      { At = 0.2; Kind = TranscriptResize; Data = "80x24" }
                      { At = 0.3; Kind = TranscriptStderr; Data = "!" } ]
            Expect.equal printed "building!" "output and stderr, in order, and nothing else"
    ]

// --- ANSI -------------------------------------------------------------------------------

let private lineTexts (lines: AnsiLine list) =
    lines |> List.map (fun l -> l.Spans |> List.map (fun s -> s.Text) |> String.concat "")

let private ansiTests =
    testList "ANSI output parsing" [
        testCase "SGR colours a run and the reset ends it" <| fun () ->
            let lines = Ansi.parse "plain \u001b[31mred\u001b[0m done"
            let spans = lines.Head.Spans
            Expect.equal (spans |> List.map (fun s -> s.Text)) [ "plain "; "red"; " done" ] "three runs"
            Expect.equal spans.[1].Style.Foreground (IndexedColour 1) "the middle one is red"
            Expect.equal spans.[2].Style.Foreground DefaultColour "and the reset really resets"

        testCase "256-colour and 24-bit selectors are understood" <| fun () ->
            let byIndex = (Ansi.parse "\u001b[38;5;208mx").Head.Spans.Head
            Expect.equal byIndex.Style.Foreground (IndexedColour 208) "38;5;n is an index"
            let byRgb = (Ansi.parse "\u001b[38;2;10;20;30mx").Head.Spans.Head
            Expect.equal byRgb.Style.Foreground (RgbColour (10, 20, 30)) "38;2;r;g;b is a colour"

        testCase "a bare ESC[m is a reset" <| fun () ->
            let spans = (Ansi.parse "\u001b[1mbold\u001b[mplain").Head.Spans
            Expect.isTrue spans.Head.Style.Bold "bold applied"
            Expect.isFalse spans.[1].Style.Bold "and cleared by the empty SGR"

        testCase "carriage return rewrites the line, so a progress bar is one line" <| fun () ->
            Expect.equal (lineTexts (Ansi.parse "10%\r50%\r100%")) [ "100%" ] "only the final state survives"

        testCase "CRLF is one line break, not an erase" <| fun () ->
            Expect.equal (lineTexts (Ansi.parse "one\r\ntwo")) [ "one"; "two" ] "two lines"

        testCase "backspace erases the character before it" <| fun () ->
            Expect.equal (lineTexts (Ansi.parse "abc\b\bx")) [ "ax" ] "two characters removed, one added"

        testCase "cursor moves, mode sets and OSC titles never reach the screen as text" <| fun () ->
            // The rule this pins: an escape this does not implement must produce NOTHING,
            // because printing `ESC[?25l` at a person is worse than printing nothing.
            let lines = Ansi.parse "\u001b[?25lhidden\u001b[2K\u001b]0;a title end"
            Expect.equal (lineTexts lines) [ "hidden end" ] "only the real text remains"

        testCase "plain text is recoverable from a styled parse" <| fun () ->
            let text = "\u001b[32mgreen\u001b[0m\nsecond"
            Expect.equal (Ansi.plainText (Ansi.parse text)) "green\nsecond" "the words without the paint"

        testCase "style carries across a chunk boundary" <| fun () ->
            // Live output arrives in arbitrary pieces; a colour opened in one must not
            // reset merely because the next byte came in a different frame.
            let first, style = Ansi.parseFrom AnsiStyle.plain "\u001b[31mred"
            let second, _ = Ansi.parseFrom style " still red"
            Expect.equal (List.head first).Spans.Head.Style.Foreground (IndexedColour 1) "opened in the first piece"
            Expect.equal (List.head second).Spans.Head.Style.Foreground (IndexedColour 1) "still set in the second"

        testCase "the arithmetic palette resolves; the sixteen named colours do not" <| fun () ->
            Expect.equal (AnsiColour.rgbOf (IndexedColour 196)) (Some (255, 0, 0)) "the cube is arithmetic"
            Expect.equal (AnsiColour.rgbOf (IndexedColour 232)) (Some (8, 8, 8)) "so is the grey ramp"
            Expect.equal
                (AnsiColour.rgbOf (IndexedColour 1))
                None
                "but 'red' is the theme's word, resolved where the contrast floor is known"
    ]

// --- The transcript ---------------------------------------------------------------------

/// An in-memory transcript of `records` output lines, plus the header at line 0 — the setup
/// the cursor cases share. Hoisted rather than repeated, because it is the ARRANGEMENT that
/// is common to them; what each one asserts is its own.
let private recorded (records: int) : Yession.Host.TranscriptStore.TranscriptStore =
    let store = Yession.Host.TranscriptStore.inMemory ()
    let transcript = store.Open terminalA { Width = 80; Height = 24; Timestamp = 0L }
    for i in 1 .. records do
        transcript.Append { At = 0.0; Kind = TranscriptOutput; Data = string i } |> ignore
    store

let private onlcrTests =
    testList "ONLCR at capture (Plan 25, stage 1)" [
        // What a tty's line discipline does to a bare LF on the way out, applied to the
        // sources that never had a tty. Without it a VT — the player, and the emulator the
        // keyframes are serialized from — starts every line where the last one ended.
        testCase "a lone newline becomes a carriage return and a newline" <| fun () ->
            Expect.equal (Onlcr.normalize false "total 4\nfile\n" |> fst) "total 4\r\nfile\r\n" "each LF gains its CR"

        testCase "a newline that already has its carriage return is left alone" <| fun () ->
            // Which is what lets pty bytes pass through untouched, and what makes normalizing
            // twice the same as normalizing once.
            Expect.equal (Onlcr.normalize false "done\r\n" |> fst) "done\r\n" "no second CR"

        testCase "a carriage return split across two chunks does not gain a second one" <| fun () ->
            // The one thing a chunk cannot see for itself. A progress bar rewriting its line
            // reads back at exactly this boundary, and a doubled CR is a byte the terminal
            // never printed sitting in the record whose purpose is fidelity.
            let first, carry = Onlcr.normalize false "done\r"
            Expect.equal first "done\r" "the CR passes through"
            Expect.isTrue carry "and is remembered"
            Expect.equal (Onlcr.normalize carry "\nnext" |> fst) "\nnext" "so the LF that follows is already paired"

        testCase "a carriage return without a newline is not a line ending" <| fun () ->
            // `\r` alone returns the cursor and keeps the row: how a progress bar overwrites
            // itself. Treating it as a line ending would insert breaks a reader never saw.
            Expect.equal (Onlcr.normalize false "50%\r100%" |> fst) "50%\r100%" "untouched"

        testCase "an empty chunk keeps the carry it was given" <| fun () ->
            // A stream can be read empty at any moment; forgetting the carry there would let
            // the next chunk double a CR that arrived before it.
            Expect.equal (Onlcr.normalize true "") ("", true) "nothing in, nothing changed"
    ]

let private transcriptTests =
    testList "Transcript" [
        // Plan 22. A client numbers an answer from what it ASKED, because a transcript line
        // cannot carry its own index — the file is an asciicast, and a private index field in
        // it would stop it being one. So the four cases below are the whole contract that
        // numbering rests on, and each is pinned alone.
        testCase "the answer to a cursor begins one line past it" <| fun () ->
            let store = recorded 10
            Expect.equal
                (store.BoundsAfter terminalA (Some 4) |> Option.map fst)
                (Some 5)
                "a client sitting at line 4 is answered from line 5"

        testCase "a cursor with no position is answered from the very start" <| fun () ->
            // Which is what puts the asciicast header — line 0, and nowhere else — in the
            // first answer, and so in the store of a client that has never read this before.
            let store = recorded 10
            Expect.equal
                (store.BoundsAfter terminalA None |> Option.map fst)
                (Some 0)
                "and line 0 is the header"

        testCase "a cursor at the tail asks for nothing rather than for an empty range" <| fun () ->
            // An empty range would be an address a client keeps for ever, and "nothing yet"
            // is exactly the thing that stops being true. `None` here is the `204`.
            let store = recorded 10
            Expect.equal
                (store.BoundsAfter terminalA (Some 10)) // the header plus ten records
                None
                "a client that has read every line is told to keep its cursor"

        testCase "a range the transcript has not reached is no answer at all" <| fun () ->
            // Answering short would put a partial answer at an address that named the whole
            // range — and the client keeps what it is given, for ever.
            let store = recorded 10
            Expect.equal
                (store.ReadLines terminalA 0 99)
                None
                "the address promised a hundred lines and the transcript has eleven"

        testCase "keyframes live in a SIDECAR, and survive the process that wrote them" <| fun () ->
            // Plan 14, stage 3. Never in the `.cast`: Plan 13 bought a standard, replayable
            // format on purpose, and a private record type inside it spends that — so the
            // transcript a stranger's player reads must be byte-identical with or without
            // keyframes beside it.
            let dir = sprintf "tests/Yession.Tests/out/.data/keyframes-%s" (string (System.Guid.NewGuid ()))
            let store = Yession.Host.TranscriptStore.openStore dir
            let transcript = store.Open terminalA { Width = 80; Height = 24; Timestamp = 0L }
            transcript.Append { At = 0.0; Kind = TranscriptOutput; Data = "before\r\n" } |> ignore
            transcript.Keyframe { Seq = 2; Cols = 100; Rows = 30; Screen = "SCREEN" }
            transcript.Append { At = 0.1; Kind = TranscriptOutput; Data = "after\r\n" } |> ignore

            let cast = readFileSync nodeFs (sprintf "%s/%s.cast" dir (TerminalId.value terminalA))
            Expect.isFalse (cast.Contains "SCREEN") "the recording is exactly what the terminal printed"

            // Read back through a SECOND store over the same directory — the restart case,
            // and the only one that shows the sidecar is a file rather than a field.
            let reopened = Yession.Host.TranscriptStore.openStore dir
            Expect.equal
                (reopened.ReadKeyframe terminalA 2)
                (Some { Seq = 2; Cols = 100; Rows = 30; Screen = "SCREEN" })
                "the keyframe outlives the handle that wrote it"
            Expect.isNone (reopened.ReadKeyframe terminalA 1) "and a line with no keyframe says so"
            Expect.isNone (reopened.ReadKeyframe terminalB 2) "as does a terminal with no sidecar at all"

        testCase "records encode as asciicast v2, so any player can read a transcript" <| fun () ->
            let header = Codec.toString Codec.transcriptLine (TranscriptHeaderLine { Width = 80; Height = 24; Timestamp = 1754092800L })
            Expect.isTrue (header.Contains "\"version\":2") "the header declares version 2"
            let record =
                Codec.toString Codec.transcriptLine (TranscriptRecordLine { At = 1.5; Kind = TranscriptOutput; Data = "hi" })
            // A bare three-element array: `[time, code, data]`. Asserted on the TEXT because
            // the format is asciinema's, and matching it is the whole point of using it.
            Expect.equal record "[1.5,\"o\",\"hi\"]" "a record is a bare [time, code, data] array"

        testCase "every line round-trips" <| fun () ->
            let lines =
                [ TranscriptHeaderLine { Width = 120; Height = 40; Timestamp = 1L }
                  TranscriptRecordLine { At = 0.0; Kind = TranscriptOutput; Data = "out" }
                  TranscriptRecordLine { At = 0.25; Kind = TranscriptStderr; Data = "err" }
                  TranscriptRecordLine { At = 0.5; Kind = TranscriptInput; Data = "ls\n" }
                  TranscriptRecordLine { At = 0.75; Kind = TranscriptResize; Data = "100x30" } ]
            for line in lines do
                let encoded = Codec.toString Codec.transcriptLine line
                Expect.equal (Codec.fromString Codec.transcriptLine encoded) (Ok line) ("round-trips: " + encoded)

        // The replay (stage 3e) is built from what the client already fetched rather than
        // from a new whole-file route, and that is only sound if the reassembly is the FILE.
        // This drives the real route end to end — write through the store, read the chunks a
        // client reads, decode them as a client decodes them, rebuild — and compares against
        // the recording on disk byte for byte.
        testCase "a replay rebuilt from fetched answers IS the recording on disk" <| fun () ->
            let dir = sprintf "tests/Yession.Tests/out/.data/replay-%s" (string (System.Guid.NewGuid ()))
            let store = Yession.Host.TranscriptStore.openStore dir
            let header = { Width = 120; Height = 40; Timestamp = 1754092800L }
            let transcript = store.Open terminalA header
            for record in
                [ { At = 0.0; Kind = TranscriptInput; Data = "ls -la\n" }
                  { At = 0.1; Kind = TranscriptOutput; Data = "[32mtotal 0[0m\n" }
                  { At = 0.2; Kind = TranscriptStderr; Data = "warning\n" }
                  { At = 0.3; Kind = TranscriptResize; Data = "100x30" } ] do
                transcript.Append record |> ignore
            // What a client holds after fetching: decoded lines, keyed by sequence number.
            let first, last =
                store.BoundsAfter terminalA None |> Option.defaultWith (fun () -> failwith "no lines")
            let lines =
                store.ReadLines terminalA first last
                |> Option.defaultWith (fun () -> failwith "the bounds named lines the store would not read")
            let decoded =
                lines
                |> List.mapi (fun i line -> first + i, Codec.fromString Codec.transcriptLine line)
                |> List.choose (fun (seq, line) ->
                    match line with
                    | Ok (TranscriptRecordLine record) -> Some (seq, record)
                    | _ -> None)
            // Deliberately shuffled: chunks arrive in whatever order the fetches settle, and
            // the map a client keeps them in has no order at all. Sequence order is the
            // recording's order, and `cast` is what restores it.
            let cast = TranscriptReplay.cast header (List.rev decoded)
            Expect.equal
                cast
                (readFileSync nodeFs (sprintf "%s/%s.cast" dir (TerminalId.value terminalA)))
                "the rebuilt cast is the file"

        // A terminal can hold no records the client has: one that printed nothing, or one
        // whose output the cap (stage 3d) refused before a single chunk arrived. A cast of
        // nothing must still be a VALID asciicast — a header and no frames — rather than an
        // empty file the player reports as broken, because "this terminal printed nothing
        // that is still kept" is a thing the surface says.
        testCase "a recording with no records left is still a valid cast" <| fun () ->
            let cast = TranscriptReplay.cast { Width = 80; Height = 24; Timestamp = 0L } []
            let lines = cast.Split '\n' |> Array.filter (fun l -> l.Trim().Length > 0)
            Expect.equal lines.Length 1 "the header, and nothing else"
            Expect.equal
                (Codec.fromString Codec.transcriptLine lines.[0])
                (Ok (TranscriptHeaderLine { Width = 80; Height = 24; Timestamp = 0L }))
                "and it is the header"
    ]

// --- Wire codecs --------------------------------------------------------------------------

let private codecTests =
    testList "Terminal wire codecs" [
        testCase "every terminal event round-trips" <| fun () ->
            let events =
                [ opened terminalA "build"
                  SessionEvent.TerminalClosed { TerminalId = terminalA; Reason = "closed by a peer" }
                  SessionEvent.TerminalBlockStarted
                      { TerminalId = terminalA
                        BlockId = block "1"
                        QueueId = Some (queue "a1")
                        Authority = Authority.agentFor (PeerRef bob)
                        Command = "ls -la"
                        FromSeq = 3
                        Background = false }
                  completed terminalA "1" CommandTimedOut 9
                  SessionEvent.TerminalTranscriptTruncated
                      { TerminalId = terminalA; BlockId = None; DroppedBytes = 17 }
                  SessionEvent.TerminalLeaseTaken { TerminalId = terminalA; By = PeerRef ada; FromSeq = 0 }
                  // All three endings, because the reason is the whole value of the event: a
                  // release, a steal and a dropped connection read differently in a log.
                  SessionEvent.TerminalLeaseReleased
                      { TerminalId = terminalA; Was = PeerRef ada; Reason = LeaseReleased; ToSeq = 0 }
                  SessionEvent.TerminalLeaseReleased
                      { TerminalId = terminalA; Was = PeerRef ada; Reason = LeaseStolen (PeerRef bob); ToSeq = 0 }
                  SessionEvent.TerminalLeaseReleased
                      { TerminalId = terminalA; Was = ActorRef.Agent; Reason = LeaseHolderGone; ToSeq = 0 }
                  SessionEvent.TerminalLeaseReleased
                      { TerminalId = terminalA; Was = PeerRef ada; Reason = LeaseIdle; ToSeq = 0 }
                  SessionEvent.TerminalIntegrationLost { TerminalId = terminalA; BlockId = Some (block "1") }
                  SessionEvent.TerminalIntegrationLost { TerminalId = terminalA; BlockId = None }
                  SessionEvent.TerminalIntegrationRestored { TerminalId = terminalA } ]
            for event in events do
                let encoded = Codec.toString Codec.sessionEvent event
                Expect.equal (Codec.fromString Codec.sessionEvent encoded) (Ok event) ("round-trips: " + encoded)

        // A round-trip cannot see this: nesting the three parties under one key would
        // round-trip perfectly and make every block ever written unreadable. An event log is
        // read back for the life of its session, so where the keys SIT is the contract, and
        // moving the value into `Authority` (Plan 20) had to leave it exactly where it was.
        testCase "a block's parties stay top-level keys on the wire" <| fun () ->
            let encoded =
                SessionEvent.TerminalBlockStarted
                    { TerminalId = terminalA
                      BlockId = block "1"
                      QueueId = Some (queue "a1")
                      Authority = Authority.agentFor (PeerRef bob)
                      Command = "ls -la"
                      FromSeq = 3
                      Background = false }
                |> Codec.toString Codec.sessionEvent
            for key in [ "\"author\""; "\"onBehalfOf\"" ] do
                Expect.isTrue (encoded.Contains key) (sprintf "%s is still written: %s" key encoded)
            Expect.isFalse (encoded.Contains "\"authority\"") "and the F# shape did not reach the wire"

        testCase "a block written before Plan 20 still decodes" <| fun () ->
            // No `onBehalfOf` and no `background`: what every block in an existing log looks
            // like. A `Required` field for either would make those pages undecodable, which
            // is a session that will not open.
            let old =
                """{"type":"terminalBlockStarted","payload":{"terminalId":"term-a","blockId":"blk-1","""
                + """"queueId":null,"author":{"kind":"agent"},"approvedBy":null,"command":"ls","fromSeq":0}}"""
            match Codec.fromString Codec.sessionEvent old with
            | Ok (SessionEvent.TerminalBlockStarted decoded) ->
                Expect.isFalse decoded.Background "it ran in the foreground, which is what its absence means"
                Expect.equal
                    (Authority.onBehalfOf decoded.Authority)
                    None
                    "and it borrowed nobody's authority, which is what that absence means"
            | other -> failwithf "a pre-Plan-20 block must still read back, got %A" other

        testCase "a block somebody approved before Plan 23 still decodes" <| fun () ->
            // The replay-safety claim the whole hard cut leans on: `approvedBy` keys in old
            // logs are ignored, never fatal — a decode failure in the event store is a
            // session that will not open. Pinned as literal JSON, so a future codec change
            // that breaks old-log replay goes red here rather than at somebody's boot.
            let approved =
                """{"type":"terminalBlockStarted","payload":{"terminalId":"term-a","blockId":"blk-1","""
                + """"queueId":"q-a1","author":{"kind":"agent"},"onBehalfOf":{"kind":"peer","peerId":"ada"},"""
                + """"approvedBy":{"kind":"peer","peerId":"bob"},"command":"ls","fromSeq":0}}"""
            match Codec.fromString Codec.sessionEvent approved with
            | Ok (SessionEvent.TerminalBlockStarted decoded) ->
                Expect.equal (Authority.author decoded.Authority) ActorRef.Agent "the author survives"
                Expect.equal
                    (Authority.onBehalfOf decoded.Authority)
                    (Some (PeerRef ada))
                    "and whose authority it ran on"
            | other -> failwithf "an approved pre-Plan-23 block must still read back, got %A" other

        testCase "a repo event somebody approved before Plan 23 still decodes" <| fun () ->
            let approved =
                """{"type":"repoAdded","payload":{"messageId":"msg-1","repo":"octo/hello","""
                + """"branch":"main","actor":{"kind":"agent"},"approvedBy":{"kind":"peer","peerId":"bob"}}}"""
            match Codec.fromString Codec.sessionEvent approved with
            | Ok (SessionEvent.RepoAdded decoded) ->
                Expect.equal decoded.Actor ActorRef.Agent "the actor survives the retired key beside it"
            | other -> failwithf "an approved pre-Plan-23 repo event must still read back, got %A" other

        testCase "terminal frames round-trip over the session transport" <| fun () ->
            let codec = Codec.sessionFrame Codec.string
            let frames =
                [ Terminal (TerminalRecord (terminalA, 7, { At = 1.0; Kind = TranscriptOutput; Data = "hi" }))
                  Terminal (TerminalTranscriptAvailable (terminalA, 42))
                  // The screen a joining peer renders: the seq it composes with, and the
                  // geometry it was painted at.
                  Terminal (TerminalSnapshot (terminalA, { Seq = 42; Cols = 120; Rows = 40; Screen = "screen" }))
                  // Live mode's two peer-authored frames (stage 2e).
                  Terminal (TerminalInput (terminalA, "\u001b[A"))
                  Terminal (TerminalResize (terminalA, 120, 40)) ]
            for frame in frames do
                let encoded = Codec.toString codec frame
                Expect.equal (Codec.fromString codec encoded) (Ok frame) ("round-trips: " + encoded)

        testCase "a snapshot with no geometry is the size every terminal opens at" <| fun () ->
            // The size was added to a frame that already existed, and a client's bundle is
            // served over a cache it may not have refreshed. A snapshot written without it
            // came from a Process that had resized nothing, so 80x24 is not a fallback guess
            // here — it is what that screen was painted at.
            let codec = Codec.sessionFrame Codec.string
            let older =
                """{"tag":"terminal","payload":{"kind":"snapshot","terminalId":"term-a",""" +
                """"seq":42,"screen":"screen"}}"""
            match Codec.fromString codec older with
            | Ok (Terminal (TerminalSnapshot (_, keyframe))) ->
                Expect.equal keyframe.Seq 42 "the fields it did carry are read"
                Expect.equal keyframe.Screen "screen" "including the screen"
                Expect.equal (keyframe.Cols, keyframe.Rows) (80, 24) "and the one it did not is the opening size"
            | other -> failwithf "a snapshot written before the geometry must still read back, got %A" other

        testCase "the terminal commands and focus fields round-trip" <| fun () ->
            let codec = Codec.sessionFrame Codec.string
            let frames =
                [ Command (Request (RequestId.fresh (), OpenTerminal "build"))
                  Command (Request (RequestId.fresh (), CloseTerminal terminalA))
                  Command (Request (RequestId.fresh (), TakeTerminalLease terminalA))
                  Command (Request (RequestId.fresh (), ReleaseTerminalLease terminalA))
                  Command (Request (RequestId.fresh (), RearmTerminal terminalA))
                  Presence
                      { PeerId = ada
                        DisplayName = "Ada"
                        Focus =
                          Some
                              { Field = TerminalDraftBody (terminalA, ada)
                                Pos = { Anchor = "AQI="; Head = "AwQ=" } } }
                  Presence
                      { PeerId = bob
                        DisplayName = "Bob"
                        Focus = Some { Field = TerminalQueuedBody (queue "a1"); Pos = { Anchor = "AQI="; Head = "AQI=" } } } ]
            for frame in frames do
                let encoded = Codec.toString codec frame
                Expect.equal (Codec.fromString codec encoded) (Ok frame) ("round-trips: " + encoded)

        testCase "an actor token round-trips through the CRDT's one-string form" <| fun () ->
            let actors =
                [ ActorRef.Agent
                  ActorRef.SessionProcess
                  ActorRef.System
                  PeerRef ada
                  UserRef (UserId.create "https://issuer/sub:with:colons" |> expect) ]
            for actor in actors do
                Expect.equal (ActorRef.ofToken (ActorRef.token actor)) (Some actor) "round-trips"
            Expect.equal (ActorRef.ofToken "nonsense") None "an unreadable token is skipped, never guessed"
    ]

// --- Queue order ---------------------------------------------------------------------------

let private orderTests =
    testList "Terminal queue order" [
        testCase "ordering is per terminal" <| fun () ->
            let q =
                queueOf
                    [ entry "a1" terminalA (PeerRef ada) 2.0
                      entry "a2" terminalA (PeerRef ada) 1.0
                      entry "b1" terminalB (PeerRef bob) 5.0 ]
            Expect.equal
                (TerminalQueueOrder.sortedFor terminalA q |> List.map (fun e -> QueueId.value e.QueueId))
                [ "q-a2"; "q-a1" ]
                "A's entries, in order"
            Expect.equal (TerminalQueueOrder.nextFor terminalB q) 6.0 "the tail of B's queue, not of everything"

        testCase "moving an entry never leaves its terminal" <| fun () ->
            let q =
                queueOf
                    [ entry "a1" terminalA (PeerRef ada) 1.0
                      entry "a2" terminalA (PeerRef ada) 2.0
                      entry "b1" terminalB (PeerRef bob) 1.0 ]
            let moved = TerminalQueueOrder.moveUp q (queue "a2") |> Option.get
            Expect.isTrue (moved < 1.0) "it moves ahead of A's head"
            Expect.equal (TerminalQueueOrder.moveUp q (queue "a1")) None "the head of a terminal cannot move up"
            Expect.equal (TerminalQueueOrder.moveDown q (queue "b1")) None "a lone entry cannot move down"
    ]

// --- The terminal manager, over a scripted sandbox -----------------------------------------

/// A `SessionEnvironment` whose spawns are scripted: each returns the given output chunks
/// and exit code. Deterministic, and the same seam the real WorkSandbox implements — so
/// the block lifecycle under test is exactly the production one, with the process removed.
let private scriptedEnvironment (script: string -> (OutputStream * string) list * int) =
    let spawned = ResizeArray<SandboxExec> ()
    let environment : SessionEnvironment.SessionEnvironment =
        { Ensure = fun _ _ -> async { return EnvironmentAvailable }
          Spawn =
            fun exec onChunk ->
                async {
                    spawned.Add exec
                    let command = exec.Arguments |> List.tryLast |> Option.defaultValue ""
                    let chunks, code = script command
                    chunks |> List.iter onChunk
                    return
                        Ok
                            { WriteStdin = ignore
                              CloseStdin = ignore
                              Kill = ignore
                              Exited = async { return SandboxExited code } }
                }
          SpawnPty = fun _ _ _ _ -> async { return Error "no pty in this fixture" }
          Stop = fun () -> async { return () }
          CurrentRef = fun () -> Some "scripted" }
    environment, spawned

/// An in-memory transcript, a reader for what it holds, and a reader for the keyframes the
/// Session Process recorded beside it (Plan 14, stage 3).
let private recordingTranscripts () =
    let lines = Collections.Generic.Dictionary<string, ResizeArray<TranscriptLine>> ()
    let keyframes = Collections.Generic.Dictionary<string, ResizeArray<TranscriptKeyframe>> ()
    let keyframesFor (id: TerminalId) =
        let key = TerminalId.value id
        match keyframes.TryGetValue key with
        | true, existing -> existing
        | _ ->
            let created = ResizeArray<TranscriptKeyframe> ()
            keyframes.[key] <- created
            created
    // A keyframe is written OFF the block's path (the Process must not await the emulator
    // between consuming a queue entry and recording the block), so a test that read them the
    // instant `RunBlock` returned would be racing the emulator's own write barrier. Waiting
    // on the WRITE itself is what makes that deterministic — no clock, no sleep, no ordering
    // luck: the latch resolves when the thing under test has happened.
    let waiters = ResizeArray<(TerminalId * int) * (unit -> unit)> ()
    let settle () =
        let ready =
            waiters
            |> Seq.filter (fun ((id, count), _) -> (keyframesFor id).Count >= count)
            |> List.ofSeq
        for w in ready do waiters.Remove w |> ignore
        for (_, resume) in ready do resume ()
    let awaitKeyframes (id: TerminalId) (count: int) : Async<unit> =
        Async.FromContinuations (fun (cont, _, _) ->
            if (keyframesFor id).Count >= count then cont ()
            else waiters.Add ((id, count), cont))
    let linesFor (id: TerminalId) =
        let key = TerminalId.value id
        match lines.TryGetValue key with
        | true, existing -> existing
        | _ ->
            let created = ResizeArray<TranscriptLine> ()
            lines.[key] <- created
            created
    let openTranscript : OpenTranscript =
        fun id header ->
            let held = linesFor id
            if held.Count = 0 then held.Add (TranscriptHeaderLine header)
            { Append =
                fun record ->
                    let seq = held.Count
                    held.Add (TranscriptRecordLine record)
                    seq
              NextSeq = fun () -> held.Count
              Keyframe =
                fun keyframe ->
                    (keyframesFor id).Add keyframe
                    settle () }
    openTranscript,
    (fun (id: TerminalId) -> linesFor id |> List.ofSeq),
    (fun (id: TerminalId) -> keyframesFor id |> List.ofSeq),
    awaitKeyframes,
    // The reader, over the same lines the writer above appends to — so a test asserting on a
    // tail reads what the manager wrote, rather than a second recording free to disagree with
    // it. Header lines are skipped and the range is half-open, exactly as the real store's is.
    (fun (id: TerminalId) (fromSeq: int) (toSeq: int option) ->
        linesFor id
        |> Seq.indexed
        |> Seq.filter (fun (index, _) ->
            index >= fromSeq && (match toSeq with Some until -> index < until | None -> true))
        |> Seq.choose (fun (_, line) ->
            match line with
            | TranscriptRecordLine record -> Some record
            | _ -> None)
        |> List.ofSeq)

/// Note the exhaustion behaviour: the LAST id repeats for ever rather than running out. That
/// is deliberate for the cases that only ever open one or two terminals, and a trap for any
/// that open more — two terminals with one id are not two terminals. Keep the lists longer
/// than any case needs.
let private mintFrom (ids: string list) =
    let remaining = ResizeArray<string> ids
    fun () ->
        let next = remaining.[0]
        if remaining.Count > 1 then remaining.RemoveAt 0
        next

let private makeTerminalsFrom attach classifier (log: EventLog<SessionEvent>) environment openTranscript readTranscript openAtBoot profilesAtBoot =
    let mintTerminal = mintFrom [ "term-a"; "term-b"; "term-c"; "term-d"; "term-e"; "term-f" ]
    let mintBlock = mintFrom [ "b-1"; "b-2"; "b-3" ]
    let records = ResizeArray<TerminalId * int * TranscriptRecord> ()
    let opens = ResizeArray<TerminalId> ()
    let terminals =
        SessionTerminals.create
            log
            // One environment under every name: these tests are about the terminal
            // manager, not about which sandbox a terminal picked.
            (fun _ -> environment)
            openTranscript
            readTranscript
            // The REAL emulator, not a stub: the whole point of the manager tests is that
            // what the Process thinks the screen is comes from the same emulator a browser
            // renders with, and a stub here would test the wiring while proving nothing
            // about the screen.
            Yession.Host.Emulator.openEmulator
            SessionTerminals.TerminalShell.posix
            fixedClock
            (fun () -> TerminalId.create (mintTerminal ()) |> expect)
            (fun () -> BlockId.create (mintBlock ()) |> expect)
            // Fixed, because a test that cannot predict the nonce cannot assert on a mark.
            (fun () -> "test-nonce")
            (mintFrom [ "m-1"; "m-2"; "m-3" ] >> (fun raw -> MessageId.create raw |> expect))
            (fun id seq record -> records.Add (id, seq, record))
            (fun id -> opens.Add id)
            // No scheduler in these tests: the drain's re-arm is exercised where the drain is
            // (`TerminalScheduler`), and wiring a real one here would test the scheduler twice
            // while making every manager assertion depend on it.
            ignore
            attach
            classifier
            openAtBoot
            profilesAtBoot
    terminals, records, opens

/// No shell profile (Plan 25) — what a session that has never set one replays as, and what
/// every case here but the profile ones is about.
let private makeTerminalsGated attach classifier log environment openTranscript readTranscript openAtBoot =
    makeTerminalsFrom attach classifier log environment openTranscript readTranscript openAtBoot ShellProfileProjection.empty

/// The bypass classifier, which is what ships (Plan 23). A case about the classifier's
/// verdict passes its own.
let private makeTerminalsWith attach log environment openTranscript readTranscript openAtBoot =
    makeTerminalsGated attach Classifier.approveAll log environment openTranscript readTranscript openAtBoot

let private makeTerminals log environment openTranscript readTranscript openAtBoot =
    makeTerminalsWith AttachTerminal.unavailable log environment openTranscript readTranscript openAtBoot

let private managerTests =
    testList "Terminal manager" [
        testCaseAsync "opening a terminal ensures the environment and records the fact" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let terminals, _, _ = makeTerminals log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                let id = opened |> expect
                Expect.isTrue (terminals.IsOpen id) "it is open"
                let! events = eventsOf log
                match events with
                | [ SessionEvent.TerminalOpened e ] ->
                    Expect.equal e.Title "build" "the title is recorded"
                    Expect.equal e.OpenedBy (PeerRef ada) "attributed to whoever opened it"
                | other -> failwithf "expected one TerminalOpened, got %A" other
            }

        testCaseAsync "a block records its command, its output range, and its exit code" <|
            async {
                let log = newLog ()
                let environment, spawned =
                    scriptedEnvironment (fun _ -> [ Stdout, "hello\n"; Stderr, "warn\n" ], 0)
                let openTranscript, linesOf, _, _, readTranscript = recordingTranscripts ()
                let terminals, records, _ = makeTerminals log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                let id = opened |> expect
                let entry = entry "a1" id (PeerRef ada) 1.0
                let mutable startedCalled = 0
                do! terminals.RunBlock id entry "echo hello" (fun () -> startedCalled <- startedCalled + 1)

                Expect.equal startedCalled 1 "the consumed callback fires exactly once, at the durable start"
                Expect.equal
                    (spawned |> Seq.map (fun e -> e.Executable, e.Arguments) |> List.ofSeq)
                    [ "/bin/sh", [ "-c"; "echo hello" ] ]
                    "a command LINE is run through a shell"

                let! events = eventsOf log
                let startedEvent =
                    events |> List.pick (function SessionEvent.TerminalBlockStarted e -> Some e | _ -> None)
                let completedEvent =
                    events |> List.pick (function SessionEvent.TerminalBlockCompleted e -> Some e | _ -> None)
                Expect.equal startedEvent.Command "echo hello" "the command is snapshotted durably"
                Expect.equal startedEvent.QueueId (Some entry.QueueId) "and anchored to the queue entry it came from"
                Expect.equal completedEvent.Result (CommandSucceeded 0) "with its exit code"
                Expect.isTrue (completedEvent.ToSeq > startedEvent.FromSeq) "and a non-empty transcript range"

                // The bytes are in the TRANSCRIPT, not in the event log — the split the whole
                // design rests on.
                Expect.isEmpty
                    (events |> List.filter (function SessionEvent.CommandOutputReceived _ -> true | _ -> false))
                    "no output events: a terminal that prints a gigabyte adds four events"
                let transcript = linesOf id
                let recordsOf kind =
                    transcript
                    |> List.choose (function TranscriptRecordLine r when r.Kind = kind -> Some r.Data | _ -> None)
                Expect.equal (recordsOf TranscriptInput) [ "echo hello\r\n" ] "what was typed is recorded too"
                Expect.equal (recordsOf TranscriptOutput) [ "hello\r\n" ] "stdout"
                Expect.equal (recordsOf TranscriptStderr) [ "warn\r\n" ] "and stderr, still told apart"
                match transcript with
                | TranscriptHeaderLine _ :: _ -> ()
                | other -> failwithf "a transcript starts with its header, got %A" other

                Expect.equal
                    (records |> Seq.map (fun (_, seq, _) -> seq) |> List.ofSeq)
                    [ 1; 2; 3 ]
                    "every record is broadcast with the line index it was written at"
            }

        testCaseAsync "output captured off a pipe is recorded as a tty would have shown it" <|
            // Plan 25, stage 1. A block on an uninstrumented shell runs through pipes, so
            // nothing puts a line discipline between the program and us and its `\n` arrives
            // bare. Fed to a VT that is the staircase: every line starting where the last one
            // ended. The transcript is what both the player and the keyframe emulator read,
            // so the conversion belongs at capture and this is where it is pinned.
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [ Stdout, "total 4\nfile\n" ], 0)
                let openTranscript, linesOf, _, _, readTranscript = recordingTranscripts ()
                let terminals, _, _ = makeTerminals log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                let id = opened |> expect
                do! terminals.RunBlock id (entry "a1" id (PeerRef ada) 1.0) "ls" ignore
                let output =
                    linesOf id
                    |> List.choose (function
                        | TranscriptRecordLine r when r.Kind = TranscriptOutput -> Some r.Data
                        | _ -> None)
                Expect.equal output [ "total 4\r\nfile\r\n" ] "every line ending is one a terminal would have written"
            }

        testCaseAsync "a failing command keeps its output and reports the exit code" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [ Stderr, "no such file\n" ], 2)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let terminals, _, _ = makeTerminals log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                let id = opened |> expect
                do! terminals.RunBlock id (entry "a1" id (PeerRef ada) 1.0) "cat missing" ignore
                let! events = eventsOf log
                let completedEvent =
                    events |> List.pick (function SessionEvent.TerminalBlockCompleted e -> Some e | _ -> None)
                Expect.equal completedEvent.Result (CommandFailed 2) "the exit code is the result"
            }

        testCaseAsync "a keyframe is recorded at the block's first line, and paints the screen before it" <|
            async {
                // Plan 14, stage 3. The keyframe is written at range STARTS and nowhere
                // else, because those are the only positions a ranged replay ever asks for.
                // A block that runs after another has a screen it inherited, and that is
                // exactly what this has to carry.
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [ Stdout, "\u001b[31mred\r\n" ], 0)
                let openTranscript, _, readKeyframes, awaitKeyframes, readTranscript = recordingTranscripts ()
                let terminals, _, _ = makeTerminals log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                let id = opened |> expect
                do! terminals.RunBlock id (entry "a1" id (PeerRef ada) 1.0) "first" ignore
                do! terminals.RunBlock id (entry "a2" id (PeerRef ada) 2.0) "second" ignore
                // Both blocks have run; wait for both keyframes to be WRITTEN before reading
                // them, since the Process deliberately does not hold a block up for one.
                do! awaitKeyframes id 2

                let! events = eventsOf log
                let starts =
                    events |> List.choose (function SessionEvent.TerminalBlockStarted e -> Some e.FromSeq | _ -> None)
                let keyframes = readKeyframes id
                Expect.equal (keyframes |> List.map (fun k -> k.Seq)) starts "one keyframe per block, at its first line"
                // The second block inherited a screen the first one drew, and the keyframe
                // carries it — which is the whole reason a slice into a fresh VT is wrong.
                match List.tryItem 1 keyframes with
                | Some second -> Expect.isTrue (second.Screen.Contains "red") "the screen the second block started from"
                | None -> failwith "the second block recorded no keyframe"
                Expect.equal
                    (keyframes |> List.map (fun k -> k.Cols, k.Rows))
                    (keyframes |> List.map (fun _ -> TerminalSize.default'.Cols, TerminalSize.default'.Rows))
                    "each one carries the geometry the range actually ran under"
            }

        testCaseAsync "the block records who wrote the command" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let terminals, _, _ = makeTerminals log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                let id = opened |> expect
                do! terminals.RunBlock id (entry "a1" id ActorRef.Agent 1.0) "rm -rf build" ignore
                let! events = eventsOf log
                let startedEvent =
                    events |> List.pick (function SessionEvent.TerminalBlockStarted e -> Some e | _ -> None)
                Expect.equal
                    (Authority.author startedEvent.Authority)
                    ActorRef.Agent
                    "the agent wrote it, and the audit says so"
            }

        testCaseAsync "runaway output is capped, and the gap is stated rather than hidden" <|
            async {
                let log = newLog ()
                // Comfortably past the 4 MiB per-block cap.
                let flood = String.replicate 200 (String.replicate 40000 "y")
                let environment, _ = scriptedEnvironment (fun _ -> [ Stdout, flood ], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let terminals, _, _ = makeTerminals log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                let id = opened |> expect
                do! terminals.RunBlock id (entry "a1" id (PeerRef ada) 1.0) "yes" ignore
                let! events = eventsOf log
                let truncation =
                    events |> List.tryPick (function SessionEvent.TerminalTranscriptTruncated e -> Some e | _ -> None)
                match truncation with
                | Some t -> Expect.isTrue (t.DroppedBytes > 0) "the dropped bytes are counted"
                | None -> failwith "a capped block must record what it dropped"
            }

        testCaseAsync "terminals left open by a dead process are closed at boot" <|
            async {
                // The event log says open; the sandbox that hosted them died with its
                // session. Closing them is what keeps the projection describing reality.
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let terminals, _, _ = makeTerminals log environment openTranscript readTranscript [ terminalA; terminalB ]
                Expect.isTrue (terminals.IsOpen terminalA) "before boot reconciliation it still reads as open"
                do! terminals.ReconcileAtBoot ()
                let! events = eventsOf log
                let closed =
                    events |> List.choose (function SessionEvent.TerminalClosed e -> Some e.TerminalId | _ -> None)
                Expect.equal (closed |> List.map TerminalId.value) [ "term-a"; "term-b" ] "both are closed"
                Expect.isFalse (terminals.IsOpen terminalA) "and no longer open"
            }

        testCaseAsync "a block in a terminal that closed under it does nothing at all" <|
            async {
                let log = newLog ()
                let environment, spawned = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let terminals, _, _ = makeTerminals log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                let id = opened |> expect
                let! _ = terminals.Close id "closed by a peer"
                do! terminals.RunBlock id (entry "a1" id (PeerRef ada) 1.0) "make" ignore
                Expect.isEmpty (List.ofSeq spawned) "nothing is spawned"
                let! events = eventsOf log
                Expect.isEmpty
                    (events |> List.filter (function SessionEvent.TerminalBlockStarted _ -> true | _ -> false))
                    "and no block is recorded — the entry stays queued for a terminal that is gone"
            }
    ]

// --- The scheduler, over a real doc ---------------------------------------------------------

let private schedulerTests =
    testList "Terminal scheduler" [
        testCaseAsync "a queued command drains, runs, and leaves the doc" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [ Stdout, "ok\n" ], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let terminals, _, _ = makeTerminals log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                let id = opened |> expect
                let doc = Y.Doc.Create ()
                let scheduler = TerminalScheduler.create doc terminals ignore Set.empty
                // Queued exactly as the agent's capability queues one — same doc write.
                SyncedStateSync.enqueueTerminalCommand doc (queue "a1") id (Authority.ofAuthor (PeerRef ada)) 1.0 "echo ok" false
                scheduler.Drain ()
                do! Async.Sleep 20

                let! events = eventsOf log
                let startedEvent =
                    events |> List.pick (function SessionEvent.TerminalBlockStarted e -> Some e | _ -> None)
                Expect.equal startedEvent.Command "echo ok" "the queued command ran"
                let synced = syncedOf doc
                Expect.isTrue (Map.isEmpty synced.Pending) "and its entry left the doc once consumed"
            }

        testCaseAsync "the agent's command runs the moment it drains — nothing parks" <|
            async {
                // Under the bypass classifier the agent's entry is exactly a person's: the
                // doc write is the whole latency (Plan 23).
                let log = newLog ()
                let environment, spawned = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let terminals, _, _ = makeTerminals log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                let id = opened |> expect
                let doc = Y.Doc.Create ()
                let scheduler = TerminalScheduler.create doc terminals ignore Set.empty
                SyncedStateSync.enqueueTerminalCommand doc (queue "a1") id (Authority.agentFor (PeerRef ada)) 1.0 "echo hi" false
                scheduler.Drain ()
                do! Async.Sleep 20
                Expect.equal (List.length (List.ofSeq spawned)) 1 "it ran, with nobody asked"
                Expect.isTrue (Map.isEmpty (syncedOf doc).Pending) "and nothing is left parked"
            }

        testCaseAsync "a rejecting classifier records the refusal and the queue advances" <|
            async {
                let log = newLog ()
                let environment, spawned = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                // Reject exactly one command, by its text — the entry behind it must run.
                let refuseFirst : Classifier =
                    fun _ act ->
                        async {
                            match act with
                            | TerminalAct (_, "rm -rf /") -> return Rejected "not in this session"
                            | _ -> return Approved
                        }
                let terminals, _, _ =
                    makeTerminalsGated AttachTerminal.unavailable refuseFirst log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                let id = opened |> expect
                let doc = Y.Doc.Create ()
                let scheduler = TerminalScheduler.create doc terminals ignore Set.empty
                SyncedStateSync.enqueueTerminalCommand doc (queue "a1") id (Authority.agentFor (PeerRef ada)) 1.0 "rm -rf /" false
                SyncedStateSync.enqueueTerminalCommand doc (queue "a2") id (Authority.ofAuthor (PeerRef ada)) 2.0 "echo ok" false
                scheduler.Drain ()
                do! Async.Sleep 50
                let! events = eventsOf log
                let refusal =
                    events |> List.pick (function SessionEvent.TerminalCommandRejected e -> Some e | _ -> None)
                Expect.equal refusal.RejectedBy ActorRef.System "attributed to the session, not a person"
                Expect.equal refusal.Command "rm -rf /" "with the command snapshotted"
                Expect.equal refusal.Reason (Some "not in this session") "and the classifier's reason"
                Expect.equal (List.length (List.ofSeq spawned)) 1 "the refused command never spawned"
                let started =
                    events |> List.pick (function SessionEvent.TerminalBlockStarted e -> Some e | _ -> None)
                Expect.equal started.Command "echo ok" "and the entry behind it ran"
                Expect.isTrue (Map.isEmpty (syncedOf doc).Pending) "nothing is left parked"
            }

        testCaseAsync "the classifier reads the text as it stands at the drain, and who wrote it" <|
            async {
                // The queue stays editable until the drain takes it, so the classifier must
                // be shown what will RUN — never what was proposed.
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let asked = ResizeArray<ActorRef * ProposedAct> ()
                let recording : Classifier =
                    fun author act ->
                        async {
                            asked.Add (author, act)
                            return Approved
                        }
                let terminals, _, _ =
                    makeTerminalsGated AttachTerminal.unavailable recording log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                let id = opened |> expect
                let doc = Y.Doc.Create ()
                let scheduler = TerminalScheduler.create doc terminals ignore Set.empty
                SyncedStateSync.enqueueTerminalCommand doc (queue "a1") id (Authority.agentFor (PeerRef ada)) 1.0 "echo draft" false
                // Edited after the enqueue, before the drain — a peer fixing the command.
                (doc.getText (BodyKey.terminalQueued (queue "a1"))).delete (0, 10)
                (doc.getText (BodyKey.terminalQueued (queue "a1"))).insert (0, "echo final")
                scheduler.Drain ()
                do! Async.Sleep 20
                match List.ofSeq asked with
                | [ author, TerminalAct (terminal, command) ] ->
                    Expect.equal command "echo final" "the text as it stood when the drain took it"
                    Expect.equal terminal id "about this terminal"
                    Expect.equal author ActorRef.Agent "and the author, not the credential it borrows"
                | other -> failwithf "expected one TerminalAct question, got %A" other
            }

        testCaseAsync "an entry the log already consumed is repaired away, never run twice" <|
            async {
                let log = newLog ()
                let environment, spawned = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let terminals, _, _ = makeTerminals log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                let id = opened |> expect
                let doc = Y.Doc.Create ()
                // The crash window: a block start reached the log, the doc removal did not.
                let scheduler = TerminalScheduler.create doc terminals ignore (Set.singleton "q-a1")
                SyncedStateSync.enqueueTerminalCommand doc (queue "a1") id (Authority.ofAuthor (PeerRef ada)) 1.0 "make" false
                scheduler.Drain ()
                do! Async.Sleep 20
                Expect.isEmpty (List.ofSeq spawned) "it does not run a second time"
                let synced = syncedOf doc
                Expect.isTrue (Map.isEmpty synced.Pending) "and the leftover is cleaned out of the doc"
            }
    ]

// --- The synced state's codec ----------------------------------------------------------------

let private syncTests =
    testList "Terminal collaborative state" [
        testCase "a terminal queue entry survives a doc round-trip" <| fun () ->
            let doc = Y.Doc.Create ()
            SyncedStateSync.enqueueTerminalCommand doc (queue "a1") terminalA (Authority.agentFor (PeerRef ada)) 3.0 "git status" false
            let synced = syncedOf doc
            let entry = synced.Pending |> Map.find (queue "a1")
            Expect.equal entry.Terminal terminalA "the entry names its terminal"
            Expect.equal
                (Authority.author entry.Authority)
                ActorRef.Agent
                "the author, as an actor rather than a peer"
            Expect.equal entry.Order 3.0 "the order"
            Expect.equal (SyncedStateSync.terminalQueuedText doc (queue "a1")) "git status" "with its command text"

        testCase "a structured command parked by an old build is dropped, never run" <| fun () ->
            // Docs written before Plan 23 can hold `command:*` acts — structured commands a
            // person was still deciding on when the manual gate existed. Running one now
            // would carry out an act nobody released; dropping it at decode is the safe
            // direction, and the terminal entry beside it is untouched.
            let doc = Y.Doc.Create ()
            SyncedStateSync.enqueueTerminalCommand doc (queue "a1") terminalA (Authority.agentFor (PeerRef ada)) 1.0 "ls" false
            legacyPendingInDoc doc yjsModule "q-cmd" "command:add_repo" "agent"
            let synced = syncedOf doc
            Expect.isTrue (Map.containsKey (queue "a1") synced.Pending) "the terminal entry survives"
            Expect.equal (Map.count synced.Pending) 1 "and the parked command act does not"

        testCase "a verdict register written by an old peer is ignored, never fatal" <| fun () ->
            // Docs written before Plan 23 carry `approvedBy`/`rejectedBy` registers, and an
            // old browser tab may still write them. Thoth-style structural reads ignore
            // fields nobody asks for — pinned, because replay depends on it.
            let doc = Y.Doc.Create ()
            SyncedStateSync.enqueueTerminalCommand doc (queue "a1") terminalA (Authority.agentFor (PeerRef ada)) 1.0 "x" false
            setQueuedFieldInDoc doc (queue "a1") "approvedBy" "bob"
            setQueuedFieldInDoc doc (queue "a1") "rejectedBy" "bob"
            let synced = syncedOf doc
            Expect.isTrue (Map.containsKey (queue "a1") synced.Pending) "the entry still decodes"

        testCase "the composer slot key round-trips both ids" <| fun () ->
            let key = SyncedStateSync.TerminalDraftKey.make terminalA ada
            Expect.equal (SyncedStateSync.TerminalDraftKey.parse key) (Some (terminalA, ada)) "both come back"
            Expect.equal (SyncedStateSync.TerminalDraftKey.parse "no-separator") None "and a malformed key is skipped"
    ]


// --- Foreign sources (Plan 16, part D) ----------------------------------------------------

/// An environment that REFUSES to start, so "did the open ensure the sandbox?" is answerable
/// by whether the open succeeded rather than by counting calls.
let private refusingEnvironment () =
    let environment, spawned = scriptedEnvironment (fun _ -> [], 0)
    { environment with Ensure = fun _ _ -> async { return EnvironmentUnavailable "no sandbox here" } }, spawned

/// A stream that records what was written to it and never ends until a test says so.
///
/// The third member is that latch. `Exited` used to resolve immediately, which was harmless
/// while nobody awaited it and became a trap the moment something did: every attached
/// terminal would close the instant it opened, and nine tests about something else would
/// fail for a reason none of them names. A stream ending is now a thing a test DOES, which
/// is also the only way to pin what happens when one does.
let private loopback () =
    let written = ResizeArray<string> ()
    // What the device says, on demand. A fixture that could only speak once at attach could
    // not produce a transcript long enough to READ ACROSS, which is exactly where a cursor
    // is wrong or right.
    let mutable say : string -> unit = ignore
    let mutable ended : SandboxRun option = None
    let mutable resume : (SandboxRun -> unit) option = None
    // Both orders work: a test that ends the stream before anything awaits it, and one that
    // ends it after. A latch that only handled the second would turn a scheduling detail
    // into a hang.
    // A stream ends ONCE — `PtyHandle.Exited` says so, and a double that resolved twice sent
    // the real thing into a loop: closing a terminal kills its handle, the kill re-fired the
    // continuation, and the close ran again, for ever.
    let finish (run: SandboxRun) =
        match ended with
        | Some _ -> ()
        | None ->
            ended <- Some run
            match resume with
            | Some ok ->
                resume <- None
                ok run
            | None -> ()
    let exited =
        Async.FromContinuations (fun (ok, _, _) ->
            match ended with
            | Some run -> ok run
            | None -> resume <- Some ok)
    let attach : AttachTerminal =
        fun _ _ _ onData ->
            async {
                say <- onData
                return
                    Ok
                        { Write = fun text -> written.Add text
                          Resize = fun _ _ -> ()
                          Kill = fun () -> finish (SandboxExited 0)
                          Exited = exited
                          }
                    |> Result.map (fun handle ->
                        onData "ready\n"
                        handle)
            }
    attach, written, finish, (fun (text: string) -> say text)

/// A stream that will not dial — the provider is down, the url is wrong, nothing is
/// listening. `AttachWs` answers exactly this way, in the caller's own words.
let private refusingStream () : AttachTerminal =
    fun _ _ _ _ -> async { return Error "could not attach to ws://127.0.0.1:0/device" }

let private deviceTicket =
    { Url = "ws://127.0.0.1:0/device"
      Capabilities = SourceCapabilities.byteStream
      Label = "USB serial" }

/// A source that DID claim an exit code — a remote shell rather than a serial line — so
/// "is the code reported" and "is one invented" are two different questions with two
/// different tickets.
let private codedTicket =
    { deviceTicket with Capabilities = { SourceCapabilities.byteStream with HasExitCode = true } }

/// The reasons a terminal was closed for, in order.
let private closureReasons (log: EventLog<SessionEvent>) =
    async {
        let! events = eventsOf log
        return events |> List.choose (function SessionEvent.TerminalClosed e -> Some e.Reason | _ -> None)
    }

let private sourceTests =
    testList "Foreign terminal sources" [

        // The whole point of a second kind of source: a session that only talks to a serial
        // port should not start a container to do it.
        testCaseAsync "an attached source does NOT ensure the WorkSandbox" <|
            async {
                let log = newLog ()
                let environment, _ = refusingEnvironment ()
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let attach, _, _, _ = loopback ()
                let terminals, _, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! shell = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                Expect.isError shell "a shell terminal IS a need, so a refused sandbox refuses the open"
                let! device = terminals.Open (PeerRef ada) (Attached { Ticket = deviceTicket; Renewable = false }) "USB serial"
                Expect.isOk device "an attached one needs nothing this session runs"
            }

        // A peer already connected when a terminal opens was not there to be told at accept,
        // and `Screens.Sync` folds records only into an emulator a snapshot created — so
        // without this every record of a terminal opened mid-session is dropped on arrival.
        testCaseAsync "opening a terminal says so, so a peer already here can start a screen" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let attach, _, _, _ = loopback ()
                let terminals, _, opens = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (Attached { Ticket = deviceTicket; Renewable = false }) "USB serial"
                let id = opened |> expect
                Expect.equal (List.ofSeq opens) [ id ] "exactly the terminal that opened"
            }

        // The other half of the same rule, and it fails separately: announcing a terminal
        // that does not exist would seed a screen for one, and every record that never comes
        // would be folded into it.
        testCaseAsync "a stream that will not open says nothing to anybody" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let terminals, _, opens = makeTerminalsWith (refusingStream ()) log environment openTranscript readTranscript []
                let! _ = terminals.Open (PeerRef ada) (Attached { Ticket = deviceTicket; Renewable = false }) "USB serial"
                Expect.isEmpty (List.ofSeq opens) "nothing opened, so nothing is announced"
            }

        // The splice: a client folds records from the snapshot's seq forward, so the two have
        // to be counted in the same currency. Report a seq from a different one and a joining
        // peer either redraws for ever or skips a record permanently.
        testCaseAsync "a snapshot's seq is the same number the length hint carries" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let attach, _, _, _ = loopback ()
                let terminals, _, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (Attached { Ticket = deviceTicket; Renewable = false }) "USB serial"
                let id = opened |> expect
                let! snapshot = terminals.Snapshot id
                let length = terminals.Lengths () |> List.tryFind (fst >> (=) id) |> Option.map snd
                match snapshot, length with
                | Some keyframe, Some hinted ->
                    Expect.equal keyframe.Seq hinted "one currency, or the client splices at the wrong line"
                | other -> failwithf "expected a snapshot and a length, got %A" other
            }

        // An attached source has no degraded mode to fall back to, unlike a shell that would
        // not start — so a dial that fails is a failed OPEN. It used to be swallowed, and the
        // caller was handed an id for a terminal with nothing behind it.
        testCaseAsync "a stream that will not open is not a terminal" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let terminals, _, _ = makeTerminalsWith (refusingStream ()) log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (Attached { Ticket = deviceTicket; Renewable = false }) "USB serial"
                Expect.isError opened "the open carries the dial's own reason"
            }

        // The other half, and it fails separately: an open that reported an error while still
        // appending the event would leave an open-looking terminal in every projection,
        // reattachable, listed, and answering reads about a stream that never existed.
        testCaseAsync "a stream that will not open records no terminal either" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let terminals, _, _ = makeTerminalsWith (refusingStream ()) log environment openTranscript readTranscript []
                let! _ = terminals.Open (PeerRef ada) (Attached { Ticket = deviceTicket; Renewable = false }) "USB serial"
                let! events = eventsOf log
                let opens =
                    events |> List.choose (function SessionEvent.TerminalOpened e -> Some e | _ -> None)
                Expect.isEmpty opens "nothing durable says a terminal was opened"
            }

        // `RunBlock` awaits `Exited` for the shells it spawns, which a live-only source never
        // reaches — it has no blocks. So a device that stopped used to leave its terminal open
        // for ever, and the only way back was pressing Kill on something already dead.
        testCaseAsync "a stream that ends closes its terminal" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let attach, _, endStream, _ = loopback ()
                let terminals, _, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (Attached { Ticket = deviceTicket; Renewable = false }) "USB serial"
                let id = opened |> expect
                endStream (SandboxExited 0)
                do! Async.Sleep 20
                Expect.isFalse (terminals.IsOpen id) "the terminal goes with the stream that fed it"
            }

        // A serial line has no exit code, and `HasExitCode = false` is the source saying so.
        // Reporting "exit 0" for one that merely went quiet invents the exact fact that flag
        // exists to deny — and it is the fact somebody deciding whether to reattach reads.
        testCaseAsync "a source that claimed no exit code is not given one" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let attach, _, endStream, _ = loopback ()
                let terminals, _, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (Attached { Ticket = deviceTicket; Renewable = false }) "USB serial"
                let _ = opened |> expect
                endStream (SandboxExited 0)
                do! Async.Sleep 20
                let! reasons = closureReasons log
                match reasons with
                | [ reason ] ->
                    Expect.stringContains reason "ended" "it says the stream ended"
                    Expect.isFalse (reason.Contains "code") "and claims no code it was never given"
                | other -> failwithf "expected one closure, got %A" other
            }

        testCaseAsync "a source that claimed an exit code reports it" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let attach, _, endStream, _ = loopback ()
                let terminals, _, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (Attached { Ticket = codedTicket; Renewable = false }) "remote shell"
                let _ = opened |> expect
                endStream (SandboxExited 7)
                do! Async.Sleep 20
                let! reasons = closureReasons log
                Expect.equal reasons [ "the stream ended with code 7" ] "the code it declared it would have"
            }

        // `{"type":"failed"}` and an abrupt close both arrive here as `SandboxRunFailed`, and
        // the provider's own words are the only thing worth putting in front of a person.
        // They used to reach no surface at all.
        testCaseAsync "a stream that failed says why, in the provider's words" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let attach, _, endStream, _ = loopback ()
                let terminals, _, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (Attached { Ticket = deviceTicket; Renewable = false }) "USB serial"
                let _ = opened |> expect
                endStream (SandboxRunFailed "the exporter went away")
                do! Async.Sleep 20
                let! reasons = closureReasons log
                Expect.equal reasons [ "the exporter went away" ] "carried through unchanged"
            }

        // A `Kill` we sent resolves `Exited` too, so the watcher fires on a terminal already
        // closed. Two closures for one ending would be two answers to "when did this end".
        testCaseAsync "a terminal closed by hand is not closed twice by its own stream" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let attach, _, _, _ = loopback ()
                let terminals, _, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (Attached { Ticket = deviceTicket; Renewable = false }) "USB serial"
                let id = opened |> expect
                let! closed = terminals.Close id "closed by a peer"
                Expect.isOk closed "the hand close succeeds"
                do! Async.Sleep 20
                let! reasons = closureReasons log
                Expect.equal reasons [ "closed by a peer" ] "one closure, and it is the one a person asked for"
            }

        // The invariant that did not exist: an agent could only ever see the last 500 lines,
        // so a device that had been talking since before it arrived was unreadable from the
        // beginning however much of it was on disk.
        testCaseAsync "a read from the beginning returns the beginning" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let attach, _, _, _ = loopback ()
                let terminals, _, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (Attached { Ticket = deviceTicket; Renewable = false }) "USB serial"
                let id = opened |> expect
                let! head = terminals.Tail id (Some 0) None
                let page = head |> expect
                Expect.equal page.From 0 "it starts where it was asked to"
                Expect.stringContains page.Text "ready" "and carries what the device said first"
            }

        // Reading ACROSS a transcript, which is the only way a cursor can be caught being
        // wrong. Asserting `next.From = first.Through` checks the cursor against itself and
        // passes while off by one; the question that bites is whether the pages, laid end to
        // end, are the transcript — no line twice, none missing.
        //
        // This is the test that would have caught the off-by-one shipped in the paging step:
        // `readTranscript` indexes LINES and line 0 is the asciicast header, so a page that
        // counted the RECORDS it received reported a cursor one short, and the next read
        // handed back what the last one already had.
        testCaseAsync "pages laid end to end are the transcript, with nothing said twice" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let attach, _, _, say = loopback ()
                let terminals, _, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (Attached { Ticket = deviceTicket; Renewable = false }) "USB serial"
                let id = opened |> expect
                // Distinct lines, so a repeat is visible as a repeat rather than as a longer
                // run of the same thing.
                for line in [ "alpha\n"; "bravo\n"; "charlie\n"; "delta\n" ] do
                    say line
                let! whole = terminals.Tail id (Some 0) None
                let whole = (whole |> expect).Text
                // Walk it in pages, following the cursor exactly as an agent would.
                let rec walk (at: int) (seen: string) (guard: int) =
                    async {
                        if guard <= 0 then return failwith "the cursor never reached the end"
                        let! page = terminals.Tail id (Some at) None
                        let page = page |> expect
                        if page.Through >= page.Length then return seen + page.Text
                        else return! walk page.Through (seen + page.Text) (guard - 1)
                    }
                let! walked = walk 0 "" 20
                Expect.equal walked whole "the same bytes, in the same order, exactly once"
            }

        // The tail's promise, asked of a page: a window that reached the end says so. With
        // the cursor counted in the wrong currency this is off by exactly the header, so it
        // fails without needing a transcript long enough to page.
        testCaseAsync "a page that reached the end is up to date, exactly as a tail is" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let attach, _, _, say = loopback ()
                let terminals, _, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (Attached { Ticket = deviceTicket; Renewable = false }) "USB serial"
                let id = opened |> expect
                say "one\n"
                say "two\n"
                let! page = terminals.Tail id (Some 0) None
                let page = page |> expect
                Expect.equal page.Through page.Length "a page holding everything has reached the live edge"
            }

        // The other direction, and it fails separately: a cursor that ran AHEAD would skip
        // lines silently, which is the failure a duplicate at least makes visible.
        testCaseAsync "a read from the cursor misses nothing the previous one did not return" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let attach, _, _, say = loopback ()
                let terminals, _, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (Attached { Ticket = deviceTicket; Renewable = false }) "USB serial"
                let id = opened |> expect
                say "before\n"
                let! first = terminals.Tail id (Some 0) None
                let first = first |> expect
                say "after\n"
                let! second = terminals.Tail id (Some first.Through) None
                let second = second |> expect
                Expect.stringContains second.Text "after" "what arrived since is returned"
                Expect.isFalse (second.Text.Contains "before") "and what was already handed over is not"
            }

        // How a reader tells a whole answer from the end of a long one. The tail reaches the
        // live edge by construction, and saying so is what stops a model reading "the last
        // 2000 characters of a day" as "everything this device ever said".
        testCaseAsync "the tail says it has reached the live edge" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let attach, _, _, _ = loopback ()
                let terminals, _, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (Attached { Ticket = deviceTicket; Renewable = false }) "USB serial"
                let id = opened |> expect
                let! tail = terminals.Tail id None None
                let tail = tail |> expect
                Expect.equal tail.Through tail.Length "a tail is up to date, and says so"
            }

        // A wait whose text is already there is not a wait at all. `loopback` says "ready\n"
        // on attach, so this answers from the transcript without holding anything.
        testCaseAsync "a wait for something already said returns it at once" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let attach, _, _, _ = loopback ()
                let terminals, _, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (Attached { Ticket = deviceTicket; Renewable = false }) "USB serial"
                let id = opened |> expect
                let! answer = terminals.Tail id (Some 0) (Some { Until = MatchLiteral "ready"; TimeoutSeconds = 5.0 })
                let answer = answer |> expect
                Expect.equal answer.Matched (Some true) "it arrived"
                Expect.stringContains answer.Text "ready" "and the text carries it"
            }

        // The bug this whole verb exists to make unexpressible. An agent that power-cycled a
        // board and waited for its login prompt used to match the one from BEFORE the reboot,
        // instantly, and carry on as though the board were up. A wait looks forward from the
        // caller's own cursor, so what it has already been handed cannot satisfy it.
        testCaseAsync "a wait cannot be satisfied by output the caller already read" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let attach, _, _, _ = loopback ()
                let terminals, _, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (Attached { Ticket = deviceTicket; Renewable = false }) "USB serial"
                let id = opened |> expect
                let! first = terminals.Tail id (Some 0) None
                let first = first |> expect
                Expect.stringContains first.Text "ready" "the caller has been handed it"
                // Waiting from where that read stopped: "ready" is behind the cursor now.
                let! again = terminals.Tail id (Some first.Through) (Some { Until = MatchLiteral "ready"; TimeoutSeconds = 0.05 })
                let again = again |> expect
                Expect.equal again.Matched (Some false) "what it already saw does not count as having arrived"
            }

        // A timeout is an ANSWER: what was said while waiting is usually where the reason it
        // never came is written.
        testCaseAsync "a wait that times out says what was said instead" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let attach, _, _, _ = loopback ()
                let terminals, _, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (Attached { Ticket = deviceTicket; Renewable = false }) "USB serial"
                let id = opened |> expect
                let! answer = terminals.Tail id (Some 0) (Some { Until = MatchLiteral "never-appears"; TimeoutSeconds = 0.05 })
                let answer = answer |> expect
                Expect.equal answer.Matched (Some false) "it did not arrive"
                Expect.stringContains answer.Text "ready" "and what DID arrive is the answer"
            }

        // The cursor rule has to hold for a PATTERN too, and it gets its own case rather than
        // an assumption of symmetry: a pattern takes a different path through the matcher, and
        // "the stale match is unexpressible" would be a much weaker promise if it turned out to
        // be true only of literals.
        testCaseAsync "a pattern cannot be satisfied by output the caller already read" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let attach, _, _, say = loopback ()
                let terminals, _, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (Attached { Ticket = deviceTicket; Renewable = false }) "USB serial"
                let id = opened |> expect
                say "root@box:~# "
                let pattern = TerminalPattern.compile "[#$>] $" |> expect
                let! first = terminals.Tail id (Some 0) None
                let first = first |> expect
                Expect.stringContains first.Text "#" "the prompt has been handed over"
                let! again =
                    terminals.Tail
                        id
                        (Some first.Through)
                        (Some { Until = MatchPattern (pattern, "[#$>] $"); TimeoutSeconds = 0.05 })
                Expect.equal (again |> expect).Matched (Some false) "a prompt already read is not a prompt that just arrived"
            }

        testCaseAsync "a pattern matches output that arrives after the cursor" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let attach, _, _, say = loopback ()
                let terminals, _, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (Attached { Ticket = deviceTicket; Renewable = false }) "USB serial"
                let id = opened |> expect
                let! first = terminals.Tail id (Some 0) None
                let first = first |> expect
                say "U-Boot 2024.01\n"
                let pattern = TerminalPattern.compile "U-Boot \\d+\\.\\d+" |> expect
                let! found =
                    terminals.Tail
                        id
                        (Some first.Through)
                        (Some { Until = MatchPattern (pattern, "U-Boot"); TimeoutSeconds = 2.0 })
                Expect.equal (found |> expect).Matched (Some true) "what arrived since is what a wait is for"
            }

        // A read that was not waiting says so, rather than reporting a wait nobody asked for.
        testCaseAsync "a read that waited for nothing claims neither outcome" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let attach, _, _, _ = loopback ()
                let terminals, _, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (Attached { Ticket = deviceTicket; Renewable = false }) "USB serial"
                let id = opened |> expect
                let! answer = terminals.Tail id None None
                Expect.equal (answer |> expect).Matched None "no wait, no verdict"
            }

        testCaseAsync "an attached source's bytes reach the transcript" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, linesOf, _, _, readTranscript = recordingTranscripts ()
                let attach, _, _, _ = loopback ()
                let terminals, _, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (Attached { Ticket = deviceTicket; Renewable = false }) "USB serial"
                let id = opened |> expect
                let printed =
                    linesOf id
                    |> List.choose (function TranscriptRecordLine record -> Some record.Data | _ -> None)
                    |> String.concat ""
                Expect.stringContains printed "ready" "the same output path a shell gets"
            }

        // Declared, not discovered at the third command: a source that cannot carry the
        // OSC 133 bootstrap has nothing that could ever report a command's outcome, so the
        // block is refused as a value instead of hanging forever.
        testCaseAsync "a live-only source refuses blocks rather than leaving them open" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let attach, written, _, _ = loopback ()
                let terminals, _, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (Attached { Ticket = deviceTicket; Renewable = false }) "USB serial"
                let id = opened |> expect
                do! terminals.RunBlock id (entry "a1" id (PeerRef ada) 1.0) "make" ignore
                let! events = eventsOf log
                let completed =
                    events
                    |> List.choose (function SessionEvent.TerminalBlockCompleted e -> Some e.Result | _ -> None)
                match completed with
                | [ CommandExecutionFailed reason ] ->
                    Expect.stringContains reason "live-only" "it says WHY, so the agent does not retry"
                | other -> failwithf "expected one refused block, got %A" other
                // Refused BEFORE the write: typing a shell command line at a serial device is
                // not a no-op.
                Expect.isFalse (written |> Seq.exists (fun w -> w.Contains "make")) "nothing was typed at the device"
            }

        // A serial line has no rows. Telling it it has 24 would be inventing a fact the
        // emulator would then draw against.
        testCaseAsync "a source that declared no size is not resized" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let attach, _, _, _ = loopback ()
                let terminals, _, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (Attached { Ticket = deviceTicket; Renewable = false }) "USB serial"
                let id = opened |> expect
                let! taken = terminals.Take id (PeerRef ada)
                Expect.isOk taken "a device can still be typed at — the lease is what arbitrates"
                Expect.isFalse (terminals.Resize id (PeerRef ada) 132 43) "but it has no size to set"
            }

        // The agent's hand in a source with no blocks (Plan 19). What matters is not that
        // bytes moved — it is that they moved THROUGH the lease, so a human watching sees
        // who is typing and can take it straight back.
        testCaseAsync "the agent types into a live-only terminal by holding it, like anyone else" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let attach, written, _, _ = loopback ()
                let terminals, _, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (Attached { Ticket = deviceTicket; Renewable = false }) "USB serial"
                let id = opened |> expect

                let! wrote = terminals.Write id ActorRef.Agent "AT\r"
                Expect.isOk wrote "the agent can talk to a device it was given"
                Expect.isTrue (written |> Seq.exists (fun w -> w.Contains "AT")) "the bytes reached the stream"
                Expect.equal (terminals.Leased ()) (Set.ofList [ TerminalId.value id ]) "and it holds the terminal"

                // Stealable, both ways: the lease means the same thing whoever is holding it.
                let! stolen = terminals.Take id (PeerRef ada)
                Expect.isOk stolen "a person takes it back without asking"
                Expect.isFalse (terminals.Input id ActorRef.Agent "more") "and the agent stops being able to type"
            }

        // The agent's eyes on a source with no blocks (Plan 19): it answers from the same
        // transcript the panel renders.
        testCaseAsync "a live-only terminal reads back what it said" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let attach, _, _, _ = loopback ()
                let terminals, _, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! device = terminals.Open (PeerRef ada) (Attached { Ticket = deviceTicket; Renewable = false }) "USB serial"

                match! terminals.Tail (expect device) None None with
                | Error e -> failwithf "a device has nothing but its transcript to read: %s" e
                | Ok tail ->
                    // `loopback` greets with "ready\n" on attach, so there is something to read
                    // without typing at it first.
                    Expect.stringContains tail.Text "ready" "what the stream said comes back"
                    Expect.equal tail.Elided 0 "and nothing was left out of a short one"
            }

        // The recording outlives the terminal — and so does the reason it is readable at all,
        // which is that its source was never instrumented.
        testCaseAsync "a closed terminal still reads back, because its recording outlived it" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let attach, _, _, _ = loopback ()
                let terminals, _, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! device = terminals.Open (PeerRef ada) (Attached { Ticket = deviceTicket; Renewable = false }) "USB serial"
                let id = expect device
                let! closed = terminals.Close id "the device went away"
                Expect.isOk closed "the terminal closes"

                match! terminals.Tail id None None with
                | Error e -> failwithf "a closed device still has a recording: %s" e
                | Ok tail -> Expect.stringContains tail.Text "ready" "and it still reads"
            }

        testCaseAsync "reading a terminal that runs blocks is refused, and says where the answer is" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let attach, _, _, _ = loopback ()
                let terminals, _, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! shell = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"

                match! terminals.Tail (expect shell) None None with
                | Ok _ -> failwith "a shell's output is its blocks', and reading it twice is two answers to one question"
                | Error reason -> Expect.stringContains reason "execute_command" "and it says where the answer is"
            }

        testCaseAsync "on an instrumented terminal nobody holds it is refused, because that is what blocks are for" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let attach, written, _, _ = loopback ()
                let terminals, _, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                let id = opened |> expect
                match! terminals.Write id ActorRef.Agent "rm -rf /\r" with
                | Ok () -> failwith "raw bytes into a shell would be the door around the approval gate"
                | Error reason -> Expect.stringContains reason "execute_command" "and it says where to go instead"
                Expect.isFalse (written |> Seq.exists (fun w -> w.Contains "rm -rf")) "nothing was typed"
            }
    ]

// --- What a terminal's state affords (Plan 20, stage 0) -------------------------------------

/// A terminal in whatever state a case is about. Written out rather than folded from events
/// because these tests are about the RULE, and building each state through the projection
/// would make them tests of the projection with the rule as an afterthought.
let private viewOf (isOpen: bool) (renewable: bool) : TerminalView =
    { TerminalId = terminalA
      Title = "build"
      OpenedBy = PeerRef ada
      Sandbox = Some SandboxName.defaultName
      Renewable = renewable
      IsOpen = isOpen
      ClosedReason = (if isOpen then None else Some "closed by nick")
      Lease = None
      IntegrationLost = false
      Blocks = []
      DroppedBytes = 0 }

let private affordanceTests =
    testList "What a terminal affords (Plan 20, stage 0)" [

        // Each of these is a BICONDITIONAL, and both halves are the invariant: a verb offered
        // where it works and absent everywhere else. Asserting only the presence would leave a
        // fold that returns `true` always looking correct, which is the failure mode a
        // loosened test reads as coverage for.

        testCase "the kill is offered exactly while the terminal is open" <| fun () ->
            let afforded (view: TerminalView) = (TerminalAffordances.ofView true view).CanKill
            Expect.isTrue (afforded (viewOf true false)) "a running terminal can be killed"
            Expect.isFalse (afforded (viewOf false false)) "a closed one has nothing left to kill"

        testCase "the rewind is offered exactly while a live terminal has something recorded" <| fun () ->
            let afforded recorded view = (TerminalAffordances.ofView recorded view).CanRewind
            Expect.isTrue (afforded true (viewOf true false)) "live, and there is something behind it"
            Expect.isFalse (afforded false (viewOf true false)) "a DVR with nothing recorded has nothing to do"
            Expect.isFalse (afforded true (viewOf false false)) "and a closed terminal is replayed, not rewound"

        testCase "the replay is offered exactly where a closed terminal's recording survives" <| fun () ->
            let afforded recorded view = (TerminalAffordances.ofView recorded view).CanReplay
            Expect.isTrue (afforded true (viewOf false false)) "closed, with its recording"
            // The stated gap: the per-terminal cap ate it. Offering a player over nothing
            // would be indistinguishable from a terminal that printed nothing.
            Expect.isFalse (afforded false (viewOf false false)) "closed, with nothing kept"
            Expect.isFalse (afforded true (viewOf true false)) "and a live terminal is not a recording yet"

        testCase "attaching again is offered exactly on a closed stream whose provider allows it" <| fun () ->
            let afforded (view: TerminalView) = (TerminalAffordances.ofView true view).CanReattach
            Expect.isTrue (afforded (viewOf false true)) "closed, and asking again is safe"
            Expect.isFalse (afforded (viewOf false false)) "a shell terminal has no provider to ask"
            Expect.isFalse (afforded (viewOf true true)) "and a stream still running needs no second one"

        // Not gated on the recording, and that is the point of asking the PROVIDER rather
        // than the store: a device is still on the other end of a stream whose recording the
        // cap ate, and refusing the way back because the RECORD is gone answers a question
        // nobody asked.
        testCase "attaching again survives a recording the cap ate" <| fun () ->
            Expect.isTrue
                ((TerminalAffordances.ofView false (viewOf false true)).CanReattach)
                "the way back is about the stream, not about what was kept of it"

        testCase "the recording is the only read exactly where a closed terminal ran nothing" <| fun () ->
            // The rule that keeps a player from being redundant. A terminal with blocks has a
            // cheaper read of the same history — the commands and what they printed — so its
            // recording is somewhere you go. A terminal with none has no such read: a device
            // whose source could never be instrumented, or a shell that only ever held a
            // lease, is entirely in its recording, and an empty block list is not a read.
            let afforded recorded view = (TerminalAffordances.ofView recorded view).ReplayIsTheRead
            let ran =
                { viewOf false false with
                    Blocks =
                        [ { BlockId = block "1"
                            QueueId = None
                            Authority = Authority.ofAuthor (PeerRef ada)
                            Command = "make"
                            Background = false
                            FromSeq = 1
                            ToSeq = Some 3
                            Status = BlockFinished (CommandSucceeded 0) } ] }
            Expect.isTrue (afforded true (viewOf false false)) "closed, recorded, and nothing ran in it"
            Expect.isFalse (afforded true ran) "the commands it ran are the read instead"
            Expect.isFalse (afforded false (viewOf false false)) "and a recording the cap ate is no read at all"
            Expect.isFalse (afforded true (viewOf true false)) "a live terminal is not a recording yet"

        testCase "the screen is the only read exactly where a live terminal has no blocks" <| fun () ->
            // The live twin, and the rule a device needs. Gated on the LEASE, the screen
            // appeared only while somebody was typing — so a serial port nobody had taken
            // rendered an empty block list beside a stream arriving the whole time, and the
            // only way to see anything was to claim the keyboard.
            let afforded view = (TerminalAffordances.ofView true view).ScreenIsTheRead
            let device = { viewOf true false with Sandbox = None }
            Expect.isTrue (afforded device) "an open stream with no blocks is its screen"
            Expect.isFalse (afforded (viewOf true false)) "a shell's read is the blocks it is about to have"
            Expect.isFalse (afforded { device with IsOpen = false }) "a closed one is a recording, not a screen"

        testCase "a stream that resolves into blocks reads as its blocks, not its screen" <| fun () ->
            // Why the rule asks the BLOCKS as well as the sandbox. A source that declared
            // `instrument` has no sandbox either, so the sandbox alone would take the block
            // read away from exactly the source that has one.
            let afforded view = (TerminalAffordances.ofView true view).ScreenIsTheRead
            let instrumented =
                { viewOf true false with
                    Sandbox = None
                    Blocks =
                        [ { BlockId = block "1"
                            QueueId = None
                            Authority = Authority.ofAuthor (PeerRef ada)
                            Command = "make"
                            Background = false
                            FromSeq = 1
                            ToSeq = Some 3
                            Status = BlockFinished (CommandSucceeded 0) } ] }
            Expect.isFalse (afforded instrumented) "it has a cheaper read of the same history"
    ]

// The agent's own terminal (Plan 15, stage 2), as a rule the manager owns rather than one the
// composition root remembered. It used to be a `Map` in `Host.fs`; nothing below could be
// asserted without building a whole session.
let private agentTerminalTests =
    testList "The agent's terminal" [

        testCaseAsync "asking twice in one sandbox gets the same terminal" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let terminals, _, _ = makeTerminals log environment openTranscript readTranscript []
                let! first = terminals.AgentTerminal SandboxName.defaultName "npm test"
                let! again = terminals.AgentTerminal SandboxName.defaultName "npm run build"
                Expect.equal again first "the second command lands in the shell the first one used"
            }

        testCaseAsync "each sandbox gets its own" <|
            async {
                // The reason it is keyed at all: `execute_command` is the only door into a
                // sandbox, and one shared cell would run a command meant for `test` in
                // whichever sandbox happened to be first.
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let terminals, _, _ = makeTerminals log environment openTranscript readTranscript []
                let other = SandboxName.create "test" |> expect
                let! default' = terminals.AgentTerminal SandboxName.defaultName "npm test"
                let! test = terminals.AgentTerminal other "npm test"
                Expect.notEqual test default' "a command for `test` cannot land in `default`"
            }

        testCaseAsync "a closed one is replaced rather than handed back" <|
            async {
                // A terminal is a process; a closed one has none. Handing back the dead id
                // would make the next command fail in a way that reads as the command's fault.
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let terminals, _, _ = makeTerminals log environment openTranscript readTranscript []
                let! first = terminals.AgentTerminal SandboxName.defaultName "npm test"
                let id = first |> expect
                let! _ = terminals.Close id "closed by a peer"
                let! next = terminals.AgentTerminal SandboxName.defaultName "npm test"
                Expect.notEqual next first "a fresh terminal, because the old one has no process"
            }

    ]

// --- The shell profile (Plan 25) -----------------------------------------------------------
//
// One durable fact about a sandbox's terminals: where a shell opened in it starts. What is
// worth pinning here is the SPAWN — the profile is applied by it and never as a `cd` typed at
// a prompt, so the assertions are about the exec the sandbox was handed.

/// A latch: something that has happened, and an async that resolves when it has. Both orders
/// work, which is what makes it a latch rather than a race — no clock, no sleep, no ordering
/// luck.
let private latch () : (unit -> unit) * Async<unit> =
    let mutable fired = false
    let mutable resume : unit -> unit = ignore
    (fun () ->
        if not fired then
            fired <- true
            resume ()),
    Async.FromContinuations (fun (cont, _, _) -> if fired then cont () else resume <- cont)

/// The argv `SetProfile` validates a directory with, as this fixture reads it back. The verb
/// asks the sandbox to `cd` there and say where it landed, so this is also where a RELATIVE
/// path acquires its meaning.
let private validatedPath (exec: SandboxExec) : string option =
    match exec.Arguments with
    | [ "-c"; body; "sh"; path ] when body.StartsWith "cd " -> Some path
    | _ -> None

/// This fixture's sandbox opens in `/ws`, so that is what a relative path resolves against —
/// standing in for the workspace a real terminal starts in.
let private fixtureWorkspace = "/ws"

let private resolvedIn (path: string) : string =
    if path.StartsWith "/" then path else sprintf "%s/%s" fixtureWorkspace path

/// A sandbox that CAN host an instrumented shell, and that knows which directories it has.
/// `present` is read on every call rather than captured, so a test can make a directory go
/// away between the profile being set and a terminal being opened in it — which is the one
/// thing the fallback exists for.
let private profileEnvironment (present: unit -> Set<string>) =
    let ptySpawned = ResizeArray<SandboxExec> ()
    let exists path = Set.contains path (present ())
    let environment : SessionEnvironment.SessionEnvironment =
        { Ensure = fun _ _ -> async { return EnvironmentAvailable }
          Spawn =
            fun exec onChunk ->
                async {
                    let code =
                        match validatedPath exec with
                        | Some path ->
                            let resolved = resolvedIn path
                            if exists resolved then
                                // What `pwd` prints once `cd` has landed: the absolute answer,
                                // which is the only thing the verb stores.
                                onChunk (Stdout, resolved + "\n")
                                0
                            else 1
                        | None -> 0
                    return
                        Ok
                            { WriteStdin = ignore
                              CloseStdin = ignore
                              Kill = ignore
                              Exited = async { return SandboxExited code } }
                }
          SpawnPty =
            fun exec _ _ onOutput ->
                async {
                    ptySpawned.Add exec
                    match exec.WorkingDirectory with
                    | Some path when not (exists path) -> return Error (sprintf "chdir %s: no such directory" path)
                    | _ ->
                        return
                            Ok
                                // The shell's own prompt hook, as this fixture's shell runs it:
                                // the rc bootstrap is typed in, and the next prompt carries the
                                // `A` mark that makes the terminal instrumented.
                                { Write = fun _ -> onOutput "\u001b]133;A;y=test-nonce\u0007"
                                  Resize = fun _ _ -> ()
                                  Kill = ignore
                                  Exited = async { return SandboxExited 0 } }
                }
          Stop = fun () -> async { return () }
          CurrentRef = fun () -> Some "scripted" }
    environment, ptySpawned

/// A sandbox whose BLOCKS do not finish until the test says so — the only way to hold a
/// terminal busy without a clock. Its `test -d` still answers, because the profile verb has to
/// be able to validate while a block runs.
let private blockingEnvironment () =
    let release, finished = latch ()
    let spawned = ResizeArray<SandboxExec> ()
    let environment : SessionEnvironment.SessionEnvironment =
        { Ensure = fun _ _ -> async { return EnvironmentAvailable }
          Spawn =
            fun exec onChunk ->
                async {
                    spawned.Add exec
                    let exited =
                        match validatedPath exec with
                        | Some path ->
                            onChunk (Stdout, resolvedIn path + "\n")
                            async { return SandboxExited 0 }
                        | None ->
                            async {
                                do! finished
                                return SandboxExited 0
                            }
                    return Ok { WriteStdin = ignore; CloseStdin = ignore; Kill = ignore; Exited = exited }
                }
          SpawnPty = fun _ _ _ _ -> async { return Error "no pty in this fixture" }
          Stop = fun () -> async { return () }
          CurrentRef = fun () -> Some "scripted" }
    environment, spawned, release

let private shellProfileTests =
    let checkout = "/ws/repos/octo/hello"
    /// A manager over a sandbox that has the checkout and can host a shell.
    let fixture () =
        let log = newLog ()
        let mutable present = Set.singleton checkout
        let environment, ptySpawned = profileEnvironment (fun () -> present)
        let openTranscript, linesOf, _, _, readTranscript = recordingTranscripts ()
        let terminals, _, _ = makeTerminals log environment openTranscript readTranscript []
        terminals, log, ptySpawned, linesOf, (fun () -> present <- Set.empty)
    let ptyDirectories (ptySpawned: ResizeArray<SandboxExec>) =
        ptySpawned |> Seq.map (fun e -> e.WorkingDirectory) |> List.ofSeq
    testList "The shell profile" [

        testCaseAsync "a terminal opened afterwards starts its shell there" <|
            async {
                // The invariant the whole plan exists for, and it is asserted on the SPAWN:
                // a `cd` typed at the prompt would echo into the audit trail on the re-arm
                // path, need quoting for a path this code did not choose, and fail invisibly.
                let terminals, _, ptySpawned, _, _ = fixture ()
                let! set = terminals.SetProfile ActorRef.Agent SandboxName.defaultName (Some checkout)
                Expect.isOk set "the directory is there, so the profile takes"
                let! _ = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                Expect.equal (ptyDirectories ptySpawned) [ Some checkout ] "the shell is spawned in the profile's directory"
            }

        testCaseAsync "a terminal opened BEFORE it keeps the directory it is in" <|
            async {
                // A shell's cwd is state its user is relying on. The one terminal that does
                // move is the one nobody named, and it moves by being reopened.
                let terminals, _, ptySpawned, _, _ = fixture ()
                let! _ = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                let! _ = terminals.SetProfile ActorRef.Agent SandboxName.defaultName (Some checkout)
                Expect.equal (ptyDirectories ptySpawned) [ None ] "nothing is re-spawned under a terminal already open"
            }

        testCaseAsync "the degraded per-block path runs its blocks there too" <|
            async {
                // A terminal with no pty gets a fresh process per block and carries nothing
                // between them, so the profile is applied per block — the same promise, kept
                // by the only means that path has. Wiring only the pty would make a degraded
                // terminal silently ignore the profile.
                let log = newLog ()
                // The profile probe asks the sandbox to `cd` there and say where it landed;
                // everything else this fixture runs says nothing.
                let environment, spawned =
                    scriptedEnvironment (fun arg -> (if arg = checkout then [ Stdout, checkout + "\n" ] else []), 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let terminals, _, _ = makeTerminals log environment openTranscript readTranscript []
                let! set = terminals.SetProfile ActorRef.Agent SandboxName.defaultName (Some checkout)
                Expect.isOk set "this fixture answers the probe with the directory it landed in"
                let! opened = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                let id = opened |> expect
                do! terminals.RunBlock id (entry "a1" id (PeerRef ada) 1.0) "pwd" ignore
                Expect.equal
                    (spawned
                     |> Seq.filter (fun e -> validatedPath e |> Option.isNone)
                     |> Seq.map (fun e -> e.WorkingDirectory)
                     |> List.ofSeq)
                    [ Some checkout ]
                    "the block's own process starts in the profile's directory"
            }

        testCaseAsync "a directory the sandbox does not have is refused" <|
            async {
                // Asked of the SANDBOX, not of this process: under docker the path is inside a
                // container we cannot see, and under srt the sandbox's read scope is not ours.
                let terminals, _, _, _, _ = fixture ()
                let! set = terminals.SetProfile ActorRef.Agent SandboxName.defaultName (Some "/repos/gone")
                match set with
                | Ok _ -> failwith "a directory that is not there must not become the profile"
                | Error reason -> Expect.isTrue (reason.Contains "/repos/gone") "the refusal names the path"
            }

        testCaseAsync "a refused directory leaves the profile as it was" <|
            async {
                let terminals, _, _, _, _ = fixture ()
                let! _ = terminals.SetProfile ActorRef.Agent SandboxName.defaultName (Some checkout)
                let! _ = terminals.SetProfile ActorRef.Agent SandboxName.defaultName (Some "/repos/gone")
                Expect.equal
                    (terminals.Profiles () |> ShellProfileProjection.workingDirectory SandboxName.defaultName)
                    (Some checkout)
                    "a refusal changes nothing"
            }

        // A path from `add_repo` is relative to where a terminal starts, and that is exactly
        // the root the sandbox resolves against — so what the repo tools answer with can be
        // passed straight here. What gets STORED is always the absolute answer, so nothing
        // downstream ever holds a path whose meaning depends on where somebody stood.
        testCaseAsync "a relative path is resolved by the sandbox, and stored absolute" <|
            async {
                let terminals, _, _, _, _ = fixture ()
                let! set = terminals.SetProfile ActorRef.Agent SandboxName.defaultName (Some "repos/octo/hello")
                Expect.isOk set "the path the repo tools answer with is a path this takes"
                // Asserted on what is STORED, not on where a shell then opened: a relative
                // string kept in the projection would still open the right shell in this
                // fixture, and would be a path whose meaning moves the moment anything else
                // reads it — Plan 26's tree matching, or a restart replaying the log.
                Expect.equal
                    (terminals.Profiles () |> ShellProfileProjection.workingDirectory SandboxName.defaultName)
                    (Some checkout)
                    "what the sandbox resolved, not what the caller typed"
            }

        testCaseAsync "a relative path that is nowhere in the sandbox is still refused" <|
            async {
                let terminals, _, _, _, _ = fixture ()
                let! set = terminals.SetProfile ActorRef.Agent SandboxName.defaultName (Some "repos/octo/absent")
                Expect.isError set "resolving is not the same as accepting"
            }

        testCaseAsync "a clear returns new terminals to wherever the sandbox puts them" <|
            async {
                let terminals, _, ptySpawned, _, _ = fixture ()
                let! _ = terminals.SetProfile ActorRef.Agent SandboxName.defaultName (Some checkout)
                let! _ = terminals.SetProfile ActorRef.Agent SandboxName.defaultName None
                let! _ = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                Expect.equal (ptyDirectories ptySpawned) [ None ] "back to what every terminal did before there were profiles"
            }

        testCaseAsync "a profile set in one sandbox does not move another's terminals" <|
            async {
                let terminals, _, ptySpawned, _, _ = fixture ()
                let other = SandboxName.create "test" |> expect
                let! _ = terminals.SetProfile ActorRef.Agent SandboxName.defaultName (Some checkout)
                let! _ = terminals.Open (PeerRef ada) (SandboxShell other) "build"
                Expect.equal (ptyDirectories ptySpawned) [ None ] "a path is only a path inside the filesystem that has it"
            }

        testCaseAsync "a session that restarts still opens terminals where it left off" <|
            async {
                // The restart promise: the profile is folded from the durable log, exactly as
                // the terminals left open are.
                let log = newLog ()
                let environment, ptySpawned = profileEnvironment (fun () -> Set.singleton checkout)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let replayed =
                    [ SessionEvent.ShellProfileSet
                        { MessageId = MessageId.create "m-old" |> expect
                          Sandbox = SandboxName.defaultName
                          WorkingDirectory = Some checkout
                          Actor = ActorRef.Agent } ]
                    |> List.fold ShellProfileProjection.applyEvent ShellProfileProjection.empty
                let terminals, _, _ =
                    makeTerminalsFrom
                        AttachTerminal.unavailable
                        Classifier.approveAll
                        log
                        environment
                        openTranscript
                        readTranscript
                        []
                        replayed
                let! _ = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                Expect.equal
                    (ptyDirectories ptySpawned)
                    [ Some checkout ]
                    "a restarted session opens its next terminal where the last one started"
            }

        testCaseAsync "the agent's idle command terminal is retired, so its next command lands there" <|
            async {
                // Left alone, the change would be invisible in exactly the flow that motivates
                // it: set the profile, run `pwd`, get the old directory, conclude the tool did
                // nothing. It is the manager's own, so nothing is lost but a shell's history.
                let terminals, _, _, _, _ = fixture ()
                let! first = terminals.AgentTerminal SandboxName.defaultName "git status"
                let before = first |> expect
                let! _ = terminals.SetProfile ActorRef.Agent SandboxName.defaultName (Some checkout)
                let! next = terminals.AgentTerminal SandboxName.defaultName "git status"
                Expect.notEqual (next |> expect) before "the next command runs in a shell opened under the new profile"
            }

        testCaseAsync "a terminal the agent NAMED is left alone" <|
            async {
                // It was asked for. Taking somebody's shell away because a default changed is
                // not a default's business.
                let terminals, _, _, _, _ = fixture ()
                let! opened = terminals.OpenAgentTerminal SandboxName.defaultName "tests"
                let id = opened |> expect
                let! _ = terminals.SetProfile ActorRef.Agent SandboxName.defaultName (Some checkout)
                Expect.isTrue (terminals.IsOpen id) "a named terminal keeps its shell"
            }

        testCaseAsync "a terminal a person opened is left alone" <|
            async {
                let terminals, _, _, _, _ = fixture ()
                let! opened = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "mine"
                let id = opened |> expect
                let! _ = terminals.SetProfile ActorRef.Agent SandboxName.defaultName (Some checkout)
                Expect.isTrue (terminals.IsOpen id) "a human's shell is not a default's to end"
            }

        testCaseAsync "a BUSY command terminal is left alone" <|
            async {
                // Killing a running command to change a default is the wrong trade in the one
                // direction that cannot be undone.
                let log = newLog ()
                let environment, _, release = blockingEnvironment ()
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let terminals, _, _ = makeTerminals log environment openTranscript readTranscript []
                let! opened = terminals.AgentTerminal SandboxName.defaultName "npm test"
                let id = opened |> expect
                let started, awaitStarted = latch ()
                Async.StartImmediate (terminals.RunBlock id (entry "a1" id ActorRef.Agent 1.0) "npm test" started)
                do! awaitStarted
                Expect.isTrue (terminals.Busy () |> Set.contains (TerminalId.value id)) "the block is running"
                let! set = terminals.SetProfile ActorRef.Agent SandboxName.defaultName (Some checkout)
                Expect.isOk set "the profile still changes"
                Expect.isTrue (terminals.IsOpen id) "but the running command is not killed for it"
                release ()
            }

        testCaseAsync "a shell that cannot start there opens anyway, and says why" <|
            async {
                // The directory can go away between being set and being opened in. A terminal
                // that refuses to open because of a DEFAULT is a worse failure than the default
                // being wrong, so it falls back once and records the reason where people read.
                let terminals, _, _, linesOf, vanish = fixture ()
                let! _ = terminals.SetProfile ActorRef.Agent SandboxName.defaultName (Some checkout)
                vanish ()
                let! opened = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                let id = opened |> expect
                Expect.isTrue (terminals.IsOpen id) "the terminal opens"
                let printed =
                    linesOf id
                    |> List.choose (function TranscriptRecordLine r -> Some r.Data | _ -> None)
                    |> String.concat ""
                Expect.isTrue (printed.Contains checkout) "and the transcript names the directory it could not use"
            }

        testCaseAsync "a tree that goes away takes the profiles pointing into it" <|
            async {
                // Plan 25's upstream half (Plan 26): a profile pointing inside a checkout that
                // has been deleted would send every future terminal somewhere that no longer
                // exists.
                let terminals, _, _, _, _ = fixture ()
                let! _ = terminals.SetProfile ActorRef.Agent SandboxName.defaultName (Some checkout)
                let! _ = terminals.ClearProfilesUnder ActorRef.Agent "/ws/repos/octo"
                Expect.equal
                    (terminals.Profiles () |> ShellProfileProjection.workingDirectory SandboxName.defaultName)
                    None
                    "the profile goes with the tree"
            }

        testCaseAsync "it answers with the sandboxes it cleared" <|
            async {
                // The caller says so in its own answer, so the model learns its next terminal
                // moved without having to ask.
                let terminals, _, _, _, _ = fixture ()
                let! _ = terminals.SetProfile ActorRef.Agent SandboxName.defaultName (Some checkout)
                let! cleared = terminals.ClearProfilesUnder ActorRef.Agent checkout
                Expect.equal (cleared |> List.map SandboxName.value) [ "default" ] "the one it cleared, named"
            }

        testCaseAsync "a profile in a sibling that shares a prefix is left alone" <|
            async {
                let terminals, _, _, _, _ = fixture ()
                let! _ = terminals.SetProfile ActorRef.Agent SandboxName.defaultName (Some checkout)
                let! cleared = terminals.ClearProfilesUnder ActorRef.Agent "/ws/repos/octo/hell"
                Expect.isEmpty cleared "a prefix is not a parent"
                Expect.equal
                    (terminals.Profiles () |> ShellProfileProjection.workingDirectory SandboxName.defaultName)
                    (Some checkout)
                    "and the profile still points where it did"
            }

        testCaseAsync "the next terminal after a cleared profile opens where the sandbox puts it" <|
            async {
                let terminals, _, ptySpawned, _, _ = fixture ()
                let! _ = terminals.SetProfile ActorRef.Agent SandboxName.defaultName (Some checkout)
                let! _ = terminals.ClearProfilesUnder ActorRef.Agent checkout
                let! _ = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                Expect.equal (ptyDirectories ptySpawned) [ None ] "nothing is asked for a directory that has gone"
            }

        testCaseAsync "the query reports where each sandbox's terminals start" <|
            async {
                // One registration reaches the agent as a read-only tool and the people as a
                // settings section. Nobody writes a panel; what is pinned is that the rows say
                // what the manager holds.
                let terminals, _, _, _, _ = fixture ()
                let! _ = terminals.SetProfile ActorRef.Agent SandboxName.defaultName (Some checkout)
                let registration = Yession.Host.ShellProfile.query (fun () -> terminals)
                match! registration.Read () with
                | Error e -> failwithf "the query failed: %s" e
                | Ok (RowsOf [ row ]) ->
                    Expect.equal
                        (row |> List.tryFind (fst >> (=) "cwd") |> Option.map snd)
                        (Some (CellText checkout))
                        "the row names the directory"
                | Ok other -> failwithf "expected one row, got %A" other
            }

        testCaseAsync "the query answers in the shape it declares" <|
            async {
                let terminals, _, _, _, _ = fixture ()
                let registration = Yession.Host.ShellProfile.query (fun () -> terminals)
                match! registration.Read () with
                | Error e -> failwithf "the query failed: %s" e
                | Ok value -> Expect.isTrue (QueryValue.fits registration.Def.Shape value) "the registry would accept it"
            }

        testCaseAsync "the profile survives a shell that could not start there" <|
            async {
                // Left alone for a person to fix: a manager that cleared it on one failed spawn
                // would silently undo a decision nobody revisited.
                let terminals, _, _, _, vanish = fixture ()
                let! _ = terminals.SetProfile ActorRef.Agent SandboxName.defaultName (Some checkout)
                vanish ()
                let! _ = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                Expect.equal
                    (terminals.Profiles () |> ShellProfileProjection.workingDirectory SandboxName.defaultName)
                    (Some checkout)
                    "a failed spawn is not a decision"
            }
    ]

// The agent's own terminal verbs (Plan 20, stage 3): the same ones a person has, over the
// same terminals. The cap is a refusal rather than a hold, because only the agent knows which
// of its terminals it has finished with.
let private agentVerbTests =
    let fixture () =
        let log = newLog ()
        let environment, _ = scriptedEnvironment (fun _ -> [], 0)
        let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
        let terminals, _, _ = makeTerminals log environment openTranscript readTranscript []
        terminals, log
    let openFour (terminals: SessionTerminals.SessionTerminals) =
        async {
            let mutable last = Unchecked.defaultof<Result<TerminalId, string>>
            for n in 1 .. 4 do
                let! opened = terminals.OpenAgentTerminal SandboxName.defaultName (sprintf "job-%d" n)
                last <- opened
            return last
        }
    testList "The agent's terminal verbs" [

        testCaseAsync "a terminal it opens is named for the job, and is its own" <|
            async {
                let terminals, log = fixture ()
                let! opened = terminals.OpenAgentTerminal SandboxName.defaultName "tests"
                let id = opened |> expect
                Expect.isTrue (terminals.OpenedByAgent id) "the agent may close what it opened"
                let! events = eventsOf log
                let titles = events |> List.choose (function SessionEvent.TerminalOpened o -> Some o.Title | _ -> None)
                Expect.equal titles [ "tests" ] "and everyone reads the name it gave"
            }

        testCaseAsync "at the limit it is refused, and told the number" <|
            async {
                // Named, so the agent can act on it: "no" with a number is a decision it can
                // make, "no" on its own is a wall it can only retry.
                let terminals, _ = fixture ()
                let! _ = openFour terminals
                let! fifth = terminals.OpenAgentTerminal SandboxName.defaultName "one more"
                match fifth with
                | Ok _ -> failwith "a fifth terminal must not open"
                | Error reason ->
                    Expect.isTrue (reason.Contains "4") "the limit is stated"
                    Expect.isTrue (reason.Contains "close") "and so is what to do about it"
            }

        testCaseAsync "closing one makes room" <|
            async {
                let terminals, _ = fixture ()
                let! fourth = openFour terminals
                let! _ = terminals.Close (fourth |> expect) "done"
                let! next = terminals.OpenAgentTerminal SandboxName.defaultName "one more"
                Expect.isTrue (Result.isOk next) "the limit counts what is OPEN, not what ever was"
            }

        testCaseAsync "a plain command still gets a shell at the limit" <|
            async {
                // The general-purpose terminal is not counted. An agent that has filled its
                // allowance can still run one command, which is the shell it should never have
                // to ask for.
                let terminals, _ = fixture ()
                let! _ = openFour terminals
                let! general = terminals.AgentTerminal SandboxName.defaultName "git status"
                Expect.isTrue (Result.isOk general) "the one door is never closed by the cap"
            }

        testCaseAsync "a terminal a person opened is not the agent's" <|
            async {
                let terminals, _ = fixture ()
                let! theirs = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "mine"
                Expect.isFalse
                    (terminals.OpenedByAgent (theirs |> expect))
                    "a human typing in their own shell is not the agent's to end"
            }
    ]

let tests =
    testList "Terminals (Plan 13)" [
        affordanceTests
        sourceTests
        drainTests
        projectionTests
        markTests
        emulatorTests
        rejectionTests
        leaseTests
        flipTests
        idleLeaseTests
        integrationTests
        retentionTests
        leaseGateTests
        leaseCommandTests
        waitTests
        digestTests
        ansiTests
        onlcrTests
        transcriptTests
        agentTerminalTests
        agentVerbTests
        shellProfileTests
        codecTests
        orderTests
        managerTests
        schedulerTests
        syncTests
    ]
