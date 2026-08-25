module Yession.Tests.Timeline

// The chat as a PERSON reads it (Plan 14, stage 1): what was said and what was run, merged
// into one order. Cheap tier throughout — the whole thing is a pure fold over envelopes and
// a sort, so nothing here needs a port, a process, or a browser.

open System
open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Tools
open Yession.Domain.Terminals
open Yession.Domain.Collab
open Yession.Domain.Chat
open Yession.App

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

let private sessionId = SessionId.create "timeline-tests" |> expect
let private terminalA = TerminalId.create "term-a" |> expect
let private terminalB = TerminalId.create "term-b" |> expect
let private ada = PeerId.create "ada" |> expect
let private bob = PeerId.create "bob" |> expect

let private block (n: string) = BlockId.create ("b-" + n) |> expect
let private message (n: string) = MessageId.create ("m-" + n) |> expect

let private epoch = DateTimeOffset (2026, 8, 8, 0, 0, 0, TimeSpan.Zero)

/// One envelope at `offset`, stamped `seconds` after the epoch. The timestamp matters: a
/// stretch's item says how long the holder had the terminal, and only the envelopes can
/// answer that — the transcript's clock is per-terminal.
let private at (offset: int64) (seconds: float) (event: SessionEvent) : EventEnvelope<SessionEvent> =
    { EventId = EventId.fresh ()
      SessionId = sessionId
      Offset = EventOffset.create offset |> expect
      Actor = ActorRef.SessionProcess
      Timestamp = epoch.AddSeconds seconds
      Event = event }

/// The two projections the timeline merges, folded from one page — exactly as a client does.
let private merge (events: EventEnvelope<SessionEvent> list) : TimelineItem list =
    let conversation, _ = ConversationProjection.applyEvents None events ConversationProjection.empty
    let timeline, _ = TimelineProjection.applyEvents None events TimelineProjection.empty
    TimelineProjection.items conversation timeline

let private openedBy (by: ActorRef) (id: TerminalId) (title: string) =
    SessionEvent.TerminalOpened { TerminalId = id; OpenedBy = by; Title = title; Sandbox = Some SandboxRef.defaultRef; Renewable = false }

let private opened (id: TerminalId) (title: string) = openedBy (PeerRef ada) id title

let private sent (n: string) (body: string) =
    MessageSent { MessageId = message n; QueueId = None; Author = PeerRef ada; Body = body }

let private started (id: TerminalId) (n: string) (author: ActorRef) (command: string) (fromSeq: int) =
    SessionEvent.TerminalBlockStarted
        { TerminalId = id
          BlockId = block n
          QueueId = None
          Authority = Authority.ofAuthor author
          Command = command
          FromSeq = fromSeq
          Background = false }

let private completed (id: TerminalId) (n: string) (result: CommandResult) (toSeq: int) =
    SessionEvent.TerminalBlockCompleted { TerminalId = id; BlockId = block n; Result = result; ToSeq = toSeq }

let private took (id: TerminalId) (by: ActorRef) (fromSeq: int) =
    SessionEvent.TerminalLeaseTaken { TerminalId = id; By = by; FromSeq = fromSeq }

let private released (id: TerminalId) (was: ActorRef) (reason: TerminalLeaseEnd) (toSeq: int) =
    SessionEvent.TerminalLeaseReleased { TerminalId = id; Was = was; Reason = reason; ToSeq = toSeq }

/// What each item IS, for an ordering assertion that does not have to spell out a record.
let private shapes (items: TimelineItem list) : string list =
    items
    |> List.map (function
        | TimelineMessage item -> "said:" + MessageId.value item.MessageId
        | TimelineBlock (_, _, blockId) -> "ran:" + BlockId.value blockId
        | TimelineStretch stretch -> "held:" + TerminalId.value stretch.TerminalId
        | TimelineToolUse (_, id) -> "used:" + ToolUseId.value id)

let private stretchesOf (items: TimelineItem list) : TerminalStretch list =
    items |> List.choose (function TimelineStretch s -> Some s | _ -> None)

// --- Ordering -------------------------------------------------------------------------------

let private orderTests =
    testList "The merged order" [
        testCase "a block that starts before a message and finishes after it sits BEFORE it" <| fun () ->
            // The whole point of anchoring a chip at its start: a four-minute build's result
            // lands above the messages sent while it ran, rather than jumping to the bottom
            // when it happens to finish. Appearing only on completion would make long work
            // invisible while it is the only thing happening.
            let items =
                merge
                    [ at 1L 0.0 (opened terminalA "build")
                      at 2L 1.0 (started terminalA "1" (PeerRef ada) "make" 1)
                      at 3L 2.0 (sent "1" "how's it going?")
                      at 4L 3.0 (completed terminalA "1" (CommandSucceeded 0) 40) ]
            Expect.equal (shapes items) [ "ran:b-1"; "said:m-1" ] "the chip holds the place it started at"

        testCase "everything is ordered by ONE key: the offset it was anchored at" <| fun () ->
            let items =
                merge
                    [ at 1L 0.0 (opened terminalA "build")
                      at 2L 0.0 (sent "1" "first")
                      at 3L 1.0 (started terminalA "1" ActorRef.Agent "ls" 1)
                      at 4L 2.0 (took terminalA (PeerRef bob) 5)
                      at 5L 9.0 (released terminalA (PeerRef bob) LeaseReleased 30)
                      at 6L 10.0 (sent "2" "done?") ]
            Expect.equal
                (shapes items)
                [ "said:m-1"; "ran:b-1"; "held:term-a"; "said:m-2" ]
                "said, ran, held, said — in log order"

        testCase "re-applying an overlapping page adds nothing twice" <| fun () ->
            // The same offset gate the other projections carry, for the same reason: pages
            // overlap, and a chip that appeared twice would be a bug a reload could not fix.
            let page =
                [ at 1L 0.0 (opened terminalA "build")
                  at 2L 1.0 (started terminalA "1" (PeerRef ada) "make" 1) ]
            let first, highWater = TimelineProjection.applyEvents None page TimelineProjection.empty
            let second, _ = TimelineProjection.applyEvents highWater page first
            Expect.equal (List.length second.TerminalItems) 1 "one chip, however many times the page arrives"
    ]

// --- Chips ------------------------------------------------------------------------------------

let private chipTests =
    testList "Block chips" [
        testCase "a chip carries NO status of its own — it is the block's, read live" <| fun () ->
            // What makes a chip mutate in place for free: the timeline holds where it goes,
            // `Projection` holds what it currently says. A chip that copied the
            // status in would need its own update path, and would be free to disagree.
            let running =
                [ at 1L 0.0 (opened terminalA "build")
                  at 2L 1.0 (started terminalA "1" (PeerRef ada) "make" 1) ]
            let finished = running @ [ at 3L 9.0 (completed terminalA "1" (CommandFailed 2) 40) ]
            let itemsOf events = (TimelineProjection.applyEvents None events TimelineProjection.empty |> fst).TerminalItems
            Expect.equal (itemsOf running) (itemsOf finished) "the timeline entry does not move or change"
            let statusOf events =
                let proj = events |> List.fold (fun p (e: EventEnvelope<SessionEvent>) -> Projection.applyEvent p e.Event) Projection.empty
                Projection.tryFind terminalA proj
                |> Option.bind (fun t -> t.Blocks |> List.tryFind (fun b -> b.BlockId = block "1"))
                |> Option.map (fun b -> b.Status)
            Expect.equal (statusOf running) (Some BlockRunning) "running, at first"
            Expect.equal (statusOf finished) (Some (BlockFinished (CommandFailed 2))) "and the exit code afterwards"

        testCase "a REJECTED command gets a chip too" <| fun () ->
            // "The agent proposed this and a human said no" is the more interesting half of
            // the two, and a refusal that appears nowhere is indistinguishable from a bug.
            let items =
                merge
                    [ at 1L 0.0 (opened terminalA "build")
                      at 2L 1.0 (
                          SessionEvent.TerminalCommandRejected
                              { TerminalId = terminalA
                                QueueId = QueueId.create "q-1" |> expect
                                BlockId = block "no"
                                Author = ActorRef.Agent
                                RejectedBy = PeerRef ada
                                Command = "rm -rf /"
                                Reason = Some "no" }) ]
            Expect.equal (shapes items) [ "ran:b-no" ] "the refusal is in the chat where it was proposed"
    ]

// --- Stretches ---------------------------------------------------------------------------------

