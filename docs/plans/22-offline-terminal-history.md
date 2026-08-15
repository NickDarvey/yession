# Plan 22 — The terminal survives the network too

> **Status: proposed.** Plan 20 gave the conversation a cursor, an address that names its own
> bounds, and a store the client reads before it asks anyone anything. The terminals were left
> on the scheme plan 20 replaced: a client computes a chunk index, asks for it by number, and
> keeps nothing of its own. Open a session offline and the conversation is there; the terminal
> under it is empty.
>
> This plan applies the same three moves to the transcript feed — cursor, range, store — and
> nothing else. It is deliberately not a new idea.

## What is still on the old scheme

| | |
|---|---|
| **The client computes the address.** `TranscriptFetch.overHttp` calls `TranscriptChunk.indexOf` on the seq it wants, then names `terminals/{t}/{index}`. Every fact about how the server groups lines — that there is a group, that it is 500 lines, that it starts at a multiple of 500 — lives in the client. | [`App.fs:215`](../../src/Yession.App/App.fs) |
| **The tail is `no-store`.** A partial chunk is uncacheable, so the newest *n* < 500 lines of every terminal exist only on the network. A terminal that never filled a chunk — nearly all of them — has no cached history at all. | [`Transcript.fs:197`](../../src/Yession.Domain/Transcript.fs) |
| **Nothing is kept.** The full chunks that ARE cacheable go into the HTTP cache, which plan 20 established is the wrong store: the client cannot enumerate it, so it cannot read its history without first knowing what to ask for. | [`Signalling.fs:246`](../../app/Signalling.fs) |
| **Nothing asks.** `fetchTranscript` runs on an availability hint or a live record — both of which arrive over the data channel. Offline, neither happens, so even a populated store is never drained. | [`App.fs:657`](../../src/Yession.App/App.fs) |

The same four facts plan 20 removed from the event log, in the same order, for the same reasons.
The correctness argument transfers whole: a transcript is append-only, the Session Process is its
only writer, and line index IS sequence number for ever ([`TranscriptStore.fs:8`](../../app/TranscriptStore.fs))
— so an address naming lines `[a, b]` returns the same bytes for ever.

## The wire

```http
GET terminals/{t}                      → 307  Location: terminals/{t}/0-499     (no cursor: the start)
GET terminals/{t}/after/499            → 307  Location: terminals/{t}/500-612
GET terminals/{t}/500-612              → 113 lines                              (immutable bytes)
GET terminals/{t}/after/612            → 204  No Content                        (you are current)
```

`terminals/{t}/keyframes/{n}` does not change. A keyframe is already addressed by a position that
never moves, which is the property everything else on this surface is being given.

The old `terminals/{t}/{index}` is removed rather than kept beside the new form. Two ways to ask
for the same lines is the belt-and-braces the repository forbids: the spare rots, and the next
failure is an archaeology dig into which one the client actually used.

## The one number the client still computes, and why it is not an address

An event envelope carries its own offset, so plan 20's client never had to number anything — it
read the offsets out of the payload and a gap announced itself. A transcript line does not carry
its index, and it must not start: the file is an asciicast, the format is the whole reason plan 13
chose it, and a line with a private index field in it is not one.

So the client numbers an answer from **what it asked**, never from where the answer lives:

> The answer to `after n` begins at line `n + 1`. `after` nothing begins at line 0.

That is a server contract, not a client guess, and it is what `BoundsAfter` already does — it
reports the bounds of what this caller has not seen, whose first line is one past the caller's
cursor by construction. It gets one test of its own, because it is the single assumption holding
the numbering up.

Everything else the client used to know goes: `TranscriptChunk.indexOf`, `firstSeq` and
`cacheControl` are deleted, and `size` survives only as the server-side cap on how much one answer
may carry — the same reduction `EventChunk` took in plan 20.

## The store

One cache per terminal, named `<session>/terminals/<id>` beside the existing `<session>/events`.

Not one cache with everything in it, and the reason is the replay. `keys()` answers in insertion
order, which is fetch order, which is ascending — that is what lets the event replay be "read them
in order" with no sorting and no address parsing. Two terminals' answers in one cache interleave by
whichever fetched first, and a walk over that would have to ask, of every entry, whose it is: which
means parsing an address. The cache name answers it instead, and answers it before the walk starts.

**What is kept is the bytes and the number the client asked for.** The stored entry carries its
first seq, because the bytes cannot say it and the address must not be asked. In the browser that
rides on the stored `Response`'s headers, which the Cache API round-trips for free; the store's
shape says it out loud:

```fsharp
type TranscriptCache =
    { Stored : unit -> Async<string list>
      Read   : string -> Async<(int * string) option>   // first seq, then lines
      Write  : string -> int -> string -> Async<unit> }
```

A gap is then noticed exactly where plan 20 notices one — in the sequence, not in the addresses. An
entry evicted from the middle leaves the rest in the store, so the walk reaches an answer whose
first seq is not one past the last folded, and the drain for THAT terminal stops there with its
cursor sitting where the fill has to start. A hole in one terminal costs that terminal's tail and
nothing else.

## The replay runs after the events

A terminal exists because an event said so. Records folded before that event has been folded have
nowhere to land, so the drain runs after `EventFetch.replay` has finished, not beside it.

Which terminals to drain is read from the store's own names — `caches.keys()` under this session's
prefix — and not from the model. Asking the model would be asking a thing this same startup path
has only just filled, and would make the order of two replays load-bearing in a second place.

## Steps

**Step 1 — the wire.** `TerminalTranscriptAfter` and `TerminalTranscriptRange` replace
`TerminalTranscript`; `serveTranscriptCursor` / `serveTranscriptRange` join their event
counterparts in `Signalling.fs`; `TranscriptEndpoint` gains `BoundsAfter` and a raw-line
`ReadRange`, backed by the store; `TranscriptFetch.overHttp` sends its cursor and numbers the
answer from it. `TranscriptChunk` is reduced to `size`.

Tests: the routes round-trip; a cursor redirects to a range and a current cursor is `204`; a range
the transcript has not reached is `404` rather than a short answer at an address that promised the
whole thing; the answer to `after n` starts at `n + 1`; the header still arrives as line 0.

**Step 2 — the store and the replay.** The per-terminal cache, the storing wrapper, the drain after
the event replay, and the gap that stops one terminal's walk.

Test: the plan 20 acceptance case, extended. The session process is gone, the page is still there,
its conversation is still on it — and so is what the terminal printed.
