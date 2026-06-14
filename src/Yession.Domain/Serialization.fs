namespace Yession.Domain

open System
open System.Globalization

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// A paired encoder/decoder for a single domain type. Serialization is an explicit
/// boundary concern: codecs are written by hand so private constructors are honoured and
/// the wire format never leaks into application logic. See docs/design.md §1 (Types
/// first) and §6.
type Codec<'a> =
    { Encode : 'a -> JsonValue
      Decode : Decoder<'a> }

module Codec =

    /// Lift a smart constructor into a decoder, failing the decode on rejected input.
    let private viaSmartCtor (create: 'raw -> Result<'a, string>) (raw: Decoder<'raw>) : Decoder<'a> =
        raw
        |> Decode.andThen (fun value ->
            match create value with
            | Ok v -> Decode.succeed v
            | Error e -> Decode.fail e)

    let sessionId : Codec<SessionId> =
        { Encode = SessionId.value >> Encode.string
          Decode = viaSmartCtor SessionId.create Decode.string }

    let peerId : Codec<PeerId> =
        { Encode = PeerId.value >> Encode.string
          Decode = viaSmartCtor PeerId.create Decode.string }

    let draftId : Codec<DraftId> =
        { Encode = DraftId.value >> Encode.string
          Decode = viaSmartCtor DraftId.create Decode.string }

    let messageId : Codec<MessageId> =
        { Encode = MessageId.value >> Encode.string
          Decode = viaSmartCtor MessageId.create Decode.string }

    let agentTurnId : Codec<AgentTurnId> =
        { Encode = AgentTurnId.value >> Encode.string
          Decode = viaSmartCtor AgentTurnId.create Decode.string }

    let eventId : Codec<EventId> =
        { Encode = EventId.value >> Encode.guid
          Decode = viaSmartCtor EventId.create Decode.guid }

    let requestId : Codec<RequestId> =
        { Encode = RequestId.value >> Encode.guid
          Decode = viaSmartCtor RequestId.create Decode.guid }

    let eventOffset : Codec<EventOffset> =
        { Encode = EventOffset.value >> Encode.int64
          Decode = viaSmartCtor EventOffset.create Decode.int64 }

    /// Timestamps are encoded as round-trippable ISO-8601 strings (offset preserved).
    let timestamp : Codec<DateTimeOffset> =
        { Encode = fun t -> Encode.string (t.ToString("o", CultureInfo.InvariantCulture))
          Decode =
            Decode.string
            |> Decode.andThen (fun s ->
                match DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
                | true, v -> Decode.succeed v
                | false, _ -> Decode.fail (sprintf "Invalid ISO-8601 timestamp: %s" s)) }

    let actor : Codec<ActorRef> =
        { Encode =
            (fun a ->
                match a with
                | HumanPeer p -> Encode.object [ "kind", Encode.string "humanPeer"; "peerId", peerId.Encode p ]
                | Agent -> Encode.object [ "kind", Encode.string "agent" ]
                | SessionProcess -> Encode.object [ "kind", Encode.string "sessionProcess" ]
                | System -> Encode.object [ "kind", Encode.string "system" ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (fun kind ->
                match kind with
                | "humanPeer" -> Decode.field "peerId" peerId.Decode |> Decode.map HumanPeer
                | "agent" -> Decode.succeed Agent
                | "sessionProcess" -> Decode.succeed SessionProcess
                | "system" -> Decode.succeed System
                | other -> Decode.fail (sprintf "Unknown actor kind: %s" other)) }

    let private sessionCreated : Codec<SessionCreated> =
        { Encode = fun (p: SessionCreated) -> Encode.object [ "sessionId", sessionId.Encode p.SessionId ]
          Decode =
            Decode.object (fun get ->
                { SessionCreated.SessionId = get.Required.Field "sessionId" sessionId.Decode }) }

    let sessionEvent : Codec<SessionEvent> =
        { Encode =
            (fun e ->
                match e with
                | SessionCreated p ->
                    Encode.object [ "type", Encode.string "sessionCreated"; "payload", sessionCreated.Encode p ])
          Decode =
            Decode.field "type" Decode.string
            |> Decode.andThen (fun t ->
                match t with
                | "sessionCreated" -> Decode.field "payload" sessionCreated.Decode |> Decode.map SessionCreated
                | other -> Decode.fail (sprintf "Unknown session event type: %s" other)) }

    /// Wrap any event codec into a codec for its envelope.
    let envelope (eventCodec: Codec<'event>) : Codec<EventEnvelope<'event>> =
        { Encode =
            (fun env ->
                Encode.object
                    [ "eventId", eventId.Encode env.EventId
                      "sessionId", sessionId.Encode env.SessionId
                      "offset", eventOffset.Encode env.Offset
                      "actor", actor.Encode env.Actor
                      "timestamp", timestamp.Encode env.Timestamp
                      "event", eventCodec.Encode env.Event ])
          Decode =
            Decode.object (fun get ->
                { EventId   = get.Required.Field "eventId" eventId.Decode
                  SessionId = get.Required.Field "sessionId" sessionId.Decode
                  Offset    = get.Required.Field "offset" eventOffset.Decode
                  Actor     = get.Required.Field "actor" actor.Decode
                  Timestamp = get.Required.Field "timestamp" timestamp.Decode
                  Event     = get.Required.Field "event" eventCodec.Decode }) }

    /// The canonical codec for a persisted session-event envelope.
    let sessionEventEnvelope : Codec<EventEnvelope<SessionEvent>> = envelope sessionEvent

    /// Serialize a value to a compact JSON string.
    let toString (codec: Codec<'a>) (value: 'a) : string =
        codec.Encode value |> Encode.toString 0

    /// Deserialize a value from a JSON string.
    let fromString (codec: Codec<'a>) (json: string) : Result<'a, string> =
        Decode.fromString codec.Decode json
