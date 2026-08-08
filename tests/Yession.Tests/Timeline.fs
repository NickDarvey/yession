module Yession.Tests.Timeline

// The chat as a PERSON reads it (Plan 14, stage 1): what was said and what was run, merged
// into one order. Cheap tier throughout — the whole thing is a pure fold over envelopes and
// a sort, so nothing here needs a port, a process, or a browser.

open System
open Fable.Pyxpecto
open Yession.Domain
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

let private opened (id: TerminalId) (title: string) =
    SessionEvent.TerminalOpened { TerminalId = id; OpenedBy = PeerRef ada; Title = title }

let private sent (n: string) (body: string) =
    MessageSent { MessageId = message n; QueueId = None; Author = PeerRef ada; Body = body }

let private started (id: TerminalId) (n: string) (author: ActorRef) (command: string) (fromSeq: int) =
    SessionEvent.TerminalBlockStarted
        { TerminalId = id
          BlockId = block n
          QueueId = None
          Author = author
          ApprovedBy = None
          Command = command
          FromSeq = fromSeq }

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
        | TimelineStretch stretch -> "held:" + TerminalId.value stretch.TerminalId)

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
            // `TerminalProjection` holds what it currently says. A chip that copied the
            // status in would need its own update path, and would be free to disagree.
            let running =
                [ at 1L 0.0 (opened terminalA "build")
                  at 2L 1.0 (started terminalA "1" (PeerRef ada) "make" 1) ]
            let finished = running @ [ at 3L 9.0 (completed terminalA "1" (CommandFailed 2) 40) ]
            let itemsOf events = (TimelineProjection.applyEvents None events TimelineProjection.empty |> fst).TerminalItems
            Expect.equal (itemsOf running) (itemsOf finished) "the timeline entry does not move or change"
            let statusOf events =
                let proj = events |> List.fold (fun p (e: EventEnvelope<SessionEvent>) -> TerminalProjection.applyEvent p e.Event) TerminalProjection.empty
                TerminalProjection.tryFind terminalA proj
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
            // through `TerminalDigest` — folding terminal events in here would double-feed
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
                [ at 1L 0.0 (AgentTurnStarted { AgentTurnId = turnId; TriggeredByMessageId = message "1" })
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
        testCase "every terminal is furniture in the strip; opened tabs come after" <| fun () ->
            let model = clientOf (oneBlock @ [ at 4L 3.0 (opened terminalB "logs") ])
            Expect.equal (stripKeys model) [ "terminal:term-a"; "terminal:term-b" ] "the terminals, in open order"
            let withTab = ClientModel.update (OpenPaneTabMsg (BlockTab (terminalA, block "1"))) model
            Expect.equal
                (stripKeys withTab)
                [ "terminal:term-a"; "terminal:term-b"; "block:term-a:b-1" ]
                "and the opened tab after them"

        testCase "opening a tab shows it, and opens the column it is in" <| fun () ->
            let model = clientOf oneBlock
            Expect.isFalse model.TerminalsOpen "the column starts shut"
            let opened' = ClientModel.update (OpenPaneTabMsg (BlockTab (terminalA, block "1"))) model
            Expect.equal
                (ClientModel.selectedPane opened' |> Option.map PaneTab.key)
                (Some "block:term-a:b-1")
                "the tab that was just opened is the one showing"
            Expect.isTrue opened'.TerminalsOpen "and the column came with it"

        testCase "tapping the same chip twice brings its tab forward, not a second copy" <| fun () ->
            let model =
                clientOf oneBlock
                |> ClientModel.update (OpenPaneTabMsg (BlockTab (terminalA, block "1")))
                |> ClientModel.update (SelectTerminalMsg terminalA)
                |> ClientModel.update (OpenPaneTabMsg (BlockTab (terminalA, block "1")))
            Expect.equal (List.length model.PaneTabs) 1 "one tab"
            Expect.equal
                (ClientModel.selectedPane model |> Option.map PaneTab.key)
                (Some "block:term-a:b-1")
                "and it is showing again"

        testCase "closing a tab falls back to the first OPEN terminal" <| fun () ->
            // Through the same resolver a closed terminal already relies on: a choice naming
            // something no longer in the strip resolves to somewhere you can type. Clearing
            // the choice as well would be a second mechanism for one fact.
            let tab = BlockTab (terminalA, block "1")
            let model =
                clientOf oneBlock
                |> ClientModel.update (OpenPaneTabMsg tab)
                |> ClientModel.update (ClosePaneTabMsg tab)
            Expect.isEmpty model.PaneTabs "the tab is gone"
            Expect.equal
                (ClientModel.selectedPane model |> Option.map PaneTab.key)
                (Some "terminal:term-a")
                "and the pane landed somewhere usable"

        testCase "a terminal's own tab cannot be closed — the strip is where every terminal is" <| fun () ->
            Expect.isFalse (PaneTab.isClosable (TerminalTab terminalA)) "furniture"
            Expect.isTrue (PaneTab.isClosable (BlockTab (terminalA, block "1"))) "a block view is a person's"

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
                |> ClientModel.update (OpenPaneTabMsg (BlockTab (terminalA, block "1")))
            let html = Support.render model
            let required =
                [ "the tab", Dom.attr Dom.Hooks.paneTab "block:term-a:b-1"
                  "its close control", Dom.attr Dom.Hooks.paneTabClose "block:term-a:b-1"
                  "the panel showing it", Dom.attr Dom.Hooks.panePanel "block:term-a:b-1"
                  "the block's read-only view", Dom.attr Dom.Hooks.paneBlock "b-1"
                  "the command", "ls -la"
                  // The output is PLAYED, not printed: what a command wrote is a recording,
                  // and a stream renderer would show a cursor-moving program as garbage.
                  "where its recording mounts", Dom.attr Dom.Hooks.paneReplay "block:term-a:b-1" ]
            for label, marker in required do
                Expect.isTrue (html.Contains marker) (sprintf "%s (`%s`) must render" label marker)
            // Read-only: no composer for a block you are reading back.
            Expect.isFalse
                (html.Contains (Dom.attr Dom.Hooks.terminalInput (BodyKey.terminalDraft terminalA ada)))
                "no command line in a block's view"

        testCase "the strip is one tablist, and every tab in it is a real tab" <| fun () ->
            let model =
                clientOf oneBlock |> ClientModel.update (OpenPaneTabMsg (BlockTab (terminalA, block "1")))
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
            // `markers` is the player's own option, which is why the step-out needs no second
            // recording: a whole cast with chapters and a start position expresses everything
            // a slice can.
            let model = withRecords (clientOf recordedTerminal)
            match ClientModel.paneReplay (TerminalTab terminalA) model with
            | Some replay ->
                Expect.equal replay.Markers [ 10.0, "make"; 40.0, "make test" ] "one chapter per block, at its first line"
                Expect.isNone replay.StartAt "and it starts at the start until somebody steps out to it"
            | None -> failwith "the header is known, so there is a recording"

        testCase "'play whole terminal' lands on the block it stepped out from" <| fun () ->
            // The two paths answer different questions: the slice is "what did this command
            // print", the whole is "what was going on around it".
            let model =
                withRecords (clientOf recordedTerminal)
                |> ClientModel.update (PlayWholeTerminalMsg (terminalA, 3))
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
                |> ClientModel.update (PlayWholeTerminalMsg (terminalA, 3))
                |> ClientModel.update (SelectTerminalMsg terminalA)
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
                Expect.equal replay.Poster (Some 30.0) "the last frame of the range, from its own zero"
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
            let html = Support.render (ClientModel.update (OpenPaneTabMsg (BlockTab (terminalA, block "no"))) model)
            Expect.isTrue (html.Contains "rejected by ada") "the tab says who refused it"
            Expect.isFalse (html.Contains (Dom.attr Dom.Hooks.paneReplay "block:term-a:b-no")) "and mounts no player"

        testCase "the step-out is offered only where there IS a whole recording" <| fun () ->
            // A live terminal's recording is still being written, and rewinding one of those
            // is the DVR — a different mechanism, not this one pretending.
            let closed =
                withRecords (clientOf recordedTerminal)
                |> ClientModel.update (OpenPaneTabMsg (BlockTab (terminalA, block "1")))
            Expect.isTrue
                ((Support.render closed).Contains (Dom.attr Dom.Hooks.panePlayWhole "b-1"))
                "a closed terminal's block can step out to the whole recording"
            let stillOpen =
                withRecords (clientOf (recordedTerminal |> List.filter (fun e -> match e.Event with SessionEvent.TerminalClosed _ -> false | _ -> true)))
                |> ClientModel.update (OpenPaneTabMsg (BlockTab (terminalA, block "1")))
            Expect.isFalse
                ((Support.render stillOpen).Contains Dom.Hooks.panePlayWhole)
                "a live one does not, because there is no finished recording to step into"

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

let tests =
    testList "Timeline and the pane (Plan 14)" [
        orderTests
        chipTests
        stretchTests
        unchangedTests
        paneTests
        keyframeTests
        videoTests
    ]