let private stretchTests =
    testList "Lease stretches" [
        testCase "a stretch appears when it CONCLUDED, not when it began" <| fun () ->
            // The difference between a stretch and a chip, and the one the user asked for: a
            // long interactive session is a thing you read about afterwards.
            let open' = [ at 1L 0.0 (opened terminalA "shell"); at 2L 1.0 (took terminalA (PeerRef bob) 5) ]
            Expect.isEmpty (shapes (merge open')) "a lease still held is not an item yet"
            let ended = open' @ [ at 3L 61.0 (released terminalA (PeerRef bob) LeaseReleased 300) ]
            Expect.equal (shapes (merge ended)) [ "held:term-a" ] "and one when it ends"

        testCase "the item says who, how long, where, and how it ended" <| fun () ->
            let items =
                merge
                    [ at 1L 0.0 (opened terminalA "shell")
                      at 2L 10.0 (took terminalA (PeerRef bob) 5)
                      at 3L 130.0 (released terminalA (PeerRef bob) LeaseReleased 300) ]
            match stretchesOf items with
            | [ stretch ] ->
                Expect.equal stretch.Holder (PeerRef bob) "who held it"
                Expect.equal stretch.Title "shell" "named by the terminal's title, not its id"
                Expect.equal (TerminalStretch.duration stretch) (TimeSpan.FromSeconds 120.0) "for two minutes"
                Expect.equal stretch.End LeaseReleased "and handed back"
                Expect.equal stretch.Range (Some (5, 300)) "with the range its replay needs"
            | other -> failwithf "expected exactly one stretch, got %d" (List.length other)

        testCase "each of the four endings is its own answer" <| fun () ->
            // "Did nick finish, get taken over, drop out, or just wander off?" has four
            // different answers, and collapsing any two would say someone decided something
            // they did not.
            let endings =
                [ LeaseReleased; LeaseStolen (PeerRef ada); LeaseHolderGone; LeaseIdle ]
                |> List.map (fun ending ->
                    merge
                        [ at 1L 0.0 (opened terminalA "shell")
                          at 2L 1.0 (took terminalA (PeerRef bob) 5)
                          at 3L 9.0 (released terminalA (PeerRef bob) ending 30) ]
                    |> stretchesOf
                    |> List.map (fun s -> s.End))
            Expect.equal
                endings
                [ [ LeaseReleased ]; [ LeaseStolen (PeerRef ada) ]; [ LeaseHolderGone ]; [ LeaseIdle ] ]
                "each ending survives to the item that reports it"

        testCase "a steal closes one stretch and opens the next, abutting exactly" <| fun () ->
            // The Process writes both at ONE transcript position, so the two ranges meet with
            // no overlap and no gap — a replay of either shows only that holder's bytes.
            let items =
                merge
                    [ at 1L 0.0 (opened terminalA "shell")
                      at 2L 1.0 (took terminalA (PeerRef ada) 5)
                      at 3L 20.0 (released terminalA (PeerRef ada) (LeaseStolen (PeerRef bob)) 40)
                      at 4L 20.0 (took terminalA (PeerRef bob) 40)
                      at 5L 50.0 (released terminalA (PeerRef bob) LeaseReleased 90) ]
            Expect.equal
                (stretchesOf items |> List.map (fun s -> s.Holder, s.Range))
                [ PeerRef ada, Some (5, 40); PeerRef bob, Some (40, 90) ]
                "ada's ends where bob's begins"

        testCase "a release naming someone who no longer holds it closes nothing" <| fun () ->
            // The same staleness guard the terminal fold applies: a steal is two events, and
            // acting on the release out of order would close the stretch the take just opened.
            let items =
                merge
                    [ at 1L 0.0 (opened terminalA "shell")
                      at 2L 1.0 (took terminalA (PeerRef bob) 5)
                      at 3L 9.0 (released terminalA (PeerRef ada) (LeaseStolen (PeerRef bob)) 30) ]
            Expect.isEmpty (shapes items) "bob still holds it, so there is no stretch to show"

        testCase "closing a terminal under a live holder still yields a stretch" <| fun () ->
            // `TerminalClosed` clears the lease WITHOUT a release event — that is the
            // Process's rule — so without this the stretch would never appear at all.
            let items =
                merge
                    [ at 1L 0.0 (opened terminalA "shell")
                      at 2L 1.0 (took terminalA (PeerRef bob) 5)
                      at 3L 9.0 (SessionEvent.TerminalClosed { TerminalId = terminalA; Reason = "closed by a peer" }) ]
            match stretchesOf items with
            | [ stretch ] ->
                Expect.equal stretch.End LeaseHolderGone "nobody decided anything; the terminal went away"
                Expect.equal stretch.Range None "and no end was recorded, so there is nothing to replay"
            | other -> failwithf "expected exactly one stretch, got %d" (List.length other)

        testCase "a stretch with no recorded range has NOTHING to replay, not the whole file" <| fun () ->
            // What a log written before Plan 14 decodes to. `[0, 0)` and a range that happens
            // to be empty mean the same thing to a reader; a default that guessed a real
            // range instead would replay the wrong bytes and look right.
            let items =
                merge
                    [ at 1L 0.0 (opened terminalA "shell")
                      at 2L 1.0 (took terminalA (PeerRef bob) 0)
                      at 3L 9.0 (released terminalA (PeerRef bob) LeaseReleased 0) ]
            Expect.equal (stretchesOf items |> List.map (fun s -> s.Range)) [ None ] "no range, rather than [0, ∞)"

        testCase "two stretches on one terminal have distinct handles" <| fun () ->
            // Leases are not minted with ids, so a tab keyed on the terminal alone could not
            // tell this morning's `vim` session from this afternoon's.
            let items =
                merge
                    [ at 1L 0.0 (opened terminalA "shell")
                      at 2L 1.0 (took terminalA (PeerRef bob) 5)
                      at 3L 9.0 (released terminalA (PeerRef bob) LeaseReleased 30)
                      at 4L 20.0 (took terminalA (PeerRef bob) 30)
                      at 5L 30.0 (released terminalA (PeerRef bob) LeaseReleased 60) ]
            let keys = stretchesOf items |> List.map TerminalStretch.key
            Expect.equal (List.length (List.distinct keys)) 2 "two stretches, two keys"

        testCase "leases on different terminals do not close each other" <| fun () ->
            let items =
                merge
                    [ at 1L 0.0 (opened terminalA "shell")
                      at 2L 0.0 (opened terminalB "logs")
                      at 3L 1.0 (took terminalA (PeerRef ada) 5)
                      at 4L 2.0 (took terminalB (PeerRef bob) 7)
                      at 5L 9.0 (released terminalA (PeerRef ada) LeaseReleased 30) ]
            Expect.equal (shapes items) [ "held:term-a" ] "only the one that ended"
    ]

// --- What did NOT change ------------------------------------------------------------------------

let private unchangedTests =
    testList "The agent's conversation is untouched" [
        testCase "terminal events still contribute NO conversation items" <| fun () ->
            // The load-bearing property of this whole stage. `ConversationProjection` is what
            // builds the agent's context, and the agent already receives block outcomes
            // through `Digest` — folding terminal events in here would double-feed
            // the model and silently change what every turn reads.
            let terminalEvents =
                [ at 1L 0.0 (opened terminalA "build")
                  at 2L 1.0 (started terminalA "1" ActorRef.Agent "ls" 1)
                  at 3L 2.0 (completed terminalA "1" (CommandSucceeded 0) 9)
                  at 4L 3.0 (took terminalA (PeerRef bob) 9)
                  at 5L 4.0 (released terminalA (PeerRef bob) LeaseReleased 20)
                  at 6L 5.0 (SessionEvent.TerminalClosed { TerminalId = terminalA; Reason = "done" }) ]
            let said = [ at 7L 6.0 (sent "1" "ship it") ]
            let withTerminals, _ = ConversationProjection.applyEvents None (terminalEvents @ said) ConversationProjection.empty
            let without, _ = ConversationProjection.applyEvents None said ConversationProjection.empty
            Expect.equal
                (withTerminals.Items |> List.map (fun i -> i.MessageId, i.Author, i.Body, i.Status))
                (without.Items |> List.map (fun i -> i.MessageId, i.Author, i.Body, i.Status))
                "the same items, in the same order, whatever the terminals did"

        testCase "an item's offset is where it was CREATED, and streaming does not move it" <| fun () ->
            // Deltas and completions move the body and the status; they never move the item.
            // A streaming answer holds its place exactly as a running command's chip does.
            let turnId = AgentTurnId.create "turn-1" |> expect
            let messageId = message "agent"
            let events =
                [ at 1L 0.0 (AgentTurnStarted { AgentTurnId = turnId; TriggeredByMessageId = Some (message "1"); Woke = None })
                  at 2L 1.0 (AgentMessageStarted { AgentTurnId = turnId; MessageId = messageId })
                  at 3L 2.0 (AgentMessageDelta { AgentTurnId = turnId; MessageId = messageId; Delta = "hel" })
                  at 4L 3.0 (AgentMessageCompleted { AgentTurnId = turnId; MessageId = messageId; Body = "hello" }) ]
            let proj, _ = ConversationProjection.applyEvents None events ConversationProjection.empty
            match proj.Items with
            | [ item ] ->
                Expect.equal (EventOffset.value item.Offset) 2L "anchored where the message started"
                Expect.equal item.Body "hello" "even though the body arrived later"
            | other -> failwithf "expected one item, got %d" (List.length other)
    ]

// --- The pane's tabs (stage 2) -------------------------------------------------------------

/// A client that has folded these events — the real path a browser takes, so the tab tests
/// run against the model a session actually produces rather than a hand-built one.
let private clientOf (events: EventEnvelope<SessionEvent> list) : ClientModel =
    ClientModel.update
        (EventsPageMsg { Events = events; LastOffset = events |> List.tryLast |> Option.map (fun e -> e.Offset); IsEnd = true })
        (ClientModel.init { PeerId = ada; DisplayName = "swift-heron" })

let private oneBlock =
    [ at 1L 0.0 (opened terminalA "build")
      at 2L 1.0 (started terminalA "1" (PeerRef ada) "ls -la" 1)
      at 3L 2.0 (completed terminalA "1" (CommandSucceeded 0) 3) ]

let private stripKeys (model: ClientModel) = ClientModel.paneTabs model |> List.map PaneTab.key

let private paneTests =
    testList "The pane's tabs (Plan 14, stage 2)" [
        testCase "opening a tab shows it, and opens the column it is in" <| fun () ->
            let model = clientOf oneBlock
            Expect.isFalse model.TerminalsOpen "the column starts shut"
            let opened' = ClientModel.update (ShowInPaneMsg (Reading (BlockTab (terminalA, block "1")))) model
            Expect.equal
                (ClientModel.selectedPane opened' |> Option.map PaneTab.key)
                (Some "block:term-a:b-1")
                "the tab that was just opened is the one showing"
            Expect.isTrue opened'.TerminalsOpen "and the column came with it"

        testCase "a block tab and its terminal have DIFFERENT keys drawn from the same ids" <| fun () ->
            // The reason a tab's key is prefixed per kind: a block id and a terminal id come
            // from the same alphabet, and a collision would silently select the wrong tab.
            let sameName = TerminalId.create "xy" |> expect
            let asBlock = BlockId.create "xy" |> expect
            Expect.notEqual
                (PaneTab.key (TerminalTab sameName))
                (PaneTab.key (BlockTab (sameName, asBlock)))
                "one name, two tabs"

        testCase "every tab is about a terminal, whichever kind it is" <| fun () ->
            // What the composer, the presence marks and the transcript reads are keyed by.
            let stretch =
                { Offset = EventOffset.create 9L |> expect
                  TerminalId = terminalB
                  Title = "shell"
                  Holder = PeerRef bob
                  End = LeaseReleased
                  Range = Some (1, 9)
                  StartedAt = epoch
                  EndedAt = epoch.AddMinutes 1.0 }
            Expect.equal (PaneTab.terminal (TerminalTab terminalA)) terminalA "a terminal's own"
            Expect.equal (PaneTab.terminal (BlockTab (terminalA, block "1"))) terminalA "a block's"
            Expect.equal (PaneTab.terminal (StretchTab stretch)) terminalB "a stretch's"

        testCase "a block tab renders the command and its output, read-only" <| fun () ->
            // Stage 2's deliverable: from the chunks the client already has, through the very
            // renderer the terminal's own history uses — a block read from the chat must not
            // be a second rendering free to drift from the first.
            let model =
                clientOf oneBlock
                |> ClientModel.update (TerminalRecordMsg (terminalA, 1, { At = 0.0; Kind = TranscriptOutput; Data = "total 0\n" }))
                |> ClientModel.update (ShowInPaneMsg (Reading (BlockTab (terminalA, block "1"))))
            let html = Support.render model
            let required =
                [ "the tab", Dom.attr Dom.Hooks.paneTab "block:term-a:b-1"
                  // Previewed, so it says it is keepable and not yet kept. The pin itself is
                  // a mark that appears only once it is.
                  "it says it is not kept", Dom.attr Dom.Hooks.paneTabPinned "false"
                  "the panel showing it", Dom.attr Dom.Hooks.panePanel "block:term-a:b-1"
                  "the block's read-only view", Dom.attr Dom.Hooks.paneBlock "b-1"
                  "the command", "ls -la"
                  // What it printed, as TEXT — the cheap read of the same bytes, through the
                  // same renderer the terminal's own history uses. Whether the OTHER read is
                  // offered is a different question, and `readsTests` is where it is asked.
                  "what it printed", "total 0" ]
            for label, marker in required do
                Expect.isTrue (html.Contains marker) (sprintf "%s (`%s`) must render" label marker)
            // Read-only: no composer for a block you are reading back.
            Expect.isFalse
                (html.Contains (Dom.attr Dom.Hooks.terminalInput (BodyKey.terminalDraft terminalA ada)))
                "no command line in a block's view"

        testCase "the strip is one tablist, and every tab in it is a real tab" <| fun () ->
            let model =
                clientOf oneBlock |> ClientModel.update (ShowInPaneMsg (Reading (BlockTab (terminalA, block "1"))))
            let html = Support.render model
            Expect.isTrue (html.Contains "role=\"tablist\"") "one tablist"
            // Roving tabindex, ARIA's manual-activation variant: exactly one tab is a Tab
            // stop, and the arrow keys move between them without mounting a panel per keypress.
            let selectedStops =
                html.Split ([| "role=\"tab\"" |], System.StringSplitOptions.None)
                |> Array.skip 1
                |> Array.filter (fun after -> (after.Split '>').[0].Contains "tabindex=\"0\"")
            Expect.equal (Array.length selectedStops) 1 "exactly one tab is a Tab stop"
            Expect.isTrue (html.Contains "aria-selected=\"true\"") "and it is the selected one"
    ]

// --- Keyframes and the ranged cast (stage 3) --------------------------------------------------

/// The output records of a `.cast`, in order — what a player would feed the emulator.
let private outputsOf (cast: string) : string list =
    cast.Split '\n'
    |> Array.filter (fun l -> l.Trim().Length > 0)
    |> Array.toList
    |> List.choose (fun line ->
        match Codec.fromString Codec.transcriptLine line with
        | Ok (TranscriptRecordLine r) when r.Kind = TranscriptOutput || r.Kind = TranscriptStderr -> Some r.Data
        | _ -> None)

let private timesOf (cast: string) : float list =
    cast.Split '\n'
    |> Array.filter (fun l -> l.Trim().Length > 0)
    |> Array.toList
    |> List.choose (fun line ->
        match Codec.fromString Codec.transcriptLine line with
        | Ok (TranscriptRecordLine r) -> Some r.At
        | _ -> None)

let private headerOf (cast: string) : TranscriptHeader =
    match Codec.fromString Codec.transcriptLine ((cast.Split '\n').[0]) with
    | Ok (TranscriptHeaderLine h) -> h
    | _ -> failwith "the first line of a cast is its header"

/// Feed an emulator and read the screen back.
let private screenOf (cols: int) (rows: int) (chunks: string list) : Async<string> =
    async {
        let emulator = Yession.Host.Emulator.openEmulator cols rows
        for chunk in chunks do emulator.Write chunk
        let! screen = emulator.Serialize ()
        emulator.Dispose ()
        return screen
    }

let private baseHeader : TranscriptHeader = { Width = 80; Height = 24; Timestamp = 0L }

let private keyframeTests =
    testList "Keyframes and the ranged cast (Plan 14, stage 3)" [
        testCase "a ranged cast REBASES its times to the range's first record" <| fun () ->
            // asciicast times are relative to the start of the FILE. Slice a block that ran
            // forty minutes in and the player sits idle for forty minutes before the first
            // frame — broken in a way that looks exactly like a hang.
            let records =
                [ 1, { At = 0.5; Kind = TranscriptOutput; Data = "early\r\n" }
                  2, { At = 2400.0; Kind = TranscriptOutput; Data = "the block\r\n" }
                  3, { At = 2400.25; Kind = TranscriptOutput; Data = "more\r\n" } ]
            let cast = TranscriptReplay.range baseHeader None 2 4 records
            Expect.equal (timesOf cast) [ 0.0; 0.25 ] "the range starts at zero and keeps its own spacing"
            Expect.equal (outputsOf cast) [ "the block\r\n"; "more\r\n" ] "and holds only the range"

        testCase "a keyframe paints the screen FIRST, and overrides the header's geometry" <| fun () ->
            // The header records the size the terminal OPENED at; a resize before the range
            // changed it, and a recording replayed under the wrong geometry rewraps every
            // line in it.
            let keyframe = { Seq = 2; Cols = 120; Rows = 40; Screen = "PAINTED" }
            let records = [ 2, { At = 9.0; Kind = TranscriptOutput; Data = "after\r\n" } ]
            let cast = TranscriptReplay.range baseHeader (Some keyframe) 2 3 records
            Expect.equal (outputsOf cast) [ "PAINTED"; "after\r\n" ] "the screen, then the range"
            Expect.equal (timesOf cast) [ 0.0; 0.0 ] "both at zero: the paint is instantaneous"
            let header = headerOf cast
            Expect.equal (header.Width, header.Height) (120, 40) "the size the range actually ran at"

        testCase "an EMPTY keyframe screen paints nothing rather than an empty frame" <| fun () ->
            // What a degraded terminal's serializer returns. A zero-length output record is
            // a frame in the recording that the terminal never printed.
            let keyframe = { Seq = 2; Cols = 80; Rows = 24; Screen = "" }
            let cast = TranscriptReplay.range baseHeader (Some keyframe) 2 3 [ 2, { At = 1.0; Kind = TranscriptOutput; Data = "x" } ]
            Expect.equal (outputsOf cast) [ "x" ] "just the range"

        testCase "an empty range is still a VALID cast — a header and no frames" <| fun () ->
            // What a rejected command carries, and what a stretch with no recorded bounds
            // resolves to. An empty file is one the player reports as broken.
            let cast = TranscriptReplay.range baseHeader None 0 0 []
            Expect.equal ((cast.Split '\n' |> Array.filter (fun l -> l.Trim() <> "")).Length) 1 "the header, alone"

        testCaseAsync "the keyframe is what makes a ranged replay CORRECT, not merely faster" <|
            async {
                // The assertion the naive slice fails. The prefix sets colour and moves the
                // cursor; the range then prints under that state. Replayed into a fresh VT
                // the slice is *approximately* right — and wrong exactly where the screen
                // carried state in, which for an audit trail is the whole point.
                let prefix = [ "\u001b[31mred prefix\r\n"; "\u001b[44m"; "\u001b[10;5H" ]
                let ranged = [ "printed under that state\r\n" ]

                // The truth: one emulator fed the whole stream, exactly as the Session
                // Process's own emulator was.
                let! truth = screenOf 80 24 (prefix @ ranged)
                // The keyframe: the same serializer, at the range's start.
                let! keyScreen = screenOf 80 24 prefix

                let records =
                    ranged |> List.mapi (fun i data -> 4 + i, { At = 60.0 + float i; Kind = TranscriptOutput; Data = data })
                let withKey =
                    TranscriptReplay.range baseHeader (Some { Seq = 4; Cols = 80; Rows = 24; Screen = keyScreen }) 4 9 records
                let without = TranscriptReplay.range baseHeader None 4 9 records

                let! replayed = screenOf 80 24 (outputsOf withKey)
                let! naive = screenOf 80 24 (outputsOf without)

                // Asserted non-empty first, because the interesting way for this to fail is
                // to pass: comparing one blank screen to another proves nothing.
                Expect.isTrue (truth.Contains "printed under that state") "the screen was actually drawn"
                Expect.equal replayed truth "the ranged replay reproduces the screen the emulator had"
                Expect.notEqual naive truth "and the naive slice does not — which is why keyframes exist"
            }

        testCase "the keyframe a range wants is the one at its FIRST line" <| fun () ->
            // Selection, stated as the property the writer relies on: keyframes are written
            // at range STARTS and nowhere else, so a range whose `from` has no keyframe has
            // no keyframe at all — there is nothing else to fall back to.
            let model =
                clientOf oneBlock
                |> ClientModel.update (TerminalHeaderMsg (terminalA, baseHeader))
                |> ClientModel.update (TerminalRecordMsg (terminalA, 1, { At = 3.0; Kind = TranscriptOutput; Data = "out\r\n" }))
                |> ClientModel.update (TerminalKeyframeMsg (terminalA, { Seq = 1; Cols = 80; Rows = 24; Screen = "SCREEN" }))
            match ClientModel.rangedCast terminalA 1 2 model with
            | Some cast -> Expect.equal (outputsOf cast) [ "SCREEN"; "out\r\n" ] "painted from the keyframe at line 1"
            | None -> failwith "the header is known, so there is a cast"
            // A different range on the same terminal has no keyframe of its own, and plays
            // without one rather than refusing.
            match ClientModel.rangedCast terminalA 0 2 model with
            | Some cast -> Expect.equal (outputsOf cast) [ "out\r\n" ] "no paint, just the range"
            | None -> failwith "a missing keyframe is not a missing cast"

        testCase "no header means no cast: a guessed geometry rewraps every line" <| fun () ->
            let model = clientOf oneBlock
            Expect.isNone (ClientModel.rangedCast terminalA 0 2 model) "nothing to render until line 0 arrives"
    ]

// --- The video item (stage 4) -----------------------------------------------------------------

/// A closed terminal with two blocks and the records they produced, which is what a
/// whole-terminal recording is made of.
let private recordedTerminal =
    [ at 1L 0.0 (opened terminalA "build")
      at 2L 1.0 (started terminalA "1" (PeerRef ada) "make" 1)
      at 3L 2.0 (completed terminalA "1" (CommandSucceeded 0) 3)
      at 4L 3.0 (started terminalA "2" (PeerRef ada) "make test" 3)
      at 5L 4.0 (completed terminalA "2" (CommandFailed 1) 5)
      at 6L 5.0 (SessionEvent.TerminalClosed { TerminalId = terminalA; Reason = "closed by a peer" }) ]

let private withRecords (model: ClientModel) =
    [ 1, { At = 10.0; Kind = TranscriptOutput; Data = "building\r\n" }
      2, { At = 11.0; Kind = TranscriptOutput; Data = "done\r\n" }
      3, { At = 40.0; Kind = TranscriptOutput; Data = "testing\r\n" }
      4, { At = 43.5; Kind = TranscriptOutput; Data = "FAILED\r\n" } ]
    |> List.fold (fun m (seq, record) -> ClientModel.update (TerminalRecordMsg (terminalA, seq, record)) m) model
    |> ClientModel.update (TerminalHeaderMsg (terminalA, baseHeader))

let private videoTests =
    testList "The video item (Plan 14, stage 4)" [
        testCase "a whole recording is chaptered by the commands that ran in it" <| fun () ->
            // Chapters ride IN the cast as `"m"` events (Plan 25, stage 1), which is what puts
            // them on the same idle-compressed clock the player runs the records on. As the
            // player's own option they stayed on the raw clock and landed in the dead air the
            // compression had just removed.
            let model = withRecords (clientOf recordedTerminal)
            match ClientModel.paneReplay (TerminalTab terminalA) model with
            | Some replay ->
                Expect.stringContains replay.Cast "[10,\"m\",\"make\"]" "a chapter at the first block's first line"
                Expect.stringContains replay.Cast "[40,\"m\",\"make test\"]" "and one at the second's"
                Expect.isNone replay.StartAt "and it starts at the start until somebody asks for a command"
            | None -> failwith "the header is known, so there is a recording"

        // Position and fidelity are two axes, and the toggle only ever moves ONE of them
        // (Plan 25, stage 3). These pin that, because it is the whole reason the reader
        // cannot lose their place any more.
        testCase "the toggle swaps the read and leaves the position alone" <| fun () ->
            let tab = TerminalTab terminalA
            Expect.equal (TabMode.toggled (Reading tab)) (Watching tab) "text to recording"
            Expect.equal (TabMode.toggled (Watching tab)) (Reading tab) "and back"

        testCase "a read positioned at a command watches from that command, and back" <| fun () ->
            // The round trip the old step-out could not make: it replaced the block tab, so
            // there was nothing to come back to. Here the position is the same fact on both
            // sides of the flip.
            let anchored = ReadingAt (terminalA, block "2")
            Expect.equal (TabMode.toggled anchored) (WatchingFrom (terminalA, block "2")) "watching from where they were"
            Expect.equal (TabMode.toggled (WatchingFrom (terminalA, block "2"))) anchored "and back to the same command"

        testCase "coming back to live drops the pin that only watching had" <| fun () ->
            // A pin is a fact about watching from behind an edge. Carried into a read it
            // would be a rewind nothing is showing.
            Expect.equal
                (TabMode.toggled (WatchingBehind (terminalA, 7)))
                (Reading (TerminalTab terminalA))
                "the live text, with no pin left over"

        testCase "a watch entered from a command starts at that command" <| fun () ->
            // The anchor IS the start position: nothing rides a message, and the line is
            // resolved against the blocks the projection actually has.
            let model =
                withRecords (clientOf recordedTerminal)
                |> ClientModel.update (ShowInPaneMsg (ReadingAt (terminalA, block "2")))
            Expect.equal (ClientModel.paneAnchor model) (Some (terminalA, block "2")) "positioned at the command"
            let watching = ClientModel.update (ShowInPaneMsg (TabMode.toggled (ReadingAt (terminalA, block "2")))) model
            match ClientModel.paneReplay (TerminalTab terminalA) watching with
            | Some replay -> Expect.equal replay.StartAt (Some 40.0) "and the recording starts where it did"
            | None -> failwith "the header is known, so there is a recording"

        testCase "a chapter is written before the record it names" <| fun () ->
            // The order the player's own multiplex picks, and the one a reader means: a
            // chapter names the command whose first byte follows it, never the silence before.
            let model = withRecords (clientOf recordedTerminal)
            match ClientModel.paneReplay (TerminalTab terminalA) model with
            | Some replay ->
                let marker = replay.Cast.IndexOf "[10,\"m\",\"make\"]"
                let record = replay.Cast.IndexOf "building"
                Expect.isTrue (marker >= 0 && marker < record) "the chapter line comes first"
            | None -> failwith "the header is known, so there is a recording"

        testCase "'play whole terminal' lands on the block it stepped out from" <| fun () ->
            // The two paths answer different questions: the slice is "what did this command
            // print", the whole is "what was going on around it".
            let model =
                withRecords (clientOf recordedTerminal)
                |> ClientModel.update (ShowInPaneMsg (WatchingFrom (terminalA, block "2")))
            Expect.equal
                (ClientModel.selectedPane model |> Option.map PaneTab.key)
                (Some "terminal:term-a")
                "the pane moved to the terminal's own recording"
            match ClientModel.paneReplay (TerminalTab terminalA) model with
            | Some replay -> Expect.equal replay.StartAt (Some 40.0) "starting where the second block did"
            | None -> failwith "the header is known, so there is a recording"

        testCase "a start hint belongs to the step-out that set it, and dies with it" <| fun () ->
            let model =
                withRecords (clientOf recordedTerminal)
                |> ClientModel.update (ShowInPaneMsg (WatchingFrom (terminalA, block "2")))
                |> ClientModel.update (ShowInPaneMsg (Reading (TerminalTab terminalA)))
            match ClientModel.paneReplay (TerminalTab terminalA) model with
            | Some replay -> Expect.isNone replay.StartAt "choosing the tab again starts it from the start"
            | None -> failwith "the header is known, so there is a recording"

        testCase "a stretch's item carries a poster: the still of its final screen" <| fun () ->
            // It costs nothing extra — the player builds the still by replaying to that point
            // internally — and the time is in the RANGE's own clock, because the cast it is
            // shown over has been rebased.
            let stretch =
                { Offset = EventOffset.create 9L |> expect
                  TerminalId = terminalA
                  Title = "build"
                  Holder = PeerRef bob
                  End = LeaseReleased
                  Range = Some (1, 4)
                  StartedAt = epoch
                  EndedAt = epoch.AddMinutes 1.0 }
            let model = withRecords (clientOf recordedTerminal)
            match ClientModel.paneReplay (StretchTab stretch) model with
            | Some replay ->
                // Nudged past the record rather than landing on it: the player feeds events
                // while `time < poster`, so asking for exactly 30.0 would show the screen as
                // it stood BEFORE the frame this still exists to show.
                Expect.equal replay.Poster (Some 30.001) "the last frame of the range, from its own zero"
                Expect.isTrue (replay.Cast.Contains "testing") "and the recording is the range"
            | None -> failwith "the header is known, so there is a recording"

        testCase "a stretch with no recorded range has no player at all" <| fun () ->
            // An empty player is indistinguishable from a quiet session, and the item says
            // which one it is instead.
            let stretch =
                { Offset = EventOffset.create 9L |> expect
                  TerminalId = terminalA
                  Title = "build"
                  Holder = PeerRef bob
                  End = LeaseHolderGone
                  Range = None
                  StartedAt = epoch
                  EndedAt = epoch.AddMinutes 1.0 }
            Expect.isNone
                (ClientModel.paneReplay (StretchTab stretch) (withRecords (clientOf recordedTerminal)))
                "nothing to play"

        testCase "a RUNNING block has no range yet, so nothing is mounted over it" <| fun () ->
            // Its recording grows on every record, and a player rebuilt on each one would
            // thrash through a streaming build. The terminal's own tab is where you watch it.
            let running =
                [ at 1L 0.0 (opened terminalA "build")
                  at 2L 1.0 (started terminalA "1" (PeerRef ada) "make" 1) ]
            let model = withRecords (clientOf running)
            Expect.isNone (ClientModel.paneReplay (BlockTab (terminalA, block "1")) model) "not yet"
            let finished = withRecords (clientOf (running @ [ at 3L 9.0 (completed terminalA "1" (CommandSucceeded 0) 3) ]))
            Expect.isSome (ClientModel.paneReplay (BlockTab (terminalA, block "1")) finished) "and now"

        testCase "a refused command is reported, never played" <| fun () ->
            let rejected =
                [ at 1L 0.0 (opened terminalA "build")
                  at 2L 1.0 (
                      SessionEvent.TerminalCommandRejected
                          { TerminalId = terminalA
                            QueueId = QueueId.create "q-1" |> expect
                            BlockId = block "no"
                            Author = ActorRef.Agent
                            RejectedBy = PeerRef ada
                            Command = "rm -rf /"
                            Reason = Some "no" }) ]
            let model = withRecords (clientOf rejected)
            Expect.isNone (ClientModel.paneReplay (BlockTab (terminalA, block "no")) model) "it never ran"
            Expect.isNone (ClientModel.missingKeyframe (BlockTab (terminalA, block "no")) model) "so there is no screen to fetch"
            let html = Support.render (ClientModel.update (ShowInPaneMsg (Reading (BlockTab (terminalA, block "no")))) model)
            // By NAME — `ada` is this fixture's local peer, so the refuser is called what
            // every other surface calls them rather than by the id underneath.
            //
            // The phrase is carried deliberately: `swift-heron` alone also appears in the
            // roster, so an assertion decoupled from the copy passes even when this pane
            // prints the bare id. Coupling to "rejected by" is what SCOPES it to the refusal.
            Expect.isTrue (html.Contains "rejected by swift-heron") "the tab says who refused it"
            Expect.isFalse (html.Contains (Dom.attr Dom.Hooks.paneReplay "block:term-a:b-no")) "and mounts no player"

        testCase "a block offers the way to its command in the terminal's own history" <| fun () ->
            // The reader's other question — what was going on around this — is about POSITION,
            // and its answer is more of the same text. Offered wherever there is a history to
            // be positioned in, open or closed: the question is as real on a running terminal.
            let closed =
                withRecords (clientOf recordedTerminal)
                |> ClientModel.update (ShowInPaneMsg (Reading (BlockTab (terminalA, block "1"))))
            Expect.isTrue
                ((Support.render closed).Contains (Dom.attr Dom.Hooks.paneShowInTerminal "b-1"))
                "a closed terminal's block can be shown where it ran"
            let stillOpen =
                withRecords (clientOf (recordedTerminal |> List.filter (fun e -> match e.Event with SessionEvent.TerminalClosed _ -> false | _ -> true)))
                |> ClientModel.update (ShowInPaneMsg (Reading (BlockTab (terminalA, block "1"))))
            Expect.isTrue
                ((Support.render stillOpen).Contains (Dom.attr Dom.Hooks.paneShowInTerminal "b-1"))
                "and so can a running one's"

        testCase "the keyframe a tab needs is asked for exactly once, and only when it can help" <| fun () ->
            let model = withRecords (clientOf recordedTerminal)
            Expect.equal
                (ClientModel.missingKeyframe (BlockTab (terminalA, block "2")) model)
                (Some (terminalA, 3))
                "the block's first line"
            let fetched = ClientModel.update (TerminalKeyframeMsg (terminalA, { Seq = 3; Cols = 80; Rows = 24; Screen = "S" })) model
            Expect.isNone (ClientModel.missingKeyframe (BlockTab (terminalA, block "2")) fetched) "and not again"
            Expect.isNone
                (ClientModel.missingKeyframe (TerminalTab terminalA) model)
                "a whole recording starts at the start; its header is its keyframe"
    ]

// --- The DVR (stage 7) -------------------------------------------------------------------------

// --- The two reads of one history -------------------------------------------------------------

/// A terminal that was only ever typed in: somebody took the lease, bytes were recorded, and
/// it closed without a command ever resolving into a block. What a device attached over a
/// stream that cannot be instrumented also looks like from here.
let private liveOnlyTerminal =
    [ at 1L 0.0 (opened terminalA "shell")
      at 2L 1.0 (took terminalA (PeerRef bob) 1)
      at 3L 5.0 (SessionEvent.TerminalClosed { TerminalId = terminalA; Reason = "closed by a peer" }) ]

let private readsTests =
    testList "Which read a surface shows" [

        // Text and recording are two reads of the same bytes, and the rule is the same on
        // every surface that has both: the text is the read, the recording is somewhere you
        // go. Each of these is a BICONDITIONAL — the swap happening when it is asked for, and
        // not happening when it is not — because a predicate that answered `true` always
        // would satisfy either half alone.

        testCase "a closed terminal that ran commands reads as its commands" <| fun () ->
            // What the player under the blocks was: a recording of the same two lines the
            // block above it had already printed.
            let model = withRecords (clientOf recordedTerminal)
            Expect.isFalse (ClientModel.playsRecording (TerminalTab terminalA) model) "the blocks are the read"

        testCase "asking for the recording swaps the read" <| fun () ->
            let model =
                withRecords (clientOf recordedTerminal)
                |> ClientModel.update (ShowInPaneMsg (Watching (TerminalTab terminalA)))
            Expect.isTrue (ClientModel.playsRecording (TerminalTab terminalA) model) "now it plays"
            Expect.isFalse
                (ClientModel.playsRecording (TerminalTab terminalB) model)
                "and only the terminal that was asked for"

        testCase "a closed terminal with nothing but a recording plays without being asked" <| fun () ->
            // There is no cheaper read to default to: an empty block list is not a read, it
            // is a `$`. Making a reader press play to see the only thing there is would be a
            // control whose answer is never no.
            let model = withRecords (clientOf liveOnlyTerminal)
            Expect.isTrue (ClientModel.playsRecording (TerminalTab terminalA) model) "the recording IS the surface"

        testCase "the way back is offered only where there is something behind the player" <| fun () ->
            let played =
                withRecords (clientOf recordedTerminal)
                |> ClientModel.update (ShowInPaneMsg (Watching (TerminalTab terminalA)))
            Expect.isTrue
                ((Support.render played).Contains (Dom.attr Dom.Hooks.terminalWatch "output"))
                "text to go back to"
            Expect.isFalse
                ((Support.render (withRecords (clientOf liveOnlyTerminal))).Contains (Dom.attr Dom.Hooks.terminalWatch "output"))
                "and none where the recording is the only read — that control undoes itself"

        testCase "a rewind that outlives its live edge is still a reader watching a recording" <| fun () ->
            // The pin dies with the live edge; the watching does not. Dropping a reader back
            // into the blocks because the terminal they were watching finished would answer
            // a question they never asked.
            let live = recordedTerminal |> List.filter (fun e -> match e.Event with SessionEvent.TerminalClosed _ -> false | _ -> true)
            let model =
                withRecords (clientOf live)
                |> ClientModel.update (RewindTerminalMsg terminalA)
                |> ClientModel.update
                    (EventsPageMsg
                        { Events = [ at 6L 61.0 (SessionEvent.TerminalClosed { TerminalId = terminalA; Reason = "done" }) ]
                          LastOffset = Some (EventOffset.create 6L |> expect)
                          IsEnd = true })
            Expect.isFalse (ClientModel.isRewound terminalA model) "no live edge, no rewind"
            Expect.isTrue (ClientModel.playsRecording (TerminalTab terminalA) model) "and still the recording"

        testCase "a recording the cap ate is never offered" <| fun () ->
            // The stated gap. A control that opens an empty player is indistinguishable from
            // a terminal that printed nothing, which is the fact the drop is recorded to say.
            let model = clientOf recordedTerminal
            Expect.isFalse (ClientModel.playable (TerminalTab terminalA) model) "nothing kept, nothing to play"
            Expect.isFalse ((Support.render model).Contains Dom.Hooks.terminalWatch) "so nothing offers it"

        testCase "a block's output is text until somebody asks for the recording" <| fun () ->
            // The case that made the rule: a command and its result, printed, needed no
            // player of the same two lines under it.
            let model =
                withRecords (clientOf recordedTerminal)
                |> ClientModel.update (ShowInPaneMsg (Reading (BlockTab (terminalA, block "1"))))
            Expect.isFalse
                ((Support.render model).Contains (Dom.attr Dom.Hooks.paneReplay "block:term-a:b-1"))
                "read as text"
            let played = ClientModel.update (ShowInPaneMsg (Watching (BlockTab (terminalA, block "1")))) model
            Expect.isTrue
                ((Support.render played).Contains (Dom.attr Dom.Hooks.paneReplay "block:term-a:b-1"))
                "and played when asked"
    ]

let private dvrTests =
    testList "Rewinding a live terminal (Plan 14, stage 7)" [
        testCase "rewinding plays what has been recorded SO FAR, and pins that length" <| fun () ->
            // A recording that grew under a reader would move the scrub bar out from under
            // them, which is the one thing rewinding exists to avoid. The terminal keeps
            // running and its records keep arriving — that is what makes this a DVR rather
            // than a replay of something finished.
            let live =
                [ at 1L 0.0 (opened terminalA "shell")
                  at 2L 1.0 (took terminalA (PeerRef bob) 1) ]
            let model = withRecords (clientOf live) |> ClientModel.update (RewindTerminalMsg terminalA)
            Expect.isTrue (ClientModel.isRewound terminalA model) "the pane is behind live"
            let castAt (m: ClientModel) =
                match ClientModel.paneReplay (TerminalTab terminalA) m with
                | Some replay -> outputsOf replay.Cast
                | None -> failwith "the header is known, so there is a recording"
            Expect.equal
                (castAt model)
                [ "building\r\n"; "done\r\n"; "testing\r\n"; "FAILED\r\n" ]
                "everything recorded when the rewind began"
            // The terminal keeps printing. What is being watched does not move.
            let stillGrowing =
                ClientModel.update
                    (TerminalRecordMsg (terminalA, 5, { At = 60.0; Kind = TranscriptOutput; Data = "after\r\n" }))
                    model
            Expect.equal (castAt stillGrowing) (castAt model) "the recording under the reader is unchanged"

        testCase "rewinding lands AT the pinned edge, not at the recording's start" <| fun () ->
            // "Rewind" on an hour-old terminal must not mean "restart from the beginning".
            // Like live TV it lands on the moment the reader left — the still of the pinned
            // screen, visually the live screen they were just watching — and the scrub bar
            // is how they go back from there.
            let before = withRecords (clientOf [ at 1L 0.0 (opened terminalA "shell"); at 2L 1.0 (took terminalA (PeerRef bob) 1) ])
            (match ClientModel.paneReplay (TerminalTab terminalA) before with
             | Some replay -> Expect.isNone replay.BehindLive "an un-rewound cast's end really is the end"
             | None -> failwith "the header is known, so there is a recording")
            match ClientModel.paneReplay (TerminalTab terminalA) (ClientModel.update (RewindTerminalMsg terminalA) before) with
            | Some replay ->
                Expect.equal replay.StartAt (Some 43.5) "starts at the last pinned record's time"
                // Nudged past that record: the still is the screen the reader was just
                // watching, and a poster landing ON the pinned time paints the one before it.
                Expect.equal replay.Poster (Some 43.501) "whose frame is the still shown before play"
                Expect.equal replay.BehindLive (Some terminalA) "and playing off this end means the reader caught up"
            | None -> failwith "the header is known, so there is a recording"

        testCase "the surface says how far behind the reader is, and it grows" <| fun () ->
            let model =
                withRecords (clientOf [ at 1L 0.0 (opened terminalA "shell"); at 2L 1.0 (took terminalA (PeerRef bob) 1) ])
                |> ClientModel.update (RewindTerminalMsg terminalA)
            Expect.equal (ClientModel.behindLive terminalA model) (Some 0.0) "nothing has accrued yet"
            let grown =
                ClientModel.update
                    (TerminalRecordMsg (terminalA, 5, { At = 103.5; Kind = TranscriptOutput; Data = "after\r\n" }))
                    model
            Expect.equal (ClientModel.behindLive terminalA grown) (Some 60.0) "a minute of recording arrived behind the pin"
            Expect.isTrue ((Support.render grown).Contains "behind live — 1m 0s") "and the pane says so"

        testCase "a live terminal with NOTHING recorded offers no rewind" <| fun () ->
            // A DVR with nothing behind it is a control with nothing to do.
            let bare = clientOf [ at 1L 0.0 (opened terminalA "shell") ]
            Expect.isFalse ((Support.render bare).Contains Dom.Hooks.terminalWatch) "no recording, no control"

        testCase "a terminal that CLOSES under a rewound reader is simply its recording again" <| fun () ->
            // The pin outlived its live edge, so it is no rewind any more. Left unresolved
            // this rendered TWO players over one recording — the rewound region and the
            // closed-terminal replay — with the final output missing from both and no
            // "jump to live" to escape by.
            let model =
                withRecords (clientOf [ at 1L 0.0 (opened terminalA "shell"); at 2L 1.0 (took terminalA (PeerRef bob) 1) ])
                |> ClientModel.update (RewindTerminalMsg terminalA)
                |> ClientModel.update
                    (TerminalRecordMsg (terminalA, 5, { At = 60.0; Kind = TranscriptOutput; Data = "after\r\n" }))
                |> ClientModel.update
                    (EventsPageMsg
                        { Events = [ at 3L 61.0 (SessionEvent.TerminalClosed { TerminalId = terminalA; Reason = "done" }) ]
                          LastOffset = Some (EventOffset.create 3L |> expect)
                          IsEnd = true })
            Expect.isFalse (ClientModel.isRewound terminalA model) "no live edge, no rewind"
            (match ClientModel.paneReplay (TerminalTab terminalA) model with
             | Some replay ->
                 Expect.isTrue ((outputsOf replay.Cast) |> List.contains "after\r\n") "the recording is whole again, pin ignored"
                 Expect.isNone replay.BehindLive "and its end really is the end"
             | None -> failwith "the header is known, so there is a recording")
            let html = Support.render model
            let mountAttr = Dom.attr Dom.Hooks.paneReplay "terminal:term-a"
            Expect.equal
                ((html.Length - html.Replace(mountAttr, "").Length) / mountAttr.Length)
                1
                "ONE player over the recording, not two"
            Expect.isFalse (html.Contains (Dom.attr Dom.Hooks.terminalWatch "live")) "and no way-back-to-live for a terminal with no live"

        testCase "jumping to live drops the rewind, and the newest bytes are back" <| fun () ->
            let live = [ at 1L 0.0 (opened terminalA "shell"); at 2L 1.0 (took terminalA (PeerRef bob) 1) ]
            let model =
                withRecords (clientOf live)
                |> ClientModel.update (RewindTerminalMsg terminalA)
                |> ClientModel.update
                    (TerminalRecordMsg (terminalA, 5, { At = 60.0; Kind = TranscriptOutput; Data = "after\r\n" }))
                |> ClientModel.update (ShowInPaneMsg (Reading (TerminalTab terminalA)))
            Expect.isFalse (ClientModel.isRewound terminalA model) "caught back up"
            match ClientModel.paneReplay (TerminalTab terminalA) model with
            | Some replay -> Expect.isTrue ((outputsOf replay.Cast) |> List.contains "after\r\n") "including what arrived while behind"
            | None -> failwith "the header is known, so there is a recording"

        testCase "choosing anything else in the pane ends the rewind" <| fun () ->
            // A pane that was still behind live because of a rewind somebody started ten
            // minutes ago would be a surprise with no cause on screen.
            let live = [ at 1L 0.0 (opened terminalA "shell"); at 2L 1.0 (took terminalA (PeerRef bob) 1) ]
            let model =
                withRecords (clientOf live)
                |> ClientModel.update (RewindTerminalMsg terminalA)
                |> ClientModel.update (ShowInPaneMsg (Reading (TerminalTab terminalA)))
            Expect.isFalse (ClientModel.isRewound terminalA model) "the rewind went with the choice"

        testCase "rewind is offered on ANY live terminal, and the screen gives way to it" <| fun () ->
            // The mechanism does not care which MODE the terminal is in: a running build and
            // a `vim` session are one growing byte stream, and a rule that offered this for
            // one and not the other would be a special case to explain rather than a feature.
            let inBlockMode = withRecords (clientOf [ at 1L 0.0 (opened terminalA "shell") ])
            Expect.isTrue
                ((Support.render inBlockMode).Contains (Dom.attr Dom.Hooks.terminalWatch "watch"))
                "a terminal in block mode is rewindable"
            let inLiveMode =
                withRecords (clientOf [ at 1L 0.0 (opened terminalA "shell"); at 2L 1.0 (took terminalA (PeerRef ada) 1) ])
            Expect.isTrue
                ((Support.render inLiveMode).Contains (Dom.attr Dom.Hooks.terminalWatch "watch"))
                "and so is one in live mode"
            let rewound = ClientModel.update (RewindTerminalMsg terminalA) inLiveMode
            let html = Support.render rewound
            Expect.isTrue (html.Contains (Dom.attr Dom.Hooks.terminalWatch "live")) "the way back to the edge"
            Expect.isTrue
                (html.Contains (Dom.attr Dom.Hooks.paneReplay "terminal:term-a"))
                "the recording mounts through the same player a finished terminal uses"
            Expect.isFalse
                (html.Contains (Dom.attr Dom.Hooks.terminalScreen "term-a"))
                "and the live screen gives way to it while you are behind"

        testCase "a CLOSED terminal is not rewindable — it is simply a recording" <| fun () ->
            let closed =
                withRecords
                    (clientOf
                        [ at 1L 0.0 (opened terminalA "shell")
                          at 2L 1.0 (SessionEvent.TerminalClosed { TerminalId = terminalA; Reason = "done" }) ])
            Expect.isFalse
                ((Support.render closed).Contains (Dom.attr Dom.Hooks.terminalWatch "live"))
                "there is no live edge to be behind"
    ]

// --- Tool use (Plan 16, part C) ---------------------------------------------------------

let private toolUse (n: string) = ToolUseId.create ("t-" + n) |> expect
let private turn (n: string) = AgentTurnId.create ("turn-" + n) |> expect

let private used (n: string) (t: string) (name: string) =
    SessionEvent.ToolUseStarted
        { ToolUseId = toolUse n
          AgentTurnId = turn t
          Namespace = "yession"
          Name = name
          Arguments = Some "{}" }

let private toolDone (n: string) (outcome: ToolOutcome) (blk: BlockId option) =
    SessionEvent.ToolUseFinished { ToolUseId = toolUse n; Outcome = outcome; Block = blk }

let private drawn (events: EventEnvelope<SessionEvent> list) : string list =
    let conversation, _ = ConversationProjection.applyEvents None events ConversationProjection.empty
    let timeline, _ = TimelineProjection.applyEvents None events TimelineProjection.empty
    TimelineProjection.rows conversation timeline
    |> List.map (function
        | RowItem item -> List.head (shapes [ item ])
        | RowToolRun (t, items) -> sprintf "run:%s:%d" (AgentTurnId.value t) (List.length items)
        | RowTaskCard (t, items) -> sprintf "card:%s:%d" (AgentTurnId.value t) (List.length items))

let private toolTests =
    testList "Tool use in the chat" [
        testCase "a call anchors where it STARTED, like a block and unlike a stretch" <| fun () ->
            // Same reason: a four-minute call must be visible while it is the only thing
            // happening, rather than appearing from nowhere when it finishes.
            let items =
                merge
                    [ at 1L 0.0 (used "1" "a" "repo_status")
                      at 2L 1.0 (sent "1" "any luck?")
                      at 3L 2.0 (toolDone "1" ToolCallOk None) ]
            Expect.equal (shapes items) [ "used:t-1"; "said:m-1" ] "the item holds the place it started at"

        testCase "the outcome moves what it SAYS, not where it sits" <| fun () ->
            // Carried by id, resolved against the projection — the same property that makes a
            // block chip mutate in place for free.
            let events =
                [ at 1L 0.0 (used "1" "a" "repo_status")
                  at 2L 1.0 (toolDone "1" (ToolCallFailed "no such tool") None) ]
            let running, _ = TimelineProjection.applyEvents None [ List.head events ] TimelineProjection.empty
            let finished, _ = TimelineProjection.applyEvents None events TimelineProjection.empty
            Expect.equal running.TerminalItems finished.TerminalItems "the entry does not move"
            Expect.equal
                (TimelineProjection.toolUse (toolUse "1") running |> Option.bind (fun u -> u.Outcome))
                None
                "a running call says it is running"
            Expect.equal
                (TimelineProjection.toolUse (toolUse "1") finished |> Option.bind (fun u -> u.Outcome))
                (Some (ToolCallFailed "no such tool"))
                "and the finish is what changes it"

        testCase "a call that became a block draws no second chip" <| fun () ->
            // The block chip already says who ran what and how it went. Two renderings of one
            // fact are free to disagree; the RECORD still exists, it just does not draw twice.
            let events =
                [ at 1L 0.0 (opened terminalA "agent")
                  at 2L 1.0 (used "1" "a" "execute_command")
                  at 3L 2.0 (started terminalA "1" ActorRef.Agent "make" 1)
                  at 4L 3.0 (toolDone "1" ToolCallOk (Some (block "1"))) ]
            Expect.equal (drawn events) [ "ran:b-1" ] "only the block's chip is drawn"
            let timeline, _ = TimelineProjection.applyEvents None events TimelineProjection.empty
            Expect.isTrue
                (Map.containsKey (ToolUseId.value (toolUse "1")) timeline.ToolUses)
                "the audit record is still there — the audit wants every call"

        testCase "consecutive calls from one turn collapse into a single row" <| fun () ->
            // Tool use is the first item a SINGLE turn can emit a dozen of, so a chatty turn
            // costs one line rather than twenty.
            let events =
                [ at 1L 0.0 (used "1" "a" "repo_status")
                  at 2L 1.0 (used "2" "a" "repo_log")
                  at 3L 2.0 (used "3" "a" "repo_diff") ]
            Expect.equal (drawn events) [ "run:turn-a:3" ] "three calls, one row"

        testCase "…but only CONSECUTIVE ones, and only within one turn" <| fun () ->
            // A message between two calls means the turn said something in the middle, and
            // hiding that inside one line would tell a reader the wrong story about the order.
            let events =
                [ at 1L 0.0 (used "1" "a" "repo_status")
                  at 2L 1.0 (sent "1" "hold on")
                  at 3L 2.0 (used "2" "a" "repo_log")
                  at 4L 3.0 (used "3" "b" "repo_diff") ]
            Expect.equal
                (drawn events)
                [ "run:turn-a:1"; "said:m-1"; "run:turn-a:1"; "run:turn-b:1" ]
                "the message splits the run, and a new turn starts another"

        testCase "an id minted for a call is a handle a link can carry" <| fun () ->
            // Why it is MINTED rather than derived: a fact that will be addressed must not be
            // identified by a rule that lives nowhere in the data (Plan 13, stage 2a).
            let timeline, _ =
                TimelineProjection.applyEvents None [ at 1L 0.0 (used "1" "a" "set_secret") ] TimelineProjection.empty
            match timeline.TerminalItems with
            | [ TimelineToolUse (_, id) ] ->
                Expect.equal (ToolUseId.value id) "t-1" "the item carries the minted id, not its position"
                Expect.equal
                    (TimelineProjection.toolUse id timeline |> Option.map ToolUse.label)
                    (Some "yession/set_secret")
                    "and it resolves to the call it names"
            | other -> failwithf "expected one tool-use item, got %A" other
    ]

// --- The terminal list (Plan 20, stage 0) --------------------------------------------------

let private closedNow (id: TerminalId) =
    SessionEvent.TerminalClosed { TerminalId = id; Reason = "closed by a peer" }

let private listTests =
    testList "The terminal list (Plan 20, stage 0)" [

        testCase "the open terminals lead, in the order the strip shows them" <| fun () ->
            // The list's open half and the strip are the same terminals, and two surfaces
            // listing them in two orders is a difference a reader has to hold in their head.
            let model = clientOf [ at 1L 0.0 (opened terminalA "build"); at 2L 1.0 (opened terminalB "logs") ]
            Expect.equal
                (ClientModel.terminalRows model |> List.map (fun t -> TerminalId.value t.TerminalId))
                [ "term-a"; "term-b" ]
                "open order, exactly as the strip"

        testCase "closed terminals follow the open ones, most recently opened first" <| fun () ->
            // The closed half is history, and history reads newest first — the one place the
            // list deliberately disagrees with the strip's order, because it is answering a
            // different question.
            let model =
                clientOf
                    [ at 1L 0.0 (opened terminalA "build")
                      at 2L 1.0 (opened terminalB "logs")
                      at 3L 2.0 (closedNow terminalA)
                      at 4L 3.0 (closedNow terminalB) ]
            Expect.equal
                (ClientModel.terminalRows model |> List.map (fun t -> TerminalId.value t.TerminalId))
                [ "term-b"; "term-a" ]
                "the newest recording first"

        testCase "a terminal is recorded for this reader whichever way its transcript arrived" <| fun () ->
            // A LIVE terminal's length arrives as a catch-up hint before any chunk is
            // fetched; a CLOSED one's records arrive as chunks with no live hint behind
            // them. Asking only one of the two would refuse the verb the other one earns.
            let model = clientOf [ at 1L 0.0 (opened terminalA "build") ]
            Expect.isFalse (ClientModel.hasRecording terminalA model) "nothing has arrived yet"
            let byHint = ClientModel.update (TerminalAvailableMsg (terminalA, 12)) model
            Expect.isTrue (ClientModel.hasRecording terminalA byHint) "a live terminal's length"
            let byRecord =
                ClientModel.update
                    (TerminalRecordMsg (terminalA, 0, { At = 0.0; Kind = TranscriptOutput; Data = "hi" }))
                    model
            Expect.isTrue (ClientModel.hasRecording terminalA byRecord) "a fetched record"

        testCase "choosing a row shows that terminal and leaves the list" <| fun () ->
            // One act, not two: a row that selected a terminal and left the reader in the
            // census would have them press twice for one intention.
            let model =
                clientOf [ at 1L 0.0 (opened terminalA "build"); at 2L 1.0 (opened terminalB "logs") ]
                |> ClientModel.update ToggleTerminalListMsg
                |> ClientModel.update (ShowInPaneMsg (Reading (TerminalTab terminalB)))
            Expect.isFalse (ClientModel.showsList model) "the list stepped aside"
            Expect.equal
                (ClientModel.selectedPane model |> Option.map PaneTab.key)
                (Some "terminal:term-b")
                "showing what was chosen"

        // The four cases below are the tombstones of the states four agreeing fields allowed
        // (Plan 25, stage 2). Each was a real defect, watched happening in a browser; each is
        // now unwritable rather than merely unwritten.
        testCase "a chip tapped over the list shows the block it names" <| fun () ->
            // It used to retitle the pane and show the census: opening a tab cleared the
            // playing and rewound fields and left the list flag alone, so the reader tapped a
            // command and got nothing. A face cannot survive the choice that replaces it.
            let model =
                clientOf [ at 1L 0.0 (opened terminalA "build")
                           at 2L 1.0 (started terminalA "1" (PeerRef ada) "make" 1)
                           at 3L 2.0 (completed terminalA "1" (CommandSucceeded 0) 3) ]
                |> ClientModel.update ToggleTerminalListMsg
                |> ClientModel.update (ShowInPaneMsg (Reading (BlockTab (terminalA, block "1"))))
            Expect.isFalse (ClientModel.showsList model) "the census stepped aside"
            Expect.equal
                (ClientModel.selectedPane model |> Option.map PaneTab.key)
                (Some "block:term-a:b-1")
                "and the block is what is showing"

        testCase "the list's rewind is one act, and it watches behind live" <| fun () ->
            // It used to be a rewind and a select, and the select cleared the pin the rewind
            // had just taken — a verb whose whole effect was to leave the list.
            let model =
                withRecords (clientOf [ at 1L 0.0 (opened terminalA "shell") ])
                |> ClientModel.update ToggleTerminalListMsg
                |> ClientModel.update (RewindTerminalMsg terminalA)
            Expect.isFalse (ClientModel.showsList model) "the census stepped aside"
            Expect.isTrue (ClientModel.isRewound terminalA model) "and the reader is behind live"
            Expect.isTrue (ClientModel.playsRecording (TerminalTab terminalA) model) "watching the recording"

        testCase "showing anything else replaces the whole read, pin and all" <| fun () ->
            // No entry clears a subset and trusts the rest: a rewind on one terminal cannot
            // survive a reader going somewhere else, whatever they go to.
            let rewound =
                withRecords (clientOf [ at 1L 0.0 (opened terminalA "shell"); at 2L 1.0 (opened terminalB "logs") ])
                |> ClientModel.update (RewindTerminalMsg terminalA)
            Expect.isTrue (ClientModel.isRewound terminalA rewound) "arranged behind live"
            let moved = ClientModel.update (ShowInPaneMsg (Reading (TerminalTab terminalB))) rewound
            Expect.isFalse (ClientModel.isRewound terminalA moved) "the pin died with the read that held it"

        testCase "leaving the list resumes the read it covered" <| fun () ->
            // The one thing the flag did right, kept: the census is somewhere you GO, and
            // coming back puts you where you were — a rewind included.
            let model =
                withRecords (clientOf [ at 1L 0.0 (opened terminalA "shell") ])
                |> ClientModel.update (RewindTerminalMsg terminalA)
                |> ClientModel.update ToggleTerminalListMsg
                |> ClientModel.update ToggleTerminalListMsg
            Expect.isFalse (ClientModel.showsList model) "back on the tab"
            Expect.isTrue (ClientModel.isRewound terminalA model) "still behind live, where they left off"

        testCase "reaching the list opens the column it is in" <| fun () ->
            // Looking for a terminal you cannot see is exactly the case where the column is
            // shut, so the toggle brings it with it.
            let model = clientOf [ at 1L 0.0 (opened terminalA "build") ]
            Expect.isFalse model.TerminalsOpen "the column starts shut"
            let listed = ClientModel.update ToggleTerminalListMsg model
            Expect.isTrue (ClientModel.showsList listed) "the list is showing"
            Expect.isTrue listed.TerminalsOpen "and the column came with it"
    ]

// --- Pins, and the preview slot (Plan 20, stage 1) ------------------------------------------

let private pinTests =
    testList "Pins and the preview (Plan 20, stage 1)" [

        testCase "the strip holds the pins, and closed terminals are not among them" <| fun () ->
            // The whole reason the strip can stop being a census: a terminal that has closed
            // is read from its row in the list, so keeping it here would be the census again
            // under another name.
            let model =
                clientOf
                    [ at 1L 0.0 (opened terminalA "build")
                      at 2L 1.0 (opened terminalB "logs")
                      at 3L 2.0 (closedNow terminalA) ]
            Expect.equal (stripKeys model) [ "terminal:term-b" ] "only what is still running"

        testCase "a terminal I opened is pinned; one somebody else opened is not" <| fun () ->
            // Rule one, and the rule that makes an agent's terminals safe to leave out of the
            // strip: you asked for it, so it is in your hands.
            let mine =
                SessionEvent.TerminalOpened
                    { TerminalId = terminalA; OpenedBy = PeerRef ada; Title = "mine"
                      Sandbox = Some SandboxRef.defaultRef; Renewable = false }
            let theirs =
                SessionEvent.TerminalOpened
                    { TerminalId = terminalB; OpenedBy = ActorRef.Agent; Title = "running the tests"
                      Sandbox = Some SandboxRef.defaultRef; Renewable = false }
            let model = clientOf [ at 1L 0.0 mine; at 2L 1.0 theirs ]
            Expect.equal (model.Pins |> List.map PaneTab.key) [ "terminal:term-a" ] "mine, and only mine"

        testCase "typing in a terminal pins it for the person typing" <| fun () ->
            // Rule three. Watching the agent work and joining it are one keystroke apart.
            let queueId = QueueId.create "q-draft" |> expect
            let model = clientOf [ at 1L 0.0 (openedBy ActorRef.Agent terminalB "running the tests") ]
            Expect.isFalse (ClientModel.isPinned (TerminalTab terminalB) model) "not pinned by watching"
            let typing = ClientModel.update (EnsureTerminalDraftMsg (terminalB, ada, queueId)) model
            Expect.isTrue (ClientModel.isPinned (TerminalTab terminalB) typing) "pinned by taking a seat at it"

        testCase "somebody else's typing does not pin their terminal to my strip" <| fun () ->
            let queueId = QueueId.create "q-draft-bob" |> expect
            let model =
                clientOf [ at 1L 0.0 (openedBy ActorRef.Agent terminalB "running the tests") ]
                |> ClientModel.update (EnsureTerminalDraftMsg (terminalB, bob, queueId))
            Expect.isFalse (ClientModel.isPinned (TerminalTab terminalB) model) "pins are one reader's"

        testCase "reading one recording after another leaves ONE tab, not a row of them" <| fun () ->
            // The preview slot: the choice, while nothing pins it. Twenty chips tapped in a
            // busy chat used to leave twenty tabs nobody closed.
            let model =
                clientOf oneBlock
                |> ClientModel.update (ShowInPaneMsg (Reading (BlockTab (terminalA, block "1"))))
                |> ClientModel.update (ShowInPaneMsg (Reading (BlockTab (terminalA, block "2"))))
            Expect.equal
                (stripKeys model |> List.filter (fun key -> key.StartsWith "block:"))
                [ "block:term-a:b-2" ]
                "the second replaced the first"

        testCase "pinning what is previewed keeps it when the next thing is opened" <| fun () ->
            let kept = BlockTab (terminalA, block "1")
            let model =
                clientOf oneBlock
                |> ClientModel.update (ShowInPaneMsg (Reading kept))
                |> ClientModel.update (TogglePinMsg kept)
                |> ClientModel.update (ShowInPaneMsg (Reading (BlockTab (terminalA, block "2"))))
            Expect.equal
                (stripKeys model |> List.filter (fun key -> key.StartsWith "block:"))
                [ "block:term-a:b-1"; "block:term-a:b-2" ]
                "the kept one, and the new preview after it"

        testCase "unpinning leaves what you are reading on screen" <| fun () ->
            // Unpin says "stop keeping this", never "take it away while I am looking at it" —
            // it becomes the preview, exactly as it would have been had it never been pinned.
            let tab = BlockTab (terminalA, block "1")
            let model =
                clientOf oneBlock
                |> ClientModel.update (ShowInPaneMsg (Reading tab))
                |> ClientModel.update (TogglePinMsg tab)
                |> ClientModel.update (TogglePinMsg tab)
            Expect.isFalse (ClientModel.isPinned tab model) "no longer kept"
            Expect.equal
                (ClientModel.selectedPane model |> Option.map PaneTab.key)
                (Some "block:term-a:b-1")
                "and still the thing on screen"

        testCase "unpinning a terminal leaves it running" <| fun () ->
            // The distinction the pin exists to make. Ending a terminal is one verb, on its
            // row in the list, and this is not it.
            let model =
                clientOf [ at 1L 0.0 (opened terminalA "build") ]
                |> ClientModel.update (TogglePinMsg (TerminalTab terminalA))
            Expect.isFalse (ClientModel.isPinned (TerminalTab terminalA) model) "out of my strip"
            Expect.isTrue
                (Projection.tryFind terminalA model.Terminals |> Option.map (fun t -> t.IsOpen) |> Option.defaultValue false)
                "and still running for everyone"
            Expect.equal
                (ClientModel.terminalRows model |> List.map (fun t -> TerminalId.value t.TerminalId))
                [ "term-a" ]
                "still in the list, which is where every terminal is"
    ]

// --- Task cards (Plan 20, stage 4) --------------------------------------------------------

let private turnStarted (t: string) =
    AgentTurnStarted { AgentTurnId = turn t; TriggeredByMessageId = Some (message "1"); Woke = None }

let private rejected (id: TerminalId) (n: string) (author: ActorRef) (command: string) =
    SessionEvent.TerminalCommandRejected
        { TerminalId = id
          QueueId = QueueId.create ("q-" + n) |> expect
          BlockId = block n
          Author = author
          RejectedBy = PeerRef ada
          Command = command
          Reason = Some "not that one" }

let private cardTests =
    testList "Task cards (Plan 20, stage 4)" [

        testCase "consecutive commands from one turn are one card" <| fun () ->
            // An agent working across several of its terminals is the second item a single
            // turn can emit a dozen of, after tool use — and it costs one row, not twelve.
            let events =
                [ at 1L 0.0 (turnStarted "a")
                  at 2L 1.0 (opened terminalA "agent 1")
                  at 3L 2.0 (opened terminalB "agent 2")
                  at 4L 3.0 (started terminalA "1" ActorRef.Agent "make" 1)
                  at 5L 4.0 (started terminalB "2" ActorRef.Agent "npm test" 1)
                  at 6L 5.0 (started terminalA "3" ActorRef.Agent "git status" 40) ]
            Expect.equal (drawn events) [ "card:turn-a:3" ] "three commands, one card"

        testCase "a card forms on the SECOND command, never the first" <| fun () ->
            // A disclosure around one chip hides the only thing the row has to say behind a
            // click, and buys nothing back.
            let events =
                [ at 1L 0.0 (turnStarted "a")
                  at 2L 1.0 (opened terminalA "agent 1")
                  at 3L 2.0 (started terminalA "1" ActorRef.Agent "make" 1) ]
            Expect.equal (drawn events) [ "ran:b-1" ] "one command is a chip"

        testCase "a message between two commands splits the card" <| fun () ->
            // The same boundary a tool run stops at, for the same reason: swallowing what was
            // said in the middle would tell a reader the wrong story about the order.
            let events =
                [ at 1L 0.0 (turnStarted "a")
                  at 2L 1.0 (opened terminalA "agent 1")
                  at 3L 2.0 (started terminalA "1" ActorRef.Agent "make" 1)
                  at 4L 3.0 (started terminalA "2" ActorRef.Agent "npm test" 40)
                  at 5L 4.0 (sent "1" "how's it going?")
                  at 6L 5.0 (started terminalA "3" ActorRef.Agent "git status" 80)
                  at 7L 6.0 (started terminalA "4" ActorRef.Agent "git diff" 120) ]
            Expect.equal
                (drawn events)
                [ "card:turn-a:2"; "said:m-1"; "card:turn-a:2" ]
                "two cards, and the message stays between them"

        testCase "commands from two turns never share a card" <| fun () ->
            // A burst is one turn's work. Two turns' commands adjacent in the log are two
            // pieces of work that happened to touch, and a card saying otherwise invents a
            // task nobody ran.
            let events =
                [ at 1L 0.0 (turnStarted "a")
                  at 2L 1.0 (opened terminalA "agent 1")
                  at 3L 2.0 (started terminalA "1" ActorRef.Agent "make" 1)
                  at 4L 3.0 (started terminalA "2" ActorRef.Agent "npm test" 40)
                  at 5L 4.0 (turnStarted "b")
                  at 6L 5.0 (started terminalA "3" ActorRef.Agent "git status" 80)
                  at 7L 6.0 (started terminalA "4" ActorRef.Agent "git diff" 120) ]
            Expect.equal (drawn events) [ "card:turn-a:2"; "card:turn-b:2" ] "one card per turn"

        testCase "a person's commands never group, even during a turn" <| fun () ->
            // Grouping is for work nobody is hand-driving. The clock says a command happened
            // DURING a turn; only the authority says whose it was, and a person typing while
            // the agent works is still a person typing.
            let events =
                [ at 1L 0.0 (turnStarted "a")
                  at 2L 1.0 (opened terminalA "build")
                  at 3L 2.0 (started terminalA "1" (PeerRef ada) "make" 1)
                  at 4L 3.0 (started terminalA "2" (PeerRef ada) "npm test" 40) ]
            Expect.equal (drawn events) [ "ran:b-1"; "ran:b-2" ] "two chips, no card"

        testCase "a command nobody's turn started never groups" <| fun () ->
            // A block the Session Process ran on its own behalf, before any turn: no turn to
            // attribute it to, and inventing one would be a task nobody asked for.
            let events =
                [ at 1L 0.0 (opened terminalA "boot")
                  at 2L 1.0 (started terminalA "1" ActorRef.Agent "make" 1)
                  at 3L 2.0 (started terminalA "2" ActorRef.Agent "npm test" 40) ]
            Expect.equal (drawn events) [ "ran:b-1"; "ran:b-2" ] "two chips, no card"

        testCase "a refused command joins the card of the turn that proposed it" <| fun () ->
            // The refusal is the more interesting half of the pair, and a card that left it
            // out would say the turn ran fewer commands than it asked to.
            let events =
                [ at 1L 0.0 (turnStarted "a")
                  at 2L 1.0 (opened terminalA "agent 1")
                  at 3L 2.0 (started terminalA "1" ActorRef.Agent "make" 1)
                  at 4L 3.0 (rejected terminalA "2" ActorRef.Agent "rm -rf /") ]
            Expect.equal (drawn events) [ "card:turn-a:2" ] "the proposal counts, whether or not it ran"

        testCase "a card anchors where its FIRST command started" <| fun () ->
            // The chip's anchoring rule, unchanged: a burst that takes four minutes stays
            // above the messages sent while it ran rather than jumping to the bottom.
            let events =
                [ at 1L 0.0 (turnStarted "a")
                  at 2L 1.0 (opened terminalA "agent 1")
                  at 3L 2.0 (started terminalA "1" ActorRef.Agent "make" 1)
                  at 4L 3.0 (started terminalA "2" ActorRef.Agent "npm test" 40)
                  at 5L 4.0 (sent "1" "how's it going?")
                  at 6L 9.0 (completed terminalA "1" (CommandSucceeded 0) 40) ]
            let conversation, _ = ConversationProjection.applyEvents None events ConversationProjection.empty
            let timeline, _ = TimelineProjection.applyEvents None events TimelineProjection.empty
            match TimelineProjection.rows conversation timeline with
            | (RowTaskCard _ as card) :: _ ->
                Expect.equal (EventOffset.value (TimelineRow.offset card)) 3L "the offset of the first command"
            | other -> failwithf "expected the card first, got %A" other

        testCase "a card carries NO status of its own — the row is the same either way" <| fun () ->
            // What makes a card's lines mutate in place for free, exactly as chips do: the
            // row holds which blocks and where, `Projection` holds what they say.
            let running =
                [ at 1L 0.0 (turnStarted "a")
                  at 2L 1.0 (opened terminalA "agent 1")
                  at 3L 2.0 (started terminalA "1" ActorRef.Agent "make" 1)
                  at 4L 3.0 (started terminalA "2" ActorRef.Agent "npm test" 40) ]
            let finished = running @ [ at 5L 9.0 (completed terminalA "1" (CommandFailed 2) 40) ]
            let rowsOf events =
                let conversation, _ = ConversationProjection.applyEvents None events ConversationProjection.empty
                let timeline, _ = TimelineProjection.applyEvents None events TimelineProjection.empty
                TimelineProjection.rows conversation timeline
            Expect.equal (rowsOf running) (rowsOf finished) "the card does not move or change as its work does"
    ]

let private tallyTests =
    testList "What a task card counts (Plan 20, stage 4)" [

        testCase "a refusal counts as a failure" <| fun () ->
            // Red on every other surface for the same reason: a command the agent proposed
            // and did not get to run is what a person scanning for trouble is scanning for.
            Expect.equal (TaskCard.stateOf (BlockRejected (PeerRef ada, Some "no"))) TaskFailed "refused reads as failed"

        testCase "a non-zero exit and a timeout are one bucket" <| fun () ->
            // The exact code is on the line and in the block behind it. A summary that
            // counted `exit 2` apart from `timed out` would be longer and say less.
            Expect.equal (TaskCard.stateOf (BlockFinished (CommandFailed 2))) TaskFailed "exit 2 failed"
            Expect.equal (TaskCard.stateOf (BlockFinished CommandTimedOut)) TaskFailed "so did the timeout"

        testCase "the summary counts every command once" <| fun () ->
            let states = [ TaskDone; TaskFailed; TaskDone; TaskRunning; TaskDone ]
            Expect.equal
                (TaskCard.tally states)
                { Commands = 5; Failed = 1; Running = 1; Done = 3 }
                "5 commands, 3 done, 1 failed, 1 running"

        testCase "failures sort first, then what is still going" <| fun () ->
            // A burst of twenty commands with one failure buried at line fourteen makes a
            // person hunt for the one thing the card exists to show them.
            let lines = [ "a", TaskDone; "b", TaskRunning; "c", TaskFailed; "d", TaskDone ]
            Expect.equal
                (TaskCard.ordered lines |> List.map fst)
                [ "c"; "b"; "a"; "d" ]
                "failed, running, then done"

        testCase "lines keep their order WITHIN a group" <| fun () ->
            // So the only thing a finishing command changes is which group it is in: a line
            // never jumps a place inside one, which is what lets the card be read twice.
            let lines = [ "a", TaskFailed; "b", TaskFailed; "c", TaskFailed ]
            Expect.equal (TaskCard.ordered lines |> List.map fst) [ "a"; "b"; "c" ] "chronological within the group"
    ]

let tests =
    testList "Timeline and the pane (Plan 14)" [
        listTests
        pinTests
        orderTests
        toolTests
        cardTests
        tallyTests
        chipTests
        stretchTests
        unchangedTests
        paneTests
        keyframeTests
        videoTests
        readsTests
        dvrTests
    ]
