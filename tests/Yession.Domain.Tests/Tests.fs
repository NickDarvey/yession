module Yession.Domain.Tests

open System
open Xunit
open Yession.Domain

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

module ``Identity smart constructors`` =

    [<Fact>]
    let ``SessionId trims surrounding whitespace`` () =
        let id = SessionId.create "  session-1  " |> expect
        Assert.Equal("session-1", SessionId.value id)

    [<Fact>]
    let ``SessionId rejects blank input`` () =
        match SessionId.create "   " with
        | Ok _ -> Assert.Fail "expected blank SessionId to be rejected"
        | Error _ -> ()

    [<Fact>]
    let ``PeerId rejects empty input`` () =
        match PeerId.create "" with
        | Ok _ -> Assert.Fail "expected empty PeerId to be rejected"
        | Error _ -> ()

    [<Fact>]
    let ``EventOffset rejects negative values`` () =
        match EventOffset.create -1L with
        | Ok _ -> Assert.Fail "expected negative EventOffset to be rejected"
        | Error _ -> ()

    [<Fact>]
    let ``EventOffset accepts zero`` () =
        let offset = EventOffset.create 0L |> expect
        Assert.Equal(0L, EventOffset.value offset)

    [<Fact>]
    let ``EventId rejects the empty guid`` () =
        match EventId.create Guid.Empty with
        | Ok _ -> Assert.Fail "expected empty-guid EventId to be rejected"
        | Error _ -> ()

module ``Envelope serialization`` =

    let private sampleEnvelope () : EventEnvelope<SessionEvent> =
        let sessionId = SessionId.create "session-42" |> expect
        let peerId = PeerId.create "peer-7" |> expect
        { EventId = EventId.fresh ()
          SessionId = sessionId
          Offset = EventOffset.zero
          Actor = HumanPeer peerId
          Timestamp = DateTimeOffset(2026, 6, 14, 10, 30, 0, TimeSpan.FromHours 10.0)
          Event = SessionCreated { SessionCreated.SessionId = sessionId } }

    [<Fact>]
    let ``EventEnvelope<SessionEvent> round-trips through serialization unchanged`` () =
        let original = sampleEnvelope ()
        let json = Codec.toString Codec.sessionEventEnvelope original
        let roundTripped = Codec.fromString Codec.sessionEventEnvelope json |> expect
        Assert.Equal(original, roundTripped)

    [<Fact>]
    let ``Decoding malformed JSON yields an Error`` () =
        match Codec.fromString Codec.sessionEventEnvelope "{ not valid json " with
        | Ok _ -> Assert.Fail "expected malformed JSON to fail decoding"
        | Error _ -> ()

module ``Conversation projection`` =

    let private sessionId = SessionId.create "session-proj" |> expect

    /// Build an ordered envelope sequence with offsets 0..n-1, all SessionCreated.
    let private envelopes (offsets: int64 list) : EventEnvelope<SessionEvent> list =
        offsets
        |> List.map (fun n ->
            { EventId = EventId.fresh ()
              SessionId = sessionId
              Offset = EventOffset.create n |> expect
              Actor = SessionProcess
              Timestamp = DateTimeOffset(2026, 6, 14, 0, 0, 0, TimeSpan.Zero)
              Event = SessionCreated { SessionCreated.SessionId = sessionId } })

    [<Fact>]
    let ``folding a fixed ordered sequence is deterministic`` () =
        let events = envelopes [ 0L; 1L; 2L; 3L ]
        let first = ConversationProjection.applyEvents None events ConversationProjection.empty
        let second = ConversationProjection.applyEvents None events ConversationProjection.empty
        Assert.Equal(first, second)

    [<Fact>]
    let ``high-water offset advances to the last applied offset`` () =
        let events = envelopes [ 0L; 1L; 2L ]
        let _, highWater = ConversationProjection.applyEvents None events ConversationProjection.empty
        Assert.Equal(Some 2L, highWater |> Option.map EventOffset.value)

    [<Fact>]
    let ``re-applying an overlapping page does not advance past the tail or duplicate items`` () =
        let firstPage = envelopes [ 0L; 1L; 2L ]
        let proj1, hw1 = ConversationProjection.applyEvents None firstPage ConversationProjection.empty
        // Overlapping page repeats offsets 1 and 2, then introduces 3.
        let overlapping = envelopes [ 1L; 2L; 3L ]
        let proj2, hw2 = ConversationProjection.applyEvents hw1 overlapping proj1
        Assert.Equal(Some 3L, hw2 |> Option.map EventOffset.value)
        // No SessionCreated event contributes an item, so the projection stays empty and
        // never duplicates regardless of overlap.
        Assert.Equal(proj1, proj2)
        Assert.Empty proj2.Items

    [<Fact>]
    let ``re-applying the identical page is a no-op`` () =
        let page = envelopes [ 0L; 1L; 2L ]
        let proj1, hw1 = ConversationProjection.applyEvents None page ConversationProjection.empty
        let proj2, hw2 = ConversationProjection.applyEvents hw1 page proj1
        Assert.Equal(proj1, proj2)
        Assert.Equal(hw1, hw2)
