module Yession.Host.AttachWs

// Attaching a terminal to a byte stream somebody else is producing (Plan 16, part D).
//
// This file is the client half of a contract somebody outside this repository implements,
// and `docs/streams.md` is that contract written down — MUST/SHOULD/MAY, and which two
// rules are load-bearing. Change what happens here and change it there; a spec that drifts
// from its only implementation is worse than none, which is how `{"type":"failed"}` came to
// be sent, parsed, and specified nowhere.
//
// WebSocket, for three reasons and not because it is fashionable: the client costs nothing
// (Node 22+ ships a global `WebSocket`, and the repo pins 24), a provider is already running
// an HTTP server for its MCP endpoint so the upgrade rides the same port, and
// terminal-over-WebSocket is the shape every comparable system uses — ttyd, gotty, xterm.js
// attach, `kubectl exec`. That last one matters most: the point of this seam is that a third
// party can implement the other end.
//
// It breaks the repo's SSE-only streak, and that is fine. This is session ↔ local provider,
// like the control RPC — not the session transport `design.md` pins.
//
// The wire:
//   * BINARY frames are data, in both directions. No framing of ours.
//   * TEXT frames are control: {"type":"resize","cols":N,"rows":M}, {"type":"kill"}.
//   * Termination is an explicit {"type":"exited","code":N} and THEN a close. Not because a
//     close reason is too small — 123 bytes is ample — but because an abnormal closure
//     (1006) carries nothing at all, and that path exists whatever we do. The two cases then
//     land cleanly on the domain type that already draws the distinction: `SandboxExited`
//     for a source that ended, `SandboxRunFailed` for one that merely stopped.

open Fable.Core
open Yession.Domain

/// What the connection gave us. `ok = false` means it never opened; everything else is only
/// meaningful when it did.
type private Connection =
    abstract ok : bool
    abstract reason : string
    abstract write : string -> unit
    abstract control : string -> unit
    abstract close : unit -> unit
    /// Resolves exactly once, when the stream ends — with the code the `exited` frame
    /// carried, or a reason when it ended without one.
    abstract exited : JS.Promise<Ending>

and private Ending =
    abstract code : int
    abstract reason : string

[<Emit("""(function (url, onData) { return (
(() => new Promise((resolve) => {
  const target = url
  const sink = onData
  let settled = false
  let finish = null
  const exited = new Promise((r) => { finish = r })
  let ended = false
  const end = (v) => { if (!ended) { ended = true; finish(v) } }
  let ws
  try { ws = new WebSocket(target) } catch (e) { resolve({ ok: false, reason: String((e && e.message) || e) }); return }
  ws.binaryType = 'arraybuffer'
  let exitCode = null
  let failure = null
  let warnedText = false
  ws.addEventListener('message', (ev) => {
    const d = ev.data
    if (typeof d === 'string') {
      // Text is CONTROL. A provider that reaches for its framework's `send_text` to emit
      // device output gets a terminal showing nothing, and every layer below here is
      // working correctly, so nothing else can say why. Said once per connection: the
      // fault repeats per frame and the diagnosis does not.
      let f = null
      try { f = JSON.parse(d) } catch (e) { f = null }
      const known = f && (f.type === 'exited' || f.type === 'failed')
      if (!known && !warnedText) {
        warnedText = true
        console.warn('attach ' + target + ': ignoring a TEXT frame — text frames are control, device output goes in BINARY frames (docs/streams.md)')
      }
      if (!f) return
      if (f.type === 'exited') exitCode = (f.code | 0)
      else if (f.type === 'failed') failure = String(f.reason || 'the source failed')
    } else {
      sink(Buffer.from(d).toString('utf8'))
    }
  })
  ws.addEventListener('open', () => {
    if (settled) return
    settled = true
    resolve({
      ok: true,
      reason: '',
      write: (s) => { try { ws.send(Buffer.from(s, 'utf8')) } catch (e) {} },
      control: (s) => { try { ws.send(s) } catch (e) {} },
      // `kill` ASKS the provider to end the stream; ending it is the provider's to do,
      // and it ends it by sending the termination frame and then closing. Closing from
      // here in the same tick would race that frame and lose the exit code — which is
      // how this was first written, and what the loopback test caught. The deadline is
      // not a second mechanism doing the same job: the frame is the request, this is
      // what happens when a provider does not answer it.
      close: () => { setTimeout(() => { try { ws.close() } catch (e) {} }, 2000) },
      exited
    })
  })
  // Swallowed: 'error' is always followed by 'close', and that is where both the failure to
  // open and the end of the stream are decided — one place, so the two cannot disagree.
  ws.addEventListener('error', () => {})
  ws.addEventListener('close', () => {
    if (!settled) { settled = true; resolve({ ok: false, reason: 'could not attach to ' + target }) }
    if (failure !== null) end({ code: -1, reason: failure })
    else if (exitCode !== null) end({ code: exitCode, reason: '' })
    else end({ code: -1, reason: 'the stream closed without saying why' })
  })
}))()
) })($0, $1)""")>]
let private connect (url: string) (onData: string -> unit) : JS.Promise<Connection> = jsNative

[<Emit("JSON.stringify($0)")>]
let private toJson (value: obj) : string = jsNative

/// The session's `AttachTerminal`: a ticket in, a handle shaped exactly like a pty's out.
///
/// `PtyHandle` and not a parallel type, because everything downstream — the lease, the input
/// route, the transcript — already speaks it. Where the source genuinely lacks something its
/// `SourceCapabilities` says so and the manager never calls the member; the member itself is
/// still there, and sending a resize to something that ignores it is nothing rather than an
/// error.
let attach : AttachTerminal =
    fun ticket cols rows onData ->
        async {
            let! connection = connect ticket.Url onData |> Interop.awaitPromise
            if not connection.ok then return Error connection.reason
            else
                // The opening size, sent once, for a source that has one. A source that does
                // not declare `CanResize` is never told a size at all — inventing 80x24 for a
                // serial line would be a fact nobody could check.
                if ticket.Capabilities.CanResize then
                    connection.control (toJson {| ``type`` = "resize"; cols = cols; rows = rows |})
                return
                    Ok
                        { Write = connection.write
                          Resize = fun c r -> connection.control (toJson {| ``type`` = "resize"; cols = c; rows = r |})
                          Kill =
                            fun () ->
                                connection.control (toJson {| ``type`` = "kill" |})
                                connection.close ()
                          // ^ asks, then holds the provider to a deadline. See the wire note
                          //   above: termination is the provider's frame, not our close.
                          Exited =
                            async {
                                let! ending = connection.exited |> Interop.awaitPromise
                                return
                                    if ending.reason = "" then SandboxExited ending.code
                                    else SandboxRunFailed ending.reason
                            } }
        }
