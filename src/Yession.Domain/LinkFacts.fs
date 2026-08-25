namespace Yession.Domain.Link

open Yession.Domain

/// The facts a peer's presence records — joining and leaving a session.
///
/// These sit BELOW `SessionEvent`, because the union names them, while the
/// projections that fold that union sit above it — so Link spans the event
/// spine rather than living on one side of it.
type PeerJoined =
    { PeerId : PeerId
      DisplayName : string
      /// The Manager-verified user behind this connection, when the authentication
      /// strategy attributed one. None = unattributed access (trust-localhost) —
      /// the connection is identified only by its peer.
      User : UserId option }

and PeerLeft =
    { PeerId : PeerId }
