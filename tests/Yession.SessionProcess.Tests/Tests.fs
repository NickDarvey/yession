module Yession.SessionProcess.Tests

open System
open Xunit
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
/// Returns the list of pages, exercising the caller-driven paging loop.
let private pagesFrom (log: EventLog<SessionEvent>) (after: EventOffset option) (limit: int) =
    let rec loop after acc =
        async {
            let! page = log.Read after limit
            let acc = page :: acc
            if page.IsEnd then return List.rev acc
            else return! loop page.LastOffset acc
        }
    loop after [] |> Async.RunSynchronously

/// All events after `after`, read in a single unbounded page.
let private allEventsAfter (log: EventLog<SessionEvent>) (after: EventOffset option) =
    let page = log.Read after Int32.MaxValue |> Async.RunSynchronously
    page.Events

module ``Append assigns monotonic offsets`` =

    [<Fact>]
    let ``sequential appends produce contiguous offsets from zero`` () =
        let log = newLog ()
        let offsets =
            [ for _ in 1..5 ->
                  log.Append System (sampleEvent ())
                  |> Async.RunSynchronously
                  |> fun r -> EventOffset.value r.Offset ]
        Assert.Equal<int64 list>([ 0L; 1L; 2L; 3L; 4L ], offsets)

    [<Fact>]
    let ``interleaved concurrent appends yield a gapless, unique offset set`` () =
        let log = newLog ()
        let count = 200
        let offsets =
            Array.init count (fun _ -> log.Append Agent (sampleEvent ()))
            |> Async.Parallel
            |> Async.RunSynchronously
            |> Array.map (fun r -> EventOffset.value r.Offset)
            |> Array.sort
        Assert.Equal<int64[]>([| 0L .. int64 count - 1L |], offsets)

module ``Read pages deterministically`` =

    let private appendN (log: EventLog<SessionEvent>) n =
        for _ in 1..n do
            log.Append System (sampleEvent ()) |> Async.RunSynchronously |> ignore

    [<Fact>]
    let ``identical inputs against identical state return identical pages`` () =
        let log = newLog ()
        appendN log 7
        let first = pagesFrom log None 3
        let second = pagesFrom log None 3
        Assert.Equal<EventPage<SessionEvent> list>(first, second)

    [<Fact>]
    let ``paging by limit chunks events and only the final page is the end`` () =
        let log = newLog ()
        appendN log 7
        let pages = pagesFrom log None 3
        Assert.Equal(3, pages.Length)
        Assert.Equal<int list>([ 3; 3; 1 ], pages |> List.map (fun p -> p.Events.Length))
        Assert.Equal<bool list>([ false; false; true ], pages |> List.map (fun p -> p.IsEnd))
        Assert.Equal(6L, pages |> List.last |> fun p -> EventOffset.value p.LastOffset.Value)

    [<Fact>]
    let ``after excludes already-seen offsets`` () =
        let log = newLog ()
        appendN log 5
        let offsets =
            allEventsAfter log (Some (EventOffset.create 2L |> expect))
            |> List.map (fun e -> EventOffset.value e.Offset)
        Assert.Equal<int64 list>([ 3L; 4L ], offsets)

    [<Fact>]
    let ``reading past the tail yields an empty end page`` () =
        let log = newLog ()
        appendN log 3
        let page = log.Read (Some (EventOffset.create 2L |> expect)) Int32.MaxValue |> Async.RunSynchronously
        Assert.Empty page.Events
        Assert.True page.IsEnd
        Assert.True page.LastOffset.IsNone

    [<Fact>]
    let ``reading an empty log yields an empty end page`` () =
        let log = newLog ()
        let page = log.Read None Int32.MaxValue |> Async.RunSynchronously
        Assert.True page.IsEnd
        Assert.Empty page.Events

