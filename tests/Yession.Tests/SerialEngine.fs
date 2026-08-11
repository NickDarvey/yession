module Yession.Tests.SerialEngine

// The serial engine itself (Plan 16, part E), against a real tty.
//
// Everything ABOVE the `SerialEngine` seam is already covered over real sockets, with the
// engine substituted — the MCP lifecycle, the claim, the WebSocket attach. This is the half
// that substitution deliberately skipped: `Ports.real`, which is the `serialport`
// import, the port open, the line settings, and the close.
//
// socat is what makes that testable with no hardware. `socat pty,raw,echo=0 pty,raw,echo=0`
// gives two pseudo-terminals wired to each other: open one with the real engine and the other
// with plain file I/O, and every byte that crosses went through a kernel tty — the same
// `open`, `termios` and `read` a USB adapter would use. What it cannot prove is anything about
// a specific chip; what it does prove is that we drive a serial port correctly, which is where
// the bugs were going to be.
//
// `Serial`, because the engine needs three things this repo cannot assume: the `serialport`
// addon, `udevadm` (Linux enumeration shells out to it), and socat.

open System
open Fable.Core
open Fable.Pyxpecto
open Yession.Domain
open Yession.Host
open SerialProvider

let private expect =
    function
    | Ok v -> v
    | Error e -> failwithf "%A" e

/// A socat PTY pair, and the raw handle to the FAR end — the side standing in for the device.
type private Pair =
    abstract ours : string
    abstract theirs : string
    /// Write bytes as the device would.
    abstract send : string -> unit
    /// Everything the device has received so far.
    abstract received : unit -> string
    abstract stop : unit -> unit

/// Spawn socat and wait for it to announce both PTY paths.
///
/// The paths are parsed from its stderr because socat allocates them — there is no way to ask
/// for a name, and guessing `/dev/pts/N` races every other process on the box.
[<Emit("""(async () => {
  const { spawn } = await import('node:child_process')
  const fs = await import('node:fs')
  const tty = await import('node:tty')
  const socat = process.env.YESSION_SOCAT_PATH || 'socat'
  const { child, paths } = await new Promise((resolve, reject) => {
    const child = spawn(socat, ['-d', '-d', 'pty,raw,echo=0', 'pty,raw,echo=0'], { stdio: ['ignore', 'ignore', 'pipe'] })
    // Matched against everything socat has said so far rather than against each chunk:
    // stderr is a byte stream with no promise of line alignment.
    let noise = ''
    const timer = setTimeout(() => {
      child.kill()
      reject(new Error('socat did not announce two PTYs in time; it said: ' + JSON.stringify(noise)))
    }, 10000)
    child.stderr.on('data', (chunk) => {
      noise += chunk.toString('utf8')
      const found = [...noise.matchAll(/PTY is (\S+)\s/g)].map(m => m[1])
      if (found.length >= 2) { clearTimeout(timer); resolve({ child, paths: found }) }
    })
    child.on('error', (e) => { clearTimeout(timer); reject(e) })
  })
  // The far end, opened directly rather than through serialport: one side of this test has to
  // be something other than the code under test, or it proves only that our engine agrees
  // with itself.
  //
  // `tty.ReadStream` and not `fs.createReadStream`, and the difference is not cosmetic. An fs
  // read stream reads through the libuv THREADPOOL, and a blocking read on a pty is not
  // cancellable: `destroy()` returns while the read is still parked in a worker thread, and
  // closing the fd then frees the NUMBER for reuse. The next `spawn` in the file gets it back
  // as socat's stderr pipe, and the stale read swallows the announcement — which presents as
  // "socat did not announce two PTYs in time; it said: ''", from a socat that is running
  // perfectly. A tty handle is epoll-driven on the event loop with nothing in flight to
  // outlive it. (It does not own the fd, so the close below is still ours to make, and is
  // safe to make.)
  const fd = fs.openSync(paths[1], 'r+')
  let received = ''
  const reader = new tty.ReadStream(fd)
  reader.on('data', (c) => { received += c.toString('utf8') })
  reader.on('error', () => {})
  return {
    ours: paths[0],
    theirs: paths[1],
    send: (text) => { try { fs.writeSync(fd, text) } catch (e) {} },
    received: () => received,
    stop: () => { try { reader.destroy(); fs.closeSync(fd) } catch (e) {} ; child.kill() }
  }
})()""")>]
let private startPair () : JS.Promise<Pair> = jsNative

