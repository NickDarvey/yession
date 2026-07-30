module Yession.Tests.Resilience

// The event feed's behaviour when the network fails — at the seam where it actually fails.
//
// Integration, not E2E, and deliberately so: the socket is a FUNCTION here, so "the network
// is down" is a value the test chooses rather than a server it has to kill and a race it has
// to win. Everything on either side of that function is production code — the real
// `EventFetch.overHttp` (URL scheme, chunk math, JSONL codec), the real resilience policy
// that ships in the browser, the real `App.connect` read loop, the real `ClientModel`, and
// the real Session Process host on the other end of an in-memory channel. That combination —
// durable history over HTTP, collaborative state over the data channel — is precisely the one
// the browser runs, and the one no existing suite covered.
//
// What these pin:
//   * a failed read is REPORTED, not silently turned into an empty final page (which is what
//     made the original bug invisible: the loop read "nothing new yet" and re-requested
//     forever, while drafts, title, and presence kept syncing over the data channel);
//   * retries happen only where retrying can help, on a bounded schedule, in zero real time
//     (the clock is injected, so backoff is asserted rather than slept through);
//   * the client stays fully usable while history is stalled — writing, sending, and
//     collaborating are local-first CRDT operations that never touch the feed;
//   * and the feed recovers on its own, resuming from where consumption stopped.

open System
open Fable.Core
open Fable.Pyxpecto
open Ylmish
open Yession.Domain
open Yession.App
open Yession.Host
open Yession.Tests.Support

// --- Test clock: the whole point of injecting `Sleep` ------------------------------------

/// A sleep that records what it was asked to wait for and returns at once, so a policy's
/// entire backoff sequence is asserted in zero real time.
let private recordingSleep (log: ResizeArray<TimeSpan>) : TimeSpan -> Async<unit> =
    fun delay -> async { log.Add delay }

/// Jitter's entropy, pinned. `0.0` yields the un-spread delay (`d - d·spread·0`), so the
/// schedule under test is exactly the one the combinators describe.
let private noJitter : unit -> float = fun () -> 0.0

let private ms (n: float) = TimeSpan.FromMilliseconds n

// --- Schedules ---------------------------------------------------------------------------

let private scheduleTests =
    testList "Schedules are values" [
        testCase "a constant schedule retries a fixed number of times, then retires" <| fun () ->
            let schedule = Resilience.Schedule.constant (ms 50.0) 3
            Expect.equal
                [ for n in 1 .. 4 -> schedule n ]
                [ Some (ms 50.0); Some (ms 50.0); Some (ms 50.0); None ]
                "three retries at 50ms, then no further attempt"

        testCase "exponential backoff doubles, saturates at the cap, and retires" <| fun () ->
            let schedule = Resilience.Schedule.exponential (ms 100.0) 2.0 (ms 500.0) 6
            Expect.equal
                [ for n in 1 .. 7 -> schedule n |> Option.map (fun d -> d.TotalMilliseconds) ]
                [ Some 100.0; Some 200.0; Some 400.0; Some 500.0; Some 500.0; Some 500.0; None ]
                "100, 200, 400, then pinned to the 500ms ceiling, then retired"

        testCase "jitter spreads delays downward within its fraction and never past it" <| fun () ->
            let inner = Resilience.Schedule.constant (ms 1000.0) 3
            // The extremes of `random`'s range bound the spread; `0.5` means "up to half off".
            let atZero = Resilience.Schedule.jittered 0.5 (fun () -> 0.0) inner
            let atOne = Resilience.Schedule.jittered 0.5 (fun () -> 0.999) inner
            Expect.equal (atZero 1) (Some (ms 1000.0)) "no jitter draw leaves the delay whole"
            Expect.isTrue
                (match atOne 1 with
                 | Some d -> d.TotalMilliseconds >= 500.0 && d.TotalMilliseconds < 501.0
                 | None -> false)
                "a maximal draw takes off (almost) exactly the spread — never more"
            Expect.equal (atZero 4) None "jitter preserves the inner schedule's retirement"
    ]

// --- The interpreter ---------------------------------------------------------------------

