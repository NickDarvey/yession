# A serial device provider

An MCP server that lends the host's serial devices to an agent. Point any MCP client at it —
Yession is one, but nothing here knows that — and the agent gets four tools and a byte stream
to a real tty.

It is here because it is the smallest complete answer to a question that keeps coming up:
**what does an integration actually look like, and what do I have to build myself?**

## What it demonstrates

**A provider is an ordinary HTTP server.** No framework, no SDK, ~150 lines of protocol. The
control leg is MCP over Streamable HTTP at `/mcp` — `initialize`, `tools/list`, `tools/call`,
and a `DELETE` that ends the session. That is the whole of what a tool server owes a client.

**Some resources need a process of their own.** `/dev/ttyACM0` cannot speak MCP, and the OS
hands out exactly one file descriptor for it. Something has to own that claim. Putting it
inside the agent runtime gives the runtime a device claim it has no business holding; putting
it inside each session makes two sessions race for the same fd and get `EBUSY` instead of
"session A has it". So it is a separate process, and arbitration is a first-class part of the
protocol: `acquire_device` names the current holder when it refuses.

**Tools are not always enough.** A device is a stream, and a request/response tool call cannot
carry one. So there are two legs:

| leg | transport | carries |
|---|---|---|
| control | MCP over HTTP at `/mcp` | list, acquire, configure, release |
| data | WebSocket at `/attach/<token>` | the bytes, both ways |

joined **one way** by the attach ticket: `acquire_device` answers with a url and the client
opens it. Nothing flows back, so a terminal closing does not disturb the control leg, and the
control leg dropping does not close a terminal already streaming.

**Say it twice, to two audiences.** The url is in the prose, because a model reads prose. It
is also in the result's `_meta`, because a *client* is what dials it:

```json
"_meta": {
  "dev.yession/stream": {
    "url": "ws://127.0.0.1:7333/attach/8f2c…",
    "label": "QinHeng CH340 /dev/ttyACM0",
    "renewable": true
  }
}
```

That object is the whole of what a provider implements to have its stream become a terminal
somebody can watch and type into. `_meta` rather than `structuredContent` because the ticket
is for the client and not the model; capabilities left out entirely because the default is
"bytes and nothing else", which is what a serial line honestly is; `renewable` because
acquiring a device you already hold mints a fresh token once the last stream has ended — so
"ask again" really does get you another one. A client that has never heard of the key ignores
it and still gets the prose. The contract behind the url is
[docs/streams.md](../../docs/streams.md).

**A claim needs a lifetime.** MCP sessions are the lifetime: a claim dies with the session
that took it, which is what stops a crashed client holding hardware forever. That is why this
server is stateful — `Mcp.OnSessionEnded` is where the device goes back.

**Degrade, don't fail.** `serialport` is a native addon and it is a *lazy dynamic import*, so
a machine without it answers "no devices" rather than refusing to start. The engine is a seam
(`Ports.SerialEngine`), which is also what lets every test run with no hardware at all.

**Offer less than you can see.** `list_devices` returns only devices in a known vendor/product
table. An unrecognised port is dropped rather than offered under its raw path, because
`/dev/ttyS0` is a serial port and on a lot of machines it is the system console. Handing an
agent every tty on the box means handing it the console.

## Running it

```bash
node dist/main.js
```

```
serial-provider 0.1.0: MCP at http://127.0.0.1:7333/mcp, streams at ws://127.0.0.1:7333/attach/<token>
```

Configured entirely by the environment:

| variable | default | |
|---|---|---|
| `YESSION_SERIAL_PORT` | `7333` | `0` binds an OS-assigned port |
| `YESSION_SERIAL_HOST` | `127.0.0.1` | |
| `YESSION_SERIAL_ORIGIN` | the bound address | the `ws://` origin clients should attach to, when the provider sits behind something |

**Loopback is the only deployment this is honest about.** The control leg is unauthenticated,
so binding it to anything but loopback hands the host's serial ports to the network.

To use it from Yession: declare `http://127.0.0.1:7333/mcp` in the management UI's MCP form.
That is the same form any third party's server is declared in — there is no serial-specific
path through the product, which is the point.

## Building it

From the repository root, where the build interface lives:

```bash
dotnet fsi tasks.fsx example serial
```

That compiles the F# to JavaScript with Fable and bundles it to `dist/main.js` with esbuild,
keeping `serialport` external so its native addon resolves at run time.

## Adding hardware it does not know

`Discovery.known` in [src/Serial.fs](src/Serial.fs) is a table of USB vendor/product pairs.
Hardware that is not in it is invisible rather than mislabelled, and adding it is one line:

```fsharp
entry "1a86" "7523" "QinHeng" "CH340"
```

Find the pair with `ioreg -p IOUSB -l` on macOS, or `lsusb` on Linux.

## The files

| | |
|---|---|
| [src/Serial.fs](src/Serial.fs) | the vocabulary: devices, ids, line settings, the tool wire. No IO. |
| [src/Interop.fs](src/Interop.fs) | the Node surface. The only file with raw JavaScript in it — port this one, keep the rest. |
| [src/Mcp.fs](src/Mcp.fs) | MCP over Streamable HTTP, longhand. |
| [src/Ws.fs](src/Ws.fs) | RFC 6455, only the frames the attach protocol needs. |
| [src/Ports.fs](src/Ports.fs) | the `serialport` seam: enumerate, open, stream, close. |
| [src/Provider.fs](src/Provider.fs) | the four tools, the claims, and the two legs joined. |
| [src/Main.fs](src/Main.fs) | argument handling, and one line of output naming both legs. |

Tests live with the rest of the suite in `tests/Yession.Tests` — the pure ones in the cheap
tier, and a `Serial`-tagged suite that drives a real pty through `socat`.
