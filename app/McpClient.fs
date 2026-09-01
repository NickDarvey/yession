module Yession.Host.McpClient

// The session as an MCP client (Plan 17, step 3).
//
// Plan 16 settled that the SESSION talks to a declared server, not the Manager, and this is
// that half. It is an ordinary MCP client over Streamable HTTP — deliberately ordinary,
// because the point of the whole plan is that a provider can be anyone's. Nothing here is
// Yession-shaped except which servers it is pointed at.
//
// The split follows the repo's existing one: the envelopes are Thoth codecs in the Domain
// (testable with no socket), and what lives here is the POSTing, the header bookkeeping and
// the connection lifecycle. That is the same division `Sse.fs` draws.

open System
open Fable.Core
open Yession.Domain
open Yession.Domain.Terminals
open Yession.Domain.Tools

/// What one POST came back as.
type private PostOutcome =
    /// Did the request REACH the server? `false` is a transport failure (nothing listening,
    /// DNS, a socket dropped); an HTTP error status is `ok = true` with that status.
    abstract ok : bool
    abstract status : int
    abstract session : string
    abstract body : string
    abstract reason : string

/// POST one JSON-RPC frame and hand back the status, the response headers we care about,
/// and the body. Distinct from `Interop.postText` because all three matter: a `404` is a
/// restarted provider rather than a failure, and `mcp-session-id` is what a stateful server
/// asks us to quote back.
///
/// `accept` names both content types the spec allows a Streamable HTTP server to answer
/// with — a server picks, and a client that offered only one would work against half of
/// them.
[<Emit("""(async function (url, session, protocol, body) {
  try {
    const headers = { 'content-type': 'application/json', 'accept': 'application/json, text/event-stream' }
    if (session) headers['mcp-session-id'] = session
    if (protocol) headers['mcp-protocol-version'] = protocol
    const r = await fetch(url, { method: 'POST', headers, body: body })
    const text = await r.text()
    return { ok: true, status: r.status, session: r.headers.get('mcp-session-id') || '', body: text, reason: '' }
  } catch (err) {
    return { ok: false, status: 0, session: '', body: '', reason: String((err && err.message) || err) }
  }
})($0, $1, $2, $3)""")>]
let private post (url: string) (session: string) (protocol: string) (body: string) : JS.Promise<PostOutcome> =
    jsNative