/// An operation that fails `failures` times with `fault`, then succeeds. Counts its calls.
let private flaky (calls: int ref) (failures: int) (fault: App.FeedFault) =
    fun (_: unit) ->
        async {
            calls.Value <- calls.Value + 1
            if calls.Value <= failures then return Error fault else return Ok "served"
        }

let private guardTests =
    testList "Policy.guard" [
        testCaseAsync "a transient fault is retried on the schedule until it succeeds" <|
            async {
                let calls = ref 0
                let delays = ResizeArray<TimeSpan> ()
                let observed = ResizeArray<Resilience.Attempt<App.FeedFault>> ()
                let policy : Resilience.Policy<App.FeedFault> =
                    { Schedule = Resilience.Schedule.exponential (ms 250.0) 2.0 (ms 10000.0) 5
                      Retryable = App.FeedFault.isTransient
                      Sleep = recordingSleep delays
                      Observe = observed.Add }
                let! result =
                    Resilience.Policy.guard policy (flaky calls 3 (App.FeedUnreachable "ECONNREFUSED")) ()
                Expect.equal result (Ok "served") "the fourth attempt got through"
                Expect.equal calls.Value 4 "three failures, then one success — no extra attempts"
                Expect.equal
                    [ for d in delays -> d.TotalMilliseconds ]
                    [ 250.0; 500.0; 1000.0 ]
                    "waited the schedule's delays, in order, and only between attempts"
                Expect.equal [ for a in observed -> a.Number ] [ 1; 2; 3 ] "every failed attempt was observed"
                Expect.isTrue
                    (observed |> Seq.forall (fun a -> a.Retrying.IsSome))
                    "each observed failure was still going to be retried"
            }

        testCaseAsync "a fault the policy does not handle fails on the first attempt" <|
            async {
                let calls = ref 0
                let delays = ResizeArray<TimeSpan> ()
                let observed = ResizeArray<Resilience.Attempt<App.FeedFault>> ()
                let policy : Resilience.Policy<App.FeedFault> =
                    { Schedule = Resilience.Schedule.constant (ms 10.0) 5
                      Retryable = App.FeedFault.isTransient
                      Sleep = recordingSleep delays
                      Observe = observed.Add }
                // 401 is a decision, not a hiccup: retrying it only hammers the session.
                let! result = Resilience.Policy.guard policy (flaky calls 99 (App.FeedRefused 401)) ()
                Expect.equal result (Error (App.FeedRefused 401)) "the fault is returned, unchanged"
                Expect.equal calls.Value 1 "an unhandled fault is never retried"
                Expect.equal delays.Count 0 "and never waited on"
                Expect.equal [ for a in observed -> a.Retrying ] [ None ] "observed once, as final"
            }

        testCaseAsync "once the schedule retires, the last error is returned — never a fake success" <|
            async {
                let calls = ref 0
                let policy : Resilience.Policy<App.FeedFault> =
                    { Schedule = Resilience.Schedule.constant (ms 10.0) 2
                      Retryable = App.FeedFault.isTransient
                      Sleep = recordingSleep (ResizeArray ())
                      Observe = ignore }
                let! result = Resilience.Policy.guard policy (flaky calls 99 (App.FeedUnreachable "offline")) ()
                Expect.equal result (Error (App.FeedUnreachable "offline")) "the failure survives as a failure"
                Expect.equal calls.Value 3 "one attempt plus the schedule's two retries — bounded"
            }
    ]

// --- Fault classification at the HTTP boundary -------------------------------------------

/// The runtime's real `fetch`, shaped exactly as the browser's port is: total, carrying the
/// status on a refusal and the error text on a transport failure.
[<Emit("""fetch($0).then(
  async r => r.ok ? { ok: true, status: r.status, detail: await r.text() } : { ok: false, status: r.status, detail: '' },
  e => ({ ok: false, status: 0, detail: String(e) }))""")>]
let private realFetch (url: string) : JS.Promise<{| ok: bool; status: int; detail: string |}> =
    Fable.Core.Util.jsNative

let private realHttpGet : App.HttpGet =
    fun url ->
        async {
            let! reply = realFetch url |> Async.AwaitPromise
            return
                if reply.ok then Ok reply.detail
                elif reply.status = 0 then Error (App.HttpUnreachable reply.detail)
                else Error (App.HttpStatus reply.status)
        }

