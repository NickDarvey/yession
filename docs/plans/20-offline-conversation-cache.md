# Plan 20 — The conversation survives the network

> **Status: proposed.** Open a session you have read before, with no network, and the app is
> empty. Not degraded, not stale — empty, with the idle caret that means *nothing was ever said
> here* ([`View.fs:1173`](../../src/Yession.App/View.fs)). Every durable fact of that session is
> already immutable, already chunked, already addressed by a cache key. None of it reaches the
> screen.
>
> This plan makes durable history a thing the client OWNS, the way the doc already is
> ([`Browser.fs:1157`](../../app/browser/Browser.fs)), and then fetches only what happened since.

## What is actually broken, in four places

The design says the browser's HTTP cache is the client-side event store
([design.md §2.3](../design.md)). Four independent facts stop that from being true, and all
four have to go.

| | | |
|---|---|---|
| **The tail is never cached at all.** A partial chunk is `no-store`, so the newest *n* < 100 events are on the network and nowhere else. A session that never filled a chunk has no cached history whatsoever — which is most sessions. | [`EventLog.fs:40`](../../src/Yession.Domain/EventLog.fs) |
| **Nothing asks.** The read loop fires only when it knows the log is longer than its cursor, and `latestKnown` arrives on `PeerAccepted` or an `EventsAvailable` hint — both over the data channel. No transport, no hint, no read. The cached chunks are sitting there and nobody requests them. | [`App.fs:436`](../../src/Yession.App/App.fs), [`App.fs:544`](../../src/Yession.App/App.fs) |
| **The whole feed is behind a live probe.** `/me` is fetched before the feed, the connection, or anything else is constructed; an unreachable probe dispatches `ConnectFailedMsg` and the `else` branch — feed, transcripts, lifecycle — never runs. | [`Browser.fs:1170`](../../app/browser/Browser.fs) |
| **There is no page to load.** The shell is `no-cache` and there is no service worker, so a cold open with no network never gets an app at all. | [`Routes.fs:239`](../../src/Yession.App/Routes.fs), [GAPS.md:279](../GAPS.md) |

The first three are why a *loaded* app shows nothing. The fourth is why a *cold* open shows
nothing at all.

## The extension point is one sentence

**A client keeps the events it has consumed, and asks only for what came after.**

Not a cache — a cache is something you hope is there. A replica of a prefix of an append-only
log, which is a thing you can enumerate, resume from, and reason about offline. The log is
append-only and the Session Process is its only writer (design.md §5), so a client's copy of
offsets `0..n` can never be wrong; it can only be short. That is the entire correctness
argument, and it is the same one that made the chunks immutable in the first place.

## The store

One IndexedDB database per session, beside the doc store and keyed the same way
(`yession/session/<id>`, read from the shell's `<meta name="yession-session">` — a
pre-connection identity, [`Browser.fs:410`](../../app/browser/Browser.fs)):

```text
events   key: offset (int64)     value: the envelope's JSONL line, verbatim
```

**Verbatim lines, keyed by offset** — not the decoded envelope, and not the chunk. Three things
follow, and each is the reason:

- The line is what the server served, so replay runs the *same* `Codec.sessionEventEnvelope`
  decode the network path runs ([`App.fs:303`](../../src/Yession.App/App.fs)). One decoder, one
  set of failure modes. A stored decoded object would be a second wire format that only this
  client can write and only this client can read.
- Keying by offset rather than by chunk index makes the tail storable. A chunk is the *fetch*
  granularity because that is what an HTTP cache key needs; the store has no such constraint,
  and the partial chunk is exactly the history that matters most.
- The conversation stays a projection. Nothing persists `ConversationProjection` — the model is
  rebuilt by folding events, so a projection change ships without a migration and without a
  store that disagrees with the log.

`navigator.storage.persist()` is requested once, best-effort: both stores are evictable
otherwise, and the doc store already carries that exposure.

## Where it composes, and what stays ignorant

Per "composition at the top": the store is a port, wired in `app/browser/Browser.fs`, and no
application code learns that storage exists.

```fsharp
/// A client's replica of a prefix of the log. Total, like the feed: a store that
/// cannot be opened is `None` and the client is exactly as it is today.
type EventStore =
    { Read  : unit -> Async<EventEnvelope<SessionEvent> list>
      Write : EventEnvelope<SessionEvent> list -> Async<unit> }
```

Two seams use it, and they are deliberately separate:

**1. A write-through decorator on the feed.** `App.EventFetch.storing : EventStore -> EventFeed
-> EventFeed` — pass the page through, write its envelopes, return it. It composes *inside* the
resilience policy's guard, so only settled pages are stored:

```fsharp
let feed =
    App.EventFetch.overHttp httpGet SessionRoute.relative None
    |> Resilience.Policy.guard (App.EventFetch.policy …)
    |> App.EventFetch.storing store
```

