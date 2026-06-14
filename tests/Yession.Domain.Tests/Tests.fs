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
