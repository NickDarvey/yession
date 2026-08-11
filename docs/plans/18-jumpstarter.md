# Plan 18 — Jumpstarter, as a provider in somebody else's language

[Jumpstarter](https://jumpstarter.dev) is hardware-in-the-loop testing as a service: an
**exporter** owns the devices attached to one host and serves them over gRPC; a **client**
takes a lease and drives them through typed drivers (power, serial, storage, …); a
**controller** (Kubernetes) schedules leases across many exporters. We want a session's agent
to be able to power-cycle a board and talk to its console — and we want the shape of that
integration to be the second proof that a provider is not a plugin.

[Plan 16](16-serial-devices.md) part E made the first one: `examples/serial`, an ordinary MCP
server that happens to own some ttys. It is written in this repository's language, longhand,
and it demonstrates the protocol. This plan is the other half of the same claim — a provider
written in **Python**, riding **somebody else's SDK**, owning a **service** rather than a
device. If both are declarable by url in the management UI and neither is named anywhere in
the product, the extensibility story is not a story.

HDMI and video are out of scope, deliberately: see Later.

## What jumpstarter already is, and what it is not

Verified against jumpstarter 0.8.1 on macOS, because a plan built on the documentation's
promises would be a plan about a different program:

- **Every package is on PyPI and installs on macOS**, Python ≥ 3.11. No container needed.
- **A controller is not needed.** `jmp run --exporter-config <yaml> --tls-grpc-listener
  127.0.0.1:8815 --tls-grpc-insecure` runs an exporter in *direct mode*, serving gRPC on
  loopback with no Kubernetes anywhere. A client reaches it with three environment variables
  (`JUMPSTARTER_HOST`, `JMP_GRPC_INSECURE`, `JMP_DRIVERS_ALLOW`) and `jumpstarter.utils.env`.
- **`jmp mcp serve` exists and does not fit.** It is stdio-only — which
  [Plan 17](17-mcp-server-configuration.md) has not built a transport for — and it resolves
  a *controller-backed* client config (endpoint + token), so it cannot address a direct
  exporter at all. Both halves would have to change upstream. So the bridge is ours to write,
  which turns out to be the more interesting thing anyway.
- **Bytes do not ride tool calls.** The pyserial driver exports `set_dtr`/`set_rts` as unary
  calls and the console itself as a *stream*. That is the same split Plan 16 opened with
  (`Control and data are different problems`), arrived at independently by another project.

## Why this is an example, and why it is Python

`examples/README.md` sets the bar: *built the way somebody outside this repository would build
them*. Somebody outside this repository integrating jumpstarter would use the jumpstarter
Python SDK, because it is the only supported client. The alternative is reimplementing
`ExporterService` **and** the router's bidirectional stream RPC in F# over Node's gRPC — a
protocol reimplementation that rots against every upstream release, in exchange for
demonstrating hand-rolled protocol, which `examples/serial` already demonstrates.

So: `examples/jumpstarter`, a uv project, in Python. The two examples then say different
things, and the pair says the thing we actually want said:

| | serial | jumpstarter |
|---|---|---|
| owns | a **resource** the OS hands out once | a **service** that owns resources |
| written in | this repository's language, longhand | Python, on a foreign SDK |
| arbitration | per device | one claim over the exporter |
| bytes | its own WebSocket data leg | the SDK's stream, drained by tools |

A provider contract that both of those satisfy is a contract, not an interface.

## One claim over the whole exporter

`examples/serial` claims per device, because two ttys are two independent resources. An
exporter is not like that: powering the DUT off invalidates the console session, so a claim
per driver would be a promise of isolation that the hardware does not keep. And direct mode
has no arbitration of its own — jumpstarter's leases live in the controller, which is not
here.

So the provider claims the **exporter**, once:

- The holder is the **MCP session id**, exactly as in serial. A claim dies with the session
  that took it, which is what stops a crashed client holding hardware forever.
- A refusal **names the holder** — `held by <session>`, not `unavailable`. A bare refusal
  reads as a hardware fault, and the agent's next move should be to wait, not to debug.
- Everything that touches hardware requires the claim. `status` does not, because "who has
  it" is the question a caller asks *before* acquiring.

If a controller ever appears, this claim is REPLACED by a jumpstarter lease — not layered
over one. Two locks over one exporter is two answers to "who has it".

## The tools

Eight, and the descriptions are written for the model, per Plan 16 part A:

| tool | claim | |
|---|---|---|
| `status` | — | is the exporter reachable, what drivers does it export (its report tree), and who holds it |
| `acquire` | takes | claims the exporter for this MCP session |
| `release` | holds | frees it, closing any open console stream |
| `power` | holds | `on` / `off` / `cycle` — the canonical HIL move, worth a tool of its own |
| `driver_call` | holds | a unary call to any driver in the tree: `driver`, `method`, JSON `args` |
| `serial_send` | holds | write to the console |
| `serial_read` | holds | drain what it said |
| `serial_expect` | holds | wait for a pattern; on timeout, say what WAS seen |

`driver_call` is what keeps this honest about exporters we have never seen: an exporter
exports whatever its config says, and a tool per driver type would be a table this repository
has to keep in step with upstream's fifty driver packages. `power` and the three serial verbs
exist beside it because they are what an agent reaches for, and because a description that
says "power-cycles the board" is worth more to a model than one that says "calls a method".

## Two legs, and why this one has one and a half

Serial's data leg is a WebSocket at `/attach/<token>`, joined to the control leg one way by a
ticket. This provider does **not** serve one, and the reason is written in `docs/GAPS.md`:
nothing in production turns an attach ticket into a terminal. `TerminalSource.Attached` is
constructed only by tests. Shipping a second transport that no client opens would be
machinery with no consumer — the thing this repository keeps deleting.

Instead the console is drained through tools: the provider holds the SDK's stream open for the
life of the claim, and `serial_send` / `serial_read` / `serial_expect` are what an agent can
actually use today. `serial_expect` is the interesting one — a request/response protocol
cannot stream, but *waiting for a pattern* is a request/response question, and it is what
console interaction mostly is.

When the session-side glue lands, the data leg goes in here too, wire-compatible with serial's
(binary frames are bytes, text frames are `{"type":"resize"|"kill"}`, and the stream
terminates with `{"type":"exited","code":N}` before it closes). Reserved, not built.

## Trust

The same posture as serial, and one more thing to say:

- **The control leg is unauthenticated**, so loopback is the only deployment this is honest
  about. Bound wider, it hands the host's attached hardware to the network.
- **The gRPC leg is unauthenticated too**, and it is a second address on the same host:
  `--tls-grpc-insecure` with no passphrase. The provider's claim arbitrates the provider's
  clients; it cannot stop another local process dialing the exporter directly. One host, one
  operator, loopback — acceptable here and recorded in GAPS.
- **`JMP_DRIVERS_ALLOW=UNSAFE`** lets the SDK import whatever driver client the exporter's
  report names. That is the trust already implied by running the exporter, which is a
  process on the same box configured by the same person.
- **A provider is unconfined.** Unchanged from Plan 17's Trust section — a declared MCP server
  is not sandboxed, and this one holds hardware.

## Delivery

1. **This document.**
2. **`examples/jumpstarter`** — the provider, its pytest suite, and the repository wiring: a
   `Jumpstarter` capability probed by running it, an `example` verb that knows a uv project
   when it sees one, and an interop suite that drives Yession's *own* `McpClient` at the
   provider. That last one is the point of doing it in-repo at all: a client exercised only
   against its own server proves the two agreed with each other.
3. **Deployment** (outside this repository): two launchd agents in the operator's home
   configuration — the exporter and the provider — both on loopback, the provider declared
   in the management UI like anyone else's server.

No version marker on any of them. Examples are not the product: `stage` never sees them, the
npm package does not carry them, the Nix installable does not wrap them. Nothing user-facing
moves.

## Later, deliberately not now

- **HDMI and video.** Jumpstarter's video drivers exist, and a frame is not a thing a tool
  call carries. It needs the data leg, and then it needs something on the other end that can
  look at a picture — a different plan, not a bigger version of this one.
- **The `/attach/<token>` data leg**, until a session can turn a ticket into a terminal.
- **`LogStream` and streaming driver calls.** The exporter narrates its hooks; a session's
  timeline is where that would belong, and nothing carries it there yet.
- **A controller.** Distributed mode is what jumpstarter is *for*, and it is also where its
  own lease system takes over the arbitration this provider does by hand. Worth wanting when
  there is a second exporter.
