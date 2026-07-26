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

/// Crockford base32 — the encoding that turns a session id into a value that is also a
/// legal Docker object name with no transformation. The alphabet is a strict subset of
/// Docker's `[a-zA-Z0-9]`; a 128-bit value encodes to a fixed 26-char string. Pure integer
/// arithmetic so it compiles the same under .NET and Fable.
module private Base32Crockford =

    [<Literal>]
    let private Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"

    /// Encode bytes as Crockford base32, no padding. 16 bytes -> 26 chars.
    let encode (bytes: byte[]) : string =
        let sb = System.Text.StringBuilder()
        let mutable buffer = 0
        let mutable bits = 0
        for b in bytes do
            buffer <- (buffer <<< 8) ||| int b
            bits <- bits + 8
            while bits >= 5 do
                bits <- bits - 5
                sb.Append(Alphabet.[(buffer >>> bits) &&& 0x1F]) |> ignore
                // Keep only the still-unconsumed low bits so `buffer` never overflows an int.
                buffer <- buffer &&& ((1 <<< bits) - 1)
        if bits > 0 then
            sb.Append(Alphabet.[(buffer <<< (5 - bits)) &&& 0x1F]) |> ignore
        sb.ToString()

    let private hexNibble (c: char) : int =
        if c >= '0' && c <= '9' then int c - int '0'
        elif c >= 'a' && c <= 'f' then int c - int 'a' + 10
        elif c >= 'A' && c <= 'F' then int c - int 'A' + 10
        else 0

    /// 16 random bytes from a fresh v4 GUID, parsed from its hex form (portable across
    /// .NET and Fable — no reliance on `Guid.ToByteArray` byte ordering).
    let guidBytes () : byte[] =
        let hex = (Guid.NewGuid().ToString()).Replace("-", "")
        Array.init 16 (fun i -> byte ((hexNibble hex.[i * 2] <<< 4) ||| hexNibble hex.[i * 2 + 1]))

type SessionId = private SessionId of string

module SessionId =
    let private isNameChar (c: char) =
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
        || c = '_' || c = '.' || c = '-'
    let private isNameStart (c: char) =
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')

    /// A session id is always a valid Docker object name: the container and its named
    /// workspace volume are named by the id verbatim (docs/plans/03). `mint` produces one;
    /// `create` parses one off the wire/env and rejects anything Docker could not name.
    let create (raw: string) : Result<SessionId, string> =
        normalize "SessionId" raw
        |> Result.bind (fun s ->
            if s.Length >= 2 && isNameStart s.[0] && String.forall isNameChar s then Ok (SessionId s)
            else Error "SessionId must be a valid container name ([A-Za-z0-9][A-Za-z0-9_.-]+)")

    /// Mint a fresh id: 128 random bits, Crockford base32-encoded (26 chars, Docker-safe).
    let mint () : SessionId = SessionId (Base32Crockford.encode (Base32Crockford.guidBytes ()))

    let value (SessionId s) = s

type PeerId = private PeerId of string

module PeerId =
    let create (raw: string) : Result<PeerId, string> =
        normalize "PeerId" raw |> Result.map PeerId
    let value (PeerId s) = s

type QueueId = private QueueId of string

module QueueId =
    let create (raw: string) : Result<QueueId, string> =
        normalize "QueueId" raw |> Result.map QueueId
    let value (QueueId s) = s

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

/// A Manager-verified user identity — the OIDC `sub` claim the Manager itself issued
/// (docs/plans/04-session-authorization.md). Under the localhost strategy this is the
/// single "local" user; a BYO strategy (docs/plans/07) mints real subjects.
type UserId = private UserId of string

module UserId =
    let create (raw: string) : Result<UserId, string> =
        normalize "UserId" raw |> Result.map UserId
    let value (UserId s) = s

/// Who an event or action is attributed to. `UserRef` is a durable human identity the
/// Manager verified; `PeerRef` is a client connection — the fallback attribution when no
/// authentication strategy binds a user to the connection.
type ActorRef =
    | UserRef of UserId
    | PeerRef of PeerId
    | Agent
    | SessionProcess
    | System
