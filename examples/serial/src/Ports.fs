module SerialProvider.Ports

// The engine behind the serial provider (Plan 16, part E): enumerate the host's ports, and
// open one as a byte stream.
//
// A SEAM rather than a direct dependency, for the reason every other backend in this repo is
// one. `serialport` is a native addon, and the provider must be testable — and shippable —
// where it is absent:
//
//   * it is a LAZY dynamic import, so a process that never opens a port never loads the
//     addon, and a box without it degrades to "no devices" rather than failing to start.
//     That is Plan 16's own wording and it is what makes the provider safe to run anywhere.
//   * the tests drive the provider through a fake engine, so the MCP lifecycle and the
//     WebSocket attach are covered on a box with no hardware at all — which is every CI box
//     we have.
//
// Cross-platform without a branch here: `serialport` is the portability layer (Linux, macOS,
// Windows), and `@serialport/bindings-cpp` ships prebuilt addons for each in its own tarball,
// so an ordinary `npm install` of the published package gets a working engine everywhere.

open Fable.Core
open SerialProvider

/// An open port. Deliberately the same shape as everything else that streams bytes in this
/// repo — write, close, and a callback for what arrives — so the provider's WebSocket side
/// has nothing to adapt.
type OpenPort =
    { Write : string -> unit
      Close : unit -> unit }

/// What the provider needs from the world. Two functions, both failable, neither knowing
/// what MCP is.
type SerialEngine =
    { /// Every port the OS can see, interesting or not — `Discovery.devices` is what decides
      /// which are offered, and it does that in the Domain where it is testable.
      List : unit -> Async<Result<SerialPortInfo list, string>>
      /// Open one, with the given settings. `onData` receives decoded text as it arrives;
      /// `onClose` fires once, when the port goes away for any reason (unplugged, closed by
      /// us, an error) — the provider turns that into the WebSocket's termination frame.
      Open :
        string
            -> SerialSettings
            -> (string -> unit)
            -> (string -> unit)
            -> Async<Result<OpenPort, string>> }

module SerialEngine =

    /// A host with no engine: no devices, and opening anything says why. What a build
    /// without the addon degrades to, and what the tests replace.
    let none : SerialEngine =
        { List = fun () -> async { return Ok [] }
          Open = fun path _ _ _ -> async { return Error (sprintf "no serial engine to open %s with" path) } }

type private RawPort =
    abstract path : string
    abstract vendorId : string
    abstract productId : string
    abstract serialNumber : string
    abstract manufacturer : string

/// `serialport`'s `SerialPort.list()`, behind a dynamic import. An import that throws — the
/// package absent, or present without an addon for this platform — answers with an empty
/// list and the reason, which is the degradation this whole module exists to make ordinary.
[<Emit("""(async () => {
  try {
    const { SerialPort } = await import('serialport')
    const ports = await SerialPort.list()
    return { ok: true, reason: '', ports: ports.map(p => ({
      path: p.path || '',
      vendorId: p.vendorId || '',
      productId: p.productId || '',
      serialNumber: p.serialNumber || '',
      manufacturer: p.manufacturer || ''
    })) }
  } catch (err) {
    return { ok: false, reason: String((err && err.message) || err), ports: [] }
  }
})()""")>]
let private listRaw () : JS.Promise<{| ok: bool; reason: string; ports: RawPort array |}> = jsNative

type private RawOpen =
    abstract ok : bool
    abstract reason : string
    abstract write : string -> unit
    abstract close : unit -> unit

