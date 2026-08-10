# Plan 16 — Extensibility: custom MCP servers, foreign terminals, and a tool-use record

> **Status: parts A, C and D landed; B and E outstanding.** Serial devices are the first
> consumer, but almost nothing here is
> about serial: the plan is three seams — a **tool registry** with namespaces, a **proxied
> MCP client** so a session can be given servers it did not compile in, and a **foreign
> terminal attach** so a byte stream from another process can become a terminal. A serial
> device pool is then an ordinary external provider rather than a feature of Yession.
>
> Supersedes two earlier plans and their implementation, kept at `e1288b4` on
> `claude/usb-serial-device-queue-8abjoy` — an in-Manager device pool, then that pool as its
> own process with a bespoke control RPC. Both predate terminals. Their vocabulary and
> VID/PID discovery are still good and are lifted; their pool, lease authority, control RPC
> and Manager UI are not.
>
> Builds on [Plan 13 — terminals](13-worksandbox-terminals.md) for the sandbox seam
> (`CreateSandbox`, `Sandbox`, `PtyHandle` in [`Sandbox.fs`](../../src/Yession.Domain/Sandbox.fs))
> and on [Plan 14 — terminal replay in chat](14-terminal-replay-in-chat.md) for the timeline
> (`Timeline.fs`, the chat chips, the pane). Section C adds one case to what that built
> rather than proposing a surface; an earlier draft of this plan predated it and proposed
> the surface, which was wrong twice over — it existed, and it was better.

## Control and data are different problems

The mistake in the superseded plans was picking one protocol for both. Discovering devices,
claiming one, and setting its baud rate are request/response — MCP fits exactly. A serial
line's bytes are a continuous bidirectional stream, and forcing those through MCP tool calls
produces long-polling reads, which is precisely where the last plan ended up.

So they split, and the join is a ticket:

```
  agent ──tools/call serial/acquire_device──▶  provider     (control: MCP over HTTP)
        ◀──── { device_id, attach: ws://… } ──
 session ──────── WebSocket attach ─────────▶  provider     (data: raw bytes)
        ◀──────── bytes, both ways ──────────
        └─▶ the existing terminal stack: emulator, transcript, lease, panel
```

The provider owns discovery and the OS-level port claim. Yession owns what a session does
with the bytes — which is already built, for shells.

## A. Tool namespaces, and the registry that makes them possible

`app/Agent.fs` builds one in-process SDK MCP server with **twelve tools hardcoded inside an
`[<Emit>]` template literal**, a `runQuery` taking **eighteen positional parameters** (one
callback per tool), and a **twelve-entry literal `allowedTools`**. Adding the git-repo tools
cost +117 lines in that shape. Dynamic tools are not awkward there; they are impossible.

So the tools become data:

```fsharp
/// A tool the agent may call, and how to call it. `Namespace` is what makes two providers
/// able to offer `list` without colliding, and what the tool-use record shows so a reader
/// can see WHERE a call went.
type ToolDescriptor =
    { Namespace   : string          // "yession" | "serial" | …
      Name        : string          // "execute_command"
      Description : string
      /// Raw JSON Schema, passed through untouched — and the ONE place secrecy is
      /// declared: a field marked `writeOnly: true` never reaches the audit record (C).
      InputSchema : string }

type ToolCall = { Namespace : string; Name : string; Arguments : string }

/// Invoke a tool. One function, so in-process and proxied tools are indistinguishable to
/// the caller and both pass the same logging seam.
type InvokeTool = ToolCall -> Async<Result<string, string>>

type ToolRegistry = { Tools : ToolDescriptor list; Invoke : InvokeTool }
```

`runQuery` then takes a registry — a descriptor list plus one dispatch function — instead of
N callbacks, builds `sdk.tool(...)` in a loop, and computes `allowedTools` from the same
list. The wire name is `mcp__yession__<ns>_<name>`, or a per-namespace SDK server; either
way the namespace is in the name the model sees and in the record.

**This refactor pays for itself before any external server exists.** It removes the
+1-param-per-tool tax the last two features paid.

## B. Custom MCP servers, proxied

