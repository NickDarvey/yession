module Yession.Tests.EventsHttp

// The event log over HTTP, read by CURSOR (Plan 20): a client sends the offset it
// has folded through (`GET /events/after/{n}?token=…`) and the server redirects to the
// range it chose (`/events/{first}-{last}`), whose bytes never change because its bounds
// do not. Verified here: that a range keeps answering the same bytes after the log has
// grown past it (the tail is the case the old fixed-chunk scheme could not hold), that a
// range the log has not reached is a 404 rather than a short answer, that a current caller
// gets `204`, token gating on both halves, and a full client consuming the log over the
// HTTP fetcher instead of frames.

open System
open Fable.Core
open Fable.Pyxpecto
open Ylmish
open Yession.Domain
open Yession.App
open Yession.Host
open Yession.Tests.Support

// A GET that exposes status + cache header alongside the body (Node 24 global fetch).
type private HttpReply =
    abstract status : int
    abstract cacheControl : string
    abstract body : string

[<Emit("fetch($0).then(async r => ({ status: r.status, cacheControl: r.headers.get('cache-control') || '', body: await r.text() }))")>]
let private httpGet (url: string) : JS.Promise<HttpReply> = Fable.Core.Util.jsNative

// The same GET with redirects left unfollowed, so the cursor's own answer is observable
// rather than the range's. `location` is empty on anything that is not a redirect.
type private RedirectReply =
    abstract status : int
    abstract cacheControl : string
    abstract location : string

[<Emit("fetch($0, { redirect: 'manual' }).then(r => ({ status: r.status, cacheControl: r.headers.get('cache-control') || '', location: r.headers.get('location') || '' }))")>]
let private httpGetRaw (url: string) : JS.Promise<RedirectReply> = Fable.Core.Util.jsNative

// The chunk GET shaped as `Client.HttpGet` is — total, with the status on a refusal. Identical
// to the browser's port (app/browser/Browser.fs); failure classification is covered in
// Resilience.fs, so here it only has to be the real thing.
[<Emit("""fetch($0).then(
  async r => r.ok ? { ok: true, status: r.status, url: r.url, detail: await r.text() } : { ok: false, status: r.status, url: r.url, detail: '' },
  e => ({ ok: false, status: 0, url: '', detail: String(e) }))""")>]
let private fetchChunk (url: string) : JS.Promise<{| ok: bool; status: int; url: string; detail: string |}> = Fable.Core.Util.jsNative

let private chunkGet : Client.HttpGet =
    fun url ->
        async {
            let! reply = fetchChunk url |> Async.AwaitPromise
            return
                if reply.ok then Ok { Url = reply.url; Body = reply.detail }
                elif reply.status = 0 then Error (Client.HttpUnreachable reply.detail)
                else Error (Client.HttpStatus reply.status)
        }

