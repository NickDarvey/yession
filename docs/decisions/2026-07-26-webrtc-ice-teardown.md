# Patch libdatachannel's libnice ICE teardown rather than switch ICE backend

> Decided 2026-07-26 · Supersedes nothing · Related: [nix/libdatachannel-nice-teardown.patch](../../nix/libdatachannel-nice-teardown.patch), [nix/packages.nix](../../nix/packages.nix), [AGENTS.md](../../AGENTS.md) "Testing"

## Decision

The intermittent SIGSEGV in the `Native`-tagged suites was a use-after-free inside
libdatachannel's libnice ICE backend, not in any Yession code. We carry a one-file patch to
libdatachannel that serialises ICE-transport teardown onto libnice's glib main loop. We do
**not** switch to libjuice, which would have deleted the faulty code path but costs ~1s on
every first connection.

## What was actually wrong

`~IceTransport()` detaches the receive callback and removes the nice stream from whatever
thread destroys it. libnice delivers receives — and emits its `component-state-changed`,
`new-candidate-full` and `candidate-gathering-done` signals, all bound to `this` — on a
single shared glib main loop. Nothing synchronises the two, so a receive the loop is *already
dispatching* runs against a half-destroyed transport whose stream is being removed underneath
it.

Caught under gdb on the 18th run:

```
Thread 32 "MainThread" received signal SIGSEGV, Segmentation fault.
#0  __memmove_evex_unaligned_erms ()                          libc
#1  rtc::make_message<std::byte*>(...)                        libdatachannel
#2  rtc::impl::IceTransport::RecvCallback(_NiceAgent*, ...)   libdatachannel  (icetransport.cpp:881)
#3  nice_component_emit_io_callback ()                        libnice
#4  component_io_cb ()                                        libnice
#5  socket_source_dispatch ()                                 libgio
#6  g_main_context_dispatch_unlocked ()                       libglib
#8  g_main_loop_run ()                                        libglib
```

The faulting thread is libnice's loop, not JS. That is why the two previous attempts at this
crash — draining close callbacks before global cleanup (`0b1b8ae8`) and awaiting
libdatachannel's `closed` state (`169faa61`) — narrowed the window without closing it: both
sequence *JS-side* teardown correctly, and this race lives below anything JS ordering can
reach.

**The libnice backend was never a deliberate choice.** `nix/node-datachannel.nix` patches out
upstream's GitHub `FetchContent` and links nixpkgs' libdatachannel instead; nixpkgs builds it
`-DUSE_NICE=ON -DPREFER_SYSTEM_LIB=ON -DNO_EXAMPLES=ON`. libdatachannel's own default is
`option(USE_NICE "Use libnice instead of libjuice" OFF)`, and upstream node-datachannel ships
libjuice — libnice is opt-in there, behind a separate `install:nice` script. So the source
swap silently moved us onto a backend upstream neither ships nor tests.

## The fix

Run the detach *on* the loop thread and wait for it. A `GMainContext` dispatches from one
thread at a time, so while the teardown runs the loop cannot be delivering anything, and once
it returns nothing can reach the object again. The signal handlers are disconnected in the
same critical section, for the same reason. `g_main_context_invoke_full` runs the function
inline when the caller already owns the context, so destroying from inside a libnice callback
cannot deadlock.

## What was measured

Rates before the fix, all on this container:

| Configuration | Crashes |
|---|---|
| `check Browser Ports Native Keyring` | 1 / 5 |
| bare `node Main.js`, caps `Ports Native` | 0 / 12 |
| bare `node Main.js` under gdb, full caps + D-Bus/keyring | 1 / 18 |

~2–5%, and the crash site moves (Phase4 Step 24 once, Phase2 acceptance E2E another) — a
teardown race, not one bad test.

A soak alone could not settle it: at a 1-in-18 rate, 25 clean runs happen ~24% of the time by
luck. The stress below hits the race directly and gives a causal answer:

| Build | 5 × 300 teardown rounds |
|---|---|
| Unpatched libnice | **3/5 SIGSEGV** — mid-loop at rounds 150, 225, 175 |
| Patched | **5/5 clean**, 1500 rounds |

Plus 25/25 clean runs of the previously-1-in-18 configuration, and `check Browser Ports Native
Keyring` (252 Node + 26 CLR) green.

## Alternative considered: libjuice

Building libdatachannel `-DUSE_NICE=OFF` removes the faulty code path outright rather than
patching it, and matches what upstream ships. It was implemented and verified — 25/25 clean —
but rejected on latency. Same two-peer non-trickle handshake, same box:

```
libnice    93ms  95ms  92ms  93ms  93ms
libjuice  1014  1015  1013  1013    66ms
```

