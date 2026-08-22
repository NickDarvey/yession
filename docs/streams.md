# Providing a terminal backend

A byte stream becomes a terminal in Yession — with a panel people can watch, a transcript
they can scroll back through, replay in chat, and a write lease that arbitrates who is
typing. None of that is yours to build. **You serve a WebSocket and put the bytes in binary
frames**; everything above is Yession's, and a source gets it whether it is a serial port, a
`pexpect` handle inside somebody else's SDK, or a shell on another machine.

This is the normative contract. The client that speaks it is
[`app/AttachWs.fs`](../app/AttachWs.fs); the smallest thing that conforms to it is the
hand-written peer in [`tests/Yession.Tests/Attach.fs`](../tests/Yession.Tests/Attach.fs),
which the suite runs on every `check Ports` — so it cannot rot while the client is tested
against it. Read that file if you would rather read code than prose.

## The wire

WebSocket, because a provider is already running an HTTP server for its MCP endpoint and the
upgrade rides the same port — and because terminal-over-WebSocket is the shape ttyd, gotty,
`kubectl exec` and xterm.js attach all use. The point of the seam is that somebody else can
implement the other end.

### MUST

1. **Accept a WebSocket upgrade** at a url somebody can reach.
2. **Binary frames are the bytes, both directions.** What the device says goes out as a
   binary frame; whatever arrives as a binary frame goes to the device. No framing of ours.
   Bytes are decoded as UTF-8.
3. **Close the socket when the stream ends.** The close is what ends the terminal — the
   frames below only say *why*. A provider that sends `exited` and holds the socket open
   leaves a terminal that nobody can reattach, because nothing has ended yet.

That is the whole of it. Everything below is quality of implementation.

### SHOULD

4. **Say why, before closing**: a text frame `{"type":"exited","code":N}`, then close.
   Without it the terminal still closes, and the person reads "the stream closed without
   saying why" instead of something they can act on.
5. **Not raise when the client vanishes mid-write.** A browser tab closes, a session ends;
   an ordinary goodbye should not become an error in your logs.

### MAY

6. `{"type":"resize","cols":N,"rows":M}` arrives only if you declared `resize` (see
   [capabilities](#capabilities)). A source that declares nothing is never sent one — ever.
   A serial line has no size, and being told it is 80x24 is a fact nobody could check.
7. `{"type":"kill"}` asks you to end the stream early. You may ignore it: the client closes
   on a 2s deadline regardless. The frame is the request; the deadline is what happens when
   nobody answers it.
8. `{"type":"failed","reason":"…"}` — the stream is ending and not because the device
   finished. The reason reaches the person, verbatim, as the terminal's closing reason. It
   takes precedence over `exited` if you send both.
9. **Ignore any text frame you do not recognise.** New control types may appear; an
   implementation that treats an unknown one as an error will break on the next.

### How an ending is read

| what the client saw | how the terminal closes |
|---|---|
| `exited` then close | "the stream ended with code N", or "the stream ended" if you declared no exit code |
| `failed` then close | your reason, unchanged |
| close, no frame | "the stream closed without saying why" |

Only a source that declared `exitCode` is described as having exited with one — saying
"exit 0" about a serial line that merely went quiet would invent the fact `exitCode: false`
exists to deny.

### One thing that will bite you

**Text frames are control, binary frames are data.** Reaching for your framework's
`send_text` to emit device output produces a terminal that shows nothing, because the client
tries to parse it as control JSON and drops it. The client logs this once per connection so
it is findable rather than silent, but it will not appear as output.

## Capabilities

A stream is bytes; anything else is something you *claim*, and claiming nothing is the
honest default.

| flag | default | what it buys |
|---|---|---|
| `instrument` | `false` | the OSC 133 bootstrap can be typed in, so output resolves into command blocks with exit codes |
| `resize` | `false` | you will be sent sizes |
| `exitCode` | `false` | your `exited` code is reported as one |

A serial line is the honest minimum: all three false. A genuine remote pty is the honest
maximum: all three true — and it inherits blocks, exit codes and the agent's command digest
with no code written in Yession. Modelling the interface as "a pty, degraded" would have made
every source lie about three things in order to carry bytes.

## Being offered to a session

The wire above is the whole of what a terminal backend does. Yession learns your url one way:
an MCP tool result carries it in `_meta`.

```json
"_meta": {
  "dev.yession/stream": {
    "url": "ws://127.0.0.1:7334/attach/8f2c…",
    "label": "serial console",
    "renewable": true,
    "capabilities": { "resize": true }
  }
}
```

Only `url` is required. Malformed `_meta` is no offer, never a failed call.

In `_meta` rather than in the content because a ticket is for the **client**, not for the
model: a url in `structuredContent` lands in the model's context, which hands a prompt
injection a socket address to work with. A client that has never heard of the key ignores it
and still gets your prose.

### Admission

A session refuses a url that is not:

- `ws://` or `wss://`,
- on the **same host** as the MCP server the operator declared you at (any port), and
- free of credentials (no `user:pass@`).

The refusal is appended to the answer the model reads, naming both hosts. This is a policy
rather than a boundary — it stops a tool result pointing a session at another machine; it
does not stop a provider from handing out a socket to something else on the box it already
runs on.

### `renewable`

`true` means: **making the same call again, with the same arguments, is how you get another
stream, and making it again is safe.** It is a claim about your own idempotence, and nothing
verifies it. Default `false`.

It is what lights up the Reattach control on a closed terminal. A reattach is a new terminal,
never a resumed one — a person names a *terminal*, never a url.

### If your resource is exclusive

One stream, one reader. If yours is a device rather than something reproducible:

- mint a **single-use** token and spend it on attach;
- **suppress the offer entirely** while a stream is live, or you hand out a second reader of
  a thing that has one;
- **refuse a replayed token in band** — accept the upgrade, send `{"type":"exited","code":1}`,
  then close. A close code carries nothing a client can act on;
- mint a fresh token on the next acquire, or `renewable: true` is a lie.

Yession's write lease arbitrates who types *within* one session. Your claim, if you have one,
arbitrates between clients. They are different locks and both are needed.

### Your own console tools

If you also expose read/write tools over MCP, they are for the client that cannot dial a
stream. While a Yession terminal is attached they should refuse and say where to type —
`write_terminal` and `read_terminal` reach the same console through the lease, where everyone
can see who is typing. Two doors onto one console is exactly what the lease exists to prevent.

## Checking yours

Point `tests/Yession.Tests/Attach.fs`'s expectations at your implementation, or walk this:

- [ ] the upgrade succeeds and the socket stays open
- [ ] device output arrives as **binary** frames
- [ ] a binary frame sent to you reaches the device
- [ ] an unknown text frame is ignored rather than fatal
- [ ] `kill` (if honoured) ends the stream rather than the process behind it
- [ ] the stream ends with `exited` **and then a close**
- [ ] a client that disappears mid-write does not raise
- [ ] if exclusive: a replayed token is refused in band, and no offer is made while attached
