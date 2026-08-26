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
    /// workspace volume are named by the id verbatim (Plan 03). `mint` produces one;
    /// `create` parses one off the wire/env and rejects anything Docker could not name.
    let create (raw: string) : Result<SessionId, string> =
        normalize "SessionId" raw
        |> Result.bind (fun s ->
            if s.Length >= 2 && isNameStart s.[0] && String.forall isNameChar s then Ok (SessionId s)
            else Error "SessionId must be a valid container name ([A-Za-z0-9][A-Za-z0-9_.-]+)")

    /// Mint a fresh id: 128 random bits, Crockford base32-encoded (26 chars, Docker-safe).
    let mint () : SessionId = SessionId (Base32Crockford.encode (Base32Crockford.guidBytes ()))

    /// The id a Session Process runs under when nobody minted one for it: a bare
    /// `yession-session`, the test harness, and the Manager's own default session.
    ///
    /// Named here, beside the rule it satisfies, rather than spelled as a string literal at
    /// each of the three boundaries that wanted it — each of which had to `create` it and
    /// then decide what to do with an `Error` that cannot happen. Constructed directly, so
    /// it is a `SessionId` and not a `Result` nobody knows how to fail.
    let local : SessionId = SessionId "local-session"

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

/// One terminal on the session's WorkSandbox (Plan 12). Constrained to the same
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

/// One call the agent made to one tool (Plan 16, part C). Minted by the Session Process
/// when the call starts, for the same reason `BlockId` is: a fact that will be ADDRESSED —
/// a chip you can tap, a link you can send someone — must not be identified by something a
/// reader has to derive, because the derivation rule then lives nowhere in the data.
type ToolUseId = private ToolUseId of string

module ToolUseId =
    let create (raw: string) : Result<ToolUseId, string> =
        normalize "ToolUseId" raw |> Result.map ToolUseId
    let value (ToolUseId s) = s

/// A Manager-verified user identity — the OIDC `sub` claim the Manager itself issued
/// (Plan 04). Under the localhost strategy this is the
/// single "local" user; a BYO strategy (Plan 07) mints real subjects.
type UserId = private UserId of string

module UserId =
    let create (raw: string) : Result<UserId, string> =
        normalize "UserId" raw |> Result.map UserId
    let value (UserId s) = s

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

/// Who an event or action is attributed to. `UserRef` is a durable human identity the
/// Manager verified; `PeerRef` is a client connection — the fallback attribution when no
/// authentication strategy binds a user to the connection.
type ActorRef =
    | UserRef of UserId
    | PeerRef of PeerId
    | Agent
    | SessionProcess
    | System
    /// A repository's own `yession.yaml` (Plan 27), as the party that asked for something.
    ///
    /// Not decoration. A file is authored by whoever can push to the repo, which is neither
    /// the operator nor anybody in the session, and freshly-cloned code is LESS trusted than
    /// the agent rather than more. So the acts a fold performs are attributed to the file
    /// that asked for them, and a classifier that wants to treat them differently has the
    /// fact it needs — as does a person reading the timeline, who would otherwise see the
    /// session process start sandboxes nobody asked it to.
    | Configured of RepoRef

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
        | Configured repo -> "configured:" + RepoRef.value repo

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
                    | "configured" -> (match RepoRef.create rest with Ok r -> Some (Configured r) | Error _ -> None)
                    | _ -> None

