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
      DataDir : string
      /// The session's PINNED port, when this Manager pins them (Plan 11). Unlike pid and
      /// the running flag, this is not a runtime fact reconciled at boot — it is the
      /// session's ADDRESS, and the browser partitions its storage by it, so it has to
      /// outlive the process that happened to bind it. `None` under `Ephemeral`, which is
      /// the default and means the OS assigns a fresh port per launch.
      Port : int option }

type ManagerState =
    { /// Schema version — the migration hook for the eventual SQLite move.
      Version : int
      /// In creation order.
      Sessions : SessionRecord list }

module ManagerState =

    let currentVersion = 1

    let empty : ManagerState = { Version = currentVersion; Sessions = [] }

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

    /// Every port the registry has already promised to somebody.
    let takenPorts (state: ManagerState) : Set<int> =
        state.Sessions |> List.choose (fun s -> s.Port) |> Set.ofList

    /// The lowest port in the range nobody holds. Deliberately excludes EVERY taken port,
    /// including any that now falls outside the range — see `assignMissingPorts`.
    let nextPort (range: PortRange) (state: ManagerState) : Result<int, string> =
        let taken = takenPorts state
        match PortRange.toList range |> List.tryFind (fun p -> not (Set.contains p taken)) with
        | Some port -> Ok port
        | None ->
            Error (
                sprintf
                    "session port range %s is exhausted (%d sessions hold one)"
                    (PortRange.describe range)
                    (Set.count taken)
            )

    /// Ports claimed by more than one session. A registry that has reached this state was
    /// hand-edited or written by two Managers over one data directory; either way the
    /// second session to launch would fail to bind and the first would answer for both, so
    /// the caller fails the boot rather than discovering it at an arbitrary later launch.
    let duplicatePorts (state: ManagerState) : (int * SessionId list) list =
        state.Sessions
        |> List.choose (fun s -> s.Port |> Option.map (fun p -> p, s.SessionId))
        |> List.groupBy fst
        |> List.map (fun (port, entries) -> port, entries |> List.map snd)
        |> List.filter (fun (_, ids) -> List.length ids > 1)

    /// Give every session that has no port one, in creation order. Returns the assignments
    /// so the caller can log what it just decided — an address appearing out of nowhere is
    /// exactly the sort of thing an operator should be able to find afterwards.
    ///
    /// A port already stored is ALWAYS kept, even when it now falls outside the configured
    /// range: shrinking a range must not silently relocate a session, because the address
    /// is what the browser partitions its storage by, and a session that moves loses
    /// whatever its user wrote offline. Out-of-range ports stay excluded from new
    /// allocations, so the two can never collide.
    ///
    /// Under `Ephemeral` this is the identity: nothing is assigned and nothing already
    /// stored is discarded, so turning pinning off and on again restores the same addresses.
    let assignMissingPorts
        (ports: SessionPorts)
        (state: ManagerState)
        : Result<ManagerState * (SessionId * int) list, string> =
        match ports with
        | Ephemeral -> Ok (state, [])
        | Pinned range ->
            let rec assign (remaining: SessionRecord list) (acc: SessionRecord list) (assigned: (SessionId * int) list) =
                match remaining with
                | [] -> Ok (List.rev acc, List.rev assigned)
                | session :: rest ->
                    match session.Port with
                    | Some _ -> assign rest (session :: acc) assigned
                    | None ->
                        // Allocate against the state as amended so far, so two portless
                        // sessions in one pass cannot be handed the same port.
                        match nextPort range { state with Sessions = List.rev acc @ remaining } with
                        | Error e -> Error e
                        | Ok port ->
                            assign rest ({ session with Port = Some port } :: acc) ((session.SessionId, port) :: assigned)
            assign state.Sessions [] []
            |> Result.map (fun (sessions, assigned) -> { state with Sessions = sessions }, assigned)

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
                      "dataDir", Encode.string s.DataDir
                      "port", Encode.option Encode.int s.Port ]
          Decode =
            Decode.object (fun get ->
                { SessionRecord.SessionId = get.Required.Field "sessionId" Codec.sessionId.Decode
                  SessionRecord.DisplayName = get.Required.Field "displayName" Decode.string
                  SessionRecord.CreatedAt = get.Required.Field "createdAt" Codec.timestamp.Decode
                  SessionRecord.DataDir = get.Required.Field "dataDir" Decode.string
                  // Optional, and null-tolerant: every registry written before Plan 11 has
                  // no `port` at all, and one written under `Ephemeral` writes it as null.
                  // Both mean the same thing — no pinned address — so neither may fail the
                  // load.
                  SessionRecord.Port = get.Optional.Field "port" Decode.int }) }

    let managerState : Codec<ManagerState> =
        { Encode =
            fun (s: ManagerState) ->
                Encode.object
                    [ "version", Encode.int s.Version
                      "sessions", s.Sessions |> List.map sessionRecord.Encode |> Encode.list ]
          Decode =
            Decode.object (fun get ->
                { ManagerState.Version = get.Required.Field "version" Decode.int
                  ManagerState.Sessions = get.Required.Field "sessions" (Decode.list sessionRecord.Decode) }) }

    let toString (state: ManagerState) : string =
        managerState.Encode state |> Encode.toString 2

    let fromString (json: string) : Result<ManagerState, string> =
        Decode.fromString managerState.Decode json
