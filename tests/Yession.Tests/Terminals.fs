module Yession.Tests.Terminals

// Terminals on the WorkSandbox (docs/plans/12). Everything here runs in the CHEAP tier:
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
// Approving a command is a CLIENT-side CRDT write (`ApprovePendingMsg` through the
// Ylmish binding), so a test driving the Session Process's own doc has to make that write
// the way a peer's merged update would arrive. These are the only Yjs calls in this file,
// and they exist so no production API has to grow a setter that only a test would call.

[<Fable.Core.Emit("(() => { const e = $0.getMap('pending').get($1); if (e) e.set($2, $3) })()")>]
let private setQueuedField (doc: Y.Doc) (id: string) (field: string) (value: obj) : unit = Fable.Core.Util.jsNative

[<Fable.Core.Emit("(() => { const m = $0.getMap('gates'); const e = new $2.Map(); m.set($1, e); e.set('mode', $3) })()")>]
let private setModeRaw (doc: Y.Doc) (id: string) (yjs: obj) (mode: string) : unit = Fable.Core.Util.jsNative

[<Fable.Core.Import("*", "yjs")>]
let private yjsModule : obj = Fable.Core.Util.jsNative

// A doc as it was written before Plan 15 stage 3: the old roots, the old field names. There
// is no production writer for this shape any more — that is the point — so the only way to
// test the migration is to write it the way the build that is being migrated FROM did.

[<Fable.Core.Emit("(() => { const q = $0.getMap('terminalQueue'); const e = new $1.Map(); q.set($2, e); e.set('terminal', $3); e.set('author', $4); e.set('order', $5); e.set('approvedBy', '') })()")>]
let private legacyEnqueueInDoc (doc: Y.Doc) (yjs: obj) (id: string) (terminal: string) (author: string) (order: float) : unit = Fable.Core.Util.jsNative

[<Fable.Core.Emit("(() => { const m = $0.getMap('terminalModes'); const e = new $1.Map(); m.set($2, e); e.set('mode', $3) })()")>]
let private legacyModeInDoc (doc: Y.Doc) (yjs: obj) (terminal: string) (mode: string) : unit = Fable.Core.Util.jsNative

/// A peer's approval, landing in this doc.
let private approveInDoc (doc: Y.Doc) (id: QueueId) (approver: PeerId) : unit =
    setQueuedField doc (QueueId.value id) "approvedBy" (box (PeerId.value approver))

/// A queue entry field, set to whatever a peer we do not control might have written.
let private setQueuedFieldInDoc (doc: Y.Doc) (id: QueueId) (field: string) (value: string) : unit =
    setQueuedField doc (QueueId.value id) field (box value)

/// A terminal's mode register, set to a raw string.
let private setModeInDoc (doc: Y.Doc) (terminal: TerminalId) (mode: string) : unit =
    setModeRaw doc (GateSubject.describe (ForTerminal terminal)) yjsModule mode

// --- The approval policy -----------------------------------------------------------------

let private approvalTests =
    testList "Approval policy" [
        testCase "the default mode gates the agent and nobody else" <| fun () ->
            Expect.isTrue (ApprovalMode.requiresApproval ApproveAgent ActorRef.Agent) "the agent is gated"
            Expect.isFalse (ApprovalMode.requiresApproval ApproveAgent (PeerRef ada)) "a peer is not"
            Expect.isFalse
                (ApprovalMode.requiresApproval ApproveAgent ActorRef.SessionProcess)
                "the Process's own commands are not gated — a gate there deadlocks on housekeeping"

        testCase "approve-all gates everyone; auto gates nobody" <| fun () ->
            for author in [ ActorRef.Agent; PeerRef ada; ActorRef.SessionProcess ] do
                Expect.isTrue (ApprovalMode.requiresApproval ApproveAll author) "approve-all gates everything"
                Expect.isFalse (ApprovalMode.requiresApproval AutoRun author) "auto gates nothing"

        testCase "an absent mode register reads as the default, never as no gate" <| fun () ->
            // The safe direction: a terminal nobody configured must still hold the agent.
            let mode = SyncedSessionState.modeOf terminalA SyncedSessionState.empty
            Expect.equal mode ApproveAgent "an unconfigured terminal is approve-agent"

        // --- The subject the mode is keyed by (Plan 15, stage 3) ---------------------------

        testCase "the default is per subject KIND, which is how both sides keep today's behaviour" <| fun () ->
            Expect.equal
                (GateSubject.defaultMode (ForTerminal terminalA))
                ApproveAgent
                "a terminal is reviewed unless somebody says otherwise"
            Expect.equal
                (GateSubject.defaultMode (ForCommand "add_repo"))
                AutoRun
                "a command is not reviewed until somebody says so"
            Expect.equal
                (SyncedSessionState.gateOf (ForCommand "add_repo") SyncedSessionState.empty)
                AutoRun
                "and an unconfigured session reads that default rather than a stored register"

        testCase "a configured gate outranks the default, for either kind" <| fun () ->
            let state =
                { SyncedSessionState.empty with
                    Gates =
                        Map.ofList [ ForTerminal terminalA, AutoRun; ForCommand "add_repo", ApproveAgent ] }
            Expect.equal (SyncedSessionState.gateOf (ForTerminal terminalA) state) AutoRun "the terminal was opened out"
            Expect.equal
                (SyncedSessionState.gateOf (ForCommand "add_repo") state)
                ApproveAgent
                "and the command was gated in"
            Expect.equal
                (SyncedSessionState.gateOf (ForCommand "start_work_sandbox") state)
                AutoRun
                "a sibling command is untouched — a gate is per subject, never per kind"

        testCase "a subject round-trips through its wire form, and a junk one is refused" <| fun () ->
            for subject in [ ForTerminal terminalA; ForCommand "add_repo" ] do
                Expect.equal
                    (GateSubject.parse (GateSubject.describe subject))
                    (Some subject)
                    (sprintf "%A survives the doc key it is stored under" subject)
            Expect.equal (GateSubject.parse "terminal:") None "a terminal with no id is not a subject"
            Expect.equal (GateSubject.parse "command:") None "nor is a command with no name"
            Expect.equal (GateSubject.parse "add_repo") None "and an unprefixed key names nothing"
    ]

