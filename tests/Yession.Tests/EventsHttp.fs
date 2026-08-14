module Yession.Tests.EventsHttp

// The event log over HTTP, read by CURSOR (docs/plans/20): a client sends the offset it
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

// The chunk GET shaped as `App.HttpGet` is — total, with the status on a refusal. Identical
// to the browser's port (app/browser/Browser.fs); failure classification is covered in
// Resilience.fs, so here it only has to be the real thing.
[<Emit("""fetch($0).then(
  async r => r.ok ? { ok: true, status: r.status, detail: await r.text() } : { ok: false, status: r.status, detail: '' },
  e => ({ ok: false, status: 0, detail: String(e) }))""")>]
let private fetchChunk (url: string) : JS.Promise<{| ok: bool; status: int; detail: string |}> = Fable.Core.Util.jsNative

let private chunkGet : App.HttpGet =
    fun url ->
        async {
            let! reply = fetchChunk url |> Async.AwaitPromise
            return
                if reply.ok then Ok reply.detail
                elif reply.status = 0 then Error (App.HttpUnreachable reply.detail)
                else Error (App.HttpStatus reply.status)
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
                    { App.ConnectOptions.defaults with
                        FetchEvents =
                            Some (App.EventFetch.overHttp chunkGet (SessionRoute.at baseUrl) (Some mintedToken)) }
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

let tests =
    testList "EventsHttp" [
        endpointTests
    ]