/// On whose authority an act happens, and who is behind it: the three parties an audit asks
/// about, as ONE value (Plan 20).
///
/// They were three loose fields that every site re-spelled — `PendingAct` spelled them,
/// `TerminalBlockStarted` spelled them, the refusal events spelled their share — and one site
/// drifted. Agent terminal commands recorded no `OnBehalfOf` at all, so Plan 08's no-borrowing
/// rule ("the agent is the acting party; the credential is the turn human's") held in two
/// places and was silently absent in a third. An invariant that holds only because a caller
/// remembered to set a field is a convention with a good reputation.
///
/// It is a triple, not a chain: three parties for ONE act, with no lineage and no history of
/// delegation. What a later act inherits, it inherits by being constructed with it.
///
/// Private, so the smart constructors below are the only way to author one. `agentFor` takes
/// the turn actor, which is what makes the omission unrepresentable: an agent-authored act
/// with nobody named on it cannot be built, so forgetting would not compile.
///
/// The field names are prefixed and deliberately unlovely. They are private — every reader
/// goes through the module below — and a record carrying bare `Author`/`OnBehalfOf` fields in
/// this namespace made every OTHER record with those names ambiguous to inference.
type Authority =
    private
        { AuthAuthor : ActorRef
          AuthOnBehalfOf : ActorRef option }

module Authority =

    /// A party acting for themselves. There is no authority to borrow, so there is none to
    /// state — which is why a person's act cannot accidentally carry somebody else's.
    let ofAuthor (actor: ActorRef) : Authority =
        { AuthAuthor = actor; AuthOnBehalfOf = None }

    /// The agent, acting on a turn human's authority (Plan 08). The rule that was missing from
    /// one call site, as the ONLY way to build an agent-authored act.
    let agentFor (turnActor: ActorRef) : Authority =
        { AuthAuthor = ActorRef.Agent; AuthOnBehalfOf = Some turnActor }

    /// A repo's own `yession.yaml`, acting on the authority of whoever asked for the fold
    /// (Plan 27). The file is the AUTHOR — it is what asked for the sandbox — and the
    /// credential is never its own: a `forward:` resolves for the human by Plan 08
    /// precedence, exactly as the agent's does.
    ///
    /// `None` is a fold nobody triggered: the one at boot, where there is no turn and no
    /// caller. The act then runs on NOTHING rather than on somebody guessed at, which is the
    /// same degraded state `effective` already answers safely — a `forward:` fails saying
    /// there is no credential to forward, which is true.
    let configuredBy (repo: RepoRef) (onBehalfOf: ActorRef option) : Authority =
        { AuthAuthor = ActorRef.Configured repo; AuthOnBehalfOf = onBehalfOf }

    /// Recover what somebody else already wrote — a doc entry, a stored event. NOT an
    /// authoring path: it can express states the constructors above refuse, because it is
    /// recovering facts rather than deciding them, and a decoder that could not represent what
    /// is written would drop the entry instead.
    ///
    /// Chiefly: an agent act whose owner did not read back. That is the degraded state
    /// `effective` answers safely — the act runs on NOTHING rather than on somebody else's
    /// credential — and refusing to represent it here would turn a corrupt field into a
    /// missing act.
    let rehydrate (author: ActorRef) (onBehalfOf: ActorRef option) : Authority =
        { AuthAuthor = author; AuthOnBehalfOf = onBehalfOf }

    let author (authority: Authority) : ActorRef = authority.AuthAuthor
    /// Whose authority this runs on, when that is not the author's own. `None` on a person's
    /// act means there is nothing borrowed; `None` on the agent's means the owner was lost.
    let onBehalfOf (authority: Authority) : ActorRef option = authority.AuthOnBehalfOf

    /// Whose credentials this resolves to — the borrowed authority when there is one, the
    /// author otherwise. The question every dispatch actually asks, answered once instead of
    /// by a `defaultArg` at each site that asks it.
    let effective (authority: Authority) : ActorRef =
        authority.AuthOnBehalfOf |> Option.defaultValue authority.AuthAuthor

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

/// A name the OPERATOR gave to a resource — one thing a sandbox may be granted, or a name
/// for several of them together.
///
/// The same charset as `SandboxName`, and that is not laziness: a refusal lists names, and
/// a list is only reproducible across runtimes if it sorts the same under .NET's ordinal
/// compare and JavaScript's code-unit compare. Lowercase ASCII, digits, `-` and `_` sort
/// identically under both.
type ResourceName = private ResourceName of string

