namespace Yession.Domain

// The agent's tools, as DATA (Plan 16, part A).
//
// They used to be code: twelve `sdk.tool(...)` calls written out inside one `[<Emit>]`
// template literal, a runner taking one positional callback per tool, and a literal list of
// wire names beside them that had to be kept in step by hand. Adding a tool cost an edit in
// three places and a parameter on a function that already had eighteen; adding a tool at RUN
// time — which is what a session-declared MCP server is — was not expensive, it was
// impossible.
//
// So a tool becomes a descriptor plus one dispatch function. The runner builds the SDK's
// tools in a loop, computes the auto-approve list from the same descriptors, and never
// learns what any individual tool does. Everything downstream of that — a proxied external
// server, a tool-use record that names where a call went — is then an ordinary consumer of
// the same two values.

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// One argument of a tool, in the small vocabulary the agent's tools actually use.
///
/// A field rather than raw schema text because the schema is READ as well as written: the
/// redactor walks it to decide what may be recorded, so a hand-rolled string per tool would
/// be a second place for that decision to live.
type ToolField =
    { Key : string
      /// A JSON Schema primitive: "string", "boolean", "number", "integer" — or "array",
      /// in which case `Items` says what it is an array of.
      Type : string
      /// The element type, when `Type` is "array". `None` for everything else.
      Items : string option
      /// What the model reads to decide what to put here.
      Description : string
      Required : bool
      /// Emitted as JSON Schema's own `writeOnly: true` — the field goes in and never comes
      /// back out. It is what marks an argument as secret, and it is the ONLY place that is
      /// declared: a list of field names kept beside the schema would silently stop matching
      /// the moment an argument was renamed.
      Secret : bool }

module ToolField =

    let required (key: string) (fieldType: string) (description: string) : ToolField =
        { Key = key; Type = fieldType; Items = None; Description = description; Required = true; Secret = false }

    let optional (key: string) (fieldType: string) (description: string) : ToolField =
        { Key = key; Type = fieldType; Items = None; Description = description; Required = false; Secret = false }

    /// An optional array argument, e.g. `forward: ["github"]`.
    let optionalList (key: string) (itemType: string) (description: string) : ToolField =
        { Key = key
          Type = "array"
          Items = Some itemType
          Description = description
          Required = false
          Secret = false }

    /// A required argument whose VALUE must never be recorded.
    let secret (key: string) (description: string) : ToolField =
        { Key = key; Type = "string"; Items = None; Description = description; Required = true; Secret = true }

module ToolSchema =

    /// The fields as a JSON Schema object, as text. Text because that is what crosses every
    /// boundary this travels — the MCP wire, an external server's `tools/list`, the SDK's
    /// tool builder — and reshaping it at each one would be three chances to lose a keyword
    /// we did not know mattered.
    let ofFields (fields: ToolField list) : string =
        let property (field: ToolField) =
            [ yield "type", Encode.string field.Type
              match field.Items with
              | Some item -> yield "items", Encode.object [ "type", Encode.string item ]
              | None -> ()
              if field.Description <> "" then yield "description", Encode.string field.Description
              if field.Secret then yield "writeOnly", Encode.bool true ]
            |> Encode.object
        let required = fields |> List.filter (fun f -> f.Required) |> List.map (fun f -> Encode.string f.Key)
        Encode.object
            [ "type", Encode.string "object"
              "properties", Encode.object (fields |> List.map (fun f -> f.Key, property f))
              "required", Encode.list required
              "additionalProperties", Encode.bool false ]
        |> Encode.toString 0

    /// No arguments at all — what a query takes, and what `list_secrets` takes.
    let none : string = ofFields []

/// A tool the agent may call, and enough about it to build the call site.
///
/// `Namespace` is what lets two providers both offer `list` without colliding, and what a
/// reader of the tool-use record sees so they can tell WHERE a call went. It is also the
/// name of the SDK MCP server the tool is mounted on, which is why the wire name a model
/// sees carries it for free.
type ToolDescriptor =
    { Namespace : string
      Name : string
      Description : string
      /// Raw JSON Schema, passed through untouched. Ours is built by `ToolSchema`; an
      /// external server's arrives already written and is never rewritten.
      InputSchema : string
      /// MCP's own `readOnlyHint`. Declared rather than inferred, so identifying the
      /// read-only tools of a THIRD-PARTY server needs no yession-specific convention.
      ReadOnly : bool
      /// A human title, when there is one worth showing beside the name.
      Title : string option
      /// Did somebody else write this schema? Redaction is schema-driven, and a schema we
      /// did not write cannot be trusted to mark its own secrets — so a foreign tool's
      /// argument VALUES are not recorded at all. The record still carries the namespace,
      /// the tool and the outcome, which is the part that answers "what did the agent just
      /// do".
      Foreign : bool }

