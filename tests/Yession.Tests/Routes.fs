module Yession.Tests.Routes

// The session's HTTP contract (`Yession.App.SessionRoute`): the paths its server matches,
// its shell emits, and its browser client fetches, all from one declaration. Pure — the
// cheapest tier covers it.

open Fable.Pyxpecto
open Yession.Domain
open Yession.App

let private offset (n: int64) =
    match EventOffset.create n with
    | Ok o -> o
    | Error e -> failwith e

/// Every route, so the properties below are checked against the whole surface rather than a
/// remembered subset. A new case fails `relative`'s and `parse`'s matches until handled;
/// this list is what makes it also fail these tests until it is listed.
let private every =
    [ Shell
      // A static file, at the depth a build actually emits: one at the top of the set and one
      // in a directory, because the route carries the whole tail and `relative` and `parse`
      // have to stay exact inverses over both.
      Asset ("K3nR7pQx2wL0", "client.js")
      Asset ("8fZs1mVb4tHc", "fonts/monaspace-neon-latin-300-normal.woff2")
      // The worker sits at the mount root rather than in the asset set, because a worker
      // controls its own path and below: under `assets/<build>/` it would control the assets
      // and nothing else.
      ServiceWorker
      Signal
      Me
      Login
      Callback
      // The event log's two halves: the cursor a client sends (both forms — from the
      // beginning, and from an offset it has folded through) and the range the server
      // redirects it to, which is the only form that carries events.
      EventsAfter None
      EventsAfter (Some (offset 0L))
      EventsAfter (Some (offset 4711L))
      Events (0L, 99L)
      Events (100L, 136L)
      // A terminal's transcript chunks (Plan 13) and the keyframes beside them (Plan 14):
      // deeper paths than anything else here, and the pair the router has to keep apart —
      // `terminals/<id>/<n>` and `terminals/<id>/keyframes/<n>` differ only by depth.
      TerminalTranscript ("term-a", 0)
      TerminalTranscript ("term-a", 3)
      TerminalKeyframe ("term-a", 0)
      TerminalKeyframe ("term-a", 41)
      ClaudeStatus
      Claude ClaudeAction.Begin
      Claude ClaudeAction.Complete
      Claude ClaudeAction.Token
      Claude ClaudeAction.Disconnect
      Queries ]

let private methodOf (route: SessionRoute) =
    match route with
    | Signal
    | Claude _ -> "POST"
    | _ -> "GET"

let private routeTests =
    testList "Session route contract" [
        testCase "no route renders root-anchored" <| fun () ->
            // The property the type exists for: a session may be mounted under a path by an
            // operator's proxy, so a leading slash would send the browser to the origin root
            // — the Manager, or nothing. `relative` is the only renderer, and it never emits
            // one, which is why no caller can write that bug.
            for route in every do
                Expect.isFalse
                    ((SessionRoute.relative route).StartsWith "/")
                    (sprintf "%A renders relative to the mount point" route)

        testCase "every route round-trips through its own rendering" <| fun () ->
            // Rendering and matching are two directions of one declaration; this is what
            // stops them drifting the way three hand-written copies of "/client.js" could.
            for route in every do
                let path = "/" + SessionRoute.relative route
                Expect.equal
                    (SessionRoute.parse (methodOf route) path)
                    (Some route)
                    (sprintf "%A parses back from %s" route path)

        testCase "the shell is the mount point itself" <| fun () ->
            Expect.equal (SessionRoute.relative Shell) "" "so `<base href>` alone addresses it"
            Expect.equal (SessionRoute.parse "GET" "/") (Some Shell) "served at the mount root"

        testCase "a route reached with the wrong method is no route at all" <| fun () ->
            // None, not a 405: an unknown path and a method mismatch answer identically, as
            // they did when the server matched on (method, path) pairs directly.
            Expect.equal (SessionRoute.parse "GET" "/signal") None "signalling is POST only"
            Expect.equal (SessionRoute.parse "POST" "/me") None "the auth probe is GET only"
            Expect.equal (SessionRoute.parse "POST" "/claude") None "the status read is GET only"
            Expect.equal (SessionRoute.parse "GET" "/claude/begin") None "the panel actions are POST only"

        testCase "the cursor takes an offset, or none at all" <| fun () ->
            Expect.equal (SessionRoute.parse "GET" "/events") (Some (EventsAfter None)) "no cursor reads from the beginning"
            Expect.equal (SessionRoute.parse "GET" "/events/after/12") (Some (EventsAfter (Some (offset 12L)))) "the cursor is parsed"
            Expect.equal (SessionRoute.parse "GET" "/events/after/-1") None "a negative offset is not an offset"
            Expect.equal (SessionRoute.parse "GET" "/events/after/x") None "a non-numeric cursor is rejected"
            Expect.equal (SessionRoute.parse "GET" "/events/after") None "the cursor needs its offset"

        testCase "a range must name bounds this server could actually have" <| fun () ->
            // The rule that keeps a partial answer from being kept for ever at an address
            // that promises a whole range: a range the parse will not accept is a 404, and
            // 404 is the only safe answer to an address whose bytes are not settled.
            Expect.equal (SessionRoute.parse "GET" "/events/0-99") (Some (Events (0L, 99L))) "a range is parsed"
            Expect.equal (SessionRoute.parse "GET" "/events/100-100") (Some (Events (100L, 100L))) "one event is a range"
            Expect.equal (SessionRoute.parse "GET" "/events/9-8") None "an inverted range is not a route"
            Expect.equal (SessionRoute.parse "GET" "/events/-1-4") None "a negative bound is not a route"
            Expect.equal
                (SessionRoute.parse "GET" (sprintf "/events/0-%d" EventChunk.size))
                None
                "a range longer than one answer carries is not a route"
            Expect.equal (SessionRoute.parse "GET" "/events/0-") None "a half-written range is not a route"
            Expect.equal (SessionRoute.parse "GET" "/events/x-y") None "a non-numeric range is rejected"

        testCase "an unknown path is not a route" <| fun () ->
            Expect.equal (SessionRoute.parse "GET" "/nope") None "unclaimed"
            Expect.equal (SessionRoute.parse "GET" "/claude/nope") None "an unknown action is not a route"

        testCase "a mounted session's absolute URLs join with exactly one slash" <| fun () ->
            // What a client outside a browser uses, having no document base to resolve
            // against — and where a caller could otherwise double or drop the separator.
            Expect.equal
                (SessionRoute.at "https://example.com/s/abc" (Events (300L, 336L)))
                "https://example.com/s/abc/events/300-336"
                "path-mounted"
            Expect.equal
                (SessionRoute.at "http://127.0.0.1:54321/" Signal)
                "http://127.0.0.1:54321/signal"
                "a trailing slash on the address does not double up"
            Expect.equal
                (SessionRoute.at "http://127.0.0.1:54321" Shell)
                "http://127.0.0.1:54321/"
                "the shell is the address itself"
    ]