// --- The drain's decision ------------------------------------------------------------------

let private entry (id: string) (terminal: TerminalId) (author: ActorRef) (order: float) (approved: PeerId option) =
    { QueueId = queue id
      Subject = ForTerminal terminal
      Author = author
      Order = order
      Payload = CommandLine
      OnBehalfOf = None
      ApprovedBy = approved
      RejectedBy = None
      RejectedReason = None }

/// The same entry, refused. Kept beside `entry` so a test says which of the two verdicts
/// it is exercising rather than threading a `None` through every call that is not about
/// rejection.
let private rejected (e: PendingAct) (by: PeerId) (reason: string option) =
    { e with RejectedBy = Some by; RejectedReason = reason }

let private queueOf entries =
    entries |> List.map (fun (e: PendingAct) -> e.QueueId, e) |> Map.ofList

let private allOpen (_: TerminalId) = true
let private planWith consumed busy isOpen modeOf entries =
    TerminalQueueDrain.plan consumed busy Set.empty Set.empty isOpen modeOf (queueOf entries)

/// The same plan with a lease in play (Plan 13, stage 2e).
let private planLeased consumed busy leased isOpen modeOf entries =
    TerminalQueueDrain.plan consumed busy leased Set.empty isOpen modeOf (queueOf entries)

/// ...and with the shell's marks gone (Plan 13, stage 2f).
let private planLost consumed lost isOpen modeOf entries =
    TerminalQueueDrain.plan consumed Set.empty Set.empty lost isOpen modeOf (queueOf entries)

