module Yession.Browser.Replay

// The asciinema replay view's platform half (Plan 13, stage 3e).
//
// A closed terminal's blocks survive in the projection, but a list of commands is not the
// same artefact as the RECORDING — a replay shows the terminal as it behaved, at the speed it
// behaved, which is what someone auditing actually wants to watch.
//
// `asciinema-player` rather than the client's own renderer, and it earns itself. PR 1's
// pure-F# SGR parser (`Ansi.fs`) renders a STREAM, not a SCREEN, so a recording of anything
// that moves the cursor — `htop`, a progress bar, `vim` — would replay as garbage. That is the
// same argument the plan made for why the Session Process needed a real emulator rather than
// half of one, and it is why the sidecar was written as asciicast v2 in the first place: so
// the standard player replays it. The player also brings timing, seek and play/pause, which
// IS the audit-read affordance.
//
// Bindings are hand-written `[<Import>]`/`[<Emit>]` over the small surface used — the
// `Emulator.fs` / `ProseMirror.fs` precedent, and the repo invariant that the platform
// boundary is interop rather than authored JS.

open Fable.Core
open Fable.Core.JsInterop

/// What `create` hands back. Only `dispose` is used: a replay is mounted when a closed
/// terminal is shown and torn down when it is not, and a player left attached to a detached
/// node keeps its worker alive.
type [<AllowNullLiteral>] private Player =
    abstract dispose : unit -> unit

/// `asciinema-player` 3.x ships proper ESM with an `exports` map, so a named import resolves —
/// unlike `@xterm/headless`, whose CommonJS `main` forced `ImportDefault`.
[<ImportMember("asciinema-player")>]
let private create (src: obj) (element: Browser.Types.Element) (opts: obj) : Player = jsNative

/// A Blob URL over the `.cast` text, so the player fetches it the way it fetches any
/// recording. Built from what the client already has rather than from a new whole-file route:
/// concatenating transcript chunks reproduces the file byte for byte (see
/// `TranscriptChunk`), so the replay rides the browser's HTTP cache — which is exactly what
/// the design chose immutable chunks for.
[<Emit("URL.createObjectURL(new Blob([$0], { type: 'text/plain' }))")>]
let private blobUrl (text: string) : string = jsNative

[<Emit("URL.revokeObjectURL($0)")>]
let private revoke (url: string) : unit = jsNative

/// One mounted replay, and how to take it down.
type Mounted =
    { Dispose : unit -> unit }

/// Mount a replay of `cast` into `element`.
///
/// `idleTimeLimit` compresses the long gaps a terminal spends waiting for a person — an audit
/// read of a session someone left open for an hour should not be an hour long. `fit: "width"`
/// keeps the recorded geometry (the header's width and height are what make a replay come out
/// the shape the terminal actually was) while scaling to the panel.
let mount (element: Browser.Types.Element) (cast: string) : Mounted =
    let url = blobUrl cast
    let player =
        create
            (box url)
            element
            (createObj [ "fit" ==> "width"; "idleTimeLimit" ==> 2; "terminalFontFamily" ==> "ui-monospace, monospace" ])
    { Dispose =
        fun () ->
            player.dispose ()
            // The Blob outlives the player unless it is revoked, and a panel someone clicks
            // through leaks one per terminal otherwise.
            revoke url }
