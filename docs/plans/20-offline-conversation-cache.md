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
                                         Link: </events/100-136>; rel="next"

GET /events/100-136                    → 37 lines, no `next` (it was the end when asked)

GET /events/head                       → no lines
                                         Link: </events/137-212>; rel="next"
```

Every one of them is `no-store`: the client keeps what it is given, deliberately, in a store it
can enumerate — see below. The header would only invite a second copy.

Three rules, and each one is load-bearing:

- **A range is served only if the log reaches its end**, else 404. The URL is guessable, and a
  guess answered with a *short* body would be kept for ever as if it were the whole range —
  wrong until someone clears it, and unfixable from the server. That is exactly
  the hazard `serveAsset` 404s to avoid ([`Signalling.fs:49`](../../app/Signalling.fs)); same
  argument, same answer.
- **The server mints every link**, capped at `EventChunk.size` events, so a chain step is
  bounded and a client never has to choose a range. Client-minted ranges are validated against
  the same cap.
- **`Link: rel=next` is a header**, so the body stays pure JSONL and the pointer is stored with
  the bytes it belongs to — the Cache API keeps whole responses, headers included. One decoder,
  unchanged.

The walk is disjoint by construction: each fetch begins where the last cached one ended, so an
event is stored in exactly one entry. That is what separates this from prefix addressing
(`/events/0-99`, `/events/0-136`, `/events/0-137` …), which is also immutable and costs
fiftyfold duplication for it.

`/events/head` is the one mutable resource and the one that has to be fetched: it makes the
HTTP leg self-sufficient. Today the feed cannot start without an `EventsAvailable` hint over
the data channel, which is why nothing asks when there is no transport
([`App.fs:436`](../../src/Yession.App/App.fs)). With a head pointer the hint becomes an
optimisation — it says *there is more now* — rather than the only way to begin.

## Where the bytes live: the Cache API, not the HTTP cache

The chain would work over the browser's own HTTP cache — that is what the immutable ranges are
for — and it should not live there, because of the one property no header can express.

`max-age=31536000, immutable` says *reuse this without asking me*. It does not say *keep this*,
and there is no way to say it. The HTTP cache is a bounded per-profile pool shared with every
other site, reclaimed roughly LRU under pressure, wiped by "Clear browsing data → Cached images
and files" — the checkbox people tick casually, and a different one from the site-data checkbox
they tick rarely. It cannot be enumerated and it cannot be asked to persist.

The Cache API has none of those limits and is available to the page directly (`window.caches`
in a secure context — no service worker involved, which is why the shell's worker stays where
it was in the delivery order):

```fsharp
caches.open (sprintf "yession/events/%s" (SessionId.value session))
```

Four properties, and each one deletes something this plan previously had to carry:

- **It stores `Response` objects** — verbatim bytes *and* the `Link` header. Same decode path,
  no second wire format, nothing to migrate when a projection changes.
- **`cache.keys()` is the index.** The chain is enumerable without the client keeping a
  `localStorage` list of URLs beside it, so the walk knows the whole chain up front and an
  evicted middle step is skippable rather than terminal.
- **`navigator.storage.persist()` covers it.** Requested once, best-effort, alongside the doc
  store's identical exposure.
- **The cache NAME carries the session id**, which closes the recycled-port collision inside
  the client instead of relying on the address. The session id still belongs in the URL (a
  shared cache and a Node client have no cache name to read), but the browser stops depending
  on it.

Under it sits the same fetch, so a step that is not in the cache goes to the network and an
offline miss is `HttpUnreachable` naming the range it wanted.

**One deployment loses this, and the answer is a certificate rather than a second store.**
`caches` requires a secure context. Loopback is one, so the zero-config default
(`http://127.0.0.1:{port}`) is fine, and so is every `https://` mount. What is left is a
session reached over plain HTTP at a non-loopback address — which deployment.md documents, as
the tailnet example (`http://host.example.ts.net:8321`).

There, the transport genuinely is encrypted; the browser simply cannot verify it, and withholds
the API on principle. So the fix is the one deployment.md already names — `--https` with a
tailnet certificate — and the plan's job is to stop that configuration failing *silently*. A
client in an insecure context keeps no history and cannot say why, which is indistinguishable
from a bug. It should say so: one line in the settings pane naming the missing capability and
the switch that restores it, gated on `window.isSecureContext`.

