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
// Approving a command is a CLIENT-side CRDT write (`ApproveTerminalQueuedMsg` through the
// Ylmish binding), so a test driving the Session Process's own doc has to make that write
// the way a peer's merged update would arrive. These are the only Yjs calls in this file,
// and they exist so no production API has to grow a setter that only a test would call.

[<Fable.Core.Emit("(() => { const e = $0.getMap('terminalQueue').get($1); if (e) e.set($2, $3) })()")>]
let private setQueuedField (doc: Y.Doc) (id: string) (field: string) (value: obj) : unit = Fable.Core.Util.jsNative

[<Fable.Core.Emit("(() => { const m = $0.getMap('terminalModes'); const e = new $2.Map(); m.set($1, e); e.set('mode', $3) })()")>]
let private setModeRaw (doc: Y.Doc) (id: string) (yjs: obj) (mode: string) : unit = Fable.Core.Util.jsNative

[<Fable.Core.Import("*", "yjs")>]
let private yjsModule : obj = Fable.Core.Util.jsNative

/// A peer's approval, landing in this doc.
let private approveInDoc (doc: Y.Doc) (id: QueueId) (approver: PeerId) : unit =
    setQueuedField doc (QueueId.value id) "approvedBy" (box (PeerId.value approver))

/// A queue entry field, set to whatever a peer we do not control might have written.
let private setQueuedFieldInDoc (doc: Y.Doc) (id: QueueId) (field: string) (value: string) : unit =
    setQueuedField doc (QueueId.value id) field (box value)

/// A terminal's mode register, set to a raw string.
let private setModeInDoc (doc: Y.Doc) (terminal: TerminalId) (mode: string) : unit =
    setModeRaw doc (TerminalId.value terminal) yjsModule mode

// --- The approval policy -----------------------------------------------------------------

let private approvalTests =
    testList "Approval policy" [
        testCase "the default mode gates the agent and nobody else" <| fun () ->
            Expect.isTrue (TerminalApprovalMode.requiresApproval ApproveAgent ActorRef.Agent) "the agent is gated"
            Expect.isFalse (TerminalApprovalMode.requiresApproval ApproveAgent (PeerRef ada)) "a peer is not"
            Expect.isFalse
                (TerminalApprovalMode.requiresApproval ApproveAgent ActorRef.SessionProcess)
                "the Process's own commands are not gated — a gate there deadlocks on housekeeping"

        testCase "approve-all gates everyone; auto gates nobody" <| fun () ->
            for author in [ ActorRef.Agent; PeerRef ada; ActorRef.SessionProcess ] do
                Expect.isTrue (TerminalApprovalMode.requiresApproval ApproveAll author) "approve-all gates everything"
                Expect.isFalse (TerminalApprovalMode.requiresApproval AutoRun author) "auto gates nothing"

        testCase "an absent mode register reads as the default, never as no gate" <| fun () ->
            // The safe direction: a terminal nobody configured must still hold the agent.
            let mode = SyncedSessionState.modeOf terminalA SyncedSessionState.empty
            Expect.equal mode ApproveAgent "an unconfigured terminal is approve-agent"
    ]

// --- The drain's decision ------------------------------------------------------------------

let private entry (id: string) (terminal: TerminalId) (author: ActorRef) (order: float) (approved: PeerId option) =
    { QueueId = queue id
      Terminal = terminal
      Author = author
      Order = order
      ApprovedBy = approved
      RejectedBy = None
      RejectedReason = None }

/// The same entry, refused. Kept beside `entry` so a test says which of the two verdicts
/// it is exercising rather than threading a `None` through every call that is not about
/// rejection.
let private rejected (e: TerminalQueued) (by: PeerId) (reason: string option) =
    { e with RejectedBy = Some by; RejectedReason = reason }

let private queueOf entries =
    entries |> List.map (fun (e: TerminalQueued) -> e.QueueId, e) |> Map.ofList

let private allOpen (_: TerminalId) = true
let private planWith consumed busy isOpen modeOf entries =
    TerminalQueueDrain.plan consumed busy isOpen modeOf (queueOf entries)

