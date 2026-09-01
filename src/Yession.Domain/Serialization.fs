namespace Yession.Domain

open Yession.Domain.Chat
open Yession.Domain.Sandboxes
open Yession.Domain.Repos
open Yession.Domain.Prs
open System
open Yession.Domain.Agent
open Yession.Domain.Link
open Yession.Domain.Terminals
open Yession.Domain.Tools
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

    let sandboxName : Codec<SandboxName> =
        { Encode = SandboxName.value >> Encode.string
          Decode = viaSmartCtor SandboxName.create Decode.string }

    /// A sandbox as the log spells it. `render`/`parse` are each other's inverse and a
    /// session-owned ref renders to the bare name, so this reads every log ever written
    /// without a compatibility branch.
    let private sandboxRef : Codec<SandboxRef> =
        { Encode = fun (r: SandboxRef) -> Encode.string (SandboxRef.render r)
          Decode =
            Decode.string
            |> Decode.andThen (fun raw ->
                match SandboxRef.parse raw with
                | Ok r -> Decode.succeed r
                | Error e -> Decode.fail e) }

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

    let repoRef : Codec<RepoRef> =
        { Encode = RepoRef.value >> Encode.string
          Decode = viaSmartCtor RepoRef.create Decode.string }

    let actor : Codec<ActorRef> =
        { Encode =
            (fun a ->
                match a with
                | UserRef u -> Encode.object [ "kind", Encode.string "user"; "sub", userId.Encode u ]
                | PeerRef p -> Encode.object [ "kind", Encode.string "peer"; "peerId", peerId.Encode p ]
                | Agent -> Encode.object [ "kind", Encode.string "agent" ]
                | SessionProcess -> Encode.object [ "kind", Encode.string "sessionProcess" ]
                | System -> Encode.object [ "kind", Encode.string "system" ]
                | Configured repo ->
                    Encode.object [ "kind", Encode.string "configured"; "repo", repoRef.Encode repo ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (fun kind ->
                match kind with
                | "user" -> Decode.field "sub" userId.Decode |> Decode.map UserRef
                | "peer" -> Decode.field "peerId" peerId.Decode |> Decode.map PeerRef
                | "agent" -> Decode.succeed Agent
                | "sessionProcess" -> Decode.succeed SessionProcess
                | "system" -> Decode.succeed System
                | "configured" -> Decode.field "repo" repoRef.Decode |> Decode.map Configured
                | other -> Decode.fail (sprintf "Unknown actor kind: %s" other)) }

    let terminalId : Codec<TerminalId> =
        { Encode = TerminalId.value >> Encode.string
          Decode = viaSmartCtor TerminalId.create Decode.string }

    let terminalTitle : Codec<TerminalTitle> =
        { Encode = TerminalTitle.value >> Encode.string
          Decode = viaSmartCtor TerminalTitle.create Decode.string }

    /// The three parties behind an act, on the wire (Plan 20). Not a nested object: these
    /// keys sit at the payload's top level and always have, and an event log is read back for
    /// the life of its session — so what changed is where the value lives in F#, and nothing
    /// about what is written. One pair of helpers rather than a spelling per event, which is
    /// how the three came to disagree in the first place.
    let private authorityFields (authority: Authority) =
        [ "author", actor.Encode (Authority.author authority)
          "onBehalfOf", Encode.option actor.Encode (Authority.onBehalfOf authority) ]

    /// Recovered, never authored — `rehydrate`'s reason. `onBehalfOf` is optional on the way
    /// in because events written before Plan 20 have no such key, and a `Required` field would
    /// make those pages undecodable, which is a session that will not open. Keys this stopped
    /// asking for (`approvedBy`, Plan 23) are simply ignored where old events still carry
    /// them — the property the pinned legacy-JSON tests hold.
    let private authorityOf (get: Decode.IGetters) : Authority =
        Authority.rehydrate
            (get.Required.Field "author" actor.Decode)
            (get.Optional.Field "onBehalfOf" (Decode.option actor.Decode) |> Option.flatten)

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

    let private prRef : Codec<PrRef> =
        { Encode =
            fun (pr: PrRef) ->
                Encode.object [ "repo", repoRef.Encode pr.Repo; "number", Encode.int pr.Number ]
          Decode =
            Decode.object (fun get ->
                (get.Required.Field "repo" repoRef.Decode, get.Required.Field "number" Decode.int))
            |> Decode.andThen (fun (repo, number) ->
                match PrRef.create repo number with
                | Ok pr -> Decode.succeed pr
                | Error e -> Decode.fail e) }

    /// Why a turn ran with nobody speaking (Plan 20, stage 2). A tagged object rather than a
    /// bare string, because the reasons are a vocabulary that grows — a roster change, a
    /// stream ending — and each may come to carry what it is about.
    let private wakeReason : Codec<WakeReason> =
        { Encode =
            (fun r ->
                match r with
                | CommandFinished -> Encode.object [ "kind", Encode.string "commandFinished" ]
                | StreamEnded id ->
                    Encode.object [ "kind", Encode.string "streamEnded"; "terminalId", terminalId.Encode id ]
                | IntegrationLost id ->
                    Encode.object [ "kind", Encode.string "integrationLost"; "terminalId", terminalId.Encode id ]
                | PrChanged pr -> Encode.object [ "kind", Encode.string "prChanged"; "pr", prRef.Encode pr ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "commandFinished" -> Decode.succeed CommandFinished
                | "streamEnded" -> Decode.field "terminalId" terminalId.Decode |> Decode.map StreamEnded
                | "integrationLost" -> Decode.field "terminalId" terminalId.Decode |> Decode.map IntegrationLost
                | "prChanged" -> Decode.field "pr" prRef.Decode |> Decode.map PrChanged
                | other -> Decode.fail (sprintf "Unknown wake reason: %s" other)) }

    let private agentTurnStarted : Codec<AgentTurnStarted> =
        { Encode =
            fun (p: AgentTurnStarted) ->
                Encode.object
                    [ "agentTurnId", agentTurnId.Encode p.AgentTurnId
                      "triggeredByMessageId", Encode.option messageId.Encode p.TriggeredByMessageId
                      "woke", Encode.option wakeReason.Encode p.Woke ]
          Decode =
            Decode.object (fun get ->
                { AgentTurnStarted.AgentTurnId = get.Required.Field "agentTurnId" agentTurnId.Decode
                  // Optional on the way in: every turn written before Plan 20 carried a bare
                  // id, and those pages are read back for the life of their session.
                  AgentTurnStarted.TriggeredByMessageId =
                    get.Optional.Field "triggeredByMessageId" (Decode.option messageId.Decode) |> Option.flatten
                  AgentTurnStarted.Woke =
                    get.Optional.Field "woke" (Decode.option wakeReason.Decode) |> Option.flatten }) }

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

    let blockId : Codec<BlockId> =
        { Encode = BlockId.value >> Encode.string
          Decode = viaSmartCtor BlockId.create Decode.string }

    let toolUseId : Codec<ToolUseId> =
        { Encode = ToolUseId.value >> Encode.string
          Decode = viaSmartCtor ToolUseId.create Decode.string }

    /// A JSON value carried as TEXT: decode renders whatever is there back to a compact
    /// string; encode parses the string and embeds it as a real JSON node, so an MCP
    /// `inputSchema` object stays an object on the wire and never becomes a quoted string.
    /// A value that is not valid JSON degrades to a JSON string rather than throwing.
    let private rawJson : Codec<string> =
        { Encode =
            (fun raw ->
                match Decode.fromString Decode.value raw with
                | Ok value -> value
                | Error _ -> Encode.string raw)
          Decode = Decode.value |> Decode.map (Encode.toString 0) }

    /// MCP's own `Tool`. What the SESSION's client decodes a server's `tools/list` into
    /// (Plan 17) — it lives here rather than in the Manager's control wire because the
    /// Manager never becomes an MCP client and so never sees one.
    let mcpTool : Codec<McpTool> =
        { Encode =
            fun (t: McpTool) ->
                Encode.object
                    ([ "name", Encode.string t.Name
                       "inputSchema", rawJson.Encode t.InputSchema ]
                     @ (t.Title |> Option.map (fun x -> [ "title", Encode.string x ]) |> Option.defaultValue [])
                     @ (t.Description
                        |> Option.map (fun x -> [ "description", Encode.string x ])
                        |> Option.defaultValue []))
          Decode =
            Decode.object (fun get ->
                { McpTool.Name = get.Required.Field "name" Decode.string
                  McpTool.Title = get.Optional.Field "title" Decode.string
                  McpTool.Description = get.Optional.Field "description" Decode.string
                  McpTool.InputSchema = get.Required.Field "inputSchema" rawJson.Decode }) }

    /// MCP's `ListToolsResult` (its `tools` array).
    let mcpToolList : Codec<McpToolList> =
        { Encode = fun (l: McpToolList) -> Encode.object [ "tools", l.Tools |> List.map mcpTool.Encode |> Encode.list ]
          Decode =
            Decode.object (fun get -> { McpToolList.Tools = get.Required.Field "tools" (Decode.list mcpTool.Decode) }) }

    // Declaring an MCP server (Plan 17). One set of codecs for two consumers — the
    // Manager's state file and the `/control/mcp` frame — because they carry the same
    // value, and two codecs for one type is two chances to disagree about it.

    let mcpServerName : Codec<McpServerName> =
        { Encode = McpServerName.value >> Encode.string
          Decode = viaSmartCtor McpServerName.create Decode.string }

    /// Tagged even with one case: a second transport changes who owns the PROCESS, and a
    /// bare url on the wire would have to be re-tagged to admit one.
    let mcpTransport : Codec<McpTransport> =
        { Encode =
            fun (transport: McpTransport) ->
                match transport with
                | McpHttp url -> Encode.object [ "type", Encode.string "http"; "url", Encode.string url ]
          Decode =
            Decode.field "type" Decode.string
            |> Decode.andThen (fun t ->
                match t with
                | "http" -> Decode.field "url" Decode.string |> Decode.map McpHttp
                | other -> Decode.fail (sprintf "Unknown MCP transport: %s" other)) }

    let mcpServerRef : Codec<McpServerRef> =
        { Encode =
            fun (server: McpServerRef) ->
                Encode.object
                    [ "name", mcpServerName.Encode server.Name
                      "transport", mcpTransport.Encode server.Transport
                      "description", Encode.option Encode.string server.Description ]
          Decode =
            Decode.object (fun get ->
                { McpServerRef.Name = get.Required.Field "name" mcpServerName.Decode
                  McpServerRef.Transport = get.Required.Field "transport" mcpTransport.Decode
                  McpServerRef.Description = get.Optional.Field "description" Decode.string }) }

    let mcpAudience : Codec<McpAudience> =
        { Encode =
            fun (audience: McpAudience) ->
                match audience with
                | AnySession -> Encode.object [ "type", Encode.string "any" ]
                | OneSession id ->
                    Encode.object [ "type", Encode.string "session"; "sessionId", sessionId.Encode id ]
          Decode =
            Decode.field "type" Decode.string
            |> Decode.andThen (fun t ->
                match t with
                | "any" -> Decode.succeed AnySession
                | "session" -> Decode.field "sessionId" sessionId.Decode |> Decode.map OneSession
                | other -> Decode.fail (sprintf "Unknown MCP audience: %s" other)) }

    let mcpServerNoted : Codec<McpServerNoted> =
        { Encode =
            fun (p: McpServerNoted) ->
                Encode.object
                    [ "messageId", messageId.Encode p.MessageId; "name", mcpServerName.Encode p.Name ]
          Decode =
            Decode.object (fun get ->
                { McpServerNoted.MessageId = get.Required.Field "messageId" messageId.Decode
                  McpServerNoted.Name = get.Required.Field "name" mcpServerName.Decode }) }

    let mcpDeclaration : Codec<McpDeclaration> =
        { Encode =
            fun (declaration: McpDeclaration) ->
                Encode.object
                    [ "server", mcpServerRef.Encode declaration.Server
                      "audience", mcpAudience.Encode declaration.Audience ]
          Decode =
            Decode.object (fun get ->
                { McpDeclaration.Server = get.Required.Field "server" mcpServerRef.Decode
                  McpDeclaration.Audience = get.Required.Field "audience" mcpAudience.Decode }) }

    // Talking to a server (Plan 17, step 3). JSON-RPC 2.0 as MCP profiles it, and the two
    // results we read. Codecs rather than string surgery in the Host, so the protocol is
    // testable with no socket — which is the same split `Sse.fs` and the control wire use.

    let jsonRpcRequest : Codec<JsonRpcRequest> =
        { Encode =
            fun (r: JsonRpcRequest) ->
                Encode.object
                    [ yield "jsonrpc", Encode.string "2.0"
                      yield "id", Encode.int r.Id
                      yield "method", Encode.string r.Method
                      match r.Params with
                      | Some p -> yield "params", rawJson.Encode p
                      | None -> () ]
          Decode =
            Decode.object (fun get ->
                { JsonRpcRequest.Id = get.Required.Field "id" Decode.int
                  JsonRpcRequest.Method = get.Required.Field "method" Decode.string
                  JsonRpcRequest.Params = get.Optional.Field "params" rawJson.Decode }) }

    /// A NOTIFICATION: a method call with no id, which is what tells the server not to
    /// answer. Encode-only, because we send them and never receive one.
    let jsonRpcNotification (method: string) : string =
        Encode.object [ "jsonrpc", Encode.string "2.0"; "method", Encode.string method ]
        |> Encode.toString 0

    /// Success and failure are the same frame with different fields, and which arrived is
    /// the interesting part — so it decodes to a DU rather than to a record with two
    /// optionals for a caller to re-derive it from.
    let jsonRpcResponse : Codec<JsonRpcResponse> =
        { Encode =
            fun (r: JsonRpcResponse) ->
                match r with
                | JsonRpcResult (id, result) ->
                    Encode.object
                        [ "jsonrpc", Encode.string "2.0"
                          "id", Encode.int id
                          "result", rawJson.Encode result ]
                | JsonRpcFailure (id, code, message) ->
                    Encode.object
                        [ "jsonrpc", Encode.string "2.0"
                          "id", (match id with Some i -> Encode.int i | None -> Encode.nil)
                          "error", Encode.object [ "code", Encode.int code; "message", Encode.string message ] ]
          Decode =
            Decode.object (fun get ->
                match get.Optional.Field "error" (Decode.option Decode.value) |> Option.flatten with
                | Some _ ->
                    JsonRpcFailure (
                        get.Optional.Field "id" (Decode.option Decode.int) |> Option.flatten,
                        get.Optional.At [ "error"; "code" ] Decode.int |> Option.defaultValue 0,
                        get.Optional.At [ "error"; "message" ] Decode.string
                        |> Option.defaultValue "the server reported an error with no message")
                | None ->
                    JsonRpcResult (
                        get.Required.Field "id" Decode.int,
                        get.Optional.Field "result" rawJson.Decode |> Option.defaultValue "{}")) }

    /// `initialize`'s params. We declare NO client capabilities: a client that declared
    /// `sampling` would be offering the provider a way to drive the model, which is the
    /// opposite of what a proxied server is for. `roots` and `elicitation` are absent for
    /// the same reason — nothing is offered that was not asked for.
    let mcpInitializeParams (clientVersion: string) : string =
        Encode.object
            [ "protocolVersion", Encode.string McpProtocol.Version
              "capabilities", Encode.object []
              "clientInfo",
              Encode.object [ "name", Encode.string "yession"; "version", Encode.string clientVersion ] ]
        |> Encode.toString 0

    /// `tools/call`'s params. The arguments arrive as the JSON text the model produced and
    /// are embedded as an object; text that will not parse becomes an empty object rather
    /// than a quoted string, because a server reading `arguments` as a string would fail in
    /// a way that reads like OUR bug.
    let mcpCallParams (name: string) (arguments: string) : string =
        Encode.object
            [ "name", Encode.string name
              "arguments",
              (match Decode.fromString Decode.value arguments with
               | Ok value -> value
               | Error _ -> Encode.object []) ]
        |> Encode.toString 0

    /// `tools/call`'s params, as a SERVER reads them. The mirror of `mcpCallParams`, and
    /// the arguments come back as the raw JSON text a tool body decodes for itself — the
    /// same discipline `ToolCall` follows, for the same reason.
    let mcpCallRequest : Codec<string * string> =
        { Encode = fun (name, arguments) -> rawJson.Encode (mcpCallParams name arguments)
          Decode =
            Decode.object (fun get ->
                get.Required.Field "name" Decode.string,
                get.Optional.Field "arguments" rawJson.Decode |> Option.defaultValue "{}") }

    /// `initialize`'s result, reduced to what we use. `capabilities` and `instructions` are
    /// not decoded — see `McpHandshake` for why the second one is dropped on purpose.
    let mcpHandshake : Codec<McpHandshake> =
        { Encode =
            fun (h: McpHandshake) ->
                Encode.object
                    [ "protocolVersion", Encode.string h.ProtocolVersion
                      "serverInfo",
                      Encode.object
                          [ "name", Encode.option Encode.string h.ServerName
                            "version", Encode.option Encode.string h.ServerVersion ] ]
          Decode =
            Decode.object (fun get ->
                { McpHandshake.ProtocolVersion = get.Required.Field "protocolVersion" Decode.string
                  McpHandshake.ServerName = get.Optional.At [ "serverInfo"; "name" ] Decode.string
                  McpHandshake.ServerVersion = get.Optional.At [ "serverInfo"; "version" ] Decode.string }) }

    /// `tools/call`'s result. Content blocks are flattened to the text the model reads; a
    /// block of some other kind is NAMED rather than dropped, so a provider answering with
    /// an image produces "[image]" instead of a blank the model reads as success.
    let mcpCallResult : Codec<McpCallResult> =
        let block : Decoder<string> =
            Decode.field "type" Decode.string
            |> Decode.andThen (fun kind ->
                match kind with
                | "text" -> Decode.field "text" Decode.string
                | other -> Decode.succeed (sprintf "[%s]" other))
        { Encode =
            fun (r: McpCallResult) ->
                Encode.object
                    ([ "content",
                       Encode.list [ Encode.object [ "type", Encode.string "text"; "text", Encode.string r.Text ] ]
                       "isError", Encode.bool r.IsError ]
                     @ (r.Meta |> Option.map (fun m -> [ "_meta", rawJson.Encode m ]) |> Option.defaultValue []))
          Decode =
            Decode.object (fun get ->
                { McpCallResult.Text =
                    get.Optional.Field "content" (Decode.list block)
                    |> Option.defaultValue []
                    |> String.concat "\n"
                  McpCallResult.IsError =
                    get.Optional.Field "isError" Decode.bool |> Option.defaultValue false
                  McpCallResult.Meta = get.Optional.Field "_meta" rawJson.Decode }) }

    /// The stream a provider offered, out of a result's `_meta` (Plan 19).
    ///
    /// TOTAL, and deliberately: `_meta` is a place anyone may put anything, so a key that is
    /// missing, a value of the wrong shape, or an offer with no url all read as "no offer"
    /// rather than as a failed tool call. The only thing a provider must get right to be
    /// heard is the url.
    ///
    /// Every other field defaults to the conservative reading — `byteStream` capabilities,
    /// no label, not renewable — so the smallest conforming provider adds one string.
    /// `docs/streams.md` is what such a provider reads.
    let streamOffer (meta: string) : StreamOffer option =
        let capabilities : Decoder<SourceCapabilities> =
            Decode.object (fun get ->
                { CanInstrument = get.Optional.Field "instrument" Decode.bool |> Option.defaultValue false
                  CanResize = get.Optional.Field "resize" Decode.bool |> Option.defaultValue false
                  HasExitCode = get.Optional.Field "exitCode" Decode.bool |> Option.defaultValue false })
        let offer : Decoder<StreamOffer> =
            Decode.object (fun get ->
                { Ticket =
                    { Url = get.Required.Field "url" Decode.string
                      Capabilities =
                        get.Optional.Field "capabilities" capabilities
                        |> Option.defaultValue SourceCapabilities.byteStream
                      Label = get.Optional.Field "label" Decode.string |> Option.defaultValue "" }
                  Renewable = get.Optional.Field "renewable" Decode.bool |> Option.defaultValue false })
        let container : Decoder<StreamOffer option> =
            Decode.object (fun get -> get.Optional.Field StreamOffer.metaKey offer)
        match Decode.fromString container meta with
        | Ok found -> found
        | Error _ -> None

    /// One `/control/mcp` frame: the whole resolved set for THIS session, every time. The
    /// AUDIENCE is deliberately absent — resolution already happened, and a session that
    /// could read who else reaches a server would be reading the Manager's configuration.
    let mcpServerSet : Codec<McpServerSet> =
        { Encode =
            fun (set: McpServerSet) ->
                Encode.object [ "servers", set.Servers |> List.map mcpServerRef.Encode |> Encode.list ]
          Decode =
            Decode.object (fun get ->
                { McpServerSet.Servers = get.Required.Field "servers" (Decode.list mcpServerRef.Decode) }) }

    let private terminalOpened : Codec<TerminalOpened> =
        { Encode =
            fun (p: TerminalOpened) ->
                Encode.object
                    [ "terminalId", terminalId.Encode p.TerminalId
                      "openedBy", actor.Encode p.OpenedBy
                      "title", terminalTitle.Encode p.Title
                      "sandbox", Encode.option sandboxRef.Encode p.Sandbox
                      "renewable", Encode.bool p.Renewable ]
          Decode =
            Decode.object (fun get ->
                // Three states in one field, and they are three different facts, so the
                // key's PRESENCE is read rather than just its value: absent is a log
                // written before named sandboxes (those terminals ran in the one sandbox
                // the session had, so `default` is where they were, not a guess), null is
                // an attached source that runs in no sandbox at all, and a name is a name.
                let written = get.Required.Raw Decode.keys |> List.contains "sandbox"
                { TerminalOpened.TerminalId = get.Required.Field "terminalId" terminalId.Decode
                  TerminalOpened.OpenedBy = get.Required.Field "openedBy" actor.Decode
                  TerminalOpened.Title = get.Required.Field "title" terminalTitle.Decode
                  // Absent in a log written before Plan 19, and false is the honest reading:
                  // nothing recorded a way back, so there is not one.
                  TerminalOpened.Renewable =
                    get.Optional.Field "renewable" Decode.bool |> Option.defaultValue false
                  TerminalOpened.Sandbox =
                    if written then get.Optional.Field "sandbox" sandboxRef.Decode
                    else Some SandboxRef.defaultRef }) }

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
                Encode.object (
                    [ "terminalId", terminalId.Encode p.TerminalId
                      "blockId", blockId.Encode p.BlockId
                      "queueId", Encode.option queueId.Encode p.QueueId
                      "command", Encode.string p.Command
                      "fromSeq", Encode.int p.FromSeq
                      "background", Encode.bool p.Background ]
                    @ authorityFields p.Authority)
          Decode =
            Decode.object (fun get ->
                { TerminalBlockStarted.TerminalId = get.Required.Field "terminalId" terminalId.Decode
                  TerminalBlockStarted.BlockId = get.Required.Field "blockId" blockId.Decode
                  TerminalBlockStarted.QueueId = get.Required.Field "queueId" (Decode.option queueId.Decode)
                  TerminalBlockStarted.Authority = authorityOf get
                  TerminalBlockStarted.Command = get.Required.Field "command" Decode.string
                  TerminalBlockStarted.FromSeq = get.Required.Field "fromSeq" Decode.int
                  // Optional on the way IN and required on the way out: every block written
                  // before Plan 20 ran in the foreground, and an event log is read back for
                  // the life of its session. A `Required` field here would make those pages
                  // undecodable — which is a session that will not open, to record a bool
                  // whose absence already means `false`.
                  TerminalBlockStarted.Background =
                    get.Optional.Field "background" Decode.bool |> Option.defaultValue false }) }

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

    let private terminalIntegrationLost : Codec<TerminalIntegrationLost> =
        { Encode =
            fun (p: TerminalIntegrationLost) ->
                Encode.object
                    [ "terminalId", terminalId.Encode p.TerminalId
                      "blockId", Encode.option blockId.Encode p.BlockId ]
          Decode =
            Decode.object (fun get ->
                { TerminalIntegrationLost.TerminalId = get.Required.Field "terminalId" terminalId.Decode
                  TerminalIntegrationLost.BlockId = get.Optional.Field "blockId" blockId.Decode }) }

    let private terminalIntegrationRestored : Codec<TerminalIntegrationRestored> =
        { Encode = fun (p: TerminalIntegrationRestored) -> Encode.object [ "terminalId", terminalId.Encode p.TerminalId ]
          Decode =
            Decode.object (fun get ->
                { TerminalIntegrationRestored.TerminalId = get.Required.Field "terminalId" terminalId.Decode }) }

    /// The transcript bound a lease event carries (Plan 14, stage 1).
    ///
    /// OPTIONAL on the way in, alone among the terminal payloads' value fields, because a log
    /// written before this existed is a log a running session still has to replay — and the
    /// store fails loudly on anything it cannot decode. Absent reads as 0, so a stretch from
    /// before the range was recorded has `ToSeq <= FromSeq`: an EMPTY range, which every reader
    /// already treats as "nothing to replay" (it is what a rejected block carries). A default
    /// that guessed a real range instead would replay the wrong bytes and look right.
    let private leaseSeq (get: Decode.IGetters) (field: string) : int =
        get.Optional.Field field Decode.int |> Option.defaultValue 0

    let private terminalLeaseTaken : Codec<TerminalLeaseTaken> =
        { Encode =
            fun (p: TerminalLeaseTaken) ->
                Encode.object
                    [ "terminalId", terminalId.Encode p.TerminalId
                      "by", actor.Encode p.By
                      "fromSeq", Encode.int p.FromSeq ]
          Decode =
            Decode.object (fun get ->
                { TerminalLeaseTaken.TerminalId = get.Required.Field "terminalId" terminalId.Decode
                  TerminalLeaseTaken.By = get.Required.Field "by" actor.Decode
                  TerminalLeaseTaken.FromSeq = leaseSeq get "fromSeq" }) }

    let private terminalLeaseReleased : Codec<TerminalLeaseReleased> =
        { Encode =
            fun (p: TerminalLeaseReleased) ->
                Encode.object
                    [ "terminalId", terminalId.Encode p.TerminalId
                      "was", actor.Encode p.Was
                      "reason", leaseEnd.Encode p.Reason
                      "toSeq", Encode.int p.ToSeq ]
          Decode =
            Decode.object (fun get ->
                { TerminalLeaseReleased.TerminalId = get.Required.Field "terminalId" terminalId.Decode
                  TerminalLeaseReleased.Was = get.Required.Field "was" actor.Decode
                  TerminalLeaseReleased.Reason = get.Required.Field "reason" leaseEnd.Decode
                  TerminalLeaseReleased.ToSeq = leaseSeq get "toSeq" }) }

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

    let private repoAdded : Codec<RepoAdded> =
        { Encode =
            fun (p: RepoAdded) ->
                Encode.object
                    [ "messageId", messageId.Encode p.MessageId
                      "repo", repoRef.Encode p.Repo
                      "branch", Encode.string p.Branch
                      "actor", actor.Encode p.Actor ]
          Decode =
            Decode.object (fun get ->
                { RepoAdded.MessageId = get.Required.Field "messageId" messageId.Decode
                  RepoAdded.Repo = get.Required.Field "repo" repoRef.Decode
                  RepoAdded.Branch = get.Required.Field "branch" Decode.string
                  RepoAdded.Actor = get.Required.Field "actor" actor.Decode }) }

    let private repoRemoved : Codec<RepoRemoved> =
        { Encode =
            fun (p: RepoRemoved) ->
                Encode.object
                    [ "messageId", messageId.Encode p.MessageId
                      "repo", repoRef.Encode p.Repo
                      "actor", actor.Encode p.Actor ]
          Decode =
            Decode.object (fun get ->
                { RepoRemoved.MessageId = get.Required.Field "messageId" messageId.Decode
                  RepoRemoved.Repo = get.Required.Field "repo" repoRef.Decode
                  RepoRemoved.Actor = get.Required.Field "actor" actor.Decode }) }

    let private repoBranchSwitched : Codec<RepoBranchSwitched> =
        { Encode =
            fun (p: RepoBranchSwitched) ->
                Encode.object
                    [ "messageId", messageId.Encode p.MessageId
                      "repo", repoRef.Encode p.Repo
                      "branch", Encode.string p.Branch
                      "created", Encode.bool p.Created
                      "actor", actor.Encode p.Actor ]
          Decode =
            Decode.object (fun get ->
                { RepoBranchSwitched.MessageId = get.Required.Field "messageId" messageId.Decode
                  RepoBranchSwitched.Repo = get.Required.Field "repo" repoRef.Decode
                  RepoBranchSwitched.Branch = get.Required.Field "branch" Decode.string
                  RepoBranchSwitched.Created = get.Required.Field "created" Decode.bool
                  RepoBranchSwitched.Actor = get.Required.Field "actor" actor.Decode }) }

    let private prState : Codec<PrState> =
        { Encode = PrState.describe >> Encode.string
          Decode =
            Decode.string
            |> Decode.andThen (function
                | "open" -> Decode.succeed PrOpen
                | "merged" -> Decode.succeed PrMerged
                | "closed" -> Decode.succeed PrClosed
                | other -> Decode.fail (sprintf "Unknown pull request state: %s" other)) }

    let private checksRollup : Codec<ChecksRollup> =
        { Encode =
            (fun (rollup: ChecksRollup) ->
                match rollup with
                | ChecksNone -> Encode.string "none"
                | ChecksPending -> Encode.string "pending"
                | ChecksGreen -> Encode.string "green"
                | ChecksRed -> Encode.string "red")
          Decode =
            Decode.string
            |> Decode.andThen (function
                | "none" -> Decode.succeed ChecksNone
                | "pending" -> Decode.succeed ChecksPending
                | "green" -> Decode.succeed ChecksGreen
                | "red" -> Decode.succeed ChecksRed
                | other -> Decode.fail (sprintf "Unknown checks rollup: %s" other)) }

    let private prSnapshot : Codec<PrSnapshot> =
        { Encode =
            fun (s: PrSnapshot) ->
                Encode.object
                    [ "state", prState.Encode s.State
                      "title", Encode.string s.Title
                      "headSha", Encode.string s.HeadSha
                      "checks", checksRollup.Encode s.Checks
                      "queued", Encode.bool s.Queued
                      "mergeable", Encode.option Encode.bool s.Mergeable ]
          Decode =
            Decode.object (fun get ->
                { PrSnapshot.State = get.Required.Field "state" prState.Decode
                  PrSnapshot.Title = get.Required.Field "title" Decode.string
                  PrSnapshot.HeadSha = get.Required.Field "headSha" Decode.string
                  PrSnapshot.Checks = get.Required.Field "checks" checksRollup.Decode
                  PrSnapshot.Queued = get.Required.Field "queued" Decode.bool
                  PrSnapshot.Mergeable = get.Required.Field "mergeable" (Decode.option Decode.bool) }) }

    let private prTransition : Codec<PrTransition> =
        { Encode =
            (fun (t: PrTransition) ->
                match t with
                | PrTransition.Merged -> Encode.string "merged"
                | PrTransition.Closed -> Encode.string "closed"
                | PrTransition.Reopened -> Encode.string "reopened"
                | PrTransition.ChecksPassed -> Encode.string "checksGreen"
                | PrTransition.ChecksFailed -> Encode.string "checksRed"
                | PrTransition.Queued -> Encode.string "queued"
                | PrTransition.Stalled -> Encode.string "stalled")
          Decode =
            Decode.string
            |> Decode.andThen (function
                | "merged" -> Decode.succeed PrTransition.Merged
                | "closed" -> Decode.succeed PrTransition.Closed
                | "reopened" -> Decode.succeed PrTransition.Reopened
                | "checksGreen" -> Decode.succeed PrTransition.ChecksPassed
                | "checksRed" -> Decode.succeed PrTransition.ChecksFailed
                | "queued" -> Decode.succeed PrTransition.Queued
                | "stalled" -> Decode.succeed PrTransition.Stalled
                | other -> Decode.fail (sprintf "Unknown pull request transition: %s" other)) }

    let private prWatched : Codec<PrWatched> =
        { Encode =
            fun (p: PrWatched) ->
                Encode.object
                    [ "messageId", messageId.Encode p.MessageId
                      "pr", prRef.Encode p.Pr
                      "initial", prSnapshot.Encode p.Initial
                      "actor", actor.Encode p.Actor ]
          Decode =
            Decode.object (fun get ->
                { PrWatched.MessageId = get.Required.Field "messageId" messageId.Decode
                  PrWatched.Pr = get.Required.Field "pr" prRef.Decode
                  PrWatched.Initial = get.Required.Field "initial" prSnapshot.Decode
                  PrWatched.Actor = get.Required.Field "actor" actor.Decode }) }

    let private prUnwatched : Codec<PrUnwatched> =
        { Encode =
            fun (p: PrUnwatched) ->
                Encode.object
                    [ "messageId", messageId.Encode p.MessageId
                      "pr", prRef.Encode p.Pr
                      "actor", actor.Encode p.Actor ]
          Decode =
            Decode.object (fun get ->
                { PrUnwatched.MessageId = get.Required.Field "messageId" messageId.Decode
                  PrUnwatched.Pr = get.Required.Field "pr" prRef.Decode
                  PrUnwatched.Actor = get.Required.Field "actor" actor.Decode }) }

    let private prTransitioned : Codec<PrTransitioned> =
        { Encode =
            fun (p: PrTransitioned) ->
                Encode.object
                    [ "messageId", messageId.Encode p.MessageId
                      "pr", prRef.Encode p.Pr
                      "transition", prTransition.Encode p.Transition
                      "state", prState.Encode p.State
                      "checks", checksRollup.Encode p.Checks
                      "watcher", actor.Encode p.Watcher ]
          Decode =
            Decode.object (fun get ->
                { PrTransitioned.MessageId = get.Required.Field "messageId" messageId.Decode
                  PrTransitioned.Pr = get.Required.Field "pr" prRef.Decode
                  PrTransitioned.Transition = get.Required.Field "transition" prTransition.Decode
                  PrTransitioned.State = get.Required.Field "state" prState.Decode
                  PrTransitioned.Checks = get.Required.Field "checks" checksRollup.Decode
                  PrTransitioned.Watcher = get.Required.Field "watcher" actor.Decode }) }

    let private workSandboxStarted : Codec<WorkSandboxStarted> =
        { Encode =
            fun (p: WorkSandboxStarted) ->
                Encode.object
                    [ "messageId", messageId.Encode p.MessageId
                      "sandbox", sandboxRef.Encode p.Sandbox
                      "backend", Encode.string p.Backend
                      // Names only. There is no branch of this codec that can carry a
                      // credential VALUE, which is the point: the log is replicated to
                      // every peer, and a shape that could hold a token eventually does.
                      "forwarded", Encode.list (p.Forwarded |> List.map Encode.string)
                      "credentialOwner", Encode.option actor.Encode p.CredentialOwner
                      "realisation", Encode.list (p.Realisation |> List.map Encode.string)
                      "actor", actor.Encode p.Actor ]
          Decode =
            Decode.object (fun get ->
                { WorkSandboxStarted.MessageId = get.Required.Field "messageId" messageId.Decode
                  WorkSandboxStarted.Sandbox = get.Required.Field "sandbox" sandboxRef.Decode
                  WorkSandboxStarted.Backend = get.Required.Field "backend" Decode.string
                  WorkSandboxStarted.Forwarded = get.Required.Field "forwarded" (Decode.list Decode.string)
                  WorkSandboxStarted.CredentialOwner = get.Required.Field "credentialOwner" (Decode.option actor.Decode)
                  // Optional on the way in, and this is the only backward-compatible reading
                  // available: a start written before this field existed has no answer, and
                  // absent is the right one — nothing was measured, so nothing is claimed.
                  // Encoded always, so every start written from here on says either what
                  // differed or that nothing did.
                  WorkSandboxStarted.Realisation =
                    get.Optional.Field "realisation" (Decode.list Decode.string) |> Option.defaultValue []
                  WorkSandboxStarted.Actor = get.Required.Field "actor" actor.Decode }) }

    let private repoCapabilitiesChanged : Codec<RepoCapabilitiesChanged> =
        { Encode =
            fun (p: RepoCapabilitiesChanged) ->
                Encode.object
                    [ "messageId", messageId.Encode p.MessageId
                      "repo", repoRef.Encode p.Repo
                      // The grants themselves, not a digest: a log a person cannot read is a
                      // log nobody audits, and a short digest could be collided by whoever
                      // authors the file this watches.
                      "granted", Encode.list (p.Granted |> List.map Encode.string)
                      "sensitive", Encode.bool p.Sensitive
                      "actor", actor.Encode p.Actor ]
          Decode =
            Decode.object (fun get ->
                { RepoCapabilitiesChanged.MessageId = get.Required.Field "messageId" messageId.Decode
                  RepoCapabilitiesChanged.Repo = get.Required.Field "repo" repoRef.Decode
                  RepoCapabilitiesChanged.Granted = get.Required.Field "granted" (Decode.list Decode.string)
                  // Absent in a log written before a set could be sensitive, and false is what
                  // that log meant: nothing then waited on anybody.
                  RepoCapabilitiesChanged.Sensitive =
                    get.Optional.Field "sensitive" Decode.bool |> Option.defaultValue false
                  RepoCapabilitiesChanged.Actor = get.Required.Field "actor" actor.Decode }) }

    let private repoCapabilitiesApproved : Codec<RepoCapabilitiesApproved> =
        { Encode =
            fun (p: RepoCapabilitiesApproved) ->
                Encode.object
                    [ "messageId", messageId.Encode p.MessageId
                      "repo", repoRef.Encode p.Repo
                      "granted", Encode.list (p.Granted |> List.map Encode.string)
                      "actor", actor.Encode p.Actor ]
          Decode =
            Decode.object (fun get ->
                { RepoCapabilitiesApproved.MessageId = get.Required.Field "messageId" messageId.Decode
                  RepoCapabilitiesApproved.Repo = get.Required.Field "repo" repoRef.Decode
                  RepoCapabilitiesApproved.Granted = get.Required.Field "granted" (Decode.list Decode.string)
                  RepoCapabilitiesApproved.Actor = get.Required.Field "actor" actor.Decode }) }

    let private repoConfigRefused : Codec<RepoConfigRefused> =
        { Encode =
            fun (p: RepoConfigRefused) ->
                Encode.object
                    [ "messageId", messageId.Encode p.MessageId
                      "repo", repoRef.Encode p.Repo
                      // Absent rather than null when the FILE is what could not be read:
                      // there is no declaration to name, and a reader that finds no sandbox
                      // here knows the fix is in the YAML rather than in what it asked for.
                      "sandbox", Encode.option sandboxRef.Encode p.Sandbox
                      "reason", Encode.string p.Reason
                      "actor", actor.Encode p.Actor ]
          Decode =
            Decode.object (fun get ->
                { RepoConfigRefused.MessageId = get.Required.Field "messageId" messageId.Decode
                  RepoConfigRefused.Repo = get.Required.Field "repo" repoRef.Decode
                  RepoConfigRefused.Sandbox = get.Required.Field "sandbox" (Decode.option sandboxRef.Decode)
                  RepoConfigRefused.Reason = get.Required.Field "reason" Decode.string
                  RepoConfigRefused.Actor = get.Required.Field "actor" actor.Decode }) }

    let private workSandboxStopped : Codec<WorkSandboxStopped> =
        { Encode =
            fun (p: WorkSandboxStopped) ->
                Encode.object
                    [ "messageId", messageId.Encode p.MessageId
                      "sandbox", sandboxRef.Encode p.Sandbox
                      "actor", actor.Encode p.Actor ]
          Decode =
            Decode.object (fun get ->
                { WorkSandboxStopped.MessageId = get.Required.Field "messageId" messageId.Decode
                  WorkSandboxStopped.Sandbox = get.Required.Field "sandbox" sandboxRef.Decode
                  WorkSandboxStopped.Actor = get.Required.Field "actor" actor.Decode }) }

    let private shellProfileSet : Codec<ShellProfileSet> =
        { Encode =
            fun (p: ShellProfileSet) ->
                Encode.object
                    [ "messageId", messageId.Encode p.MessageId
                      "sandbox", sandboxRef.Encode p.Sandbox
                      // The CLEAR rides as an absent value, which is what makes it
                      // indistinguishable from a line an older build wrote — and that is the
                      // right answer for both: no profile.
                      "workingDirectory", Encode.option Encode.string p.WorkingDirectory
                      "actor", actor.Encode p.Actor ]
          Decode =
            Decode.object (fun get ->
                { ShellProfileSet.MessageId = get.Required.Field "messageId" messageId.Decode
                  ShellProfileSet.Sandbox = get.Required.Field "sandbox" sandboxRef.Decode
                  ShellProfileSet.WorkingDirectory = get.Optional.Field "workingDirectory" Decode.string
                  ShellProfileSet.Actor = get.Required.Field "actor" actor.Decode }) }

    let private commandRefused : Codec<CommandRefused> =
        { Encode =
            fun (p: CommandRefused) ->
                Encode.object
                    [ "messageId", messageId.Encode p.MessageId
                      "queueId", queueId.Encode p.QueueId
                      "tool", Encode.string p.Tool
                      "summary", Encode.string p.Summary
                      "author", actor.Encode p.Author
                      "rejectedBy", actor.Encode p.RejectedBy
                      "reason", Encode.option Encode.string p.Reason ]
          Decode =
            Decode.object (fun get ->
                { CommandRefused.MessageId = get.Required.Field "messageId" messageId.Decode
                  CommandRefused.QueueId = get.Required.Field "queueId" queueId.Decode
                  CommandRefused.Tool = get.Required.Field "tool" Decode.string
                  CommandRefused.Summary = get.Required.Field "summary" Decode.string
                  CommandRefused.Author = get.Required.Field "author" actor.Decode
                  CommandRefused.RejectedBy = get.Required.Field "rejectedBy" actor.Decode
                  CommandRefused.Reason = get.Required.Field "reason" (Decode.option Decode.string) }) }

    /// Whether the CALL happened. Tagged rather than a nullable reason, so "it went fine"
    /// and "it failed with an empty message" stay distinguishable on the wire.
    let private toolOutcome : Codec<ToolOutcome> =
        { Encode =
            fun (outcome: ToolOutcome) ->
                match outcome with
                | ToolCallOk -> Encode.object [ "type", Encode.string "ok" ]
                | ToolCallFailed reason ->
                    Encode.object [ "type", Encode.string "failed"; "reason", Encode.string reason ]
          Decode =
            Decode.field "type" Decode.string
            |> Decode.andThen (fun t ->
                match t with
                | "ok" -> Decode.succeed ToolCallOk
                | "failed" -> Decode.field "reason" Decode.string |> Decode.map ToolCallFailed
                | other -> Decode.fail (sprintf "Unknown tool outcome: %s" other)) }

    let private toolUseStarted : Codec<ToolUseStarted> =
        { Encode =
            fun (p: ToolUseStarted) ->
                Encode.object
                    [ "toolUseId", toolUseId.Encode p.ToolUseId
                      "agentTurnId", agentTurnId.Encode p.AgentTurnId
                      "namespace", Encode.string p.Namespace
                      "name", Encode.string p.Name
                      // `null` and a recorded object are different facts: nothing of this
                      // call's arguments may be recorded, versus these are its arguments
                      // with the secrets already gone.
                      "arguments", (match p.Arguments with Some a -> Encode.string a | None -> Encode.nil) ]
          Decode =
            Decode.object (fun get ->
                { ToolUseStarted.ToolUseId = get.Required.Field "toolUseId" toolUseId.Decode
                  ToolUseStarted.AgentTurnId = get.Required.Field "agentTurnId" agentTurnId.Decode
                  ToolUseStarted.Namespace = get.Required.Field "namespace" Decode.string
                  ToolUseStarted.Name = get.Required.Field "name" Decode.string
                  ToolUseStarted.Arguments = get.Required.Field "arguments" (Decode.option Decode.string) }) }

    let private toolUseFinished : Codec<ToolUseFinished> =
        { Encode =
            fun (p: ToolUseFinished) ->
                Encode.object
                    [ "toolUseId", toolUseId.Encode p.ToolUseId
                      "outcome", toolOutcome.Encode p.Outcome
                      "block", (match p.Block with Some b -> blockId.Encode b | None -> Encode.nil) ]
          Decode =
            Decode.object (fun get ->
                { ToolUseFinished.ToolUseId = get.Required.Field "toolUseId" toolUseId.Decode
                  ToolUseFinished.Outcome = get.Required.Field "outcome" toolOutcome.Decode
                  ToolUseFinished.Block = get.Required.Field "block" (Decode.option blockId.Decode) }) }

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
                | SessionEvent.TerminalIntegrationLost p ->
                    Encode.object
                        [ "type", Encode.string "terminalIntegrationLost"
                          "payload", terminalIntegrationLost.Encode p ]
                | SessionEvent.TerminalIntegrationRestored p ->
                    Encode.object
                        [ "type", Encode.string "terminalIntegrationRestored"
                          "payload", terminalIntegrationRestored.Encode p ]
                | TerminalLeaseTaken p ->
                    Encode.object [ "type", Encode.string "terminalLeaseTaken"; "payload", terminalLeaseTaken.Encode p ]
                | TerminalLeaseReleased p ->
                    Encode.object [ "type", Encode.string "terminalLeaseReleased"; "payload", terminalLeaseReleased.Encode p ]
                | TerminalTranscriptTruncated p ->
                    Encode.object [ "type", Encode.string "terminalTranscriptTruncated"; "payload", terminalTranscriptTruncated.Encode p ]
                | RepoAdded p ->
                    Encode.object [ "type", Encode.string "repoAdded"; "payload", repoAdded.Encode p ]
                | RepoRemoved p ->
                    Encode.object [ "type", Encode.string "repoRemoved"; "payload", repoRemoved.Encode p ]
                | RepoBranchSwitched p ->
                    Encode.object [ "type", Encode.string "repoBranchSwitched"; "payload", repoBranchSwitched.Encode p ]
                | WorkSandboxStarted p ->
                    Encode.object [ "type", Encode.string "workSandboxStarted"; "payload", workSandboxStarted.Encode p ]
                | WorkSandboxStopped p ->
                    Encode.object [ "type", Encode.string "workSandboxStopped"; "payload", workSandboxStopped.Encode p ]
                | RepoConfigRefused p ->
                    Encode.object [ "type", Encode.string "repoConfigRefused"; "payload", repoConfigRefused.Encode p ]
                | RepoCapabilitiesChanged p ->
                    Encode.object
                        [ "type", Encode.string "repoCapabilitiesChanged"
                          "payload", repoCapabilitiesChanged.Encode p ]
                | RepoCapabilitiesApproved p ->
                    Encode.object
                        [ "type", Encode.string "repoCapabilitiesApproved"
                          "payload", repoCapabilitiesApproved.Encode p ]
                | ShellProfileSet p ->
                    Encode.object [ "type", Encode.string "shellProfileSet"; "payload", shellProfileSet.Encode p ]
                | SessionEvent.CommandRefused p ->
                    Encode.object [ "type", Encode.string "commandRefused"; "payload", commandRefused.Encode p ]
                | ToolUseStarted p ->
                    Encode.object [ "type", Encode.string "toolUseStarted"; "payload", toolUseStarted.Encode p ]
                | ToolUseFinished p ->
                    Encode.object [ "type", Encode.string "toolUseFinished"; "payload", toolUseFinished.Encode p ]
                | McpServerAvailable p ->
                    Encode.object [ "type", Encode.string "mcpServerAvailable"; "payload", mcpServerNoted.Encode p ]
                | McpServerUnavailable p ->
                    Encode.object [ "type", Encode.string "mcpServerUnavailable"; "payload", mcpServerNoted.Encode p ]
                | SessionEvent.PrWatched p ->
                    Encode.object [ "type", Encode.string "prWatched"; "payload", prWatched.Encode p ]
                | SessionEvent.PrUnwatched p ->
                    Encode.object [ "type", Encode.string "prUnwatched"; "payload", prUnwatched.Encode p ]
                | SessionEvent.PrTransitioned p ->
                    Encode.object [ "type", Encode.string "prTransitioned"; "payload", prTransitioned.Encode p ])
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
                | "terminalIntegrationLost" ->
                    Decode.field "payload" terminalIntegrationLost.Decode |> Decode.map TerminalIntegrationLost
                | "terminalIntegrationRestored" ->
                    Decode.field "payload" terminalIntegrationRestored.Decode |> Decode.map TerminalIntegrationRestored
                | "terminalLeaseTaken" -> Decode.field "payload" terminalLeaseTaken.Decode |> Decode.map TerminalLeaseTaken
                | "terminalLeaseReleased" -> Decode.field "payload" terminalLeaseReleased.Decode |> Decode.map TerminalLeaseReleased
                | "terminalTranscriptTruncated" ->
                    Decode.field "payload" terminalTranscriptTruncated.Decode |> Decode.map TerminalTranscriptTruncated
                | "repoAdded" -> Decode.field "payload" repoAdded.Decode |> Decode.map RepoAdded
                | "repoRemoved" -> Decode.field "payload" repoRemoved.Decode |> Decode.map RepoRemoved
                | "repoBranchSwitched" -> Decode.field "payload" repoBranchSwitched.Decode |> Decode.map RepoBranchSwitched
                | "workSandboxStarted" -> Decode.field "payload" workSandboxStarted.Decode |> Decode.map WorkSandboxStarted
                | "repoConfigRefused" -> Decode.field "payload" repoConfigRefused.Decode |> Decode.map RepoConfigRefused
                | "repoCapabilitiesChanged" ->
                    Decode.field "payload" repoCapabilitiesChanged.Decode |> Decode.map RepoCapabilitiesChanged
                | "repoCapabilitiesApproved" ->
                    Decode.field "payload" repoCapabilitiesApproved.Decode |> Decode.map RepoCapabilitiesApproved
                | "workSandboxStopped" -> Decode.field "payload" workSandboxStopped.Decode |> Decode.map WorkSandboxStopped
                | "shellProfileSet" -> Decode.field "payload" shellProfileSet.Decode |> Decode.map ShellProfileSet
                | "commandRefused" -> Decode.field "payload" commandRefused.Decode |> Decode.map SessionEvent.CommandRefused
                | "toolUseStarted" -> Decode.field "payload" toolUseStarted.Decode |> Decode.map ToolUseStarted
                | "toolUseFinished" -> Decode.field "payload" toolUseFinished.Decode |> Decode.map ToolUseFinished
                | "mcpServerAvailable" ->
                    Decode.field "payload" mcpServerNoted.Decode |> Decode.map McpServerAvailable
                | "mcpServerUnavailable" ->
                    Decode.field "payload" mcpServerNoted.Decode |> Decode.map McpServerUnavailable
                | "prWatched" ->
                    Decode.field "payload" prWatched.Decode |> Decode.map SessionEvent.PrWatched
                | "prUnwatched" ->
                    Decode.field "payload" prUnwatched.Decode |> Decode.map SessionEvent.PrUnwatched
                | "prTransitioned" ->
                    Decode.field "payload" prTransitioned.Decode |> Decode.map SessionEvent.PrTransitioned
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

    /// One keyframe line in a terminal's `.keys.jsonl` sidecar (Plan 14, stage 3). This one
    /// IS this file's usual object shape rather than asciinema's, and deliberately so: it is
    /// ours, it is not in the `.cast`, and nothing outside Yession has to read it.
    let transcriptKeyframe : Codec<TranscriptKeyframe> =
        { Encode =
            fun (k: TranscriptKeyframe) ->
                Encode.object
                    [ "seq", Encode.int k.Seq
                      "cols", Encode.int k.Cols
                      "rows", Encode.int k.Rows
                      "screen", Encode.string k.Screen ]
          Decode =
            Decode.object (fun get ->
                { Seq = get.Required.Field "seq" Decode.int
                  Cols = get.Required.Field "cols" Decode.int
                  Rows = get.Required.Field "rows" Decode.int
                  Screen = get.Required.Field "screen" Decode.string }) }

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
                // Flat rather than a nested keyframe object, so the two fields this frame
                // has always carried keep their names and their places: a client served an
                // older bundle out of its service-worker cache still reads the screen it
                // knows how to read, and simply does not learn the size.
                | TerminalSnapshot (id, keyframe) ->
                    Encode.object
                        [ "kind", Encode.string "snapshot"
                          "terminalId", terminalId.Encode id
                          "seq", Encode.int keyframe.Seq
                          "cols", Encode.int keyframe.Cols
                          "rows", Encode.int keyframe.Rows
                          "screen", Encode.string keyframe.Screen ]
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
                // The size is OPTIONAL, and defaults to the size every terminal opens at.
                // A frame written before it was carried is a frame from a Session Process
                // that had not resized anything — so 80x24 is not a guess there, it is what
                // that screen was painted at.
                | "snapshot" ->
                    Decode.object (fun get ->
                        TerminalSnapshot (
                            get.Required.Field "terminalId" terminalId.Decode,
                            { Seq = get.Required.Field "seq" Decode.int
                              Cols =
                                get.Optional.Field "cols" Decode.int
                                |> Option.defaultValue Size.default'.Cols
                              Rows =
                                get.Optional.Field "rows" Decode.int
                                |> Option.defaultValue Size.default'.Rows
                              Screen = get.Required.Field "screen" Decode.string }))
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
                        [ "kind", Encode.string "releaseTerminalLease"; "terminalId", terminalId.Encode id ]
                | RearmTerminal id ->
                    Encode.object [ "kind", Encode.string "rearmTerminal"; "terminalId", terminalId.Encode id ]
                | ReattachTerminal id ->
                    Encode.object [ "kind", Encode.string "reattachTerminal"; "terminalId", terminalId.Encode id ]
                | ApproveRepoCapabilities (repo, granted) ->
                    Encode.object
                        [ "kind", Encode.string "approveRepoCapabilities"
                          "repo", repoRef.Encode repo
                          "granted", Encode.list (granted |> List.map Encode.string) ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "interruptAgentTurn" -> Decode.field "agentTurnId" agentTurnId.Decode |> Decode.map InterruptAgentTurn
                | "approveRepoCapabilities" ->
                    Decode.map2
                        (fun repo granted -> ApproveRepoCapabilities (repo, granted))
                        (Decode.field "repo" repoRef.Decode)
                        (Decode.field "granted" (Decode.list Decode.string))
                | "openTerminal" -> Decode.field "title" Decode.string |> Decode.map OpenTerminal
                | "closeTerminal" -> Decode.field "terminalId" terminalId.Decode |> Decode.map CloseTerminal
                | "takeTerminalLease" -> Decode.field "terminalId" terminalId.Decode |> Decode.map TakeTerminalLease
                | "releaseTerminalLease" ->
                    Decode.field "terminalId" terminalId.Decode |> Decode.map ReleaseTerminalLease
                | "rearmTerminal" -> Decode.field "terminalId" terminalId.Decode |> Decode.map RearmTerminal
                | "reattachTerminal" -> Decode.field "terminalId" terminalId.Decode |> Decode.map ReattachTerminal
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

    // --- the query surface (Plan 15) ------------------------------------------------------
    // The wire between the Session Process's query registry and the browser's generated
    // read surface. Both a query's DECLARATION and its VALUE cross it: the declaration
    // because the client renders a query it has never heard of, the value because that is
    // the point. Shape and value are encoded separately rather than as one fused blob, so
    // the section's markup exists before the first value arrives (`/queries` answers, the
    // stream fills in) and so an invalidation frame carries only what changed.

    let queryName : Codec<QueryName> =
        { Encode = QueryName.value >> Encode.string
          Decode = viaSmartCtor QueryName.create Decode.string }

    let private queryColumn : Codec<QueryColumn> =
        { Encode =
            fun (c: QueryColumn) -> Encode.object [ "key", Encode.string c.Key; "label", Encode.string c.Label ]
          Decode =
            Decode.object (fun get ->
                { Key = get.Required.Field "key" Decode.string
                  Label = get.Required.Field "label" Decode.string }) }

    let private queryShape : Codec<QueryShape> =
        { Encode =
            (fun shape ->
                match shape with
                | Value -> Encode.object [ "kind", Encode.string "value" ]
                | Fields columns ->
                    Encode.object [ "kind", Encode.string "fields"; "columns", Encode.list (columns |> List.map queryColumn.Encode) ]
                | Rows columns ->
                    Encode.object [ "kind", Encode.string "rows"; "columns", Encode.list (columns |> List.map queryColumn.Encode) ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "value" -> Decode.succeed Value
                | "fields" -> Decode.field "columns" (Decode.list queryColumn.Decode) |> Decode.map Fields
                | "rows" -> Decode.field "columns" (Decode.list queryColumn.Decode) |> Decode.map Rows
                | other -> Decode.fail (sprintf "Unknown query shape: %s" other)) }

    /// A tone as one word. Spelled out rather than numbered so the stream stays readable
    /// to anything consuming it without this codec, which is the same reason a cell rides
    /// as its native JSON type below.
    let private queryTone : Codec<QueryTone> =
        { Encode = QueryTone.name >> Encode.string
          Decode =
            Decode.string
            |> Decode.andThen (fun raw ->
                match QueryTone.parse raw with
                | Some tone -> Decode.succeed tone
                | None -> Decode.fail (sprintf "Unknown query tone: %s" raw)) }

    /// A cell rides as its NATIVE JSON type — a string, a bool, or null — rather than as a
    /// tagged object, which keeps the payload readable to anything that consumes the stream
    /// without this codec.
    ///
    /// A toned cell is the one that cannot: it carries two facts, so it takes an object.
    /// It is tried LAST, after the three native forms, because `oneOf` takes the first
    /// decoder that succeeds and a bare string must stay a `CellText` — the object form is
    /// the only shape none of the others can claim.
    let private queryCell : Codec<QueryCell> =
        { Encode =
            (fun cell ->
                match cell with
                | CellText text -> Encode.string text
                | CellFlag flag -> Encode.bool flag
                | CellStatus (text, tone) ->
                    Encode.object [ "text", Encode.string text; "tone", queryTone.Encode tone ]
                | CellAbsent -> Encode.nil)
          Decode =
            Decode.oneOf
                [ Decode.string |> Decode.map CellText
                  Decode.bool |> Decode.map CellFlag
                  Decode.nil CellAbsent
                  Decode.map2
                      (fun text tone -> CellStatus (text, tone))
                      (Decode.field "text" Decode.string)
                      (Decode.field "tone" queryTone.Decode) ] }

    let private queryRow : Codec<(string * QueryCell) list> =
        { Encode = fun row -> Encode.object (row |> List.map (fun (key, cell) -> key, queryCell.Encode cell))
          Decode = Decode.keyValuePairs queryCell.Decode }

    let queryValue : Codec<QueryValue> =
        { Encode =
            (fun value ->
                match value with
                | ValueOf cell -> Encode.object [ "kind", Encode.string "value"; "value", queryCell.Encode cell ]
                | FieldsOf fields -> Encode.object [ "kind", Encode.string "fields"; "fields", queryRow.Encode fields ]
                | RowsOf rows ->
                    Encode.object [ "kind", Encode.string "rows"; "rows", Encode.list (rows |> List.map queryRow.Encode) ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "value" -> Decode.field "value" queryCell.Decode |> Decode.map ValueOf
                | "fields" -> Decode.field "fields" queryRow.Decode |> Decode.map FieldsOf
                | "rows" -> Decode.field "rows" (Decode.list queryRow.Decode) |> Decode.map RowsOf
                | other -> Decode.fail (sprintf "Unknown query value: %s" other)) }

    let queryDef : Codec<QueryDef> =
        { Encode =
            (fun (def: QueryDef) ->
                Encode.object
                    [ "name", queryName.Encode def.Name
                      "title", Encode.string def.Title
                      "description", Encode.string def.Description
                      "shape", queryShape.Encode def.Shape ])
          Decode =
            Decode.object (fun get ->
                { Name = get.Required.Field "name" queryName.Decode
                  Title = get.Required.Field "title" Decode.string
                  Description = get.Required.Field "description" Decode.string
                  Shape = get.Required.Field "shape" queryShape.Decode }) }

    /// One frame of the multiplexed query stream. Tagged like `sessionFrame` for the same
    /// reason: one connection carries every query there will ever be, and a client folds
    /// each frame by name into a map.
    let queryFrame : Codec<QueryFrame> =
        { Encode =
            (fun frame ->
                match frame with
                | QueriesDeclared defs ->
                    Encode.object [ "tag", Encode.string "queries"; "queries", Encode.list (defs |> List.map queryDef.Encode) ]
                | QueryValued (name, value) ->
                    Encode.object
                        [ "tag", Encode.string "value"
                          "name", queryName.Encode name
                          "value", queryValue.Encode value ])
          Decode =
            Decode.field "tag" Decode.string
            |> Decode.andThen (function
                | "queries" -> Decode.field "queries" (Decode.list queryDef.Decode) |> Decode.map QueriesDeclared
                | "value" ->
                    Decode.map2
                        (fun name value -> QueryValued (name, value))
                        (Decode.field "name" queryName.Decode)
                        (Decode.field "value" queryValue.Decode)
                | other -> Decode.fail (sprintf "Unknown query frame: %s" other)) }

    let modelId : Codec<ModelId> =
        { Encode = ModelId.value >> Encode.string
          Decode = viaSmartCtor ModelId.create Decode.string }

    let agentModel : Codec<AgentModel> =
        { Encode =
            (fun (model: AgentModel) ->
                Encode.object
                    [ "id", modelId.Encode model.Id
                      "name", Encode.string model.Name ])
          Decode =
            Decode.object (fun get ->
                AgentModel.create
                    (get.Required.Field "id" modelId.Decode)
                    (get.Optional.Field "name" Decode.string |> Option.defaultValue "")) }

    /// The catalogue as the session serves it to a picker. An OBJECT around the list rather
    /// than a bare array, so the reply has somewhere to grow — a provider's default, a
    /// deprecation note — without every reader needing a new shape on the same day.
    let modelCatalogue : Codec<AgentModel list> =
        { Encode = (fun models -> Encode.object [ "models", Encode.list (models |> List.map agentModel.Encode) ])
          Decode = Decode.field "models" (Decode.list agentModel.Decode) }

    /// Serialize a value to a compact JSON string.
    let toString (codec: Codec<'a>) (value: 'a) : string =
        codec.Encode value |> Encode.toString 0

    /// Deserialize a value from a JSON string.
    let fromString (codec: Codec<'a>) (json: string) : Result<'a, string> =
        Decode.fromString codec.Decode json

    /// A gated command's arguments, on the wire (Plan 15, stage 3b).
    ///
    /// A list of strings, deliberately — every gated command's arguments are names, branches
    /// and flags, and a positional list is the smallest thing that survives the doc and comes
    /// back to a process that did not write it. A per-command schema would be the
    /// JSON-Schema-subset renderer this plan already deferred, arriving through the back
    /// door; when that lands, the card can read these and this becomes its encoding.
    ///
    /// Never a credential. `PendingAct.OnBehalfOf` names WHOSE, and the value is resolved at
    /// execution — the pending list replicates to every peer, and a shape that could hold a
    /// token eventually does.
    let gatedArgs : Codec<string list> =
        { Encode = fun values -> Encode.list (values |> List.map Encode.string)
          Decode = Decode.list Decode.string }

/// Rebuilding a `.cast` file from what a client has fetched (Plan 13, stage 3e).
///
/// The audit read. A closed terminal's blocks survive in the projection, but a list of
/// commands is not the same artefact as the RECORDING — a replay shows the terminal as it
/// behaved, at the speed it behaved, which is what someone auditing actually wants to watch.
///
/// It is cheap because the transcript already IS asciicast v2 on disk and the chunk route
/// already serves it: concatenating chunk 0, 1, 2 … reproduces the file byte for byte, so any
/// prefix of chunks is a valid `.cast`. This reassembles that from the DECODED records a
/// client holds, which is the same thing through a round trip the codec already pins — and it
/// means the replay rides the browser's HTTP cache rather than a second whole-file route.
module TranscriptReplay =

    /// The `.cast` text for a header, the records under it, and CHAPTER MARKERS spliced in
    /// as asciicast's own `"m"` events (Plan 25, stage 1).
    ///
    /// Markers belong in the file rather than in the player's `markers` option, and the
    /// difference is not cosmetic. The player idle-compresses EVENTS as it loads a recording
    /// (`idleTimeLimit`), then multiplexes an option-supplied marker list in afterwards, on
    /// the clock the file was written in — so option markers land in dead air the
    /// compression just removed. Everything downstream of that reads wrong at once: the
    /// duration shown is the uncompressed one, playback trudges through gaps to reach a
    /// marker, a `startAt` computed against the compressed clock lands short of the chapter
    /// it names, and the last marker is dropped by the chapter list's `time < duration`
    /// filter because it has become the final event. A marker written into the cast rides
    /// the same compression as every record around it and none of that happens.
    ///
    /// A marker sorts BEFORE a record at the same time — the order the player's own
    /// multiplex picks — so a chapter names the command whose first byte follows it.
    ///
    /// `"m"` is not a `TranscriptKind`, and it should not become one: no transcript on disk
    /// contains a marker. Chapters are a fact about the BLOCKS a terminal ran, folded from
    /// the event log at the moment a recording is assembled for a reader.
    ///
    /// Two residuals the player keeps, and neither is a reason to go back to the option. A
    /// marker at exactly the cast's LAST event time is still dropped from the chapter list —
    /// the UI filters on `time < duration`, strictly, and a marker that is the final event has
    /// become the duration. That is a block that printed nothing, and the `"m"` event is in the
    /// recording either way. And the control bar shows the raw poster time until the reader
    /// first interacts, so a poster nudged past the final record reads a hair long before play.
    let castWithMarkers
        (header: TranscriptHeader)
        (records: (int * TranscriptRecord) list)
        (markers: (float * string) list)
        : string =
        let markerLine (at: float) (label: string) =
            [ Encode.float at; Encode.string "m"; Encode.string label ]
            |> Encode.list
            |> Encode.toString 0
        let recordLine (record: TranscriptRecord) =
            Codec.toString Codec.transcriptLine (TranscriptRecordLine record)
        let rec merge (records: (int * TranscriptRecord) list) (markers: (float * string) list) =
            match records, markers with
            | [], [] -> []
            | [], (at, label) :: restMarkers -> markerLine at label :: merge [] restMarkers
            | (_, record) :: restRecords, [] -> recordLine record :: merge restRecords []
            | (_, record) :: restRecords, (at, label) :: restMarkers ->
                if at <= record.At then markerLine at label :: merge records restMarkers
                else recordLine record :: merge restRecords markers
        let lines =
            (Codec.toString Codec.transcriptLine (TranscriptHeaderLine header))
            :: merge (records |> List.sortBy fst) (markers |> List.sortBy fst)
        String.concat "\n" lines + "\n"

    /// The `.cast` text for a header and the records under it, in sequence order.
    ///
    /// Gaps are simply absent rather than filled: a record the client never fetched, or one
    /// retention deleted, is a line that is not there. asciicast has no notion of a hole, and
    /// inventing a placeholder would put something in the recording that the terminal never
    /// printed. Whether the recording is COMPLETE is a separate question, answered by the
    /// terminal's `DroppedBytes` and said in the surface rather than smuggled into the file.
    ///
    /// A recording with no chapters: the same text `castWithMarkers` writes when nothing
    /// marks it, so the two can never disagree about the shape of a cast.
    let cast (header: TranscriptHeader) (records: (int * TranscriptRecord) list) : string =
        castWithMarkers header records []

    /// The `.cast` text for the half-open transcript range `[fromSeq, toSeq)` — one block's
    /// output, or one stretch of live mode — as a standalone recording the stock player
    /// renders unmodified.
    ///
    /// Three things happen here, and the first two are the ones a naive slice gets wrong:
    ///
    ///   * **The keyframe is painted first**, as a synthesized output record at `t = 0`, so
    ///     the range starts from the screen it actually started from rather than from a
    ///     blank VT. Its size overrides the header's, because the header records the size
    ///     the terminal OPENED at and a resize before the range changed it.
    ///   * **Times are rebased** to the range's first record, because asciicast times are
    ///     relative to the start of the file — without this a block forty minutes in makes
    ///     the player idle for forty minutes before its first frame.
    ///   * Records outside the range are dropped, and INPUT records are kept: what someone
    ///     typed is part of a stretch of live mode, and a pty echoes it anyway.
    ///
    /// With no keyframe (a recording written before they existed) the range still rebases
    /// and still plays; it is the naive slice, approximately right for command output and
    /// wrong wherever the screen carried state in. That is the honest degradation — the
    /// alternative is refusing to play a recording we do have.
    let range
        (header: TranscriptHeader)
        (keyframe: TranscriptKeyframe option)
        (fromSeq: int)
        (toSeq: int)
        (records: (int * TranscriptRecord) list)
        : string =
        let inRange =
            records
            |> List.filter (fun (seq, _) -> seq >= fromSeq && seq < toSeq)
            |> List.sortBy fst
        let origin = inRange |> List.tryHead |> Option.map (fun (_, r) -> r.At) |> Option.defaultValue 0.0
        let header =
            match keyframe with
            | Some k -> { header with Width = k.Cols; Height = k.Rows }
            | None -> header
        let painted =
            match keyframe with
            | Some k when k.Screen <> "" -> [ 0, { At = 0.0; Kind = TranscriptOutput; Data = k.Screen } ]
            | _ -> []
        let rebased =
            inRange |> List.map (fun (seq, record) -> seq + 1, { record with At = record.At - origin })
        cast header (painted @ rebased)
