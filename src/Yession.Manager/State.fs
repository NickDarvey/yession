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
                      "sessions", s.Sessions |> List.map sessionRecord.Encode |> Encode.list ]
          Decode =
            Decode.object (fun get ->
                { ManagerState.Version = get.Required.Field "version" Decode.int
                  ManagerState.Sessions = get.Required.Field "sessions" (Decode.list sessionRecord.Decode) }) }

    let toString (state: ManagerState) : string =
        managerState.Encode state |> Encode.toString 2

    let fromString (json: string) : Result<ManagerState, string> =
        Decode.fromString managerState.Decode json