module ``Process model`` =

    let private envelope (offset: int64) : EventEnvelope<SessionEvent> =
        { EventId = EventId.fresh ()
          SessionId = sessionId
          Offset = EventOffset.create offset |> expect
          Actor = SessionProcess
          Timestamp = fixedClock ()
          Event = SessionCreated { SessionCreated.SessionId = sessionId } }

    [<Fact>]
    let ``initial model is empty and idle`` () =
        let model = ProcessModel.initial sessionId
        Assert.Equal(sessionId, model.SessionId)
        Assert.True model.EventLog.LatestOffset.IsNone
        Assert.Empty model.Conversation.Items
        Assert.Empty model.Peers
        Assert.Equal(Idle, model.Agent)

    [<Fact>]
    let ``applying events advances the event-log read position`` () =
        let model =
            ProcessModel.initial sessionId
            |> ProcessModel.applyEvents [ envelope 0L; envelope 1L; envelope 2L ]
        Assert.Equal(Some 2L, model.EventLog.LatestOffset |> Option.map EventOffset.value)

    [<Fact>]
    let ``applying overlapping pages is idempotent on offset`` () =
        let model =
            ProcessModel.initial sessionId
            |> ProcessModel.applyEvents [ envelope 0L; envelope 1L ]
            |> ProcessModel.applyEvents [ envelope 1L; envelope 2L ]
        Assert.Equal(Some 2L, model.EventLog.LatestOffset |> Option.map EventOffset.value)
        Assert.Empty model.Conversation.Items

module ``Peer handshake and presence`` =

    let private token = "local-session-token"
    let private peerId = PeerId.create "peer-ada" |> expect

    let private hello : SessionFrame<unit> =
        Control (PeerHello { PeerId = peerId; DisplayName = "Ada"; Token = token })

    /// Run a peer session against an in-memory loopback, driving the client side with
    /// `client` and returning the appended events once the session ends.
    let private runSession
        (sendToken: string)
        (client: FrameChannel<unit> -> Async<unit>)
        : SessionEvent list =
        let log = newLog ()
        let clientEnd, serverEnd = InMemoryChannel.createPair<unit> ()
        async {
            let! server = Async.StartChild (PeerSession.run sessionId sendToken log serverEnd)
            do! client clientEnd
            do! server
        }
        |> Async.RunSynchronously
        allEventsAfter log None |> List.map (fun e -> e.Event)

    [<Fact>]
    let ``a valid hello is accepted and appends PeerJoined`` () =
        let mutable response : SessionFrame<unit> option = None
        let events =
            runSession token (fun ch ->
                async {
                    do! ch.Send hello
                    let! resp = ch.Receive ()
                    response <- resp
                    do! ch.Close ()
                })
        match response with
        | Some (Control (PeerAccepted accepted)) ->
            Assert.Equal("Ada", accepted.AssignedDisplayName)
            Assert.Equal(sessionId, accepted.SessionId)
            Assert.Equal(Some 0L, accepted.LatestOffset |> Option.map EventOffset.value)
        | other -> Assert.Fail(sprintf "expected PeerAccepted, got %A" other)
        Assert.Contains(PeerJoined { PeerId = peerId; DisplayName = "Ada" }, events)

    [<Fact>]
    let ``a bad token is rejected and appends nothing`` () =
        let mutable response : SessionFrame<unit> option = None
        let events =
            runSession "the-wrong-token" (fun ch ->
                async {
                    do! ch.Send hello
                    let! resp = ch.Receive ()
                    response <- resp
                })
        match response with
        | Some (Control (PeerRejected _)) -> ()
        | other -> Assert.Fail(sprintf "expected PeerRejected, got %A" other)
        Assert.Empty events

    [<Fact>]
    let ``disconnecting after accept appends PeerLeft after PeerJoined`` () =
        let events =
            runSession token (fun ch ->
                async {
                    do! ch.Send hello
                    let! _ = ch.Receive () // drain PeerAccepted
                    do! ch.Close ()        // disconnect
                })
        Assert.Equal<SessionEvent list>(
            [ PeerJoined { PeerId = peerId; DisplayName = "Ada" }
              PeerLeft { PeerId = peerId } ],
            events)
