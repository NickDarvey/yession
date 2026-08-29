module Yession.Tests.Queries

// The query surface (Plan 15). What is worth pinning here is not "a registry stores
// things" — it is the handful of properties the WHOLE design leans on:
//
//   * a query's answer is held to the shape it declared, so one renderer can draw every
//     query that will ever be registered without a case for the one that lied;
//   * the stream's opening burst IS the snapshot, so a subscriber needs nothing else and
//     there is no second mechanism to keep in step;
//   * a command is the only thing that can change an answer, so a command is the only
//     thing that has to say so — nothing polls;
//   * the door is shut: reading session state needs a session identity.

open Fable.Core
open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Tools
open Yession.Host
open Yession.SessionProcess

let private expect result =
    match result with
    | Ok v -> v
    | Error e -> failwithf "invariant: %A" e

let private name (raw: string) = QueryName.create raw |> expect

let private rowsShape =
    Rows [ QueryColumn.create "repo" "repo"; QueryColumn.create "dirty" "uncommitted changes" ]

let private def (raw: string) (shape: QueryShape) : QueryDef =
    { Name = name raw
      Title = raw
      Description = sprintf "the %s of this session" raw
      Shape = shape }

let private constant (raw: string) (shape: QueryShape) (value: QueryValue) : Queries.QueryRegistration =
    { Def = def raw shape
      Read = fun () -> async { return Ok value } }

// --- the shape contract -------------------------------------------------------------------

let private shapeTests =
    testList "a query answers in the shape it declares" [

        // The registry is where this is enforced, not the renderer and not the model: a
        // capability that answers a `Rows` query with a scalar is a bug in the capability,
        // and every consumer downstream would otherwise have to carry a case for it.
        testCaseAsync "a mismatched answer is an error, not a value nothing can draw" <|
            async {
                let registry =
                    Queries.create [ constant "repos" rowsShape (ValueOf (CellText "surprise")) ] |> expect
                match! registry.Read (name "repos") with
                | Ok value -> failwithf "expected the shape to be refused, got %A" value
                | Error e -> Expect.isTrue (e.Contains "shape") "the error names the problem"
            }

        testCaseAsync "an unregistered name is refused by name" <|
            async {
                let registry = Queries.create [] |> expect
                match! registry.Read (name "nothing") with
                | Ok _ -> failwith "expected an error"
                | Error e -> Expect.isTrue (e.Contains "nothing") "the error names what was asked for"
            }

        // Two queries answering to one name would make the stream ambiguous (which value is
        // `repos`?) and the agent's tool list contradictory. Refused at composition, where
        // it is a startup failure rather than a mystery at runtime.
        testCase "a name registered twice is refused" <| fun () ->
            match Queries.create [ constant "repos" Value (ValueOf (CellText "a")); constant "repos" Value (ValueOf (CellText "b")) ] with
            | Ok _ -> failwith "expected a duplicate to be refused"
            | Error e -> Expect.isTrue (e.Contains "twice") "the error says what happened"

        // The agent and the humans read ONE value. The text form is what the model gets;
        // it renders the same cells, with the same human labels, as the table.
        testCase "the agent's rendering carries the columns' labels and the flags as words" <| fun () ->
            let text =
                QueryValue.describe
                    rowsShape
                    (RowsOf [ [ "repo", CellText "octo/hello"; "dirty", CellFlag true ] ])
            Expect.equal text "repo: octo/hello, uncommitted changes: yes" "one line per row, labelled"

        // A column a row does not carry is NOT the empty string: "no answer" and "answered
        // with nothing" are different facts, and a model told the latter will act on it.
        testCase "a missing cell renders as absent, in both renderings" <| fun () ->
            let text = QueryValue.describe rowsShape (RowsOf [ [ "repo", CellText "octo/hello" ] ])
            Expect.equal text "repo: octo/hello, uncommitted changes: —" "the absent cell is marked"

        testCase "an empty rows answer says so rather than rendering blank" <| fun () ->
            Expect.equal (QueryValue.describe rowsShape (RowsOf [])) "(none)" "the model is told there are none"

        // The tool description is what a model reads to decide whether to call the tool.
        // Until the in-process MCP transport carries an `outputSchema`, the shape has to be
        // in the words, so this pins that it is.
        testCase "the tool description states the shape and the read-only contract" <| fun () ->
            let described = QueryDef.toolDescription (def "repos" rowsShape)
            Expect.isTrue (described.Contains "rows of: repo, uncommitted changes") "the columns are named"
            Expect.isTrue (described.Contains "Read-only") "the contract is stated"
    ]