Half the pipe is already laid: `McpTool`/`McpToolList` in
[`Mcp.fs`](../../src/Yession.Domain/Mcp.fs), a `/control/mcp` SSE reverse leg, and
`ProcessManager.PublishMcpTools`. What is missing is everything that makes it work: nothing
**publishes** into it, the session-side handler **only logs the count**
(`app/Host.fs:521` — "left to whatever handler a later composition wants"), the stream
carries **descriptors but no way to call them**, and there is **no MCP client anywhere in
the repo**.

### The session is the MCP client, not the agent

Handing the SDK an external server directly (`mcpServers: { serial: {type:'http', url} }`)
is one line and wrong here, for the reason Plan 13 stage 3b already settled when it merged
two tools into one: *"the gate only becomes real when there is one door."* A server the
agent reaches directly is a second door — its calls skip approval, skip the tool-use record,
and skip attribution.

It also breaks the human story. If the agent's own MCP session holds the device claim, a
human in the Yession UI cannot take it. Proxying puts the claim on **the session**, so the
terminal's existing write lease arbitrates human vs agent on top of it — the two-locks
layering that makes "both can use this device" work at all.

So the Session Process runs an MCP **client** per configured server, calls `tools/list`,
namespaces what it gets, and merges it into the registry from A. Every call routes
`agent → yession's in-process server → proxy → external server`.

### Declaring them

`SessionRecord` has four fields today; MCP infrastructure is currently "the same for every
session" (`RetainedHub`). Per-session servers need: an `McpServerRef` (name, transport, url)
in the domain, a list on `SessionRecord` behind the existing `ManagerState.Version`
migration hook, per-session keying on the publish side, and a management-UI surface to add
one.

**No credentials, this round.** Servers are localhost-only and unauthenticated; the
`headers`/auth field is deliberately absent rather than stubbed. When it arrives it belongs
in Connections/Secrets (Plans 06/08), not in launch env.

## C. The tool-use record, as a fourth timeline item

There is **no tool-use event today**, and the hole is specific: `set_secret` /
`list_secrets` / `delete_secret` are recorded nowhere at all. A human cannot find out that
the agent wrote a secret, because nothing anywhere says so.

Everything *else* about this section is now cheap, because Plan 14 built the surface. An
earlier draft of this plan proposed a merged timeline and argued that a terminal-routed tool
should render as a pointer to its block rather than a copy of it. Both already exist, and
the second is implemented with the same reasoning it was proposed for — the chip renders no
output, because *"a tail inline would make the chat noisiest exactly when it is busiest"*
(`View.fs`). So this section is not designing a surface; it is adding one case to one that
is built:

```fsharp
// src/Yession.Domain/Timeline.fs, today
type TimelineItem =
    | TimelineMessage of ConversationItem
    | TimelineBlock   of EventOffset * TerminalId * BlockId   // anchored where it STARTED
    | TimelineStretch of TerminalStretch                       // anchored where it CONCLUDED
```

One `ToolUseLog` capability, injected into **both** the in-process tool bodies and the proxy,
appending one event, folded into `TimelineProjection` as a fourth case:

```
| ToolUsed of { ToolUseId                                     // minted — see deep links
                AgentTurnId; Namespace; Name
                Arguments : string                            // secrets already gone
                Outcome   : ToolOutcome                       // Ok | Failed of string
                BlockId   : BlockId option }                  // when it ran as a block

| TimelineToolUse of EventOffset * ToolUseId
```

**Carried by id and resolved against a projection**, following `TimelineBlock` exactly: the
timeline holds *where* an item goes, another projection holds *what it currently says*, and
an outcome arriving later moves the second without touching the first. A tool call that is
still running therefore holds its place in the order rather than appearing when it finishes.

### It anchors where it started, and it does not duplicate a block

Plan 14's two anchoring rules are deliberate opposites — a block anchors at its start
because a four-minute build must be visible *while it is the only thing happening*; a
stretch anchors at its conclusion because a lease is only interesting once you know how it
ended. Tool use takes the block's rule for the same reason.

When `BlockId` is set, **no separate chip is rendered** — the block chip already says who
ran what and how it went, and a second item beside it would be two renderings of one fact,
free to disagree. The tool-use record still exists (the audit wants every call), it simply
does not draw twice.

### The agent's chat and the human's chat diverge, on purpose

