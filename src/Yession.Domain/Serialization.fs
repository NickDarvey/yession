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

    let queueId : Codec<QueueId> =
        { Encode = QueueId.value >> Encode.string
          Decode = viaSmartCtor QueueId.create Decode.string }

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

    let userId : Codec<UserId> =
        { Encode = UserId.value >> Encode.string
          Decode = viaSmartCtor UserId.create Decode.string }

    let actor : Codec<ActorRef> =
        { Encode =
            (fun a ->
                match a with
                | UserRef u -> Encode.object [ "kind", Encode.string "user"; "sub", userId.Encode u ]
                | PeerRef p -> Encode.object [ "kind", Encode.string "peer"; "peerId", peerId.Encode p ]
                | Agent -> Encode.object [ "kind", Encode.string "agent" ]
                | SessionProcess -> Encode.object [ "kind", Encode.string "sessionProcess" ]
                | System -> Encode.object [ "kind", Encode.string "system" ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (fun kind ->
                match kind with
                | "user" -> Decode.field "sub" userId.Decode |> Decode.map UserRef
                | "peer" -> Decode.field "peerId" peerId.Decode |> Decode.map PeerRef
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
                Encode.object
                    [ "peerId", peerId.Encode p.PeerId
                      "displayName", Encode.string p.DisplayName
                      "user", Encode.option userId.Encode p.User ]
          Decode =
            Decode.object (fun get ->
                { PeerJoined.PeerId = get.Required.Field "peerId" peerId.Decode
                  PeerJoined.DisplayName = get.Required.Field "displayName" Decode.string
                  PeerJoined.User = get.Optional.Field "user" userId.Decode }) }

    let private peerLeft : Codec<PeerLeft> =
        { Encode = fun (p: PeerLeft) -> Encode.object [ "peerId", peerId.Encode p.PeerId ]
          Decode =
            Decode.object (fun get ->
                { PeerLeft.PeerId = get.Required.Field "peerId" peerId.Decode }) }

    let private messageSent : Codec<MessageSent> =
        { Encode =
            fun (p: MessageSent) ->
                Encode.object
                    [ "messageId", messageId.Encode p.MessageId
                      "queueId", Encode.option queueId.Encode p.QueueId
                      "author", actor.Encode p.Author
                      "body", Encode.string p.Body ]
          Decode =
            Decode.object (fun get ->
                { MessageSent.MessageId = get.Required.Field "messageId" messageId.Decode
                  MessageSent.QueueId = get.Optional.Field "queueId" queueId.Decode
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

    let private agentTurnInterrupted : Codec<AgentTurnInterrupted> =
        { Encode =
            fun (p: AgentTurnInterrupted) ->
                Encode.object
                    [ "agentTurnId", agentTurnId.Encode p.AgentTurnId
                      "requestedBy", peerId.Encode p.RequestedBy ]
          Decode =
            Decode.object (fun get ->
                { AgentTurnInterrupted.AgentTurnId = get.Required.Field "agentTurnId" agentTurnId.Decode
                  AgentTurnInterrupted.RequestedBy = get.Required.Field "requestedBy" peerId.Decode }) }

    let private environmentNeedIdentified : Codec<EnvironmentNeedIdentified> =
        { Encode =
            fun (p: EnvironmentNeedIdentified) ->
                Encode.object
                    [ "reason", Encode.string p.Reason
                      "agentTurnId", Encode.option agentTurnId.Encode p.AgentTurnId ]
          Decode =
            Decode.object (fun get ->
                { EnvironmentNeedIdentified.Reason = get.Required.Field "reason" Decode.string
                  EnvironmentNeedIdentified.AgentTurnId = get.Required.Field "agentTurnId" (Decode.option agentTurnId.Decode) }) }

    let private environmentStartRequested : Codec<EnvironmentStartRequested> =
        { Encode =
            fun (p: EnvironmentStartRequested) ->
                Encode.object
                    [ "environmentId", Encode.string p.EnvironmentId
                      "specSummary", Encode.string p.SpecSummary ]
          Decode =
            Decode.object (fun get ->
                { EnvironmentStartRequested.EnvironmentId = get.Required.Field "environmentId" Decode.string
                  EnvironmentStartRequested.SpecSummary = get.Required.Field "specSummary" Decode.string }) }

    let private environmentStarted : Codec<EnvironmentStarted> =
        { Encode =
            fun (p: EnvironmentStarted) ->
                Encode.object
                    [ "environmentId", Encode.string p.EnvironmentId
                      "containerRef", Encode.string p.ContainerRef ]
          Decode =
            Decode.object (fun get ->
                { EnvironmentStarted.EnvironmentId = get.Required.Field "environmentId" Decode.string
                  EnvironmentStarted.ContainerRef = get.Required.Field "containerRef" Decode.string }) }

    let private environmentStartFailed : Codec<EnvironmentStartFailed> =
        { Encode =
            fun (p: EnvironmentStartFailed) ->
                Encode.object
                    [ "environmentId", Encode.string p.EnvironmentId
                      "reason", Encode.string p.Reason ]
          Decode =
            Decode.object (fun get ->
                { EnvironmentStartFailed.EnvironmentId = get.Required.Field "environmentId" Decode.string
                  EnvironmentStartFailed.Reason = get.Required.Field "reason" Decode.string }) }

    let private environmentStopRequested : Codec<EnvironmentStopRequested> =
        { Encode =
            fun (p: EnvironmentStopRequested) ->
                Encode.object [ "environmentId", Encode.string p.EnvironmentId ]
          Decode =
            Decode.object (fun get ->
                { EnvironmentStopRequested.EnvironmentId = get.Required.Field "environmentId" Decode.string }) }

    let private environmentStopped : Codec<EnvironmentStopped> =
        { Encode =
            fun (p: EnvironmentStopped) ->
                Encode.object [ "environmentId", Encode.string p.EnvironmentId ]
          Decode =
            Decode.object (fun get ->
                { EnvironmentStopped.EnvironmentId = get.Required.Field "environmentId" Decode.string }) }

    let commandId : Codec<CommandId> =
        { Encode = CommandId.value >> Encode.string
          Decode = viaSmartCtor CommandId.create Decode.string }

    let outputStream : Codec<OutputStream> =
        { Encode =
            (fun s -> Encode.string (match s with Stdout -> "stdout" | Stderr -> "stderr"))
          Decode =
            Decode.string
            |> Decode.andThen (function
                | "stdout" -> Decode.succeed Stdout
                | "stderr" -> Decode.succeed Stderr
                | other -> Decode.fail (sprintf "Unknown output stream: %s" other)) }

    let commandResult : Codec<CommandResult> =
        { Encode =
            (fun r ->
                match r with
                | CommandSucceeded code -> Encode.object [ "kind", Encode.string "succeeded"; "exitCode", Encode.int code ]
                | CommandFailed code -> Encode.object [ "kind", Encode.string "failed"; "exitCode", Encode.int code ]
                | CommandTimedOut -> Encode.object [ "kind", Encode.string "timedOut" ]
                | CommandExecutionFailed reason ->
                    Encode.object [ "kind", Encode.string "executionFailed"; "reason", Encode.string reason ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "succeeded" -> Decode.field "exitCode" Decode.int |> Decode.map CommandSucceeded
                | "failed" -> Decode.field "exitCode" Decode.int |> Decode.map CommandFailed
                | "timedOut" -> Decode.succeed CommandTimedOut
                | "executionFailed" -> Decode.field "reason" Decode.string |> Decode.map CommandExecutionFailed
                | other -> Decode.fail (sprintf "Unknown command result: %s" other)) }

    let private commandRequested : Codec<CommandRequested> =
        { Encode =
            fun (p: CommandRequested) ->
                Encode.object
                    [ "commandId", commandId.Encode p.CommandId
                      "executable", Encode.string p.Executable
                      "arguments", p.Arguments |> List.map Encode.string |> Encode.list ]
          Decode =
            Decode.object (fun get ->
                { CommandRequested.CommandId = get.Required.Field "commandId" commandId.Decode
                  CommandRequested.Executable = get.Required.Field "executable" Decode.string
                  CommandRequested.Arguments = get.Required.Field "arguments" (Decode.list Decode.string) }) }

    let private commandStarted : Codec<CommandStarted> =
        { Encode = fun (p: CommandStarted) -> Encode.object [ "commandId", commandId.Encode p.CommandId ]
          Decode =
            Decode.object (fun get ->
                { CommandStarted.CommandId = get.Required.Field "commandId" commandId.Decode }) }

    let private commandOutputReceived : Codec<CommandOutputReceived> =
        { Encode =
            fun (p: CommandOutputReceived) ->
                Encode.object
                    [ "commandId", commandId.Encode p.CommandId
                      "stream", outputStream.Encode p.Stream
                      "text", Encode.string p.Text ]
          Decode =
            Decode.object (fun get ->
                { CommandOutputReceived.CommandId = get.Required.Field "commandId" commandId.Decode
                  CommandOutputReceived.Stream = get.Required.Field "stream" outputStream.Decode
                  CommandOutputReceived.Text = get.Required.Field "text" Decode.string }) }

    let private commandCompleted : Codec<CommandCompleted> =
        { Encode =
            fun (p: CommandCompleted) ->
                Encode.object
                    [ "commandId", commandId.Encode p.CommandId
                      "result", commandResult.Encode p.Result ]
          Decode =
            Decode.object (fun get ->
                { CommandCompleted.CommandId = get.Required.Field "commandId" commandId.Decode
                  CommandCompleted.Result = get.Required.Field "result" commandResult.Decode }) }

    let terminalId : Codec<TerminalId> =
        { Encode = TerminalId.value >> Encode.string
          Decode = viaSmartCtor TerminalId.create Decode.string }

    let blockId : Codec<BlockId> =
        { Encode = BlockId.value >> Encode.string
          Decode = viaSmartCtor BlockId.create Decode.string }

    let private terminalOpened : Codec<TerminalOpened> =
        { Encode =
            fun (p: TerminalOpened) ->
                Encode.object
                    [ "terminalId", terminalId.Encode p.TerminalId
                      "openedBy", actor.Encode p.OpenedBy
                      "title", Encode.string p.Title ]
          Decode =
            Decode.object (fun get ->
                { TerminalOpened.TerminalId = get.Required.Field "terminalId" terminalId.Decode
                  TerminalOpened.OpenedBy = get.Required.Field "openedBy" actor.Decode
                  TerminalOpened.Title = get.Required.Field "title" Decode.string }) }

    let private terminalClosed : Codec<TerminalClosed> =
        { Encode =
            fun (p: TerminalClosed) ->
                Encode.object
                    [ "terminalId", terminalId.Encode p.TerminalId
                      "reason", Encode.string p.Reason ]
          Decode =
            Decode.object (fun get ->
                { TerminalClosed.TerminalId = get.Required.Field "terminalId" terminalId.Decode
                  TerminalClosed.Reason = get.Required.Field "reason" Decode.string }) }

    let private terminalBlockStarted : Codec<TerminalBlockStarted> =
        { Encode =
            fun (p: TerminalBlockStarted) ->
                Encode.object
                    [ "terminalId", terminalId.Encode p.TerminalId
                      "blockId", blockId.Encode p.BlockId
                      "queueId", Encode.option queueId.Encode p.QueueId
                      "author", actor.Encode p.Author
                      "approvedBy", Encode.option actor.Encode p.ApprovedBy
                      "command", Encode.string p.Command
                      "fromSeq", Encode.int p.FromSeq ]
          Decode =
            Decode.object (fun get ->
                { TerminalBlockStarted.TerminalId = get.Required.Field "terminalId" terminalId.Decode
                  TerminalBlockStarted.BlockId = get.Required.Field "blockId" blockId.Decode
                  TerminalBlockStarted.QueueId = get.Required.Field "queueId" (Decode.option queueId.Decode)
                  TerminalBlockStarted.Author = get.Required.Field "author" actor.Decode
                  TerminalBlockStarted.ApprovedBy = get.Required.Field "approvedBy" (Decode.option actor.Decode)
                  TerminalBlockStarted.Command = get.Required.Field "command" Decode.string
                  TerminalBlockStarted.FromSeq = get.Required.Field "fromSeq" Decode.int }) }

    let private terminalBlockCompleted : Codec<TerminalBlockCompleted> =
        { Encode =
            fun (p: TerminalBlockCompleted) ->
                Encode.object
                    [ "terminalId", terminalId.Encode p.TerminalId
                      "blockId", blockId.Encode p.BlockId
                      "result", commandResult.Encode p.Result
                      "toSeq", Encode.int p.ToSeq ]
          Decode =
            Decode.object (fun get ->
                { TerminalBlockCompleted.TerminalId = get.Required.Field "terminalId" terminalId.Decode
                  TerminalBlockCompleted.BlockId = get.Required.Field "blockId" blockId.Decode
                  TerminalBlockCompleted.Result = get.Required.Field "result" commandResult.Decode
                  TerminalBlockCompleted.ToSeq = get.Required.Field "toSeq" Decode.int }) }

    let private leaseEnd : Codec<TerminalLeaseEnd> =
        { Encode =
            (fun e ->
                match e with
                | LeaseReleased -> Encode.object [ "kind", Encode.string "released" ]
                | LeaseStolen by -> Encode.object [ "kind", Encode.string "stolen"; "by", actor.Encode by ]
                | LeaseHolderGone -> Encode.object [ "kind", Encode.string "holderGone" ]
                | LeaseIdle -> Encode.object [ "kind", Encode.string "idle" ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "released" -> Decode.succeed LeaseReleased
                | "stolen" -> Decode.field "by" actor.Decode |> Decode.map LeaseStolen
                | "holderGone" -> Decode.succeed LeaseHolderGone
                | "idle" -> Decode.succeed LeaseIdle
                | other -> Decode.fail (sprintf "Unknown lease end: %s" other)) }

    let private terminalLeaseTaken : Codec<TerminalLeaseTaken> =
        { Encode =
            fun (p: TerminalLeaseTaken) ->
                Encode.object [ "terminalId", terminalId.Encode p.TerminalId; "by", actor.Encode p.By ]
          Decode =
            Decode.object (fun get ->
                { TerminalLeaseTaken.TerminalId = get.Required.Field "terminalId" terminalId.Decode
                  TerminalLeaseTaken.By = get.Required.Field "by" actor.Decode }) }

    let private terminalLeaseReleased : Codec<TerminalLeaseReleased> =
        { Encode =
            fun (p: TerminalLeaseReleased) ->
                Encode.object
                    [ "terminalId", terminalId.Encode p.TerminalId
                      "was", actor.Encode p.Was
                      "reason", leaseEnd.Encode p.Reason ]
          Decode =
            Decode.object (fun get ->
                { TerminalLeaseReleased.TerminalId = get.Required.Field "terminalId" terminalId.Decode
                  TerminalLeaseReleased.Was = get.Required.Field "was" actor.Decode
                  TerminalLeaseReleased.Reason = get.Required.Field "reason" leaseEnd.Decode }) }

    let private terminalCommandRejected : Codec<TerminalCommandRejected> =
        { Encode =
            fun (p: TerminalCommandRejected) ->
                Encode.object
                    [ "terminalId", terminalId.Encode p.TerminalId
                      "queueId", queueId.Encode p.QueueId
                      "blockId", blockId.Encode p.BlockId
                      "author", actor.Encode p.Author
                      "rejectedBy", actor.Encode p.RejectedBy
                      "command", Encode.string p.Command
                      "reason", Encode.option Encode.string p.Reason ]
          Decode =
            Decode.object (fun get ->
                { TerminalCommandRejected.TerminalId = get.Required.Field "terminalId" terminalId.Decode
                  TerminalCommandRejected.QueueId = get.Required.Field "queueId" queueId.Decode
                  TerminalCommandRejected.BlockId = get.Required.Field "blockId" blockId.Decode
                  TerminalCommandRejected.Author = get.Required.Field "author" actor.Decode
                  TerminalCommandRejected.RejectedBy = get.Required.Field "rejectedBy" actor.Decode
                  TerminalCommandRejected.Command = get.Required.Field "command" Decode.string
                  TerminalCommandRejected.Reason = get.Required.Field "reason" (Decode.option Decode.string) }) }

    let private terminalTranscriptTruncated : Codec<TerminalTranscriptTruncated> =
        { Encode =
            fun (p: TerminalTranscriptTruncated) ->
                Encode.object
                    [ "terminalId", terminalId.Encode p.TerminalId
                      "blockId", Encode.option blockId.Encode p.BlockId
                      "droppedBytes", Encode.int p.DroppedBytes ]
          Decode =
            Decode.object (fun get ->
                { TerminalTranscriptTruncated.TerminalId = get.Required.Field "terminalId" terminalId.Decode
                  TerminalTranscriptTruncated.BlockId = get.Required.Field "blockId" (Decode.option blockId.Decode)
                  TerminalTranscriptTruncated.DroppedBytes = get.Required.Field "droppedBytes" Decode.int }) }

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
                    Encode.object [ "type", Encode.string "agentTurnFailed"; "payload", agentTurnFailed.Encode p ]
                | AgentTurnInterrupted p ->
                    Encode.object [ "type", Encode.string "agentTurnInterrupted"; "payload", agentTurnInterrupted.Encode p ]
                | EnvironmentNeedIdentified p ->
                    Encode.object [ "type", Encode.string "environmentNeedIdentified"; "payload", environmentNeedIdentified.Encode p ]
                | EnvironmentStartRequested p ->
                    Encode.object [ "type", Encode.string "environmentStartRequested"; "payload", environmentStartRequested.Encode p ]
                | EnvironmentStarted p ->
                    Encode.object [ "type", Encode.string "environmentStarted"; "payload", environmentStarted.Encode p ]
                | EnvironmentStartFailed p ->
                    Encode.object [ "type", Encode.string "environmentStartFailed"; "payload", environmentStartFailed.Encode p ]
                | EnvironmentStopRequested p ->
                    Encode.object [ "type", Encode.string "environmentStopRequested"; "payload", environmentStopRequested.Encode p ]
                | EnvironmentStopped p ->
                    Encode.object [ "type", Encode.string "environmentStopped"; "payload", environmentStopped.Encode p ]
                | CommandRequested p ->
                    Encode.object [ "type", Encode.string "commandRequested"; "payload", commandRequested.Encode p ]
                | CommandStarted p ->
                    Encode.object [ "type", Encode.string "commandStarted"; "payload", commandStarted.Encode p ]
                | CommandOutputReceived p ->
                    Encode.object [ "type", Encode.string "commandOutputReceived"; "payload", commandOutputReceived.Encode p ]
                | CommandCompleted p ->
                    Encode.object [ "type", Encode.string "commandCompleted"; "payload", commandCompleted.Encode p ]
                | TerminalOpened p ->
                    Encode.object [ "type", Encode.string "terminalOpened"; "payload", terminalOpened.Encode p ]
                | TerminalClosed p ->
                    Encode.object [ "type", Encode.string "terminalClosed"; "payload", terminalClosed.Encode p ]
                | TerminalBlockStarted p ->
                    Encode.object [ "type", Encode.string "terminalBlockStarted"; "payload", terminalBlockStarted.Encode p ]
                | TerminalBlockCompleted p ->
                    Encode.object [ "type", Encode.string "terminalBlockCompleted"; "payload", terminalBlockCompleted.Encode p ]
                | TerminalCommandRejected p ->
                    Encode.object [ "type", Encode.string "terminalCommandRejected"; "payload", terminalCommandRejected.Encode p ]
                | TerminalLeaseTaken p ->
                    Encode.object [ "type", Encode.string "terminalLeaseTaken"; "payload", terminalLeaseTaken.Encode p ]
                | TerminalLeaseReleased p ->
                    Encode.object [ "type", Encode.string "terminalLeaseReleased"; "payload", terminalLeaseReleased.Encode p ]
                | TerminalTranscriptTruncated p ->
                    Encode.object [ "type", Encode.string "terminalTranscriptTruncated"; "payload", terminalTranscriptTruncated.Encode p ])
          Decode =
            Decode.field "type" Decode.string
            |> Decode.andThen (fun t ->
                match t with
                | "sessionCreated" -> Decode.field "payload" sessionCreated.Decode |> Decode.map SessionCreated
                | "peerJoined" -> Decode.field "payload" peerJoined.Decode |> Decode.map PeerJoined
                | "peerLeft" -> Decode.field "payload" peerLeft.Decode |> Decode.map PeerLeft
                | "messageSent" -> Decode.field "payload" messageSent.Decode |> Decode.map MessageSent
                | "agentTurnStarted" -> Decode.field "payload" agentTurnStarted.Decode |> Decode.map AgentTurnStarted
                | "agentContextBuilt" -> Decode.field "payload" agentContextBuilt.Decode |> Decode.map AgentContextBuilt
                | "agentMessageStarted" -> Decode.field "payload" agentMessageStarted.Decode |> Decode.map AgentMessageStarted
                | "agentMessageDelta" -> Decode.field "payload" agentMessageDelta.Decode |> Decode.map AgentMessageDelta
                | "agentMessageCompleted" -> Decode.field "payload" agentMessageCompleted.Decode |> Decode.map AgentMessageCompleted
                | "agentTurnFailed" -> Decode.field "payload" agentTurnFailed.Decode |> Decode.map AgentTurnFailed
                | "agentTurnInterrupted" -> Decode.field "payload" agentTurnInterrupted.Decode |> Decode.map AgentTurnInterrupted
                | "environmentNeedIdentified" -> Decode.field "payload" environmentNeedIdentified.Decode |> Decode.map EnvironmentNeedIdentified
                | "environmentStartRequested" -> Decode.field "payload" environmentStartRequested.Decode |> Decode.map EnvironmentStartRequested
                | "environmentStarted" -> Decode.field "payload" environmentStarted.Decode |> Decode.map EnvironmentStarted
                | "environmentStartFailed" -> Decode.field "payload" environmentStartFailed.Decode |> Decode.map EnvironmentStartFailed
                | "environmentStopRequested" -> Decode.field "payload" environmentStopRequested.Decode |> Decode.map EnvironmentStopRequested
                | "environmentStopped" -> Decode.field "payload" environmentStopped.Decode |> Decode.map EnvironmentStopped
                | "commandRequested" -> Decode.field "payload" commandRequested.Decode |> Decode.map CommandRequested
                | "commandStarted" -> Decode.field "payload" commandStarted.Decode |> Decode.map CommandStarted
                | "commandOutputReceived" -> Decode.field "payload" commandOutputReceived.Decode |> Decode.map CommandOutputReceived
                | "commandCompleted" -> Decode.field "payload" commandCompleted.Decode |> Decode.map CommandCompleted
                | "terminalOpened" -> Decode.field "payload" terminalOpened.Decode |> Decode.map TerminalOpened
                | "terminalClosed" -> Decode.field "payload" terminalClosed.Decode |> Decode.map TerminalClosed
                | "terminalBlockStarted" -> Decode.field "payload" terminalBlockStarted.Decode |> Decode.map TerminalBlockStarted
                | "terminalBlockCompleted" -> Decode.field "payload" terminalBlockCompleted.Decode |> Decode.map TerminalBlockCompleted
                | "terminalCommandRejected" -> Decode.field "payload" terminalCommandRejected.Decode |> Decode.map TerminalCommandRejected
                | "terminalLeaseTaken" -> Decode.field "payload" terminalLeaseTaken.Decode |> Decode.map TerminalLeaseTaken
                | "terminalLeaseReleased" -> Decode.field "payload" terminalLeaseReleased.Decode |> Decode.map TerminalLeaseReleased
                | "terminalTranscriptTruncated" ->
                    Decode.field "payload" terminalTranscriptTruncated.Decode |> Decode.map TerminalTranscriptTruncated
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

    /// One asciicast line. Deliberately NOT this file's usual tagged-object shape: the
    /// format is asciinema's, and matching it exactly is the point — a transcript is only
    /// worth calling an audit artifact if something other than Yession can read it. So the
    /// header is a bare object with `version: 2` and a record is a bare three-element
    /// array, `[time, code, data]`.
    let transcriptLine : Codec<TranscriptLine> =
        let encodeHeader (h: TranscriptHeader) =
            Encode.object
                [ "version", Encode.int 2
                  "width", Encode.int h.Width
                  "height", Encode.int h.Height
                  "timestamp", Encode.int64 h.Timestamp ]
        let encodeRecord (r: TranscriptRecord) =
            [ Encode.float r.At; Encode.string (TranscriptKind.code r.Kind); Encode.string r.Data ]
            |> Encode.list
        let decodeHeader : Decoder<TranscriptLine> =
            Decode.object (fun get ->
                { Width = get.Required.Field "width" Decode.int
                  Height = get.Required.Field "height" Decode.int
                  Timestamp = get.Required.Field "timestamp" Decode.int64 })
            |> Decode.map TranscriptHeaderLine
        let decodeRecord : Decoder<TranscriptLine> =
            Decode.map3
                (fun at code data -> at, code, data)
                (Decode.index 0 Decode.float)
                (Decode.index 1 Decode.string)
                (Decode.index 2 Decode.string)
            |> Decode.andThen (fun (at, code, data) ->
                match TranscriptKind.parse code with
                | Some kind -> Decode.succeed (TranscriptRecordLine { At = at; Kind = kind; Data = data })
                | None -> Decode.fail (sprintf "Unknown transcript record kind: %s" code))
        { Encode =
            (fun line ->
                match line with
                | TranscriptHeaderLine h -> encodeHeader h
                | TranscriptRecordLine r -> encodeRecord r)
          // The header is tried first because it is the one shape with a discriminator of
          // its own; a record is anything array-shaped.
          Decode = Decode.oneOf [ decodeHeader; decodeRecord ] }

    let transcriptRecord : Codec<TranscriptRecord> =
        { Encode = fun r -> transcriptLine.Encode (TranscriptRecordLine r)
          Decode =
            transcriptLine.Decode
            |> Decode.andThen (function
                | TranscriptRecordLine r -> Decode.succeed r
                | TranscriptHeaderLine _ -> Decode.fail "expected a transcript record, found the header") }

    let private terminalFrame : Codec<TerminalFrame> =
        { Encode =
            (fun f ->
                match f with
                | TerminalRecord (id, seq, record) ->
                    Encode.object
                        [ "kind", Encode.string "record"
                          "terminalId", terminalId.Encode id
                          "seq", Encode.int seq
                          "record", transcriptRecord.Encode record ]
                | TerminalTranscriptAvailable (id, nextSeq) ->
                    Encode.object
                        [ "kind", Encode.string "available"
                          "terminalId", terminalId.Encode id
                          "nextSeq", Encode.int nextSeq ]
                | TerminalSnapshot (id, seq, screen) ->
                    Encode.object
                        [ "kind", Encode.string "snapshot"
                          "terminalId", terminalId.Encode id
                          "seq", Encode.int seq
                          "screen", Encode.string screen ]
                | TerminalInput (id, data) ->
                    Encode.object
                        [ "kind", Encode.string "input"
                          "terminalId", terminalId.Encode id
                          "data", Encode.string data ]
                | TerminalResize (id, cols, rows) ->
                    Encode.object
                        [ "kind", Encode.string "resize"
                          "terminalId", terminalId.Encode id
                          "cols", Encode.int cols
                          "rows", Encode.int rows ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "record" ->
                    Decode.map3
                        (fun id seq record -> TerminalRecord (id, seq, record))
                        (Decode.field "terminalId" terminalId.Decode)
                        (Decode.field "seq" Decode.int)
                        (Decode.field "record" transcriptRecord.Decode)
                | "available" ->
                    Decode.map2
                        (fun id nextSeq -> TerminalTranscriptAvailable (id, nextSeq))
                        (Decode.field "terminalId" terminalId.Decode)
                        (Decode.field "nextSeq" Decode.int)
                | "snapshot" ->
                    Decode.map3
                        (fun id seq screen -> TerminalSnapshot (id, seq, screen))
                        (Decode.field "terminalId" terminalId.Decode)
                        (Decode.field "seq" Decode.int)
                        (Decode.field "screen" Decode.string)
                | "input" ->
                    Decode.map2
                        (fun id data -> TerminalInput (id, data))
                        (Decode.field "terminalId" terminalId.Decode)
                        (Decode.field "data" Decode.string)
                | "resize" ->
                    Decode.map3
                        (fun id cols rows -> TerminalResize (id, cols, rows))
                        (Decode.field "terminalId" terminalId.Decode)
                        (Decode.field "cols" Decode.int)
                        (Decode.field "rows" Decode.int)
                | other -> Decode.fail (sprintf "Unknown terminal frame: %s" other)) }

    let private sessionCommand : Codec<SessionCommand> =
        { Encode =
            (fun c ->
                match c with
                | InterruptAgentTurn t ->
                    Encode.object [ "kind", Encode.string "interruptAgentTurn"; "agentTurnId", agentTurnId.Encode t ]
                | OpenTerminal title ->
                    Encode.object [ "kind", Encode.string "openTerminal"; "title", Encode.string title ]
                | CloseTerminal id ->
                    Encode.object [ "kind", Encode.string "closeTerminal"; "terminalId", terminalId.Encode id ]
                | TakeTerminalLease id ->
                    Encode.object [ "kind", Encode.string "takeTerminalLease"; "terminalId", terminalId.Encode id ]
                | ReleaseTerminalLease id ->
                    Encode.object
                        [ "kind", Encode.string "releaseTerminalLease"; "terminalId", terminalId.Encode id ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "interruptAgentTurn" -> Decode.field "agentTurnId" agentTurnId.Decode |> Decode.map InterruptAgentTurn
                | "openTerminal" -> Decode.field "title" Decode.string |> Decode.map OpenTerminal
                | "closeTerminal" -> Decode.field "terminalId" terminalId.Decode |> Decode.map CloseTerminal
                | "takeTerminalLease" -> Decode.field "terminalId" terminalId.Decode |> Decode.map TakeTerminalLease
                | "releaseTerminalLease" ->
                    Decode.field "terminalId" terminalId.Decode |> Decode.map ReleaseTerminalLease
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

    let private focusField : Codec<FocusField> =
        { Encode =
            (fun f ->
                match f with
                | Title -> Encode.object [ "kind", Encode.string "title" ]
                | DraftBody p -> Encode.object [ "kind", Encode.string "draft"; "peerId", peerId.Encode p ]
                | QueueBody q -> Encode.object [ "kind", Encode.string "queue"; "queueId", queueId.Encode q ]
                | TerminalDraftBody (t, p) ->
                    Encode.object
                        [ "kind", Encode.string "terminalDraft"
                          "terminalId", terminalId.Encode t
                          "peerId", peerId.Encode p ]
                | TerminalQueuedBody q ->
                    Encode.object [ "kind", Encode.string "terminalQueued"; "queueId", queueId.Encode q ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "title" -> Decode.succeed Title
                | "draft" -> Decode.field "peerId" peerId.Decode |> Decode.map DraftBody
                | "queue" -> Decode.field "queueId" queueId.Decode |> Decode.map QueueBody
                | "terminalDraft" ->
                    Decode.map2
                        (fun t p -> TerminalDraftBody (t, p))
                        (Decode.field "terminalId" terminalId.Decode)
                        (Decode.field "peerId" peerId.Decode)
                | "terminalQueued" -> Decode.field "queueId" queueId.Decode |> Decode.map TerminalQueuedBody
                | other -> Decode.fail (sprintf "Unknown focus field: %s" other)) }

    let private cursorPos : Codec<CursorPos> =
        { Encode = fun (p: CursorPos) -> Encode.object [ "anchor", Encode.string p.Anchor; "head", Encode.string p.Head ]
          Decode =
            Decode.object (fun get ->
                { Anchor = get.Required.Field "anchor" Decode.string
                  Head = get.Required.Field "head" Decode.string }) }

    let private focus : Codec<Focus> =
        { Encode = fun (f: Focus) -> Encode.object [ "field", focusField.Encode f.Field; "pos", cursorPos.Encode f.Pos ]
          Decode =
            Decode.object (fun get ->
                { Field = get.Required.Field "field" focusField.Decode
                  Pos = get.Required.Field "pos" cursorPos.Decode }) }

    let private presencePayload : Codec<PresencePayload> =
        { Encode =
            (fun (p: PresencePayload) ->
                Encode.object
                    [ "peerId", peerId.Encode p.PeerId
                      "displayName", Encode.string p.DisplayName
                      "focus", Encode.option focus.Encode p.Focus ])
          Decode =
            Decode.object (fun get ->
                { PresencePayload.PeerId = get.Required.Field "peerId" peerId.Decode
                  PresencePayload.DisplayName = get.Required.Field "displayName" Decode.string
                  PresencePayload.Focus = get.Required.Field "focus" (Decode.option focus.Decode) }) }

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
                | Control c -> Encode.object [ "tag", Encode.string "control"; "payload", controlFrame.Encode c ]
                | Presence p -> Encode.object [ "tag", Encode.string "presence"; "payload", presencePayload.Encode p ]
                | Terminal t -> Encode.object [ "tag", Encode.string "terminal"; "payload", terminalFrame.Encode t ])
          Decode =
            Decode.field "tag" Decode.string
            |> Decode.andThen (function
                | "state" -> Decode.field "payload" sf.Decode |> Decode.map State
                | "command" -> Decode.field "payload" commandFrame.Decode |> Decode.map Command
                | "eventLog" -> Decode.field "payload" eventLogFrame.Decode |> Decode.map EventLog
                | "control" -> Decode.field "payload" controlFrame.Decode |> Decode.map Control
                | "presence" -> Decode.field "payload" presencePayload.Decode |> Decode.map Presence
                | "terminal" -> Decode.field "payload" terminalFrame.Decode |> Decode.map Terminal
                | other -> Decode.fail (sprintf "Unknown session frame: %s" other)) }

    /// Serialize a value to a compact JSON string.
    let toString (codec: Codec<'a>) (value: 'a) : string =
        codec.Encode value |> Encode.toString 0

    /// Deserialize a value from a JSON string.
    let fromString (codec: Codec<'a>) (json: string) : Result<'a, string> =
        Decode.fromString codec.Decode json
