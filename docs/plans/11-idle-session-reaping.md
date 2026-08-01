# Plan 11 — Pinned session ports, idle reaping, and a stable way back in

> **Status: implemented, not yet merged** (branch `feat/idle-session-reaping`). The
> [tracker](TODO.md) row lands with the merge.
>
> Addresses [GAPS.md](../GAPS.md) § Runtime & topology on two fronts: *"children die with
> the Manager … a Manager restart stops every running session"*, and *"session ports are
> OS-assigned and change on every launch, so a session's client URL is not stable across
> resumes"* — which turns out to cost more than a broken bookmark.

## The problem is an upgrade, not a resource

A deployment that tracks master closely restarts its Manager whenever a new build lands.
Every restart kills every session: children are spawned under the stdin parent guard and
die with their parent. Sessions resume — the event log and doc sidecar survive, and resume
is just launch — but an agent mid-turn is interrupted and a human mid-sentence watches
their peer connection drop. So the operator either evicts live users on every promotion, or
defers the promotion and runs stale.

The asymmetry that makes this cheap: **a session process is not the Manager.** The Manager
restart is what evicts people, and the Manager is upgraded far less urgently than the
session code is — sessions are where the agent, the editor, the transport, and nearly all
the churn live. If idle sessions exit on their own and relaunch from a floating path
(`YESSION_SESSION_BIN`, which `spawn` resolves at exec, so no code change was needed for
it), sessions upgrade continuously while the Manager is left alone. The Manager's own
restart can then wait for a moment when nothing is running, which costs nobody.

Reaping also reclaims what a long-lived idle session holds: a Node process, its Yjs
replica, the fully-loaded in-memory event log, and a doc sidecar that only compacts at
open. On a multi-session host that is the larger prize.

## What had to be true first: a session's origin must survive a relaunch

`persistenceKey` (`app/browser/Browser.fs`) keys the browser's IndexedDB store
`yession/session/<id>`, and its comment claimed a session "keeps its store wherever it is
served from". That holds only *within* one origin. **IndexedDB is partitioned by origin,
and a port is part of the origin**, so a session that comes back on a new port comes back
to an empty database, with whatever its user wrote offline stranded in one nothing will
open again. On reconnect the client pushes full state from that empty replica and the
server's copy wins.

This was already broken on every stop/resume. Reaping would have promoted it from rare to
routine, so it is fixed first, and everything else depends on it.

`SessionPorts` (`src/Yession.Manager/Ports.fs`) is one validated value, in the shape
`PublicAccess` established:

```
YESSION_SESSION_PORTS unset      -> Ephemeral      (the default; today's behaviour exactly)
YESSION_SESSION_PORTS=8400-8499  -> Pinned <range> (one address per session, for life)
```

`PortRange` has no public constructor and `SessionPorts.create` is the only way to obtain
one, so a caller holding `Pinned` holds a range validated at boot, and no code path can ask
for a pinned port and find none configured. `create` takes the Manager's own port and
refuses a range containing it — a deployment that works until the day a session is
allocated 8321 belongs in a refused boot, not a support thread.

Three rules protect the address once assigned:

- **A stored port is authoritative even outside the current range.** Shrinking a range must
  not silently relocate a session; out-of-range ports stay excluded from new allocations so
  the two can never collide.
- **A pinned port that will not bind fails the launch, naming the port.** Never a silent
  fallback to an OS-assigned one — moving the session is the data-losing behaviour this
  exists to prevent.
- **Duplicate stored ports fail at load**, where the cause is still visible, rather than at
  whichever launch happens to come second.

`Ephemeral` assigns nothing and discards nothing, so turning pinning off and on again
restores the same addresses. This supersedes the Manager's `YESSION_PORT`, which pinned one
port for *every* session and was already wrong for more than one.

**Path-mounting (Plan 10) is the intended end state** and removes per-session ports
entirely: one origin, `…/s/{id}`, so storage survives by construction. Pinning is the
near-term fix that makes reaping safe without also rewriting an operator's proxy
reconciliation.

## What "in use" means, and who gets to say

The Manager cannot see use. Peers connect over WebRTC straight to the session's own port,
`launchPeers` only ever grows, and a running turn is internal to the session's scheduler.
Every outside proxy is wrong in the same direction: the mtime of `events.jsonl` cannot see
a human reading with the tab open (no appends, session very much in use) and cannot see a
turn stalled inside a long tool call (no appends until it finishes). Both would be reaped
mid-use.

So the session decides, and the Manager decides what to do about it. A session is **busy**
when any of these hold:

- a peer is connected, or
- an agent turn is running, or
- the queue is non-empty — accepted but not yet drained.

Drafts and presence are deliberately not inputs: they belong to a connected peer, so the
first condition already covers them. A doc that will not decode counts as busy — a session
in trouble should not be stopped out from under its owner.

`POST /control/activity` carries one boolean, in the shape `POST /control/name` already
established. Transitions go out at once (including peer connect and disconnect, so a closed
tab starts the clock immediately) and busy repeats every 30s. The repeat is what makes the
mechanism robust rather than merely prompt: **the Manager reaps on silence**, so no single
delivery has to succeed. Unlike `nameReporter`, which swallows every failure because a
title that fails to arrive is cosmetic, `activityReporter` logs each one — the session is
about to be stopped for it.

