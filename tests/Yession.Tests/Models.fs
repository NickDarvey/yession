module Yession.Tests.Models

// The model catalogue, end to end below the browser: the provider lookup that produces it,
// and the status reply that carries it.
//
// What is worth pinning here is not "a list comes back". It is the handful of properties
// the picker leans on:
//
//   * a paged catalogue arrives WHOLE, because a lookup that silently stopped at page one
//     would read as "this provider offers nothing else";
//   * the credential decides the dialect, so an OAuth grant and an API key each present
//     themselves the way their provider expects rather than one guessing for both;
//   * a failed lookup says why, and says it all the way to the browser — "nobody has
//     connected an account" and "this provider offers nothing" are different facts and a
//     picker that conflated them would show an empty menu with no way out;
//   * it rides the credential status it is a fact about, so the two cannot disagree;
//   * the door is shut: what this session can run on is for the people in the session.
//
// Ports, because the lookup IS an HTTP conversation. There is no in-memory stand-in for
// paging, headers and a status code, and those are exactly what the cases turn on.

open Fable.Core
open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Agent
open Yession.App
open Yession.Host
open Yession.SessionProcess

let private expect result =
    match result with
    | Ok v -> v
    | Error e -> failwithf "invariant: %A" e

type private HttpReply = { status: int; body: string }

[<Emit("""(function (url, cookie) { return (
fetch(url, { headers: cookie ? { cookie: cookie } : {}, cache: 'no-store' })
  .then(async r => ({ status: r.status, body: await r.text() }))
) })($0, $1)""")>]
let private get (url: string) (cookie: string) : JS.Promise<HttpReply> = Util.jsNative

/// Start a server on a free port and answer with `reply`, which sees the request.
let private serving (handler: Interop.IncomingMessage -> Interop.ServerResponse -> unit) =
    async {
        let server = Interop.createServer handler
        let! listening =
            Async.FromContinuations (fun (cont, _, _) -> server.listen (0, "127.0.0.1", fun () -> cont server) |> ignore)
        return sprintf "http://127.0.0.1:%d" (Interop.serverPort listening), server
    }

let private json (res: Interop.ServerResponse) (status: int) (body: string) =
    res.writeHead (status, JsInterop.createObj [ "content-type", box "application/json" ]) |> ignore
    res.``end`` body

// --- the provider lookup ------------------------------------------------------------------

/// A provider that answers the models endpoint in two pages, and records how each request
/// presented itself — which is what the dialect cases read back.
let private pagedProvider (seen: ResizeArray<string>) =
    fun (req: Interop.IncomingMessage) (res: Interop.ServerResponse) ->
        let auth =
            match Interop.headerOf req "x-api-key", Interop.headerOf req "authorization" with
            | Some key, _ -> "x-api-key:" + key
            | _, Some bearer -> "authorization:" + bearer
            | _ -> "none"
        seen.Add auth
        if (req.url : string).Contains "after_id=" then
            json res 200 """{"data":[{"id":"model-c","display_name":"Model C"}],"has_more":false,"last_id":"model-c"}"""
        else
            json
                res
                200
                """{"data":[{"id":"model-a","display_name":"Model A"},{"id":"model-b","display_name":"Model B"}],"has_more":true,"last_id":"model-b"}"""

