namespace Yession.Manager

open System
open Yession.Domain
open Yession.Domain.Tools

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
      DataDir : string
      /// When an operator archived this session, if they have. Archiving retires a session
      /// from the working list and from the set of things that can run; it deletes nothing,
      /// so the event log and doc sidecar under `DataDir` stay exactly where they are.
      ///
      /// A Manager-only durable fact: the session itself is never told, and nothing on the
      /// control channel carries it. `DateTimeOffset option` rather than a flag because
      /// "when" is the question a later ordering will ask, and a bool cannot be widened
      /// into it without a schema break.
      ArchivedAt : DateTimeOffset option }

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

    /// The record a launch may use. The lookup and the refusal are ONE verb: a caller that
    /// could find a record without learning it is archived is the caller that starts one,
    /// and there are four of them (the launch route, `/open`, boot, tests).
    let launchable (sessionId: SessionId) (state: ManagerState) : Result<SessionRecord, string> =
        match tryFind sessionId state with
        | None -> Error (sprintf "unknown session %s" (SessionId.value sessionId))
        | Some record when record.ArchivedAt.IsSome ->
            Error (sprintf "session %s is archived — unarchive it to start it" (SessionId.value sessionId))
        | Some record -> Ok record

    /// Archive a session. Idempotent — the FIRST archival's timestamp stands, because "when
    /// was this archived" has one answer and a repeated click is not a new one. An
    /// unregistered session is an error: nothing was archived, so saying `Ok` would lie.
    let archive (sessionId: SessionId) (at: DateTimeOffset) (state: ManagerState) : Result<ManagerState, string> =
        match tryFind sessionId state with
        | None -> Error (sprintf "unknown session %s" (SessionId.value sessionId))
        | Some record when record.ArchivedAt.IsSome -> Ok state
        | Some _ ->
            Ok
                { state with
                    Sessions =
                        state.Sessions
                        |> List.map (fun s -> if s.SessionId = sessionId then { s with ArchivedAt = Some at } else s) }

    /// Unarchive. A session that is not archived is already in the asked-for state, so this
    /// is `Ok` — the same posture `setDisplayName` takes toward a no-op rename. An
    /// unregistered one is still an error, for the same reason `archive` refuses it.
    let unarchive (sessionId: SessionId) (state: ManagerState) : Result<ManagerState, string> =
        match tryFind sessionId state with
        | None -> Error (sprintf "unknown session %s" (SessionId.value sessionId))
        | Some record when record.ArchivedAt.IsNone -> Ok state
        | Some _ ->
            Ok
                { state with
                    Sessions =
                        state.Sessions
                        |> List.map (fun s -> if s.SessionId = sessionId then { s with ArchivedAt = None } else s) }

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

/// Whether an operator has archived a session — the only filter axis there is today.
type ArchiveState =
    | Active
    | Archived

/// How the registry is ordered. Two cases rather than a key/direction pair, because
/// `CreatedAt` is the only key a record HAS to sort by; when last-activity or a summary
/// lands it adds cases here and a sortable column header there, and neither has to be
/// redesigned first.
type SessionOrder =
    | NewestFirst
    | OldestFirst

/// What a reader of the registry asked to see. `Show` is a set, so "neither state" is
/// representable and means exactly what it says — nothing shown. That is the honest
/// reading of two cleared filters, and the surface renders it as an empty state with the
/// controls that caused it sitting right above.
type SessionQuery =
    { Show : Set<ArchiveState>
      Order : SessionOrder }