What that deployment keeps is most of it: IndexedDB (so the doc store, drafts, queue and title
persist exactly as now), the WebRTC transport, and every part of this plan except the store and
the service worker. What it can never have is the offline cold open — a service worker needs a
secure context and there is no way around that, which is worth saying out loud rather than
leaving as a puzzle.

**Decision:** ship the chain over the Cache API. An IndexedDB store keyed by offset stays the
documented fallback — a *replacement* for this port, never an addition beside it — and the
trigger for building it is **measured eviction, and nothing else**. Insecure contexts are
explicitly not a trigger: a store that only that deployment exercises is the spare that rots
unverified, and it would buy back one PR for a configuration whose real remedy is one flag.

## Where it composes, and what stays ignorant

Per "composition at the top": the walk is wired in `app/browser/Browser.fs`, and no application
code learns where bytes come from — `EventFeed` is unchanged in shape.

```fsharp
/// The steps this client has been given, and where they are kept. Total, like the
/// feed: a cache that cannot be opened reads empty, and the client walks from
/// `head` over the network exactly as it does today.
type HistoryCache =
    { Steps : unit -> Async<string list>          // cache.keys (), oldest first
      Read  : string -> Async<string option>      // a step's body, or a miss
      Write : string -> string -> Async<unit> }   // url -> response, after a settled fetch
```

Two seams use it, and they are deliberately separate:

**1. The feed follows links instead of computing addresses.** `EventFetch.overHttp` currently
turns an offset into `/events/{EventChunk.indexOf n}` ([`App.fs:291`](../../src/Yession.App/App.fs));
it instead follows the `next` link the last response carried, falling back to a client-minted
range when it has none (a resume, or a `head` it has just read). Each step is written to the
cache as it settles, *inside* the resilience guard, so only settled steps are kept.

**2. A replay pump, before and independent of the transport.** Read `Steps ()`, walk them in
order, dispatch each as a page. It is not part of the read loop and must not be: the read loop
chases a moving remote end, and a replay has no remote end to chase.

The replay runs *unconditionally at boot*, before the `/me` probe and regardless of its
outcome. That is the fix for the third broken thing: the probe decides whether this client may
*connect*, not whether it may read what it has already been given.

**A gap stops the fold, not the report.** `Steps ()` names the whole chain, so a step evicted
from the middle is visible as a gap rather than as the end of history. The replay folds
contiguously and stops at the gap — `LastProcessedOffset` is both "shown through" and "resume
from", and advancing it past unread offsets would skip them for ever — but it *reports* what
lies beyond, as a `FeedFault` naming the missing range. The gap is repaired by one network
fetch of exactly that range, after which the walk continues through steps that are still
local. Showing the events past a gap while holding the resume mark below it is a real
improvement and a real complication (two watermarks, and a projection rebuild on repair); it is
in "Later" with the measurement that would justify it.

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
- **`Cache-Control` goes away entirely: every response on this surface is `no-store`.** This
  reads backwards for a plan about caching, and it is the same argument that killed the
  three-day `max-age`, applied honestly to the new design. Ask who reads the header once the
  client keeps its own store: not the browser's HTTP cache, which is bypassed (the range fetch
  is `cache: 'no-store'`, so one copy is kept rather than two, in the store that can be
  enumerated and persisted); not a shared cache, which `private` already excluded; not a Node
  client, which has no cache. Immutability is now expressed where it is actually enforced — the
  bounds in the URL and the 404 on an unreached range — instead of in a header nobody obeys.
- **The session id no longer has to reach the address**, which is the collision fix arriving
  from the other direction. A URL-keyed cache is what makes a recycled port
  (`http://127.0.0.1:{port}`, moving per launch — Plan 12) serve the *previous* session's
  history; with no URL-keyed cache in the path and the store named for the session, there is no
  collision to fix. A live fetch always reaches whichever session is listening, which is the
  right one by definition.

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
immutable *including* when it ends mid-chunk, and a link is an enumeration. The third
objection — cache keys are not session-scoped — is answered by the store's name rather than by
the chain.

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

