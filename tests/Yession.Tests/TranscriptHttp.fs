module Yession.Tests.TranscriptHttp

// A terminal's transcript over HTTP, read by CURSOR (docs/plans/22): a client sends the line
// it has folded through (`GET /terminals/{t}/after/{n}?token=…`) and the server redirects to
// the range it chose (`/terminals/{t}/{first}-{last}`), whose bytes never change because line
// index IS sequence number and those bounds do not move.
//
// The event feed's tests are next door in `EventsHttp.fs` and this is deliberately their
// shape: same cursor, same redirect, same refusals. What is different, and is what these
// pin, is that a transcript is a TERMINAL's and its lines do not carry their own index.

open Fable.Core
open Fable.Pyxpecto
open Yession.Domain
open Yession.App
open Yession.Host
open Yession.Tests.Support

// A GET with redirects left unfollowed, so the cursor's own answer is observable rather than
// the range's. `location` is empty on anything that is not a redirect.
type private RedirectReply =
    abstract status : int
    abstract cacheControl : string
    abstract location : string

[<Emit("fetch($0, { redirect: 'manual' }).then(r => ({ status: r.status, cacheControl: r.headers.get('cache-control') || '', location: r.headers.get('location') || '' }))")>]
let private httpGetRaw (url: string) : JS.Promise<RedirectReply> = Fable.Core.Util.jsNative

type private HttpReply =
    abstract status : int
    abstract cacheControl : string
    abstract body : string

[<Emit("fetch($0).then(async r => ({ status: r.status, cacheControl: r.headers.get('cache-control') || '', body: await r.text() }))")>]
let private httpGet (url: string) : JS.Promise<HttpReply> = Fable.Core.Util.jsNative

let private terminal = TerminalId.create "term-http" |> expect
let private token = "minted-for-this-test"

/// A server serving one terminal's transcript of `records` output lines (plus the header at
/// line 0), and the base URL it is on. The arrangement every case here shares; what each one
/// asserts is its own.
let private serving (records: int) =
    async {
        let store = TranscriptStore.inMemory ()
        let transcript = store.Open terminal { Width = 80; Height = 24; Timestamp = 0L }
        for i in 1 .. records do
            transcript.Append { At = 0.0; Kind = TranscriptOutput; Data = sprintf "line %d" i } |> ignore
        // The REAL endpoint the Session Process serves from, not a stand-in: what these cases
        // are about is the wire, and a hand-built endpoint here would agree with the store by
        // construction and prove nothing about the one production uses.
        let endpoint = TranscriptStore.endpoint (fun t -> t = token) store
        let! server, _ =
            Signalling.start
                (SessionId.create "transcript-http" |> expect)
                ignore
                None
                (Some endpoint)
                None
                None
                (fun _ -> token)
                ""
                None
                false
                0
        let port = Yession.Host.Interop.serverPort server
        let at (route: SessionRoute) (t: string) =
            sprintf "http://127.0.0.1:%d/%s?token=%s" port (SessionRoute.relative route) t
        return at, (fun () -> async { server.close ignore })
    }

let private lines (body: string) = body.Split '\n' |> Array.filter (fun l -> l.Trim().Length > 0)

