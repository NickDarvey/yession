# Plan 19 — A stream a provider offers, a terminal a person can use

> **Status: shipped**, all five steps, one PR each. It closed the join
> [Plan 16](16-serial-devices.md) built one half of and [Plan 18](18-jumpstarter.md) declined
> to build the other half of: `TerminalSource.Attached` was constructed only by tests, the
> serial provider served a WebSocket nothing opened, and the jumpstarter provider drained a
> console through tool calls because there was no client for a second leg. All three had one
> cause — **a session could not learn a stream's address.**
>
> The plan is the contract that lets it, and it is a contract rather than a case. Nothing in
> the product learns what serial is, or what jumpstarter is, at build time or at any other
> time. A provider says "here is a stream, and here is what it can do"; a terminal is what
> Yession makes of it. Both examples are now ordinary consumers of a thing any third party can
> implement in an afternoon.
>
> Four things landed differently from the text below, and each is called out where it happens:
>
> - **`TerminalSource.Attached` carries the whole OFFER, not its ticket**, and `TerminalOpened`
>   records renewability — step 4 needed the answer to "is there a way back" at the worst
>   moment to go looking for it: the stream has ended and the terminal is closed.
> - **The decorator is `ToolStreams.create` + `Decorate`**, not one `offering` function: the
>   url→terminal map is the SESSION's (a provider polled across two turns offers one stream)
>   while the ceiling is a TURN's, and one function cannot hold both scopes.
> - **`read_terminal` was not in the plan and is now half the story.** `write_terminal` gave
>   the agent a hand and no eyes, which was tolerable only for a provider that kept its own
>   read tools. Serial has none, so the agent could type at a board and never learn what it
>   said. It lives beside `Write` in the terminal manager, takes no lease, and is refused
>   where blocks already answer.
> - **A terminal's SOURCE outlives it.** Closing used to forget it, so a closed device read
>   back the default — "this runs commands as blocks" — which is a lie about a recording that
>   is right there. The source is now kept, because the question is asked of the recording.

## The extension point is one sentence

**A tool result may offer a byte stream, and a byte stream becomes a terminal.**

That is the whole of what the product knows. It does not know which tool offers one, or what
the thing on the other end is, or that a device is involved. Serial and jumpstarter are not
mentioned in any file under `src/` or `app/` after this lands, exactly as they are not
mentioned before it.

Three seams already exist and are not redesigned here:

| | | |
|---|---|---|
| the terminal over foreign bytes | `TerminalSource.Attached` | [`TerminalSource.fs:69`](../../src/Yession.Domain/TerminalSource.fs) |
| what a source can do, declared | `SourceCapabilities` | [`TerminalSource.fs:21`](../../src/Yession.Domain/TerminalSource.fs) |
| the WebSocket client | `AttachWs.attach`, wired at [`Host.fs:316`](../../app/Host.fs) | [`AttachWs.fs:108`](../../app/AttachWs.fs) |

What is missing is only the **address**, and this plan is about how one travels.

## Why the interface is a stream, and not a pty

A pty is a byte stream plus a size, plus signals, plus an exit code. A serial line is a byte
stream and none of those. A remote shell is a byte stream and all of them. Modelling the
interface as "a pty, degraded" would make every source lie about three things to be allowed to
carry bytes; modelling it as "bytes, and say what else you have" makes the serial line the
honest minimum and the remote pty the honest maximum, with no case in between needing a new
type.

`SourceCapabilities` already draws exactly that line and already decides what the terminal
does with it — `CanInstrument = false` means live-only, no OSC 133 bootstrap typed at
somebody's wire, no blocks, no exit codes. So a provider that genuinely has a pty on another
host declares `instrument`, `resize` and `exitCode`, and gets Plan 13's entire apparatus —
blocks, approval policy, exit codes, the digest the agent reads — **with no code written
here**. That generalisation is worth more than either example, and it is already paid for.

## Where the offer rides: `_meta`, once

MCP reserves `_meta` on a result for implementation-specific data, keyed by a reverse-DNS
prefix. The offer goes there, under `dev.yession/stream`:

```json
{
  "content": [ { "type": "text", "text": "ttyACM0 is yours. 115200 8N1." } ],
  "isError": false,
  "_meta": {
    "dev.yession/stream": {
      "url": "ws://127.0.0.1:7333/attach/8f2c9a…",
      "label": "USB serial /dev/ttyACM0",
      "capabilities": { "instrument": false, "resize": false, "exitCode": false },
      "renewable": true
    }
  }
}
```

