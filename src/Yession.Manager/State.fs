namespace Yession.Manager

open System
open Yession.Domain

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// The Manager's durable state (Phase 4, Step 22): the session registry alone.
/// Runtime facts — pid, running/stopped — are deliberately NOT here; they are reconciled
/// at boot (children die with the Manager), never persisted.
type SessionRecord =
    { SessionId : SessionId
      DisplayName : string
      CreatedAt : DateTimeOffset
      /// Directory name under the Manager's data dir holding the session's stores
      /// (event log + doc sidecar).
      DataDir : string }

type ManagerState =
    { /// Schema version — the migration hook for the eventual SQLite move.
      Version : int
      /// In creation order.
      Sessions : SessionRecord list
      /// The MCP servers an operator has declared (Plan 17). Host-wide and session-scoped
      /// alike, in ONE list, because they are the same kind of fact — somebody with
      /// operator authority said this server exists — differing only in who reaches it.
      ///
      /// Deliberately NOT on `SessionRecord`: a session does not select from these, it
      /// simply gets every one that names it, so there is nothing per-session to store.
      McpServers : McpDeclaration list }

module ManagerState =

    let currentVersion = 1

    let empty : ManagerState = { Version = currentVersion; Sessions = []; McpServers = [] }

    let tryFind (sessionId: SessionId) (state: ManagerState) : SessionRecord option =
        state.Sessions |> List.tryFind (fun s -> s.SessionId = sessionId)

    /// Add a session; rejects duplicates (session ids are the registry key).
    let addSession (record: SessionRecord) (state: ManagerState) : Result<ManagerState, string> =
        if state.Sessions |> List.exists (fun s -> s.SessionId = record.SessionId) then
            Error (sprintf "session %s already exists" (SessionId.value record.SessionId))
        else
            Ok { state with Sessions = state.Sessions @ [ record ] }

    /// Rename a session's display name (the session's self-assigned title, reported over the
    /// control channel). A no-op if the session is not registered; the registry key
    /// (`SessionId`) never changes, so this only ever touches `DisplayName`.
    let setDisplayName (sessionId: SessionId) (displayName: string) (state: ManagerState) : ManagerState =
        { state with
            Sessions =
                state.Sessions
                |> List.map (fun s -> if s.SessionId = sessionId then { s with DisplayName = displayName } else s) }

    /// Declare an MCP server (Plan 17). Refuses a name any one session would then see
    /// twice — the check belongs HERE, on the way in, where the operator can pick another
    /// name, rather than in a precedence rule at read time.
    let declareMcpServer (declaration: McpDeclaration) (state: ManagerState) : Result<ManagerState, string> =
        McpDeclaration.admit state.McpServers declaration
        |> Result.map (fun declared -> { state with McpServers = declared })

    /// Withdraw one. By name AND audience: the same name may legitimately be declared for
    /// two different sessions, and "the one called `serial`" would be ambiguous exactly
    /// when it matters.
    let withdrawMcpServer (name: McpServerName) (audience: McpAudience) (state: ManagerState) : ManagerState =
        { state with McpServers = McpDeclaration.withdraw name audience state.McpServers }

    /// The servers one session gets — the whole of what `/control/mcp` carries to it.
    let mcpServersFor (sessionId: SessionId) (state: ManagerState) : McpServerSet =
        { Servers = McpDeclaration.resolve state.McpServers sessionId }

/// The explicit wire codec for the Manager's state — the ONLY way it touches storage
/// (same discipline as the event envelope). Hand-written so private constructors are
/// honoured; decoding tolerates unknown fields, so a newer schema's file still loads.
module ManagerCodec =

    let private sessionRecord : Codec<SessionRecord> =
        { Encode =
            fun (s: SessionRecord) ->
                Encode.object
                    [ "sessionId", Codec.sessionId.Encode s.SessionId
                      "displayName", Encode.string s.DisplayName
                      "createdAt", Codec.timestamp.Encode s.CreatedAt
                      "dataDir", Encode.string s.DataDir ]
          Decode =
            Decode.object (fun get ->
                { SessionRecord.SessionId = get.Required.Field "sessionId" Codec.sessionId.Decode
                  SessionRecord.DisplayName = get.Required.Field "displayName" Decode.string
                  SessionRecord.CreatedAt = get.Required.Field "createdAt" Codec.timestamp.Decode
                  SessionRecord.DataDir = get.Required.Field "dataDir" Decode.string }) }

    let managerState : Codec<ManagerState> =
        { Encode =
            fun (s: ManagerState) ->
                Encode.object
                    [ "version", Encode.int s.Version
                      "sessions", s.Sessions |> List.map sessionRecord.Encode |> Encode.list
                      "mcpServers", s.McpServers |> List.map Codec.mcpDeclaration.Encode |> Encode.list ]
          Decode =
            Decode.object (fun get ->
                { ManagerState.Version = get.Required.Field "version" Decode.int
                  ManagerState.Sessions = get.Required.Field "sessions" (Decode.list sessionRecord.Decode)
                  // OPTIONAL on the way in: a state file written before Plan 17 has no such
                  // field, and a Manager with no declarations is the ordinary starting
                  // state rather than a migration.
                  ManagerState.McpServers =
                    get.Optional.Field "mcpServers" (Decode.list Codec.mcpDeclaration.Decode)
                    |> Option.defaultValue [] }) }

    let toString (state: ManagerState) : string =
        managerState.Encode state |> Encode.toString 2

    let fromString (json: string) : Result<ManagerState, string> =
        Decode.fromString managerState.Decode json