// --- names ---------------------------------------------------------------------------------

let private nameTests =
    testList "a query name" [
        // One string is the MCP tool name, the URL segment and the stream key, so it is
        // constrained to what all three carry without escaping.
        testCase "accepts the MCP tool-naming shape and refuses the rest" <| fun () ->
            let ok = [ "repos"; "work_sandboxes"; "a1" ]
            let bad = [ ""; "Repos"; "work sandboxes"; "work-sandboxes"; "_repos"; "repos_"; "repos/x"; "../x" ]
            for raw in ok do
                Expect.isTrue (Result.isOk (QueryName.create raw)) (sprintf "'%s' is a name" raw)
            for raw in bad do
                Expect.isTrue (Result.isError (QueryName.create raw)) (sprintf "'%s' is not a name" raw)
    ]

// --- the stream, in the registry ------------------------------------------------------------

/// Collect frames a subscription delivers. The registry's reads are `async`, and under
/// Node an `async` that never awaits still resolves on a later microtask — so a collector
/// plus a yield is the honest way to observe the burst, not a synchronous assertion.
let private collect () =
    let frames = ResizeArray<QueryFrame> ()
    frames, (fun frame -> frames.Add frame)

let private settle () : Async<unit> =
    Async.FromContinuations (fun (cont, _, _) -> JS.setTimeout (fun () -> cont ()) 0 |> ignore)

let private streamTests =
    testList "the multiplexed stream" [

        // THE property that lets there be exactly one route: a subscriber connects and is
        // told everything — what exists, and what each one says. If this burst were partial,
        // a snapshot fetch would have to exist beside the stream, and the two would drift.
        testCaseAsync "connecting declares every query and then answers every one of them" <|
            async {
                let registry =
                    Queries.create
                        [ constant "repos" rowsShape (RowsOf [ [ "repo", CellText "octo/hello"; "dirty", CellFlag false ] ])
                          constant "leases" Value (ValueOf (CellText "none")) ]
                    |> expect
                let frames, sink = collect ()
                let subscription = registry.Subscribe sink
                do! settle ()
                match List.ofSeq frames with
                | QueriesDeclared defs :: values ->
                    Expect.equal
                        (defs |> List.map (fun d -> QueryName.value d.Name))
                        [ "repos"; "leases" ]
                        "the declarations come first, in registration order"
                    Expect.equal
                        (values |> List.map (function QueryValued (n, _) -> QueryName.value n | _ -> "?"))
                        [ "repos"; "leases" ]
                        "then every query's current value"
                | other -> failwithf "expected declarations then values, got %A" other
                subscription.Stop ()
            }

        // Nothing polls. A value reaches a screen because the command that changed it said
        // so — which is also why the whole design can afford to hold no cache.
        testCaseAsync "invalidating a query pushes its fresh value, and only its own" <|
            async {
                let mutable answer = "before"
                let registry =
                    Queries.create
                        [ { Def = def "repos" Value
                            Read = fun () -> async { return Ok (ValueOf (CellText answer)) } }
                          constant "leases" Value (ValueOf (CellText "none")) ]
                    |> expect
                let frames, sink = collect ()
                let subscription = registry.Subscribe sink
                do! settle ()
                frames.Clear ()
                answer <- "after"
                registry.Invalidate (name "repos")
                do! settle ()
                Expect.equal
                    (List.ofSeq frames)
                    [ QueryValued (name "repos", ValueOf (CellText "after")) ]
                    "one frame, for the query that changed, carrying a freshly read value"
                subscription.Stop ()
            }

        // A failing read does not blank a panel: the subscriber keeps what it last knew,
        // and the failure surfaces where somebody can act on it (the agent's tool call).
        testCaseAsync "a query that cannot be answered pushes nothing" <|
            async {
                let registry =
                    Queries.create
                        [ { Def = def "repos" Value
                            Read = fun () -> async { return Error "the checkout is gone" } } ]
                    |> expect
                let frames, sink = collect ()
                let subscription = registry.Subscribe sink
                do! settle ()
                frames.Clear ()
                registry.Invalidate (name "repos")
                do! settle ()
                Expect.equal (List.ofSeq frames) [] "no frame carries a failure"
                subscription.Stop ()
            }

        // `RetainedHub`'s contract, at per-query grain: every frame is the query's WHOLE
        // value, so a reconnect is the entire recovery protocol and a client never has to
        // reconstruct state from a sequence it might have gaps in.
        testCaseAsync "a change carries the whole value, never a delta" <|
            async {
                let mutable rows =
                    [ [ "repo", CellText "octo/hello"; "dirty", CellFlag false ] ]
                let registry =
                    Queries.create
                        [ { Def = def "repos" rowsShape
                            Read = fun () -> async { return Ok (RowsOf rows) } } ]
                    |> expect
                let frames, sink = collect ()
                let subscription = registry.Subscribe sink
                do! settle ()
                frames.Clear ()
                // A SECOND repo appears. A delta protocol would send the new row; this
                // sends both, which is what makes the frame self-sufficient.
                rows <- rows @ [ [ "repo", CellText "octo/other"; "dirty", CellFlag true ] ]
                registry.Invalidate (name "repos")
                do! settle ()
                match List.ofSeq frames with
                | [ QueryValued (_, RowsOf sent) ] ->
                    Expect.equal (List.length sent) 2 "the frame carries every row, not the one that changed"
                | other -> failwithf "expected one whole-value frame, got %A" other
                subscription.Stop ()
            }

        testCaseAsync "a stopped subscription stops receiving" <|
            async {
                let registry = Queries.create [ constant "repos" Value (ValueOf (CellText "x")) ] |> expect
                let frames, sink = collect ()
                let subscription = registry.Subscribe sink
                do! settle ()
                subscription.Stop ()
                frames.Clear ()
                registry.Invalidate (name "repos")
                do! settle ()
                Expect.equal (List.ofSeq frames) [] "teardown is real"
            }
    ]

