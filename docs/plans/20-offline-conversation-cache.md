# Plan 20 — The conversation survives the network

> **Status: proposed.** Open a session you have read before, with no network, and the app is
> empty. Not degraded, not stale — empty, with the idle caret that means *nothing was ever said
> here* ([`View.fs:1173`](../../src/Yession.App/View.fs)). Every durable fact of that session is
> already immutable, already chunked, already addressed by a cache key. None of it reaches the
> screen.
>
> This plan finishes the idea the chunks started — an address that names its own bounds, and a
> link to the next one — so a client reads from the start of time out of its own cache and
> fetches only what happened since.

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

**An address that names its own bounds is immutable, so a client reads from the start of time
and follows links.**

Not a cache — a cache is something you hope is there. A chain of immutable resources, walked
from a fixed origin, which is a thing you can enumerate, resume from, and reason about offline.
The log is append-only and the Session Process is its only writer (design.md §5), so a resource
that names offsets `[a, b]` returns the same bytes for ever; it can only be joined by later
ones. That is the entire correctness argument, and it is the one that made the chunks immutable
in the first place — applied to an address that can carry the tail.

## The chain

`/events/{index}` is mutable because "chunk 3" means *whatever chunk 3 holds now*, and what it
holds now grows. `/events/{from}-{to}` is not: offsets 300–336 are those 37 events for ever,
including after the log reaches 500. The mutability does not disappear, it compresses into a
single pointer — where the log ends *right now* — and everything reachable from that pointer is
cacheable for ever.

```http
GET /events/0-99                       → 100 lines
                                         Cache-Control: private, max-age=31536000, immutable
                                         Link: </events/100-136>; rel="next"

GET /events/100-136                    → 37 lines, immutable, no `next` (it was the end)

GET /events/head                       → no lines
                                         Cache-Control: no-store
                                         Link: </events/137-212>; rel="next"
```

Three rules, and each one is load-bearing:

- **A range is served only if the log reaches its end**, else 404. The URL is guessable, and a
  guess answered with a *short* body would write a truncated answer into an `immutable` entry
  at a full range's address — wrong for a year and unfixable from the server. That is exactly
  the hazard `serveAsset` 404s to avoid ([`Signalling.fs:49`](../../app/Signalling.fs)); same
  argument, same answer.
- **The server mints every link**, capped at `EventChunk.size` events, so a chain step is
  bounded and a client never has to choose a range. Client-minted ranges are validated against
  the same cap.
- **`Link: rel=next` is a header**, so the body stays pure JSONL and the pointer is cached with
  the bytes it belongs to. One decoder, unchanged.

The walk is disjoint by construction: each fetch begins where the last cached one ended, so an
event is stored in exactly one entry. That is what separates this from prefix addressing
(`/events/0-99`, `/events/0-136`, `/events/0-137` …), which is also immutable and costs
fiftyfold duplication for it.

`/events/head` is the one mutable resource and the one that has to be fetched: it makes the
HTTP leg self-sufficient. Today the feed cannot start without an `EventsAvailable` hint over
the data channel, which is why nothing asks when there is no transport
([`App.fs:436`](../../src/Yession.App/App.fs)). With a head pointer the hint becomes an
optimisation — it says *there is more now* — rather than the only way to begin.

## Where the bytes live, and the one thing the cache cannot do

The chain makes the browser's HTTP cache a workable event store: the tail is cacheable at the
length it was seen, enumeration is the chain rather than a guess, and a failed step names
exactly the offsets that are missing — so a dead feed is still distinguishable from an empty
one, which the guess-walk could not manage.

What it cannot do is survive eviction gracefully, and the chain makes that sharper rather than
softer: the next URL is only learnable from the entry that carries it, so an evicted *middle*
entry costs not that entry but everything after it. An offset-keyed store degrades to a hole;
a chain degrades to a truncation.

