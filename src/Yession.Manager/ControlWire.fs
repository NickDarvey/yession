namespace Yession.Manager

open System
open Yession.Domain

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// The control-RPC wire shapes (Phase 4, Step 24): how a Session Process's environment
/// capability calls cross the process boundary to the Manager. Hand-written codecs like
/// every boundary; NO session content crosses this channel — only environment and
/// command traffic. Handles serialize as plain data on purpose: constructing a handle
/// grants nothing (the Manager re-validates ownership in its registry on every use —
/// exactly the Step 11 contract).
module ControlWire =

    let private secretName : Codec<SecretName> =
        { Encode = SecretName.value >> Encode.string
          Decode =
            Decode.string
            |> Decode.andThen (fun raw ->
                match SecretName.create raw with
                | Ok v -> Decode.succeed v
                | Error e -> Decode.fail e) }

    let private environmentVariableRef : Codec<EnvironmentVariableRef> =
        { Encode =
            (fun v ->
                match v with
                | PlainValue value -> Encode.object [ "kind", Encode.string "plain"; "value", Encode.string value ]
                | SecretRef name -> Encode.object [ "kind", Encode.string "secret"; "name", secretName.Encode name ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "plain" -> Decode.field "value" Decode.string |> Decode.map PlainValue
                | "secret" -> Decode.field "name" secretName.Decode |> Decode.map SecretRef
                | other -> Decode.fail (sprintf "Unknown environment variable ref: %s" other)) }

    let private mountSource : Codec<MountSource> =
        { Encode =
            (fun s ->
                match s with
                | HostPath path -> Encode.object [ "kind", Encode.string "hostPath"; "path", Encode.string path ]
                | NamedVolume name -> Encode.object [ "kind", Encode.string "namedVolume"; "name", Encode.string name ]
                | SessionWorkspace -> Encode.object [ "kind", Encode.string "sessionWorkspace" ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "hostPath" -> Decode.field "path" Decode.string |> Decode.map HostPath
                | "namedVolume" -> Decode.field "name" Decode.string |> Decode.map NamedVolume
                | "sessionWorkspace" -> Decode.succeed SessionWorkspace
                | other -> Decode.fail (sprintf "Unknown mount source: %s" other)) }

    let private containerMount : Codec<ContainerMount> =
        { Encode =
            fun (m: ContainerMount) ->
                Encode.object
                    [ "source", mountSource.Encode m.Source
                      "target", Encode.string m.Target
                      "mode", Encode.string (match m.Mode with ReadOnly -> "ro" | ReadWrite -> "rw") ]
          Decode =
            Decode.object (fun get ->
                { ContainerMount.Source = get.Required.Field "source" mountSource.Decode
                  ContainerMount.Target = get.Required.Field "target" Decode.string
                  ContainerMount.Mode =
                    match get.Required.Field "mode" Decode.string with
                    | "ro" -> ReadOnly
                    | _ -> ReadWrite }) }

    let environmentSpec : Codec<EnvironmentSpec> =
        { Encode =
            fun (s: EnvironmentSpec) ->
                Encode.object
                    [ "kind", Encode.string (match s.Kind with Docker -> "docker" | LocalProcess -> "localProcess")
                      "workingDirectory", Encode.option Encode.string s.WorkingDirectory
                      "image",
                      Encode.option
                          (fun (i: ContainerImage) ->
                              Encode.object [ "name", Encode.string i.Name; "tag", Encode.option Encode.string i.Tag ])
                          s.Image
                      "build",
                      Encode.option
                          (fun (b: ContainerBuildSpec) ->
                              Encode.object
                                  [ "contextPath", Encode.string b.ContextPath
                                    "dockerfilePath", Encode.option Encode.string b.DockerfilePath ])
                          s.Build
                      "mounts", s.Mounts |> List.map containerMount.Encode |> Encode.list
                      "environmentVariables",
                      s.EnvironmentVariables
                      |> Map.toList
                      |> List.map (fun (k, v) -> k, environmentVariableRef.Encode v)
                      |> Encode.object ]
          Decode =
            Decode.object (fun get ->
                { EnvironmentSpec.Kind =
                    match get.Required.Field "kind" Decode.string with
                    | "docker" -> Docker
                    | _ -> LocalProcess
                  EnvironmentSpec.WorkingDirectory = get.Required.Field "workingDirectory" (Decode.option Decode.string)
                  EnvironmentSpec.Image =
                    get.Required.Field
                        "image"
                        (Decode.option (
                            Decode.object (fun g ->
                                { ContainerImage.Name = g.Required.Field "name" Decode.string
                                  ContainerImage.Tag = g.Required.Field "tag" (Decode.option Decode.string) })))
                  EnvironmentSpec.Build =
                    get.Required.Field
                        "build"
                        (Decode.option (
                            Decode.object (fun g ->
                                { ContainerBuildSpec.ContextPath = g.Required.Field "contextPath" Decode.string
                                  ContainerBuildSpec.DockerfilePath = g.Required.Field "dockerfilePath" (Decode.option Decode.string) })))
                  EnvironmentSpec.Mounts = get.Required.Field "mounts" (Decode.list containerMount.Decode)
                  EnvironmentSpec.EnvironmentVariables =
                    get.Required.Field "environmentVariables" (Decode.dict environmentVariableRef.Decode)
                    |> Map.toList
                    |> Map.ofList }) }

    let containerHandle : Codec<ContainerHandle> =
        { Encode =
            fun (h: ContainerHandle) ->
                Encode.object
                    [ "sessionId", Codec.sessionId.Encode (ContainerHandle.sessionId h)
                      "containerId", Encode.string (ContainerHandle.containerId h) ]
          Decode =
            Decode.object (fun get ->
                ContainerHandle.create
                    (get.Required.Field "sessionId" Codec.sessionId.Decode)
                    (get.Required.Field "containerId" Decode.string)) }

    let startContainerResult : Codec<StartContainerResult> =
        { Encode =
            (fun r ->
                match r with
                | ContainerStarted handle ->
                    Encode.object [ "kind", Encode.string "started"; "handle", containerHandle.Encode handle ]
                | ContainerStartFailed reason ->
                    Encode.object [ "kind", Encode.string "failed"; "reason", Encode.string reason ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "started" -> Decode.field "handle" containerHandle.Decode |> Decode.map ContainerStarted
                | "failed" -> Decode.field "reason" Decode.string |> Decode.map ContainerStartFailed
                | other -> Decode.fail (sprintf "Unknown start result: %s" other)) }

    let stopContainerResult : Codec<StopContainerResult> =
        { Encode =
            (fun r ->
                match r with
                | ContainerStopped -> Encode.object [ "kind", Encode.string "stopped" ]
                | ContainerStopFailed reason ->
                    Encode.object [ "kind", Encode.string "failed"; "reason", Encode.string reason ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "stopped" -> Decode.succeed ContainerStopped
                | "failed" -> Decode.field "reason" Decode.string |> Decode.map ContainerStopFailed
                | other -> Decode.fail (sprintf "Unknown stop result: %s" other)) }

    let commandRequest : Codec<CommandRequest> =
        { Encode =
            fun (c: CommandRequest) ->
                Encode.object
                    [ "commandId", Codec.commandId.Encode c.CommandId
                      "executable", Encode.string c.Executable
                      "arguments", c.Arguments |> List.map Encode.string |> Encode.list
                      "workingDirectory", Encode.option Encode.string c.WorkingDirectory
                      "environment",
                      c.Environment |> Map.toList |> List.map (fun (k, v) -> k, Encode.string v) |> Encode.object
                      "timeoutMs", Encode.option (fun (t: TimeSpan) -> Encode.float t.TotalMilliseconds) c.Timeout ]
          Decode =
            Decode.object (fun get ->
                { CommandRequest.CommandId = get.Required.Field "commandId" Codec.commandId.Decode
                  CommandRequest.Executable = get.Required.Field "executable" Decode.string
                  CommandRequest.Arguments = get.Required.Field "arguments" (Decode.list Decode.string)
                  CommandRequest.WorkingDirectory = get.Required.Field "workingDirectory" (Decode.option Decode.string)
                  CommandRequest.Environment =
                    get.Required.Field "environment" (Decode.dict Decode.string) |> Map.toList |> Map.ofList
                  CommandRequest.Timeout =
                    get.Required.Field "timeoutMs" (Decode.option Decode.float)
                    |> Option.map TimeSpan.FromMilliseconds }) }

    /// The execute request: the handle plus the command.
    type ExecuteRequest =
        { Handle : ContainerHandle
          Command : CommandRequest }

    let executeRequest : Codec<ExecuteRequest> =
        { Encode =
            fun (r: ExecuteRequest) ->
                Encode.object
                    [ "handle", containerHandle.Encode r.Handle
                      "command", commandRequest.Encode r.Command ]
          Decode =
            Decode.object (fun get ->
                { ExecuteRequest.Handle = get.Required.Field "handle" containerHandle.Decode
                  ExecuteRequest.Command = get.Required.Field "command" commandRequest.Decode }) }

    /// One line of the execute response stream: output chunks in order, then exactly
    /// one final result line.
    type ExecuteLine =
        | OutputLine of CommandOutputChunk
        | ResultLine of CommandResult

    let private commandOutputChunk : Codec<CommandOutputChunk> =
        { Encode =
            fun (c: CommandOutputChunk) ->
                Encode.object
                    [ "commandId", Codec.commandId.Encode c.CommandId
                      "stream", Codec.outputStream.Encode c.Stream
                      "text", Encode.string c.Text ]
          Decode =
            Decode.object (fun get ->
                { CommandOutputChunk.CommandId = get.Required.Field "commandId" Codec.commandId.Decode
                  CommandOutputChunk.Stream = get.Required.Field "stream" Codec.outputStream.Decode
                  CommandOutputChunk.Text = get.Required.Field "text" Decode.string }) }

    let executeLine : Codec<ExecuteLine> =
        { Encode =
            (fun l ->
                match l with
                | OutputLine chunk -> Encode.object [ "chunk", commandOutputChunk.Encode chunk ]
                | ResultLine result -> Encode.object [ "result", Codec.commandResult.Encode result ])
          Decode =
            Decode.oneOf
                [ Decode.field "chunk" commandOutputChunk.Decode |> Decode.map OutputLine
                  Decode.field "result" Codec.commandResult.Decode |> Decode.map ResultLine ] }

    /// A session's self-assigned display name, reported child→Manager. The one piece of
    /// non-environment traffic on the control channel: a label (metadata), never
    /// conversation or event content.
    let sessionNameReport : Codec<string> =
        { Encode = fun name -> Encode.object [ "name", Encode.string name ]
          Decode = Decode.field "name" Decode.string }

    /// A Manager→Session notification (the reverse leg, Manager-pushed): the payload of the
    /// `/control/notifications` SSE stream. Tagged by `kind` like every other control shape,
    /// so new cases extend it without breaking older decoders. See `SessionNotification`.
    let sessionNotification : Codec<SessionNotification> =
        { Encode =
            (fun n ->
                match n with
                | EnvironmentChanged () -> Encode.object [ "kind", Encode.string "environmentChanged" ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "environmentChanged" -> Decode.succeed (EnvironmentChanged ())
                | other -> Decode.fail (sprintf "Unknown notification: %s" other)) }

    /// Carry an arbitrary JSON value through as raw text: decode reads the value and renders it
    /// back to a compact string; encode parses the string and embeds it as a real JSON node (so
    /// an MCP `inputSchema` object stays an object on the wire, never a quoted string). A value
    /// that is not valid JSON degrades to a JSON string rather than throwing.
    let private rawJson : Codec<string> =
        { Encode =
            (fun raw ->
                match Decode.fromString Decode.value raw with
                | Ok value -> value
                | Error _ -> Encode.string raw)
          Decode = Decode.value |> Decode.map (Encode.toString 0) }

    let private mcpTool : Codec<McpTool> =
        { Encode =
            fun (t: McpTool) ->
                Encode.object
                    ([ "name", Encode.string t.Name
                       "inputSchema", rawJson.Encode t.InputSchema ]
                     @ (t.Title |> Option.map (fun x -> [ "title", Encode.string x ]) |> Option.defaultValue [])
                     @ (t.Description |> Option.map (fun x -> [ "description", Encode.string x ]) |> Option.defaultValue []))
          Decode =
            Decode.object (fun get ->
                { McpTool.Name = get.Required.Field "name" Decode.string
                  McpTool.Title = get.Optional.Field "title" Decode.string
                  McpTool.Description = get.Optional.Field "description" Decode.string
                  McpTool.InputSchema = get.Required.Field "inputSchema" rawJson.Decode }) }

    /// The MCP tool list — the standard `ListToolsResult` shape (`{ "tools": [...] }`). The
    /// payload of every `/control/mcp` frame (Manager→Session): the current list, then updates.
    let mcpToolList : Codec<McpToolList> =
        { Encode = fun (l: McpToolList) -> Encode.object [ "tools", l.Tools |> List.map mcpTool.Encode |> Encode.list ]
          Decode = Decode.object (fun get -> { McpToolList.Tools = get.Required.Field "tools" (Decode.list mcpTool.Decode) }) }

    // --- secrets (Plan 06) ---------------------------------------------------------------
    // Requests carry an explicit scope: the wire is self-describing and deny paths are
    // testable, even though v1 policy only ever permits a session's own scope for writes.
    // NO response shape can carry a secret value — `set` answers with metadata, `list`
    // with metadata, `delete` with a flag. There is deliberately no get/read shape.

    type SetSecretRequest = { Scope : SecretScope; Name : SecretName; Value : string }
    type ListSecretsRequest = { Scope : SecretScope }
    type DeleteSecretRequest = { Scope : SecretScope; Name : SecretName }
    type ListSecretsResponse = { Secrets : SecretMetadata list }
    type DeleteSecretResponse = { Deleted : bool }

    let secretMetadata : Codec<SecretMetadata> = SecretsCodec.secretMetadata

    let setSecretRequest : Codec<SetSecretRequest> =
        { Encode =
            fun (r: SetSecretRequest) ->
                Encode.object
                    [ "scope", SecretsCodec.secretScope.Encode r.Scope
                      "name", secretName.Encode r.Name
                      "value", Encode.string r.Value ]
          Decode =
            Decode.object (fun get ->
                { SetSecretRequest.Scope = get.Required.Field "scope" SecretsCodec.secretScope.Decode
                  SetSecretRequest.Name = get.Required.Field "name" secretName.Decode
                  SetSecretRequest.Value = get.Required.Field "value" Decode.string }) }

    let listSecretsRequest : Codec<ListSecretsRequest> =
        { Encode = fun (r: ListSecretsRequest) -> Encode.object [ "scope", SecretsCodec.secretScope.Encode r.Scope ]
          Decode =
            Decode.object (fun get ->
                { ListSecretsRequest.Scope = get.Required.Field "scope" SecretsCodec.secretScope.Decode }) }

    let deleteSecretRequest : Codec<DeleteSecretRequest> =
        { Encode =
            fun (r: DeleteSecretRequest) ->
                Encode.object
                    [ "scope", SecretsCodec.secretScope.Encode r.Scope
                      "name", secretName.Encode r.Name ]
          Decode =
            Decode.object (fun get ->
                { DeleteSecretRequest.Scope = get.Required.Field "scope" SecretsCodec.secretScope.Decode
                  DeleteSecretRequest.Name = get.Required.Field "name" secretName.Decode }) }

    let listSecretsResponse : Codec<ListSecretsResponse> =
        { Encode =
            fun (r: ListSecretsResponse) ->
                Encode.object [ "secrets", r.Secrets |> List.map SecretsCodec.secretMetadata.Encode |> Encode.list ]
          Decode =
            Decode.object (fun get ->
                { ListSecretsResponse.Secrets =
                    get.Required.Field "secrets" (Decode.list SecretsCodec.secretMetadata.Decode) }) }

    let deleteSecretResponse : Codec<DeleteSecretResponse> =
        { Encode = fun (r: DeleteSecretResponse) -> Encode.object [ "deleted", Encode.bool r.Deleted ]
          Decode =
            Decode.object (fun get ->
                { DeleteSecretResponse.Deleted = get.Required.Field "deleted" Decode.bool }) }

    // --- connections (Plan 08) -----------------------------------------------------------
    // The Manager-brokered external-service credentials. The wire is service-agnostic BY
    // DESIGN: a begin request carries the provider's endpoints as data, so the Manager
    // never learns which service it brokered. Exactly ONE response shape can carry a
    // credential value — `ConnectionResolveResponse`, the answer to the per-turn resolve —
    // and NO shape can carry a refresh token (that type lives in `BrokerState`, which has
    // no codec here).

    /// Everything the standards need to begin an authorization-code + PKCE flow, as data.
    /// `AuthorizeUrl` may already carry provider-specific query params; the broker appends
    /// only the standard ones.
    type ConnectionBeginRequest =
        { Target : SecretId
          AuthorizeUrl : string
          TokenUrl : string
          ClientId : string
          Scopes : string }

    type ConnectionBeginResponse = { AuthorizeUrl : string; State : string }

    /// Manual completion (the paste path): the pasted payload is `code` or `code#state`.
    /// The target names which begun flow this completes — the broker checks it matches.
    type ConnectionCompleteRequest = { Target : SecretId; Code : string }

    /// Store a pasted static token/key verbatim.
    type ConnectionPutRequest = { Target : SecretId; Value : string }

    type ConnectionDisconnectRequest = { Target : SecretId }
    type ConnectionDisconnectResponse = { Disconnected : bool }

    type ConnectionResolveRequest = { Target : SecretId }
    type ConnectionResolveResponse = { Kind : ConnectionKind; Value : string }

    let private secretId : Codec<SecretId> =
        { Encode =
            fun (id: SecretId) ->
                Encode.object
                    [ "scope", SecretsCodec.secretScope.Encode id.Scope
                      "name", secretName.Encode id.Name ]
          Decode =
            Decode.object (fun get ->
                { SecretId.Scope = get.Required.Field "scope" SecretsCodec.secretScope.Decode
                  SecretId.Name = get.Required.Field "name" secretName.Decode }) }

    let connectionKind : Codec<ConnectionKind> =
        { Encode =
            (fun k ->
                match k with
                | OAuthConnection -> Encode.string "oauth"
                | StaticConnection -> Encode.string "static")
          Decode =
            Decode.string
            |> Decode.andThen (function
                | "oauth" -> Decode.succeed OAuthConnection
                | "static" -> Decode.succeed StaticConnection
                | other -> Decode.fail (sprintf "Unknown connection kind: %s" other)) }

    let connectionStatus : Codec<ConnectionStatus> =
        { Encode =
            fun (s: ConnectionStatus) ->
                Encode.object
                    [ "id", secretId.Encode s.Id
                      "kind", connectionKind.Encode s.Kind
                      "updatedAt", Codec.timestamp.Encode s.UpdatedAt ]
          Decode =
            Decode.object (fun get ->
                { ConnectionStatus.Id = get.Required.Field "id" secretId.Decode
                  ConnectionStatus.Kind = get.Required.Field "kind" connectionKind.Decode
                  ConnectionStatus.UpdatedAt = get.Required.Field "updatedAt" Codec.timestamp.Decode }) }

    /// The `/control/connections` SSE frame: every connection the receiving launch may
    /// currently read. Metadata only — the type cannot carry a value.
    let connectionStatusList : Codec<ConnectionStatusList> =
        { Encode =
            fun (l: ConnectionStatusList) ->
                Encode.object [ "connections", l.Connections |> List.map connectionStatus.Encode |> Encode.list ]
          Decode =
            Decode.object (fun get ->
                { ConnectionStatusList.Connections =
                    get.Required.Field "connections" (Decode.list connectionStatus.Decode) }) }

    let connectionBeginRequest : Codec<ConnectionBeginRequest> =
        { Encode =
            fun (r: ConnectionBeginRequest) ->
                Encode.object
                    [ "target", secretId.Encode r.Target
                      "authorizeUrl", Encode.string r.AuthorizeUrl
                      "tokenUrl", Encode.string r.TokenUrl
                      "clientId", Encode.string r.ClientId
                      "scopes", Encode.string r.Scopes ]
          Decode =
            Decode.object (fun get ->
                { ConnectionBeginRequest.Target = get.Required.Field "target" secretId.Decode
                  ConnectionBeginRequest.AuthorizeUrl = get.Required.Field "authorizeUrl" Decode.string
                  ConnectionBeginRequest.TokenUrl = get.Required.Field "tokenUrl" Decode.string
                  ConnectionBeginRequest.ClientId = get.Required.Field "clientId" Decode.string
                  ConnectionBeginRequest.Scopes = get.Required.Field "scopes" Decode.string }) }

    let connectionBeginResponse : Codec<ConnectionBeginResponse> =
        { Encode =
            fun (r: ConnectionBeginResponse) ->
                Encode.object
                    [ "authorizeUrl", Encode.string r.AuthorizeUrl
                      "state", Encode.string r.State ]
          Decode =
            Decode.object (fun get ->
                { ConnectionBeginResponse.AuthorizeUrl = get.Required.Field "authorizeUrl" Decode.string
                  ConnectionBeginResponse.State = get.Required.Field "state" Decode.string }) }

    let connectionCompleteRequest : Codec<ConnectionCompleteRequest> =
        { Encode =
            fun (r: ConnectionCompleteRequest) ->
                Encode.object [ "target", secretId.Encode r.Target; "code", Encode.string r.Code ]
          Decode =
            Decode.object (fun get ->
                { ConnectionCompleteRequest.Target = get.Required.Field "target" secretId.Decode
                  ConnectionCompleteRequest.Code = get.Required.Field "code" Decode.string }) }

    let connectionPutRequest : Codec<ConnectionPutRequest> =
        { Encode =
            fun (r: ConnectionPutRequest) ->
                Encode.object [ "target", secretId.Encode r.Target; "value", Encode.string r.Value ]
          Decode =
            Decode.object (fun get ->
                { ConnectionPutRequest.Target = get.Required.Field "target" secretId.Decode
                  ConnectionPutRequest.Value = get.Required.Field "value" Decode.string }) }

    let connectionDisconnectRequest : Codec<ConnectionDisconnectRequest> =
        { Encode = fun (r: ConnectionDisconnectRequest) -> Encode.object [ "target", secretId.Encode r.Target ]
          Decode =
            Decode.object (fun get ->
                { ConnectionDisconnectRequest.Target = get.Required.Field "target" secretId.Decode }) }

    let connectionDisconnectResponse : Codec<ConnectionDisconnectResponse> =
        { Encode = fun (r: ConnectionDisconnectResponse) -> Encode.object [ "disconnected", Encode.bool r.Disconnected ]
          Decode =
            Decode.object (fun get ->
                { ConnectionDisconnectResponse.Disconnected = get.Required.Field "disconnected" Decode.bool }) }

    let connectionResolveRequest : Codec<ConnectionResolveRequest> =
        { Encode = fun (r: ConnectionResolveRequest) -> Encode.object [ "target", secretId.Encode r.Target ]
          Decode =
            Decode.object (fun get ->
                { ConnectionResolveRequest.Target = get.Required.Field "target" secretId.Decode }) }

    let connectionResolveResponse : Codec<ConnectionResolveResponse> =
        { Encode =
            fun (r: ConnectionResolveResponse) ->
                Encode.object
                    [ "kind", connectionKind.Encode r.Kind
                      "value", Encode.string r.Value ]
          Decode =
            Decode.object (fun get ->
                { ConnectionResolveResponse.Kind = get.Required.Field "kind" connectionKind.Decode
                  ConnectionResolveResponse.Value = get.Required.Field "value" Decode.string }) }

    /// One Running session in the registry stream (docs/plans/09): what an operator's
    /// serving binding needs to expose it — the OS-assigned port — plus identity for
    /// display and the pid for supervision-side correlation.
    type SessionRegistryEntry =
        { Id : SessionId
          Name : string
          Port : int
          Pid : int }

    /// A `/sessions/stream` SSE frame: the FULL current set of Running sessions.
    /// Snapshot semantics, never deltas — a consumer applies each frame wholesale, so
    /// reconnecting (whose first frame is the current snapshot) is the recovery path.
    type SessionRegistryFrame = { Sessions : SessionRegistryEntry list }

    let private sessionRegistryEntry : Codec<SessionRegistryEntry> =
        { Encode =
            fun (e: SessionRegistryEntry) ->
                Encode.object
                    [ "id", Codec.sessionId.Encode e.Id
                      "name", Encode.string e.Name
                      "port", Encode.int e.Port
                      "pid", Encode.int e.Pid ]
          Decode =
            Decode.object (fun get ->
                { SessionRegistryEntry.Id = get.Required.Field "id" Codec.sessionId.Decode
                  SessionRegistryEntry.Name = get.Required.Field "name" Decode.string
                  SessionRegistryEntry.Port = get.Required.Field "port" Decode.int
                  SessionRegistryEntry.Pid = get.Required.Field "pid" Decode.int }) }

    let sessionRegistryFrame : Codec<SessionRegistryFrame> =
        { Encode =
            fun (f: SessionRegistryFrame) ->
                Encode.object [ "sessions", f.Sessions |> List.map sessionRegistryEntry.Encode |> Encode.list ]
          Decode =
            Decode.object (fun get ->
                { SessionRegistryFrame.Sessions =
                    get.Required.Field "sessions" (Decode.list sessionRegistryEntry.Decode) }) }

    let toString (codec: Codec<'a>) (value: 'a) : string = codec.Encode value |> Encode.toString 0

    let fromString (codec: Codec<'a>) (json: string) : Result<'a, string> = Decode.fromString codec.Decode json