module ToolDescriptor =

    let create (ns: string) (name: string) (description: string) (schema: string) : ToolDescriptor =
        { Namespace = ns
          Name = name
          Description = description
          InputSchema = schema
          ReadOnly = false
          Title = None
          Foreign = false }

    /// A tool somebody else declared — an external MCP server's, arriving already written.
    let foreign (ns: string) (name: string) (description: string) (schema: string) : ToolDescriptor =
        { create ns name description schema with Foreign = true }

    /// The name the model calls and the audit records: the SDK's `mcp__<server>__<tool>`,
    /// where the server IS the namespace. Existing tools keep the names they already had,
    /// which is what makes part A a refactor rather than a wire change.
    let wireName (descriptor: ToolDescriptor) : string =
        sprintf "mcp__%s__%s" descriptor.Namespace descriptor.Name

/// One invocation: which tool, and its arguments as the JSON object the model produced.
///
/// Arguments stay JSON text rather than being decoded into a per-tool record, because the
/// two things that must happen to every call — recording it, and redacting what may not be
/// recorded — are schema-driven and generic. A tool body decodes its own arguments; nothing
/// on the path to it has to know their shape.
[<RequireQualifiedAccess>]
type ToolCall =
    { Namespace : string
      Name : string
      Arguments : string }

/// What a tool answered: the text the model reads, and — when the call became one — the
/// block it produced.
///
/// The block is here rather than dug out of the text because the audit record needs it and
/// nothing else can supply it: `execute_command` is the only party that knows a call turned
/// into something the chat already draws, and a record that could not say so would draw a
/// second chip beside the block's own.
type ToolAnswer =
    { Text : string
      Block : BlockId option
      /// A byte stream the provider offered along with its answer (Plan 19), already
      /// admitted by whoever decoded it. `None` for every in-process tool: the session's own
      /// verbs run in the session, and a stream is by definition somebody else's.
      Stream : StreamOffer option }

module ToolAnswer =

    let text (value: string) : ToolAnswer = { Text = value; Block = None; Stream = None }

/// Invoke a tool. ONE function, so an in-process tool and a proxied one are
/// indistinguishable to the caller — which is what makes a single audit seam possible
/// rather than one per implementation.
///
/// `Error` is reserved for the call not happening at all (no such tool, unreadable
/// arguments). A tool that ran and failed answers `Ok` with text saying so: the model is
/// meant to read that and choose differently, not to see a protocol error.
type InvokeTool = ToolCall -> Async<Result<ToolAnswer, string>>

/// What a turn may call, and how. The whole of the agent's authority surface, as a value.
type ToolRegistry =
    { Tools : ToolDescriptor list
      Invoke : InvokeTool }

module ToolRegistry =

    let empty : ToolRegistry =
        { Tools = []
          Invoke = fun call -> async { return Error (sprintf "no tool '%s/%s'" call.Namespace call.Name) } }

    /// Every tool's wire name — the SDK's auto-approve list, computed from the descriptors
    /// instead of maintained beside them. The list and the tools cannot disagree because
    /// there is only one of them.
    let allowedTools (registry: ToolRegistry) : string list =
        registry.Tools |> List.map ToolDescriptor.wireName

    /// The namespaces in play, in first-seen order — one SDK MCP server each.
    let namespaces (registry: ToolRegistry) : string list =
        registry.Tools |> List.map (fun t -> t.Namespace) |> List.distinct

    let private has (call: ToolCall) (tools: ToolDescriptor list) =
        tools |> List.exists (fun t -> t.Namespace = call.Namespace && t.Name = call.Name)

    /// The descriptor a call names, if the registry has it. The audit seam's one lookup:
    /// the schema is what decides what may be recorded.
    let tryFind (call: ToolCall) (registry: ToolRegistry) : ToolDescriptor option =
        registry.Tools |> List.tryFind (fun t -> t.Namespace = call.Namespace && t.Name = call.Name)

    /// A registry over one namespace: descriptors, and a dispatch that only ever sees calls
    /// belonging to it.
    let ofNamespace (ns: string) (tools: ToolDescriptor list) (invoke: InvokeTool) : ToolRegistry =
        let tools = tools |> List.map (fun t -> { t with Namespace = ns })
        { Tools = tools
          Invoke =
            fun call ->
                if has call tools then invoke call
                else async { return Error (sprintf "no tool '%s/%s'" call.Namespace call.Name) } }

    /// Combine two registries, REFUSING a collision rather than resolving one.
    ///
    /// Silently letting one shadow the other would mean the model's tool list and what
    /// actually runs could differ, and the difference would only show as the wrong thing
    /// happening. Namespaces exist so this is rare; when it is not, the session says which
    /// name is doubled and declines to start.
    let merge (first: ToolRegistry) (second: ToolRegistry) : Result<ToolRegistry, string> =
        let collisions =
            second.Tools
            |> List.filter (fun t -> first.Tools |> List.exists (fun e -> e.Namespace = t.Namespace && e.Name = t.Name))
            |> List.map ToolDescriptor.wireName
        match collisions with
        | [] ->
            Ok
                { Tools = first.Tools @ second.Tools
                  Invoke =
                    fun call ->
                        if has call first.Tools then first.Invoke call
                        elif has call second.Tools then second.Invoke call
                        else async { return Error (sprintf "no tool '%s/%s'" call.Namespace call.Name) } }
        | doubled ->
            Error (sprintf "two tools claim %s" (doubled |> String.concat ", "))

    let mergeAll (registries: ToolRegistry list) : Result<ToolRegistry, string> =
        registries
        |> List.fold
            (fun acc registry ->
                match acc with
                | Error e -> Error e
                | Ok combined -> merge combined registry)
            (Ok empty)