The Manager timestamps each report with **its own clock**; a child's idea of the time never
enters the decision.

## Reaping

`Reaper.plan` is pure, like `QueueDrain.plan`: given `now`, the window, and what each
running launch has reported, it names the sessions to stop and why. The Manager's sweep is
a loop over its answer, and its interval is derived from the window (a quarter of it,
clamped) rather than configured separately, so there is no second setting to hold a
contradictory value.

`LastBusyAt` is seeded at **launch**, so every launch gets the whole window before it can be
reaped, whether or not it ever reports. And a launch that never reports **is** reaped —
tagged `NeverReported` rather than `Idle`, because a build too old to report and a wedged
process are exactly what needs stopping, and the alternative is that anything which stops
reporting becomes immortal. The two reasons reach telemetry as
`yession.session.stop_reason`, which is the difference between a log line and a diagnosis.

Reaping goes through the existing `Stop`, so the exit records as expected rather than as a
crash. A stop that fails logs and is retried on the next sweep — a reaper that silently
gives up looks exactly like one with nothing to do.

`IdleTimeout` is `None` by default. Reaping is an explicit operator choice.

## Version skew now refuses the launch

`Spawn.warnOnMajorSkew` printed to stderr and launched anyway. That was defensible while
the session binary was whatever shipped beside the Manager; it is not, once a deployment
points `YESSION_SESSION_BIN` at a floating path, because a major bump upstream then
silently pairs two processes that no longer agree and surfaces later as something else
entirely.

`Spawn.majorSkew` now refuses, naming both builds. The consequence is self-correcting where
it matters: no session starts, the running set drains to empty, and an operator whose
promotion rule waits for quiescence restarts the Manager on its own. Builds that cannot
state a release version (`dev`, `test`) are never compared — those are the paths where both
halves are built together.

## The way back in

Reaping makes a stopped session's missing address routine, so there is now one stable URL
per session:

```
GET /sessions/{id}/open      launch if stopped -> wait -> land at the session's address
```

The address comes from `PublicAccess.sessionAddress`, so one route is correct in every
deployment shape. It answers with a small page that polls its target before redirecting,
rather than a bare 302: a session that had to be launched is reachable only once the
operator's proxy has a mapping for it, and a reconciler driven by `/sessions/stream` gets
there in a few hundred milliseconds — quick, but a race against a redirect the browser
follows immediately. It gives up after 20 seconds and says what it suspects, because an
`/open` that spins forever is indistinguishable from one that is about to work.

### The client offers it where the status word used to be

When the session it was talking to has gone, the client settles into
`Disconnected (Some reason)` on its own — `SessionLifecycle.run` gives an accepted session
exactly one more attempt, and the retry policy settles when nothing answers. In that state
the sidebar's connection status is replaced by a card: what happened, that the work is
safe, and the button that fixes it. The same move `peopleSection` already makes for a
missing agent — when the thing being reported is not a state you can wait out, a status
word is the wrong shape.

The offer is **total over the model** — it needs a settled disconnection, a Manager origin,
and a session id — which is what makes its failure modes structural rather than defensive.
The view never reads the DOM, so there is no path that renders a button with nowhere to go;
the shell omitting the meta tag is sufficient, which is why it must omit it rather than
emit an empty one.

The Manager's origin reaches the client as `<meta name="yession-manager">`, alongside the
session id tag that was already there. It is known synchronously at boot on **every**
deployment, loopback included: `YESSION_CONTROL_URL` is the Manager's own endpoint, and
that endpoint is the same HTTP server as the management UI — precisely the origin that
serves `/open`. `PublicAccess.managerUrlOr` states the precedence once (a configured public
origin always wins, because a loopback endpoint is unreachable from a browser that is not
on this machine), and the Manager's OIDC issuer now reuses it, so the two cannot disagree.

One latent bug fell out of this: the shell SSR'd `Session = Some id` into the model and the
browser dropped it on hydration, so `data-session-id` rendered, blanked, and only returned
with `PeerAccepted`. Cosmetic before; load-bearing now, since the offer names the session at
exactly the moment `PeerAccepted` has never happened. The client seeds both tags from the
shell.

## What this does not fix

The Manager still cannot upgrade itself without evicting. This shrinks the window to "when
nothing is running" rather than removing it, and a permanently open tab keeps both its
session and the Manager stale — a connected peer counts as busy, full stop. Removing the
window entirely means daemonising sessions: dropping the stdin parent guard, re-parenting
orphans at boot, and re-establishing per-launch control secrets and OAuth client
registrations for launches the new Manager never made. GAPS scopes that out, and this plan
gets most of the benefit without touching the security model.

Two smaller costs, both stated rather than discovered:

- **Reaping logs users out of that session.** `launchUsers`, the OAuth client registration,
  and the per-launch secret all die with the launch by design. Invisible under
  `--auth localhost`; a re-bounce under trusted headers or a real IdP.
- **A wedged session stops beating and gets reaped.** Arguably a partial fix for "no health
  checks beyond the readiness line", but it is a behaviour change dressed as a timeout.
