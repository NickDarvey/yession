module Yession.Tests.Resilience

// The client's two network legs when they fail — the durable history feed (HTTP) and the
// session transport (the data channel) — at the seams where they actually fail.
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
//   * the feed recovers on its own, resuming from where consumption stopped;
//   * and the session lifecycle keeps the promise the model makes — a dropped session comes
//     back and resumes, a refused one does not, and a transport that never opens settles with
//     a reason instead of leaving the shell on "connecting" forever.

open System
open Fable.Core
open Fable.Pyxpecto
open Yjs
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
  async r => r.ok ? { ok: true, status: r.status, url: r.url, detail: await r.text() } : { ok: false, status: r.status, url: r.url, detail: '' },
  e => ({ ok: false, status: 0, url: '', detail: String(e) }))""")>]
let private realFetch (url: string) : JS.Promise<{| ok: bool; status: int; url: string; detail: string |}> =
    Fable.Core.Util.jsNative

let private realHttpGet : App.HttpGet =
    fun url ->
        async {
            let! reply = realFetch url |> Async.AwaitPromise
            return
                if reply.ok then Ok { Url = reply.url; Body = reply.detail }
                elif reply.status = 0 then Error (App.HttpUnreachable reply.detail)
                else Error (App.HttpStatus reply.status)
        }

let private classificationTests =
    testList "HTTP faults are classified, not flattened" [
        testCaseAsync "a transport failure is an unreachable feed, not an empty log" <|
            async {
                let get : App.HttpGet = fun _ -> async { return Error (App.HttpUnreachable "ECONNREFUSED") }
                let! result = App.EventFetch.overHttp get SessionRoute.relative None None
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
                let! unauthorized = App.EventFetch.overHttp (refusing 401) SessionRoute.relative None None
                let! overloaded = App.EventFetch.overHttp (refusing 503) SessionRoute.relative None None
                Expect.equal unauthorized (Error (App.FeedRefused 401)) "401 survives as 401"
                Expect.equal overloaded (Error (App.FeedRefused 503)) "503 survives as 503"
                Expect.isFalse (App.FeedFault.isTransient (App.FeedRefused 401)) "retrying cannot fix a 401"
                Expect.isTrue (App.FeedFault.isTransient (App.FeedRefused 503)) "a struggling session is worth waiting for"
                Expect.equal (App.FeedFault.describe (App.FeedRefused 401)) "not authorized" "and it says so in the UI"
            }

        testCaseAsync "a chunk that will not decode is corruption — a value, not a thrown page" <|
            async {
                let get : App.HttpGet =
                    fun url -> async { return Ok { Url = url; Body = "{\"not\":\"an envelope\"}" } }
                match! App.EventFetch.overHttp get SessionRoute.relative None None with
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
                match! App.EventFetch.overHttp realHttpGet (SessionRoute.at "http://127.0.0.1:1") None None with
                | Error (App.FeedUnreachable _) -> ()
                | other -> failwithf "a dead port must be an unreachable feed, got %A" other
            }
    ]

// --- The integration test ----------------------------------------------------------------

/// A stand-in for the Session Process's event cursor that can be switched off. When
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
                    // `overHttp` builds `<base>/events` or `<base>/events/after/{n}`. The
                    // real server answers a cursor with a redirect to the range it chose;
                    // this collapses the two hops, because what is under test here is the
                    // retry policy around the fetch, not the shape of the address.
                    let after =
                        let last = url.Substring (url.LastIndexOf '/' + 1)
                        match System.Int64.TryParse last with
                        | true, offset -> EventOffset.create offset |> expect |> Some
                        | _ -> None
                    let! page = host.Log.Read after EventChunk.size
                    // The fake answers at the address that asked — it does not redirect, and
                    // what is under test here is the retry policy, not the addressing.
                    return
                        Ok
                            { Url = url
                              Body =
                                page.Events
                                |> List.map (Codec.toString Codec.sessionEventEnvelope)
                                |> String.concat "\n" }
            } }

/// A client whose history arrives over `get` under the SHIPPED policy, with the policy's
/// interim reports dispatched into the model exactly as the browser wires them. `retries`
/// collects those reports so the test can assert the retry behaviour it cannot see from the
/// model alone (the model only ever holds the latest).
let private connectOverFeed (get: App.HttpGet) (retries: ResizeArray<FeedHealth>) =
    connectInMemoryClientVia (fun dispatch ->
        let feed =
            App.EventFetch.overHttp get SessionRoute.relative None
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

// --- The link's heartbeat -----------------------------------------------------------------

/// A clock the test advances by hand: `Sleep` parks until `Tick ()` releases everything
/// waiting on it. The rule under test counts ticks rather than measuring time, so this counts
/// them too — and these cases run in zero real time, like every other schedule here.
type private TestClock () =
    let waiting = ResizeArray<unit -> unit> ()

    member _.Sleep : TimeSpan -> Async<unit> =
        fun _ -> Async.FromContinuations (fun (cont, _, _) -> waiting.Add (fun () -> cont ()))

    /// Release everything parked, once. Snapshotted before releasing, because a beat that is
    /// released parks again immediately — and a tick that swept that up would be two ticks
    /// wearing one name.
    member _.Tick () =
        let due = waiting.ToArray ()
        waiting.Clear ()
        due |> Array.iter (fun release -> release ())

/// The shipped cadence on a clock the test owns, reporting what it decided.
let private linkPolicy (clock: TestClock) (seen: ResizeArray<Link.LinkEvent>) : Link.LinkPolicy =
    { Tick = TimeSpan.Zero
      QuietTicksBeforeDeath = Link.LinkPolicy.quietTicksBeforeDeath
      Sleep = clock.Sleep
      Observe = seen.Add }

let private died (seen: ResizeArray<Link.LinkEvent>) =
    seen |> Seq.exists (function Link.LinkDied _ -> true | _ -> false)

let private probes (seen: ResizeArray<Link.LinkEvent>) =
    seen |> Seq.filter (function Link.LinkProbed _ -> true | _ -> false) |> Seq.length

/// Anything that is not a heartbeat, for cases that only care that a frame is a frame.
let private traffic (payload: string) : SessionFrame<string> = State (StateSync payload)

let private linkTests =
    testList "A supervised link" [
        testCaseAsync "a link carrying traffic is never probed" <|
            async {
                let clock = TestClock ()
                let seen = ResizeArray ()
                let carrier, remote = Yession.SessionProcess.InMemoryChannel.createPair<string> ()
                let _link = Link.supervise (linkPolicy clock seen) carrier
                // Well past the death threshold, but never quiet for a whole tick.
                for i in 1 .. Link.LinkPolicy.quietTicksBeforeDeath * 3 do
                    do! remote.Send (traffic (string i))
                    clock.Tick ()
                Expect.equal (probes seen) 0 "a busy link is asked to prove nothing"
            }

        testCaseAsync "an answered probe keeps the link alive indefinitely" <|
            async {
                let clock = TestClock ()
                let seen = ResizeArray ()
                let carrier, remote = Yession.SessionProcess.InMemoryChannel.createPair<string> ()
                let _link = Link.supervise (linkPolicy clock seen) carrier
                // The other end supervises too, which is all that answering a probe takes.
                let _peer = Link.supervise (linkPolicy clock (ResizeArray ())) remote
                for _ in 1 .. Link.LinkPolicy.quietTicksBeforeDeath * 3 do
                    clock.Tick ()
                Expect.isFalse (died seen) "a link that answers is never given up on"
            }

        testCaseAsync "a link whose peer stops answering ends its own receive" <|
            async {
                let clock = TestClock ()
                let seen = ResizeArray ()
                let carrier, _remote = Yession.SessionProcess.InMemoryChannel.createPair<string> ()
                // `_remote` is never supervised and never speaks: the half-open transport a
                // backgrounded phone leaves behind — open, and carrying nothing.
                let link = Link.supervise (linkPolicy clock seen) carrier
                for _ in 1 .. Link.LinkPolicy.quietTicksBeforeDeath do
                    clock.Tick ()
                let! frame = link.Receive ()
                Expect.isNone frame "the pump is told the link is gone, rather than parked on it for ever"
            }

        testCaseAsync "a link whose peer stops answering tells the remote it has gone" <|
            async {
                let clock = TestClock ()
                let seen = ResizeArray ()
                let carrier, remote = Yession.SessionProcess.InMemoryChannel.createPair<string> ()
                let _link = Link.supervise (linkPolicy clock seen) carrier
                for _ in 1 .. Link.LinkPolicy.quietTicksBeforeDeath do
                    clock.Tick ()
                // The probes it never answered are still in flight ahead of the close, so what
                // is asserted is that the stream ENDS — not that the next frame is the end.
                let rec drain (budget: int) =
                    async {
                        if budget = 0 then return false
                        else
                            match! remote.Receive () with
                            | None -> return true
                            | Some _ -> return! drain (budget - 1)
                    }
                let! ended = drain (Link.LinkPolicy.quietTicksBeforeDeath + 1)
                Expect.isTrue ended "the far end learns too — a lease held by a peer that is gone is nobody's"
            }

        testCaseAsync "a probe is answered without ever reaching the pump" <|
            async {
                let clock = TestClock ()
                let carrier, remote = Yession.SessionProcess.InMemoryChannel.createPair<string> ()
                let link = Link.supervise (linkPolicy clock (ResizeArray ())) carrier
                do! remote.Send (Control Ping)
                do! remote.Send (traffic "after the probe")
                let! frame = link.Receive ()
                Expect.equal frame (Some (traffic "after the probe"))
                    "the heartbeat is invisible above this module — the pump sees only the real frame"
            }

        testCaseAsync "an inbound probe is answered" <|
            async {
                let clock = TestClock ()
                let carrier, remote = Yession.SessionProcess.InMemoryChannel.createPair<string> ()
                let _link = Link.supervise (linkPolicy clock (ResizeArray ())) carrier
                do! remote.Send (Control Ping)
                let! answer = remote.Receive ()
                Expect.equal answer (Some (Control Pong)) "a probe is answered by the supervisor itself"
            }

        testCaseAsync "a probe cannot race ahead of the handshake" <|
            async {
                // The fatal case if heartbeats were visible: `PeerSession.run` rejects any
                // first frame that is not `PeerHello`, and a refused peer PARKS rather than
                // retrying — so a probe arriving first would end the session for good.
                let token = "handshake-token"
                let peerId = PeerId.create "peer-ada" |> expect
                let clock = TestClock ()
                let log = Yession.SessionProcess.InMemoryEventLog.create (SessionId.create "handshake" |> expect) (fun () -> DateTimeOffset (2026, 6, 14, 0, 0, 0, TimeSpan.Zero))
                let clientCarrier, serverCarrier = Yession.SessionProcess.InMemoryChannel.createPair<string> ()
                let server = Link.supervise (linkPolicy clock (ResizeArray ())) serverCarrier
                let client = Link.supervise (linkPolicy clock (ResizeArray ())) clientCarrier
                Async.StartImmediate (
                    Yession.SessionProcess.PeerSession.run
                        (SessionId.create "handshake" |> expect)
                        (fun t -> if t = token then Some Yession.SessionProcess.UnattributedAccess else None)
                        log
                        Yession.SessionProcess.PeerSession.PeerHandlers.none
                        server)
                // Both ends probe each other before a word of the protocol is spoken.
                clock.Tick ()
                clock.Tick ()
                do! client.Send (Control (PeerHello { PeerId = peerId; DisplayName = "Ada"; Token = token }))
                let! response = client.Receive ()
                match response with
                | Some (Control (PeerAccepted _)) -> ()
                | other -> failwithf "expected the peer to be accepted, got %A" other
            }
    ]

/// A channel that has stopped carrying anything WITHOUT closing — the half-open transport a
/// backgrounded phone leaves behind. Once gagged, sends are accepted and discarded and
/// anything the far end says is swallowed, but no close is ever signalled: nothing above it
/// can tell the difference between this and a quiet session.
let private gaggable (channel: FrameChannel<'S>) : FrameChannel<'S> * (unit -> unit) =
    let silent = ref false
    let rec receive () =
        async {
            match! channel.Receive () with
            | Some frame when not silent.Value -> return Some frame
            | Some _ -> return! receive ()
            | None -> return None
        }
    { Send = fun frame -> async { if not silent.Value then do! channel.Send frame }
      Receive = receive
      Close = channel.Close },
    fun () -> silent.Value <- true

// --- The session lifecycle ----------------------------------------------------------------

/// A lifecycle wired to a real Host over in-memory channel pairs — the same rules the browser
/// runs, with WebRTC and Lit swapped for the two things a test can hold: a channel it can cut,
/// and a record of every attempt.
type private Lifecycle =
    { Runner : Harness.Runner<ClientModel, Ylmish.Program.Message<ClientMsg>>
      /// Compose a body and send it through the LIVE connection — the app's own atomic send,
      /// so the drain sees one update and the timeline is reached the way it really is.
      Say : string -> Async<unit>
      /// Cut the SERVER end of the current session — a Session Process dropping its peer.
      DropSession : unit -> Async<unit>
      /// How many times a transport was opened.
      Opens : unit -> int
      /// The resume offset each served session was started with, in order.
      Resumes : unit -> EventOffset option list
      /// Make the CURRENT session's transport go half-open: still open, carrying nothing.
      /// The failure the heartbeat exists for, and the one a closed channel cannot express.
      Silence : unit -> unit
      /// Advance the supervisor's clock by one tick.
      Tick : unit -> unit
      /// Every message the lifecycle routed, in order — the connection story as it happened.
      /// Asserted instead of the model, because over in-memory channels a whole reconnect can
      /// complete synchronously: a transient state like `Reconnecting` is real but is gone
      /// before anything could wait on it.
      Seen : unit -> ClientMsg list }

/// Start a lifecycle against `host`. `token` is the peer's credential — a bad one is refused by
/// the handshake, which is how the "never reconnect a refused peer" rule is exercised for real.
let private startLifecycle (host: Host.SessionHost) (token: string) (id: string) (name: string) : Lifecycle =
    let doc = Y.Doc.Create ()
    let local = peer id name
    let registry = BodyRegistry doc
    let runner = Harness.run (App.makeProgram doc (ClientModel.init local))
    let hello = { PeerId = local.PeerId; DisplayName = name; Token = token }
    let opens = ref 0
    let resumes = ref []
    let serverEnd : FrameChannel<string> option ref = ref None
    let live : App.Connection option ref = ref None
    // The link is supervised here exactly as the browser supervises it, on a clock this test
    // owns — so a half-open transport is noticed by the production rule, on demand.
    let clock = TestClock ()
    let silence : (unit -> unit) ref = ref ignore
    let seen = ResizeArray<ClientMsg> ()
    let record msg =
        seen.Add msg
        runner.Dispatch (user msg)
    Async.StartImmediate (
        App.SessionLifecycle.run
            (Resilience.Schedule.constant System.TimeSpan.Zero 0)
            { // What this harness drives is the FEED; the session leg makes one pass and stops
              // rather than supervising, so a refused peer here ends instead of parking.
              WaitBeforeRetry = fun _ -> async { return false }
              Open =
                fun () ->
                    async {
                        opens.Value <- opens.Value + 1
                        let clientEnd, server = Yession.SessionProcess.InMemoryChannel.createPair<string> ()
                        serverEnd.Value <- Some server
                        // The Host drives the server end exactly as it would a WebRTC connection.
                        host.Connect server
                        let carrier, gag = gaggable clientEnd
                        silence.Value <- gag
                        return Ok (Link.supervise (linkPolicy clock (ResizeArray ())) carrier)
                    }
              Serve =
                fun resumeAfter dispatch channel ->
                    async {
                        resumes.Value <- resumes.Value @ [ resumeAfter ]
                        let connection =
                            App.connect
                                { App.ConnectOptions.defaults with ResumeAfter = resumeAfter }
                                doc
                                registry
                                (TextRegistry doc)
                                hello
                                dispatch
                                channel
                        live.Value <- Some connection
                        do! connection.Run
                        live.Value <- None
                    }
              ReadPosition = fun () -> (runner.Model ()).EventConsumer.LastProcessedOffset
              Dispatch = record })
    { Runner = runner
      Say =
        fun markdown ->
            async {
                // A send needs a live session; after a drop that means the NEXT one.
                do! runner.WaitFor (fun m -> m.Connection = Connected)
                // The draft carries the queue key it will become, so any co-editor's send writes
                // the same one; this harness is the author, so it mints it here.
                runner.Dispatch (user (EnsureDraftMsg (local.PeerId, QueueId.create (string (System.Guid.NewGuid ())) |> expect)))
                do! runner.WaitFor (fun m -> Map.containsKey local.PeerId m.Synced.Drafts)
                Markdown.intoFragment markdown (registry.Fragment (BodyKey.draft local.PeerId))
                match live.Value with
                | Some connection -> connection.SendDraft local.PeerId
                | None -> failwith "no live connection to send through"
            }
      DropSession =
        fun () ->
            async {
                match serverEnd.Value with
                | Some server -> do! server.Close ()
                | None -> ()
            }
      Silence = fun () -> silence.Value ()
      Tick = fun () -> clock.Tick ()
      Opens = fun () -> opens.Value
      Resumes = fun () -> resumes.Value
      Seen = fun () -> List.ofSeq seen }

let private lifecycleTests =
    testList "The session lifecycle" [
        testCaseAsync "a dropped session reconnects and resumes from the offset it reached" <|
            async {
                let! host = Host.start (SessionId.create "lifecycle-resume" |> expect) 0
                let ada = startLifecycle host (host.MintPeerToken ()) "ada" "Ada"
                do! ada.Runner.WaitFor (fun m -> m.Connection = Connected)

                // Build history, so "resume from where it read" has something to mean.
                do! ada.Say "before the drop"
                do! ada.Runner.WaitFor (fun m -> bodies m = [ "before the drop" ])
                let readTo = (ada.Runner.Model ()).EventConsumer.LastProcessedOffset
                Expect.isTrue readTo.IsSome "the client consumed the message it sent"

                // The Session Process drops the peer. The old shell set `Reconnecting` and then
                // did nothing at all — a state that was a promise no code kept. Now the session
                // comes back on its own, and the client is usable again without a reload.
                do! ada.DropSession ()
                do! ada.Say "after the drop"
                do! ada.Runner.WaitFor (fun m -> bodies m = [ "before the drop"; "after the drop" ])

                Expect.equal (ada.Opens ()) 2 "it came back — once, not in a loop"
                Expect.equal
                    (ada.Resumes ())
                    [ None; readTo ]
                    "the first session read from the start; the second resumed where the model got to"
                Expect.equal
                    (bodies (ada.Runner.Model ()))
                    [ "before the drop"; "after the drop" ]
                    "history is continuous across the reconnect — no gap, no duplicate"

                // The connection story, in order: accepted, dropped (which is what puts the
                // model in `Reconnecting`), accepted again.
                let lifecycleStory =
                    ada.Seen ()
                    |> List.choose (function
                        | ConnectedMsg _ -> Some "connected"
                        | DisconnectedMsg -> Some "dropped"
                        | RejectedMsg _ | ConnectFailedMsg _ -> Some "settled"
                        | _ -> None)
                Expect.equal lifecycleStory [ "connected"; "dropped"; "connected" ] "one clean round trip"
                do! host.Stop ()
            }

        testCaseAsync "a message sent over a half-open transport reaches the session anyway" <|
            async {
                // The bug this whole mechanism exists for. A send is a CRDT write, so the queue
                // row appears whether or not the frame carrying it went anywhere; with the
                // transport half-open, nothing drained it and nothing said so, and the only
                // cure was reloading the page. What must happen instead: the link notices, the
                // lifecycle brings it back, and the queued message lands.
                let! host = Host.start (SessionId.create "lifecycle-half-open" |> expect) 0
                let ada = startLifecycle host (host.MintPeerToken ()) "ada" "Ada"
                do! ada.Runner.WaitFor (fun m -> m.Connection = Connected)
                do! ada.Say "while the link was honest"
                do! ada.Runner.WaitFor (fun m -> bodies m = [ "while the link was honest" ])

                // The transport dies without closing: open, and carrying nothing.
                ada.Silence ()
                do! ada.Say "into the void"

                // Nothing above the link can tell yet — so let the supervisor look.
                for _ in 1 .. Link.LinkPolicy.quietTicksBeforeDeath do
                    ada.Tick ()

                do! ada.Runner.WaitFor (fun m ->
                        bodies m = [ "while the link was honest"; "into the void" ])
                Expect.equal (ada.Opens ()) 2 "the link was noticed to be dead, and replaced"
                Expect.isTrue
                    (Map.isEmpty (ada.Runner.Model ()).Synced.Queue)
                    "and the queue that had been stuck on screen is drained"
                do! host.Stop ()
            }

        testCaseAsync "a refused peer is not reconnected — it is told why" <|
            async {
                let! host = Host.start (SessionId.create "lifecycle-refused" |> expect) 0
                // A session that refuses the handshake will refuse it again; coming back would
                // be a retry loop against a decision — the trap the feed's 401 also avoids.
                let ada = startLifecycle host "not-a-minted-token" "ada" "Ada"
                do! ada.Runner.WaitFor (fun m ->
                        match m.Connection with
                        | Disconnected (Some _) -> true
                        | _ -> false)
                Expect.equal (ada.Opens ()) 1 "the transport was opened once and never again"
                do! host.Stop ()
            }

        testCaseAsync "a transport that cannot be opened keeps being tried, and says so each time" <|
            async {
                // The policy at the composition site spends its budget on ONE attempt; this is
                // the slower outer loop, and the difference it draws is between "this attempt
                // failed" and "give up on the session" — which are not the same claim. A client
                // that made them the same claim left "reload the tab" as the only cure for a
                // laptop that had been closed for a minute.
                //
                // The wait is a port, so this drives the rule with no clock in it at all.
                let opens = ref 0
                let waits = ResizeArray<System.TimeSpan option> ()
                let dispatched = ResizeArray<ClientMsg> ()
                let model = ref (ClientModel.init (peer "ada" "Ada"))
                do!
                    App.SessionLifecycle.run
                        (App.SessionLifecycle.supervision (fun () -> 0.0))
                        { Open =
                            fun () ->
                                async {
                                    opens.Value <- opens.Value + 1
                                    return Error App.ChannelTimedOut
                                }
                          Serve = fun _ _ _ -> async { failwith "must not serve a channel it never opened" }
                          ReadPosition = fun () -> None
                          // Three failures is enough to show it keeps going and to read the
                          // schedule off; a real client stops when the page does.
                          WaitBeforeRetry =
                            fun delay ->
                                async {
                                    waits.Add delay
                                    return waits.Count < 3
                                }
                          Dispatch =
                            fun msg ->
                                dispatched.Add msg
                                model.Value <- ClientModel.update msg model.Value }
                Expect.equal opens.Value 3 "it kept trying rather than settling on the first failure"
                Expect.equal
                    (List.ofSeq dispatched)
                    [ ConnectingMsg
                      ConnectFailedMsg "the session did not answer"
                      ConnectingMsg
                      ConnectFailedMsg "the session did not answer"
                      ConnectingMsg
                      ConnectFailedMsg "the session did not answer" ]
                    "each attempt is announced and each failure reported with its reason"
                Expect.isTrue
                    (waits |> Seq.forall Option.isSome)
                    "an unreachable session is a scheduled wait, never the indefinite park"
                Expect.isTrue
                    (waits |> Seq.forall (fun d -> d.Value <= System.TimeSpan.FromMinutes 1.0))
                    "and the backoff is capped, so a session that never returns is not polled less and less for ever"
                Expect.equal
                    model.Value.Connection
                    (Disconnected (Some "the session did not answer"))
                    "and the model holds why, instead of an eternal 'connecting'"
            }

        testCaseAsync "an accepted session earns exactly one more attempt; the announcement is not repeated" <|
            async {
                // The loop's arithmetic, isolated from any transport: acceptance (and only
                // acceptance) buys another pass, and `Reconnecting` is never overwritten by a
                // second `Connecting` announcement.
                let accepted : PeerAcceptedPayload =
                    { SessionId = SessionId.create "lifecycle-unit" |> expect
                      AssignedDisplayName = "Ada"
                      LatestOffset = None }
                let serves = ref 0
                let waits = ResizeArray<System.TimeSpan option> ()
                let dispatched = ResizeArray<ClientMsg> ()
                do!
                    App.SessionLifecycle.run
                        (App.SessionLifecycle.supervision (fun () -> 0.0))
                        { Open = fun () -> async { return Ok () }
                          Serve =
                            fun _ dispatch _ ->
                                async {
                                    serves.Value <- serves.Value + 1
                                    if serves.Value = 1 then
                                        // Accepted, then the channel closed under it.
                                        dispatch (ConnectedMsg accepted)
                                        dispatch DisconnectedMsg
                                    else
                                        // Refused this time: no schedule can help, so it parks.
                                        dispatch (RejectedMsg "peer token expired")
                                }
                          ReadPosition = fun () -> None
                          WaitBeforeRetry =
                            fun delay ->
                                async {
                                    waits.Add delay
                                    return false
                                }
                          Dispatch = dispatched.Add }
                Expect.equal serves.Value 2 "the ended session earned one more attempt straight away"
                Expect.equal
                    (List.ofSeq waits)
                    [ None ]
                    "and the refusal waited to be ASKED rather than backing off — a token no amount of waiting fixes"
                Expect.equal
                    (dispatched |> Seq.filter (fun m -> m = ConnectingMsg) |> Seq.length)
                    1
                    "announced once, on the first attempt — a reconnect keeps `Reconnecting`"
            }
    ]

// --- What the failure looks like ----------------------------------------------------------

let private surfaceTests =
    testList "Degradation is visible and never blocking" [
        testCase "a disconnected client is offered a way to ask again; a connected one is not" <| fun () ->
            // The affordance GAPS asked for, and the availability invariant behind it: the
            // supervised loop is already trying, but the one client it deliberately will not
            // carry — a peer whose token was refused — parks until a person asks, and a park
            // with nothing to press is a dead end wearing a reason.
            let model = ClientModel.init (peer "ada" "Ada")
            let disconnected =
                Support.render { model with Connection = Disconnected (Some "peer token expired") }
            Expect.isTrue
                (disconnected.Contains "data-retry-now")
                "a settled disconnection offers the ask"
            let connected = Support.render { model with Connection = Connected }
            Expect.isFalse
                (connected.Contains "data-retry-now")
                "a working session offers nothing to retry"

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

        testCase "history this device does not hold is said when nothing is coming to fill it" <| fun () ->
            // A conversation that starts in the middle, on a client that cannot reach its
            // session: nothing here can repair it, so the timeline says what it is instead of
            // letting a truncated history read as the whole of one.
            let model = ClientModel.init (peer "ada" "Ada")
            let html =
                Support.render
                    { model with
                        Connection = Disconnected (Some "session unreachable")
                        // As the replay leaves it: the walk that found the hole is the walk
                        // that finished looking.
                        HistoryRead = true
                        EventConsumer =
                            { model.EventConsumer with MissingBefore = Some (EventOffset.create 22L |> expect) } }
            Expect.isTrue (html.Contains "data-history-gap") "the timeline says its earlier half is not here"

        testCase "a gap the feed is already filling says nothing" <| fun () ->
            // The regression that made this its own fact: a hole is found before a single
            // read leaves the client, and the read that follows resumes exactly where the
            // fill has to start. Reported as feed health it flashed a red "history paused"
            // over the cold open of anyone whose store had one — for the one round trip it
            // took to repair itself.
            let model = ClientModel.init (peer "ada" "Ada")
            let html =
                Support.render
                    { model with
                        Connection = Connected
                        EventConsumer =
                            { model.EventConsumer with MissingBefore = Some (EventOffset.create 22L |> expect) } }
            Expect.isFalse (html.Contains "data-history-gap") "a hole being filled is catch-up, not a fact worth a line"
            Expect.isFalse (html.Contains Dom.Hooks.degraded) "and nothing about it is a degradation"

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
    testList "Transport resilience" [
        scheduleTests
        guardTests
        classificationTests
        feedFailureTests
        channelTests
        linkTests
        lifecycleTests
        surfaceTests
    ]