let private classificationTests =
    testList "HTTP faults are classified, not flattened" [
        testCaseAsync "a transport failure is an unreachable feed, not an empty log" <|
            async {
                let get : App.HttpGet = fun _ -> async { return Error (App.HttpUnreachable "ECONNREFUSED") }
                let! result = App.EventFetch.overHttp get "" None None
                Expect.equal
                    result
                    (Error (App.FeedUnreachable "ECONNREFUSED"))
                    "the old design returned an empty FINAL page here, which reads as 'nothing new'"
                Expect.isTrue
                    (App.FeedFault.isTransient (App.FeedUnreachable "ECONNREFUSED"))
                    "and it is worth retrying"
            }

        testCaseAsync "a refusal keeps its status, so authorization and overload differ" <|
            async {
                let refusing (status: int) : App.HttpGet = fun _ -> async { return Error (App.HttpStatus status) }
                let! unauthorized = App.EventFetch.overHttp (refusing 401) "" None None
                let! overloaded = App.EventFetch.overHttp (refusing 503) "" None None
                Expect.equal unauthorized (Error (App.FeedRefused 401)) "401 survives as 401"
                Expect.equal overloaded (Error (App.FeedRefused 503)) "503 survives as 503"
                Expect.isFalse (App.FeedFault.isTransient (App.FeedRefused 401)) "retrying cannot fix a 401"
                Expect.isTrue (App.FeedFault.isTransient (App.FeedRefused 503)) "a struggling session is worth waiting for"
                Expect.equal (App.FeedFault.describe (App.FeedRefused 401)) "not authorized" "and it says so in the UI"
            }

        testCaseAsync "a chunk that will not decode is corruption — a value, not a thrown page" <|
            async {
                let get : App.HttpGet = fun _ -> async { return Ok "{\"not\":\"an envelope\"}" }
                match! App.EventFetch.overHttp get "" None None with
                | Error (App.FeedCorrupt _) -> ()
                | other -> failwithf "expected FeedCorrupt, got %A" other
                Expect.isFalse
                    (App.FeedFault.isTransient (App.FeedCorrupt "x"))
                    "a bad line will not decode next time either"
            }

        testCaseAsync "a real socket with nothing behind it reports unreachable" <|
            async {
                // The one thing a fake `HttpGet` cannot prove: that a genuine `fetch` rejection
                // maps to `FeedUnreachable`. Port 1 has no listener, so this is a real
                // connection failure, with no server to start or stop.
                match! App.EventFetch.overHttp realHttpGet "http://127.0.0.1:1" None None with
                | Error (App.FeedUnreachable _) -> ()
                | other -> failwithf "a dead port must be an unreachable feed, got %A" other
            }
    ]

// --- The integration test ----------------------------------------------------------------

/// A stand-in for the Session Process's `/events/{n}` endpoint that can be switched off. When
/// up it serves the host's REAL log through the REAL codec — byte-identical to the HTTP route
/// (app/Host.fs `eventsEndpoint`); when down it fails the way an unreachable session does.
type private Socket =
    { /// The port `EventFetch.overHttp` is built over.
      Get : App.HttpGet
      /// Bring the feed back up.
      GoOnline : unit -> unit
      /// Every request ever made, so a spin cannot hide.
      Attempts : unit -> int }

/// A read loop that re-requests on failure instead of parking is unbounded, and with an
/// instant test clock it is unbounded IMMEDIATELY — so a low ceiling turns that regression
/// into a named failure in milliseconds rather than a hung suite. A correct run needs at most
/// six requests (one attempt + five retries) per availability hint, and this test produces a
/// handful of hints.
let private spinLimit = 120

let private fakeSocket (host: Host.SessionHost) : Socket =
    let mutable offline = true
    let mutable attempts = 0
    { GoOnline = fun () -> offline <- false
      Attempts = fun () -> attempts
      Get =
        fun url ->
            async {
                attempts <- attempts + 1
                if attempts > spinLimit then
                    failwithf "the event feed spun: %d requests for one session's history" attempts
                if offline then
                    return Error (App.HttpUnreachable "ECONNREFUSED")
                else
                    // `overHttp` builds `<base>/events/{chunk}`; serve that chunk from the log.
                    let index = int (url.Substring (url.LastIndexOf '/' + 1))
                    let after =
                        if index = 0 then None
                        else EventOffset.create (EventChunk.firstOffset index - 1L) |> expect |> Some
                    let! page = host.Log.Read after EventChunk.size
                    return
                        Ok (page.Events |> List.map (Codec.toString Codec.sessionEventEnvelope) |> String.concat "\n")
            } }