**Not `structuredContent`**, which was the obvious candidate and is wrong twice. It is
validated against the tool's `outputSchema`, so putting our key there forces every provider to
publish a Yession extension inside its own declared contract — the opposite of an integration
that costs an afternoon. And `structuredContent` is *for the model*: it lands in the context,
which means the url lands in the context, which means a prompt injection has a socket address
to work with. The offer is client metadata. It has no business being read by anything that can
be talked into repeating it.

**One location, and no fallback.** A provider whose SDK cannot emit result `_meta` does not get
a second sanctioned place to put the offer — it gets a bug report against its SDK. Two
locations would be the belt-and-braces `CLAUDE.md` forbids: a redundant spare hides which path
is live and rots unverified.

Every field but `url` is optional, and the defaults are the conservative reading:

| field | absent means |
|---|---|
| `url` | there is no offer — the whole `_meta` entry is ignored |
| `label` | the tool's namespace and name, so a panel always has a title |
| `capabilities` | `SourceCapabilities.byteStream` — the least a source can be |
| any one capability flag | `false` |
| `renewable` | `false`, and see [Getting one back](#getting-one-back) |

So the smallest conforming provider adds one JSON object with one string in it to a tool it
already has. That is the bar this plan is trying to hit.

**It is our extension and it says so.** The reverse-DNS prefix is not decoration: if MCP grows
a standard streaming affordance, the decoder learns the standard one, both are read for a
release, and `dev.yession/stream` is deleted. A bare key like `stream` would make that a flag
day.

## The wire does not change

Plan 16 part D settled it, `AttachWs.attach` implements it, and a hand-written RFC 6455 peer
tests it:

- **binary frames are data**, both directions, no framing of ours;
- **text frames are control**: `{"type":"resize","cols":N,"rows":M}`, `{"type":"kill"}`;
- **termination is a frame, then a close**: `{"type":"exited","code":N}`, because an abnormal
  closure carries nothing and that path exists anyway.

This plan adds no wire. It adds the way a session learns a url — which is the only reason two
providers implementing that wire have so far had nobody to talk to.

## From offer to terminal

`app/Host.fs:492` is where a turn's foreign registries are snapshotted:

```fsharp
ForeignTools = mcpConnections.Registries ()
```

One decorator goes there, beside the audit seam that is already there:

```fsharp
/// Turn a stream a provider offers into a terminal people can see (Plan 19).
///
/// A DECORATOR over the registry rather than a branch inside the MCP client, for the reason
/// `ToolUseLog` is one: every call goes through `InvokeTool`, in-process and proxied alike,
/// so a seam here cannot be bypassed by a tool that arrives later.
val offering :
    origin: string ->                                         // the server's declared url
    openTerminal: (AttachTicket -> Async<Result<TerminalId, string>>) ->
    ToolRegistry -> ToolRegistry
```

**Automatic on offer, and no `attach_stream` tool.** An offer is a provider's answer to a call
somebody made on purpose; a second step in which the agent then asks for the thing it was just
handed is the selection layer Plan 17 deleted twice — "a step that only ever gets performed".
A provider that does not want a terminal opened simply does not offer one; that is the knob,
and it is on the side that knows.

Four rules make automatic safe:

- **One terminal per url.** A `status` tool that reports the live stream on every poll offers
  the same url every time and opens one terminal, not forty.
- **A small ceiling per turn.** A chatty turn cannot fill the pane; calls past it are answered
  with a line the model reads and no terminal.
- **The offer is admitted before it is opened** — see [Trust](#trust).
- **The agent is told, in the answer.** The decorator appends one sentence to the tool's own
  text: `Opened as terminal <id> — people in this session can see it and type into it.` The
  provider's prose is untouched, and the one fact only the session knows is added by the only
  component that knows it.

`openTerminal` is `terminals.Open Agent (Attached ticket) label` — the existing path, which
already declines to start a WorkSandbox for an attached source
([`Terminals.fs:1143`](../../src/Yession.SessionProcess/Terminals.fs)).

## The human's hand, and the second door

This is the part that decides whether "so humans can interact with it too" is true or merely
rendered.

Once the terminal exists, a person gets the whole of Plan 13 and 14 for free: the panel, the
live screen (Plan 14 stage 6 — `Style.fs:815`, `Dom.fs:205`), the transcript, scrollback,
replay in chat, and the **write lease** — take it, type, steal it back from whoever has it,
all on the record. That is the human story, and it needs no new surface. A live-only source
leans on that stage harder than a shell does, because with no blocks the live screen is the
*only* rendering the panel has: the first thing to check when step 1 lands is that an
attached terminal with no instrumentation looks like something rather than like an empty
block list.

The problem is the agent's hand. In a live-only terminal there are no blocks, so
`execute_command` — the agent's one execution path — has nothing to do there. Meanwhile the
provider's own tools (`serial_send`, jumpstarter's console verbs) write the same device
through a leg the lease cannot see. That is a **second door**, and Plan 16 part B already
settled the principle it violates: *the gate only becomes real when there is one door.*

Reads are fine — a second reader is not a race, and `serial_expect` is genuinely the right
shape for "wait for a prompt". **Writes are the problem**, and closing it takes one small
tool:

```
write_terminal(terminal_id, data)
```

Available only for a terminal whose source declares no instrumentation (an instrumented one
has `execute_command`, which is better in every way), and it **takes the lease exactly as a
peer does** — so the agent appears in the same holder field a person appears in, a person can
steal it back mid-sentence, and every byte the agent typed is in the same transcript.

That reverses a recorded policy, and deliberately: *"agent commands cannot hold a live-mode
lease — a policy decision, not a mechanism gap: leases are human-only until there is a reason
to change that"* ([GAPS](../GAPS.md), Terminals). A source with no blocks is that reason. The
policy exists so the agent cannot type into a session someone else is driving, and taking the
lease — visibly, stealably — is how it keeps meaning that here.

With that in place a provider *may* refuse its own console-write tool while a stream is
attached — "the console is attached to a terminal; type into it there". That is the provider's
choice, expressed in its own prose, and the product still knows nothing about consoles. The
plan recommends it for both examples and does not require it of anyone.

## Getting one back

Serial's attach token is spent on use ([`Provider.fs:266`](../../examples/serial/src/Provider.fs)),
which is right — a replayable token hands a second client the stream a claim says is
exclusive. So a stream that ends cannot be resumed; Plan 16 already recorded that "reconnect
is a new terminal, not a resumed one".

But a person who closed a panel, or whose device stream dropped, should not have to ask the
agent to say the magic word again. So `renewable` on the offer means one specific thing:
**making the same call again, with the same arguments, is how you get another stream, and
making it again is safe.**

When it is set, the session records the `(namespace, name, arguments)` that produced the offer
alongside the terminal, and a peer command replays it:

```fsharp
/// Ask the provider for this terminal's stream again (Plan 19). Rejected when the terminal's
/// offer was not `renewable`, or the server is gone.
| ReattachTerminal of TerminalId
```

The peer names a **terminal**, never a url — which keeps the invariant `Commands.fs:42` was
written to hold ("a peer command carrying a URL would be a peer choosing what this session
connects to"), while the *tool* being replayed is one the session learned at runtime from a
server it was declared. Default `false`, because replaying an unknown call could power-cycle
somebody's board.

## A human who wants a stream first

Out of scope, and named rather than discovered: today a person who wants a device terminal
before the agent has opened one asks the agent for it. Yession cannot offer a button, because
the tool that mints an offer has a name and an argument schema it learns at runtime and the
product has no opinion about either.

The general answer is a human surface for invoking a declared tool — a form generated from
its JSON Schema, and an authorization story for a person calling a foreign tool directly.
That is a plan of its own and it is worth having; it is not this one, and this one does not
prejudge it.

## Trust

Everything Plan 16 and 17 recorded still applies (a provider is unconfined; tool descriptions
are untrusted text in the model's context; terminal access equals session access). Three
things are new:

- **A url the session dials.** Admitted only when it shares the **host** of the server's
  declared url and its scheme is `ws`/`wss`. Same host, any port — a provider serving its
  stream leg on a second port is ordinary; a provider pointing the session at another machine
  is an operator's declaration, not a tool result's. Refusal is a line in the answer, so the
  model finds out rather than the stream silently never opening.
- **Only a declared server can produce one.** The offer never enters the model's context, so
  no injected text can manufacture one; the authority to hand a session a socket is exactly
  the authority to be declared, which an operator already granted.
- **`label` is foreign text on a human surface.** It titles a panel. Text, never markup —
  which the existing surfaces already hold, and which the test says out loud.

## Test gating (`Tag.needs`)

**Cheap tier** — the contract, which is where nearly all of it belongs:

- decode: offer present / absent / partial; every capability default; a `_meta` with other
  keys in it; a malformed offer ignored rather than fatal;
- admission: a foreign host refused, a non-`ws` scheme refused, a second port allowed;
- one terminal per url; the per-turn ceiling; the appended sentence in the answer;
- `renewable = false` ⇒ `ReattachTerminal` rejected; `true` ⇒ replays the recorded call with
  the recorded arguments;
- `write_terminal` takes the lease, is refused on an instrumented source, and a peer can steal
  the lease back from the agent.

**`Ports`** — an echo provider that offers a stream from a tool result: real MCP call, real
WebSocket, bytes both ways through the transcript, `exited` ⇒ the terminal closes with a code,
abrupt close ⇒ closes with a reason.

**`Serial`** — the existing socat pty pair, end to end through the real example: acquire over
MCP, terminal appears, a person's keystrokes reach the tty, the tty's bytes reach the
transcript.

**`Jumpstarter`** — the python provider's new data leg, driven by the session's own client and
`AttachWs`. Two implementations that never checked against each other agreeing is the only
evidence either read the wire right, which is the same reason that suite exists at all.

## What the examples change

Both are example-only changes, and neither teaches the product anything.

**serial** — `acquire_device` already answers with the url in prose; it gains the same url in
`_meta`, with `renewable: true` (acquiring a device you already hold mints a fresh token, so
replay is exactly right). `Mcp.Invoke` returns `string` today, so the tool signature grows an
optional meta alongside the text. Its data leg is already built and already tested.

**jumpstarter** — grows the leg it deliberately did not build: a WebSocket on the same ASGI
app, wire-compatible with serial's, tee'ing the console stream it already holds open for the
life of the claim, so `serial_read` / `serial_expect` keep working for the agent while the
bytes also reach a terminal. `acquire` gains the `_meta`. One thing to verify before
committing to the step: whether the Python MCP SDK's tool registration exposes result `_meta`,
and if not, what the lower-level registration path costs.

No version marker on either — examples are not the product. The product steps are a new
user-facing capability and take `+semver: minor` on a commit body, once.

## Delivery

Independently shippable, in this order for the first one only:

1. **The offer, and the terminal it opens.** `StreamOffer` + its codec + admission in the
   domain; `McpCallResult` gains `Meta : string option` (raw JSON, decoded in one place, the
   way `InputSchema` is already carried); the `offering` decorator at `Host.fs:492`. Verified
   against an echo provider under `Ports` — no serial, no jumpstarter, no hardware. **After
   this step the product is finished**: any provider that offers a stream gets a terminal.
2. **Serial offers one.** The example change, plus the `Serial` E2E through real socat.
3. **`write_terminal`.** The agent's hand in a live-only terminal, under the lease. Independent
   of 2 and 4.
4. **Reattach.** `renewable`, the recorded call, `ReattachTerminal`, and the control on the
   panel.
5. **Jumpstarter's data leg.** The example change, plus the `Jumpstarter` E2E through the
   session's own client.

Steps 2 and 5 are the same shape done twice on purpose — a contract with one implementor is a
contract that fits its implementor.

## Risks & open questions

- **Two writers, until step 3.** Between steps 1 and 3 a device has a terminal under a lease
  *and* a provider tool that writes it regardless. That is a real gap and the reason step 3 is
  in this plan rather than a later one.
- **The tool-use chip does not point at the terminal it opened.** `ToolUseFinished` carries
  `Block : BlockId option` for exactly this reason in the block case. A `Terminal :
  TerminalId option` beside it is the obvious symmetry and is deliberately *not* smuggled into
  step 1 — it is an event field, and adding one should be a decision somebody made.
- **A provider's lifecycle is still nobody's** ([GAPS](../GAPS.md)). This plan makes a
  down provider more visible (a terminal that will not open) without making anybody
  responsible for starting one.
- **A live-only terminal's transcript has no natural bound.** A chatty device streams for as
  long as it is attached, and nothing here caps that. The existing transcript store's limits
  apply; whether they are the right ones for a device is unmeasured.
- **`renewable` is the provider's claim about its own idempotence,** and nothing verifies it.
  A provider that marks a destructive tool renewable makes a reattach button destructive. The
  default is `false` and the field is documented as a promise, which is the same standing
  `SourceCapabilities` already has.
- **Two sessions, one device.** Unchanged and not this plan's: the provider arbitrates and its
  refusal names the holder. Yession's lease arbitrates *within* a session, on top.

## Later, deliberately not now

- **A human surface for invoking a declared tool**, which is what would let a person open a
  device terminal without the agent.
- **Frames that are not bytes.** Jumpstarter exports video; a picture is not a terminal, and
  a second offer kind (`dev.yession/frames`?) is a different plan with a different consumer.
- **An offer from something that is not a tool result** — a server notification, a resource
  subscription. Real, and premature: nothing produces one, and the tool result is where both
  existing providers already know the answer.
- **`stdio` providers**, which change who owns the process rather than merely where it is
  (Plan 17 said the same about the transport, and it is still true).