The proportionate answer is to keep the *index* rather than the bytes — the URLs walked, in
`localStorage`, a few hundred bytes per session, keyed by session id like the doc store
([`Browser.fs:410`](../../app/browser/Browser.fs)):

```text
yession/session/<id>/chain   ["events/0-99", "events/100-136", "events/137-212"]
```

A missing entry is then skippable: the walk continues at the next known URL, the gap is a
`FeedFault` naming its offsets, and the model shows a hole instead of a truncation. It is a
fraction of an events store's cost and it holds no session content.

**Decision, and it is the plan's one open one:** ship the chain plus the index, and treat a
full IndexedDB event store as the fallback if measurement shows the HTTP cache evicting under
real use. The cache cannot be asked to persist (`navigator.storage.persist()` reaches
script-writable storage, not it), so this is an empirical question, not an argument — and the
chain is worth having under either answer, because it is the fetch protocol, not the storage.

## Where it composes, and what stays ignorant

Per "composition at the top": the walk is wired in `app/browser/Browser.fs`, and no application
code learns where bytes come from — `EventFeed` is unchanged in shape.

```fsharp
/// The chain this client has walked, oldest step first. Total, like the feed: an
/// index that cannot be read is empty, and the client walks from `head` instead.
type ChainIndex =
    { Read   : unit -> Async<string list>
      Append : string -> Async<unit> }
```

Two seams use it, and they are deliberately separate:

**1. The feed follows links instead of computing addresses.** `EventFetch.overHttp` currently
turns an offset into `/events/{EventChunk.indexOf n}` ([`App.fs:291`](../../src/Yession.App/App.fs));
it instead follows the `next` link the last response carried, falling back to a client-minted
range when it has none (a resume, or a `head` it has just read). The chain index is written as
each step settles, *inside* the resilience guard, so only settled steps are recorded.

**2. A replay pump, before and independent of the transport.** Walk the chain from
`events/0-…`, dispatch each step as a page, stop at the first URL that is neither cached nor
reachable. It is not part of the read loop and must not be: the read loop chases a moving
remote end, and a replay has no remote end to chase.

The replay runs *unconditionally at boot*, before the `/me` probe and regardless of its
outcome. That is the fix for the third broken thing: the probe decides whether this client may
*connect*, not whether it may read what it has already been given.

Note what this does to the fetches themselves — they are the same fetches, hitting the same
cache, so `cache: 'force-cache'` on a range URL is a hit or an honest miss and never a
revalidation the network has to answer.

## Fetching from the last message onwards falls out

Nothing new is needed for the resume. `ConnectOptions.ReadPosition` is already the model's
`LastProcessedOffset` ([`Browser.fs:1217`](../../app/browser/Browser.fs)), and the read loop
already asks the model rather than its own bookkeeping — for precisely this class of reason
([`App.fs:236`](../../src/Yession.App/App.fs)). Replay first, and the first *network* request
the loop makes is the range beginning one past where the chain ended — from `/events/head`, or
from the `EventsAvailable` hint if a transport got there first. One step, usually small.

That ordering is the only new invariant, and it is worth a test of its own: **a client that
replayed must never re-fetch what it replayed.**

## One fold, two things to say about the feed

`EventsPageMsg` sets `Feed = FeedLive`, because a page arriving over HTTP *is* the feed working
([`Model.fs:993`](../../src/Yession.App/Model.fs)). A step served out of the cache with no
network behind it is not, and must not claim to be — an offline client would report a live
history feed.

So: `LocalHistoryMsg of EventPage<SessionEvent>`, folding through the same offset-gated
projection code as `EventsPageMsg` (extract the fold, keep the two cases differing only in what
they conclude about `FeedHealth`). It leaves `Feed` untouched and does not advance
`LatestKnownOffset` beyond its own high-water mark — offline, "how far does the log go" is
genuinely unknown, and the model should say so rather than assert that it is up to date.

## What the chunk index was for, and why it goes