let private endpointTests =
    testList "HTTP endpoint" [
        testCaseAsync "a cursor redirects to a range whose bytes never change, token-gated" <|
            async {
                let! h = Host.start (SessionId.create "events-http" |> expect) 0
                let mintedToken = h.MintPeerToken ()
                let append (n: int) =
                    async {
                        for i in 1 .. n do
                            let! _ =
                                h.Log.Append
                                    ActorRef.SessionProcess
                                    (PeerJoined
                                        { PeerId = PeerId.create (sprintf "p-%d-%d" n i) |> expect
                                          DisplayName = "filler"
                                          User = None })
                            ()
                    }
                // Two answers' worth plus a five-event tail: the tail is the whole point,
                // because it is what a fixed chunk index could never give an address to.
                do! append (2 * EventChunk.size + 5)
                let at (route: SessionRoute) (token: string) =
                    sprintf "http://127.0.0.1:%d/%s?token=%s" h.Port (SessionRoute.relative route) token
                let offset (n: int64) = EventOffset.create n |> expect

                // The cursor itself: no events, never cached, and it says where to look.
                let! start = httpGetRaw (at (EventsAfter None) mintedToken) |> Async.AwaitPromise
                Expect.equal start.status 307 "a cursor redirects rather than answering"
                Expect.equal start.cacheControl "no-store" "where the events are is a thing that moves"
                Expect.stringContains start.location (sprintf "events/0-%d" (EventChunk.size - 1)) "to the first range"
                Expect.stringContains start.location "token=" "carrying the token, which a redirect would otherwise drop"

                // Following it lands on the events.
                let! first = httpGet (at (EventsAfter None) mintedToken) |> Async.AwaitPromise
                Expect.equal first.status 200 "the range serves"
                Expect.equal first.cacheControl "no-store" "the client keeps this, not the HTTP cache"
                let lines (body: string) = body.Split '\n' |> Array.filter (fun l -> l.Trim().Length > 0)
                Expect.equal (lines first.body).Length EventChunk.size "one answer's worth"
                let decoded = Codec.fromString Codec.sessionEventEnvelope (lines first.body).[0] |> expect
                Expect.equal (EventOffset.value decoded.Offset) 0L "starting at the beginning"

                // The tail, at an address of its own — and still five events after the log
                // has grown past it. THIS is what the chunk index could not do: `events/2`
                // meant "whatever chunk 2 holds now", so the newest events were unkeepable.
                let tailFirst = int64 (2 * EventChunk.size)
                let tailRange = Events (tailFirst, tailFirst + 4L)
                let! tail = httpGet (at tailRange mintedToken) |> Async.AwaitPromise
                Expect.equal tail.status 200 "the tail has an address"
                Expect.equal (lines tail.body).Length 5 "and five events in it"
                do! append 10
                let! tailAgain = httpGet (at tailRange mintedToken) |> Async.AwaitPromise
                Expect.equal tailAgain.body tail.body "the same address answers the same bytes after the log grew"

                // A range the log has not reached is a 404, never a short answer: a partial
                // body here would be kept for ever as if it were the whole range.
                let! unreached = httpGet (at (Events (10_000L, 10_009L)) mintedToken) |> Async.AwaitPromise
                Expect.equal unreached.status 404 "a range beyond the log does not exist yet"

                // Current: nothing to keep, so nothing to give an address to.
                let! current = httpGetRaw (at (EventsAfter (Some (offset (2L * int64 EventChunk.size + 14L)))) mintedToken) |> Async.AwaitPromise
                Expect.equal current.status 204 "a caller at the end is told it is current"
                Expect.equal current.cacheControl "no-store" "and emptiness is never kept"

                let! wrongToken = httpGet (at (EventsAfter None) "stolen") |> Async.AwaitPromise
                Expect.equal wrongToken.status 401 "the cursor is gated on minted tokens"
                Expect.equal wrongToken.cacheControl "no-store" "rejections never cache"

                let! wrongTokenRange = httpGet (at (Events (0L, 9L)) "stolen") |> Async.AwaitPromise
                Expect.equal wrongTokenRange.status 401 "and so are the events themselves"

                let! bare = httpGet (sprintf "http://127.0.0.1:%d/events" h.Port) |> Async.AwaitPromise
                Expect.equal bare.status 401 "no cookie and no token is unauthorized"

                let! notARange = httpGet (sprintf "http://127.0.0.1:%d/events/nope?token=%s" h.Port mintedToken) |> Async.AwaitPromise
                Expect.equal notARange.status 404 "an unparseable range is not a route"
                do! h.Stop ()
            }

        Tag.needs "HTTP fetcher client" [ Tag.Ports; Tag.Native ] (fun () ->
            testCaseAsync "a client consuming over the HTTP fetcher builds the same timeline as the frame path" <|
            async {
                let! h = Host.start (SessionId.create "events-http-client" |> expect) 0
                let mintedToken = h.MintPeerToken ()
                let baseUrl = sprintf "http://127.0.0.1:%d" h.Port
                let signalUrl = SessionRoute.at baseUrl Signal
                let options =
                    { Client.ConnectOptions.defaults with
                        FetchEvents =
                            Some (Client.EventFetch.overHttp chunkGet (SessionRoute.at baseUrl) (Some mintedToken)) }
                let! a = connectClientWith options signalUrl mintedToken "ada" "Ada"

                do! compose a a.Hello.PeerId "fetched over http"
                a.Connection.SendDraft a.Hello.PeerId
                do! a.Runner.WaitFor (fun m ->
                        not m.EventConsumer.IsCatchingUp
                        && (m.Conversation.Items |> List.map (fun i -> i.Body)) = [ "fetched over http" ])
                do! a.Channel.Close ()
                do! h.Stop ()
            })
    ]

// --- What a client keeps, and what it does with it (Plan 20, step 2) ----------------------
// The store is a port, so these need no browser: a dictionary standing in for the Cache API
// answers the same three questions, and what is under test is the client's reasoning about
// what it holds — replay it in order, notice a hole, and ask the network only for what is
// missing.

