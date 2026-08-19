module SerialProvider.Ws

// The server half of the byte-stream leg (Plan 16, part D/E), hand-written.
//
// `AttachWs.fs` is the client — what a session opens against a provider. This is what a
// provider answers with, and the two are deliberately independent implementations of RFC
// 6455 rather than two halves of one library: the whole reason the data plane is a WebSocket
// is that a THIRD PARTY can be on either end, and a pair built on one library would only ever
// prove the two agreed with each other.
//
// Only what the attach protocol needs: an upgrade, unfragmented text and binary frames, the
// client's mask, close and ping. No extensions, no continuation frames, no compression — a
// device stream is short frames in both directions, and every line here is a line somebody
// has to be able to check against the RFC.

open Fable.Core
open SerialProvider.Interop

[<ImportAll("node:crypto")>]
let private nodeCrypto : obj = jsNative

/// One connected peer, from the provider's side.
type WsPeer =
    { /// A DATA frame (binary): bytes from the device.
      Send : string -> unit
      /// A CONTROL frame (text): JSON the client reads as protocol, never as device output.
      /// Keeping the two on different opcodes is what stops a device that happens to print
      /// JSON from being mistaken for the provider talking.
      Control : string -> unit
      Close : unit -> unit }

/// Accept upgrades on an existing HTTP server.
///
/// `route` is given the request path and decides whether to take the connection: `None`
/// refuses the upgrade (the socket is closed with a 404 handshake), `Some` receives the peer
/// and returns the two callbacks the provider wants — what to do with an inbound data frame,
/// and what to do when the socket goes away.
[<Emit("""(function (server, route, crypto) {
  // NEVER name these after the F# parameters they are substituted from: Fable inlines the
  // caller's own identifier for `server`, so `const server = server` becomes `const server = server`
  // and dies in the temporal dead zone. (The same trap cost `AttachWs.fs` a debugging round.)
  const httpServer = server
  const dispatch = route
  // Imported rather than `require`d: this file is ESM both bundled and unbundled, and the
  // bundle's createRequire banner does not exist in a plain Fable run.
  const GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11'
  const frame = (opcode, payload) => {
    const body = Buffer.from(payload, 'utf8')
    let head
    if (body.length < 126) head = Buffer.from([0x80 | opcode, body.length])
    else if (body.length < 65536) {
      head = Buffer.alloc(4); head[0] = 0x80 | opcode; head[1] = 126; head.writeUInt16BE(body.length, 2)
    } else {
      head = Buffer.alloc(10); head[0] = 0x80 | opcode; head[1] = 127
      head.writeUInt32BE(0, 2); head.writeUInt32BE(body.length, 6)
    }
    return Buffer.concat([head, body])
  }
  httpServer.on('upgrade', (req, socket) => {
    socket.on('error', () => {})
    const path = new URL(req.url, 'http://local').pathname
    const handlers = dispatch(path)
    if (!handlers) {
      socket.write('HTTP/1.1 404 Not Found\r\nConnection: close\r\n\r\n')
      socket.destroy()
      return
    }
    const key = req.headers['sec-websocket-key']
    const accept = crypto.createHash('sha1').update(key + GUID).digest('base64')
    socket.write(
      'HTTP/1.1 101 Switching Protocols\r\n' +
      'Upgrade: websocket\r\nConnection: Upgrade\r\n' +
      'Sec-WebSocket-Accept: ' + accept + '\r\n\r\n')

    let gone = false
    const peer = {
      Send: (text) => { if (!gone) socket.write(frame(0x2, text)) },
      Control: (text) => { if (!gone) socket.write(frame(0x1, text)) },
      Close: () => { if (!gone) { gone = true; try { socket.write(frame(0x8, '')) } catch (e) {} ; socket.end() } }
    }
    const bound = handlers(peer)
    const finish = () => { if (!gone) { gone = true; bound.closed() } }

    let buffer = Buffer.alloc(0)
    socket.on('data', (chunk) => {
      buffer = Buffer.concat([buffer, chunk])
      for (;;) {
        if (buffer.length < 2) return
        const opcode = buffer[0] & 0x0f
        const masked = (buffer[1] & 0x80) !== 0
        let length = buffer[1] & 0x7f
        let offset = 2
        if (length === 126) { if (buffer.length < 4) return; length = buffer.readUInt16BE(2); offset = 4 }
        else if (length === 127) {
          if (buffer.length < 10) return
          // The high word is refused rather than truncated: a 4GB frame is not a device.
          if (buffer.readUInt32BE(2) !== 0) { socket.destroy(); return }
          length = buffer.readUInt32BE(6); offset = 10
        }
        const maskKey = masked ? buffer.subarray(offset, offset + 4) : null
        if (masked) offset += 4
        if (buffer.length < offset + length) return
        const payload = Buffer.from(buffer.subarray(offset, offset + length))
        if (maskKey) for (let i = 0; i < payload.length; i++) payload[i] ^= maskKey[i % 4]
        buffer = buffer.subarray(offset + length)
        if (opcode === 0x8) { finish(); socket.end(); return }
        else if (opcode === 0x9) { socket.write(frame(0xa, payload.toString('utf8'))) }
        else if (opcode === 0x1) { bound.control(payload.toString('utf8')) }
        else if (opcode === 0x2) { bound.data(payload.toString('utf8')) }
      }
    })
    socket.on('close', finish)
  })
})($0, $1, $2)""")>]
let private accept (server: HttpServer) (route: string -> obj) (crypto: obj) : unit = jsNative

/// What a route does with a connection it took.
type WsHandlers =
    { /// A binary frame: bytes the client wants written to the device.
      Data : string -> unit
      /// A text frame: control JSON (`resize`, `kill`).
      Control : string -> unit
      /// The socket went away, for any reason. Fires exactly once.
      Closed : unit -> unit }

/// Serve WebSocket upgrades on `server`, dispatching by path.
let serve (server: HttpServer) (route: string -> (WsPeer -> WsHandlers) option) : unit =
    let dispatch (path: string) : obj =
        match route path with
        | None -> null
        | Some bind ->
            box (fun (peer: WsPeer) ->
                let handlers = bind peer
                box
                    {| data = handlers.Data
                       control = handlers.Control
                       closed = handlers.Closed |})
    accept server dispatch nodeCrypto
