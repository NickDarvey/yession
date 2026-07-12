module Yession.Tests.SessionProcess

open System
open Fable.Pyxpecto
open Yession.Domain
open Yession.SessionProcess

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

let private sessionId = SessionId.create "session-log" |> expect
let private fixedClock () = DateTimeOffset(2026, 6, 14, 0, 0, 0, TimeSpan.Zero)

let private newLog () : EventLog<SessionEvent> =
    InMemoryEventLog.create sessionId fixedClock

let private sampleEvent () = SessionCreated { SessionCreated.SessionId = sessionId }

/// Page through the whole log from `after`, `limit` events at a time, until the tail.
let private pagesFrom (log: EventLog<SessionEvent>) (after: EventOffset option) (limit: int) =
    let rec loop after acc =
        async {
            let! page = log.Read after limit
            let acc = page :: acc
            if page.IsEnd then return List.rev acc
            else return! loop page.LastOffset acc
        }
    loop after []

/// All events after `after`, read in a single unbounded page.
let private allEventsAfter (log: EventLog<SessionEvent>) (after: EventOffset option) =
    async {
        let! page = log.Read after Int32.MaxValue
        return page.Events
    }

let private appendN (log: EventLog<SessionEvent>) n =
    async {
        for _ in 1..n do
            let! _ = log.Append System (sampleEvent ())
            ()
    }

let private eventLogTests =
    testList "Event log" [
        testCaseAsync "sequential appends produce contiguous offsets from zero" <|
            async {
                let log = newLog ()
                let mutable offsets = []
                for _ in 1..5 do
                    let! r = log.Append System (sampleEvent ())
                    offsets <- EventOffset.value r.Offset :: offsets
                Expect.equal (List.rev offsets) [ 0L; 1L; 2L; 3L; 4L ] "contiguous from zero"
            }

        // Node is single-threaded, so this exercises a large gapless append run rather than
        // true parallelism (the .NET race test is not meaningful on this runtime).
        testCaseAsync "a long append run yields a gapless, unique offset set" <|
            async {
                let log = newLog ()
                let count = 200
                do! appendN log count
                let! events = allEventsAfter log None
                let offsets = events |> List.map (fun e -> EventOffset.value e.Offset)
                Expect.equal offsets [ 0L .. int64 count - 1L ] "gapless 0..n-1"
            }

        testCaseAsync "identical inputs against identical state return identical pages" <|
            async {
                let log = newLog ()
                do! appendN log 7
                let! first = pagesFrom log None 3
                let! second = pagesFrom log None 3
                Expect.equal first second "deterministic paging"
            }

        testCaseAsync "paging by limit chunks events and only the final page is the end" <|
            async {
                let log = newLog ()
                do! appendN log 7
                let! pages = pagesFrom log None 3
                Expect.equal pages.Length 3 "three pages"
                Expect.equal (pages |> List.map (fun p -> p.Events.Length)) [ 3; 3; 1 ] "chunk sizes"
                Expect.equal (pages |> List.map (fun p -> p.IsEnd)) [ false; false; true ] "only last is end"
                Expect.equal (pages |> List.last |> fun p -> EventOffset.value p.LastOffset.Value) 6L "last offset is 6"
            }

        testCaseAsync "after excludes already-seen offsets" <|
            async {
                let log = newLog ()
                do! appendN log 5
                let! events = allEventsAfter log (Some(EventOffset.create 2L |> expect))
                let offsets = events |> List.map (fun e -> EventOffset.value e.Offset)
                Expect.equal offsets [ 3L; 4L ] "only offsets above 2"
            }

        testCaseAsync "reading past the tail yields an empty end page" <|
            async {
                let log = newLog ()
                do! appendN log 3
                let! page = log.Read (Some(EventOffset.create 2L |> expect)) Int32.MaxValue
                Expect.isEmpty page.Events "no events"
                Expect.isTrue page.IsEnd "is end"
                Expect.isNone page.LastOffset "no last offset"
            }

        testCaseAsync "reading an empty log yields an empty end page" <|
            async {
                let log = newLog ()
                let! page = log.Read None Int32.MaxValue
                Expect.isTrue page.IsEnd "is end"
                Expect.isEmpty page.Events "no events"
            }
    ]