The constraint that governs this section, and it is already written down in
`Conversation.fs`: terminals do **not** fold into `ConversationProjection`, because that
projection builds the agent's context and the agent already receives block outcomes through
`TerminalDigest` — *"folding them in here would double-feed the model and silently change
what every turn reads."* Plan 14 reversed the **screen**, not the fold.

Tool use has the same hazard in a sharper form: the agent *made* the call and already has
the result in its own transcript, so feeding it back would be pure duplication. So
`ToolUsed` folds into `TimelineProjection` (the view-level merge) and **not** into
`ConversationProjection`.

That is the opposite of the choice repos made — `RepoAdded`/`RepoRemoved`/`RepoBranchSwitched`
*do* fold into the conversation, as `RepoNote` items riding a Process-minted `MessageId`,
because a repo change is a session-shaping fact a *later* turn needs ("we are now on branch
Y"). The distinguishing question is not "did the agent do it" but **"does a future turn need
to be told?"** — and for an ordinary tool call the answer is no. A tool whose effect *is*
session-shaping should be modelled the way repos were, with its own note.

### A secret is a type, and it is gone before the record exists

`set_secret`'s value must never reach the log: the log is served in immutable cacheable
chunks, so a leak there is durable, fetchable, and unrecallable.

Two things settle how:

**Secrecy is declared in the schema, not in a list beside it.** An earlier draft carried a
`Loggable : string list` on the descriptor — an allowlist of recordable fields. It is
rejected because it is a *parallel* list of field names: rename an argument and the entry
silently stops matching the thing it was protecting. The schema already describes every
field, so the declaration belongs on the field. A field marked **`writeOnly: true`** is
secret. That is JSON Schema's own keyword (draft-07+), it is what the ecosystem already uses
for passwords, its meaning — goes in, never comes back out — is exactly the property being
relied on, and it stays intelligible to any MCP client that reads the schema. A custom
`x-yession-secret` would be unambiguous but invisible to everything else, and honouring both
would be two mechanisms for one requirement.

**Redaction happens at decode, not at write.** "Masked on write" still routes the plaintext
through the logging path, where one bug prints it. Instead one generic redactor — schema-
driven, so no tool carries logging code — replaces marked fields as the arguments are
decoded, and `ToolUseLog` is handed a record that never held the value. There is no write
path left to get wrong.

The trade-off is stated rather than hidden: an allowlist fails *safe* (an unmarked field is
never recorded), a marker fails *open* (an unmarked field is recorded). Two things narrow
that, and they are one mechanism rather than two:

- **The marker is derived from an F# type.** A `SecretValue` argument emits the marked
  schema, so a field cannot be declared without its secrecy being declared with it;
  forgetting now means deliberately typing a secret as `string`. This is the existing idiom
  — `SecretName` is already `private SecretName of string` — extended to values, which
  nothing does today.
- **External tools record no argument values at all.** We do not control their schemas, so
  no marking there can be trusted. The record still carries the namespace, tool and outcome,
  which is the part that answers "what did the agent just do".

### Rendering

The chip vocabulary exists (`data-chat-block`, `data-chat-stretch`, a `<button>` so it is
keyboard-operable by construction, tapping it opens a pane tab), so a tool-use item is
`data-chat-tool` alongside them, with `data-chat-tool-status` reusing the outcome tokens
rather than minting parallel ones. Two rules keep it from becoming noise:

- Consecutive tool uses **group by `AgentTurnId`** into one expandable line. The events
  already carry the turn, and a chatty turn should cost one line, not twenty.
- The item **names its namespace**, so `serial/acquire_device` reads differently from
  `yession/execute_command` without a legend.

### Deep links, and minting ids for them

A chip you can tap is one you will eventually want to *send someone* — "look at what the
agent just did" is the natural next request after "I can see what the agent just did". That
wants a URL that reopens a specific item, and it is worth settling now because it decides
which handles get **minted** rather than derived.

The rule Plan 13 stage 2a already established, for `BlockId`: an id that a reader must
*derive* "makes every reader of the projection depend on a derivation rule that lives
nowhere in the data", so a fact that will be addressed gets its id minted by the Session
Process and carried on its event. Applied to the timeline as it stands:

| Item | Handle today | Addressable? |
|---|---|---|
| message / repo note | `MessageId`, Process-minted on the event | ✅ |
| block | `BlockId`, Process-minted on the event | ✅ |
| **stretch** | `TerminalStretch.key` = `"<terminalId>@<offset>"`, **derived** | ⚠️ works, but by rule |
| **tool use** | — | ✗ (this plan mints `ToolUseId`) |

So: **`ToolUsed` carries a Process-minted `ToolUseId`**, for the same reason
`TerminalCommandRejected` carries a minted `BlockId` rather than deriving one from its
`QueueId`. One field now is cheaper than an event migration once a handle is addressable,
and it is the difference between a link that survives and one that depends on an anchoring
offset never being reconsidered.

**The stretch is the outstanding case, and it is worth minting too.** Its key is a composite
of terminal plus the offset it concluded at — stable in practice, and exactly the derivation
rule the `BlockId` argument rejects. It holds only while the anchoring offset never changes;
the moment a stretch is deep-linked, that becomes a wire contract nobody wrote down. Minting
a `StretchId` on `TerminalLeaseReleased` (and its idle/stolen/holder-gone siblings) closes
it. That is a Plan 14 change rather than this plan's, so it is **raised here, not smuggled**:
this plan does not need it, and a deep-link feature does.

The link itself needs no new concept. `SessionRoute` already declares every session path
once, and the pane already keys its tabs by `PaneTab.key` with `OpenPaneTabMsg` opening one
— so a deep link is a route carrying a tab key, resolved on load into the same message a tap
dispatches. What makes that work is only that every key is minted; what makes it break is a
key computed from where something happened to sit.

## D. Foreign terminals over WebSocket

A terminal is an emulator, a transcript, a lease and a panel **over a byte stream**. Today
that stream always comes from `Sandbox.SpawnPty` via `SessionEnvironment.SpawnPty`
(`Terminals.fs` calls it directly at the open path). A serial port is a byte stream too —
`PtyHandle` minus `Resize`, which is the honest difference, since a serial line has no size.

```fsharp
/// Where a terminal's bytes come from, and what that source can do. Capabilities are
/// DECLARED, the way `Sandbox.SpawnPty : … option` already declares pty support, so a
/// source that cannot be instrumented says so at open instead of being discovered at the
/// third command.
type TerminalSource =
    | SandboxShell                     // instrumentable → blocks, marks, exit codes
    | Attached of AttachTicket         // whatever the provider says it can do

type SourceCapabilities =
    { CanInstrument : bool             // OSC 133 marks → blocks; false → live-only
      CanResize     : bool
      HasExitCode   : bool }
```

That generalisation is worth more than serial. An attached source that *is* a shell — a pty
on another host, a CI runner, a container elsewhere — can declare `CanInstrument = true` and
get blocks, approval and exit codes for free. Serial is simply the least-capable instance.

### The wire

**WebSocket.** The client costs nothing (Node ships a global `WebSocket`; the repo pins
Node 24), the provider is already running an HTTP server for its MCP endpoint so the upgrade
rides the same port, and terminal-over-WebSocket is the shape every comparable system uses
(ttyd, gotty, xterm.js attach, k8s exec) — which matters when the point is that a third
party can implement it.

- **Binary frames are data**, in both directions. No framing of ours.
- **Text frames are control**: `{"type":"resize","cols":N,"rows":M}`, `{"type":"kill"}`.
- **Termination**: an explicit `{"type":"exited","code":N}` **then** close. Not because a
  close reason is too small — 123 bytes is ample — but because an abnormal closure (1006)
  carries nothing, and that path exists anyway. The two cases then map cleanly onto the
  domain type that already draws the distinction: `SandboxExited code` vs
  `SandboxRunFailed reason`.

It breaks the repo's SSE-only streak, and that is fine: this is session↔local-provider, like
the control RPC, not the session transport `design.md` pins.

### What has to change in Terminals

`OpenTerminal` gains a source; a terminal takes its channel from a resolver instead of
calling `environment.SpawnPty`; and **a device terminal must not ensure the WorkSandbox** —
a session that only talks to a serial port should not start a container.

## E. The serial provider, as an ordinary consumer

A separate program — ours or anyone's — that owns discovery and the port claim, and speaks
the two protocols above. Its MCP tools: `list_devices`, `acquire_device` (returns the attach
ticket), `configure_device`, `release_device`. Its data plane: the WebSocket. Its
implementation is lifted from `e1288b4`: `VidPidStrategy` (make/model from `(vid,pid)`, ids
stable on serial number, unrecognised ports excluded) and the `serialport` engine behind a
lazy dynamic import so it degrades to "no devices" rather than failing.

Nothing about it is Yession-shaped, which is the whole point — it is separately shippable,
and useful to any MCP client.

## Trust

Three things this opens that the repo does not have today, and none is closed by "localhost":

- **Tool descriptions are untrusted text in the model's context.** An external server's
  `description` goes straight into the prompt. Today the surface is tiny and ours
  (`tools: []` drops every built-in). This is a prompt-injection surface and should be
  named in GAPS rather than discovered.
- **A provider is unconfined.** There is no srt/docker analogue for a tty, and terminal
  access already equals session access. A device is more physical than a filesystem path.
- **Approval policy for external tools.** The terminal's `AutoRun | ApproveAgent |
  ApproveAll` generalises; a sensible default is that external tools are gated until a human
  says otherwise.

## Test gating (`Tag.needs`)

**Cheap tier:** the registry (namespacing, collision refusal, `allowedTools` computed from
descriptors); **redaction — the test that matters most here**: the generic redactor drops
every `writeOnly` field for any schema, `set_secret` records its name and *never* its value,
a `SecretValue` argument emits a marked schema, and an external tool's call records no
argument values at all; the timeline fold (`TimelineToolUse` interleaved with messages,
blocks and stretches by offset; a call carrying a `BlockId` draws no second chip; turn
grouping; a minted `ToolUseId` round-trips as a tab key); the proxy over an in-memory
MCP transport; `SourceCapabilities` deciding blocks-vs-live-only; `OpenTerminal` on an
attached source not ensuring the sandbox.

**`Ports`:** a real WebSocket attach against a loopback echo provider — bytes both ways,
resize control frame, `exited` frame → `SandboxExited`, abrupt close → `SandboxRunFailed`.

**New `Serial` capability, probed by running** (like `Docker`/`Srt`/`Pty`): a **socat PTY
pair** stands in for hardware, so CI needs no device — acquire over MCP, attach, write, read
the echo through the transcript, kill socat and watch the terminal close with a reason.

**`LiveAgent`:** the agent calls a namespaced external tool and the call appears in the
timeline.

## Delivery

Parts A–D are independent except where noted; E needs B and D.

**Landed: 1, 2, 3.** The registry, the tool-use record and the foreign-terminal attach are
in, green in the cheap tier and (for the attach) under `Ports`. **Outstanding: 4, 5** — a
session cannot yet be GIVEN a server, so nothing produces an attach ticket and the serial
provider has nothing to be a consumer of. Step 4 is the one that makes the rest reachable.

1. **A — the tool registry.** ✅ Descriptors + one `Invoke`, `runQuery` takes a registry,
   `allowedTools` computed, namespaces on the wire. Pure refactor; the twelve existing tools
   keep working. *Ships alone and is worth it alone.*
2. **C — the tool-use record.** ✅ `ToolUseLog` injected into the in-process path, the event
   with its minted `ToolUseId`, the `SecretValue` type and its schema marking, the
   decode-time redactor, and `TimelineToolUse` — one case added to `TimelineItem`, one chip
   beside the two that exist. Needs A (for the descriptor's schema), nothing else.
3. **D — foreign terminal attach.** ✅ `TerminalSource`, `SourceCapabilities`, the WebSocket
   channel, the resolver in `Terminals.fs`. Verified against a loopback echo provider — no
   MCP, no serial.
4. **B — custom MCP servers.** ⬜ `McpServerRef`, per-session declaration and publication, the
   MCP client, the proxy merging into the registry and through `ToolUseLog`.
5. **E — the serial provider.** ✅ `yession-serial`, a third bin: the four tools over MCP
   Streamable HTTP, the byte stream over a WebSocket, VID/PID discovery behind a lazy
   `serialport` import. Its E2E drives the SESSION's own client and attach against it over
   real sockets, with the serial ENGINE substituted — so it runs where there is no hardware,
   which is every CI box. The engine itself is still uncovered; see below.

## What landing E changed

`yession-serial` exists and is declared like anyone else's server — the management UI's MCP
form, a url, done. It ships in the same npm package as a third bin because that is where the
build already is, and it depends on nothing in Yession: `SerialProvider` takes an engine and
an origin, and neither mentions a session.

Two implementations of each protocol now exist in this repo, deliberately: `McpClient` /
`McpServer`, and `AttachWs` / `WsServer`. Neither pair shares a line. That is what makes the
tests worth anything — a client exercised only against its own server proves the two agreed,
which is precisely the claim a third-party provider must not depend on.

`serialport` is an **optional** dependency of the PUBLISHED package, not of this repo's dev
tree. The provider lazy-imports it and degrades to "no devices", so the package installs on a
box with no hardware and the dev tree — whose npm dependencies are a Nix fixed-output
derivation — does not have to carry a native addon it never loads. Cross-platform comes free:
`@serialport/bindings-cpp` ships prebuilt addons for Linux, macOS and Windows inside its own
tarball, so an ordinary `npm install` gets a working engine everywhere and nothing is built
from source.

**The engine is covered too**, by the `Serial` capability: socat gives two pseudo-terminals
wired to each other, `SerialPorts.real` opens one and plain file I/O holds the other, and
every byte that crosses went through the same `open`/`termios`/`read` a USB adapter would.
It earned its keep on the first run: `serialport` emits neither `close` nor `error` on an
IDLE port whose device has gone away — it reports the failure only when something next
writes — so a read-only attach to an unplugged adapter hung for ever instead of emitting
`exited`. The engine now watches the device node for removal, which is what unplugging
actually does. What a PTY cannot prove is anything about a specific chip; that stays in
`docs/GAPS.md`.

## Risks & open questions

- **The registry refactor touches the one file every feature touches.** It should land
  first, alone, green — not underneath external servers.
- **Timeline volume** is a judgement call; turn-grouping is the mitigation, and it may still
  need a density control before it feels right. The timeline now carries four kinds of item,
  and tool use is the first that a *single* turn can emit a dozen of.
- **The stretch's derived key is a latent deep-link bug**, and not this plan's to fix. It
  holds only while the anchoring offset never changes; a link handed to someone else turns
  that into a wire contract nobody wrote down. Minting a `StretchId` is the fix and belongs
  to Plan 14.
- **Provider lifecycle is nobody's.** If it is down, tools fail and attaches refuse; who
  supervises it (systemd, the Manager, nothing) is unsettled, and "the Manager only declares"
  argues for nothing. Softened but not closed by Plan 17's poll: a provider that is down is
  retried forever and picked up whenever it returns, so nothing has to restart to notice —
  but nothing starts it either.
- **A device is a host resource, a terminal is a session's.** Two sessions cannot share one
  device. The claim refusal names the holder, and there is a test that says so — a bare
  "unavailable" reads as a hardware fault and sends the agent looking for another way.
- **Reconnect is a new terminal, not a resumed one** when a device re-enumerates under a
  different path.
- **Schema-marked secrecy fails open.** A secret field typed as plain `string` is recorded,
  and nothing catches it — the `SecretValue` type narrows this but cannot close it. The
  redaction test is therefore not optional coverage; it is the mechanism's only guard, and a
  new tool taking a sensitive argument needs one.

## What landing A, C and D changed, and what it did not

Three things are now true that were not:

- **A tool is data.** `ToolDescriptor` + one `InvokeTool`, one SDK MCP server per namespace,
  and `allowedTools` computed from the same descriptors. The wire names did not change,
  because the namespace IS the server's name and the server was already `yession`.
- **Every tool call is recorded.** `ToolUseStarted`/`ToolUseFinished`, folded into the
  timeline and not into the conversation, with `set_secret`'s value dropped at the schema —
  the hole this plan opened with is closed.
- **A terminal can take its bytes from somewhere else.** `TerminalSource`,
  `SourceCapabilities`, and a WebSocket attach verified against a hand-written RFC 6455 peer.

What is still missing is the thing that connects them: **nothing can give a session a
server**, so nothing mints an attach ticket. `ToolDescriptor.foreign` and
`AttachTerminal.unavailable` are the two seams waiting for step 4, and both are exercised by
tests today rather than left as unverified spares.
