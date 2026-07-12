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

    let private peerJoined : Codec<PeerJoined> =
        { Encode =
            fun (p: PeerJoined) ->
                Encode.object [ "peerId", peerId.Encode p.PeerId; "displayName", Encode.string p.DisplayName ]
          Decode =
            Decode.object (fun get ->
                { PeerJoined.PeerId = get.Required.Field "peerId" peerId.Decode
                  PeerJoined.DisplayName = get.Required.Field "displayName" Decode.string }) }

    let private peerLeft : Codec<PeerLeft> =
        { Encode = fun (p: PeerLeft) -> Encode.object [ "peerId", peerId.Encode p.PeerId ]
          Decode =
            Decode.object (fun get ->
                { PeerLeft.PeerId = get.Required.Field "peerId" peerId.Decode }) }

    let private draftStarted : Codec<DraftStarted> =
        { Encode =
            fun (p: DraftStarted) ->
                Encode.object [ "draftId", draftId.Encode p.DraftId; "startedBy", peerId.Encode p.StartedBy ]
          Decode =
            Decode.object (fun get ->
                { DraftStarted.DraftId = get.Required.Field "draftId" draftId.Decode
                  DraftStarted.StartedBy = get.Required.Field "startedBy" peerId.Decode }) }

    let private messageSent : Codec<MessageSent> =
        { Encode =
            fun (p: MessageSent) ->
                Encode.object
                    [ "messageId", messageId.Encode p.MessageId
                      "draftId", Encode.option draftId.Encode p.DraftId
                      "author", actor.Encode p.Author
                      "body", Encode.string p.Body ]
          Decode =
            Decode.object (fun get ->
                { MessageSent.MessageId = get.Required.Field "messageId" messageId.Decode
                  MessageSent.DraftId = get.Required.Field "draftId" (Decode.option draftId.Decode)
                  MessageSent.Author = get.Required.Field "author" actor.Decode
                  MessageSent.Body = get.Required.Field "body" Decode.string }) }

    let private agentTurnStarted : Codec<AgentTurnStarted> =
        { Encode =
            fun (p: AgentTurnStarted) ->
                Encode.object
                    [ "agentTurnId", agentTurnId.Encode p.AgentTurnId
                      "triggeredByMessageId", messageId.Encode p.TriggeredByMessageId ]
          Decode =
            Decode.object (fun get ->
                { AgentTurnStarted.AgentTurnId = get.Required.Field "agentTurnId" agentTurnId.Decode
                  AgentTurnStarted.TriggeredByMessageId = get.Required.Field "triggeredByMessageId" messageId.Decode }) }

    let private agentContextBuilt : Codec<AgentContextBuilt> =
        { Encode =
            fun (p: AgentContextBuilt) ->
                Encode.object
                    [ "agentTurnId", agentTurnId.Encode p.AgentTurnId
                      "messageCount", Encode.int p.MessageCount ]
          Decode =
            Decode.object (fun get ->
                { AgentContextBuilt.AgentTurnId = get.Required.Field "agentTurnId" agentTurnId.Decode
                  AgentContextBuilt.MessageCount = get.Required.Field "messageCount" Decode.int }) }

    let private agentMessageStarted : Codec<AgentMessageStarted> =
        { Encode =
            fun (p: AgentMessageStarted) ->
                Encode.object
                    [ "agentTurnId", agentTurnId.Encode p.AgentTurnId
                      "messageId", messageId.Encode p.MessageId ]
          Decode =
            Decode.object (fun get ->
                { AgentMessageStarted.AgentTurnId = get.Required.Field "agentTurnId" agentTurnId.Decode
                  AgentMessageStarted.MessageId = get.Required.Field "messageId" messageId.Decode }) }

    let private agentMessageDelta : Codec<AgentMessageDelta> =
        { Encode =
            fun (p: AgentMessageDelta) ->
                Encode.object
                    [ "agentTurnId", agentTurnId.Encode p.AgentTurnId
                      "messageId", messageId.Encode p.MessageId
                      "delta", Encode.string p.Delta ]
          Decode =
            Decode.object (fun get ->
                { AgentMessageDelta.AgentTurnId = get.Required.Field "agentTurnId" agentTurnId.Decode
                  AgentMessageDelta.MessageId = get.Required.Field "messageId" messageId.Decode
                  AgentMessageDelta.Delta = get.Required.Field "delta" Decode.string }) }

    let private agentMessageCompleted : Codec<AgentMessageCompleted> =
        { Encode =
            fun (p: AgentMessageCompleted) ->
                Encode.object
                    [ "agentTurnId", agentTurnId.Encode p.AgentTurnId
                      "messageId", messageId.Encode p.MessageId
                      "body", Encode.string p.Body ]
          Decode =
            Decode.object (fun get ->
                { AgentMessageCompleted.AgentTurnId = get.Required.Field "agentTurnId" agentTurnId.Decode
                  AgentMessageCompleted.MessageId = get.Required.Field "messageId" messageId.Decode
                  AgentMessageCompleted.Body = get.Required.Field "body" Decode.string }) }

    let private agentTurnFailed : Codec<AgentTurnFailed> =
        { Encode =
            fun (p: AgentTurnFailed) ->
                Encode.object
                    [ "agentTurnId", agentTurnId.Encode p.AgentTurnId
                      "reason", Encode.string p.Reason ]
          Decode =
            Decode.object (fun get ->
                { AgentTurnFailed.AgentTurnId = get.Required.Field "agentTurnId" agentTurnId.Decode
                  AgentTurnFailed.Reason = get.Required.Field "reason" Decode.string }) }

    let sessionEvent : Codec<SessionEvent> =
        { Encode =
            (fun e ->
                match e with
                | SessionCreated p ->
                    Encode.object [ "type", Encode.string "sessionCreated"; "payload", sessionCreated.Encode p ]
                | PeerJoined p ->
                    Encode.object [ "type", Encode.string "peerJoined"; "payload", peerJoined.Encode p ]
                | PeerLeft p ->
                    Encode.object [ "type", Encode.string "peerLeft"; "payload", peerLeft.Encode p ]
                | DraftStarted p ->
                    Encode.object [ "type", Encode.string "draftStarted"; "payload", draftStarted.Encode p ]
                | MessageSent p ->
                    Encode.object [ "type", Encode.string "messageSent"; "payload", messageSent.Encode p ]
                | AgentTurnStarted p ->
                    Encode.object [ "type", Encode.string "agentTurnStarted"; "payload", agentTurnStarted.Encode p ]
                | AgentContextBuilt p ->
                    Encode.object [ "type", Encode.string "agentContextBuilt"; "payload", agentContextBuilt.Encode p ]
                | AgentMessageStarted p ->
                    Encode.object [ "type", Encode.string "agentMessageStarted"; "payload", agentMessageStarted.Encode p ]
                | AgentMessageDelta p ->
                    Encode.object [ "type", Encode.string "agentMessageDelta"; "payload", agentMessageDelta.Encode p ]
                | AgentMessageCompleted p ->
                    Encode.object [ "type", Encode.string "agentMessageCompleted"; "payload", agentMessageCompleted.Encode p ]
                | AgentTurnFailed p ->
                    Encode.object [ "type", Encode.string "agentTurnFailed"; "payload", agentTurnFailed.Encode p ])
          Decode =
            Decode.field "type" Decode.string
            |> Decode.andThen (fun t ->
                match t with
                | "sessionCreated" -> Decode.field "payload" sessionCreated.Decode |> Decode.map SessionCreated
                | "peerJoined" -> Decode.field "payload" peerJoined.Decode |> Decode.map PeerJoined
                | "peerLeft" -> Decode.field "payload" peerLeft.Decode |> Decode.map PeerLeft
                | "draftStarted" -> Decode.field "payload" draftStarted.Decode |> Decode.map DraftStarted
                | "messageSent" -> Decode.field "payload" messageSent.Decode |> Decode.map MessageSent
                | "agentTurnStarted" -> Decode.field "payload" agentTurnStarted.Decode |> Decode.map AgentTurnStarted
                | "agentContextBuilt" -> Decode.field "payload" agentContextBuilt.Decode |> Decode.map AgentContextBuilt
                | "agentMessageStarted" -> Decode.field "payload" agentMessageStarted.Decode |> Decode.map AgentMessageStarted
                | "agentMessageDelta" -> Decode.field "payload" agentMessageDelta.Decode |> Decode.map AgentMessageDelta
                | "agentMessageCompleted" -> Decode.field "payload" agentMessageCompleted.Decode |> Decode.map AgentMessageCompleted
                | "agentTurnFailed" -> Decode.field "payload" agentTurnFailed.Decode |> Decode.map AgentTurnFailed
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

    /// A plain string codec, handy as the `'State` codec when exercising frames whose
    /// state payload is opaque to the transport.
    let string : Codec<string> =
        { Encode = Encode.string; Decode = Decode.string }

    /// An event page codec for any event codec.
    let eventPage (eventCodec: Codec<'event>) : Codec<EventPage<'event>> =
        let env = envelope eventCodec
        { Encode =
            (fun (p: EventPage<'event>) ->
                Encode.object
                    [ "events", p.Events |> List.map env.Encode |> Encode.list
                      "lastOffset", Encode.option eventOffset.Encode p.LastOffset
                      "isEnd", Encode.bool p.IsEnd ])
          Decode =
            Decode.object (fun get ->
                { Events = get.Required.Field "events" (Decode.list env.Decode)
                  LastOffset = get.Required.Field "lastOffset" (Decode.option eventOffset.Decode)
                  IsEnd = get.Required.Field "isEnd" Decode.bool }) }

    let sessionEventPage : Codec<EventPage<SessionEvent>> = eventPage sessionEvent

    let private sessionCommand : Codec<SessionCommand> =
        { Encode =
            (fun c ->
                match c with
                | StartDraft -> Encode.object [ "kind", Encode.string "startDraft" ]
                | SendDraft d -> Encode.object [ "kind", Encode.string "sendDraft"; "draftId", draftId.Encode d ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "startDraft" -> Decode.succeed StartDraft
                | "sendDraft" -> Decode.field "draftId" draftId.Decode |> Decode.map SendDraft
                | other -> Decode.fail (sprintf "Unknown session command: %s" other)) }

    let private sessionCommandResult : Codec<SessionCommandResult> =
        { Encode =
            (fun r ->
                match r with
                | CommandAccepted -> Encode.object [ "kind", Encode.string "accepted" ]
                | CommandRejected reason ->
                    Encode.object [ "kind", Encode.string "rejected"; "reason", Encode.string reason ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "accepted" -> Decode.succeed CommandAccepted
                | "rejected" -> Decode.field "reason" Decode.string |> Decode.map CommandRejected
                | other -> Decode.fail (sprintf "Unknown command result: %s" other)) }

    let private commandFrame : Codec<CommandFrame> =
        { Encode =
            (fun f ->
                match f with
                | Request (rid, cmd) ->
                    Encode.object
                        [ "kind", Encode.string "request"
                          "requestId", requestId.Encode rid
                          "command", sessionCommand.Encode cmd ]
                | Response (rid, res) ->
                    Encode.object
                        [ "kind", Encode.string "response"
                          "requestId", requestId.Encode rid
                          "result", sessionCommandResult.Encode res ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "request" ->
                    Decode.map2
                        (fun rid cmd -> Request(rid, cmd))
                        (Decode.field "requestId" requestId.Decode)
                        (Decode.field "command" sessionCommand.Decode)
                | "response" ->
                    Decode.map2
                        (fun rid res -> Response(rid, res))
                        (Decode.field "requestId" requestId.Decode)
                        (Decode.field "result" sessionCommandResult.Decode)
                | other -> Decode.fail (sprintf "Unknown command frame: %s" other)) }

    let private eventLogFrame : Codec<EventLogFrame> =
        { Encode =
            (fun f ->
                match f with
                | EventsAvailable off ->
                    Encode.object [ "kind", Encode.string "eventsAvailable"; "latestOffset", eventOffset.Encode off ]
                | ReadEventsAfter (rid, after, limit) ->
                    Encode.object
                        [ "kind", Encode.string "readEventsAfter"
                          "requestId", requestId.Encode rid
                          "after", Encode.option eventOffset.Encode after
                          "limit", Encode.int limit ]
                | EventsPage (rid, page) ->
                    Encode.object
                        [ "kind", Encode.string "eventsPage"
                          "requestId", requestId.Encode rid
                          "page", sessionEventPage.Encode page ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "eventsAvailable" ->
                    Decode.field "latestOffset" eventOffset.Decode |> Decode.map EventsAvailable
                | "readEventsAfter" ->
                    Decode.map3
                        (fun rid after limit -> ReadEventsAfter(rid, after, limit))
                        (Decode.field "requestId" requestId.Decode)
                        (Decode.field "after" (Decode.option eventOffset.Decode))
                        (Decode.field "limit" Decode.int)
                | "eventsPage" ->
                    Decode.map2
                        (fun rid page -> EventsPage(rid, page))
                        (Decode.field "requestId" requestId.Decode)
                        (Decode.field "page" sessionEventPage.Decode)
                | other -> Decode.fail (sprintf "Unknown event-log frame: %s" other)) }

    let private peerHello : Codec<PeerHelloPayload> =
        { Encode =
            (fun (p: PeerHelloPayload) ->
                Encode.object
                    [ "peerId", peerId.Encode p.PeerId
                      "displayName", Encode.string p.DisplayName
                      "token", Encode.string p.Token ])
          Decode =
            Decode.object (fun get ->
                { PeerHelloPayload.PeerId = get.Required.Field "peerId" peerId.Decode
                  PeerHelloPayload.DisplayName = get.Required.Field "displayName" Decode.string
                  PeerHelloPayload.Token = get.Required.Field "token" Decode.string }) }

    let private peerAccepted : Codec<PeerAcceptedPayload> =
        { Encode =
            (fun (p: PeerAcceptedPayload) ->
                Encode.object
                    [ "sessionId", sessionId.Encode p.SessionId
                      "assignedDisplayName", Encode.string p.AssignedDisplayName
                      "latestOffset", Encode.option eventOffset.Encode p.LatestOffset ])
          Decode =
            Decode.object (fun get ->
                { PeerAcceptedPayload.SessionId = get.Required.Field "sessionId" sessionId.Decode
                  PeerAcceptedPayload.AssignedDisplayName = get.Required.Field "assignedDisplayName" Decode.string
                  PeerAcceptedPayload.LatestOffset = get.Required.Field "latestOffset" (Decode.option eventOffset.Decode) }) }

    let private controlFrame : Codec<ControlFrame> =
        { Encode =
            (fun f ->
                match f with
                | PeerHello p -> Encode.object [ "kind", Encode.string "peerHello"; "payload", peerHello.Encode p ]
                | PeerAccepted p -> Encode.object [ "kind", Encode.string "peerAccepted"; "payload", peerAccepted.Encode p ]
                | PeerRejected reason -> Encode.object [ "kind", Encode.string "peerRejected"; "reason", Encode.string reason ]
                | Ping -> Encode.object [ "kind", Encode.string "ping" ]
                | Pong -> Encode.object [ "kind", Encode.string "pong" ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "peerHello" -> Decode.field "payload" peerHello.Decode |> Decode.map PeerHello
                | "peerAccepted" -> Decode.field "payload" peerAccepted.Decode |> Decode.map PeerAccepted
                | "peerRejected" -> Decode.field "reason" Decode.string |> Decode.map PeerRejected
                | "ping" -> Decode.succeed Ping
                | "pong" -> Decode.succeed Pong
                | other -> Decode.fail (sprintf "Unknown control frame: %s" other)) }

    let private stateFrame (stateCodec: Codec<'State>) : Codec<StateFrame<'State>> =
        { Encode = fun (StateSync s) -> Encode.object [ "kind", Encode.string "stateSync"; "state", stateCodec.Encode s ]
          Decode = Decode.field "state" stateCodec.Decode |> Decode.map StateSync }

    /// A frame codec for any `'State` codec. The transport never inspects the state
    /// payload; the state codec belongs to the sync-boundary layer (Step 05).
    let sessionFrame (stateCodec: Codec<'State>) : Codec<SessionFrame<'State>> =
        let sf = stateFrame stateCodec
        { Encode =
            (fun f ->
                match f with
                | State s -> Encode.object [ "tag", Encode.string "state"; "payload", sf.Encode s ]
                | Command c -> Encode.object [ "tag", Encode.string "command"; "payload", commandFrame.Encode c ]
                | EventLog e -> Encode.object [ "tag", Encode.string "eventLog"; "payload", eventLogFrame.Encode e ]
                | Control c -> Encode.object [ "tag", Encode.string "control"; "payload", controlFrame.Encode c ])
          Decode =
            Decode.field "tag" Decode.string
            |> Decode.andThen (function
                | "state" -> Decode.field "payload" sf.Decode |> Decode.map State
                | "command" -> Decode.field "payload" commandFrame.Decode |> Decode.map Command
                | "eventLog" -> Decode.field "payload" eventLogFrame.Decode |> Decode.map EventLog
                | "control" -> Decode.field "payload" controlFrame.Decode |> Decode.map Control
                | other -> Decode.fail (sprintf "Unknown session frame: %s" other)) }

    /// Serialize a value to a compact JSON string.
    let toString (codec: Codec<'a>) (value: 'a) : string =
        codec.Encode value |> Encode.toString 0

    /// Deserialize a value from a JSON string.
    let fromString (codec: Codec<'a>) (json: string) : Result<'a, string> =
        Decode.fromString codec.Decode json
