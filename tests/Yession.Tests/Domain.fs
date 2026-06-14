module Yession.Tests.Domain

open System
open Fable.Pyxpecto
open Yession.Domain

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

let private identityTests =
    testList "Identity smart constructors" [
        testCase "SessionId trims surrounding whitespace" <| fun () ->
            let id = SessionId.create "  session-1  " |> expect
            Expect.equal (SessionId.value id) "session-1" "should be trimmed"

        testCase "SessionId rejects blank input" <| fun () ->
            Expect.isError (SessionId.create "   ") "blank should be rejected"

        testCase "PeerId rejects empty input" <| fun () ->
            Expect.isError (PeerId.create "") "empty should be rejected"

        testCase "EventOffset rejects negative values" <| fun () ->
            Expect.isError (EventOffset.create -1L) "negative should be rejected"

        testCase "EventOffset accepts zero" <| fun () ->
            let offset = EventOffset.create 0L |> expect
            Expect.equal (EventOffset.value offset) 0L "zero is valid"

        testCase "EventId rejects the empty guid" <| fun () ->
            Expect.isError (EventId.create Guid.Empty) "empty guid should be rejected"
    ]

let private envelopeSerializationTests =
    let sampleEnvelope () : EventEnvelope<SessionEvent> =
        let sessionId = SessionId.create "session-42" |> expect
        let peerId = PeerId.create "peer-7" |> expect
        { EventId = EventId.fresh ()
          SessionId = sessionId
          Offset = EventOffset.zero
          Actor = HumanPeer peerId
          Timestamp = DateTimeOffset(2026, 6, 14, 10, 30, 0, TimeSpan.FromHours 10.0)
          Event = SessionCreated { SessionCreated.SessionId = sessionId } }

    testList "Envelope serialization" [
        testCase "EventEnvelope<SessionEvent> round-trips through serialization unchanged" <| fun () ->
            let original = sampleEnvelope ()
            let json = Codec.toString Codec.sessionEventEnvelope original
            let roundTripped = Codec.fromString Codec.sessionEventEnvelope json |> expect
            Expect.equal roundTripped original "round-trip should be identical"

        testCase "Decoding malformed JSON yields an Error" <| fun () ->
            Expect.isError (Codec.fromString Codec.sessionEventEnvelope "{ not valid json ") "malformed JSON should fail"
    ]

let private conversationProjectionTests =
    let sessionId = SessionId.create "session-proj" |> expect

    /// Ordered envelopes with the given offsets, all SessionCreated.
    let envelopes (offsets: int64 list) : EventEnvelope<SessionEvent> list =
        offsets
        |> List.map (fun n ->
            { EventId = EventId.fresh ()
              SessionId = sessionId
              Offset = EventOffset.create n |> expect
              Actor = SessionProcess
              Timestamp = DateTimeOffset(2026, 6, 14, 0, 0, 0, TimeSpan.Zero)
              Event = SessionCreated { SessionCreated.SessionId = sessionId } })

    testList "Conversation projection" [
        testCase "folding a fixed ordered sequence is deterministic" <| fun () ->
            let events = envelopes [ 0L; 1L; 2L; 3L ]
            let first = ConversationProjection.applyEvents None events ConversationProjection.empty
            let second = ConversationProjection.applyEvents None events ConversationProjection.empty
            Expect.equal first second "same input yields same projection"

        testCase "high-water offset advances to the last applied offset" <| fun () ->
            let events = envelopes [ 0L; 1L; 2L ]
            let _, highWater = ConversationProjection.applyEvents None events ConversationProjection.empty
            Expect.equal (highWater |> Option.map EventOffset.value) (Some 2L) "high-water is the last offset"

        testCase "re-applying an overlapping page does not advance past the tail or duplicate items" <| fun () ->
            let firstPage = envelopes [ 0L; 1L; 2L ]
            let proj1, hw1 = ConversationProjection.applyEvents None firstPage ConversationProjection.empty
            let overlapping = envelopes [ 1L; 2L; 3L ]
            let proj2, hw2 = ConversationProjection.applyEvents hw1 overlapping proj1
            Expect.equal (hw2 |> Option.map EventOffset.value) (Some 3L) "advances only to 3"
            Expect.equal proj2 proj1 "projection unchanged (no conversation items)"
            Expect.isEmpty proj2.Items "no items contributed"

        testCase "re-applying the identical page is a no-op" <| fun () ->
            let page = envelopes [ 0L; 1L; 2L ]
            let proj1, hw1 = ConversationProjection.applyEvents None page ConversationProjection.empty
            let proj2, hw2 = ConversationProjection.applyEvents hw1 page proj1
            Expect.equal proj2 proj1 "projection unchanged"
            Expect.equal hw2 hw1 "high-water unchanged"
    ]

let private frameSerializationTests =
    let sessionId = SessionId.create "session-frames" |> expect
    let peerId = PeerId.create "peer-1" |> expect
    let draftId = DraftId.create "draft-1" |> expect
    let requestId = RequestId.fresh ()
    let offset = EventOffset.create 7L |> expect

    let sampleEnvelope : EventEnvelope<SessionEvent> =
        { EventId = EventId.fresh ()
          SessionId = sessionId
          Offset = offset
          Actor = HumanPeer peerId
          Timestamp = DateTimeOffset(2026, 6, 14, 0, 0, 0, TimeSpan.Zero)
          Event = SessionCreated { SessionCreated.SessionId = sessionId } }

    let samplePage : EventPage<SessionEvent> =
        { Events = [ sampleEnvelope ]; LastOffset = Some offset; IsEnd = true }

    let everyVariant : SessionFrame<string> list =
        [ State (StateSync "opaque-sync-payload")
          Command (Request(requestId, StartDraft))
          Command (Request(requestId, SendDraft draftId))
          Command (Response(requestId, CommandAccepted))
          Command (Response(requestId, CommandRejected "nope"))
          EventLog (EventsAvailable offset)
          EventLog (ReadEventsAfter(requestId, Some offset, 50))
          EventLog (ReadEventsAfter(requestId, None, 10))
          EventLog (EventsPage(requestId, samplePage))
          Control (PeerHello { PeerId = peerId; DisplayName = "Ada"; Token = "tok" })
          Control (PeerAccepted { SessionId = sessionId; AssignedDisplayName = "Ada"; LatestOffset = Some offset })
          Control (PeerRejected "bad token")
          Control Ping
          Control Pong ]

    testList "Session frame serialization" [
        testCase "every session frame variant round-trips unchanged" <| fun () ->
            let codec = Codec.sessionFrame Codec.string
            for frame in everyVariant do
                let roundTripped = Codec.toString codec frame |> Codec.fromString codec |> expect
                Expect.equal roundTripped frame "frame round-trip"

        testCase "PeerJoined and PeerLeft events round-trip through the envelope codec" <| fun () ->
            let joined = { sampleEnvelope with Event = PeerJoined { PeerId = peerId; DisplayName = "Ada" } }
            let left = { sampleEnvelope with Event = PeerLeft { PeerId = peerId } }
            for env in [ joined; left ] do
                let roundTripped =
                    Codec.toString Codec.sessionEventEnvelope env
                    |> Codec.fromString Codec.sessionEventEnvelope
                    |> expect
                Expect.equal roundTripped env "event round-trip"
    ]

let tests =
    testList "Domain" [
        identityTests
        envelopeSerializationTests
        conversationProjectionTests
        frameSerializationTests
    ]