let private lookupTests =
    testList "the provider lookup" [

        testCaseAsync "a paged catalogue arrives whole" <|
            async {
                // The failure this exists for is the quiet one: stop at page one and the
                // picker offers a subset nobody can tell is a subset.
                let seen = ResizeArray<string> ()
                let! url, server = serving (pagedProvider seen)
                let! models = ClaudeConnection.modelsAt url ("ANTHROPIC_API_KEY", "sk-ant-test")
                server.close ignore
                Expect.equal
                    (models |> expect |> List.map (fun m -> ModelId.value m.Id))
                    [ "model-a"; "model-b"; "model-c" ]
                    "every page's models, in the provider's order"
            }

        testCaseAsync "an api key presents itself as one, and an oauth grant as a bearer" <|
            async {
                // One credential PAIR decides this, so there is nothing to guess from the
                // shape of a secret — and nothing that can send a token the wrong way.
                let seen = ResizeArray<string> ()
                let! url, server = serving (pagedProvider seen)
                let! _ = ClaudeConnection.modelsAt url ("ANTHROPIC_API_KEY", "sk-ant-key")
                let! _ = ClaudeConnection.modelsAt url ("CLAUDE_CODE_OAUTH_TOKEN", "sk-ant-oat01-grant")
                server.close ignore
                Expect.isTrue (Seq.contains "x-api-key:sk-ant-key" seen) "the key goes in the key header"
                Expect.isTrue
                    (Seq.contains "authorization:Bearer sk-ant-oat01-grant" seen)
                    "and the grant goes in the bearer header"
            }

        testCaseAsync "a refused lookup answers with what the provider said" <|
            async {
                // The detail is the whole value: "401 invalid x-api-key" is actionable and
                // "could not list models" is not.
                let! url, server = serving (fun _ res -> json res 401 """{"error":{"message":"invalid x-api-key"}}""")
                let! models = ClaudeConnection.modelsAt url ("ANTHROPIC_API_KEY", "sk-ant-wrong")
                server.close ignore
                match models with
                | Ok _ -> failwith "a refusal must not read as a catalogue"
                | Error failure ->
                    Expect.isTrue (failure.Message.Contains "401") "the status is in the reason"
                    Expect.isTrue (failure.Message.Contains "invalid x-api-key") "and so is what the provider said"
                    // And it is marked as a fact about the CREDENTIAL, which is what lets it
                    // travel back to the Manager rather than stopping at the picker's note.
                    Expect.isTrue failure.Refused "a 401 is the provider refusing this credential"
            }

        // The other side of that line. Both of these happen to a credential that is working
        // perfectly, so both must leave it alone: telling somebody to sign in again because
        // an org policy blocked one endpoint, or because the provider had a bad minute,
        // spends their time on the wrong problem.
        testCaseAsync "a lookup that fails for reasons other than the credential leaves it alone" <|
            async {
                let! forbiddenUrl, forbiddenServer = serving (fun _ res -> json res 403 """{"error":{"message":"not permitted"}}""")
                let! forbidden = ClaudeConnection.modelsAt forbiddenUrl ("ANTHROPIC_API_KEY", "sk-ant-fine")
                forbiddenServer.close ignore
                match forbidden with
                | Ok _ -> failwith "a refusal must not read as a catalogue"
                | Error failure -> Expect.isFalse failure.Refused "403 is a policy or a scope, not a dead key"

                let! brokenUrl, brokenServer = serving (fun _ res -> json res 500 """{"error":{"message":"oops"}}""")
                let! broken = ClaudeConnection.modelsAt brokenUrl ("ANTHROPIC_API_KEY", "sk-ant-fine")
                brokenServer.close ignore
                match broken with
                | Ok _ -> failwith "a refusal must not read as a catalogue"
                | Error failure -> Expect.isFalse failure.Refused "a provider having a bad minute is not a verdict"
            }

        testCaseAsync "a row the id invariant refuses costs that row, never the catalogue" <|
            async {
                let! url, server =
                    serving (fun _ res ->
                        json res 200 """{"data":[{"id":"","display_name":"Nameless"},{"id":"model-a"}],"has_more":false}""")
                let! models = ClaudeConnection.modelsAt url ("ANTHROPIC_API_KEY", "sk-ant-test")
                server.close ignore
                match models |> expect with
                | [ only ] ->
                    Expect.equal (ModelId.value only.Id) "model-a" "the usable row survives"
                    Expect.equal only.Name "model-a" "and stands in for its own missing label"
                | other -> failwithf "expected one usable model, got %A" other
            }
    ]

// --- the route --------------------------------------------------------------------------

/// `who=<name>` is an identity; anything else is nobody — the same stub the query route's
/// suite uses, and for the same reason: what is under test is what the ROUTE does with one.
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

/// A broker that refuses everything: what is under test here is the STATUS reply, and no
/// case in this suite drives a write action.
let private stubConnections : ControlClient.SessionConnections =
    let refuse _ = async { return Error "not under test" }
    { Begin = refuse
      Complete = fun _ _ -> refuse ()
      Put = fun _ _ -> refuse ()
      PutGrant = refuse
      Disconnect = refuse
      Reject = fun _ _ -> refuse ()
      Resolve = refuse }