let private drainTests =
    testList "Terminal drain plan" [
        testCase "one command per terminal, and terminals do not block each other" <| fun () ->
            let plan =
                planWith Set.empty Set.empty allOpen (fun _ -> AutoRun)
                    [ entry "a1" terminalA (PeerRef ada) 1.0 None
                      entry "a2" terminalA (PeerRef ada) 2.0 None
                      entry "b1" terminalB (PeerRef bob) 1.0 None ]
            Expect.equal
                (plan.Ready |> List.map (fun (_, e) -> QueueId.value e.QueueId))
                [ "q-a1"; "q-b1" ]
                "the head of each terminal runs; the second in a terminal waits its turn"

        testCase "a terminal with a block already running is skipped" <| fun () ->
            let plan =
                planWith Set.empty (Set.singleton (TerminalId.value terminalA)) allOpen (fun _ -> AutoRun)
                    [ entry "a1" terminalA (PeerRef ada) 1.0 None
                      entry "b1" terminalB (PeerRef bob) 1.0 None ]
            Expect.equal
                (plan.Ready |> List.map (fun (_, e) -> QueueId.value e.QueueId))
                [ "q-b1" ]
                "a busy terminal runs nothing more; its sibling is unaffected"

        testCase "an unapproved head holds the queue rather than being skipped over" <| fun () ->
            // The property that matters: approval must not silently REORDER execution. In a
            // shell, running the approved second command first is a different program.
            let plan =
                planWith Set.empty Set.empty allOpen (fun _ -> ApproveAgent)
                    [ entry "a1" terminalA ActorRef.Agent 1.0 None
                      entry "a2" terminalA (PeerRef ada) 2.0 None ]
            Expect.isEmpty plan.Ready "the terminal waits at its unapproved head"

        testCase "an approved head runs" <| fun () ->
            let plan =
                planWith Set.empty Set.empty allOpen (fun _ -> ApproveAgent)
                    [ entry "a1" terminalA ActorRef.Agent 1.0 (Some ada) ]
            Expect.equal (plan.Ready |> List.map (fun (_, e) -> QueueId.value e.QueueId)) [ "q-a1" ] "the approval releases it"

        testCase "a closed terminal runs nothing" <| fun () ->
            let plan =
                planWith Set.empty Set.empty (fun _ -> false) (fun _ -> AutoRun)
                    [ entry "a1" terminalA (PeerRef ada) 1.0 None ]
            Expect.isEmpty plan.Ready "nothing runs in a terminal that is not open"

        testCase "an entry already named by a started block is repaired away, never re-run" <| fun () ->
            // The crash window: the block event was appended and the doc removal was not.
            let plan =
                planWith (Set.singleton "q-a1") Set.empty allOpen (fun _ -> AutoRun)
                    [ entry "a1" terminalA (PeerRef ada) 1.0 None
                      entry "a2" terminalA (PeerRef ada) 2.0 None ]
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
                      Author = PeerRef ada
                      ApprovedBy = None
                      Command = "ls"
                      FromSeq = 0 }
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
          Author = PeerRef ada
          ApprovedBy = None
          Command = command
          FromSeq = fromSeq }

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
        testCase "a refused entry never runs, under ANY mode — AutoRun included" <| fun () ->
            // The whole point of the gate order: a refusal outranks a policy that would
            // otherwise have run the command without asking anyone.
            for mode in [ AutoRun; ApproveAgent; ApproveAll ] do
                let plan =
                    planWith Set.empty Set.empty allOpen (fun _ -> mode)
                        [ rejected (entry "a1" terminalA ActorRef.Agent 1.0 (Some ada)) bob (Some "no") ]
                Expect.isEmpty plan.Ready (sprintf "nothing runs under %A" mode)
                Expect.equal
                    (plan.Rejections |> List.map (fun (_, e) -> QueueId.value e.QueueId))
                    [ "q-a1" ]
                    (sprintf "and the refusal is planned under %A" mode)

        testCase "an approval already granted does not override a later refusal" <| fun () ->
            let plan =
                planWith Set.empty Set.empty allOpen (fun _ -> ApproveAll)
                    [ rejected (entry "a1" terminalA ActorRef.Agent 1.0 (Some ada)) bob None ]
            Expect.isEmpty plan.Ready "approved and then refused is refused"

        testCase "a refused head holds its queue rather than being skipped over" <| fun () ->
            // Same property the approval gate has: a verdict must not silently REORDER
            // execution. The entry behind it waits until the refusal is drained away.
            let plan =
                planWith Set.empty Set.empty allOpen (fun _ -> AutoRun)
                    [ rejected (entry "a1" terminalA ActorRef.Agent 1.0 None) bob None
                      entry "a2" terminalA (PeerRef ada) 2.0 None ]
            Expect.isEmpty plan.Ready "the terminal waits at its refused head"

        testCase "a refusal is planned for a terminal that is busy or closed" <| fun () ->
            // Refusing touches no process, so it does not queue behind one. Someone can
            // clear a bad queue while a colleague's command is still running.
            let plan =
                planWith Set.empty (Set.singleton (TerminalId.value terminalA)) (fun _ -> false) (fun _ -> AutoRun)
                    [ rejected (entry "a1" terminalA ActorRef.Agent 1.0 None) bob None ]
            Expect.equal (List.length plan.Rejections) 1 "the refusal is recorded regardless"
            Expect.isEmpty plan.Ready "and nothing runs"

        testCase "a rejected QueueId folds into the consumed set, so it can never run after" <| fun () ->
            let consumed = rejectedEvent terminalA "a1" "1" bob None |> TerminalQueueDrain.consumedOf
            Expect.equal consumed (Some "q-a1") "the rejection is the exactly-once anchor"
            let plan =
                planWith (Set.singleton "q-a1") Set.empty allOpen (fun _ -> AutoRun)
                    [ entry "a1" terminalA ActorRef.Agent 1.0 None ]
            Expect.isEmpty plan.Ready "an entry already refused in the log never runs"
            Expect.isEmpty plan.Rejections "nor is it refused a second time"
            Expect.equal
                (plan.Removals |> List.map QueueId.value)
                [ "q-a1" ]
                "it is simply swept out of the doc"

        testCase "the reject/drain race leaves exactly one outcome, either way round" <| fun () ->
            // Under AutoRun a human can press reject in the same tick the drain takes the
            // entry. Whichever event reaches the log first wins; the loser is dropped as
            // already consumed. Neither side needs a lock.
            let started =
                SessionEvent.TerminalBlockStarted
                    { TerminalId = terminalA
                      BlockId = block "1"
                      QueueId = Some (queue "a1")
                      Author = ActorRef.Agent
                      ApprovedBy = None
                      Command = "make"
                      FromSeq = 0 }
            let rejection = rejectedEvent terminalA "a1" "2" bob None
            for winner in [ started; rejection ] do
                let consumed =
                    [ winner ] |> List.choose TerminalQueueDrain.consumedOf |> Set.ofList
                let plan =
                    planWith consumed Set.empty allOpen (fun _ -> AutoRun)
                        [ rejected (entry "a1" terminalA ActorRef.Agent 1.0 None) bob None ]
                Expect.isEmpty plan.Ready "the loser does not run it"
                Expect.isEmpty plan.Rejections "and does not record a second verdict"

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
            Expect.equal refusal.Author ActorRef.Agent "and whose command it was"
            Expect.equal refusal.ApprovedBy None "nobody approved it — someone did the opposite"
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
            Expect.isSome (store.ReadChunk terminalA 0) "and its recording is still served"
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
            let entries = [ entry "a1" terminalA (PeerRef ada) 1.0 None ]
            let lost = Set.singleton (TerminalId.value terminalA)
            Expect.isEmpty (planLost Set.empty lost allOpen (fun _ -> AutoRun) entries).Ready "nothing runs"
            Expect.equal
                (TerminalQueueDrain.holdOf Set.empty Set.empty Set.empty lost allOpen (fun _ -> AutoRun) (queueOf entries) terminalA)
                (Some TerminalQueueDrain.AwaitingIntegration)
                "and the hold names the repair, not a person"

        testCase "re-arming yields the entry Ready, unchanged" <| fun () ->
            let entries = [ entry "a1" terminalA (PeerRef ada) 1.0 None ]
            Expect.equal
                ((planLost Set.empty Set.empty allOpen (fun _ -> AutoRun) entries).Ready
                 |> List.map (fun (_, e) -> QueueId.value e.QueueId))
                [ "q-a1" ]
                "the command that was held runs once marking is back"

        testCase "a refusal is planned even while the terminal is not marking" <| fun () ->
            // Refusing touches no pty, so it outranks this exactly as it outranks the lease
            // and the mode: a bad queue can be cleared on a terminal nobody can run in.
            let plan =
                planLost Set.empty (Set.singleton (TerminalId.value terminalA)) allOpen (fun _ -> AutoRun)
                    [ rejected (entry "a1" terminalA ActorRef.Agent 1.0 None) bob None ]
            Expect.equal (List.length plan.Rejections) 1 "the refusal is recorded"
            Expect.isEmpty plan.Ready "and still nothing runs"

        testCase "the agent is told the terminal is not free, and does not wait on it" <| fun () ->
            // Only a person re-arming brings marking back, so this is an unbounded wait like
            // an approval — it returns at once rather than burning a deadline.
            Expect.equal
                (TerminalCommandWait.step
                    false
                    false
                    { TerminalCommandWait.Observation.Block = None
                      TerminalCommandWait.Observation.InQueue = true
                      TerminalCommandWait.Observation.IsHead = true
                      TerminalCommandWait.Observation.Hold = Some TerminalQueueDrain.AwaitingIntegration })
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
            for hold in [ None; Some TerminalQueueDrain.NotWaiting; Some TerminalQueueDrain.AwaitingApproval ] do
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

        testCase "an agent's block does not flip — live mode is human-only" <| fun () ->
            Expect.equal (TerminalFlip.propose true None false (Some ActorRef.Agent)) FlipNothing "no agent lease"
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
            let entries = [ entry "a1" terminalA (PeerRef ada) 1.0 None ]
            let leased = Set.singleton (TerminalId.value terminalA)
            let plan = planLeased Set.empty Set.empty leased allOpen (fun _ -> AutoRun) entries
            Expect.isEmpty plan.Ready "the queue waits for the terminal"
            Expect.equal
                (TerminalQueueDrain.holdOf Set.empty Set.empty leased Set.empty allOpen (fun _ -> AutoRun) (queueOf entries) terminalA)
                (Some TerminalQueueDrain.AwaitingTerminal)
                "and the hold names the terminal, not an approval"

        testCase "releasing the lease yields the entry Ready, unchanged" <| fun () ->
            let entries = [ entry "a1" terminalA (PeerRef ada) 1.0 None ]
            let plan = planLeased Set.empty Set.empty Set.empty allOpen (fun _ -> AutoRun) entries
            Expect.equal (plan.Ready |> List.map (fun (_, e) -> QueueId.value e.QueueId)) [ "q-a1" ] "it runs on release"
            Expect.equal
                (TerminalQueueDrain.holdOf Set.empty Set.empty Set.empty Set.empty allOpen (fun _ -> AutoRun) (queueOf entries) terminalA)
                None
                "nothing is holding it"

        testCase "the three holds are told apart" <| fun () ->
            let entries = [ entry "a1" terminalA ActorRef.Agent 1.0 None ]
            let hold busy leased mode =
                TerminalQueueDrain.holdOf Set.empty busy leased Set.empty allOpen (fun _ -> mode) (queueOf entries) terminalA
            let busyA = Set.singleton (TerminalId.value terminalA)
            Expect.equal (hold busyA Set.empty AutoRun) (Some TerminalQueueDrain.AwaitingBlock) "a block is running"
            Expect.equal (hold Set.empty busyA AutoRun) (Some TerminalQueueDrain.AwaitingTerminal) "a peer is typing"
            Expect.equal
                (hold Set.empty Set.empty ApproveAgent)
                (Some TerminalQueueDrain.AwaitingApproval)
                "the agent needs a yes"

        testCase "a refusal is planned even while a peer holds the terminal" <| fun () ->
            // Refusing touches no pty, so it outranks the lease exactly as it outranks the
            // mode: someone can clear a bad queue while a colleague is inside vim.
            let leased = Set.singleton (TerminalId.value terminalA)
            let plan =
                planLeased Set.empty Set.empty leased allOpen (fun _ -> AutoRun)
                    [ rejected (entry "a1" terminalA ActorRef.Agent 1.0 None) bob None ]
            Expect.equal (List.length plan.Rejections) 1 "the refusal is recorded"
            Expect.isEmpty plan.Ready "and still nothing runs"
    ]

