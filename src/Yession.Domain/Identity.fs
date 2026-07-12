namespace Yession.Domain

open System

/// Shared identity vocabulary. Every type below uses a private constructor; construct
/// values through the companion module's `create` smart constructor so validation and
/// normalisation always run, and read the underlying value with `value`.
/// See docs/design.md §6.

[<AutoOpen>]
module private IdString =

    /// Validate and normalise a string-based identifier: reject null/blank, trim the rest.
    let normalize (label: string) (raw: string) : Result<string, string> =
        if isNull (box raw) then Error (label + " cannot be null")
        elif String.IsNullOrWhiteSpace raw then Error (label + " cannot be empty or whitespace")
        else Ok (raw.Trim())

type SessionId = private SessionId of string

module SessionId =
    let create (raw: string) : Result<SessionId, string> =
        normalize "SessionId" raw |> Result.map SessionId
    let value (SessionId s) = s

type PeerId = private PeerId of string

module PeerId =
    let create (raw: string) : Result<PeerId, string> =
        normalize "PeerId" raw |> Result.map PeerId
    let value (PeerId s) = s

type DraftId = private DraftId of string

module DraftId =
    let create (raw: string) : Result<DraftId, string> =
        normalize "DraftId" raw |> Result.map DraftId
    let value (DraftId s) = s

type MessageId = private MessageId of string

module MessageId =
    let create (raw: string) : Result<MessageId, string> =
        normalize "MessageId" raw |> Result.map MessageId
    let value (MessageId s) = s

type AgentTurnId = private AgentTurnId of string

module AgentTurnId =
    let create (raw: string) : Result<AgentTurnId, string> =
        normalize "AgentTurnId" raw |> Result.map AgentTurnId
    let value (AgentTurnId s) = s

type EventId = private EventId of Guid

module EventId =
    let create (id: Guid) : Result<EventId, string> =
        if id = Guid.Empty then Error "EventId cannot be the empty guid"
        else Ok (EventId id)
    /// Generate a fresh, unique event id.
    let fresh () : EventId = EventId (Guid.NewGuid())
    let value (EventId id) = id

type RequestId = private RequestId of Guid

module RequestId =
    let create (id: Guid) : Result<RequestId, string> =
        if id = Guid.Empty then Error "RequestId cannot be the empty guid"
        else Ok (RequestId id)
    /// Generate a fresh, unique request id.
    let fresh () : RequestId = RequestId (Guid.NewGuid())
    let value (RequestId id) = id

type EventOffset = private EventOffset of int64

module EventOffset =
    let create (n: int64) : Result<EventOffset, string> =
        if n < 0L then Error "EventOffset cannot be negative"
        else Ok (EventOffset n)
    /// The first offset in any event log.
    let zero = EventOffset 0L
    let value (EventOffset n) = n
    /// The later of two optional offsets (`None` = nothing known/processed yet).
    let maxOption (a: EventOffset option) (b: EventOffset option) : EventOffset option =
        match a, b with
        | Some (EventOffset x), Some (EventOffset y) -> Some (EventOffset (max x y))
        | Some x, None -> Some x
        | None, b -> b

type CommandId = private CommandId of string

module CommandId =
    let create (raw: string) : Result<CommandId, string> =
        normalize "CommandId" raw |> Result.map CommandId
    let value (CommandId s) = s

/// Who an event or action is attributed to.
type ActorRef =
    | HumanPeer of PeerId
    | Agent
    | SessionProcess
    | System
