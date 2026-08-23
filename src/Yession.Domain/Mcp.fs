namespace Yession.Domain

/// MCP (Model Context Protocol) tool descriptors, in the standard `Tool` / `ListToolsResult`
/// shapes. What the SESSION's MCP client decodes a server's `tools/list` into (Plan 17): the
/// session is the client, so this is a reply it receives, not a payload the Manager pushes.
/// Pagination (`nextCursor`) is intentionally omitted — a device provider's tool list is
/// short and static, and paging it would be machinery with no producer.
type McpTool =
    { /// The tool's unique name — the MCP call target (`Tool.name`).
      Name : string
      /// Human-readable title (`Tool.title`), when the server provides one.
      Title : string option
      /// Human-readable description (`Tool.description`).
      Description : string option
      /// The tool's input JSON Schema — verbatim MCP `Tool.inputSchema` (a JSON object). Kept
      /// as raw JSON text so any schema passes the boundary losslessly; the wire codec embeds
      /// it as a real JSON object, not a quoted string.
      InputSchema : string }

/// The MCP tool list — the standard `ListToolsResult` (its `tools` array).
type McpToolList = { Tools : McpTool list }

module McpToolList =
    let empty : McpToolList = { Tools = [] }

// ---------------------------------------------------------------------------------------
// Declaring a server (Plan 17).
//
// The Manager declares, every session it names gets it, live. There is no second step: no
// per-session enablement in the management UI (Plan 15 made a session's surface generated
// and read-only, with commands belonging to the agent), and no agent command to attach what
// the operator already declared. An operator who declares a server declares it in order for
// it to be used.
//
// The org/repo shape — a broad scope that allows, a narrow one that enables — was the earlier
// draft and is the wrong size here. Selection below policy earns its keep when one broad scope
// covers many narrow ones with different owners and different needs; a Yession host has one
// operator and a handful of sessions, so a gate with the same person on both sides is a step
// that only ever gets performed. The scope survives and the selection does not.
// ---------------------------------------------------------------------------------------

/// How to reach it. One case, because localhost HTTP is the only transport this round — a DU
/// rather than a bare url because the wire needs a tag anyway, and because a stdio server
/// (deliberately later) changes who owns the PROCESS rather than merely where it is.
type McpTransport =
    | McpHttp of url: string

module McpTransport =

    let describe =
        function
        | McpHttp url -> url

/// One server, as the operator described it.
///
/// There is deliberately no `Headers` or credential field. This round's servers are loopback
/// and unauthenticated, and when one needs a credential it belongs in Connections/Secrets —
/// which already own who may read one and inject it — not on a declaration, where a token
/// would sit in the Manager's state file in the clear and be copied to every session the
/// audience names. The absence is the point: a field that exists gets filled in.
type McpServerRef =
    { Name : McpServerName
      Transport : McpTransport
      /// One sentence, for the humans. NOT sent to the model — the model reads the tool
      /// descriptions the server itself supplies, which is a separate trust problem.
      Description : string }

/// WHO reaches it. The whole of the configuration model, because there is no second step: a
/// session this names has the server, from the moment it is declared until it is not.
type McpAudience =
    | AnySession
    | OneSession of SessionId

module McpAudience =

    /// Does this declaration reach that session?
    let reaches (id: SessionId) =
        function
        | AnySession -> true
        | OneSession one -> one = id

/// A server, and who may reach it. Host-wide and session-scoped declarations are the same
/// kind of fact — somebody with operator authority said this server exists — differing only
/// in who reaches it, so they live in one list.
type McpDeclaration =
    { Server : McpServerRef
      Audience : McpAudience }

