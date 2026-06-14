namespace Yession.Domain

/// The multiplexed session transport protocol. These are pure protocol shapes shared by
/// the Session Process and the Browser Client; the actual WebRTC/HTTP carrier is an
/// adapter that implements a frame channel over these types. State-sync payloads are
/// opaque to the transport (owned by the Ylmish sync boundary in Step 05), hence the
/// `'State` type parameter. See docs/design.md §2.3 and docs/plans/00-init/03-*.

type SessionCommand =
    | StartDraft
    | SendDraft of DraftId

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

/// Every message exchanged over the session transport is one of these multiplexed frames.
type SessionFrame<'State> =
    | State of StateFrame<'State>
    | Command of CommandFrame
    | EventLog of EventLogFrame
    | Control of ControlFrame