// --- The two waits (Plan 13, stage 3b) ---------------------------------------------------

let private blockOf (status: TerminalBlockStatus) : TerminalBlock =
    { BlockId = block "1"
      QueueId = Some (queue "a1")
      Author = ActorRef.Agent
      ApprovedBy = None
      Command = "make"
      FromSeq = 0
      ToSeq = None
      Status = status }

/// The observation of a request that is the head of its terminal's queue, held for `hold`.
let private waitingOn (hold: TerminalQueueDrain.TerminalHold option) : TerminalCommandWait.Observation =
    { TerminalCommandWait.Observation.Block = None; TerminalCommandWait.Observation.InQueue = true; TerminalCommandWait.Observation.IsHead = true; TerminalCommandWait.Observation.Hold = hold }

let private waitTests =
    testList "The command wait" [
        testCase "an approval gets the GRACE, and yields a handle when it runs out" <| fun () ->
            // Waiting on a person is unbounded in principle — they may be asleep — so the
            // grace exists only so that a supervised session still chains.
            let awaiting = waitingOn (Some TerminalQueueDrain.AwaitingApproval)
            Expect.equal
                (TerminalCommandWait.step false false awaiting)
                TerminalCommandWait.KeepWaiting
                "inside the grace it keeps waiting, so an approval in seconds still chains"
            Expect.equal
                (TerminalCommandWait.step true false awaiting)
                (TerminalCommandWait.Return TerminalCommandAwaitingApproval)
                "past it, the turn is handed back rather than held"

        testCase "a held terminal does NOT get the grace — it returns at once" <| fun () ->
            // A peer with a terminal open is mid-task and will not be done in five seconds.
            // Burning the grace on that wait only makes the turn slower.
            Expect.equal
                (TerminalCommandWait.step false false (waitingOn (Some TerminalQueueDrain.AwaitingTerminal)))
                (TerminalCommandWait.Return TerminalCommandAwaitingTerminal)
                "no waiting at all"

        testCase "waiting on a PROCESS gets the process deadline, not the grace" <| fun () ->
            // A command running ahead of ours in the same terminal, and our own command once
            // it starts: both are waits on a process, so a quick one still chains.
            for observation in
                [ waitingOn (Some TerminalQueueDrain.AwaitingBlock)
                  { TerminalCommandWait.Observation.Block = Some (blockOf BlockRunning); TerminalCommandWait.Observation.InQueue = false; TerminalCommandWait.Observation.IsHead = false; TerminalCommandWait.Observation.Hold = None } ] do
                Expect.equal
                    (TerminalCommandWait.step true false observation)
                    TerminalCommandWait.KeepWaiting
                    "an elapsed approval grace does not end a wait on a process"
            Expect.equal
                (TerminalCommandWait.step true true (waitingOn (Some TerminalQueueDrain.AwaitingBlock)))
                (TerminalCommandWait.Return TerminalCommandAwaitingTerminal)
                "the terminal was never free"
            Expect.equal
                (TerminalCommandWait.step
                    true
                    true
                    { TerminalCommandWait.Observation.Block = Some (blockOf BlockRunning); TerminalCommandWait.Observation.InQueue = false; TerminalCommandWait.Observation.IsHead = false; TerminalCommandWait.Observation.Hold = None })
                (TerminalCommandWait.Return TerminalCommandRunning)
                "and a running block yields — the deadline is a yield, not a cancellation"

        testCase "an entry BEHIND another waits for the queue, whatever the head waits for" <| fun () ->
            // Reporting the head's reason as ours would tell the agent its own command needs
            // an approval when somebody else's does.
            let behind =
                { TerminalCommandWait.Observation.Block = None; TerminalCommandWait.Observation.InQueue = true; TerminalCommandWait.Observation.IsHead = false; TerminalCommandWait.Observation.Hold = Some TerminalQueueDrain.AwaitingApproval }
            Expect.equal (TerminalCommandWait.step true false behind) TerminalCommandWait.KeepWaiting "still queued"
            Expect.equal
                (TerminalCommandWait.step true true behind)
                (TerminalCommandWait.Return TerminalCommandAwaitingTerminal)
                "and it names the queue, not an approval it does not need"

        testCase "an outcome ends the wait, whichever deadline is still running" <| fun () ->
            for status, expected in
                [ BlockFinished (CommandSucceeded 0), TerminalCommandRan (CommandSucceeded 0)
                  BlockFinished (CommandFailed 3), TerminalCommandRan (CommandFailed 3)
                  BlockRejected (PeerRef bob, Some "not on prod"), TerminalCommandRefused (PeerRef bob, Some "not on prod") ] do
                Expect.equal
                    (TerminalCommandWait.step false false { TerminalCommandWait.Observation.Block = Some (blockOf status); TerminalCommandWait.Observation.InQueue = false; TerminalCommandWait.Observation.IsHead = false; TerminalCommandWait.Observation.Hold = None })
                    (TerminalCommandWait.Return expected)
                    "an answer is returned the moment it exists"

        testCase "a withdrawn request is an absence, not an outcome" <| fun () ->
            // Deleting a queued entry is withdrawal and has no event. Reporting it as any
            // status would be inventing one.
            Expect.equal
                (TerminalCommandWait.step false false { TerminalCommandWait.Observation.Block = None; TerminalCommandWait.Observation.InQueue = false; TerminalCommandWait.Observation.IsHead = false; TerminalCommandWait.Observation.Hold = None })
                TerminalCommandWait.Gone
                "the caller is told the request is gone"

        testCase "every status the agent can be handed says which state it is in" <| fun () ->
            // The wording is the mechanism, not decoration: told "queued" when it is blocked
            // on a person, a model concludes after a silent pause that the command failed and
            // tries something else — which is how a review gate becomes a thing to route
            // around. Every case must be distinguishable, so none may collapse into another.
            let statuses =
                [ TerminalCommandRan (CommandSucceeded 0)
                  TerminalCommandRan (CommandFailed 1)
                  TerminalCommandRunning
                  TerminalCommandAwaitingApproval
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
          TriggeredByMessageId = MessageId.create ("m-" + n) |> expect }

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

        testCase "the digest carries who wrote the command and who approved it" <| fun () ->
            let events =
                [ opened terminalA "build"
                  turnStarted "1"
                  SessionEvent.TerminalBlockStarted
                      { TerminalId = terminalA
                        BlockId = block "1"
                        QueueId = None
                        Author = ActorRef.Agent
                        ApprovedBy = Some (PeerRef bob)
                        Command = "rm -rf build"
                        FromSeq = 0 }
                  completed terminalA "1" (CommandSucceeded 0) 3 ]
            let entry = (digestOf events).Head
            Expect.equal entry.Author ActorRef.Agent "the agent's own command"
            Expect.equal entry.ApprovedBy (Some (PeerRef bob)) "and the human who let it run"
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

let private transcriptTests =
    testList "Transcript" [
        testCase "chunk bounds are fixed, so a full chunk is immutable" <| fun () ->
            Expect.equal (TranscriptChunk.indexOf 0) 0 "line 0 is in chunk 0"
            Expect.equal (TranscriptChunk.indexOf (TranscriptChunk.size - 1)) 0 "and so is the last of chunk 0"
            Expect.equal (TranscriptChunk.indexOf TranscriptChunk.size) 1 "the next line starts chunk 1"
            Expect.equal (TranscriptChunk.firstSeq 2) (TranscriptChunk.size * 2) "chunk starts are exact multiples"
            Expect.isTrue ((TranscriptChunk.cacheControl true).Contains "immutable") "a full chunk caches hard"
            Expect.equal (TranscriptChunk.cacheControl false) "no-store" "the growing tail never does"

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
        testCase "a replay rebuilt from fetched chunks IS the recording on disk" <| fun () ->
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
            let lines, _ = store.ReadChunk terminalA 0 |> Option.defaultWith (fun () -> failwith "no chunk 0")
            let decoded =
                lines
                |> List.mapi (fun i line -> i, Codec.fromString Codec.transcriptLine line)
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
                        Author = ActorRef.Agent
                        ApprovedBy = Some (PeerRef ada)
                        Command = "ls -la"
                        FromSeq = 3 }
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

        testCase "terminal frames round-trip over the session transport" <| fun () ->
            let codec = Codec.sessionFrame Codec.string
            let frames =
                [ Terminal (TerminalRecord (terminalA, 7, { At = 1.0; Kind = TranscriptOutput; Data = "hi" }))
                  Terminal (TerminalTranscriptAvailable (terminalA, 42))
                  // The screen a joining peer renders, and the seq it composes with.
                  Terminal (TerminalSnapshot (terminalA, 42, "screen"))
                  // Live mode's two peer-authored frames (stage 2e).
                  Terminal (TerminalInput (terminalA, "\u001b[A"))
                  Terminal (TerminalResize (terminalA, 120, 40)) ]
            for frame in frames do
                let encoded = Codec.toString codec frame
                Expect.equal (Codec.fromString codec encoded) (Ok frame) ("round-trips: " + encoded)

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
                    [ entry "a1" terminalA (PeerRef ada) 2.0 None
                      entry "a2" terminalA (PeerRef ada) 1.0 None
                      entry "b1" terminalB (PeerRef bob) 5.0 None ]
            Expect.equal
                (TerminalQueueOrder.sortedFor terminalA q |> List.map (fun e -> QueueId.value e.QueueId))
                [ "q-a2"; "q-a1" ]
                "A's entries, in order"
            Expect.equal (TerminalQueueOrder.nextFor terminalB q) 6.0 "the tail of B's queue, not of everything"

        testCase "moving an entry never leaves its terminal" <| fun () ->
            let q =
                queueOf
                    [ entry "a1" terminalA (PeerRef ada) 1.0 None
                      entry "a2" terminalA (PeerRef ada) 2.0 None
                      entry "b1" terminalB (PeerRef bob) 1.0 None ]
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

let private mintFrom (ids: string list) =
    let remaining = ResizeArray<string> ids
    fun () ->
        let next = remaining.[0]
        if remaining.Count > 1 then remaining.RemoveAt 0
        next

let private makeTerminalsWith attach (log: EventLog<SessionEvent>) environment openTranscript readTranscript openAtBoot =
    let mintTerminal = mintFrom [ "term-a"; "term-b" ]
    let mintBlock = mintFrom [ "b-1"; "b-2"; "b-3" ]
    let records = ResizeArray<TerminalId * int * TranscriptRecord> ()
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
            (fun id seq record -> records.Add (id, seq, record))
            // No scheduler in these tests: the drain's re-arm is exercised where the drain is
            // (`TerminalScheduler`), and wiring a real one here would test the scheduler twice
            // while making every manager assertion depend on it.
            ignore
            attach
            openAtBoot
    terminals, records

let private makeTerminals log environment openTranscript readTranscript openAtBoot =
    makeTerminalsWith AttachTerminal.unavailable log environment openTranscript readTranscript openAtBoot

let private managerTests =
    testList "Terminal manager" [
        testCaseAsync "opening a terminal ensures the environment and records the fact" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let terminals, _ = makeTerminals log environment openTranscript readTranscript []
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
                let terminals, records = makeTerminals log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                let id = opened |> expect
                let entry = entry "a1" id (PeerRef ada) 1.0 None
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
                Expect.equal (recordsOf TranscriptInput) [ "echo hello\n" ] "what was typed is recorded too"
                Expect.equal (recordsOf TranscriptOutput) [ "hello\n" ] "stdout"
                Expect.equal (recordsOf TranscriptStderr) [ "warn\n" ] "and stderr, still told apart"
                match transcript with
                | TranscriptHeaderLine _ :: _ -> ()
                | other -> failwithf "a transcript starts with its header, got %A" other

                Expect.equal
                    (records |> Seq.map (fun (_, seq, _) -> seq) |> List.ofSeq)
                    [ 1; 2; 3 ]
                    "every record is broadcast with the line index it was written at"
            }

        testCaseAsync "a failing command keeps its output and reports the exit code" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [ Stderr, "no such file\n" ], 2)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let terminals, _ = makeTerminals log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                let id = opened |> expect
                do! terminals.RunBlock id (entry "a1" id (PeerRef ada) 1.0 None) "cat missing" ignore
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
                let terminals, _ = makeTerminals log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                let id = opened |> expect
                do! terminals.RunBlock id (entry "a1" id (PeerRef ada) 1.0 None) "first" ignore
                do! terminals.RunBlock id (entry "a2" id (PeerRef ada) 2.0 None) "second" ignore
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

        testCaseAsync "an approval is recorded on the block, so the audit says who let it run" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let terminals, _ = makeTerminals log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                let id = opened |> expect
                do! terminals.RunBlock id (entry "a1" id ActorRef.Agent 1.0 (Some bob)) "rm -rf build" ignore
                let! events = eventsOf log
                let startedEvent =
                    events |> List.pick (function SessionEvent.TerminalBlockStarted e -> Some e | _ -> None)
                Expect.equal startedEvent.Author ActorRef.Agent "the agent wrote it"
                Expect.equal startedEvent.ApprovedBy (Some (PeerRef bob)) "and bob approved it"
            }

        testCaseAsync "runaway output is capped, and the gap is stated rather than hidden" <|
            async {
                let log = newLog ()
                // Comfortably past the 4 MiB per-block cap.
                let flood = String.replicate 200 (String.replicate 40000 "y")
                let environment, _ = scriptedEnvironment (fun _ -> [ Stdout, flood ], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let terminals, _ = makeTerminals log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                let id = opened |> expect
                do! terminals.RunBlock id (entry "a1" id (PeerRef ada) 1.0 None) "yes" ignore
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
                let terminals, _ = makeTerminals log environment openTranscript readTranscript [ terminalA; terminalB ]
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
                let terminals, _ = makeTerminals log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                let id = opened |> expect
                let! _ = terminals.Close id "closed by a peer"
                do! terminals.RunBlock id (entry "a1" id (PeerRef ada) 1.0 None) "make" ignore
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
                let terminals, _ = makeTerminals log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                let id = opened |> expect
                let doc = Y.Doc.Create ()
                let scheduler = TerminalScheduler.create doc terminals Set.empty
                // Queued exactly as the agent's capability queues one — same doc write.
                SyncedStateSync.enqueueTerminalCommand doc (queue "a1") id (PeerRef ada) 1.0 "echo ok"
                scheduler.Drain ()
                do! Async.Sleep 20

                let! events = eventsOf log
                let startedEvent =
                    events |> List.pick (function SessionEvent.TerminalBlockStarted e -> Some e | _ -> None)
                Expect.equal startedEvent.Command "echo ok" "the queued command ran"
                let synced = syncedOf doc
                Expect.isTrue (Map.isEmpty synced.Pending) "and its entry left the doc once consumed"
            }

        testCaseAsync "the agent's command waits for a human under the default mode" <|
            async {
                let log = newLog ()
                let environment, spawned = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let terminals, _ = makeTerminals log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                let id = opened |> expect
                let doc = Y.Doc.Create ()
                let scheduler = TerminalScheduler.create doc terminals Set.empty
                SyncedStateSync.enqueueTerminalCommand doc (queue "a1") id ActorRef.Agent 1.0 "rm -rf /"
                scheduler.Drain ()
                do! Async.Sleep 20
                Expect.isEmpty (List.ofSeq spawned) "nothing ran"
                let synced = syncedOf doc
                Expect.equal (Map.count synced.Pending) 1 "the command is still queued, visible and editable"

                // A peer approves it — a plain CRDT write, which is the entire mechanism.
                let entry = synced.Pending |> Map.toList |> List.head |> snd
                let approved =
                    ClientModel.update
                        (ApprovePendingMsg (entry.QueueId, bob))
                        { ClientModel.init { PeerId = bob; DisplayName = "Bob" } with Synced = synced }
                let approvedEntry = approved.Synced.Pending |> Map.find entry.QueueId
                Expect.equal approvedEntry.ApprovedBy (Some bob) "the approval is a register on the entry"

                // Reflect the approval into the doc the scheduler reads, and drain again.
                approveInDoc doc entry.QueueId bob
                scheduler.Drain ()
                do! Async.Sleep 20
                Expect.equal (List.length (List.ofSeq spawned)) 1 "and now it runs"
            }

        testCaseAsync "an entry the log already consumed is repaired away, never run twice" <|
            async {
                let log = newLog ()
                let environment, spawned = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let terminals, _ = makeTerminals log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                let id = opened |> expect
                let doc = Y.Doc.Create ()
                // The crash window: a block start reached the log, the doc removal did not.
                let scheduler = TerminalScheduler.create doc terminals (Set.singleton "q-a1")
                SyncedStateSync.enqueueTerminalCommand doc (queue "a1") id (PeerRef ada) 1.0 "make"
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
            SyncedStateSync.enqueueTerminalCommand doc (queue "a1") terminalA ActorRef.Agent 3.0 "git status"
            let synced = syncedOf doc
            let entry = synced.Pending |> Map.find (queue "a1")
            Expect.equal entry.Subject (ForTerminal terminalA) "the subject names its terminal"
            Expect.equal entry.Payload CommandLine "and its payload is an editable command line"
            Expect.equal entry.Author ActorRef.Agent "the author, as an actor rather than a peer"
            Expect.equal entry.Order 3.0 "the order"
            Expect.equal entry.ApprovedBy None "and never pre-approved"
            Expect.equal (SyncedStateSync.terminalQueuedText doc (queue "a1")) "git status" "with its command text"

        testCase "an unreadable approval reads as NOT approved" <| fun () ->
            // Fail closed: a value we cannot read must never release an agent's command.
            let doc = Y.Doc.Create ()
            SyncedStateSync.enqueueTerminalCommand doc (queue "a1") terminalA ActorRef.Agent 1.0 "x"
            setQueuedFieldInDoc doc (queue "a1") "approvedBy" "   "
            let synced = syncedOf doc
            Expect.equal (synced.Pending |> Map.find (queue "a1")).ApprovedBy None "blank is not an approval"

        testCase "an unreadable mode reads as the default, not as no gate" <| fun () ->
            let doc = Y.Doc.Create ()
            setModeInDoc doc terminalA "nonsense-mode"
            let synced = syncedOf doc
            Expect.equal (SyncedSessionState.modeOf terminalA synced) ApproveAgent "it falls back to the gate"

        testCase "the composer slot key round-trips both ids" <| fun () ->
            let key = SyncedStateSync.TerminalDraftKey.make terminalA ada
            Expect.equal (SyncedStateSync.TerminalDraftKey.parse key) (Some (terminalA, ada)) "both come back"
            Expect.equal (SyncedStateSync.TerminalDraftKey.parse "no-separator") None "and a malformed key is skipped"

        // --- The widened roots, and the docs written before them (Plan 15, stage 3) --------

        testCase "a gate is stored per subject, for either kind" <| fun () ->
            let doc = Y.Doc.Create ()
            SyncedStateSync.setGate doc (ForTerminal terminalA) AutoRun
            SyncedStateSync.setGate doc (ForCommand "add_repo") ApproveAgent
            let synced = syncedOf doc
            Expect.equal (SyncedSessionState.gateOf (ForTerminal terminalA) synced) AutoRun "the terminal's"
            Expect.equal (SyncedSessionState.gateOf (ForCommand "add_repo") synced) ApproveAgent "and the command's"

        testCase "a doc written before the widening keeps its pending commands and its modes" <| fun () ->
            // The regression this exists to stop is silent and one-directional: a bare
            // rename drops a terminal somebody set to `ApproveAll` back to the default,
            // which is LESS gated than what they asked for.
            let doc = Y.Doc.Create ()
            legacyEnqueueInDoc doc yjsModule (QueueId.value (queue "a1")) (TerminalId.value terminalA) "agent" 3.0
            legacyModeInDoc doc yjsModule (TerminalId.value terminalA) "approve-all"
            Expect.equal (SyncedStateSync.migrateGateRoots doc) (1, 1) "one act and one gate moved"
            let synced = syncedOf doc
            let entry = synced.Pending |> Map.find (queue "a1")
            Expect.equal entry.Subject (ForTerminal terminalA) "the entry names the terminal it named before"
            Expect.equal entry.Payload CommandLine "as the only payload kind that existed then"
            Expect.equal entry.Author ActorRef.Agent "with its author"
            Expect.equal entry.Order 3.0 "and its place in the queue"
            Expect.equal (SyncedSessionState.modeOf terminalA synced) ApproveAll "the stricter mode survives"
            // Exactly one live location afterwards, never two that can disagree.
            Expect.equal (SyncedStateSync.migrateGateRoots doc) (0, 0) "and a migrated doc has nothing left to move"

        testCase "migrating a doc that was never legacy does nothing" <| fun () ->
            let doc = Y.Doc.Create ()
            SyncedStateSync.enqueueTerminalCommand doc (queue "a1") terminalA ActorRef.Agent 1.0 "git status"
            Expect.equal (SyncedStateSync.migrateGateRoots doc) (0, 0) "no legacy roots, no work"
            Expect.equal (Map.count (syncedOf doc).Pending) 1 "and the entry it did have is untouched"
    ]


// --- Foreign sources (Plan 16, part D) ----------------------------------------------------

/// An environment that REFUSES to start, so "did the open ensure the sandbox?" is answerable
/// by whether the open succeeded rather than by counting calls.
let private refusingEnvironment () =
    let environment, spawned = scriptedEnvironment (fun _ -> [], 0)
    { environment with Ensure = fun _ _ -> async { return EnvironmentUnavailable "no sandbox here" } }, spawned

/// A stream that records what was written to it and never ends on its own.
let private loopback () =
    let written = ResizeArray<string> ()
    let attach : AttachTerminal =
        fun _ _ _ onData ->
            async {
                return
                    Ok
                        { Write = fun text -> written.Add text
                          Resize = fun _ _ -> ()
                          Kill = ignore
                          Exited = async { return SandboxExited 0 }
                          }
                    |> Result.map (fun handle ->
                        onData "ready\n"
                        handle)
            }
    attach, written

let private deviceTicket =
    { Url = "ws://127.0.0.1:0/device"
      Capabilities = SourceCapabilities.byteStream
      Label = "USB serial" }

let private sourceTests =
    testList "Foreign terminal sources" [

        // The whole point of a second kind of source: a session that only talks to a serial
        // port should not start a container to do it.
        testCaseAsync "an attached source does NOT ensure the WorkSandbox" <|
            async {
                let log = newLog ()
                let environment, _ = refusingEnvironment ()
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let attach, _ = loopback ()
                let terminals, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! shell = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                Expect.isError shell "a shell terminal IS a need, so a refused sandbox refuses the open"
                let! device = terminals.Open (PeerRef ada) (Attached { Ticket = deviceTicket; Renewable = false }) "USB serial"
                Expect.isOk device "an attached one needs nothing this session runs"
            }

        testCaseAsync "an attached source's bytes reach the transcript" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, linesOf, _, _, readTranscript = recordingTranscripts ()
                let attach, _ = loopback ()
                let terminals, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
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
                let attach, written = loopback ()
                let terminals, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (Attached { Ticket = deviceTicket; Renewable = false }) "USB serial"
                let id = opened |> expect
                do! terminals.RunBlock id (entry "a1" id (PeerRef ada) 1.0 None) "make" ignore
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
                let attach, _ = loopback ()
                let terminals, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
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
                let attach, written = loopback ()
                let terminals, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
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
                let attach, _ = loopback ()
                let terminals, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! device = terminals.Open (PeerRef ada) (Attached { Ticket = deviceTicket; Renewable = false }) "USB serial"

                match! terminals.Tail (expect device) with
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
                let attach, _ = loopback ()
                let terminals, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! device = terminals.Open (PeerRef ada) (Attached { Ticket = deviceTicket; Renewable = false }) "USB serial"
                let id = expect device
                let! closed = terminals.Close id "the device went away"
                Expect.isOk closed "the terminal closes"

                match! terminals.Tail id with
                | Error e -> failwithf "a closed device still has a recording: %s" e
                | Ok tail -> Expect.stringContains tail.Text "ready" "and it still reads"
            }

        testCaseAsync "reading a terminal that runs blocks is refused, and says where the answer is" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let attach, _ = loopback ()
                let terminals, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! shell = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"

                match! terminals.Tail (expect shell) with
                | Ok _ -> failwith "a shell's output is its blocks', and reading it twice is two answers to one question"
                | Error reason -> Expect.stringContains reason "execute_command" "and it says where the answer is"
            }

        testCaseAsync "on an instrumented terminal it is refused, because that is what blocks are for" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _, _, _, readTranscript = recordingTranscripts ()
                let attach, written = loopback ()
                let terminals, _ = makeTerminalsWith attach log environment openTranscript readTranscript []
                let! opened = terminals.Open (PeerRef ada) (SandboxShell SandboxName.defaultName) "build"
                let id = opened |> expect
                match! terminals.Write id ActorRef.Agent "rm -rf /\r" with
                | Ok () -> failwith "raw bytes into a shell would be the door around the approval gate"
                | Error reason -> Expect.stringContains reason "execute_command" "and it says where to go instead"
                Expect.isFalse (written |> Seq.exists (fun w -> w.Contains "rm -rf")) "nothing was typed"
            }
    ]

let tests =
    testList "Terminals (Plan 13)" [
        sourceTests
        approvalTests
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
        transcriptTests
        codecTests
        orderTests
        managerTests
        schedulerTests
        syncTests
    ]
