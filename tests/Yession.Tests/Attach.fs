module Yession.Tests.Attach

// Attaching a terminal to somebody else's byte stream (Plan 16, part D), over a real
// WebSocket against a loopback provider.
//
// The provider below is also the REFERENCE implementation `docs/streams.md` points an
// outside implementer at: the smallest thing that conforms, running on every `check Ports`.
// That is the only reason it cannot rot — a sample nobody executes drifts from the client
// it claims to match, which is what the spec exists to stop.
//
// The provider here is written by hand — an HTTP upgrade, the RFC 6455 accept hash, and
// enough frame handling to echo — rather than built on a library, and that is the point
// rather than an inconvenience: the whole reason for choosing WebSocket over something of
// our own is that a THIRD PARTY can implement the other end. A test that talked to our own
// client through our own server abstraction would prove only that the two agreed.
//
// `Ports`, because the upgrade is the thing being tested and there is no meaningful
// in-memory stand-in for it.

open Fable.Core
open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Terminals

let private expect =
    function
    | Ok v -> v
    | Error e -> failwithf "%A" e

/// A loopback provider. `/echo` sends every data frame straight back, answers a `resize`
/// control frame by saying what size it was told, and on `kill` sends the termination frame
/// and then closes. `/abrupt` accepts the upgrade and drops the connection without saying
/// anything — which is the case an in-band termination frame exists FOR, since an abnormal
/// closure carries nothing.
type private Provider =
    abstract port : int
    abstract stop : unit -> JS.Promise<unit>

[<Emit("""(async () => {
  const http = await import('node:http')
  const crypto = await import('node:crypto')
  const GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11'
  const frame = (opcode, payload) => {
    const body = Buffer.from(payload)
    const head = body.length < 126
      ? Buffer.from([0x80 | opcode, body.length])
      : Buffer.concat([Buffer.from([0x80 | opcode, 126]), (() => { const b = Buffer.alloc(2); b.writeUInt16BE(body.length); return b })()])
    return Buffer.concat([head, body])
  }
  const server = http.createServer((_, res) => { res.statusCode = 426; res.end() })
  server.on('upgrade', (req, socket) => {
    const key = req.headers['sec-websocket-key']
    const accept = crypto.createHash('sha1').update(key + GUID).digest('base64')
    socket.write(
      'HTTP/1.1 101 Switching Protocols\r\n' +
      'Upgrade: websocket\r\nConnection: Upgrade\r\n' +
      'Sec-WebSocket-Accept: ' + accept + '\r\n\r\n')
    if (req.url === '/abrupt') { setTimeout(() => socket.destroy(), 30); return }
    // A provider that says things on the TEXT channel we have no meaning for: a control type
    // from a later version, and something that is not JSON at all — which is what reaching
    // for a framework's `send_text` to emit device output looks like from here.
    if (req.url === '/talkative') {
      socket.write(frame(0x1, JSON.stringify({ type: 'from-a-later-version' })))
      socket.write(frame(0x1, 'device output on the wrong channel'))
    }
    let buffer = Buffer.alloc(0)
    socket.on('data', (chunk) => {
      buffer = Buffer.concat([buffer, chunk])
      // One frame at a time, unfragmented, masked (every client frame is). Enough of RFC
      // 6455 to be a peer; deliberately not a library.
      for (;;) {
        if (buffer.length < 2) return
        const opcode = buffer[0] & 0x0f
        const masked = (buffer[1] & 0x80) !== 0
        let length = buffer[1] & 0x7f
        let offset = 2
        if (length === 126) { if (buffer.length < 4) return; length = buffer.readUInt16BE(2); offset = 4 }
        const maskKey = masked ? buffer.subarray(offset, offset + 4) : null
        if (masked) offset += 4
        if (buffer.length < offset + length) return
        const payload = Buffer.from(buffer.subarray(offset, offset + length))
        if (maskKey) for (let i = 0; i < payload.length; i++) payload[i] ^= maskKey[i % 4]
        buffer = buffer.subarray(offset + length)
        if (opcode === 0x8) { socket.end(); return }
        if (opcode === 0x1) {
          let control = null
          try { control = JSON.parse(payload.toString('utf8')) } catch (e) { control = null }
          if (control && control.type === 'resize') {
            socket.write(frame(0x2, 'sized ' + control.cols + 'x' + control.rows + '\n'))
          } else if (control && control.type === 'kill') {
            socket.write(frame(0x1, JSON.stringify({ type: 'exited', code: 7 })))
            socket.end()
            return
          }
        } else if (opcode === 0x2) {
          socket.write(frame(0x2, 'echo:' + payload.toString('utf8')))
        }
      }
    })
    socket.on('error', () => {})
  })
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve))
  return { port: server.address().port, stop: () => new Promise((r) => server.close(() => r())) }
})()""")>]
let private startProvider () : JS.Promise<Provider> = jsNative

let private ticket (port: int) (path: string) (capabilities: SourceCapabilities) : AttachTicket =
    { Url = sprintf "ws://127.0.0.1:%d%s" port path
      Capabilities = capabilities
      Label = Some "loopback" }

