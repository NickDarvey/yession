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
open Yession.App

/// What `create` hands back. Only `dispose` is used: a replay is mounted when a closed
/// terminal is shown and torn down when it is not, and a player left attached to a detached
/// node keeps its worker alive.
type [<AllowNullLiteral>] private Player =
    abstract dispose : unit -> unit

/// The player's own `ended` event — playback ran off the end of the cast. The DVR's catch-up
/// signal (Plan 14, stage 7): a rewound cast ends at the pin, so ending it means the reader
/// caught up.
[<Emit("$0.addEventListener('ended', $1)")>]
let private onEnded (player: Player) (handler: unit -> unit) : unit = jsNative

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

/// Turn on the player's stylesheet, which the shell links inert (`Style.deferredHeadTags`):
/// most sessions never open a recording, and a second render-blocking sheet in the head would
/// make all of them pay for the ones that do. Flipped at the first mount, when the sheet has
/// long since arrived — so this costs a style recalculation, not a round trip.
///
/// Idempotent, and a no-op where no such link exists: a page may mount a replay without being
/// the shell (the editor harness does).
[<Emit("(function (hook) { const link = document.querySelector('[' + hook + ']'); if (link) link.media = 'all' })($0)")>]
let private enableStylesheet (hook: string) : unit = jsNative

/// One mounted replay, and how to take it down.
type Mounted =
    { Dispose : unit -> unit }

/// Mount a replay into `element`, with whatever the model computed for this tab.
///
/// `idleTimeLimit` compresses the long gaps a terminal spends waiting for a person — an audit
/// read of a session someone left open for an hour should not be an hour long. `fit: "width"`
/// keeps the recorded geometry (the header's width and height are what make a replay come out
/// the shape the terminal actually was) while scaling to the panel.
///
/// `startAt` and `poster` are the player's own options (Plan 14, stage 4), which is why a
/// watch entered from a chip needs no second recording: a whole-terminal cast with a start
/// position expresses everything a slice can. Both are given in the RECORDING's clock and the
/// player maps them onto the compressed one itself; `poster: "npt:<t>"` costs nothing extra,
/// because the player builds the still by replaying to that point internally.
///
/// There is deliberately no `markers` option (Plan 25, stage 1). Chapters are written into the
/// cast as `"m"` events, so they ride the same idle compression as the records around them —
/// and supplying the option would STRIP the events in the file, which is how one mechanism
/// here stays one mechanism.
///
/// `caughtUp` is the DVR's half (Plan 14, stage 7): a rewound live terminal's cast ends at
/// the pin, so playing off its end means the reader has caught up — the handler jumps back
/// to live rather than leaving them on a stale final frame. `None` for every recording
/// whose end really is the end.
let mount (element: Browser.Types.Element) (replay: PaneReplay) (caughtUp: (unit -> unit) option) : Mounted =
    enableStylesheet Dom.playerStylesheetHook
    let url = blobUrl replay.Cast
    let options =
        [ "fit" ==> "width"
          "idleTimeLimit" ==> 2
          // The same face a live terminal wears. A literal here meant a recording replayed in
          // a different typeface than the terminal beside it — invisible on a box where both
          // fell back to the platform mono, plain on any box where they did not.
          "terminalFontFamily" ==> "var(--font-terminal)" ]
        @ (match replay.StartAt with Some at -> [ "startAt" ==> at ] | None -> [])
        @ (match replay.Poster with Some at -> [ "poster" ==> sprintf "npt:%f" at ] | None -> [])
    let player = create (box url) element (createObj options)
    caughtUp |> Option.iter (onEnded player)
    { Dispose =
        fun () ->
            player.dispose ()
            // The Blob outlives the player unless it is revoked, and a panel someone clicks
            // through leaks one per terminal otherwise.
            revoke url }
