module Yession.Tests.TranscriptHttp

// A terminal's transcript over HTTP, read by CURSOR (Plan 22): a client sends the line
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
open Yession.Domain.Terminals
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

// --- What a client keeps, and what it does with it (Plan 22, step 2) ----------------------
// The store is a port, so these need no browser: a list standing in for the Cache API answers
// the same questions, and what is under test is the client's reasoning about what it holds —
// replay each terminal in order, notice a hole in one without losing the others.

/// A `TranscriptCaches` over per-terminal lists, ordered the way the Cache API orders `keys()`:
/// insertion first, which is fetch order, which is ascending.
let private storeOf (entries: (TerminalId * (string * int * string) list) list) =
    let kept = System.Collections.Generic.Dictionary<string, ResizeArray<string * int * string>> ()
    for (terminal, answers) in entries do
        kept.[TerminalId.value terminal] <- ResizeArray answers
    let forTerminal (terminal: TerminalId) =
        match kept.TryGetValue (TerminalId.value terminal) with
        | true, answers -> answers
        | _ ->
            let answers = ResizeArray ()
            kept.[TerminalId.value terminal] <- answers
            answers
    { Client.For =
        fun terminal ->
            let answers = forTerminal terminal
            // Annotated, because `Stored`/`Read`/`Write` are field names both history stores
            // share and inference would otherwise pick whichever was declared last.
            async.Return
                ({ Stored = fun () -> async.Return (answers |> Seq.map (fun (u, _, _) -> u) |> List.ofSeq)
                   Read =
                     fun url ->
                         async.Return (
                             answers
                             |> Seq.tryPick (fun (u, first, body) -> if u = url then Some (first, body) else None))
                   Write = fun url first body -> async { answers.Add (url, first, body) } } : Client.TranscriptCache)
      Client.Kept = fun () -> async.Return (entries |> List.map fst) } : Client.TranscriptCaches

/// One kept answer: the JSONL the server served for lines [first, first + count - 1], and the
/// address it came back from. The header goes at line 0, exactly as the recording has it.
let private answerOf (first: int) (count: int) =
    let line (seq: int) =
        if seq = 0 then
            Codec.toString Codec.transcriptLine (TranscriptHeaderLine { Width = 80; Height = 24; Timestamp = 0L })
        else
            Codec.toString
                Codec.transcriptLine
                (TranscriptRecordLine { At = 0.0; Kind = TranscriptOutput; Data = sprintf "line %d" seq })
    let last = first + count - 1
    sprintf "terminals/%s/%d-%d" (TerminalId.value terminal) first last,
    first,
    [ first .. last ] |> List.map line |> String.concat "\n"