/// Streamable HTTP lets a server answer a POST with either `application/json` or an SSE
/// stream carrying the same frame. We do not open the optional GET stream (see below), so a
/// response body is always ONE frame either way — which makes unwrapping it this small.
let private frameOf (body: string) : string =
    let trimmed = body.Trim ()
    if trimmed.StartsWith "{" || trimmed.StartsWith "[" then trimmed
    else
        // SSE-framed: the `data:` lines of the one event, rejoined.
        trimmed.Replace("\r\n", "\n").Split ([| "\n\n" |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose Sse.dataOf
        |> Array.tryHead
        |> Option.defaultValue trimmed

/// A live connection to one server. Everything mutable about talking to it lives here, so
/// the connection map below holds values rather than coordinating state.
type private Connection =
    { Server : McpServerRef
      mutable SessionId : string
      mutable NextId : int
      mutable Tools : ToolDescriptor list
      mutable Status : McpServerStatus }

/// Every server this session currently has, and the tools they answer with. One value the
/// composition holds; a set frame goes in, and what a turn's registry is built from comes
/// out.
type McpConnections =
    { /// Reconcile against a newly resolved set. Unchanged servers keep their connection
      /// (and their `Mcp-Session-Id`); new ones are handshaked; removed ones are dropped.
      Apply : McpServerSet -> Async<unit>
      /// The foreign registries, one per CONNECTED server, ready for
      /// `ToolRegistry.mergeAll`. A server that is down contributes nothing and fails
      /// nothing — the agent simply does not have those tools.
      Registries : unit -> ToolRegistry list
      /// How each declared server is doing, in declaration order. What the `mcp_servers`
      /// query reports.
      Health : unit -> McpServerHealth list
      /// Re-ask every declared server what it has. `true` when anything moved — a server
      /// came up, went away, or changed its tool list — so a caller knows to invalidate
      /// the query and nothing redraws on a tick where nothing happened.
      ///
      /// This is the whole liveness story, and it replaces the GET stream the plan
      /// considered. Two things it buys that a one-shot connect cannot:
      ///
      ///   * a provider that is not there when its declaration arrives — started later,
      ///     restarted, or a device host that came back — is picked up. A bounded backoff
      ///     gives up; hardware does not stay gone on a schedule.
      ///   * a tool list that CHANGES while attached is noticed, which is what a provider
      ///     needs to be able to grow tools as devices are plugged in.
      Poll : unit -> Async<bool> }

module McpConnections =

    /// A session that was given no servers, and the composition default. Not an error
    /// state: a host with nothing declared is the ordinary host.
    let none : McpConnections =
        { Apply = fun _ -> async { return () }
          Registries = fun () -> []
          Health = fun () -> []
          Poll = fun () -> async { return false } }

/// How often a session re-asks its servers what they have.
///
/// One mechanism rather than two: an earlier draft retried the FIRST connect on a
/// `Resilience` backoff and left everything after it to a GET stream we did not open, which
/// meant a provider that appeared thirty seconds late was unreachable for the rest of the
/// session. The poll is that backoff's steady state and the stream's replacement at once, so
/// there is one answer to "when does this notice" instead of two that can disagree.
///
/// Ten seconds is chosen against a HUMAN plugging something in: long enough that a handful
/// of sessions do not hammer a provider, short enough that "I plugged it in, ask again"
/// is not advice anyone has to give.
///
/// And the GET stream stays unopened, not merely unbuilt. `list_changed` lands within one
/// interval anyway, resources and prompts are unused, a long call's progress notifications
/// ride the POST's own SSE response, and server→client requests are unsupported — so a
/// stream alongside this poll would be two mechanisms noticing one fact. Nor can it replace
/// the poll, which is also reachability, reconnect and the provider that appears thirty
/// seconds late, and which a conforming server may refuse outright. Skipping the poll for a
/// streamed connection is the only route back to one mechanism and is worse: it assumes
/// every provider counts an open stream as liveness, which is exactly the assumption
/// `examples/jumpstarter` had to be fixed for.
let PollIntervalMs = 10000

/// Build the connection manager.
let create () : McpConnections =
    // Declaration order is the set's order, which is the operator's order — preserved so
    // the query's rows do not shuffle between reads.
    let mutable connections : Connection list = []

    let urlOf (server: McpServerRef) =
        match server.Transport with
        | McpHttp url -> url

    // ---- one request ----------------------------------------------------------------
    //
    // Answers `Error` for anything that is not a decoded JSON-RPC result: a transport
    // failure, a non-2xx status, an undecodable body, or a JSON-RPC error frame. The
    // 404-means-restarted case is handled a level up, because only a caller that is in the
    // middle of a request knows whether re-handshaking and retrying is the right response.
    let request (connection: Connection) (method: string) (parameters: string option) : Async<Result<string, string>> =
        async {
            let id = connection.NextId
            connection.NextId <- id + 1
            let body = Codec.toString Codec.jsonRpcRequest { Id = id; Method = method; Params = parameters }
            let! outcome =
                post (urlOf connection.Server) connection.SessionId McpProtocol.Version body
                |> Async.AwaitPromise
            if not outcome.ok then return Error outcome.reason
            elif outcome.status = 404 then return Error "404"
            elif outcome.status < 200 || outcome.status >= 300 then
                return Error (sprintf "the server answered %d" outcome.status)
            else
                // A server that names a session wants it quoted on everything after.
                if outcome.session <> "" then connection.SessionId <- outcome.session
                match Codec.fromString Codec.jsonRpcResponse (frameOf outcome.body) with
                | Error e -> return Error (sprintf "could not read the reply to %s: %s" method e)
                | Ok (JsonRpcFailure (_, code, message)) ->
                    return Error (sprintf "%s failed (%d): %s" method code message)
                | Ok (JsonRpcResult (_, result)) -> return Ok result
        }

    /// A notification: a method with no id, so the server does not answer. Its outcome is
    /// not inspected beyond reachability — there is nothing to inspect.
    let notify (connection: Connection) (method: string) : Async<unit> =
        async {
            let! _ =
                post (urlOf connection.Server) connection.SessionId McpProtocol.Version (Codec.jsonRpcNotification method)
                |> Async.AwaitPromise
            return ()
        }

    // ---- the lifecycle ----------------------------------------------------------------

    /// One of a server's tools as a descriptor. `foreign`, which is what suppresses argument
    /// recording: we did not write this schema and cannot trust it to mark its own secrets.
    let foreignOf (connection: Connection) (tool: McpTool) : ToolDescriptor =
        ToolDescriptor.foreign
            (McpServerName.value connection.Server.Name)
            tool.Name
            (Option.defaultValue "" tool.Description)
            tool.InputSchema

    /// MCP's lifecycle, unmodified: `initialize`, then `notifications/initialized` (the
    /// spec requires it before ordinary requests, and a provider that enforces it will
    /// reject `tools/list` without one), then the tools.
    ///
    /// We declare NO client capabilities. A client that declared `sampling` would be
    /// offering the provider a way to drive the model, which is the opposite of what a
    /// proxied server is for.
    let handshake (connection: Connection) : Async<Result<ToolDescriptor list, string>> =
        async {
            connection.SessionId <- ""
            let initialize = Codec.mcpInitializeParams Version.current
            match! request connection "initialize" (Some initialize) with
            | Error e -> return Error e
            | Ok result ->
                match Codec.fromString Codec.mcpHandshake result with
                | Error e -> return Error (sprintf "could not read the handshake: %s" e)
                | Ok handshake when handshake.ProtocolVersion <> McpProtocol.Version ->
                    // A refusal, recorded as this server's status — not an exception, and
                    // not a session that fails to boot.
                    return
                        Error (
                            sprintf
                                "the server speaks MCP %s and we speak %s"
                                handshake.ProtocolVersion
                                McpProtocol.Version)
                | Ok _ ->
                    do! notify connection "notifications/initialized"
                    match! request connection "tools/list" None with
                    | Error e -> return Error e
                    | Ok listed ->
                        match Codec.fromString Codec.mcpToolList listed with
                        | Error e -> return Error (sprintf "could not read the tool list: %s" e)
                        | Ok list -> return Ok (list.Tools |> List.map (foreignOf connection))
        }

    /// One tool call, with the one retry the plan allows. A `404` on a session id is the
    /// provider saying it RESTARTED, which is a handshake rather than an outage: re-run the
    /// lifecycle once and retry the call. A second `404` is a failure and is reported as
    /// one, so a genuinely broken provider cannot loop.
    let callTool (connection: Connection) (name: string) (arguments: string) : Async<Result<ToolAnswer, string>> =
        let parameters = Codec.mcpCallParams name arguments
        let read (result: string) =
            match Codec.fromString Codec.mcpCallResult result with
            | Error e -> Error (sprintf "could not read what %s answered: %s" name e)
            // A tool that RAN and went badly is `Ok` with text saying so — the model is
            // meant to read it and choose differently. Only a call that never reached a
            // tool is an `Error`.
            | Ok answer ->
                // A stream the provider offered (Plan 19). Admitted HERE because this is
                // where the server's declared url is known — the thing an offered url has
                // to agree with — and never in the terminal manager, which would have to be
                // told which server an offer came from to ask the same question.
                match answer.Meta |> Option.bind Codec.streamOffer with
                | None -> Ok (ToolAnswer.text answer.Text)
                | Some offer ->
                    let fallback = sprintf "%s/%s" (McpServerName.value connection.Server.Name) name
                    match StreamOffer.admit (McpTransport.describe connection.Server.Transport) (StreamOffer.named fallback offer) with
                    | Ok admitted -> Ok { ToolAnswer.text answer.Text with Stream = Some admitted }
                    | Error reason ->
                        // Said in the answer rather than logged: a stream that will not open
                        // is a fact the model has to act on, and silence would read as a
                        // terminal that simply has not appeared yet.
                        Ok (ToolAnswer.text (answer.Text + "\n\nThis session refused the stream that was offered: " + reason))
        async {
            match! request connection "tools/call" (Some parameters) with
            | Ok result -> return read result
            | Error "404" ->
                match! handshake connection with
                | Error e ->
                    connection.Status <- McpUnreachable e
                    return Error e
                | Ok tools ->
                    connection.Tools <- tools
                    connection.Status <- McpConnected (List.length tools)
                    match! request connection "tools/call" (Some parameters) with
                    | Ok result -> return read result
                    | Error "404" -> return Error "the server keeps forgetting its session; giving up on this call"
                    | Error e -> return Error e
            | Error e -> return Error e
        }

    /// Bring one connection up, or record why not. ONE attempt: a failure is not terminal
    /// because the poll comes back, so retrying here would be the same waiting done twice.
    /// Every outcome updates the status rather than logging — what an operator sees is the
    /// `mcp_servers` query.
    ///
    /// Returns whether the registry moved, which is what the poll reports upward.
    let connect (connection: Connection) : Async<bool> =
        async {
            let before = connection.Tools
            match! handshake connection with
            | Ok tools ->
                connection.Tools <- tools
                connection.Status <- McpConnected (List.length tools)
                return tools <> before
            | Error e ->
                connection.Tools <- []
                connection.Status <- McpUnreachable e
                return not (List.isEmpty before)
        }

    /// Re-ask a server that is already connected. Cheaper than `connect` — no re-handshake,
    /// so its `Mcp-Session-Id` survives — and a `404` means it restarted underneath us,
    /// which is the one case that does re-handshake.
    let refresh (connection: Connection) : Async<bool> =
        async {
            let before = connection.Tools
            match! request connection "tools/list" None with
            | Error "404" -> return! connect connection
            | Error e ->
                connection.Tools <- []
                connection.Status <- McpUnreachable e
                return not (List.isEmpty before)
            | Ok listed ->
                match Codec.fromString Codec.mcpToolList listed with
                | Error e ->
                    connection.Tools <- []
                    connection.Status <- McpUnreachable (sprintf "could not read the tool list: %s" e)
                    return not (List.isEmpty before)
                | Ok list ->
                    let tools = list.Tools |> List.map (foreignOf connection)
                    connection.Tools <- tools
                    connection.Status <- McpConnected (List.length tools)
                    return tools <> before
        }

    /// One tick. Every declared server, connected or not — a server that is down is asked
    /// again (it may have come back) and one that is up is re-listed (its tools may have
    /// grown). Sequential rather than parallel: a handful of loopback servers is not worth
    /// the concurrency, and one slow provider delaying the next tick is the correct
    /// backpressure.
    let poll () : Async<bool> =
        async {
            let mutable moved = false
            for connection in connections do
                let! changed =
                    match connection.Status with
                    | McpConnected _ -> refresh connection
                    | McpUnreachable _ -> connect connection
                if changed then moved <- true
            return moved
        }

    // ---- reconciling a set frame ------------------------------------------------------

    let apply (set: McpServerSet) : Async<unit> =
        async {
            let existing = connections
            // Unchanged servers KEEP their connection. Rebuilding every client because one
            // was added would drop `Mcp-Session-Id`s and re-run handshakes for servers
            // nothing happened to — and a device claim behind one of them is not something
            // to churn. A server counts as unchanged only if its whole ref matches: a url
            // an operator corrected IS a different server.
            let arrived = ResizeArray ()
            // Rebuilt in the SET's order, which is the operator's order, so the query's rows
            // do not shuffle between reads.
            connections <-
                set.Servers
                |> List.map (fun server ->
                    match existing |> List.tryFind (fun c -> c.Server = server) with
                    | Some connection -> connection
                    | None ->
                        let connection =
                            { Server = server
                              SessionId = ""
                              NextId = 1
                              Tools = []
                              Status = McpUnreachable "not connected yet" }
                        arrived.Add connection
                        connection)
            // A removed server's connection is simply dropped, and its tools leave the
            // registry at the next turn. There is nothing to close: Streamable HTTP holds no
            // socket between requests, and we open no GET stream — and nothing closes a
            // terminal. The control leg and a data leg a provider offered have separate
            // lifetimes, joined one way by the attach ticket: withdrawing a server means no
            // NEW stream can be acquired from it, never that bytes already flowing stop. A
            // terminal outlives the declaration that produced it, and closing one does not
            // disturb this connection.
            for connection in arrived do
                let! _ = connect connection
                ()
        }

    let registries () : ToolRegistry list =
        connections
        |> List.filter (fun c -> not (List.isEmpty c.Tools))
        |> List.map (fun connection ->
            ToolRegistry.ofNamespace
                (McpServerName.value connection.Server.Name)
                connection.Tools
                (fun call -> callTool connection call.Name call.Arguments))

    { Apply = apply
      Registries = registries
      Health = fun () -> connections |> List.map (fun c -> { Server = c.Server; Status = c.Status })
      Poll = poll }

// ---------------------------------------------------------------------------------------
// Observability, for free (Plan 17, step 4).
//
// A QUERY, so registering it IS the UI change: `queriesSection` maps over whatever the
// session declared, and the WCAG floor is already held once in `queryValueView` for every
// query that will ever exist. No panel to write, no route to add.
//
// It is also the answer to "how does an operator find out they typo'd a url" — which is
// why there is no add-time reachability probe. A probe would be a second MCP client
// answering, worse, a question the process that is actually connected answers exactly.
// ---------------------------------------------------------------------------------------

let queryName : QueryName =
    match QueryName.create "mcp_servers" with
    | Ok name -> name
    | Error e -> failwithf "mcp servers query name: %s" e

let private queryDef : QueryDef =
    { Name = queryName
      Title = "MCP servers"
      Description =
        "The MCP servers this session was given, each with where it is, whether this \
         session is connected to it, and how many tools it is contributing. Read from the \
         connections themselves, so an unreachable server says so rather than being absent."
      Shape =
        Rows
            [ QueryColumn.create "name" "name"
              QueryColumn.create "transport" "address"
              QueryColumn.create "status" "status"
              QueryColumn.create "tools" "tools"
              QueryColumn.create "description" "description" ] }

/// Register the session's servers as a query. It takes a GETTER for the same reason the
/// sandboxes query does: the query surface is composed before the Host is started, and the
/// Host is what builds the connections.
let query (current: unit -> McpConnections) : Queries.QueryRegistration =
    { Def = queryDef
      Read =
        fun () ->
            async {
                return
                    Ok (RowsOf (
                        (current ()).Health ()
                        |> List.map (fun health ->
                            [ "name", CellText (McpServerName.value health.Server.Name)
                              "transport", CellText (McpTransport.describe health.Server.Transport)
                              // The STATUS word, and — when it is bad news — why. An
                              // operator reading "unreachable" learns nothing they can act
                              // on; "unreachable: ECONNREFUSED" names the typo.
                              "status",
                              CellText (
                                  match health.Status with
                                  | McpConnected _ -> "connected"
                                  | McpUnreachable reason -> sprintf "unreachable: %s" reason)
                              "tools", CellText (string (McpServerStatus.toolCount health.Status))
                              "description",
                              (match health.Server.Description with
                               | None -> CellAbsent
                               | Some description -> CellText description) ])))
            } }
