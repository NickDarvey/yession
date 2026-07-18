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

    let toString (codec: Codec<'a>) (value: 'a) : string = codec.Encode value |> Encode.toString 0

    let fromString (codec: Codec<'a>) (json: string) : Result<'a, string> = Decode.fromString codec.Decode json
