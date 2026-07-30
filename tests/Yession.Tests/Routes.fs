module Yession.Tests.Routes

// The session's HTTP contract (`Yession.App.SessionRoute`): the paths its server matches,
// its shell emits, and its browser client fetches, all from one declaration. Pure — the
// cheapest tier covers it.

open Fable.Pyxpecto
open Yession.App

/// Every route, so the properties below are checked against the whole surface rather than a
/// remembered subset. A new case fails `relative`'s and `parse`'s matches until handled;
/// this list is what makes it also fail these tests until it is listed.
let private every =
    [ Shell
      ClientBundle
      AppCss
      Signal
      Me
      Login
      Callback
      Events 0
      Events 7
      ClaudeStatus
      Claude ClaudeAction.Begin
      Claude ClaudeAction.Complete
      Claude ClaudeAction.Token
      Claude ClaudeAction.Disconnect ]

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

        testCase "an event chunk carries its index; a malformed one is not a route" <| fun () ->
            Expect.equal (SessionRoute.parse "GET" "/events/12") (Some (Events 12)) "the index is parsed"
            Expect.equal (SessionRoute.parse "GET" "/events/-1") None "a negative index is rejected"
            Expect.equal (SessionRoute.parse "GET" "/events/x") None "a non-numeric index is rejected"
            Expect.equal (SessionRoute.parse "GET" "/events") None "the collection itself is not served"

        testCase "an unknown path is not a route" <| fun () ->
            Expect.equal (SessionRoute.parse "GET" "/nope") None "unclaimed"
            Expect.equal (SessionRoute.parse "GET" "/claude/nope") None "an unknown action is not a route"

        testCase "a mounted session's absolute URLs join with exactly one slash" <| fun () ->
            // What a client outside a browser uses, having no document base to resolve
            // against — and where a caller could otherwise double or drop the separator.
            Expect.equal
                (SessionRoute.at "https://example.com/s/abc" (Events 3))
                "https://example.com/s/abc/events/3"
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
            Expect.equal (SessionRoute.parseUnder mount "GET" "/s/01hx/client.js") (Some ClientBundle) "its bundle"
            Expect.equal (SessionRoute.parseUnder mount "POST" "/s/01hx/signal") (Some Signal) "its signalling"
            Expect.equal (SessionRoute.parseUnder mount "GET" "/s/01hx/events/4") (Some (Events 4)) "its event chunks"
            Expect.equal (SessionRoute.parseUnder mount "GET" "/client.js") None "the origin root is not its own"
            Expect.equal (SessionRoute.parseUnder mount "GET" "/s/other/client.js") None "a sibling's prefix is not its own"
            Expect.equal (SessionRoute.parseUnder mount "GET" "/s/01hxtra/client.js") None "a prefix it merely starts with is not its own"

        testCase "is reached at its mount with or without a trailing slash" <| fun () ->
            Expect.equal (SessionRoute.parseUnder mount "GET" "/s/01hx/") (Some Shell) "the usual form"
            Expect.equal (SessionRoute.parseUnder mount "GET" "/s/01hx") (Some Shell) "and the bare mount"

        testCase "an unmounted session is unchanged" <| fun () ->
            Expect.equal (SessionRoute.parseUnder "" "GET" "/client.js") (Some ClientBundle) "byte-identical to today"
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