/// A client whose history arrives over `get` under the SHIPPED policy, with the policy's
/// interim reports dispatched into the model exactly as the browser wires them. `retries`
/// collects those reports so the test can assert the retry behaviour it cannot see from the
/// model alone (the model only ever holds the latest).
let private connectOverFeed (get: App.HttpGet) (retries: ResizeArray<FeedHealth>) =
    connectInMemoryClientVia (fun dispatch ->
        let feed =
            App.EventFetch.overHttp get "" None
            |> Resilience.Policy.guard
                (App.EventFetch.policy (recordingSleep (ResizeArray ())) noJitter (fun attempt ->
                    App.EventFetch.retrying attempt
                    |> Option.iter (fun health ->
                        retries.Add health
                        dispatch (EventFeedMsg health))))
        { App.ConnectOptions.defaults with FetchEvents = Some feed })

let private stalled (m: ClientModel) =
    match m.EventConsumer.Feed with
    | FeedStalled _ -> true
    | _ -> false

let private bodies (m: ClientModel) = m.Conversation.Items |> List.map (fun i -> i.Body)

let private feedFailureTests =
    testList "A client whose history feed fails" [
        testCaseAsync "reports the failure, keeps working, and recovers where it left off" <|
            async {
                let! host = Host.start (SessionId.create "feed-failure" |> expect) 0
                let socket = fakeSocket host
                let retries = ResizeArray<FeedHealth> ()
                // Ada's history feed is down from her first read; Bob reads over frames, so he
                // is the healthy peer hers is compared against.
                let! ada = connectOverFeed socket.Get retries host "ada" "Ada"
                let! bob = connectInMemoryClient host "bob" "Bob"

                // 1. The failure is REPORTED. Not an empty timeline that looks up to date — a
                //    stalled feed carrying the fault, which is what the old code could not say.
                do! ada.Runner.WaitFor stalled
                Expect.equal
                    (ada.Runner.Model ()).EventConsumer.Feed
                    (FeedStalled "ECONNREFUSED")
                    "the model carries the fault, not silence"
                Expect.equal
                    (ada.Runner.Model ()).Connection
                    Connected
                    "and the data channel is untouched — only the history leg is down"

                // 2. Retrying was bounded, and by the shipped schedule: five retries per read,
                //    numbered, then the read settles.
                Expect.isTrue (retries.Count >= 5) "the transient fault was retried"
                Expect.isTrue
                    (retries |> Seq.forall (function FeedRetrying (n, _) -> n >= 1 && n <= 5 | _ -> false))
                    "no read ever went past the policy's five retries"

                // 3. The client stays USABLE. Composing, sending, and titling are CRDT writes to
                //    a local doc relayed over the data channel — none of them reads the feed, so
                //    none of them cares that it is down.
                let peerId = ada.Hello.PeerId
                do! compose ada peerId "written while history was down"
                ada.Connection.SendDraft peerId
                ada.Runner.Dispatch (
                    user (EditTitleMsg (Text.insert 0 "still working" (ada.Runner.Model ()).Synced.Title)))

                // The Host drained her message and relayed her title: Bob sees both. This is the
                // graceful-degradation claim as an assertion — the failure is confined to Ada's
                // history feed and costs her nothing else.
                do! bob.Runner.WaitFor (fun m ->
                        Text.toString m.Synced.Title = "still working"
                        && bodies m = [ "written while history was down" ])
                do! ada.Runner.WaitFor (fun m -> Map.isEmpty m.Synced.Queue)
                Expect.equal
                    (bodies (ada.Runner.Model ()))
                    []
                    "her message is in the log but not her timeline: history is what is lost, and only that"
                Expect.isTrue (stalled (ada.Runner.Model ())) "the feed is still reported as stalled"

                // 4. Recovery, unassisted. The feed comes back; the next availability hint
                //    re-arms the read, which resumes from the untouched read position and
                //    replays everything missed, in order.
                socket.GoOnline ()
                do! compose bob bob.Hello.PeerId "sent after the feed came back"
                bob.Connection.SendDraft bob.Hello.PeerId
                do! ada.Runner.WaitFor (fun m ->
                        m.EventConsumer.Feed = FeedLive
                        && bodies m = [ "written while history was down"; "sent after the feed came back" ])
                Expect.equal
                    (bodies (ada.Runner.Model ()))
                    [ "written while history was down"; "sent after the feed came back" ]
                    "history catches up in order, including what was appended while the feed was down"

                // 5. No spin. Requests are proportional to availability hints (four events were
                //    appended here), never to elapsed time: at most six per hint while the feed
                //    was down, one per hint after. The `spinLimit` above is the hard floor under
                //    this claim — a re-request-on-failure loop trips it long before this line.
                Expect.isTrue
                    (socket.Attempts () <= 36)
                    (sprintf "a stalled feed parks and re-arms on hints; %d requests is polling" (socket.Attempts ()))

                do! host.Stop ()
            }

        testCaseAsync "an unauthorized feed is reported at once and never hammered" <|
            async {
                let! host = Host.start (SessionId.create "feed-unauthorized" |> expect) 0
                let mutable attempts = 0
                let refusing : App.HttpGet =
                    fun _ ->
                        async {
                            attempts <- attempts + 1
                            return Error (App.HttpStatus 401)
                        }
                let retries = ResizeArray<FeedHealth> ()
                let! ada = connectOverFeed refusing retries host "ada" "Ada"

                do! ada.Runner.WaitFor stalled
                Expect.equal
                    (ada.Runner.Model ()).EventConsumer.Feed
                    (FeedStalled "not authorized")
                    "the reason names the actual problem — a login, not a network"
                Expect.equal retries.Count 0 "a 401 is never retried, so nothing was ever reported as retrying"
                let settled = attempts
                do! compose ada ada.Hello.PeerId "still composable"
                Expect.equal attempts settled "and the feed is not re-requested behind the scenes"
                do! host.Stop ()
            }
    ]

