namespace Yession.Domain

/// The multiplexed session transport protocol. These are pure protocol shapes shared by
/// the Session Process and the Browser Client; the actual WebRTC/HTTP carrier is an
/// adapter that implements a frame channel over these types. State-sync payloads are
/// opaque to the transport (owned by the Ylmish sync boundary in Step 05), hence the
/// `'State` type parameter. See docs/design.md §2.3 and docs/plans/00-init/03-*.

/// Commands are how clients ask for durable facts the CRDT cannot express. Drafting,
/// sending (enqueueing), editing, reordering, and deleting are all pure CRDT writes —
/// only the agent-turn interrupt needs a request/response.
type SessionCommand =
    /// Cancel the running agent turn (Phase 3, Step 17). Rejected if that turn is no
    /// longer running — the interrupt-vs-completion race resolves at the Process.
    | InterruptAgentTurn of AgentTurnId
    /// Open a terminal on the session's WorkSandbox (Plan 13). A command rather than a
    /// CRDT write because the terminal's id is minted by the Process and its existence is
    /// a durable fact — a client cannot conjure one into the doc. The new terminal arrives
    /// back as a `TerminalOpened` event, not in the response: one path for the fact, and
    /// it is the one every peer already reads.
    | OpenTerminal of title: string
    /// Close a terminal. Rejected if it is already closed or was never opened.
    | CloseTerminal of TerminalId

type SessionCommandResult =
    | CommandAccepted
    | CommandRejected of reason: string

type CommandFrame =
    | Request of RequestId * SessionCommand
    | Response of RequestId * SessionCommandResult

type EventLogFrame =
    | EventsAvailable of latestOffset: EventOffset
    | ReadEventsAfter of RequestId * after: EventOffset option * limit: int
    | EventsPage of RequestId * EventPage<SessionEvent>

type PeerHelloPayload =
    { PeerId : PeerId
      DisplayName : string
      Token : string }

type PeerAcceptedPayload =
    { SessionId : SessionId
      AssignedDisplayName : string
      LatestOffset : EventOffset option }

type ControlFrame =
    | PeerHello of PeerHelloPayload
    | PeerAccepted of PeerAcceptedPayload
    | PeerRejected of reason: string
    | Ping
    | Pong

/// The state-sync frame. Its payload is opaque to the transport; encoding belongs to the
/// sync-boundary layer (Step 05).
type StateFrame<'State> = StateSync of 'State

/// Terminal traffic (Plan 13), multiplexed onto the session's existing data channel
/// rather than given a leg of its own.
///
/// The alternative considered and rejected was SSE-out/POST-in per terminal. Three
/// reasons it loses here: `design.md` §5 makes WebRTC the session transport and HTTP
/// bootstrap-only; N terminals across M tabs runs into the browser's six-connections-
/// per-origin cap, because a locally served session has no TLS and therefore no HTTP/2
/// multiplexing; and every byte would take a server round trip on a channel that is
/// already direct, ordered and reliable.
///
/// HISTORY still rides HTTP, and that is not a contradiction — it is the same split the
/// event log already makes. Immutable transcript chunks are cacheable
/// (`TranscriptChunk`), so a reload replays terminals out of the browser cache; only the
/// live tail needs the channel.
type TerminalFrame =
    /// One transcript record as it is written, with the line index it was written at.
    /// The seq is what makes the live leg and the HTTP leg interchangeable: a client
    /// keeps a high-water mark and ignores anything at or below it, so a record arriving
    /// twice — once live, once in a fetched chunk — is folded once.
    | TerminalRecord of TerminalId * seq: int * TranscriptRecord
    /// How long a terminal's transcript is now, so a client can tell whether it is
    /// behind. The exact counterpart of `EventsAvailable`: a hint that triggers a read,
    /// never data.
    | TerminalTranscriptAvailable of TerminalId * nextSeq: int
    /// The terminal's screen as the Session Process has it, with the transcript position
    /// it represents (Plan 13, stage 2b) — what a peer joining mid-session renders instead
    /// of replaying every byte the terminal ever printed.
    ///
    /// The seq is what makes it composable with the live feed, exactly as an event offset
    /// is: render this, then fold records ABOVE that seq. The snapshot may be NEWER than
    /// its seq (the screen is read after the position is taken), which is the safe
    /// direction — a client re-folds a record already drawn, and drawing it twice is
    /// idempotent, whereas a seq ahead of the screen would skip a record for ever.
    | TerminalSnapshot of TerminalId * seq: int * screen: string

/// The collaborative field a peer's cursor is in — the extension point for presence "in many
/// places": add a case plus its report/render sites. `DraftBody`/`QueueBody` name the same body
/// roots as `BodyKey`.
type FocusField =
    | Title
    | DraftBody of PeerId
    | QueueBody of QueueId
    /// A terminal composer's command line (Plan 13) — presence works there for the same
    /// reason it works in a message draft: co-editing a command you cannot see someone
    /// else's caret in is co-editing blind.
    | TerminalDraftBody of TerminalId * PeerId
    /// A queued terminal command's line, still editable while it waits for approval.
    | TerminalQueuedBody of QueueId

/// A peer's caret+selection within its focused field, as base64-encoded Yjs *relative positions*
/// over that field's shared type (the title `Y.Text` / a body `Y.XmlFragment`). Relative positions
/// survive concurrent edits; a collapsed selection (`Anchor = Head`) is a bare caret.
type CursorPos = { Anchor : string; Head : string }

type Focus = { Field : FocusField; Pos : CursorPos }

/// Ephemeral presence: where a peer's caret+selection is. Relayed peer-to-peer by the Session
/// Process (never durable, never in Yjs, never an event) so a collaborator's cursor is visible
/// while they edit. `Focus = None` when the peer's caret is nowhere collaborative, or the peer
/// has left (which clears it everywhere).
type PresencePayload =
    { PeerId : PeerId
      DisplayName : string
      Focus : Focus option }

/// Every message exchanged over the session transport is one of these multiplexed frames.
type SessionFrame<'State> =
    | State of StateFrame<'State>
    | Command of CommandFrame
    | EventLog of EventLogFrame
    | Control of ControlFrame
    /// Ephemeral cursor presence; relayed to other peers, never logged or persisted.
    | Presence of PresencePayload
    /// Live terminal traffic (Plan 13). Durable by the time it is sent — the Session
    /// Process appends to the transcript before it broadcasts — so a dropped frame costs
    /// latency, never the record.
    | Terminal of TerminalFrame

/// An abstract, bidirectional channel of session frames shared by both peers (Session
/// Process and Browser Client). The real carrier is a WebRTC data channel (with HTTP used
/// only for static bootstrap and temporary signalling); this abstraction lets the
/// handshake and frame-pump logic be written and tested without the Node/WebRTC IO. The
/// WebRTC adapter is a thin shell that implements this interface.
///
/// `Receive` returns `None` once the channel is closed by the remote end.
type FrameChannel<'State> =
    { Send : SessionFrame<'State> -> Async<unit>
      Receive : unit -> Async<SessionFrame<'State> option>
      Close : unit -> Async<unit> }