let private drainTests =
    testList "Terminal drain plan" [
        testCase "one command per terminal, and terminals do not block each other" <| fun () ->
            let plan =
                planWith Set.empty Set.empty allOpen (fun _ -> AutoRun)
                    [ entry "a1" terminalA (PeerRef ada) 1.0 None
                      entry "a2" terminalA (PeerRef ada) 2.0 None
                      entry "b1" terminalB (PeerRef bob) 1.0 None ]
            Expect.equal
                (plan.Ready |> List.map (fun e -> QueueId.value e.QueueId))
                [ "q-a1"; "q-b1" ]
                "the head of each terminal runs; the second in a terminal waits its turn"

        testCase "a terminal with a block already running is skipped" <| fun () ->
            let plan =
                planWith Set.empty (Set.singleton (TerminalId.value terminalA)) allOpen (fun _ -> AutoRun)
                    [ entry "a1" terminalA (PeerRef ada) 1.0 None
                      entry "b1" terminalB (PeerRef bob) 1.0 None ]
            Expect.equal
                (plan.Ready |> List.map (fun e -> QueueId.value e.QueueId))
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
            Expect.equal (plan.Ready |> List.map (fun e -> QueueId.value e.QueueId)) [ "q-a1" ] "the approval releases it"

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
                (plan.Ready |> List.map (fun e -> QueueId.value e.QueueId))
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
    SessionEvent.TerminalOpened { TerminalId = id; OpenedBy = PeerRef ada; Title = title }

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
                    (plan.Rejections |> List.map (fun e -> QueueId.value e.QueueId))
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
                      { TerminalId = terminalA; BlockId = None; DroppedBytes = 17 } ]
            for event in events do
                let encoded = Codec.toString Codec.sessionEvent event
                Expect.equal (Codec.fromString Codec.sessionEvent encoded) (Ok event) ("round-trips: " + encoded)

        testCase "terminal frames round-trip over the session transport" <| fun () ->
            let codec = Codec.sessionFrame Codec.string
            let frames =
                [ Terminal (TerminalRecord (terminalA, 7, { At = 1.0; Kind = TranscriptOutput; Data = "hi" }))
                  Terminal (TerminalTranscriptAvailable (terminalA, 42))
                  // The screen a joining peer renders, and the seq it composes with.
                  Terminal (TerminalSnapshot (terminalA, 42, "screen")) ]
            for frame in frames do
                let encoded = Codec.toString codec frame
                Expect.equal (Codec.fromString codec encoded) (Ok frame) ("round-trips: " + encoded)

        testCase "the terminal commands and focus fields round-trip" <| fun () ->
            let codec = Codec.sessionFrame Codec.string
            let frames =
                [ Command (Request (RequestId.fresh (), OpenTerminal "build"))
                  Command (Request (RequestId.fresh (), CloseTerminal terminalA))
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
          Execute = fun _ _ -> async { return CommandExecutionFailed "not used here" }
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

/// An in-memory transcript, and a reader for what it holds.
let private recordingTranscripts () =
    let lines = Collections.Generic.Dictionary<string, ResizeArray<TranscriptLine>> ()
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
              NextSeq = fun () -> held.Count }
    openTranscript, (fun (id: TerminalId) -> linesFor id |> List.ofSeq)

let private mintFrom (ids: string list) =
    let remaining = ResizeArray<string> ids
    fun () ->
        let next = remaining.[0]
        if remaining.Count > 1 then remaining.RemoveAt 0
        next

let private makeTerminals (log: EventLog<SessionEvent>) environment openTranscript openAtBoot =
    let mintTerminal = mintFrom [ "term-a"; "term-b" ]
    let mintBlock = mintFrom [ "b-1"; "b-2"; "b-3" ]
    let records = ResizeArray<TerminalId * int * TranscriptRecord> ()
    let terminals =
        SessionTerminals.create
            log
            environment
            openTranscript
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
            openAtBoot
    terminals, records