**2. A hydration pump, before and independent of the transport.** Read the store, dispatch the
events as a page, done. It is not part of the read loop and must not be: the read loop's job is
to chase a moving remote end, and this has no remote end to chase.

Hydration runs *unconditionally at boot*, before the `/me` probe and regardless of its outcome.
That is the fix for the third broken thing: the probe decides whether this client can *connect*,
not whether it may read what it already has.

## Fetching from the last message onwards falls out

Nothing new is needed for the resume. `ConnectOptions.ReadPosition` is already the model's
`LastProcessedOffset` ([`Browser.fs:1217`](../../app/browser/Browser.fs)), and the read loop
already asks the model rather than its own bookkeeping — for precisely this class of reason
([`App.fs:236`](../../src/Yession.App/App.fs)). Hydrate first, and the first request the loop
makes when a transport finally opens is `after = <last stored offset>`. One chunk, the tail,
usually partial, usually small.

That ordering is the only new invariant, and it is worth a test of its own: **a client that
hydrated must never re-fetch what it hydrated.**

## One fold, two things to say about the feed

`EventsPageMsg` sets `Feed = FeedLive`, because a page arriving over HTTP *is* the feed working
([`Model.fs:993`](../../src/Yession.App/Model.fs)). A page off the local store is not, and must
not claim to be — an offline client would report a live history feed.

So: `LocalHistoryMsg of EventPage<SessionEvent>`, folding through the same offset-gated
projection code as `EventsPageMsg` (extract the fold, keep the two cases differing only in what
they conclude about `FeedHealth`). It leaves `Feed` untouched and does not advance
`LatestKnownOffset` beyond its own high-water mark — offline, "how far does the log go" is
genuinely unknown, and the model should say so rather than assert that it is up to date.

## What this deletes

Per "no belt-and-braces": once the browser holds its own replica, ask who is left reading
`Cache-Control: private, max-age=259200, immutable` on a full chunk.

- Not the browser: it now requests only offsets it does not have, so the request that would hit
  the cache is the request it no longer makes.
- Not a shared cache: `private` excludes them, deliberately.
- Not Node clients or tests: no HTTP cache in that path.

**Recommendation: drop the `max-age`, and let every chunk response be `no-store`.** The chunk
*bounds* stay fixed forever — that is the fetch granularity and the store's read-through
argument — but the header stops describing a mechanism nobody uses. A header that satisfies
nothing is the redundant spare the rule is about, and leaving it in place would leave two
answers to "where does this client's history live" with only one of them true.

This is the one reversible-but-load-bearing call in the plan, so it is called out rather than
buried: it means a browser whose IndexedDB is denied (private mode) re-fetches history on every
reload. That is the same degradation the doc store already has there, and it is the honest one —
today that browser gets an empty conversation instead.

`TranscriptChunk` keeps its policy until the transcript follow-up gives it the same treatment.

## The shell, offline

Everything above is worth nothing on a cold open with no network, because there is no page. A
service worker is the only mechanism a browser has for that, and it is the mechanism this repo
has been missing since GAPS first named it.

Scope is the mount (`sw.js` served under the session's prefix, so a path-mounted session
controls its own pages and no one else's — the same reason every URL the shell emits is
relative, docs/plans/09). Two rules, and nothing else:

- **The shell**: network-first, falling back to the cached copy. Network-first keeps `no-cache`'s
  actual promise — a new build is picked up on the next load with a network — and the fallback
  is what makes the offline open possible at all.
- **Fingerprinted assets** (`client.<digest>.js`, `app.<digest>.css`): cache-first, forever.
  Their address pins their bytes, which is the same argument that already gives them
  `max-age=31536000, immutable`.

It caches **nothing else**. Not `/events/*` — that is the store's job, and a service worker
holding history too would be exactly the redundant spare deleted one section above. Not `/me`,
`/signal`, `/claude*`, `/queries` — those are liveness questions, and a cached answer to
"can I reach this session" is a wrong answer.

Old caches are dropped on `activate`, keyed by the digests the shell names, so a build's assets
do not accumulate.

## Retrying, and the loader that says so