/// Open one port. `binary` is deliberately NOT used: the stream is decoded as UTF-8 text
/// because that is what a terminal emulator downstream consumes, and a device speaking
/// binary is a case this provider does not claim to serve.
///
/// The interval is not belt-and-braces over the `close`/`error` handlers — it covers a case
/// they demonstrably do not. An IDLE open port whose device has gone away emits NEITHER
/// event: measured against a hung-up tty, `serialport` sat there with `isOpen === true`
/// indefinitely, and only surfaced EIO when something tried to write. Nothing writes to a
/// device the human is only reading from, so without this an unplugged adapter is
/// indistinguishable from a quiet one and the attached terminal waits forever. What the
/// handlers cover and this does not is the reverse: OUR close, and an I/O error on a device
/// node that still exists.
///
/// Vanishing is detected by the device node disappearing, because that is what unplugging
/// actually does — `/dev/ttyUSB0` is removed, exactly as a pty slave is removed when its
/// master hangs up. Armed only when the path exists as a file at open time, which keeps it
/// off Windows, where `COM3` is not a filesystem entry and the check would report every
/// port as instantly gone.
[<Emit("""(async function (path, baud, dataBits, stopBits, parity, onData, onClose) {
  try {
    const fs = await import('node:fs')
    const { SerialPort } = await import('serialport')
    const port = await new Promise((resolve, reject) => {
      const p = new SerialPort(
        { path: path, baudRate: baud, dataBits: dataBits, stopBits: stopBits, parity: parity, autoOpen: true },
        (err) => { if (err) reject(err); else resolve(p) })
    })
    let closed = false
    let watchdog = null
    const finish = (why) => {
      if (watchdog) { clearInterval(watchdog); watchdog = null }
      if (!closed) { closed = true; onClose(why) }
    }
    port.on('data', (chunk) => onData(chunk.toString('utf8')))
    port.on('close', () => finish('the port closed'))
    port.on('error', (err) => finish(String((err && err.message) || err)))
    if (fs.existsSync(path)) {
      watchdog = setInterval(() => {
        if (fs.existsSync(path)) return
        // Release the fd as well as reporting it: the device is gone, so the handle is
        // never becoming useful again, and `close` on a dead node can itself throw.
        try { port.close(() => {}) } catch (e) {}
        finish('the device went away')
      }, 500)
      // Never a reason for the process to stay alive.
      if (watchdog.unref) watchdog.unref()
    }
    return {
      ok: true,
      reason: '',
      write: (text) => { try { port.write(text) } catch (e) { finish(String((e && e.message) || e)) } },
      close: () => { try { port.close(() => {}) } catch (e) { finish('closed') } }
    }
  } catch (err) {
    return { ok: false, reason: String((err && err.message) || err), write: () => {}, close: () => {} }
  }
})($0, $1, $2, $3, $4, $5, $6)""")>]
let private openRaw
    (path: string)
    (baud: int)
    (dataBits: int)
    (stopBits: int)
    (parity: string)
    (onData: string -> unit)
    (onClose: string -> unit)
    : JS.Promise<RawOpen> =
    jsNative

/// The real engine. Everything it does is lazy: this value costs nothing to build on a box
/// with no `serialport` installed, and the import happens on the first list or open.
let real : SerialEngine =
    { List =
        fun () ->
            async {
                let! outcome = listRaw () |> Interop.awaitPromise
                if not outcome.ok then
                    // Not an error the caller must handle — a host without the addon has no
                    // devices, which is a true and useful answer.
                    eprintfn "serial: no engine (%s)" outcome.reason
                    return Ok []
                else
                    return
                        Ok (
                            outcome.ports
                            |> Array.toList
                            |> List.map (fun raw ->
                                { Path = raw.path
                                  Usb = UsbId.create raw.vendorId raw.productId
                                  SerialNumber = (if raw.serialNumber = "" then None else Some raw.serialNumber)
                                  Manufacturer =
                                    (if raw.manufacturer = "" then None else Some raw.manufacturer) }))
            }
      Open =
        fun path settings onData onClose ->
            async {
                let! opened =
                    openRaw path settings.BaudRate settings.DataBits settings.StopBits settings.Parity onData onClose
                    |> Interop.awaitPromise
                if not opened.ok then return Error opened.reason
                else return Ok { Write = opened.write; Close = opened.close }
            } }