// --- the wire ------------------------------------------------------------------------------

let private codecTests =
    testList "the query wire" [

        // The client renders a query it was never compiled against, so the DECLARATION has
        // to survive the round trip as faithfully as the value does.
        testCase "declarations and values round-trip in every shape" <| fun () ->
            let frames =
                [ QueriesDeclared
                    [ def "repos" rowsShape
                      def "work_environment" (Fields [ QueryColumn.create "backend" "backend" ])
                      def "leases" Value ]
                  QueryValued (name "repos", RowsOf [ [ "repo", CellText "octo/hello"; "dirty", CellFlag true ] ])
                  QueryValued (name "repos", RowsOf [])
                  QueryValued (name "work_environment", FieldsOf [ "backend", CellText "srt" ])
                  QueryValued (name "leases", ValueOf CellAbsent) ]
            for frame in frames do
                let json = Codec.toString Codec.queryFrame frame
                Expect.equal (Codec.fromString Codec.queryFrame json) (Ok frame) (sprintf "round trip: %s" json)

        // A cell rides as its native JSON type, which is what keeps the stream readable to
        // anything that consumes it without this codec.
        testCase "cells ride as JSON strings, booleans and null" <| fun () ->
            let json =
                Codec.toString
                    Codec.queryFrame
                    (QueryValued (name "repos", RowsOf [ [ "repo", CellText "octo/hello"; "dirty", CellFlag true; "note", CellAbsent ] ]))
            Expect.isTrue (json.Contains "\"dirty\":true") "a flag is a boolean"
            Expect.isTrue (json.Contains "\"note\":null") "an absent cell is null"

        testCase "a toned cell round-trips, and a bare string is still text" <| fun () ->
            // The toned form is an object because it carries two facts, and it is decoded
            // LAST — `oneOf` takes the first decoder that succeeds, so a bare string must
            // not be claimable by anything but `CellText`.
            let frame =
                QueryValued (
                    name "repos",
                    RowsOf [ [ "repo", CellText "octo/hello"; "dirty", CellStatus ("checks red", ToneBad) ] ])
            let json = Codec.toString Codec.queryFrame frame
            Expect.equal (Codec.fromString Codec.queryFrame json) (Ok frame) "the tone survives the wire"
            Expect.isTrue (json.Contains "\"tone\":\"bad\"") "spelled as a word, so the stream stays readable"

        testCase "a tone the client does not know is an error, never a silent default" <| fun () ->
            // A tone it drew as "whatever the fall-through was" would be a wrong verdict
            // rendered confidently, which is worse than a frame that failed to decode.
            Expect.isError
                (Codec.fromString
                    Codec.queryFrame
                    """{"kind":"valued","name":"repos","value":{"rows":[{"dirty":{"text":"x","tone":"lilac"}}]}}""")
                "an unknown tone is refused"

        testCase "an unknown frame tag decodes to an error, never a crash" <| fun () ->
            Expect.isTrue
                (Result.isError (Codec.fromString Codec.queryFrame """{"tag":"whatever"}"""))
                "a future frame kind is refused legibly"
    ]