// -------------------------------------------------------------------------------------
// The audit seam (Plan 16, part C)
// -------------------------------------------------------------------------------------

module ToolArguments =

    /// The fields a schema marks `writeOnly: true`. JSON Schema's own keyword, chosen over
    /// a custom marker because its meaning — goes in, never comes back out — is exactly the
    /// property being relied on, and because it stays intelligible to any MCP client that
    /// reads the schema.
    let secretFields (schema: string) : string list =
        let property = Decode.object (fun get -> get.Optional.Field "writeOnly" Decode.bool |> Option.defaultValue false)
        match Decode.fromString (Decode.field "properties" (Decode.keyValuePairs property)) schema with
        | Ok fields -> fields |> List.filter snd |> List.map fst
        | Error _ -> []

    /// What may be recorded of one call's arguments.
    ///
    /// This is a REBUILD, not a mask: the recorded object is constructed field by field and
    /// a secret field's value is simply never copied into it. "Masked on write" would still
    /// route the plaintext through the logging path, where one bug prints it; here there is
    /// no write path left to get wrong.
    ///
    /// The trade-off, stated rather than hidden: an allowlist would fail SAFE (an unmarked
    /// field is never recorded) and a marker fails OPEN (an unmarked field is recorded).
    /// The marker is chosen anyway because an allowlist is a parallel list of field names —
    /// rename an argument and the entry silently stops protecting the thing it named.
    let redact (schema: string) (arguments: string) : string option =
        let secrets = secretFields schema
        match Decode.fromString (Decode.keyValuePairs Decode.value) arguments with
        | Error _ -> None
        | Ok fields ->
            fields
            |> List.map (fun (key, value) ->
                if List.contains key secrets then key, Encode.nil else key, value)
            |> Encode.object
            |> Encode.toString 0
            |> Some

/// What the audit seam is told when a call starts. The turn and the handle are NOT here:
/// the log is bound to a turn, and minting the handle is the Session Process's job — the
/// same rule `MessageId` and `BlockId` already follow.
[<RequireQualifiedAccess>]
type ToolUseBegin =
    { Namespace : string
      Name : string
      /// Already redacted, or `None` when nothing of the arguments may be recorded.
      Arguments : string option }

/// …and when it ends.
type ToolUseEnd =
    { Outcome : ToolOutcome
      Block : BlockId option }

/// The audit seam: ONE service, injected into every path a tool call can take, so the
/// session's own tools and a proxied external server are recorded the same way. Different
/// implementations may answer a call; only one thing records it.
///
/// Two moments rather than one, mirroring `TerminalBlockStarted`/`TerminalBlockCompleted`:
/// the start is what anchors the call in the chat, so a four-minute call is visible while
/// it is the only thing happening, and the finish moves what it says without moving where
/// it sits.
type ToolUseLog =
    { /// Record that a call started, and answer with the handle it will be addressed by.
      /// `None` means nothing is being recorded, and `Finished` is then never called.
      Started : ToolUseBegin -> Async<ToolUseId option>
      Finished : ToolUseId -> ToolUseEnd -> Async<unit> }

module ToolUseLog =

    /// Records nothing — for turns nobody is watching (tests, one-shots).
    let none : ToolUseLog =
        { Started = fun _ -> async { return None }
          Finished = fun _ _ -> async { return () } }

    /// Wrap a registry so every call through it is recorded. Applied ONCE, to the merged
    /// registry, which is what makes "the same way" true rather than aspirational: a
    /// provider added later cannot arrive with its own logging, or without any.
    let wrap (log: ToolUseLog) (registry: ToolRegistry) : ToolRegistry =
        { registry with
            Invoke =
              fun call ->
                async {
                    let recorded =
                        match ToolRegistry.tryFind call registry with
                        | Some descriptor when not descriptor.Foreign ->
                            ToolArguments.redact descriptor.InputSchema call.Arguments
                        | _ -> None
                    let! handle =
                        log.Started { Namespace = call.Namespace; Name = call.Name; Arguments = recorded }
                    // A tool body that throws is still a call that happened, and an audit
                    // that loses exactly the calls that went worst is worse than none.
                    let! answer =
                        async {
                            try
                                return! registry.Invoke call
                            with e ->
                                return Error e.Message
                        }
                    match handle with
                    | None -> ()
                    | Some id ->
                        let ending =
                            match answer with
                            | Ok answer -> { Outcome = ToolCallOk; Block = answer.Block }
                            | Error reason -> { Outcome = ToolCallFailed reason; Block = None }
                        do! log.Finished id ending
                    return answer
                } }