module ResourceName =

    let create (raw: string) : Result<ResourceName, string> =
        let name = (defaultArg (Option.ofObj raw) "").Trim ()
        let charOk c = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '-' || c = '_'
        let startOk c = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
        if name = "" then Error "a resource name cannot be empty"
        elif name.Length > 40 then Error (sprintf "'%s' is too long for a resource name (40 characters)" name)
        elif not (startOk name.[0]) then
            Error (sprintf "'%s' is not a resource name (it must start with a lowercase letter or digit)" name)
        elif name |> Seq.forall charOk then Ok (ResourceName name)
        else Error (sprintf "'%s' is not a resource name (lowercase letters, digits, '-' and '_' only)" name)

    let value (ResourceName name) = name

/// WHO declared a sandbox (Plan 27).
///
/// The session has always had its own — `default`, and whatever the agent starts. A repo
/// that carries a `yession.yaml` declares its own, and the scope is what keeps the two
/// apart: two repos may both want a sandbox called `dev`, and neither is wrong.
///
/// This is what makes the union of several repos' files TOTAL. The key is a pair, the
/// repos are disjoint, so there is no precedence rule to invent and nothing can shadow
/// anything — a clash is only possible INSIDE one file, where it is refused at the point
/// somebody can still pick another name.
type SandboxScope =
    /// The session's own — `default`, and whatever the agent started. No file declares one.
    | SessionOwned
    /// Declared by a repo's `yession.yaml`.
    | RepoOwned of RepoRef

/// One sandbox, named within its scope.
///
/// The scope rides BESIDE the name rather than inside it, because `SandboxName`'s charset
/// is the intersection of everything a name has to survive — a Docker object-name
/// component, a directory, a terminal label — and `owner/repo:name` is none of those.
/// Smuggling the scope into the string would put a `/` and a `:` through every one of
/// those consumers.
///
/// A pair in a single-case union rather than a record, following `RepoRef`: the two
/// obvious field names (`Scope`, `Name`) are `SecretId`'s, and a second record wearing
/// them would silently capture every `{ Scope = …; Name = … }` in the codebase.
type SandboxRef = private SandboxRef of scope: SandboxScope * name: SandboxName

