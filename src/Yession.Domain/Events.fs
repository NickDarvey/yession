namespace Yession.Domain

open System

/// The generic envelope wrapping every persisted event. The wire format is a boundary
/// concern handled in Serialization.fs; this is the in-memory domain shape.
/// See docs/design.md §6.
type EventEnvelope<'event> =
    { EventId   : EventId
      SessionId : SessionId
      Offset    : EventOffset
      Actor     : ActorRef
      Timestamp : DateTimeOffset
      Event     : 'event }

/// The single, append-only session event type. New cases are added per delivery step;
/// foundations define only `SessionCreated`.
///
/// Planned additions:
///   - PeerJoined / PeerLeft   -> Step 03 (control/presence)
///   - DraftStarted            -> Step 05/06
///   - MessageSent             -> Step 06
///   - Agent* events           -> Step 08
type SessionEvent =
    | SessionCreated of SessionCreated

and SessionCreated =
    { SessionId : SessionId }