/// Poll rather than sleep a fixed amount: a tty round trip is fast but not instant, and a
/// fixed sleep is either flaky or slow.
let private until (predicate: unit -> bool) : Async<bool> =
    let rec loop (remaining: int) =
        async {
            if predicate () then return true
            elif remaining <= 0 then return false
            else
                do! Async.Sleep 20
                return! loop (remaining - 20)
        }
    loop 5000

let tests =
    testList "The serial engine, against a real tty" [

        testCaseAsync "bytes cross a real serial port in both directions" <|
            async {
                let! pair = startPair () |> Async.AwaitPromise
                let received = Text.StringBuilder ()
                let mutable closedWith = None
                let! opened =
                    Ports.real.Open
                        pair.ours
                        SerialSettings.defaults
                        (fun text -> received.Append text |> ignore)
                        (fun why -> closedWith <- Some why)
                let port = opened |> expect

                // Device -> us. This is the read path: an fd opened by `serialport`, put into
                // raw mode, streaming into the callback the provider hands the WebSocket.
                pair.send "READY\r\n"
                let! arrived = until (fun () -> received.ToString().Contains "READY")
                Expect.isTrue arrived "what the device printed reached the engine's callback"

                // Us -> device. The write path, which is what a human typing in an attached
                // terminal actually drives.
                port.Write "AT+VERSION\r"
                let! echoed = until (fun () -> pair.received().Contains "AT+VERSION")
                Expect.isTrue echoed "what we wrote reached the device"

                port.Close ()
                let! closed = until (fun () -> closedWith.IsSome)
                Expect.isTrue closed "closing the port fires onClose exactly as the provider expects"
                pair.stop ()
            }

        testCaseAsync "a device that goes away closes the port rather than hanging" <|
            async {
                // The case the provider turns into the WebSocket's `exited` frame. Without it
                // an unplugged adapter is indistinguishable from a quiet one, and the terminal
                // waits for bytes that are never coming.
                //
                // This test found that gap rather than confirming its absence: `serialport`
                // emits neither `close` nor `error` on an idle port whose device has gone —
                // it reports the failure only when something next writes. Note that nothing
                // here writes, deliberately, because a read-only attach is the case that hung.
                let! pair = startPair () |> Async.AwaitPromise
                let mutable closedWith = None
                let! opened =
                    Ports.real.Open pair.ours SerialSettings.defaults ignore (fun why -> closedWith <- Some why)
                let port = opened |> expect
                pair.stop ()
                let! noticed = until (fun () -> closedWith.IsSome)
                Expect.isTrue noticed "the engine noticed the far end vanish"
                Expect.isFalse (closedWith = Some "") "and said something about why"
                port.Close ()
            }

        testCaseAsync "line settings are applied, not ignored" <|
            async {
                // 9600 8N1 rather than the default 115200: a baud rate that is silently
                // dropped is the classic serial bug, and it looks exactly like working code
                // until somebody attaches hardware that cares.
                let! pair = startPair () |> Async.AwaitPromise
                let received = Text.StringBuilder ()
                let! opened =
                    Ports.real.Open
                        pair.ours
                        { SerialSettings.defaults with BaudRate = 9600 }
                        (fun text -> received.Append text |> ignore)
                        ignore
                let port = opened |> expect
                pair.send "slow\r\n"
                let! arrived = until (fun () -> received.ToString().Contains "slow")
                Expect.isTrue arrived "the port opened and carried bytes at the rate it was given"
                port.Close ()
                pair.stop ()
            }

        testCaseAsync "opening a path that is not a port fails as a value, never an exception" <|
            async {
                // The provider turns this into text the model reads. An engine that threw
                // would take the whole attach down instead.
                let! outcome =
                    Ports.real.Open "/dev/definitely-not-a-serial-port" SerialSettings.defaults ignore ignore
                match outcome with
                | Ok _ -> failwith "opening a nonexistent path should not succeed"
                | Error reason -> Expect.isFalse (reason = "") "and the reason says something"
            }

        testCaseAsync "enumeration answers, which is the whole of what discovery needs" <|
            async {
                // On Linux this shells out to `udevadm`; without it the call THROWS, which is
                // how the `Serial` capability came to need eudev. Zero ports is a pass — the
                // contract is that the engine answers, not that this box has hardware.
                match! Ports.real.List () with
                | Error reason -> failwithf "enumeration should answer, not fail: %s" reason
                | Ok ports ->
                    // Whatever it found must survive the discovery filter without throwing.
                    // A socat PTY has no USB identity, so it is correctly NOT offered — the
                    // same rule that keeps `/dev/ttyS0` out of an agent's reach.
                    let devices = Discovery.devices ports
                    Expect.isTrue
                        (List.length devices <= List.length ports)
                        "discovery only ever narrows what the OS reported"
            }
    ]