`/events/{index}` and its `EventChunk.indexOf` / `firstOffset` arithmetic exist to give the log
cacheable addresses. A range does that strictly better, so the index is deleted rather than kept
beside it — per "no belt-and-braces", the redundant spare is the one that hides which path is
live. `EventChunk.size` survives as what it always was underneath: the cap on how much one step
carries.

Three consequences worth stating before they are discovered:

- **`SessionRoute.Events of index: int` becomes `Events of from: int64 * until: int64`**, with
  the parse rejecting an inverted or over-long range. `TerminalTranscript` keeps its index until
  the transcript follow-up gives it the same treatment.
- **`Cache-Control` becomes one policy, not two.** Every range response is
  `private, max-age=31536000, immutable`, because every range response *is* immutable — the
  `isFull` branch that made the tail `no-store` ([`EventLog.fs:40`](../../src/Yession.Domain/EventLog.fs))
  has nothing left to distinguish. `/events/head` is the only `no-store` response the surface
  has, and it carries no events.
- **The session id has to reach the address.** An HTTP cache keys on origin + path, and the
  zero-config default addresses a session as `http://127.0.0.1:{port}` with the port moving per
  launch (Plan 12) — so a recycled port would serve the *previous* session's `/events/0-99`,
  for a year. Path-mounted deployments already scope it; the default must too, or the year-long
  entry is a correctness bug rather than a saving. This is not optional and it is not a
  follow-up.

## Why not just raise `max-age` and leave the rest

Worth recording, because it is the first thing anyone will ask and two thirds of it is right.
Raising the full chunk's `max-age` and fetching `force-cache` genuinely does serve history
offline — a fresh cache entry needs no network — and it costs one constant.

What it cannot do is the tail: a partial chunk grows, so caching it pins a truncated log at a
live address for a year, unfixable from the server. A session that has not reached 100 events
lives entirely in chunk 0, partial for ever, cached never — which is most sessions, and exactly
the one somebody opens and finds empty. And a cache cannot be enumerated, so an offline client
would guess-walk `0, 1, 2 …` and read a miss as a network error, indistinguishable from a
genuine fault — collapsing the distinction the feed was deliberately built to keep (design.md
§2.3).

The chain answers both, which is why it is the plan rather than the header bump: a range is
immutable *including* when it ends mid-chunk, and a link is an enumeration. The third objection
— cache keys are not session-scoped — the chain does *not* answer, which is why the address
change above is part of it.

The rejected alternative is prefix addressing (`/events/0-99`, `/events/0-136`, `/events/0-137`
…), also immutable, and duplicating every event once per length at which it was ever observed.
Disjoint ranges cost each event exactly once.

Note what none of this buys back: causes 2 and 3 above — nothing asks, and the feed is
constructed inside the probe's success branch — have to be fixed under any addressing scheme.
That is PR 2, and it is where most of the client work is.

**If value is wanted before PR 1 lands**, the `max-age` bump plus `force-cache` plus the boot
walk is an honest interim that helps long sessions, and PR 1 deletes it. It does nothing at all
for a session under 100 events.

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

It caches **nothing else**. Not `/events/*` — the ranges are immutable and the browser's own
cache already holds them, so a service worker copy would be the redundant spare, and one that
answers `/events/head` from cache would be actively wrong. Not `/me`, `/signal`, `/claude*`,
`/queries` — those are liveness questions, and a cached answer to "can I reach this session" is
a wrong answer.

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

- **A range is served only when the log reaches its end.** Ask for `100-136` against a log of
  120 and get a 404, not 21 lines — the property that keeps a truncated answer out of an
  `immutable` cache entry. Regressed to red by serving the short body and watching it pass
  everything else.
- **The chain is disjoint and total**: following `next` from `0-…` yields every offset exactly
  once, and the last step of a chain walked against a growing log is the one `head` points past.
- **Resume**: replay a chain covering `0..41`, then a transport advertising latest `47`, and
  assert the network was asked for `42-47` and nothing below it — the plan's central promise,
  pinned on the feed rather than observed through the UI.