/// Filtering and ordering are rules over the REGISTRY, so they live with it: pure, and
/// inside the cheap tier. Nothing about them belongs in a render function or a route.
module SessionQuery =

    let defaults : SessionQuery = { Show = Set.singleton Active; Order = NewestFirst }

    let stateOf (record: SessionRecord) : ArchiveState =
        match record.ArchivedAt with
        | Some _ -> Archived
        | None -> Active

    /// The spelling of an empty `Show`. A key has to be PRESENT to mean "no states" —
    /// absent means "you did not choose", which is the default — so the empty set needs a
    /// word, and a word beats a bare `show=` in a URL somebody has bookmarked.
    let private noneToken = "none"

    let private showToken =
        function
        | Active -> "active"
        | Archived -> "archived"

    let private sortToken =
        function
        | NewestFirst -> "created-desc"
        | OldestFirst -> "created-asc"

    /// In a fixed order, so a query has ONE spelling and a round-trip is an equality.
    let private showOrder = [ Active; Archived ]

    /// Split `a=1&b=2` (with or without a leading `?`, and with or without the path in
    /// front of it) into pairs. Deliberately no percent-decoding: every value this query
    /// has a word for is plain ASCII, so an encoded one cannot be one of them either way.
    let private pairsOf (query: string) : (string * string) list =
        let afterMark =
            match query.IndexOf '?' with
            | -1 -> query
            | at -> query.Substring (at + 1)
        afterMark.Split '&'
        |> Array.toList
        |> List.choose (fun part ->
            match part.IndexOf '=' with
            | -1 -> if part = "" then None else Some (part, "")
            | at -> Some (part.Substring (0, at), part.Substring (at + 1)))

    /// TOTAL, deliberately. An axis whose key is absent takes its default; a key that IS
    /// present names what it names, and a word neither side knows is ignored rather than
    /// refused. A query string is what somebody bookmarked, and refusing one shows them no
    /// list at all — which is why this is not shaped like `SecretsMode.ofName`, where an
    /// unknown word is an operator declaring a posture that does not exist and must fail.
    let ofQueryString (query: string) : SessionQuery =
        let pairs = pairsOf query
        let valuesOf key = pairs |> List.filter (fst >> (=) key) |> List.map snd
        let show =
            match valuesOf "show" with
            | [] -> defaults.Show
            | given ->
                given
                |> List.collect (fun value -> value.Split ',' |> Array.toList)
                |> List.choose (fun token ->
                    match token.Trim () with
                    | "active" -> Some Active
                    | "archived" -> Some Archived
                    | _ -> None)
                |> Set.ofList
        let order =
            match valuesOf "sort" |> List.tryLast with
            | Some "created-asc" -> OldestFirst
            | Some "created-desc" -> NewestFirst
            | _ -> defaults.Order
        { Show = show; Order = order }

    /// The canonical spelling. The SERVER computes every href the surface links to, so
    /// this is the one encoder and the page's script never builds a URL.
    let toQueryString (query: SessionQuery) : string =
        let shown =
            match showOrder |> List.filter (fun state -> query.Show.Contains state) with
            | [] -> [ noneToken ]
            | states -> states |> List.map showToken
        (shown |> List.map (sprintf "show=%s")) @ [ sprintf "sort=%s" (sortToken query.Order) ]
        |> String.concat "&"

    /// The same query with one archive state flipped — what a filter control links TO.
    let toggling (state: ArchiveState) (query: SessionQuery) : SessionQuery =
        { query with
            Show = if query.Show.Contains state then Set.remove state query.Show else Set.add state query.Show }

    /// The same query ordered the other way — what a sortable column header links TO.
    let reversed (query: SessionQuery) : SessionQuery =
        { query with
            Order =
                match query.Order with
                | NewestFirst -> OldestFirst
                | OldestFirst -> NewestFirst }

    /// Filter, then order. Takes a projection so it serves a `SessionRecord list` and the
    /// `SessionView list` the management surface holds alike, without this module having to
    /// know what a view is.
    let apply (query: SessionQuery) (recordOf: 'a -> SessionRecord) (items: 'a list) : 'a list =
        let kept = items |> List.filter (fun item -> query.Show.Contains (stateOf (recordOf item)))
        match query.Order with
        | NewestFirst -> kept |> List.sortByDescending (fun item -> (recordOf item).CreatedAt)
        | OldestFirst -> kept |> List.sortBy (fun item -> (recordOf item).CreatedAt)

