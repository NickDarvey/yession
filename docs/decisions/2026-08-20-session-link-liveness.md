# A session link proves itself alive, rather than being assumed alive until told otherwise

> Decided 2026-08-20 · Supersedes nothing · Related:
> [src/Yession.Domain/Link.fs](../../src/Yession.Domain/Link.fs),
> [design.md](../design.md) §2.3 "Transport",
> [src/Yession.Domain/Resilience.fs](../../src/Yession.Domain/Resilience.fs) — the history
> feed's policy, whose injected-clock shape this mirrors on the other leg

## Decision

Every `FrameChannel` that carries a session — the browser's, the Host's, and the Session
Process's — is wrapped in `Link.supervise` before anything else holds it. The wrapper answers
inbound `Ping` with `Pong`, sends its own `Ping` on a **1s** tick, and after **3** ticks with
nothing heard from the far end declares the link dead: it delivers `None` to its consumer and
closes the underlying channel.

Death has exactly one expression — a closed channel — which is a thing both pumps already
handled. No reconnect logic was added anywhere.

Supervision is **symmetric**. The Host holds every peer to the same heartbeat it answers.

## The failure this fixes

A message typed on a phone showed `QUEUED · 1` and stayed there until the page was reloaded.

The chain: a backgrounded phone's data channel goes half-open — the DTLS association is gone
but `readyState` never leaves `open` and `onclose` never fires. `dc.onclose` was the client's
ONLY liveness signal, so `ClientModel.Connection` stayed `Connected` for ever, `channel.Send`
accepted and discarded every frame, and `SessionLifecycle` — which reconnects a session that
DROPPED — was never told one had.

The recovery path was already correct and already tested. What was missing was noticing.

## Why a heartbeat and not the connection state

`RTCPeerConnection` does report `failed`, and #200 wired it: the browser now keeps its peer
connection and ends the link the moment either state machine says `failed`. That is strictly
better than nothing and strictly insufficient — it is the browser's, so it does not exist on
the Host side or over an in-memory channel, and ICE consent freshness can take tens of
seconds to give a verdict a phone's radio already made.

A heartbeat is a property of the CHANNEL, so one rule covers every transport the product has
and every transport a test can build. Both mechanisms ship: the state machine is the fast
path where it exists, the heartbeat is the floor everywhere. That is not belt-and-braces —
they are one mechanism each at two points, and they go red at different times.

## Why 3 seconds

The cost of being wrong in each direction is asymmetric. A false death costs a reconnect,
which is a handshake and a full-state push over a channel that is already open — cheap, and
idempotent by construction. A missed death costs a person their message until they think to
reload, which is the bug.

1s × 3 puts detection inside the window where somebody is still looking at the screen they
sent from. The traffic is a few bytes a second on a link that already carries keystrokes.

## Why death has one expression

`Link.supervise` could have reported liveness — a `Health` field, an event, a callback — and
every consumer would then have had to decide what to do about it. Instead a dead link CLOSES,
because "closed" is the one thing every consumer already handles correctly: the client's pump
ends and `SessionLifecycle` reconnects; the Host's pump ends and the peer is evicted, which
releases its terminal leases.

That last one was a correctness gap nobody had reported. A silently-dead peer used to hold a
terminal lease for ever, and the idle-lease reclaim only helps when somebody is queued behind
it.

## What makes it cheap to test

`LinkPolicy` takes its `Sleep` as a port, exactly as `Resilience.Policy` does. The suite hands
it a hand-advanced clock, so the entire quiet-tick sequence is asserted in zero real time, in
the cheap tier, with no WebRTC and no native addon. A half-open transport is a five-line test
double (`gaggable`) — sends accepted and discarded, receives swallowed, no close ever
signalled — which is precisely what the phone did.

## What would change it

- **A transport that reports liveness itself, everywhere.** If every channel the product uses
  gained a trustworthy liveness signal, the tick becomes redundant and should go rather than
  sit beside it.
- **A link that carries something with its own cadence.** If session frames were guaranteed
  to flow at least once per tick, the probe could ride on them instead of being sent.
- **Battery evidence.** The tick is a wake on a phone. If it measurably costs battery, the
  answer is a longer tick while backgrounded — not a heartbeat that stops, which is the bug
  wearing a policy.