let private recordsIn (seen: ResizeArray<ClientMsg>) (of': TerminalId) =
    seen
    |> Seq.choose (fun msg ->
        match msg with
        | TerminalRecordMsg (t, seq, _) when t = of' -> Some seq
        | _ -> None)
    |> List.ofSeq

let private storeTests =
    testList "What a client keeps" [
        testCaseAsync "a replay folds what was kept for a terminal, in order, with no network" <|
            async {
                let store = storeOf [ terminal, [ answerOf 0 3; answerOf 3 2 ] ]
                let seen = ResizeArray ()
                do! Client.TranscriptFetch.replay store seen.Add
                // Line 0 is the header and is no record, so the records are lines 1..4.
                Expect.equal (recordsIn seen terminal) [ 1; 2; 3; 4 ] "every kept line, once, in recording order"
            }

        testCaseAsync "a replay carries the header, without which a rebuilt cast is not one" <|
            async {
                // The recorded width and height are what make a replay come out the shape the
                // terminal actually was, and they live on line 0 alone.
                let store = storeOf [ terminal, [ answerOf 0 3 ] ]
                let seen = ResizeArray ()
                do! Client.TranscriptFetch.replay store seen.Add
                let header =
                    seen |> Seq.tryPick (fun msg ->
                        match msg with
                        | TerminalHeaderMsg (_, h) -> Some h
                        | _ -> None)
                Expect.equal (header |> Option.map (fun h -> h.Width)) (Some 80) "the recording's own width"
            }

        testCaseAsync "a replay says how far it read, so the next fetch resumes rather than refetches" <|
            async {
                let store = storeOf [ terminal, [ answerOf 0 3; answerOf 3 2 ] ]
                let seen = ResizeArray ()
                do! Client.TranscriptFetch.replay store seen.Add
                let readThrough =
                    seen |> Seq.fold (fun acc msg ->
                        match msg with
                        | TerminalReadThroughMsg (_, seq) -> max acc seq
                        | _ -> acc) 0
                Expect.equal readThrough 5 "one past the last line kept"
            }

        testCaseAsync "answers the store hands back out of order still fold in recording order" <|
            async {
                // The store's own order is not ascending and cannot be made to be: the Cache
                // API's `put` of an address already held deletes the entry and appends the new
                // one, so two tabs of one session fetching the same range moves the earliest
                // answer to the end. The line an answer starts on is kept beside its bytes,
                // and that — never the store's order — is what the walk goes by.
                let store = storeOf [ terminal, [ answerOf 3 2; answerOf 0 3 ] ]
                let seen = ResizeArray ()
                do! Client.TranscriptFetch.replay store seen.Add
                Expect.equal (recordsIn seen terminal) [ 1; 2; 3; 4 ] "every kept line, once, in recording order"
            }

        testCaseAsync "a hole stops that terminal's walk at its edge" <|
            async {
                // An entry evicted from the middle leaves the rest kept. Folding over the gap
                // would advance the read position past lines nothing will ever offer again.
                let store = storeOf [ terminal, [ answerOf 0 3; answerOf 8 2 ] ]
                let seen = ResizeArray ()
                do! Client.TranscriptFetch.replay store seen.Add
                Expect.equal (recordsIn seen terminal) [ 1; 2 ] "everything up to the hole, and nothing past it"
            }

        testCaseAsync "a hole in one terminal costs that terminal alone" <|
            async {
                // What one store per terminal buys, and the reason they are not one store: a
                // walk that had to sort a shared store by address would stop them all.
                let other = TerminalId.create "term-other" |> expect
                let store =
                    storeOf
                        [ terminal, [ answerOf 0 3; answerOf 8 2 ]
                          other, [ answerOf 0 3 ] ]
                let seen = ResizeArray ()
                do! Client.TranscriptFetch.replay store seen.Add
                Expect.equal (recordsIn seen terminal) [ 1; 2 ] "the holed terminal stops at its hole"
                Expect.equal (recordsIn seen other) [ 1; 2 ] "and the other one is folded in full"
            }

        testCaseAsync "an answer is kept under the address it came back FROM, at the line asked for" <|
            async {
                // The cursor moves; the range it resolves to does not. And the line it starts
                // on is kept beside the bytes, because a transcript line cannot carry its own
                // index and the address must never be parsed for one.
                let store = storeOf []
                let answering : Client.HttpGet =
                    fun _ ->
                        async {
                            let url, _, body = answerOf 5 2
                            return Ok { Url = url; Body = body }
                        }
                let feed = Client.TranscriptFetch.overHttp store answering (fun r -> SessionRoute.relative r) None
                let! _ = feed terminal 5
                let! cache = store.For terminal
                let! kept = cache.Stored ()
                Expect.equal kept [ sprintf "terminals/%s/5-6" (TerminalId.value terminal) ] "the resolved range is the key"
                let! entry = cache.Read (List.head kept)
                Expect.equal (entry |> Option.map fst) (Some 5) "with the line it starts on beside it"
            }

        testCaseAsync "a client told it is current keeps nothing" <|
            async {
                // The `204` arrives as an answer with no lines, from the CURSOR's address —
                // no redirect happened. Keeping it would put an empty entry in the store that
                // the replay would then read as a hole in the recording.
                let store = storeOf []
                let answering : Client.HttpGet =
                    fun url -> async { return Ok { Url = url; Body = "" } }
                let feed = Client.TranscriptFetch.overHttp store answering (fun r -> SessionRoute.relative r) None
                let! _ = feed terminal 7
                let! cache = store.For terminal
                let! kept = cache.Stored ()
                Expect.equal kept [] "nothing to keep, so nothing kept"
            }
    ]

let tests =
    testList "TranscriptHttp" [
        endpointTests
        storeTests
    ]