/// The explicit wire codec for the Manager's state — the ONLY way it touches storage
/// (same discipline as the event envelope). Hand-written so private constructors are
/// honoured; decoding tolerates unknown fields, so a newer schema's file still loads.
module ManagerCodec =

    let private sessionRecord : Codec<SessionRecord> =
        { Encode =
            fun (s: SessionRecord) ->
                Encode.object
                    ([ "sessionId", Codec.sessionId.Encode s.SessionId
                       "displayName", Encode.string s.DisplayName
                       "createdAt", Codec.timestamp.Encode s.CreatedAt
                       "dataDir", Encode.string s.DataDir ]
                     // Written only when it is set, so an active session's record stays
                     // byte-for-byte what it has always been and "no such field" keeps one
                     // meaning on both sides of this change.
                     @ (match s.ArchivedAt with
                        | Some at -> [ "archivedAt", Codec.timestamp.Encode at ]
                        | None -> []))
          Decode =
            Decode.object (fun get ->
                { SessionRecord.SessionId = get.Required.Field "sessionId" Codec.sessionId.Decode
                  SessionRecord.DisplayName = get.Required.Field "displayName" Decode.string
                  SessionRecord.CreatedAt = get.Required.Field "createdAt" Codec.timestamp.Decode
                  SessionRecord.DataDir = get.Required.Field "dataDir" Decode.string
                  // OPTIONAL, like `mcpServers` below and for the same reason: a file
                  // written before archiving existed has no such field, and every session
                  // in it IS active. That is a true reading, not a migration — which is why
                  // `currentVersion` does not move for it.
                  SessionRecord.ArchivedAt = get.Optional.Field "archivedAt" Codec.timestamp.Decode }) }

    /// The one schema this build knows how to read. A version's decoder produces the
    /// CURRENT `ManagerState`, so when a break eventually happens the old version's decoder
    /// IS its migration — reading the old shape and filling the new one's defaults — and
    /// there is no separate ladder to keep in step with the decoders.
    let private version1 : Decoder<ManagerState> =
        Decode.object (fun get ->
            { ManagerState.Version = ManagerState.currentVersion
              ManagerState.Sessions = get.Required.Field "sessions" (Decode.list sessionRecord.Decode)
              // OPTIONAL on the way in: a state file written before Plan 17 has no such
              // field, and a Manager with no declarations is the ordinary starting
              // state rather than a migration.
              ManagerState.McpServers =
                get.Optional.Field "mcpServers" (Decode.list Codec.mcpDeclaration.Decode)
                |> Option.defaultValue [] })

    let private decoderFor (version: int) : Decoder<ManagerState> option =
        if version = ManagerState.currentVersion then Some version1 else None

    let managerState : Codec<ManagerState> =
        { Encode =
            fun (s: ManagerState) ->
                Encode.object
                    [ "version", Encode.int s.Version
                      "sessions", s.Sessions |> List.map sessionRecord.Encode |> Encode.list
                      "mcpServers", s.McpServers |> List.map Codec.mcpDeclaration.Encode |> Encode.list ]
          // Version-DISPATCHED, which is what the `Version` field was reserved for. Unknown
          // fields are still tolerated within a version (that is how an optional field like
          // `archivedAt` arrives without a break), but a file naming a version this build
          // has never heard of is refused rather than read as the newest one it knows —
          // otherwise a downgrade decodes a newer file, silently drops everything it does
          // not recognise, and saves the loss back over the original.
          Decode =
            Decode.field "version" Decode.int
            |> Decode.andThen (fun version ->
                match decoderFor version with
                | Some decoder -> decoder
                | None ->
                    Decode.fail (
                        sprintf
                            "manager state schema version %d was written by a newer build (this one reads %d)"
                            version
                            ManagerState.currentVersion)) }

    let toString (state: ManagerState) : string =
        managerState.Encode state |> Encode.toString 2

    let fromString (json: string) : Result<ManagerState, string> =
        Decode.fromString managerState.Decode json