let private processModelTests =
    let envelope (offset: int64) : EventEnvelope<SessionEvent> =
        { EventId = EventId.fresh ()
          SessionId = sessionId
          Offset = EventOffset.create offset |> expect
          Actor = SessionProcess
          Timestamp = fixedClock ()
          Event = SessionCreated { SessionCreated.SessionId = sessionId } }

    testList "Process model" [
        testCase "initial model is empty and idle" <| fun () ->
            let model = ProcessModel.initial sessionId
            Expect.equal model.SessionId sessionId "session id"
            Expect.isNone model.EventLog.LatestOffset "no offset consumed"
            Expect.isEmpty model.Conversation.Items "no conversation items"
            Expect.isEmpty model.Peers "no peers"
            Expect.equal model.Agent Idle "agent idle"

        testCase "applying events advances the event-log read position" <| fun () ->
            let model =
                ProcessModel.initial sessionId
                |> ProcessModel.applyEvents [ envelope 0L; envelope 1L; envelope 2L ]
            Expect.equal (model.EventLog.LatestOffset |> Option.map EventOffset.value) (Some 2L) "advanced to 2"

        testCase "applying overlapping pages is idempotent on offset" <| fun () ->
            let model =
                ProcessModel.initial sessionId
                |> ProcessModel.applyEvents [ envelope 0L; envelope 1L ]
                |> ProcessModel.applyEvents [ envelope 1L; envelope 2L ]
            Expect.equal (model.EventLog.LatestOffset |> Option.map EventOffset.value) (Some 2L) "advanced to 2 once"
            Expect.isEmpty model.Conversation.Items "no items"
    ]

let private handshakeTests =
    let token = "local-session-token"
    let peerId = PeerId.create "peer-ada" |> expect
    let hello : SessionFrame<unit> =
        Control (PeerHello { PeerId = peerId; DisplayName = "Ada"; Token = token })

    /// Run a peer session against an in-memory loopback, driving the client side with
    /// `client`, and return the appended events once the session ends.
    let runSession (sendToken: string) (client: FrameChannel<unit> -> Async<unit>) : Async<SessionEvent list> =
        async {
            let log = newLog ()
            let clientEnd, serverEnd = InMemoryChannel.createPair<unit> ()
            let! server = Async.StartChild (PeerSession.run sessionId sendToken log PeerSession.PeerHandlers.none serverEnd)
            do! client clientEnd
            do! server
            let! events = allEventsAfter log None
            return events |> List.map (fun e -> e.Event)
        }

    testList "Peer handshake and presence" [
        testCaseAsync "a valid hello is accepted and appends PeerJoined" <|
            async {
                let mutable response : SessionFrame<unit> option = None
                let! events =
                    runSession token (fun ch ->
                        async {
                            do! ch.Send hello
                            let! resp = ch.Receive ()
                            response <- resp
                            do! ch.Close ()
                        })
                match response with
                | Some (Control (PeerAccepted accepted)) ->
                    Expect.equal accepted.AssignedDisplayName "Ada" "assigned name"
                    Expect.equal accepted.SessionId sessionId "session id"
                    Expect.equal (accepted.LatestOffset |> Option.map EventOffset.value) (Some 0L) "joined offset"
                | other -> failwithf "expected PeerAccepted, got %A" other
                Expect.containsAll events [ PeerJoined { PeerId = peerId; DisplayName = "Ada" } ] "PeerJoined appended"
            }

        testCaseAsync "a bad token is rejected and appends nothing" <|
            async {
                let mutable response : SessionFrame<unit> option = None
                let! events =
                    runSession "the-wrong-token" (fun ch ->
                        async {
                            do! ch.Send hello
                            let! resp = ch.Receive ()
                            response <- resp
                        })
                match response with
                | Some (Control (PeerRejected _)) -> ()
                | other -> failwithf "expected PeerRejected, got %A" other
                Expect.isEmpty events "no events appended"
            }

        testCaseAsync "disconnecting after accept appends PeerLeft after PeerJoined" <|
            async {
                let! events =
                    runSession token (fun ch ->
                        async {
                            do! ch.Send hello
                            let! _ = ch.Receive () // drain PeerAccepted
                            do! ch.Close ()        // disconnect
                        })
                Expect.equal
                    events
                    [ PeerJoined { PeerId = peerId; DisplayName = "Ada" }
                      PeerLeft { PeerId = peerId } ]
                    "PeerJoined then PeerLeft"
            }
    ]

let tests =
    testList "SessionProcess" [
        eventLogTests
        processModelTests
        handshakeTests
    ]
