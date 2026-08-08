module Yession.Tests.Timeline

// The chat as a PERSON reads it (Plan 14, stage 1): what was said and what was run, merged
// into one order. Cheap tier throughout — the whole thing is a pure fold over envelopes and
// a sort, so nothing here needs a port, a process, or a browser.

open System
open Fable.Pyxpecto
open Yession.Domain

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

let tests =
    testList "Timeline (Plan 14, stage 1)" [
        orderTests
        chipTests
        stretchTests
        unchangedTests
    ]