/// A `HistoryCache` over a list, ordered the way the Cache API orders `keys()`: insertion
/// first, which is fetch order, which is ascending.
let private storeOf (entries: (string * string) list) =
    let kept = ResizeArray entries
    // Type-qualified: `Read` is a field name several records in the domain share, and an
    // unqualified one would resolve to whichever was declared last. `HistoryCache` carries
    // `RequireQualifiedAccess`, so the record TYPE has to be named, not just its module.
    { Client.HistoryCache.Stored = fun () -> async.Return (kept |> Seq.map fst |> List.ofSeq)
      Client.HistoryCache.Read = fun url -> async.Return (kept |> Seq.tryPick (fun (u, body) -> if u = url then Some body else None))
      Client.HistoryCache.Write = fun url body -> async { kept.Add (url, body) } }

/// One kept answer: the JSONL the server served for offsets [first, last].
let private answerOf (first: int64) (count: int) =
    let line (offset: int64) =
        Codec.toString
            Codec.sessionEventEnvelope
            { EventId = EventId.fresh ()
              SessionId = SessionId.create "kept-history" |> expect
              Offset = EventOffset.create offset |> expect
              Actor = ActorRef.SessionProcess
              Timestamp = System.DateTimeOffset.FromUnixTimeSeconds 0L
              Event =
                PeerJoined
                    { PeerId = PeerId.create (sprintf "p-%d" offset) |> expect
                      DisplayName = sprintf "peer %d" offset
                      User = None } }
    let last = first + int64 count - 1L
    sprintf "events/%d-%d" first last,
    [ first .. last ] |> List.map line |> String.concat "\n"