let private endpointTests =
    testList "HTTP endpoint" [
        testCaseAsync "a cursor with no position redirects to the range that starts the recording" <|
            async {
                let! at, stop = serving 10
                let! start = httpGetRaw (at (TerminalTranscriptAfter (TerminalId.value terminal, None)) token) |> Async.AwaitPromise
                Expect.equal start.status 307 "a cursor redirects rather than answering"
                Expect.stringContains
                    start.location
                    (sprintf "terminals/%s/0-10" (TerminalId.value terminal))
                    "to the header and the ten records after it"
                do! stop ()
            }

        testCaseAsync "the cursor's own answer is never kept" <|
            async {
                // Where the lines are is the one thing on this surface allowed to change its
                // mind, so it must not be stored anywhere — by the client or by a cache.
                let! at, stop = serving 10
                let! start = httpGetRaw (at (TerminalTranscriptAfter (TerminalId.value terminal, None)) token) |> Async.AwaitPromise
                Expect.equal start.cacheControl "no-store" "a cursor is never cached"
                do! stop ()
            }

        testCaseAsync "a redirect carries the token a redirect would otherwise drop" <|
            async {
                // The cookie-less path: a Node client authorizes with `?token=`, and a
                // redirect drops the query — so the range would arrive unauthorized.
                let! at, stop = serving 10
                let! start = httpGetRaw (at (TerminalTranscriptAfter (TerminalId.value terminal, None)) token) |> Async.AwaitPromise
                Expect.stringContains start.location "token=" "so the range is reachable with what got here"
                do! stop ()
            }

        testCaseAsync "the range a cursor resolves to begins one line past it" <|
            async {
                // The contract the client's numbering rests on, seen from the wire. A
                // transcript line cannot carry its own index, so a client numbers an answer
                // from what it ASKED — and this is what makes that true.
                let! at, stop = serving 10
                let! start = httpGetRaw (at (TerminalTranscriptAfter (TerminalId.value terminal, Some 4)) token) |> Async.AwaitPromise
                Expect.stringContains
                    start.location
                    (sprintf "terminals/%s/5-10" (TerminalId.value terminal))
                    "a client sitting at line 4 is sent to a range starting at line 5"
                do! stop ()
            }

        testCaseAsync "a range answers the lines it names, and the client keeps them" <|
            async {
                let! at, stop = serving 10
                let! answer = httpGet (at (TerminalTranscriptRange (TerminalId.value terminal, 0, 10)) token) |> Async.AwaitPromise
                Expect.equal answer.status 200 "the range serves"
                Expect.equal (lines answer.body).Length 11 "the header and ten records"
                Expect.equal answer.cacheControl "no-store" "the client keeps this, not the HTTP cache"
                do! stop ()
            }

        testCaseAsync "the same range answers the same bytes after the terminal printed more" <|
            async {
                // The whole argument for keeping an answer for ever, and the thing a chunk
                // INDEX could never give the tail: `terminals/{t}/3` meant whatever chunk 3
                // holds now, which grows.
                let store = TranscriptStore.inMemory ()
                let transcript = store.Open terminal { Width = 80; Height = 24; Timestamp = 0L }
                for i in 1 .. 4 do
                    transcript.Append { At = 0.0; Kind = TranscriptOutput; Data = sprintf "line %d" i } |> ignore
                let endpoint = TranscriptStore.endpoint (fun t -> t = token) store
                let! server, _ =
                    Signalling.start
                        (SessionId.create "transcript-http-grow" |> expect)
                        ignore None (Some endpoint) None None (fun _ -> token) "" None false 0
                let port = Yession.Host.Interop.serverPort server
                let url =
                    sprintf
                        "http://127.0.0.1:%d/%s?token=%s"
                        port
                        (SessionRoute.relative (TerminalTranscriptRange (TerminalId.value terminal, 0, 4)))
                        token
                let! before = httpGet url |> Async.AwaitPromise
                for i in 5 .. 20 do
                    transcript.Append { At = 0.0; Kind = TranscriptOutput; Data = sprintf "line %d" i } |> ignore
                let! after = httpGet url |> Async.AwaitPromise
                Expect.equal after.body before.body "the tail's address still answers the tail it named"
                server.close ignore
            }

        testCaseAsync "a range the transcript has not reached does not exist yet" <|
            async {
                // Never a short answer: a partial body at an address that promised the whole
                // range is what a client would keep, for ever, as if it were the whole range.
                let! at, stop = serving 10
                let! unreached = httpGet (at (TerminalTranscriptRange (TerminalId.value terminal, 100, 199)) token) |> Async.AwaitPromise
                Expect.equal unreached.status 404 "a range beyond the recording is a 404"
                do! stop ()
            }

        testCaseAsync "a caller at the tail is told it is current, not given an empty range" <|
            async {
                // An empty range is a resource a client keeps, and "nothing yet" is exactly
                // the thing that stops being true.
                let! at, stop = serving 10
                let! current = httpGetRaw (at (TerminalTranscriptAfter (TerminalId.value terminal, Some 10)) token) |> Async.AwaitPromise
                Expect.equal current.status 204 "the caller has every line"
                do! stop ()
            }

        testCaseAsync "a terminal with no recording answers its cursor rather than refusing it" <|
            async {
                // A 404 here would tell a client that a terminal it can SEE does not exist.
                // "There are no lines you have not seen" is the honest answer, and it is the
                // same one a caller at the tail gets.
                let! at, stop = serving 10
                let! unknown = httpGetRaw (at (TerminalTranscriptAfter ("term-never-opened", None)) token) |> Async.AwaitPromise
                Expect.equal unknown.status 204 "a cursor over nothing says there is nothing"
                do! stop ()
            }

        testCaseAsync "both legs are gated on minted tokens" <|
            async {
                let! at, stop = serving 10
                let! cursor = httpGet (at (TerminalTranscriptAfter (TerminalId.value terminal, None)) "stolen") |> Async.AwaitPromise
                Expect.equal cursor.status 401 "the cursor is gated"
                let! range = httpGet (at (TerminalTranscriptRange (TerminalId.value terminal, 0, 10)) "stolen") |> Async.AwaitPromise
                Expect.equal range.status 401 "and so are the lines themselves"
                do! stop ()
            }
    ]

// Binds a port and speaks HTTP, so it needs `Ports`; `Signalling` loads the WebRTC addon at
// import, so it needs `Native` too — even though nothing here signals.
let tests =
    testList "TranscriptHttp" [
        Tag.needs "transcripts over HTTP" [ Tag.Ports; Tag.Native ] (fun () -> endpointTests)
    ]