It caches **nothing else**, and in particular it does not touch `/events/*`: the page owns that
cache directly (`window.caches` needs no worker), so a worker copy would be the redundant
spare, and one that answered `/events/head` from cache would be actively wrong. Nor `/me`,
`/signal`, `/claude*`, `/queries` — those are liveness questions, and a cached answer to "can I
reach this session" is a wrong answer.

That independence is why the worker lands last rather than first. Nothing above it waits on
this, and it is the piece with the most ways to be subtly wrong (an update that strands a
client on an old shell, a scope that swallows a sibling mount) — so it ships when the thing it
would make reachable already works.

Old caches are dropped on `activate`, keyed by the digests the shell names, so a build's assets
do not accumulate. The session's event cache is left alone: it is not the worker's, and its
name is a session id rather than a build.

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
  120 and get a 404, not 21 lines — the property that keeps a truncated answer out of a store
  that keeps it for ever. Regressed to red by serving the short body and watching it pass
  everything else.
- **The chain is disjoint and total**: following `next` from `0-…` yields every offset exactly
  once, and the last step of a chain walked against a growing log is the one `head` points past.
- **Resume**: replay a chain covering `0..41`, then a transport advertising latest `47`, and
  assert the network was asked for `42-47` and nothing below it — the plan's central promise,
  pinned on the feed rather than observed through the UI.
- **A gap is named, not hidden.** With a middle step missing, the replay folds up to it, reports
  a `FeedFault` carrying the missing range, and does not advance the read position past it —
  driven through a fake `HistoryCache` whose `Steps` lists more than its `Read` will answer.
- `LocalHistoryMsg` folds the conversation without reporting `FeedLive`, and does not assert a
  `LatestKnownOffset` it has no evidence for.
- Fold idempotence across a replayed step and a network page covering the same offsets (the
  offset gate already guarantees it; this pins that replay goes through the gate).
- **A shorter server does not truncate a longer client.** A session that answers `head` with a
  log shorter than what this client has replayed leaves the replayed history standing. The
  hazard is real and quiet: a rolled-back `LatestKnownOffset` reads as "you are ahead of the
  log", and the honest answer is that history a client was given does not un-happen.

`Browser` tier — Chromium only, no host machinery:

- The shell loads cold with no network (service worker), and the retry affordance is present and
  keyboard-operable.

`[Browser; Native]` — a real Session Process, in the existing path-mounted fixture
([`Browser.fs:1071`](../../tests/Yession.Tests/Browser.fs)), which already kills the host, wipes
its data directory, restarts it and reloads to prove a *draft* could only have come from the
browser. History is that same test one level over, and it is the plan's acceptance criterion in
two halves:

- **The session's history is the client's (PR 2).** Send messages, kill the host, delete its
  data directory, restart it, reload. The messages are still in the timeline — and they can only
  have come from the client, because the server's copy was deleted. The same argument the draft
  case already makes, applied to durable events, which today vanish. It runs before the service
  worker exists because the restarted host still serves the shell.
