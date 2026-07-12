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
///   - Agent* events           -> Step 08
type SessionEvent =
    | SessionCreated of SessionCreated
    // Control/presence facts appended by the Session Process on connect/disconnect (Step 03).
    | PeerJoined of PeerJoined
    | PeerLeft of PeerLeft
    // The durable fact that a draft began (Step 05). The draft's *content* lives in the
    // synced collaborative state (Yjs), never in the event log.
    | DraftStarted of DraftStarted
    // A draft sent: the body snapshotted at send time by the Session Process (Step 06).
    // Immutable in Phase 1 — later draft edits never touch it.
    | MessageSent of MessageSent

and SessionCreated =
    { SessionId : SessionId }

and PeerJoined =
    { PeerId : PeerId
      DisplayName : string }

and PeerLeft =
    { PeerId : PeerId }

and DraftStarted =
    { DraftId : DraftId
      StartedBy : PeerId }

and MessageSent =
    { MessageId : MessageId
      /// The draft the message came from; `None` once direct (draftless) sends exist.
      DraftId : DraftId option
      Author : ActorRef
      Body : string }
