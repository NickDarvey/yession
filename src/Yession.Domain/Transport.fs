namespace Yession.Domain

open Yession.Domain.Terminals

/// The multiplexed session transport protocol. These are pure protocol shapes shared by
/// the Session Process and the Browser Client; the actual WebRTC/HTTP carrier is an
/// adapter that implements a frame channel over these types. State-sync payloads are
/// opaque to the transport (owned by the Ylmish sync boundary in Step 05), hence the
/// `'State` type parameter. See docs/design.md §2.3.

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
    /// Take the terminal's stdin — enter live mode (Plan 13, stage 2e). Succeeds even when
    /// another peer holds it: collaborators are trusted, so this STEALS rather than queues,
    /// and the previous holder's lease ends on the record. Rejected only when the terminal
    /// is not open, or is degraded (no shell to type into).
    | TakeTerminalLease of TerminalId
    /// Hand the terminal back to block mode. Rejected when this peer is not the holder —
    /// releasing someone else's lease is a steal wearing a polite word, and a steal is
    /// `TakeTerminalLease`, which says so on the record.
    | ReleaseTerminalLease of TerminalId
    /// Type the shell instrumentation into the terminal again (Plan 13, stage 2f), after its
    /// shell stopped emitting marks. Any peer may: it repairs a terminal rather than taking
    /// anything from anyone. Rejected when the terminal is not in that state.
    | RearmTerminal of TerminalId
    /// Ask the provider for this terminal's stream again (Plan 19, step 4), when the stream
    /// it was attached to has ended.
    ///
    /// The peer names a TERMINAL and never a url, which is what keeps `OpenTerminal`'s rule
    /// intact: a peer does not choose what this session connects to. What is replayed is the
    /// tool call that produced the stream in the first place, and only when the provider
    /// declared that asking again is safe. A new terminal arrives as `TerminalOpened`.
    | ReattachTerminal of TerminalId

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
/// event log already makes. A transcript is read by CURSOR and answered at an address
/// naming the range it chose (`TranscriptChunk`), so those bytes never change and the
/// client keeps the ranges it has folded in its own Cache API store — the responses
/// themselves are `no-store`, because the address a cursor is asked at is not the address
/// the answer is kept under. A reload replays terminals out of that store; only the live
/// tail needs the channel.
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
    ///
    /// A `TranscriptKeyframe` rather than a seq and a screen, because that IS the pair plus
    /// the one thing it was missing: the SIZE the screen was drawn at. A serialized screen is
    /// a paint at a geometry, and a client that repaints it at a different one rewraps every
    /// line in it — which the ranged replay has always known (`Serialization.range` takes the
    /// keyframe's `Cols`/`Rows` over the header's) and this frame did not, so a browser
    /// composing a live screen sized it 80x24 and left it there for the terminal's whole life.
    /// One type, produced by one serializer, carrying what a screen cannot be read without.
    | TerminalSnapshot of TerminalId * TranscriptKeyframe
    /// Keystrokes from the lease holder, relayed straight to the pty (Plan 13, stage 2e).
    ///
    /// Lease-checked at the Session Process, which is the only place it CAN be checked: a
    /// client that believes it holds the lease may be wrong (a steal it has not seen yet),
    /// and the pty is the Process's. A frame from a non-holder is dropped, not answered —
    /// there is no response frame here by design, because a keystroke that needed an
    /// acknowledgement would make typing a round trip.
    ///
    /// Never recorded as a transcript `"i"` record.
    ///
    /// The obvious narrower rule — record keystrokes except while the pty's ECHO bit is off —
    /// cannot be implemented on this seam. `termios` lives kernel-side on the pty slave,
    /// neither `node-pty` nor `docker exec` surfaces it, and it is not inferable from the byte
    /// stream a headless emulator sees. So the choice is all keystrokes or none, and none is
    /// right: live mode makes typing a password ordinary (`ssh`, `sudo`, a REPL token prompt),
    /// and a durable, replayable, HTTP-fetchable keystroke log is a class of exposure the
    /// session never had. Nothing is lost from the audit, because a shell echoes what is typed
    /// at it — what recording these would add is exactly what the terminal deliberately did not
    /// display. Attribution survives in the lease events, which bracket transcript ranges.
    | TerminalInput of TerminalId * data: string
    /// The lease holder's viewport size, relayed to the pty (Plan 13, stage 2e). Live-mode
    /// only: their foreground program is the one that has to agree with the pty, so in block
    /// mode the size comes from the synced register instead and this frame is dropped.
    | TerminalResize of TerminalId * cols: int * rows: int

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
///
/// Deliberately NOT Yjs awareness. Awareness would carry presence on a second, binary relay
/// keyed by Yjs `clientID`, which means a `clientID`↔`PeerId` mapping to maintain and a sync
/// path outside the typed frames — Ylmish is the sync boundary, and presence is not doc state.
/// The typed frame costs a little more render code and buys one identity, one relay, and a
/// payload the cheap tier can round-trip.
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