module SandboxRef =

    let create (scope: SandboxScope) (name: SandboxName) : SandboxRef = SandboxRef (scope, name)

    /// The sandbox a terminal that names nothing lands in. Unchanged by scoping: it is the
    /// session's, and no file may declare it.
    let defaultRef : SandboxRef = SandboxRef (SessionOwned, SandboxName.defaultName)

    /// One a repo declared.
    let inScope (repo: RepoRef) (name: SandboxName) : SandboxRef = SandboxRef (RepoOwned repo, name)

    let scope (SandboxRef (s, _)) = s
    let name (SandboxRef (_, n)) = n

    /// How it is written for a person and for the agent's tools: `dev` for the session's
    /// own, `octo/hello:dev` for a repo's. Unambiguous because neither half's charset
    /// admits a `:`.
    let render (SandboxRef (scope, name)) : string =
        match scope with
        | SessionOwned -> SandboxName.value name
        | RepoOwned repo -> sprintf "%s:%s" (RepoRef.value repo) (SandboxName.value name)

    /// A 28-bit hash of a string, as seven lowercase hex characters.
    ///
    /// Pure shift-and-add integer arithmetic, for `Base32Crockford`'s reason: it has to
    /// compute the SAME value under .NET and Fable, and a multiply would differ the moment an
    /// intermediate left JavaScript's exactly-representable range. Both runtimes shift as
    /// 32-bit signed, and the mask keeps the result non-negative and inside 28 bits.
    let private hex7 (raw: string) : string =
        let mutable h = 17
        for c in raw do
            h <- ((h <<< 5) - h + int c) &&& 0xFFFFFFF
        let digits = "0123456789abcdef"
        String (Array.init 7 (fun i -> digits.[(h >>> ((6 - i) * 4)) &&& 0xF]))

    /// What identifies this sandbox WITHIN its session, in a charset a Docker object name
    /// and a directory both accept.
    ///
    /// The scope cannot be spelled out here: `owner/repo` carries a `/`, and `SandboxName`'s
    /// charset is narrow exactly because these two consumers are. Slugifying it would be
    /// worse than a hash rather than better — `a/b-c` and `a-b/c` both flatten to `a-b-c`,
    /// and two repos quietly sharing one container is the failure this segment exists to
    /// prevent.
    ///
    /// So a repo's sandboxes carry a hash of the scope. It is 28 bits, which is a bound worth
    /// stating rather than a guarantee: two DIFFERENT repos declaring the SAME sandbox name
    /// could collide, at roughly one in 268 million. The readable answer to "which repo is
    /// this" is the `work_sandboxes` query, which carries the scope itself; this is the part
    /// that has to survive being a container name.
    let slug (ref: SandboxRef) : string =
        match scope ref with
        | SessionOwned -> SandboxName.value (name ref)
        | RepoOwned repo -> sprintf "%s-%s" (SandboxName.value (name ref)) (hex7 (RepoRef.value repo))

    /// The name this sandbox's BACKEND objects take — a container, a volume, a docker label.
    ///
    /// Lives here, beside the type, rather than in the composition root that used to compute
    /// it: a rule about what a `SandboxRef` may be called is a property of `SandboxRef`, and
    /// one a cheap test should be able to reach without building a session.
    ///
    /// The session's own `default` keeps the bare session id it has always had, so nothing
    /// about an existing session moves.
    let objectName (session: SessionId) (ref: SandboxRef) : string =
        if ref = defaultRef then SessionId.value session
        else sprintf "%s-%s" (SessionId.value session) (slug ref)

    /// Read one back. Total by returning a `Result`: this parses agent input.
    let parse (raw: string) : Result<SandboxRef, string> =
        let trimmed = (defaultArg (Option.ofObj raw) "").Trim()
        match trimmed.Split ':' with
        | [| name |] -> SandboxName.create name |> Result.map (fun n -> SandboxRef (SessionOwned, n))
        | [| repo; name |] ->
            RepoRef.create repo
            |> Result.bind (fun r -> SandboxName.create name |> Result.map (fun n -> inScope r n))
        | _ -> Error (sprintf "'%s' is not a sandbox (expected 'name' or 'owner/repo:name')" trimmed)

/// What a server is called — and, because Plan 16 part A made a namespace the SDK MCP
/// server's NAME, the namespace every one of its tools lands in.
///
/// So this is not a label: it is the prefix of every wire name the model sees
/// (`mcp__serial__acquire_device`) and the word the tool-use record shows. Constrained to
/// what a tool name can carry, which is why the charset is `QueryName`'s rather than
/// `SandboxName`'s — a name reaching the model has to survive the SDK's wire name, not a
/// Docker volume.
type McpServerName = private McpServerName of string

module McpServerName =

    /// The session's own namespace. Reserved, because the session's verbs are not something
    /// a provider may impersonate: a server called `yession` would put foreign tools behind
    /// the wire names `execute_command` and `set_secret` already answer to.
    [<Literal>]
    let Reserved = "yession"

    let create (raw: string) : Result<McpServerName, string> =
        let name = (defaultArg (Option.ofObj raw) "").Trim ()
        let charOk c = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '_'
        if name = "" then Error "server name cannot be empty"
        elif name.Length > 64 then Error (sprintf "'%s' is too long for a server name" name)
        elif name = Reserved then
            Error (sprintf "'%s' is the session's own namespace and cannot be a server name" name)
        elif name.StartsWith "_" || name.EndsWith "_" then
            Error (sprintf "'%s' is not a server name (no leading or trailing underscore)" name)
        elif name |> Seq.forall charOk then Ok (McpServerName name)
        else Error (sprintf "'%s' is not a server name (lowercase, digits and underscore only)" name)

    let value (McpServerName name) = name