module McpDeclaration =

    /// The servers ONE session gets. Pure, total, and the only place the question is
    /// answered, so "why can the agent see this tool" has exactly one answer.
    let resolve (declared: McpDeclaration list) (id: SessionId) : McpServerRef list =
        declared
        |> List.filter (fun d -> McpAudience.reaches id d.Audience)
        |> List.map (fun d -> d.Server)

    /// Could these two ever land in one session's resolved set?
    let private overlap (a: McpAudience) (b: McpAudience) =
        match a, b with
        | AnySession, _
        | _, AnySession -> true
        | OneSession x, OneSession y -> x = y

    /// May this be added? A name clash is REFUSED at declare time, when the operator is
    /// standing there and can pick another name — not resolved at read time by a precedence
    /// rule, which would mean a session silently getting a different server than the one an
    /// operator thought they were looking at.
    ///
    /// That is also what keeps `ToolRegistry.merge`'s refusal honest: after resolution names
    /// are unique by construction, so a collision at runtime is necessarily a BUG — two
    /// registries claiming one namespace — rather than a configuration choice somebody made.
    let admit (declared: McpDeclaration list) (candidate: McpDeclaration) : Result<McpDeclaration list, string> =
        let clash =
            declared
            |> List.tryFind (fun d ->
                d.Server.Name = candidate.Server.Name && overlap d.Audience candidate.Audience)
        match clash with
        | Some _ ->
            Error (
                sprintf
                    "a server called '%s' is already declared for that session"
                    (McpServerName.value candidate.Server.Name))
        | None -> Ok (declared @ [ candidate ])

    /// Withdraw one. By name AND audience, because the same name may legitimately be
    /// declared for two different sessions — removing "the one called `serial`" would be
    /// ambiguous exactly when it matters.
    let withdraw (name: McpServerName) (audience: McpAudience) (declared: McpDeclaration list) : McpDeclaration list =
        declared |> List.filter (fun d -> not (d.Server.Name = name && d.Audience = audience))

/// One frame of `/control/mcp`: the whole resolved set for THIS session, every time.
/// Snapshots, never deltas — a reconnect is then the entire recovery protocol.
type McpServerSet = { Servers : McpServerRef list }

module McpServerSet =

    let empty : McpServerSet = { Servers = [] }

    let names (set: McpServerSet) : McpServerName list = set.Servers |> List.map (fun s -> s.Name)

// ---------------------------------------------------------------------------------------
// Talking to one (Plan 17, step 3).
//
// The envelopes, and only the envelopes. Everything below is a value with a codec and no
// idea a socket exists, which is what lets the whole protocol be tested without one — the
// Host supplies the POSTing, the retry policy and the connection bookkeeping.
// ---------------------------------------------------------------------------------------

module McpProtocol =

    /// The revision we speak. Sent on `initialize`; a server that answers with one we do
    /// not know is recorded as unreachable rather than argued with.
    [<Literal>]
    let Version = "2025-06-18"

    /// The header a server uses to name a session it wants us to keep quoting, and the one
    /// we echo the negotiated revision back on.
    [<Literal>]
    let SessionIdHeader = "mcp-session-id"

    [<Literal>]
    let VersionHeader = "mcp-protocol-version"

/// One JSON-RPC request. `Params` is raw JSON text for the same reason a tool's arguments
/// are: what is inside is the callee's business, and a per-method record would make this
/// module know every method there will ever be.
type JsonRpcRequest =
    { Id : int
      Method : string
      Params : string option }

/// What came back. `JsonRpcResult`'s payload is raw JSON text, decoded by whoever knows
/// what the method answers.
///
/// A NOTIFICATION gets no response at all, which is why `notifications/initialized` is a
/// separate call on the client rather than a request with an id nobody answers.
type JsonRpcResponse =
    | JsonRpcResult of id: int * result: string
    | JsonRpcFailure of id: int option * code: int * message: string

/// What `initialize` answered, reduced to what we use.
///
/// `capabilities` is dropped because we ask for nothing conditional on it — we call
/// `tools/list` and find out. `instructions` is dropped DELIBERATELY: MCP lets a server
/// return prose intended for the model's system prompt, and that is a prompt-injection
/// surface in its most direct form — a string from an external process, concatenated ahead
/// of everything the operator wrote. Tool descriptions we cannot avoid (the model must read
/// them to call anything); this we can, so we do.
type McpHandshake =
    { ProtocolVersion : string
      ServerName : string
      ServerVersion : string }