// --- the route (Ports) -----------------------------------------------------------------------

/// `who=<name>` is an identity; anything else is nobody. The OIDC round trip is Oidc.fs's
/// subject — what is under test here is what the ROUTE does with an identity.
let private stubAuth () : SessionAuth.Auth =
    { Configure = fun _ _ _ _ -> async { return Ok () }
      IsAuthenticated = fun req -> (Interop.headerOf req "cookie").IsSome
      IdentityOf =
        fun req ->
            match Interop.headerOf req "cookie" with
            | Some cookie when cookie.StartsWith "who=" ->
                Some
                    ({ Subject = cookie.Substring 4
                       DisplayName = None
                       Attribution = AttributedUser (UserId.create (cookie.Substring 4) |> expect) } : CookieIdentity)
            | _ -> None
      BeginLogin = fun _ -> async { return None }
      HandleCallback = fun _ -> async { return Error (500, "not under test") }
      CookieName = "who" }

type private HttpReply = { status: int; body: string }

[<Emit("""fetch($0, { headers: { cookie: $1 }, cache: 'no-store' }).then(async r => ({ status: r.status, body: await r.text() }))""")>]
let private getWithCookie (url: string) (cookie: string) : JS.Promise<HttpReply> = Util.jsNative

let private startQueryRoutes (registry: Queries.QueryRegistry) =
    async {
        let route = Queries.routes (stubAuth ()) registry ""
        let handler (req: Interop.IncomingMessage) (res: Interop.ServerResponse) =
            if not (route req res) then
                res.writeHead (404, Fable.Core.JsInterop.createObj [ "content-type", box "text/plain" ]) |> ignore
                res.``end`` "not found"
        let server = Interop.createServer handler
        let! listening =
            Async.FromContinuations (fun (cont, _, _) -> server.listen (0, "127.0.0.1", fun () -> cont server) |> ignore)
        return sprintf "http://127.0.0.1:%d" (Interop.serverPort listening)
    }

/// Wait until `predicate` holds over the frames received so far, or give up. The stream is
/// a push leg — there is nothing to poll and no completion to await — so the honest wait is
/// on the CONDITION, never on a duration.
let private awaitFrames (frames: ResizeArray<QueryFrame>) (predicate: QueryFrame list -> bool) : Async<QueryFrame list> =
    let rec go attempts =
        async {
            let current = List.ofSeq frames
            if predicate current then return current
            elif attempts <= 0 then return failwithf "the stream never satisfied the condition; got %A" current
            else
                do! Async.Sleep 25
                return! go (attempts - 1)
        }
    go 200

