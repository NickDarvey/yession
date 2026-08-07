module Yession.Host.Emulator

// The Session Process's terminal emulator (Plan 13, stage 2b): `@xterm/headless`, which is
// the same emulator the browser renders with minus the DOM.
//
// That sameness is the point. The Process has to answer three questions about a terminal —
// what the screen looks like for a peer joining mid-session, whether a foreground program
// has taken the alternate screen, and where OSC 133 marks fall — and answering them with a
// second, home-grown interpretation of ANSI would mean two implementations that agree until
// they do not, with the disagreement showing up as a screen that reads differently on two
// people's machines. Stage 1's pure-F# SGR parser is not a competitor here: it renders a
// STREAM of block output, which is a different job from maintaining a SCREEN, and it stays
// where it is.
//
// Bindings are hand-written `[<Import>]`/`[<Emit>]` over the small surface actually used —
// the `ProseMirror.fs` precedent, and the repo invariant that the platform boundary is
// interop rather than authored JS.

open Fable.Core
open Fable.Core.JsInterop
open Yession.SessionProcess

// --- The binding ------------------------------------------------------------------------

/// `@xterm/headless`'s Terminal. Only the members used are typed; everything else is opaque.
type [<AllowNullLiteral>] private Term =
    /// Buffered and parsed asynchronously; the callback fires once THIS write has been
    /// applied, and writes are applied in order. There is no synchronous form.
    abstract write : string * (unit -> unit) -> unit
    abstract resize : int * int -> unit
    abstract dispose : unit -> unit
    abstract loadAddon : obj -> unit
    /// The active buffer, whose `type` is `"normal"` or `"alternate"`.
    abstract buffer : Buffers

and [<AllowNullLiteral>] private Buffers =
    abstract active : Buffer
    /// Fires with the new active buffer whenever a program enters or leaves the alternate
    /// screen — xterm's own API for the transition, so no DECSET 1049 parsing here.
    abstract onBufferChange : (Buffer -> unit) -> unit

and [<AllowNullLiteral>] private Buffer =
    abstract ``type`` : string

type [<AllowNullLiteral>] private SerializeAddon =
    abstract serialize : unit -> string

// DEFAULT imports, not named ones, and that is forced by how the packages ship rather than
// a preference. `@xterm/headless`'s `main` is a CommonJS bundle and it declares no
// `exports` map, so Node resolves an ESM `import` to that CJS file and offers only the
// default — a named `import { Terminal }` fails at load with "does not provide an export
// named 'Terminal'". Its `module` field does point at ESM, but at `lib/xterm.mjs`: the
// BROWSER build, which is not what a headless emulator wants and which Node ignores anyway.
[<ImportDefault("@xterm/headless")>]
let private headless : obj = jsNative

[<ImportDefault("@xterm/addon-serialize")>]
let private serializeModule : obj = jsNative

/// `new Terminal({ cols, rows, allowProposedApi: true, scrollback })`.
///
/// `allowProposedApi` is required for the serialize addon, which is what a snapshot is.
/// `scrollback` is bounded here rather than left at the default: the screen is a live view a
/// joining peer downloads in one frame, and the transcript — not this — is what holds
/// everything a terminal ever printed.
[<Emit("new $0.Terminal({ cols: $1, rows: $2, allowProposedApi: true, scrollback: $3 })")>]
let private newTerminal (m: obj) (cols: int) (rows: int) (scrollback: int) : Term = jsNative

[<Emit("new $0.SerializeAddon()")>]
let private newSerializeAddon (m: obj) : SerializeAddon = jsNative

/// Lines of scrollback the Process keeps per terminal. A snapshot travels in one frame, so
/// this is a frame-size decision as much as a memory one.
let private scrollback = 1000

// --- The capability ---------------------------------------------------------------------

/// Open an emulator. `cols`/`rows` are the terminal's size register (default 80x24).
let openEmulator : OpenEmulator =
    fun cols rows ->
        let term = newTerminal headless cols rows scrollback
        let serializer = newSerializeAddon serializeModule
        term.loadAddon (box serializer)
        { Write = fun data -> term.write (data, ignore)
          // Empty write as a BARRIER. xterm parses writes asynchronously and in order, so a
          // zero-length write queued behind everything already fed resolves only once all of
          // it has been applied — which is the difference between serializing the screen and
          // serializing a screen that has not been drawn yet. There is no synchronous flush
          // to reach for instead.
          Serialize =
            fun () ->
                Async.FromContinuations (fun (cont, _, _) ->
                    term.write ("", fun () -> cont (serializer.serialize ())))
          Resize = fun c r -> term.resize (c, r)
          // Reported on the TRANSITION only, which is what `onBufferChange` already is: the
          // flip policy is a decision about a change of ownership, and firing it on every
          // write would make "a TUI took the screen" a thing said continuously.
          OnAltScreen =
            fun handler -> term.buffer.onBufferChange (fun buffer -> handler (buffer.``type`` = "alternate"))
          Dispose = fun () -> term.dispose () }