Offline, today, the client gives up permanently and silently: `SessionChannel.policy` spends
four attempts and dispatches `ConnectFailedMsg`; the feed's policy spends five and parks with
`FeedStalled`, re-armed only by an availability hint or a reconnect
([`App.fs:471`](../../src/Yession.App/App.fs)) — neither of which can arrive with no network.
GAPS already records the missing half ("recovery waits for the next availability hint or
reconnect … that peer must reload").

Three changes, smallest first:

1. **Re-arm on `online`.** The browser knows when the network came back; listen for it and
   retry the transport immediately. One event listener, and it turns the common case (laptop
   lid closed on a train) from "reload the tab" into "it just comes back".
2. **Supervise rather than surrender.** After a settled `ConnectFailedMsg`, keep trying on a
   capped schedule (the existing `Resilience.Schedule` values, ceiling raised to a minute) with
   the attempt count reported. The lifecycle's existing rule is untouched and is the reason this
   cannot spin: a *rejected* peer still stops for good, because reconnecting would only be
   rejected again ([`App.fs:697`](../../src/Yession.App/App.fs)).
3. **A manual retry**, on the strip. The affordance GAPS asks for, and the only recovery a peer
   with a permanently-401'd token has short of a reload.

The visual vocabulary already exists and is not being reinvented: `statusDotPulse` beside a
status word, with the degraded strip over the timeline and the header status
([`View.fs:683`](../../src/Yession.App/View.fs)). What is added is the state that had no
representation — **history not read yet** — because the empty timeline currently renders the
idle caret, which means the opposite:

```text
HistoryRestore = Pending | Restoring | Restored
```

The timeline renders the loader when it is empty AND history is not `Restored`; the idle caret
means what it has always meant, and now only ever means it. `Pending` is the initial model, so
the server-rendered shell paints the loader too — at first paint the history genuinely has not
been read, and the SSR'd page is the one case where that lasts long enough to see.

## Test gating (`Tag.needs`)

Cheap tier — no capability, runs on every PR:

- The store's read-through: a page fetched once is stored; a hydration then yields the same
  envelopes through the same decoder; a corrupt stored line fails the page as `FeedCorrupt`
  rather than silently dropping an event (the rule the network path already holds).
- **Resume**: hydrate a store holding `0..41`, connect a fake transport advertising latest `47`,
  assert the feed was asked for `after = 41` and nothing below it — the plan's central promise,
  pinned as a contract on the feed rather than as an observation about the UI.
- `LocalHistoryMsg` folds the conversation without reporting `FeedLive`, and does not assert a
  `LatestKnownOffset` it has no evidence for.
- Fold idempotence across hydration + a network page covering the same offsets (the offset gate
  already guarantees it; this pins that hydration goes through the gate).

`Browser` tier — Chromium only, no host machinery:

- **Availability under fault**: a session with messages, reloaded with the network cut, still
  shows those messages and still has a working composer. Scoped to the timeline element, and
  regressed-to-red before it is believed — a bare page-level `Contains` would pass off the
  roster while the timeline stayed empty.
- The shell loads cold with no network (service worker), and the retry affordance is present and
  keyboard-operable.

Not tested: which element the loader is, what the strip says, or any class token. Those are the
design changing, which is what a design is for.

## Delivery

Four PRs, each shippable alone, in this order — because each one is worth something without the
next, and the reverse is not true.

| | | |
|---|---|---|
| 1 | **The store and hydration.** `EventStore`, the write-through decorator, the boot-time pump, `LocalHistoryMsg`, hydration moved above the `/me` probe. Drops the chunk `max-age`. | A session unreachable with the network up (asleep, reaped) reads back in full. |
| 2 | **Retrying that does not give up.** `online` re-arm, supervised reconnect, manual retry, GAPS entry closed. | Recovery without a reload. |
| 3 | **The loader.** `HistoryRestore`, the empty-timeline split, the strip's restoring state. | The empty screen stops lying. |
| 4 | **The service worker.** Shell and fingerprinted assets, scoped to the mount. | A cold open with no network at all. |

Docs ride the PR that makes them true: design.md §2.3 says the browser's HTTP cache is the event
store and stops being true in PR 1; the GAPS entries for the missing shell and the missing manual
retry close in PRs 4 and 2.

## Risks & open questions

- **Unbounded growth.** The replica is the whole prefix. Envelope lines are small and the
  Session Process already holds the same JSONL on disk, but a long-lived session has no ceiling.
  Measure on a real session before adding a prune policy; if one is needed, it is "keep the last
  *k* chunks and the first" and it is a follow-up, not a guess made now.
- **Stale stores outlive their sessions.** A deleted session leaves its database behind. Small
  and per-session, but it is durable conversation content sitting in a browser profile — worth a
  sweep (drop stores for sessions the Manager no longer lists) once there is a surface that knows.
- **Ephemeral deployments strand it, exactly as they already strand the doc.** Storage is
  origin-partitioned, so a session that reopens on a new port gets a new store. No new problem
  and no new promise: `EphemeralStorage` already changes what the degraded strip says.
- **`online` lies sometimes.** It reports a link, not reachability. It is a *trigger* for an
  attempt that can fail, never a claim that the session is back.

## Later, deliberately not now

- **Terminal transcripts**, which are the same shape one level over: chunked, immutable,
  `no-store` at the tail, and lost on reload. Same store, same decorator, same argument — after
  the event log has proven it.
- **Writing offline.** Out of scope by design: drafts and the queue are CRDT state in the local
  doc and already work offline; a *sent* message is a durable event, and the Session Process is
  the only writer of those.