- A missing middle entry is a `FeedFault` naming its offsets and the walk continues from the
  index, rather than the chain truncating there.
- `LocalHistoryMsg` folds the conversation without reporting `FeedLive`, and does not assert a
  `LatestKnownOffset` it has no evidence for.
- Fold idempotence across a replayed step and a network page covering the same offsets (the
  offset gate already guarantees it; this pins that replay goes through the gate).

`Browser` tier — Chromium only, no host machinery:

- **Availability under fault**: a session with messages, reloaded with the network cut, still
  shows those messages — *including ones in the partial tail*, which is the case the old chunk
  policy could not hold — and still has a working composer. Scoped to the timeline element, and
  regressed-to-red before it is believed: a bare page-level `Contains` would pass off the roster
  while the timeline stayed empty.
- The shell loads cold with no network (service worker), and the retry affordance is present and
  keyboard-operable.

Not tested: which element the loader is, what the strip says, or any class token. Those are the
design changing, which is what a design is for.

## Delivery

Four PRs, each shippable alone, in this order — because each one is worth something without the
next, and the reverse is not true.

| | | |
|---|---|---|
| 1 | **Ranges, links, and a head.** `Events of from * until`, one immutable cache policy, the 404 on an unreached range, `/events/head`, the session id in the address. Server and route only — the existing client keeps working by minting the ranges it used to mint indices. | The tail becomes cacheable, which is the whole symptom. |
| 2 | **The walk.** Follow `next`, keep the chain index, replay from zero at boot above the `/me` probe, `LocalHistoryMsg`. | A session unreachable with the network up (asleep, reaped) reads back in full. |
| 3 | **Retrying that does not give up.** `online` re-arm, supervised reconnect, manual retry, GAPS entry closed. | Recovery without a reload. |
| 4 | **The loader.** `HistoryRestore`, the empty-timeline split, the strip's restoring state. | The empty screen stops lying. |
| 5 | **The service worker.** Shell and fingerprinted assets, scoped to the mount. | A cold open with no network at all. |

Splitting 1 from 2 is deliberate: the wire change is testable on its own (the 404 property, the
chain's disjointness, the single cache policy) and lands without touching the client's read
loop, so a regression in either half is attributable.

Docs ride the PR that makes them true: design.md §2.3 describes the chunk-index scheme and stops
being true in PR 1; the GAPS entries for the missing shell and the missing manual retry close in
PRs 5 and 3.

## Risks & open questions

- **Eviction is the open question, and it is empirical.** The HTTP cache cannot be asked to
  persist. If measurement shows real-use eviction, the fallback is an IndexedDB store holding
  the envelope lines keyed by offset, replayed by the same pump — the chain stays either way,
  because it is the fetch protocol and not the storage. Do not build both.
- **A chain step is a fetch.** Replaying 5 000 events is 50 cache hits rather than one scan.
  Fine at that size, and bounded by the same thing the model is; worth a number before anyone
  assumes it stays fine.
- **Cache entries accumulate per session** and nothing drops them when a session is deleted. The
  browser evicts on its own terms, which is the one place eviction is a feature — but it means
  history sits in a profile with no sweep, exactly as it does today.
- **Ephemeral deployments strand it, exactly as they already strand the doc.** Caches and
  storage are both origin-partitioned, so a session that reopens on a new port starts cold. No
  new problem and no new promise: `EphemeralStorage` already changes what the degraded strip
  says.
- **`online` lies sometimes.** It reports a link, not reachability. It is a *trigger* for an
  attempt that can fail, never a claim that the session is back.

## Later, deliberately not now

- **Terminal transcripts**, which are the same shape one level over: chunk-indexed, `no-store`
  at the tail, and lost on reload. Same ranges, same links, same argument — after the event log
  has proven it.
- **Writing offline.** Out of scope by design: drafts and the queue are CRDT state in the local
  doc and already work offline; a *sent* message is a durable event, and the Session Process is
  the only writer of those.