/// Poll for a condition rather than sleeping a fixed amount: what is being waited on is a
/// round trip over loopback, and a fixed sleep is either flaky or slow.
let private until (predicate: unit -> bool) : Async<bool> =
    let rec loop (remaining: int) =
        async {
            if predicate () then return true
            elif remaining <= 0 then return false
            else
                do! Async.Sleep 20
                return! loop (remaining - 20)
        }
    loop 3000

let private device = { SourceCapabilities.byteStream with CanResize = true }

let portsTests =
    testList "Foreign terminal attach (Plan 16)" [

        testCaseAsync "bytes go both ways, and a control frame is control rather than data" <|
            async {
                let! provider = startProvider () |> Async.AwaitPromise
                let received = System.Text.StringBuilder ()
                let! attached =
                    Yession.Host.AttachWs.attach (ticket provider.port "/echo" device) 80 24 (fun text ->
                        received.Append text |> ignore)
                let handle = attached |> expect
                // The opening size is sent as a TEXT frame; the provider answers on the data
                // channel, so seeing it proves the two channels are distinct rather than one
                // stream we happen to parse twice.
                let! sized = until (fun () -> received.ToString().Contains "sized 80x24")
                Expect.isTrue sized "the resize control frame arrived as control"
                handle.Write "hello"
                let! echoed = until (fun () -> received.ToString().Contains "echo:hello")
                Expect.isTrue echoed "a binary frame went out and came back"
                handle.Resize 132 43
                let! resized = until (fun () -> received.ToString().Contains "sized 132x43")
                Expect.isTrue resized "and a later resize is control too"
                handle.Kill ()
                let! ending = handle.Exited
                Expect.equal ending (SandboxExited 7) "the in-band exited frame carries the code"
                do! provider.stop () |> Async.AwaitPromise
            }

        // Control types will be added, and a client that treated an unknown one as fatal
        // would break every provider written against the newer spec. Ignoring it is the rule
        // `docs/streams.md` states, and this is the rule rather than the wording.
        testCaseAsync "a text frame we have no meaning for does not end the stream" <|
            async {
                let! provider = startProvider () |> Async.AwaitPromise
                let received = System.Text.StringBuilder ()
                let! attached =
                    Yession.Host.AttachWs.attach (ticket provider.port "/talkative" device) 80 24 (fun text ->
                        received.Append text |> ignore)
                let handle = attached |> expect
                handle.Write "still here"
                let! echoed = until (fun () -> received.ToString().Contains "echo:still here")
                Expect.isTrue echoed "the stream carries data after text it could not read"
                // Close the socket before the server: `server.close` waits for its
                // connections, so a test that walks away from an open one hangs the whole
                // suite rather than failing.
                handle.Kill ()
                let! _ = handle.Exited
                do! provider.stop () |> Async.AwaitPromise
            }

        // Text is CONTROL, and a provider that sends device output on it gets a terminal
        // showing nothing while every layer below is working correctly. So the bytes must
        // NOT arrive as output — the fault is the provider's, and a client that quietly
        // accepted them would make the wire mean two things.
        testCaseAsync "device output sent as text is not mistaken for output" <|
            async {
                let! provider = startProvider () |> Async.AwaitPromise
                let received = System.Text.StringBuilder ()
                let! attached =
                    Yession.Host.AttachWs.attach (ticket provider.port "/talkative" device) 80 24 (fun text ->
                        received.Append text |> ignore)
                let handle = attached |> expect
                handle.Write "marker"
                let! echoed = until (fun () -> received.ToString().Contains "echo:marker")
                Expect.isTrue echoed "the round trip completed, so anything text-borne had arrived by now"
                Expect.isFalse
                    (received.ToString().Contains "device output on the wrong channel")
                    "a text frame is never data"
                handle.Kill ()
                let! _ = handle.Exited
                do! provider.stop () |> Async.AwaitPromise
            }

        // The reason termination is a frame and not a close code: an abnormal closure (1006)
        // carries nothing at all, and that path exists whatever the provider intends.
        testCaseAsync "a stream that just stops is a failure, not an exit" <|
            async {
                let! provider = startProvider () |> Async.AwaitPromise
                let! attached =
                    Yession.Host.AttachWs.attach (ticket provider.port "/abrupt" device) 80 24 ignore
                match attached with
                | Error _ ->
                    // Racing the drop: the connection may never have opened, which is the
                    // same fact reported one step earlier.
                    do! provider.stop () |> Async.AwaitPromise
                | Ok handle ->
                    let! ending = handle.Exited
                    match ending with
                    | SandboxRunFailed reason -> Expect.isFalse (reason = "") "it says why, however little it knows"
                    | SandboxExited code -> failwithf "a dropped connection is not an exit (got %d)" code
                    do! provider.stop () |> Async.AwaitPromise
            }

        testCaseAsync "a provider that is not there is an error, not a hang" <|
            async {
                // Port 1 on loopback: reserved, never listening, and refused immediately.
                let! attached =
                    Yession.Host.AttachWs.attach
                        { Url = "ws://127.0.0.1:1/echo"; Capabilities = device; Label = Some "nothing" }
                        80 24 ignore
                Expect.isError attached "a refused connection is reported, not awaited"
            }
    ]