let private mountTests =
    // A session mounted under a path. The operator's proxy forwards the public path
    // unchanged; the session strips its own prefix. The alternative contract — proxy
    // strips, session serves at root — would make correctness depend on per-proxy
    // rewriting behaviour, which cannot be verified here.
    let mount = "/s/01hx"
    testList "A path-mounted session" [
        testCase "claims its own prefix and nothing outside it" <| fun () ->
            Expect.equal (SessionRoute.parseUnder mount "GET" "/s/01hx/assets/abc123/client.js") (Some (Asset ("abc123", "client.js"))) "its bundle"
            Expect.equal (SessionRoute.parseUnder mount "POST" "/s/01hx/signal") (Some Signal) "its signalling"
            Expect.equal (SessionRoute.parseUnder mount "GET" "/s/01hx/events") (Some (EventsAfter None)) "its event cursor"
            Expect.equal (SessionRoute.parseUnder mount "GET" "/s/01hx/events/0-99") (Some (Events (0L, 99L))) "its event ranges"
            Expect.equal (SessionRoute.parseUnder mount "GET" "/assets/abc123/client.js") None "the origin root is not its own"
            Expect.equal (SessionRoute.parseUnder mount "GET" "/s/other/assets/abc123/client.js") None "a sibling's prefix is not its own"
            Expect.equal (SessionRoute.parseUnder mount "GET" "/s/01hxtra/assets/abc123/client.js") None "a prefix it merely starts with is not its own"

        testCase "is reached at its mount with or without a trailing slash" <| fun () ->
            Expect.equal (SessionRoute.parseUnder mount "GET" "/s/01hx/") (Some Shell) "the usual form"
            Expect.equal (SessionRoute.parseUnder mount "GET" "/s/01hx") (Some Shell) "and the bare mount"

        testCase "an unmounted session is unchanged" <| fun () ->
            Expect.equal (SessionRoute.parseUnder "" "GET" "/assets/abc123/client.js") (Some (Asset ("abc123", "client.js"))) "an origin-root session claims the path as written"
            Expect.equal (SessionRoute.parseUnder "" "GET" "/") (Some Shell) "including its shell"

        testCase "what the browser asks for is what the session claims" <| fun () ->
            // The round trip that matters end to end: `<base href="<mount>/">` makes the
            // browser resolve each relative route to `<mount>/<relative>`, and that is
            // exactly what `parseUnder` accepts back. If either side changed alone, this
            // would fail.
            for route in every do
                let asked = mount + "/" + SessionRoute.relative route
                Expect.equal
                    (SessionRoute.parseUnder mount (methodOf route) asked)
                    (Some route)
                    (sprintf "%A resolves to %s and comes back" route asked)
    ]

let tests = testList "Routes" [ routeTests; mountTests ]