/// The Claude panel's status route over a stub catalogue — the one route that answers what
/// this session can run a turn on.
let private startClaudeRoutes (list: ListModels) =
    async {
        let sessionId = SessionId.create "sess-models" |> expect
        let route =
            ClaudeConnection.routes
                sessionId
                (stubAuth ())
                stubConnections
                (fun _ -> None)
                (fun () -> false)
                list
                ""
        let! url, server =
            serving (fun req res ->
                if not (route req res) then
                    res.writeHead (404, JsInterop.createObj [ "content-type", box "text/plain" ]) |> ignore
                    res.``end`` "not found")
        return url + "/" + SessionRoute.relative SessionRoute.ClaudeStatus, server
    }

/// The models off a status reply, as the browser reads them: the list, or the reason there
/// is none.
[<Emit("""(function (body) { const s = JSON.parse(body); return {
  models: s.models ? JSON.stringify(s.models) : null,
  unavailable: s.modelsUnavailable || null } })($0)""")>]
let private catalogueOf (body: string) : {| models: string option; unavailable: string option |} = Util.jsNative

let private routeTests =
    testList "the catalogue on the status reply" [

        testCaseAsync "no identity, no catalogue" <|
            async {
                // Which models this session can run on is a fact about the session, so it
                // goes to the people in it and to nobody else.
                let! url, server = startClaudeRoutes (fun _ -> async { return Ok [] })
                let! reply = get url "" |> Async.AwaitPromise
                server.close ignore
                Expect.equal reply.status 401 "unauthenticated is refused"
            }

        testCaseAsync "the catalogue crosses as the shared codec, on the asking party's authority" <|
            async {
                let mutable askedFor : ActorRef option = None
                let! url, server =
                    startClaudeRoutes (fun actor ->
                        async {
                            askedFor <- Some actor
                            return Ok [ AgentModel.create (ModelId.create "model-a" |> expect) "Model A" ]
                        })
                let! reply = get url "who=ada" |> Async.AwaitPromise
                server.close ignore
                Expect.equal reply.status 200 "an identity gets an answer"
                Expect.equal
                    (Codec.fromString Codec.modelCatalogue (catalogueOf reply.body).models.Value |> expect)
                    [ AgentModel.create (ModelId.create "model-a" |> expect) "Model A" ]
                    "and it is the catalogue, decoded by the codec the browser uses"
                Expect.equal
                    askedFor
                    (Some (UserRef (UserId.create "ada" |> expect)))
                    "asked on the credential of whoever is looking"
            }

        testCaseAsync "a lookup that failed says why, rather than answering an empty list" <|
            async {
                // An empty menu with no explanation is the state this whole shape exists to
                // avoid: the remedy is one panel up, and nothing would have pointed at it.
                let! url, server = startClaudeRoutes (fun _ -> async { return Error "no Claude account connected" })
                let! reply = get url "who=ada" |> Async.AwaitPromise
                server.close ignore
                let catalogue = catalogueOf reply.body
                Expect.isNone catalogue.models "a failed lookup is not a catalogue"
                Expect.equal
                    catalogue.unavailable
                    (Some "no Claude account connected")
                    "and the reason rides the same reply"
            }

        testCaseAsync "the credential status and the catalogue arrive together, or not at all" <|
            async {
                // The invariant the fold exists for. Two routes with two refresh triggers
                // let the picker keep a refusal naming an account the panel beside it had
                // already shown as connected; one reply cannot disagree with itself.
                let! url, server = startClaudeRoutes (fun _ -> async { return Error "no Claude account connected" })
                let! reply = get url "who=ada" |> Async.AwaitPromise
                server.close ignore
                Expect.isTrue (reply.body.Contains "\"owner\"") "the status is on the reply"
                Expect.isTrue (reply.body.Contains "\"modelsUnavailable\"") "and so is what the picker can offer"
            }
    ]

let portsTests =
    testList "Model catalogue" [
        lookupTests
        routeTests
    ]