// --- The other leg: opening the transport ------------------------------------------------

let private channelTests =
    testList "Opening the transport" [
        testCaseAsync "a session that never answers is a reported failure, not an eternal wait" <|
            async {
                // The browser's handshake used to settle ONLY on success, so this case had no
                // representation at all: the promise stayed pending and the shell stayed on
                // "connecting" with nothing to say. Now it is a fault, and the policy bounds
                // how long the client spends hoping.
                let attempts = ref 0
                let policy = App.SessionChannel.policy (recordingSleep (ResizeArray ())) noJitter
                let! result =
                    Resilience.Policy.guard policy (fun () ->
                        async {
                            attempts.Value <- attempts.Value + 1
                            return Error App.ChannelTimedOut
                        })
                    <| ()
                Expect.equal result (Error App.ChannelTimedOut) "the attempt settles as a failure"
                Expect.equal attempts.Value 5 "one attempt plus the policy's four retries"
                Expect.equal
                    (App.ChannelFault.describe App.ChannelTimedOut)
                    "the session did not answer"
                    "and it says something a person can act on"
            }

        testCaseAsync "a session that comes back mid-retry is connected to" <|
            async {
                // A restarting Session Process is the ordinary case, and every fault this port
                // produces means "not there YET" — which is why the policy retries all of them.
                let attempts = ref 0
                let policy = App.SessionChannel.policy (recordingSleep (ResizeArray ())) noJitter
                let! result =
                    Resilience.Policy.guard policy (fun () ->
                        async {
                            attempts.Value <- attempts.Value + 1
                            if attempts.Value < 3 then return Error (App.ChannelUnreachable "signalling refused: 502")
                            else return Ok "channel"
                        })
                    <| ()
                Expect.equal result (Ok "channel") "the third attempt got a channel"
                Expect.equal attempts.Value 3 "and it stopped there"
            }

        testCase "a settled disconnection carries its reason into the model and the page" <| fun () ->
            let init = ClientModel.init (peer "ada" "Ada")
            Expect.equal init.Connection (Disconnected None) "a fresh client knows nothing yet"
            let refused = ClientModel.update (RejectedMsg "peer token expired") init
            let unreachable = ClientModel.update (ConnectFailedMsg "the session did not answer") init
            Expect.equal refused.Connection (Disconnected (Some "peer token expired"))
                "a rejection keeps the reason the session gave"
            Expect.equal unreachable.Connection (Disconnected (Some "the session did not answer"))
                "and so does a session that never answered"
            let html = Support.render unreachable
            Expect.isTrue (html.Contains (Dom.hookText Dom.Hooks.connection Dom.Text.disconnected))
                "the status word is unchanged — the reason is additive"
            Expect.isTrue (html.Contains (Dom.hookText Dom.Hooks.connectionReason "the session did not answer"))
                "the reason is on the page, where the old model had nothing to show"
            Expect.isTrue (html.Contains (Dom.attr Dom.Hooks.degraded Dom.Text.degradedOffline))
                "and the strip reports the session leg, not the feed"
    ]

