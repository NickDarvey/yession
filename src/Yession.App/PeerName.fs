namespace Yession.App

open System
open Yession.Domain

/// A small pool of friendly display names. The Browser Client picks one at random for the
/// local peer on load (the product shows a human-readable presence name, not a raw id).
module PeerName =

    let private adjectives =
        [| "calm"; "swift"; "bright"; "quiet"; "bold"; "keen"; "warm"; "lucid" |]

    let private animals =
        [| "otter"; "heron"; "lynx"; "finch"; "ibex"; "marten"; "wren"; "tern" |]

    /// A random, human-readable display name like "swift-heron".
    let random (rng: Random) : string =
        let pick (xs: string[]) = xs.[rng.Next xs.Length]
        sprintf "%s-%s" (pick adjectives) (pick animals)