Not gathering (complete at 10ms) — DTLS. The answerer reaches ICE-connected at 14ms and sends
its ClientHello immediately, but the offerer's agent doesn't finish checks until ~70ms, so the
packet is dropped and retransmitted on DTLS's 1s timer. An 11x regression on session open, for
a local-first app, was the worse trade.

It also needed more machinery: nixpkgs has no libjuice at all, so it meant packaging it
(pinned to libdatachannel v0.24.1's own `deps/libjuice` submodule) plus
`-DENABLE_LOCALHOST_ADDRESS=ON` to restore libnice's loopback candidates — without which a
host whose only interface is loopback cannot connect.

That work is complete and reachable at commit `9ed17a2` (force-pushed off
`claude/run-tests-y4qp1b`, not in master's history).

## A separate crash this uncovered

The first version of the stress called `ndc.cleanup()` at the end. **Both** builds survived
all 300 teardown rounds and then crashed at that global cleanup — so the first A/B measured
the wrong thing entirely and was rebuilt without it. That independently confirms why
`tests/Yession.Tests/Client.fs` sequences its teardown: node-datachannel's global cleanup with
objects that have not yet reported `closed` still aborts, on patched and unpatched alike. Out
of scope here; do not call `Interop.cleanup ()` with live native objects behind it.

## What would change this decision

- **nixpkgs packaging libjuice**, or libdatachannel there defaulting to it — the patch becomes
  unnecessary maintenance
- **A libdatachannel bump** in nixpkgs: the patch is against `src/impl/icetransport.cpp` at
  v0.24.1 and will need re-checking (it will fail loudly, not silently — a failed `patches`
  application breaks the build)
- **Upstreaming it.** Not submitted. Upstream's default backend does not have this race, so
  the fix only matters to `USE_NICE=ON` builds like nixpkgs'
- **libjuice's DTLS stall being fixed or tuned away** — then the backend swap dominates,
  because deleting a code path beats patching one

## The stress harness

Not committed as a suite: it is ~30s and inherently timing-based, which cuts against the
no-flaky-tests rule in AGENTS.md. Recorded here instead so the verification can be repeated.
Run it from the repo root inside `devenv shell` (it resolves `node-datachannel` from
`node_modules`); to test an alternative build, symlink that derivation's package into a scratch
`node_modules` and run from there.

```js
// stress-teardown.mjs — hammer the teardown race directly: connect a pair, get data flowing,
// then destroy both peers WHILE receives are in flight — exactly the window where libnice's
// glib loop can be dispatching a receive into a transport being torn down.
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const ndc = require('node-datachannel');

const ROUNDS = Number(process.argv[2] ?? 200);
const payload = 'x'.repeat(4096);

const gathered = (pc) =>
  new Promise((res) => pc.onGatheringStateChange((s) => { if (s === 'complete') res(pc.localDescription()); }));

for (let i = 1; i <= ROUNDS; i++) {
  const offerer = new ndc.PeerConnection(`o${i}`, { iceServers: [] });
  const answerer = new ndc.PeerConnection(`a${i}`, { iceServers: [] });

  const offerReady = gathered(offerer);
  const dc = offerer.createDataChannel('session');
  const opened = new Promise((res) => dc.onOpen(res));

  let remote = null;
  answerer.onDataChannel((c) => {
    remote = c;
    c.onMessage(() => { try { c.sendMessage(payload); } catch {} });
  });

  const offer = await offerReady;
  const answerReady = gathered(answerer);
  answerer.setRemoteDescription(offer.sdp, offer.type);
  const answer = await answerReady;
  offerer.setRemoteDescription(answer.sdp, answer.type);
  await opened;

  // Saturate both directions, then tear down mid-flight — no waiting for closed.
  const pump = setInterval(() => { try { dc.sendMessage(payload); } catch {} }, 0);
  for (let k = 0; k < 40; k++) { try { dc.sendMessage(payload); } catch {} }
  await new Promise((r) => setTimeout(r, 15));
  clearInterval(pump);

  offerer.close();
  answerer.close();
  if (remote) { try { remote.close(); } catch {} }

  if (i % 25 === 0) console.log(`round ${i} ok`);
}

// Deliberately NO ndc.cleanup(): the global teardown racing objects that have not yet reported
// closed is a separate, known-unsupported pattern (see Client.fs). Exit hard so anything that
// crashes here is attributable to per-connection teardown.
console.log(`survived ${ROUNDS} rounds`);
process.exit(0);
```

The `1 in 18` figure came from looping the Node suite under gdb until it faulted, which is how
the backtrace above was captured:

```
gdb -q -batch -ex run -ex 'info threads' -ex 'thread apply all bt 30' \
    --args node tests/Yession.Tests/out/Main.js
```