let private routeTests =
    testList "the /queries stream" [

        // The door. Reading what this session is doing is for the people in the session:
        // an unauthenticated caller must not learn which repos are checked out here.
        testCaseAsync "no identity, no stream" <|
            async {
                let registry = Queries.create [ constant "repos" Value (ValueOf (CellText "x")) ] |> expect
                let! url = startQueryRoutes registry
                let! reply = getWithCookie (url + "/queries") "" |> Async.AwaitPromise
                Expect.equal reply.status 401 "the stream is gated"
                Expect.isFalse (reply.body.Contains "repos") "and it leaked nothing on the way out"
            }

        // End to end over real HTTP: connect, receive the burst, then watch a command's
        // invalidation arrive on the SAME connection. That last part is what "multiplexed"
        // buys — one connection, every query, including ones registered in later plans.
        testCaseAsync "one connection carries the declarations, every value, and later changes" <|
            async {
                let mutable branch = "main"
                let registry =
                    Queries.create
                        [ { Def = def "repos" rowsShape
                            Read =
                              fun () ->
                                  async { return Ok (RowsOf [ [ "repo", CellText "octo/hello"; "dirty", CellText branch ] ]) } }
                          constant "leases" Value (ValueOf (CellText "none")) ]
                    |> expect
                let! url = startQueryRoutes registry
                let frames = ResizeArray<QueryFrame> ()
                let subscription =
                    Sse.subscribe
                        (url + "/queries")
                        [ "cookie", "who=ada" ]
                        (fun data ->
                            match Codec.fromString Codec.queryFrame data with
                            | Ok frame -> frames.Add frame
                            | Error _ -> ())

                let! opening = awaitFrames frames (fun fs -> List.length fs >= 3)
                match opening with
                | QueriesDeclared defs :: _ ->
                    Expect.equal
                        (defs |> List.map (fun d -> QueryName.value d.Name))
                        [ "repos"; "leases" ]
                        "the client is told what exists before it is told what they say"
                | other -> failwithf "expected the declarations first, got %A" other
                Expect.isTrue
                    (opening |> List.contains (QueryValued (name "leases", ValueOf (CellText "none"))))
                    "every query's opening value arrives without being asked for"

                // What a command does: change the state, then say it changed. The value on
                // the wire is READ AFTER the invalidation, so it is the new one.
                branch <- "topic"
                registry.Invalidate (name "repos")
                let! _ =
                    awaitFrames frames (fun fs ->
                        fs
                        |> List.exists (function
                            | QueryValued (n, RowsOf [ row ]) ->
                                QueryName.value n = "repos" && row |> List.contains ("dirty", CellText "topic")
                            | _ -> false))
                subscription.Stop ()
            }

        // A stream the server does not OFFER (Plan 20, stage 5b). Every leg inside this
        // product retries a refusal for ever, because the peer is ours and its absence is
        // always temporary. That is exactly wrong against a server somebody else wrote: MCP
        // makes the GET stream optional, a conforming server answers 405, and retrying that
        // every second is a hot loop against a server behaving correctly.
        testCaseAsync "a refusal the caller calls permanent is asked ONCE, not in a loop" <|
            async {
                let attempts = ResizeArray<int> ()
                let handler (_req: Interop.IncomingMessage) (res: Interop.ServerResponse) =
                    attempts.Add 1
                    res.writeHead (405, Fable.Core.JsInterop.createObj [ "content-type", box "text/plain" ]) |> ignore
                    res.``end`` "no stream here"
                let server = Interop.createServer handler
                let! listening =
                    Async.FromContinuations (fun (cont, _, _) -> server.listen (0, "127.0.0.1", fun () -> cont server) |> ignore)
                let url = sprintf "http://127.0.0.1:%d/stream" (Interop.serverPort listening)
                let subscription = Sse.subscribeWhile url [] (fun status -> status <> 405) ignore
                // The retry delay is one second, so anything past it that still reads ONE is
                // a subscription that gave up rather than one that has not come round yet.
                let! _ = Async.Sleep 2500
                Expect.equal (Seq.length attempts) 1 "asked once, and believed the answer"
                subscription.Stop ()
            }

        testCaseAsync "a refusal the caller calls temporary is asked again" <|
            async {
                // The other half, and the reason it is a separate case: the verdict is the
                // CALLER's, so a change that made every refusal permanent would pass the
                // test above and silently stop the control legs from ever reconnecting.
                let attempts = ResizeArray<int> ()
                let handler (_req: Interop.IncomingMessage) (res: Interop.ServerResponse) =
                    attempts.Add 1
                    res.writeHead (503, Fable.Core.JsInterop.createObj [ "content-type", box "text/plain" ]) |> ignore
                    res.``end`` "not yet"
                let server = Interop.createServer handler
                let! listening =
                    Async.FromContinuations (fun (cont, _, _) -> server.listen (0, "127.0.0.1", fun () -> cont server) |> ignore)
                let url = sprintf "http://127.0.0.1:%d/stream" (Interop.serverPort listening)
                let subscription = Sse.subscribeWhile url [] (fun status -> status <> 405) ignore
                let! _ = Async.Sleep 2500
                Expect.isTrue (Seq.length attempts > 1) "a server that is merely down is still coming back"
                subscription.Stop ()
            }
    ]

let tests =
    testList "Queries" [
        shapeTests
        nameTests
        streamTests
        codecTests
    ]

let portsTests = testList "Queries over HTTP" [ routeTests ]
