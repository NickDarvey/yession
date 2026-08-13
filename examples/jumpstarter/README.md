# A Jumpstarter provider

An MCP server that lends a [Jumpstarter](https://jumpstarter.dev) exporter — and the hardware
behind it — to an agent. Point any MCP client at it and the agent gets eight tools: power the
board, talk to its console, and call anything else the exporter exports.

It is here as the counterpart to [serial](../serial/): that one is written in this
repository's language, longhand, and owns a device. This one is written in **Python** on
**somebody else's SDK**, and owns a **service**. Between them they make the claim the
`examples/` directory exists to make — a provider is not a plugin, it is a server, and it can
be written in whatever the thing you are integrating is written in.

## What it demonstrates

**A provider does not have to be in the host's language.** Yession is F# on Node; jumpstarter
is Python. Nothing in either knows about the other, because the seam between them is HTTP and
a JSON schema. Reimplementing jumpstarter's gRPC in F# would have been a protocol
reimplementation to maintain forever; using its own SDK is ~600 lines and upstream keeps it
working.

**Arbitration is yours when the service does not do it.** Jumpstarter arbitrates with
*leases*, and leases live in its Kubernetes controller. A direct-mode exporter has none, so
this server claims it: one holder, named in refusals, released when its MCP session goes
quiet. See [Who holds it](#who-holds-it).

**A blocking SDK, on a server that cannot block.** The jumpstarter client is a synchronous
context manager, and so is the serial console inside it; MCP is an event loop answering
requests. So the SDK lives on a thread of its own with a command queue
([src/jumpstarter_provider/exporter.py](src/jumpstarter_provider/exporter.py)), entered once
and exited once, and the tools hand it work. That is the whole trick, and it is the part
worth stealing for any provider wrapping a library that was not written for a server.

**Introspection has to be honest, or it sends the caller somewhere worse.** A driver client
carries thirty names and six of them are the driver's; the rest is the SDK's plumbing. So one
function ([src/jumpstarter_provider/introspect.py](src/jumpstarter_provider/introspect.py))
answers *what does this offer* for both the listing and every refusal, and it answers from the
CLASS — because `getattr(client, "status")` runs the getter, and `status` is a property. A
refusal that says only "no such method" and then lists names the listing hides is how a caller
ends up invoking `get_status_async` and being handed an un-awaited coroutine. Streaming methods
are drained to their first few items rather than described as `<generator object …>`, which is
what makes `driver_call power read` the answer to "what is the board doing".

**Bytes do not ride tool calls, so they get a leg of their own.** The console is a stream, and
there are two ways to reach it here — both of them useful, and the difference between them is
who is asking:

| leg | transport | carries |
|---|---|---|
| control | MCP over HTTP at `/mcp` | the eight tools |
| data | WebSocket at `/attach/<token>` | the console's bytes, both ways |

joined one way by a ticket, which `acquire` hands back in the result's `_meta`:

```json
"_meta": {
  "dev.yession/stream": {
    "url": "ws://127.0.0.1:7334/attach/8f2c…",
    "label": "serial console",
    "renewable": true
  }
}
```

That object is the whole of what a provider implements to have its console become a terminal
a person can watch and type into. It is in `_meta` rather than in the content because a ticket
is for the *client*, not for the model — a client that has never heard of the key ignores it,
still gets the prose, and still has the three console tools.

`serial_expect` remains the tool that matters on the control leg, with or without a stream:
request/response cannot stream, but *waiting for a prompt* is a request/response question, and
that is what console interaction mostly is.

**One drain, two readers; one writer.** This is the part worth stealing. The SDK's console is
a `pexpect` spawn, so whoever reads it consumes it: a drain loop feeding a socket would starve
`serial_read`/`serial_expect` of the very bytes they exist to return, and the agent would go
blind the moment a person opened the terminal. So while a stream is attached, one drain owns
the console and everything else reads what it has already read.

Writes go the other way — from two doors down to one. While a terminal is attached,
`serial_send` refuses and says where to type instead, because two writers on one console is
exactly what a terminal's write lease exists to arbitrate.

## The tools

| tool | needs the claim | |
|---|---|---|
| `status` | no | can the exporter be reached, what does it export, and who holds it |
| `acquire` | takes it | claim the exporter |
| `release` | yes | give it back, closing the console |
| `power` | yes | `on`, `off`, or `cycle` |
| `driver_call` | yes | any method on any driver in the tree — the general case. Call it with no method to be told what that driver offers |
| `serial_send` | yes | write to the console (refused while a terminal is attached — type there) |
| `serial_read` | yes | everything it has said, up to a pause |
| `serial_expect` | yes | wait for a pattern; on a timeout, say what WAS seen |

Every answer is prose, because a model is what reads it. A refusal names the holder: "in use,
not broken" is a different instruction from "unavailable".

## Who holds it

One claim over the **whole exporter**, not one per driver — powering the board off cuts the
console out from under anybody else, so per-driver claims would promise an isolation the
hardware does not keep.

The holder is the **MCP session id**, and the claim follows a live session: every request
under that id keeps it (including the `tools/list` a client polls with), and a client that
goes quiet for `JUMPSTARTER_PROVIDER_TTL` loses it. One rule, and it covers both a client
that says goodbye and a client that is killed — which is the case a goodbye cannot cover.

## Running it

The exporter first, in direct mode — no Kubernetes:

```bash
jmp run --exporter-config exporter.yaml --tls-grpc-listener 127.0.0.1:8815 --tls-grpc-insecure
```

Then the provider:

```bash
uv run --project examples/jumpstarter jumpstarter-provider
```

```
jumpstarter-provider 0.1.0: MCP at http://127.0.0.1:7334/mcp, exporter at 127.0.0.1:8815
```

Configured entirely by the environment:

| variable | default | |
|---|---|---|
| `JUMPSTARTER_PROVIDER_PORT` | `7334` | `0` binds an OS-assigned port |
| `JUMPSTARTER_PROVIDER_HOST` | `127.0.0.1` | |
| `JUMPSTARTER_HOST` | `127.0.0.1:8815` | the exporter's gRPC address |
| `JUMPSTARTER_PROVIDER_CONSOLE` | `serial` | which exported driver is the console |
| `JUMPSTARTER_PROVIDER_TTL` | `300` | seconds of client silence that release a claim |
| `JUMPSTARTER_PROVIDER_ORIGIN` | the bound address | the `ws://` origin clients should attach to, when the provider sits behind something |

**Loopback is the only deployment this is honest about**, twice over: the MCP leg is
unauthenticated, and the exporter's gRPC leg runs insecure with no passphrase. Either one
bound wider hands the hardware to the network.

To use it from Yession: declare `http://127.0.0.1:7334/mcp` in the management UI's MCP form,
which is the same form any third party's server is declared in.

## An exporter to point it at

[tests/exporter.yaml](tests/exporter.yaml) is the no-hardware one the suite runs against —
upstream's `MockPower` and a pyserial `loop://` line that echoes what you write. Real hardware
is the same file with a device in it:

```yaml
export:
  power:
    type: jumpstarter_driver_power.driver.MockPower
  serial:
    type: jumpstarter_driver_pyserial.driver.PySerial
    config:
      url: "/dev/ttyUSB0"
```

Everything upstream exports — [fifty-odd driver
packages](https://jumpstarter.dev/main/reference/package-apis/index.html) — works through
`driver_call` without this provider knowing they exist.

## Testing it

```bash
dotnet fsi tasks.fsx example jumpstarter
```

That syncs the locked environment, runs the pytest suite against a REAL exporter (spawned per
session, no hardware), and boots the provider to prove it starts.

The suite is deliberately two-layered, and the second layer is not here:

| where | what only it can prove |
|---|---|
| [tests/](tests/) | the claim, the tools, the console — driven by a hand-written HTTP client, because a client from the same SDK as the server agrees with it by construction |
| `tests/Yession.Tests/Jumpstarter.fs` | that **Yession's own MCP client** can drive this server. Two implementations that never checked against each other agreeing is the only evidence either read the protocol right. Runs under the `Jumpstarter` capability |

## The files

| | |
|---|---|
| [src/jumpstarter_provider/main.py](src/jumpstarter_provider/main.py) | argument handling, the environment, and one line of output naming both ends |
| [src/jumpstarter_provider/provider.py](src/jumpstarter_provider/provider.py) | the eight tools, the claim, and the liveness that ends one |
| [src/jumpstarter_provider/exporter.py](src/jumpstarter_provider/exporter.py) | the SDK, behind one seam and on one thread |
| [src/jumpstarter_provider/stream.py](src/jumpstarter_provider/stream.py) | the data leg: one drain, two readers, one writer |