- **The session is down and the history is still there (PR 5).** Send messages, kill the host,
  and *leave it dead*. Reload. The page loads at all (the worker's cached shell), the timeline
  shows the history, and the client says it is retrying rather than saying the conversation is
  empty. This is the bug report, verbatim, and it cannot go green before PR 5 — the shell is
  `no-cache` and a dead host cannot answer a revalidation, so until the worker lands there is no
  page to assert about.

Both assert into `[data-conversation]` rather than the page, and both are regressed to red
first: a bare page-level `Contains` is satisfied by the roster while the timeline sits empty,
which is the exact failure mode a loosened UI assertion produces.

One more, cheap to add where the fixture already stands: **the store is the store** — after a
load, the session's cache holds the chain and the HTTP cache holds none of it. Readable from
`caches.keys()` and Resource Timing (`transferSize`), and it is what keeps a second copy from
creeping back in.

Not tested: which element the loader is, what the strip says, or any class token. Those are the
design changing, which is what a design is for.

## Delivery

Five PRs, each shippable alone, in this order — because each one is worth something without the
next, and the reverse is not true.

| | | |
|---|---|---|
| 1 | **Ranges, links, and a head.** `Events of from * until`, the 404 on an unreached range, `/events/head`, `no-store` everywhere. Server and route only — the existing client keeps working by minting the ranges it used to mint indices. | The tail becomes fetchable at a stable address, which is the whole symptom. |
| 2 | **The walk and the store.** `HistoryCache` over the Cache API, follow `next`, replay from zero at boot above the `/me` probe, `LocalHistoryMsg`, gaps named, and the insecure-context line that says why there is no store. | A session unreachable with the network up (asleep, reaped) reads back in full. |
| 3 | **Retrying that does not give up.** `online` re-arm, supervised reconnect, manual retry, GAPS entry closed. | Recovery without a reload. |
| 4 | **The loader.** `HistoryRestore`, the empty-timeline split, the strip's restoring state. | The empty screen stops lying. |
| 5 | **The service worker.** Shell and fingerprinted assets, scoped to the mount. Nothing else depends on it. | A cold open with no network at all — and the bug report's own test goes green. |

Splitting 1 from 2 is deliberate: the wire change is testable on its own (the 404 property, the
chain's disjointness, the single response policy) and lands without touching the client's read
loop, so a regression in either half is attributable.

Docs ride the PR that makes them true: design.md §2.3 describes the chunk-index scheme and says
the browser's HTTP cache is the client-side event store — both stop being true across PRs 1 and
2; the GAPS entries for the missing shell and the missing manual retry close in PRs 5 and 3.

## Risks & open questions

- **Eviction is still the open question, and it is still empirical.** `storage.persist()` is a
  request, not a guarantee: granted on Chrome for engaged sites, granted on Safari essentially
  only for an installed home-screen app, and Safari additionally caps script-writable storage at
  seven days without user interaction — which reaches the Cache API. So the exposure is the
  session nobody has opened in a week, which is also the one somebody most wants back. Instrument
  it: a walk knows how many steps it served locally and how many went to the network, and that
  ratio belongs on the OTel resource. If it says the store is not holding, the replacement is an
  IndexedDB store keyed by offset behind the same `HistoryCache` port. Do not build both.
- **No secure context, no store — but it is a flag, not a fallback.** Loopback and every
  `https://` mount are secure contexts; a non-loopback plain-HTTP session (deployment.md's
  tailnet example) is not, and keeps today's behaviour exactly. The port is total, so this
  degrades rather than breaks, and the remedy is `--https` with a tailnet certificate. What is
  irreducible there is the cold offline open: no service worker without a secure context, no
  exceptions.
- **A chain step is a fetch.** Replaying 5 000 events is 50 local reads rather than one scan.
  Fine at that size, and bounded by the same thing the model is; worth a number before anyone
  assumes it stays fine.
- **Caches accumulate per session** and nothing drops one when a session is deleted. Named by
  session id, so a sweep is possible the moment a surface knows which sessions are gone —
  `caches.keys()` makes it a filter rather than an archaeology dig.
- **Ephemeral deployments strand it, exactly as they already strand the doc.** Cache storage is
  origin-partitioned, so a session that reopens on a new port starts cold. No new problem and no
  new promise: `EphemeralStorage` already changes what the degraded strip says.
- **`online` lies sometimes.** It reports a link, not reachability. It is a *trigger* for an
  attempt that can fail, never a claim that the session is back.

## Later, deliberately not now

- **Showing events past a gap.** The replay stops folding at a missing step, so an evicted
  *early* step costs the whole session offline even though the rest is local. Fixing it means
  splitting `LastProcessedOffset` into "shown through" and "resume from", and rebuilding the
  projection when the gap is filled. Worth doing if — and only if — the eviction telemetry says
  gaps happen at all.
- **Terminal transcripts**, which are the same shape one level over: chunk-indexed, `no-store`
  at the tail, and lost on reload. Same ranges, same links, same store, same argument — after
  the event log has proven it.
- **Writing offline.** Out of scope by design: drafts and the queue are CRDT state in the local
  doc and already work offline; a *sent* message is a durable event, and the Session Process is
  the only writer of those.