let private storeTests =
    testList "What a client keeps" [
        testCaseAsync "a replay folds what was kept, in order, with no network at all" <|
            async {
                let store = storeOf [ answerOf 0L 3; answerOf 3L 2 ]
                let seen = ResizeArray ()
                do! Client.EventFetch.replay store seen.Add
                let offsets =
                    seen
                    |> Seq.collect (fun msg ->
                        match msg with
                        | LocalHistoryMsg page -> page.Events |> List.map (fun e -> EventOffset.value e.Offset)
                        | _ -> [])
                    |> List.ofSeq
                Expect.equal offsets [ 0L; 1L; 2L; 3L; 4L ] "every kept event, once, in log order"
            }

        testCaseAsync "a replayed page never claims the feed is live" <|
            async {
                // The distinction the separate message exists for: these events prove this
                // client KEPT them, and nothing whatever about whether the network works. A
                // client replaying offline while reporting a healthy history feed is lying
                // about the one leg that is down.
                let store = storeOf [ answerOf 0L 2 ]
                let seen = ResizeArray ()
                do! Client.EventFetch.replay store seen.Add
                let stalled = { ClientModel.init { PeerId = PeerId.create "p" |> expect; DisplayName = "P" } with
                                  EventConsumer =
                                    { (ClientModel.init { PeerId = PeerId.create "p" |> expect; DisplayName = "P" }).EventConsumer with
                                        Feed = FeedStalled "offline" } }
                let folded = seen |> Seq.fold (fun m msg -> ClientModel.update msg m) stalled
                Expect.equal folded.EventConsumer.Feed (FeedStalled "offline") "the feed's health is untouched by a local read"
                Expect.equal
                    (folded.Conversation.Items |> List.length)
                    0
                    "these are joins, so nothing lands in the conversation — the offsets are the point"
                Expect.equal
                    (folded.EventConsumer.LastProcessedOffset |> Option.map EventOffset.value)
                    (Some 1L)
                    "and the read position moved, which is what the resume reads"
            }

        testCaseAsync "kept answers the store hands back out of order still fold in log order" <|
            async {
                // The store's own order is not ascending and cannot be made to be: the Cache
                // API's `put` of an address already held deletes the entry and appends the new
                // one, so two tabs of one session fetching the same range moves the earliest
                // answer to the end. Walking in that order read a later answer first and
                // called every event before it missing, on a client that held them all.
                let store = storeOf [ answerOf 3L 2; answerOf 0L 3 ]
                let seen = ResizeArray ()
                do! Client.EventFetch.replay store seen.Add
                let offsets =
                    seen
                    |> Seq.collect (fun msg ->
                        match msg with
                        | LocalHistoryMsg page -> page.Events |> List.map (fun e -> EventOffset.value e.Offset)
                        | _ -> [])
                    |> List.ofSeq
                Expect.equal offsets [ 0L; 1L; 2L; 3L; 4L ] "every kept event, once, in log order"
                Expect.isFalse
                    (seen |> Seq.exists (function LocalHistoryGapMsg _ -> true | _ -> false))
                    "and nothing is missing — the events were all here, in a bag rather than a queue"
            }

        testCaseAsync "a hole stops the fold at its edge" <|
            async {
                // An entry evicted from the middle leaves the rest kept. Folding over the gap
                // would advance the read position past events nothing will ever offer again,
                // so the walk stops AT the hole rather than looking like the end of history.
                let store = storeOf [ answerOf 0L 2; answerOf 5L 2 ]
                let seen = ResizeArray ()
                do! Client.EventFetch.replay store seen.Add
                let folded =
                    seen
                    |> Seq.collect (fun msg ->
                        match msg with
                        | LocalHistoryMsg page -> page.Events |> List.map (fun e -> EventOffset.value e.Offset)
                        | _ -> [])
                    |> List.ofSeq
                Expect.equal folded [ 0L; 1L ] "everything up to the hole, and nothing past it"
            }

        testCaseAsync "a hole is reported as history this device lacks, never as the feed's health" <|
            async {
                // Nothing has been read from the network when a replay runs, so a hole can
                // say nothing whatever about the feed. Reported as one, it flashed a red
                // "history paused" over every cold open with a hole in it — moments before
                // the first page repaired the thing it was complaining about.
                let store = storeOf [ answerOf 0L 2; answerOf 5L 2 ]
                let seen = ResizeArray ()
                do! Client.EventFetch.replay store seen.Add
                Expect.isFalse
                    (seen |> Seq.exists (function EventFeedMsg _ -> true | _ -> false))
                    "a local read reports no feed health at all"
                let reported =
                    seen |> Seq.tryPick (function LocalHistoryGapMsg at -> Some at | _ -> None)
                match reported with
                | None -> failwith "a hole must be reported, not silently treated as the end"
                | Some at ->
                    Expect.equal (EventOffset.value at) 5L "naming where the kept history resumes"
            }

        testCaseAsync "a page off the network clears the gap it fills" <|
            async {
                // The read resumes at the cursor the replay parked at, which is exactly where
                // the fill has to start — so a page arriving from the network means the hole
                // is being filled, and what is left to arrive is ordinary catch-up.
                let store = storeOf [ answerOf 0L 2; answerOf 5L 2 ]
                let seen = ResizeArray ()
                do! Client.EventFetch.replay store seen.Add
                let model =
                    seen
                    |> Seq.fold
                        (fun m msg -> ClientModel.update msg m)
                        (ClientModel.init { PeerId = PeerId.create "p" |> expect; DisplayName = "P" })
                Expect.equal
                    (model.EventConsumer.MissingBefore |> Option.map EventOffset.value)
                    (Some 5L)
                    "the replay left the hole on the model"
                let fill =
                    match Client.EventFetch.decodeLines (snd (answerOf 2L 3)) with
                    | Ok envelopes -> envelopes
                    | Error fault -> failwith (Client.FeedFault.describe fault)
                let filled =
                    ClientModel.update
                        (EventsPageMsg
                            { Events = fill
                              LastOffset = fill |> List.tryLast |> Option.map (fun e -> e.Offset)
                              IsEnd = false })
                        model
                Expect.equal filled.EventConsumer.MissingBefore None "and a page off the network takes it away"
            }

        testCaseAsync "an answer is kept under the address it came back FROM, not the one asked for" <|
            async {
                // The cursor moves; the range it resolves to does not. Keying by the request
                // would key history under an address whose meaning changes, which is the bug
                // the whole scheme exists to avoid.
                let store = storeOf []
                let answering : Client.HttpGet =
                    fun _ -> async { return Ok { Url = "events/0-41"; Body = snd (answerOf 0L 2) } }
                let storing = Client.EventFetch.storing store answering
                let! _ = storing "events/after/99"
                let! kept = store.Stored ()
                Expect.equal kept [ "events/0-41" ] "the resolved range is the key"
            }
    ]

let tests =
    testList "EventsHttp" [
        endpointTests
        storeTests
    ]
