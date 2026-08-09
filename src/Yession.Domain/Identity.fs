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

/// One terminal on the session's WorkSandbox (docs/plans/12). Constrained to the same
/// filename-safe alphabet as `SessionId` for the same reason: a terminal's transcript is a
/// sidecar file named after it, and an id that cannot be a filename would be discovered at
/// the first append rather than at parse.
type TerminalId = private TerminalId of string

module TerminalId =
    let private isNameChar (c: char) =
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c = '-'

    let create (raw: string) : Result<TerminalId, string> =
        normalize "TerminalId" raw
        |> Result.bind (fun s ->
            if s.Length >= 2 && String.forall isNameChar s then Ok (TerminalId s)
            else Error "TerminalId must be filename-safe ([A-Za-z0-9-], at least 2 characters)")

    /// Mint a fresh id: 128 random bits, Crockford base32-encoded — the same shape a
    /// session id has, and legal in a filename by construction.
    let mint () : TerminalId = TerminalId (Base32Crockford.encode (Base32Crockford.guidBytes ()))

    let value (TerminalId s) = s

/// One executed command in a terminal: the command, its output range, and its exit code.
/// Minted by the Session Process when it starts the run — never by a client, because a
/// block is a durable fact about something that actually happened.
type BlockId = private BlockId of string

module BlockId =
    let create (raw: string) : Result<BlockId, string> =
        normalize "BlockId" raw |> Result.map BlockId
    let value (BlockId s) = s

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

module ActorRef =

    /// An actor as ONE string. The events' wire format is a tagged object (Serialization.fs)
    /// and stays that way; this exists for the places that need a value a CRDT register can
    /// hold — a terminal queue entry's author, which may be the agent and so cannot be the
    /// bare `PeerId` the message queue gets away with.
    ///
    /// The `:` separator is safe because neither a `UserId` nor a `PeerId` is parsed out of
    /// the remainder — `ofToken` splits on the FIRST colon only, so a subject containing one
    /// round-trips.
    let token (actor: ActorRef) : string =
        match actor with
        | UserRef u -> "user:" + UserId.value u
        | PeerRef p -> "peer:" + PeerId.value p
        | Agent -> "agent"
        | SessionProcess -> "process"
        | System -> "system"

    /// Parse a token back. Total by returning an option: the doc is shared with peers we do
    /// not control, so an unreadable actor is an entry to skip, never a crash.
    let ofToken (raw: string) : ActorRef option =
        if isNull (box raw) then None
        else
            match raw with
            | "agent" -> Some Agent
            | "process" -> Some SessionProcess
            | "system" -> Some System
            | _ ->
                let idx = raw.IndexOf ':'
                if idx <= 0 then None
                else
                    let rest = raw.Substring (idx + 1)
                    match raw.Substring (0, idx) with
                    | "user" -> (match UserId.create rest with Ok u -> Some (UserRef u) | Error _ -> None)
                    | "peer" -> (match PeerId.create rest with Ok p -> Some (PeerRef p) | Error _ -> None)
                    | _ -> None

/// A GitHub repository, named `owner/repo` (Plan 14). Validated at the boundary so the
/// clone URL is CONSTRUCTED from it — there is no free-form remote anywhere downstream,
/// which is what keeps the repo surface free of arbitrary-URL fetches. The charset is
/// GitHub's own (word characters, `.`/`-`; no leading dot or hyphen abuse is worth
/// modelling beyond what the API itself enforces), and both halves are length-capped.
type RepoRef = private RepoRef of owner: string * repo: string

module RepoRef =

    let private segmentOk (maxLen: int) (s: string) : bool =
        s <> ""
        && s.Length <= maxLen
        && s |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '-' || c = '_' || c = '.')
        && s <> "." && s <> ".."

    /// Parse `owner/repo`. A trailing `.git` is stripped rather than refused — it is how
    /// people paste repo names, and the canonical form should win.
    let create (raw: string) : Result<RepoRef, string> =
        let trimmed = (defaultArg (Option.ofObj raw) "").Trim()
        match trimmed.Split '/' with
        | [| owner; repo |] ->
            let repo = if repo.EndsWith ".git" then repo.Substring (0, repo.Length - 4) else repo
            if segmentOk 39 owner && segmentOk 100 repo then Ok (RepoRef (owner, repo))
            else Error (sprintf "'%s' is not an owner/repo name" trimmed)
        | _ -> Error (sprintf "'%s' is not an owner/repo name (expected exactly one '/')" trimmed)

    let owner (RepoRef (o, _)) = o
    let repo (RepoRef (_, r)) = r

    /// The canonical `owner/repo` rendering — also the codec's wire form.
    let value (RepoRef (o, r)) = sprintf "%s/%s" o r

    /// The one place a clone URL is spelled.
    let cloneUrl (ref: RepoRef) : string = sprintf "https://github.com/%s.git" (value ref)

    /// Where the checkout lives under the session's repos directory.
    let relativePath (RepoRef (o, r)) : string = sprintf "%s/%s" o r

/// The name of one of the session's WorkSandboxes (Plan 15, stage 2). A session used to
/// have exactly one, so it needed no name; now the agent can ask for a `test` sandbox
/// beside the `default` one and get the SAME sandbox back on the second ask — which is
/// the whole point of naming them, and the property the declarative form will lean on
/// when it folds a file into these commands at boot.
///
/// The charset is the intersection of everything a name has to survive: a Docker
/// container/volume name component, a directory under the session's data dir, and a
/// terminal's label. Lowercase alphanumerics plus `-`/`_`, starting with a letter or
/// digit — narrow enough that no downstream consumer needs to escape it.
type SandboxName = private SandboxName of string

module SandboxName =

    /// The sandbox a session has always had. Terminals opened without naming one land
    /// here, so every pre-Plan-15 session behaves exactly as it did.
    let defaultName = SandboxName "default"

    let create (raw: string) : Result<SandboxName, string> =
        let name = (defaultArg (Option.ofObj raw) "").Trim ()
        let charOk c = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '-' || c = '_'
        let startOk c = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
        if name = "" then Error "sandbox name cannot be empty"
        elif name.Length > 40 then Error (sprintf "'%s' is too long for a sandbox name (40 characters)" name)
        elif not (startOk name.[0]) then
            Error (sprintf "'%s' is not a sandbox name (it must start with a lowercase letter or digit)" name)
        elif name |> Seq.forall charOk then Ok (SandboxName name)
        else Error (sprintf "'%s' is not a sandbox name (lowercase letters, digits, '-' and '_' only)" name)

    let value (SandboxName name) = name
