module Yession.Tests.EventsHttp

// The HTTP-cacheable event log: the append-only log served as fixed-size chunks
// (`GET /events/{n}?token=…`). Full chunks are immutable by construction, so they
// carry a hard 3-day cache header and any HTTP cache — the browser's — becomes the
// client-side event store; the growing tail chunk is `no-store`. Verified here: the
// chunk math, the headers, token gating, wire round-trip of the served lines, and a
// full client consuming the log over the HTTP fetcher instead of frames.

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

[<Emit("fetch($0).then(r => { if (!r.ok) throw new Error('events fetch failed: ' + r.status); return r.text() })")>]
let private fetchText (url: string) : JS.Promise<string> = Fable.Core.Util.jsNative

let private chunkMathTests =
    testList "Chunk math" [
        testCase "offsets map to fixed chunks and back" <| fun () ->
            Expect.equal (EventChunk.indexOf 0L) 0 "offset 0 is chunk 0"
            Expect.equal (EventChunk.indexOf (int64 EventChunk.size - 1L)) 0 "the last offset of chunk 0"
            Expect.equal (EventChunk.indexOf (int64 EventChunk.size)) 1 "the first offset of chunk 1"
            Expect.equal (EventChunk.firstOffset 2) (int64 (2 * EventChunk.size)) "chunk 2 starts at 2*size"

        testCase "full chunks cache hard for 3 days; the tail never caches" <| fun () ->
            Expect.equal (EventChunk.cacheControl true) "public, max-age=259200, immutable" "immutable + 3 days"
            Expect.equal (EventChunk.cacheControl false) "no-store" "a growing chunk is never cached"
    ]

let private endpointTests =
    testList "HTTP endpoint" [
        testCaseAsync "chunks serve JSONL with immutability-derived cache headers, token-gated" <|
            async {
                let! h = Host.start (SessionId.create "events-http" |> expect) "events-token" 0
                // Fill two full chunks plus a 5-event tail (no client connects, so appends
                // go straight to the log from offset 0).
                for i in 1 .. 2 * EventChunk.size + 5 do
                    let! _ =
                        h.Log.Append
                            ActorRef.SessionProcess
                            (PeerJoined
                                { PeerId = PeerId.create (sprintf "p-%d" i) |> expect
                                  DisplayName = "filler" })
                    ()
                let url (chunk: int) (token: string) =
                    sprintf "http://127.0.0.1:%d/events/%d?token=%s" h.Port chunk token

                let! full = httpGet (url 0 "events-token") |> Async.AwaitPromise
                Expect.equal full.status 200 "chunk 0 serves"
                Expect.equal full.cacheControl "public, max-age=259200, immutable" "a full chunk is immutable"
                let fullLines = full.body.Split '\n' |> Array.filter (fun l -> l.Trim().Length > 0)
                Expect.equal fullLines.Length EventChunk.size "exactly one chunk of lines"
                // The served lines are the canonical wire encoding.
                let decoded = Codec.fromString Codec.sessionEventEnvelope fullLines.[0] |> expect
                Expect.equal (EventOffset.value decoded.Offset) 0L "chunk 0 starts at offset 0"

                let! tail = httpGet (url 2 "events-token") |> Async.AwaitPromise
                Expect.equal tail.cacheControl "no-store" "the growing tail chunk never caches"
                let tailLines = tail.body.Split '\n' |> Array.filter (fun l -> l.Trim().Length > 0)
                Expect.equal tailLines.Length 5 "the tail has the remainder"

                let! beyond = httpGet (url 9 "events-token") |> Async.AwaitPromise
                Expect.equal beyond.status 200 "a chunk beyond the log serves"
                Expect.equal beyond.cacheControl "no-store" "emptiness must never be cached"
                Expect.equal (beyond.body.Trim ()) "" "and is empty"

                let! wrongToken = httpGet (url 0 "stolen") |> Async.AwaitPromise
                Expect.equal wrongToken.status 401 "the event log is token-gated"
                Expect.equal wrongToken.cacheControl "no-store" "rejections never cache"

                let! notAChunk = httpGet (sprintf "http://127.0.0.1:%d/events/nope?token=events-token" h.Port) |> Async.AwaitPromise
                Expect.equal notAChunk.status 404 "non-numeric chunk paths are not found"
                do! h.Stop ()
            }

        Tag.needs "HTTP fetcher client" [ Tag.Ports ] (fun () ->
            testCaseAsync "a client consuming over the HTTP fetcher builds the same timeline as the frame path" <|
            async {
                let! h = Host.start (SessionId.create "events-http-client" |> expect) "fetch-token" 0
                let signalUrl = sprintf "http://127.0.0.1:%d/signal" h.Port
                let baseUrl = sprintf "http://127.0.0.1:%d" h.Port
                let options =
                    { App.ConnectOptions.defaults with
                        FetchEvents = Some (App.EventFetch.overHttp (fetchText >> Async.AwaitPromise) baseUrl "fetch-token") }
                let! a = connectClientWith options signalUrl "fetch-token" "ada" "Ada"

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
        chunkMathTests
        endpointTests
    ]