let private managerTests =
    testList "Terminal manager" [
        testCaseAsync "opening a terminal ensures the environment and records the fact" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _ = recordingTranscripts ()
                let terminals, _ = makeTerminals log environment openTranscript []
                let! opened = terminals.Open (PeerRef ada) "build"
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
                let openTranscript, readTranscript = recordingTranscripts ()
                let terminals, records = makeTerminals log environment openTranscript []
                let! opened = terminals.Open (PeerRef ada) "build"
                let id = opened |> expect
                let entry = entry "a1" id (PeerRef ada) 1.0 None
                let mutable startedCalled = 0
                do! terminals.RunBlock entry "echo hello" (fun () -> startedCalled <- startedCalled + 1)

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
                let transcript = readTranscript id
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
                let openTranscript, _ = recordingTranscripts ()
                let terminals, _ = makeTerminals log environment openTranscript []
                let! opened = terminals.Open (PeerRef ada) "build"
                let id = opened |> expect
                do! terminals.RunBlock (entry "a1" id (PeerRef ada) 1.0 None) "cat missing" ignore
                let! events = eventsOf log
                let completedEvent =
                    events |> List.pick (function SessionEvent.TerminalBlockCompleted e -> Some e | _ -> None)
                Expect.equal completedEvent.Result (CommandFailed 2) "the exit code is the result"
            }

        testCaseAsync "an approval is recorded on the block, so the audit says who let it run" <|
            async {
                let log = newLog ()
                let environment, _ = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _ = recordingTranscripts ()
                let terminals, _ = makeTerminals log environment openTranscript []
                let! opened = terminals.Open (PeerRef ada) "build"
                let id = opened |> expect
                do! terminals.RunBlock (entry "a1" id ActorRef.Agent 1.0 (Some bob)) "rm -rf build" ignore
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
                let openTranscript, _ = recordingTranscripts ()
                let terminals, _ = makeTerminals log environment openTranscript []
                let! opened = terminals.Open (PeerRef ada) "build"
                let id = opened |> expect
                do! terminals.RunBlock (entry "a1" id (PeerRef ada) 1.0 None) "yes" ignore
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
                let openTranscript, _ = recordingTranscripts ()
                let terminals, _ = makeTerminals log environment openTranscript [ terminalA; terminalB ]
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
                let openTranscript, _ = recordingTranscripts ()
                let terminals, _ = makeTerminals log environment openTranscript []
                let! opened = terminals.Open (PeerRef ada) "build"
                let id = opened |> expect
                let! _ = terminals.Close id "closed by a peer"
                do! terminals.RunBlock (entry "a1" id (PeerRef ada) 1.0 None) "make" ignore
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
                let openTranscript, _ = recordingTranscripts ()
                let terminals, _ = makeTerminals log environment openTranscript []
                let! opened = terminals.Open (PeerRef ada) "build"
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
                Expect.isTrue (Map.isEmpty synced.TerminalQueue) "and its entry left the doc once consumed"
            }

        testCaseAsync "the agent's command waits for a human under the default mode" <|
            async {
                let log = newLog ()
                let environment, spawned = scriptedEnvironment (fun _ -> [], 0)
                let openTranscript, _ = recordingTranscripts ()
                let terminals, _ = makeTerminals log environment openTranscript []
                let! opened = terminals.Open (PeerRef ada) "build"
                let id = opened |> expect
                let doc = Y.Doc.Create ()
                let scheduler = TerminalScheduler.create doc terminals Set.empty
                SyncedStateSync.enqueueTerminalCommand doc (queue "a1") id ActorRef.Agent 1.0 "rm -rf /"
                scheduler.Drain ()
                do! Async.Sleep 20
                Expect.isEmpty (List.ofSeq spawned) "nothing ran"
                let synced = syncedOf doc
                Expect.equal (Map.count synced.TerminalQueue) 1 "the command is still queued, visible and editable"

                // A peer approves it — a plain CRDT write, which is the entire mechanism.
                let entry = synced.TerminalQueue |> Map.toList |> List.head |> snd
                let approved =
                    ClientModel.update
                        (ApproveTerminalQueuedMsg (entry.QueueId, bob))
                        { ClientModel.init { PeerId = bob; DisplayName = "Bob" } with Synced = synced }
                let approvedEntry = approved.Synced.TerminalQueue |> Map.find entry.QueueId
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
                let openTranscript, _ = recordingTranscripts ()
                let terminals, _ = makeTerminals log environment openTranscript []
                let! opened = terminals.Open (PeerRef ada) "build"
                let id = opened |> expect
                let doc = Y.Doc.Create ()
                // The crash window: a block start reached the log, the doc removal did not.
                let scheduler = TerminalScheduler.create doc terminals (Set.singleton "q-a1")
                SyncedStateSync.enqueueTerminalCommand doc (queue "a1") id (PeerRef ada) 1.0 "make"
                scheduler.Drain ()
                do! Async.Sleep 20
                Expect.isEmpty (List.ofSeq spawned) "it does not run a second time"
                let synced = syncedOf doc
                Expect.isTrue (Map.isEmpty synced.TerminalQueue) "and the leftover is cleaned out of the doc"
            }
    ]

// --- The synced state's codec ----------------------------------------------------------------

let private syncTests =
    testList "Terminal collaborative state" [
        testCase "a terminal queue entry survives a doc round-trip" <| fun () ->
            let doc = Y.Doc.Create ()
            SyncedStateSync.enqueueTerminalCommand doc (queue "a1") terminalA ActorRef.Agent 3.0 "git status"
            let synced = syncedOf doc
            let entry = synced.TerminalQueue |> Map.find (queue "a1")
            Expect.equal entry.Terminal terminalA "the terminal"
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
            Expect.equal (synced.TerminalQueue |> Map.find (queue "a1")).ApprovedBy None "blank is not an approval"

        testCase "an unreadable mode reads as the default, not as no gate" <| fun () ->
            let doc = Y.Doc.Create ()
            setModeInDoc doc terminalA "nonsense-mode"
            let synced = syncedOf doc
            Expect.equal (SyncedSessionState.modeOf terminalA synced) ApproveAgent "it falls back to the gate"

        testCase "the composer slot key round-trips both ids" <| fun () ->
            let key = SyncedStateSync.TerminalDraftKey.make terminalA ada
            Expect.equal (SyncedStateSync.TerminalDraftKey.parse key) (Some (terminalA, ada)) "both come back"
            Expect.equal (SyncedStateSync.TerminalDraftKey.parse "no-separator") None "and a malformed key is skipped"
    ]


let tests =
    testList "Terminals (Plan 13)" [
        approvalTests
        drainTests
        projectionTests
        markTests
        emulatorTests
        rejectionTests
        digestTests
        ansiTests
        transcriptTests
        codecTests
        orderTests
        managerTests
        schedulerTests
        syncTests
    ]