/// What `tools/call` answered, flattened. MCP returns a list of content blocks; the model
/// reads text, so text is what is kept and anything else is named rather than dropped
/// silently.
///
/// `IsError` is the TOOL's own failure flag — a call that reached a tool and went badly.
/// That is `ToolCallOk` in our record with text saying so, and it is not the same thing as
/// a JSON-RPC error, which means the call never reached a tool at all.
type McpCallResult =
    { Text : string
      IsError : bool
      /// The result's `_meta`, verbatim, when it had one — raw JSON text, carried the way
      /// `McpTool.InputSchema` is carried, so a boundary type does not have to know what
      /// every key in it means. Exactly one key is read (Plan 19's stream offer); the rest
      /// crosses and is ignored.
      Meta : string option }

/// How one declared server is actually doing, from the process that is actually connected
/// to it. What the `mcp_servers` query reports, and the reason there is no add-time
/// reachability probe: a second MCP client answering this question worse.
type McpServerStatus =
    | McpConnected of tools: int
    /// Not there, or not speaking. Retried on a backoff forever — a device unplugged now
    /// may be plugged in later — so this is a state, never a terminal failure.
    | McpUnreachable of reason: string

module McpServerStatus =

    let describe =
        function
        | McpConnected _ -> "connected"
        | McpUnreachable _ -> "unreachable"

    let toolCount =
        function
        | McpConnected count -> count
        | McpUnreachable _ -> 0

/// One row of what a session can see about its servers.
type McpServerHealth =
    { Server : McpServerRef
      Status : McpServerStatus }

// ---------------------------------------------------------------------------------------
// A set that changes is a fact a later turn needs (Plan 17, step 4).
// ---------------------------------------------------------------------------------------

module McpNotes =

    /// What this session was LAST TOLD it had, folded from its own events.
    ///
    /// The comparison basis is the log rather than an in-memory previous set, and that is
    /// the whole design of this: a boot emits nothing (the registry is simply built), a
    /// reconnect emits nothing, a process restart emits nothing — and only a genuine change
    /// by the operator produces a note. Comparing against memory would re-announce
    /// everything after every restart, which is exactly the noise that teaches people to
    /// stop reading the timeline.
    let announced (events: SessionEvent seq) : Set<string> =
        events
        |> Seq.fold
            (fun known event ->
                match event with
                | SessionEvent.McpServerAvailable m -> Set.add (McpServerName.value m.Name) known
                | SessionEvent.McpServerUnavailable m -> Set.remove (McpServerName.value m.Name) known
                | _ -> known)
            Set.empty

    /// The notes a newly resolved set owes, given what was announced before. Names only,
    /// in a stable order, so a caller mints one `MessageId` per note and appends.
    ///
    /// Note that this is about the DECLARATION, not the connection: a server that is
    /// declared but unreachable is still announced, because the declaration is what
    /// changed and its reachability is the query's business. A provider that dies
    /// mid-session moves a status and produces no note at all.
    let delta (announced: Set<string>) (resolved: McpServerSet) : McpServerName list * McpServerName list =
        let now = resolved.Servers |> List.map (fun s -> s.Name)
        let nowNames = now |> List.map McpServerName.value |> Set.ofList
        let gained = now |> List.filter (fun name -> not (Set.contains (McpServerName.value name) announced))
        let lost =
            Set.difference announced nowNames
            |> Set.toList
            |> List.choose (fun name ->
                match McpServerName.create name with
                | Ok name -> Some name
                // A name in the log that no longer parses can only mean the charset was
                // tightened under a live session. There is nothing to announce about a
                // server nobody can declare any more, so it is dropped rather than crashing
                // a fold that every client runs.
                | Error _ -> None)
        gained, lost
