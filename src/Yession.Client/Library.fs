namespace Yession.Client

open Yession.Domain

/// Placeholder that establishes the dependency on the shared domain library. The real
/// Browser Client (Elmish loop, Ylmish/Yjs sync, WebRTC connection, event consumption)
/// is built up across later delivery steps. See docs/plans/00-init.
module App =

    /// Smoke helper proving the shared domain vocabulary is reachable from the client.
    let describe (event: SessionEvent) : string =
        match event with
        | SessionCreated _ -> "session-created"
        | PeerJoined _ -> "peer-joined"
        | PeerLeft _ -> "peer-left"
