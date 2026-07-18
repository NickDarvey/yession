namespace Yession.App

open Yession.Domain

/// A stable colour per peer, derived from its id so every replica agrees without coordination.
/// One source of truth for presence colour — title markers and body cursor decorations both use
/// it, so a peer is the same colour wherever it appears.
module PeerColour =

    /// The peer's hue (0-359), hashed from its id — the same hash the title-cursor markup used.
    let private hueOf (peer: PeerId) : int =
        let s = PeerId.value peer
        let mutable h = 0
        for i in 0 .. s.Length - 1 do
            // int32 arithmetic wraps like the JS `| 0`, so this matches across runtimes.
            h <- h * 31 + int s.[i]
        ((h % 360) + 360) % 360

    /// A solid `hsl(...)` colour (the caret bar and label).
    let ofPeer (peer: PeerId) : string = sprintf "hsl(%d, 70%%, 55%%)" (hueOf peer)

    /// A translucent `hsla(...)` of the same hue (the selection highlight).
    let translucent (peer: PeerId) : string = sprintf "hsla(%d, 70%%, 55%%, 0.25)" (hueOf peer)