// --- What the failure looks like ----------------------------------------------------------

let private surfaceTests =
    testList "Degradation is visible and never blocking" [
        testCase "a stalled feed shows history paused with its reason, and the composer stays live" <| fun () ->
            let model = ClientModel.init (peer "ada" "Ada")
            let html =
                Support.render
                    { model with
                        Connection = Connected
                        EventConsumer = { model.EventConsumer with Feed = FeedStalled "ECONNREFUSED" } }
            Expect.isTrue
                (html.Contains (Dom.attr Dom.Hooks.feed Dom.Text.feedPaused))
                "the sidebar reports the feed as paused"
            Expect.isTrue
                (html.Contains (Dom.attr Dom.Hooks.degraded Dom.Text.feedPaused))
                "the strip over the timeline says history is paused"
            Expect.isTrue (html.Contains "ECONNREFUSED") "with the fault that caused it"
            Expect.isTrue
                (html.Contains Dom.Text.localFallback)
                "and what still works — this is a local-first client, not a broken one"
            // The local-first promise, asserted rather than described: nothing about a dead
            // history feed takes the composer or its send away.
            Expect.isTrue (html.Contains (Dom.attr Dom.Hooks.sendDraft "ada")) "send is still offered"
            Expect.isTrue (html.Contains Dom.Hooks.draftEditor) "and the composer is still mounted"
            Expect.isFalse (html.Contains "disabled") "nothing is disabled by a stalled feed"

        testCase "a retrying feed shows its attempt; a healthy client shows no strip at all" <| fun () ->
            let model = { ClientModel.init (peer "ada" "Ada") with Connection = Connected }
            let retrying =
                Support.render
                    { model with EventConsumer = { model.EventConsumer with Feed = FeedRetrying (3, "HTTP 503") } }
            Expect.isTrue
                (retrying.Contains (Dom.attr Dom.Hooks.degraded Dom.Text.feedRetrying))
                "a retrying feed is strip-worthy: something is wrong but it is being handled"
            Expect.isTrue (retrying.Contains "HTTP 503") "with the fault"
            Expect.isTrue (retrying.Contains "attempt 3") "and how far along the retries are"
            let healthy = Support.render model
            Expect.isTrue (healthy.Contains (Dom.attr Dom.Hooks.feed Dom.Text.feedLive)) "a live feed says so quietly"
            Expect.isFalse (healthy.Contains Dom.Hooks.degraded) "and takes no room on the page"

        testCase "the session leg outranks the feed: one strip, one problem" <| fun () ->
            // A Process that cannot be reached cannot serve its feed either, so reporting both
            // would be reporting one fault twice.
            let model = ClientModel.init (peer "ada" "Ada")
            let html =
                Support.render
                    { model with
                        Connection = Disconnected (Some "session unreachable")
                        EventConsumer = { model.EventConsumer with Feed = FeedStalled "ECONNREFUSED" } }
            Expect.isTrue
                (html.Contains (Dom.attr Dom.Hooks.degraded Dom.Text.degradedOffline))
                "the strip reports the session"
            Expect.isFalse
                (html.Contains (Dom.attr Dom.Hooks.degraded Dom.Text.feedPaused))
                "and not, redundantly, its feed as well"
    ]

let tests =
    testList "Event feed resilience" [
        scheduleTests
        guardTests
        classificationTests
        feedFailureTests
        channelTests
        surfaceTests
    ]
