module Yession.Tests.Connections

// Plan 08: the Manager's standards-only connection broker and the session-side Claude
// module over it. Pure flow logic, envelope + wire codecs, and Claude classification run
// in the cheap tier; the broker service and control routes run under [Ports] against a
// fake token endpoint (the broker must never need the real claude.ai to be provable).

open System
open Fable.Core
open Fable.Pyxpecto
open Yession.Domain
open Yession.Manager

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

let private sessionA = SessionId.create "conn-session-a" |> expect
let private sessionB = SessionId.create "conn-session-b" |> expect
let private alice = UserId.create "alice" |> expect
let private peer1 = PeerId.create "browser-1" |> expect
let private claudeName = SecretName.create "claude-code" |> expect

let private target scope : SecretId = { Scope = scope; Name = claudeName }

let private grant : OAuthGrant =
    { AccessToken = "at-1"
      RefreshToken = Some "rt-1"
      ExpiresAt = Some (DateTimeOffset.Parse "2026-07-28T12:00:00Z")
      TokenUrl = "http://token.example/oauth"
      ClientId = "client-1"
      Dialect = FormEncoded }

// --- Pure: envelope codec ----------------------------------------------------------------

let private codecTests =
    testList "credential envelope codec" [
        testCase "oauth round-trips (with refresh + expiry)" <| fun () ->
            let credential = BrokeredOAuth grant
            Expect.equal (BrokeredCredentialCodec.toString credential |> BrokeredCredentialCodec.fromString |> expect) credential "identical"

        testCase "oauth round-trips without refresh or expiry" <| fun () ->
            let credential = BrokeredOAuth { grant with RefreshToken = None; ExpiresAt = None }
            Expect.equal (BrokeredCredentialCodec.toString credential |> BrokeredCredentialCodec.fromString |> expect) credential "identical"

        testCase "static round-trips" <| fun () ->
            let credential = BrokeredStatic "sk-ant-oat01-abc"
            Expect.equal (BrokeredCredentialCodec.toString credential |> BrokeredCredentialCodec.fromString |> expect) credential "identical"

        testCase "unknown kind fails the decode" <| fun () ->
            Expect.isError (BrokeredCredentialCodec.fromString """{"kind":"planet"}""") "unknown kind rejected"

        testCase "the dialect round-trips, and an envelope stored before dialects existed is form-encoded" <| fun () ->
            let json = BrokeredOAuth { grant with Dialect = JsonEncoded }
            Expect.equal (BrokeredCredentialCodec.toString json |> BrokeredCredentialCodec.fromString |> expect) json "json dialect survives"
            // A grant written by an older Manager has no `dialect` field: it must keep
            // decoding (and refreshing) as the standard rather than fail the whole store.
            let legacy =
                """{"kind":"oauth","grant":{"accessToken":"at","refreshToken":null,"expiresAt":null,"tokenUrl":"http://t","clientId":"c"}}"""
            match BrokeredCredentialCodec.fromString legacy |> expect with
            | BrokeredOAuth g -> Expect.equal g.Dialect FormEncoded "defaults to the standard"
            | BrokeredStatic _ -> failwith "expected an oauth envelope"
    ]

// --- Pure: flow logic ----------------------------------------------------------------------

let private now = DateTimeOffset.Parse "2026-07-28T10:00:00Z"

let private flowTests =
    testList "broker flow" [
        testCase "authorizeUrl carries the standard params and appends to an existing query" <| fun () ->
            let url = BrokerFlow.authorizeUrl "https://p.example/authorize?code=true" "cid" "http://m/cb" "a b" "st" "ch"
            Expect.isTrue (url.StartsWith "https://p.example/authorize?code=true&") "appends with &"
            Expect.isTrue (url.Contains "response_type=code") "code flow"
            Expect.isTrue (url.Contains "client_id=cid") "client id"
            Expect.isTrue (url.Contains "redirect_uri=http%3A%2F%2Fm%2Fcb") "redirect encoded"
            Expect.isTrue (url.Contains "scope=a%20b") "scopes encoded"
            Expect.isTrue (url.Contains "state=st") "state"
            Expect.isTrue (url.Contains "code_challenge=ch") "challenge"
            Expect.isTrue (url.Contains "code_challenge_method=S256") "S256"
            let bare = BrokerFlow.authorizeUrl "https://p.example/authorize" "cid" "r" "s" "st" "ch"
            Expect.isTrue (bare.StartsWith "https://p.example/authorize?") "starts a query when none exists"

        testCase "grant bodies are standard form-encoded shapes" <| fun () ->
            let exchange = BrokerFlow.exchangeRequest FormEncoded "cid" "http://m/cb" "ver" "st" "the-code"
            Expect.equal exchange.ContentType "application/x-www-form-urlencoded" "RFC 6749 §4.1.3"
            Expect.isTrue (exchange.Body.Contains "grant_type=authorization_code") "exchange grant"
            Expect.isTrue (exchange.Body.Contains "code_verifier=ver") "verifier"
            Expect.isFalse (exchange.Body.Contains "state=") "state is not a standard token-request parameter"
            let refresh = BrokerFlow.refreshRequest grant "rt-1"
            Expect.equal refresh.ContentType "application/x-www-form-urlencoded" "RFC 6749 §6"
            Expect.isTrue (refresh.Body.Contains "grant_type=refresh_token") "refresh grant"
            Expect.isTrue (refresh.Body.Contains "refresh_token=rt-1") "token"
            Expect.isTrue (refresh.Body.Contains "client_id=client-1") "client id"

        testCase "the json dialect posts a JSON body, with state replayed in it" <| fun () ->
            // The shape Anthropic's `/v1/oauth/token` requires — a form body there comes
            // back `invalid_request_error: "Invalid request format"`, which is what broke
            // every Claude sign-in at the paste step.
            let exchange = BrokerFlow.exchangeRequest JsonEncoded "cid" "http://m/cb" "ver" "st" "the-code"
            Expect.equal exchange.ContentType "application/json" "JSON content type"
            let decoded =
                Decode.fromString
                    (Decode.dict Decode.string)
                    exchange.Body
                |> expect
            Expect.equal (Map.tryFind "grant_type" decoded) (Some "authorization_code") "exchange grant"
            Expect.equal (Map.tryFind "code" decoded) (Some "the-code") "the code"
            Expect.equal (Map.tryFind "redirect_uri" decoded) (Some "http://m/cb") "redirect repeated"
            Expect.equal (Map.tryFind "client_id" decoded) (Some "cid") "client id"
            Expect.equal (Map.tryFind "code_verifier" decoded) (Some "ver") "verifier"
            Expect.equal (Map.tryFind "state" decoded) (Some "st") "state replayed in the body"
            // Refresh follows the grant's recorded dialect: a provider that cannot parse
            // the exchange cannot parse the renewal either.
            let refresh = BrokerFlow.refreshRequest { grant with Dialect = JsonEncoded } "rt-1"
            Expect.equal refresh.ContentType "application/json" "refresh matches the exchange"
            Expect.isTrue (refresh.Body.StartsWith "{") "a JSON object, not a query string"

        testCase "token responses decode: full, minimal, and malformed" <| fun () ->
            let full = BrokerFlow.decodeTokenResponse now "http://t" "cid" FormEncoded """{"access_token":"at","refresh_token":"rt","expires_in":3600}""" |> expect
            Expect.equal full.AccessToken "at" "access"
            Expect.equal full.RefreshToken (Some "rt") "refresh"
            Expect.equal full.ExpiresAt (Some (now.AddSeconds 3600.0)) "expiry from expires_in"
            Expect.equal full.TokenUrl "http://t" "token url captured"
            let minimal = BrokerFlow.decodeTokenResponse now "http://t" "cid" JsonEncoded """{"access_token":"at"}""" |> expect
            Expect.equal minimal.RefreshToken None "no refresh"
            Expect.equal minimal.ExpiresAt None "no expiry"
            Expect.equal minimal.Dialect JsonEncoded "the dialect is captured for later refreshes"
            Expect.isError (BrokerFlow.decodeTokenResponse now "http://t" "cid" FormEncoded """{"token":"x"}""") "missing access_token"

        testCase "needsRefresh: due inside the 5-minute margin, never for static or refreshless" <| fun () ->
            let expiring margin = BrokeredOAuth { grant with ExpiresAt = Some (now.AddSeconds margin) }
            Expect.isFalse (BrokerFlow.needsRefresh now (expiring 600.0)) "fresh"
            Expect.isTrue (BrokerFlow.needsRefresh now (expiring 200.0)) "inside the margin"
            Expect.isTrue (BrokerFlow.needsRefresh now (expiring -10.0)) "expired"
            Expect.isFalse (BrokerFlow.needsRefresh now (BrokeredOAuth { grant with RefreshToken = None; ExpiresAt = Some now })) "no refresh token"
            Expect.isFalse (BrokerFlow.needsRefresh now (BrokeredOAuth { grant with ExpiresAt = None })) "no known expiry"
            Expect.isFalse (BrokerFlow.needsRefresh now (BrokeredStatic "sk")) "static never refreshes"

        testCase "merged keeps the previous refresh token when the response omits one" <| fun () ->
            let fresh = { grant with AccessToken = "at-2"; RefreshToken = None }
            Expect.equal (BrokerFlow.merged grant fresh).RefreshToken (Some "rt-1") "old refresh survives"
            let rotated = { grant with AccessToken = "at-2"; RefreshToken = Some "rt-2" }
            Expect.equal (BrokerFlow.merged grant rotated).RefreshToken (Some "rt-2") "rotation wins"

        testCase "pending flows are single-use and expire" <| fun () ->
            let mutable clock = 0L
            let pending = PendingFlows (fun () -> clock)
            let flow = { Verifier = "v"; Target = target (UserScope alice); TokenUrl = "t"; ClientId = "c"; Scopes = "s"; RedirectUri = "r"; Dialect = FormEncoded }
            pending.Add "st" flow
            Expect.equal (pending.Take "st") (Some flow) "first take"
            Expect.equal (pending.Take "st") None "single-use"
            pending.Add "st2" flow
            clock <- 601L
            Expect.equal (pending.Take "st2") None "expired after the TTL"
            Expect.equal (pending.Take "unknown") None "unknown state"
    ]

// --- Pure: wire codecs ---------------------------------------------------------------------

let private beginRequest : ControlWire.ConnectionBeginRequest =
    { Target = target (UserScope alice)
      AuthorizeUrl = "https://p.example/authorize?code=true"
      TokenUrl = "https://p.example/token"
      ClientId = "cid"
      Scopes = "a b"
      RedirectUri = None
      TokenDialect = FormEncoded }

let private wireTests =
    testList "connection wire codecs" [
        testCase "begin request/response round-trip" <| fun () ->
            Expect.equal (ControlWire.toString ControlWire.connectionBeginRequest beginRequest |> ControlWire.fromString ControlWire.connectionBeginRequest |> expect) beginRequest "request"
            let withRedirect = { beginRequest with RedirectUri = Some "https://provider.example/code" }
            Expect.equal (ControlWire.toString ControlWire.connectionBeginRequest withRedirect |> ControlWire.fromString ControlWire.connectionBeginRequest |> expect) withRedirect "request with a provider redirect"
            let resp : ControlWire.ConnectionBeginResponse = { AuthorizeUrl = "https://u"; State = "st" }
            Expect.equal (ControlWire.toString ControlWire.connectionBeginResponse resp |> ControlWire.fromString ControlWire.connectionBeginResponse |> expect) resp "response"

        testCase "the token dialect crosses the wire, and an older session still means the standard" <| fun () ->
            let json = { beginRequest with TokenDialect = JsonEncoded }
            Expect.equal (ControlWire.toString ControlWire.connectionBeginRequest json |> ControlWire.fromString ControlWire.connectionBeginRequest |> expect) json "json dialect survives"
            // A session built before dialects existed sends no such field; it speaks the
            // standard, so a newer Manager must read it that way rather than reject it.
            let older =
                """{"target":{"scope":{"kind":"user","sub":"alice"},"name":"claude-code"},"authorizeUrl":"https://p.example/authorize?code=true","tokenUrl":"https://p.example/token","clientId":"cid","scopes":"a b"}"""
            let decoded = ControlWire.fromString ControlWire.connectionBeginRequest older |> expect
            Expect.equal decoded.TokenDialect FormEncoded "defaults to the standard"

        testCase "complete/put/disconnect/resolve round-trip" <| fun () ->
            let complete : ControlWire.ConnectionCompleteRequest = { Target = target (SessionScope sessionA); Code = "c#st" }
            Expect.equal (ControlWire.toString ControlWire.connectionCompleteRequest complete |> ControlWire.fromString ControlWire.connectionCompleteRequest |> expect) complete "complete"
            let put : ControlWire.ConnectionPutRequest = { Target = target (PeerScope peer1); Value = "sk-ant-x" }
            Expect.equal (ControlWire.toString ControlWire.connectionPutRequest put |> ControlWire.fromString ControlWire.connectionPutRequest |> expect) put "put"
            let disc : ControlWire.ConnectionDisconnectRequest = { Target = target (UserScope alice) }
            Expect.equal (ControlWire.toString ControlWire.connectionDisconnectRequest disc |> ControlWire.fromString ControlWire.connectionDisconnectRequest |> expect) disc "disconnect"
            let resolve : ControlWire.ConnectionResolveResponse = { Kind = OAuthConnection; Value = "at" }
            Expect.equal (ControlWire.toString ControlWire.connectionResolveResponse resolve |> ControlWire.fromString ControlWire.connectionResolveResponse |> expect) resolve "resolve response"

        testCase "the status list frame is value-free by type and round-trips" <| fun () ->
            let list : ConnectionStatusList =
                { Connections =
                    [ { Id = target (UserScope alice); Kind = OAuthConnection; UpdatedAt = DateTimeOffset.Parse "2026-07-28T10:00:00Z" }
                      { Id = target (SessionScope sessionA); Kind = StaticConnection; UpdatedAt = DateTimeOffset.Parse "2026-07-28T11:00:00Z" } ] }
            Expect.equal (ControlWire.toString ControlWire.connectionStatusList list |> ControlWire.fromString ControlWire.connectionStatusList |> expect) list "identical"
    ]

// --- Pure: the session-side Claude module ---------------------------------------------------

open Yession.Host

let private claudeTests =
    testList "claude connection module" [
        testCase "pasted credentials classify by prefix" <| fun () ->
            Expect.equal (ClaudeConnection.classifyPasted "  sk-ant-oat01-tok  ") (Ok "sk-ant-oat01-tok") "setup token, trimmed"
            Expect.equal (ClaudeConnection.classifyPasted "sk-ant-api03-key") (Ok "sk-ant-api03-key") "api key"
            Expect.isError (ClaudeConnection.classifyPasted "hunter2") "not a claude credential"
            Expect.isError (ClaudeConnection.classifyPasted "   ") "blank"

        testCase "resolved credentials map to the right env var" <| fun () ->
            Expect.equal (ClaudeConnection.envVarFor OAuthConnection "at") ("CLAUDE_CODE_OAUTH_TOKEN", "at") "brokered oauth"
            Expect.equal (ClaudeConnection.envVarFor StaticConnection "sk-ant-oat01-x") ("CLAUDE_CODE_OAUTH_TOKEN", "sk-ant-oat01-x") "setup token"
            Expect.equal (ClaudeConnection.envVarFor StaticConnection "sk-ant-api03-x") ("ANTHROPIC_API_KEY", "sk-ant-api03-x") "api key"

        testCase "scope choices map to targets" <| fun () ->
            Expect.equal (ClaudeConnection.targetFor sessionA (UserOwner alice) "session") (Ok (target (SessionScope sessionA))) "this session"
            Expect.equal (ClaudeConnection.targetFor sessionA (UserOwner alice) "mine") (Ok (target (UserScope alice))) "all my sessions (user)"
            Expect.equal (ClaudeConnection.targetFor sessionA LocalOwner "mine") (Ok (target LocalScope)) "all my sessions (unattributed deployment)"
            Expect.isError (ClaudeConnection.targetFor sessionA (UserOwner alice) "everyone") "unknown choice"

        testCase "a connection is owned by the cookie's attribution, never by the browser" <| fun () ->
            // The whole reason `ownerOf` stopped taking a peer id: a browser's self-asserted
            // identity churns (origin-partitioned localStorage) and used to strand the
            // credential behind every new value it took.
            let identity attribution : Yession.SessionProcess.CookieIdentity =
                { Subject = "local"; DisplayName = None; Attribution = attribution }
            Expect.equal
                (ClaudeConnection.ownerOf (identity (Yession.SessionProcess.AttributedUser alice)))
                (UserOwner alice)
                "an attributed user owns their own"
            Expect.equal
                (ClaudeConnection.ownerOf (identity Yession.SessionProcess.UnattributedAccess))
                LocalOwner
                "unattributed access owns the deployment's"
            Expect.equal
                (GitHubConnection.ownerOf (identity Yession.SessionProcess.UnattributedAccess))
                LocalOwner
                "and GitHub reads the same rule"

        testCase "turn targets: session first, then the actor's own scope, then the deployment's" <| fun () ->
            Expect.equal
                (ClaudeConnection.turnTargets sessionA (UserRef alice))
                [ target (SessionScope sessionA); target (UserScope alice); target LocalScope ]
                "session shadows the actor, who shadows the deployment"
            // A peer owns nothing of their own. Naming LocalScope here is unconditional and
            // safe: an attributed deployment never reports it readable, so this candidate is
            // filtered out before anything is resolved.
            Expect.equal
                (ClaudeConnection.turnTargets sessionA (PeerRef peer1))
                [ target (SessionScope sessionA); target LocalScope ]
                "an unverified peer falls straight through to the deployment"
            Expect.equal
                (ClaudeConnection.turnTargets sessionA ActorRef.Agent)
                [ target (SessionScope sessionA); target LocalScope ]
                "agent has no own scope"

        testCase "the begin request declares Anthropic's JSON token dialect" <| fun () ->
            // Anthropic's token endpoint rejects a standards-correct form body
            // (`invalid_request_error: "Invalid request format"`), so the session must say
            // so — the broker has no provider knowledge to fall back on.
            Expect.equal (ClaudeConnection.beginRequest (target (UserScope alice))).TokenDialect JsonEncoded "json, declared session-side"
    ]

// --- Pure: the session-side GitHub module (Plan 14) ------------------------------------------

let private githubName = SecretName.create "github" |> expect
let private githubTarget scope : SecretId = { Scope = scope; Name = githubName }

let private githubTests =
    testList "github connection module" [
        testCase "pasted credentials classify by prefix" <| fun () ->
            Expect.equal (GitHubConnection.classifyPasted "  github_pat_11ABC  ") (Ok "github_pat_11ABC") "fine-grained PAT, trimmed"
            Expect.equal (GitHubConnection.classifyPasted "ghp_classic") (Ok "ghp_classic") "classic PAT"
            Expect.equal (GitHubConnection.classifyPasted "ghu_apptoken") (Ok "ghu_apptoken") "app user token"
            Expect.equal (GitHubConnection.classifyPasted "gho_devicetoken") (Ok "gho_devicetoken") "oauth device token"
            Expect.isError (GitHubConnection.classifyPasted "hunter2") "not a github credential"
            Expect.isError (GitHubConnection.classifyPasted "sk-ant-api03-x") "a claude credential is a paste mistake"
            Expect.isError (GitHubConnection.classifyPasted "   ") "blank"

        testCase "every kind rides GITHUB_TOKEN" <| fun () ->
            Expect.equal (GitHubConnection.envVarFor OAuthConnection "t") ("GITHUB_TOKEN", "t") "oauth"
            Expect.equal (GitHubConnection.envVarFor StaticConnection "t") ("GITHUB_TOKEN", "t") "static"

        testCase "scope choices map to targets" <| fun () ->
            Expect.equal (GitHubConnection.targetFor sessionA (UserOwner alice) "session") (Ok (githubTarget (SessionScope sessionA))) "this session"
            Expect.equal (GitHubConnection.targetFor sessionA (UserOwner alice) "mine") (Ok (githubTarget (UserScope alice))) "all my sessions (user)"
            Expect.isError (GitHubConnection.targetFor sessionA (UserOwner alice) "everyone") "unknown choice"

        testCase "operation targets: session first, then the actor's own scope, then the deployment's" <| fun () ->
            Expect.equal
                (GitHubConnection.turnTargets sessionA (UserRef alice))
                [ githubTarget (SessionScope sessionA); githubTarget (UserScope alice); githubTarget LocalScope ]
                "session shadows the actor, who shadows the deployment"
            Expect.equal
                (GitHubConnection.turnTargets sessionA ActorRef.Agent)
                [ githubTarget (SessionScope sessionA); githubTarget LocalScope ]
                "agent has no own scope"

        testCase "a device-code grant decodes, defaulting the interval" <| fun () ->
            let full = """{"device_code":"dc-1","user_code":"ABCD-1234","verification_uri":"https://github.com/login/device","expires_in":900,"interval":7}"""
            let decoded = Decode.fromString GitHubConnection.deviceCodeDecoder full |> expect
            Expect.equal decoded.DeviceCode "dc-1" "device code"
            Expect.equal decoded.UserCode "ABCD-1234" "user code"
            Expect.equal decoded.Interval 7 "explicit interval"
            let bare = """{"device_code":"dc-2","user_code":"EFGH-5678","verification_uri":"https://github.com/login/device"}"""
            Expect.equal (Decode.fromString GitHubConnection.deviceCodeDecoder bare |> expect).Interval 5 "default interval"
            Expect.isError (Decode.fromString GitHubConnection.deviceCodeDecoder """{"user_code":"X"}""") "missing device code refused"

        testCase "poll outcomes fold from the body, never the status code" <| fun () ->
            Expect.equal (GitHubConnection.pollOutcome 5 """{"access_token":"gho_t","token_type":"bearer"}""") (GitHubConnection.PollGranted "gho_t") "granted"
            Expect.equal (GitHubConnection.pollOutcome 5 """{"error":"authorization_pending"}""") (GitHubConnection.PollPending 5) "pending keeps the pace"
            Expect.equal (GitHubConnection.pollOutcome 5 """{"error":"slow_down"}""") (GitHubConnection.PollPending 10) "slow_down widens by the spec's 5s"
            Expect.equal
                (GitHubConnection.pollOutcome 5 """{"error":"expired_token"}""")
                (GitHubConnection.PollFailed "the device code expired — start the sign-in again")
                "expiry is terminal"
            Expect.equal
                (GitHubConnection.pollOutcome 5 """{"error":"access_denied"}""")
                (GitHubConnection.PollFailed "the sign-in was denied on github.com")
                "denial is terminal"
            Expect.equal
                (GitHubConnection.pollOutcome 5 """{"error":"incorrect_device_code","error_description":"The device code is wrong"}""")
                (GitHubConnection.PollFailed "The device code is wrong")
                "unknown errors surface their description"
            match GitHubConnection.pollOutcome 5 "not json" with
            | GitHubConnection.PollFailed _ -> ()
            | other -> failwithf "garbage should fail the poll, got %A" other
    ]

// --- [Ports]: the broker service against a fake token endpoint ------------------------------

type private HttpReply =
    abstract status : int
    abstract body : string

[<Emit("fetch($0, { method: 'POST', headers: { 'x-yession-control': $1, 'content-type': 'application/json' }, body: $2 }).then(async r => ({ status: r.status, body: await r.text() }))")>]
let private postControl (url: string) (secret: string) (body: string) : JS.Promise<HttpReply> = Util.jsNative

/// A scripted token endpoint: answers every POST with the current `response` (400 when
/// it is not JSON-shaped) and records the raw bodies it saw, each with the content type
/// it arrived under — a real provider accepts only one, so the pairing is the contract.
type private TokenEndpoint =
    { Url : string
      SetResponse : string -> unit
      Requests : ResizeArray<string>
      ContentTypes : ResizeArray<string> }

let private startTokenEndpoint () : Async<TokenEndpoint> =
    async {
        let mutable response = """{"access_token":"at-1","refresh_token":"rt-1","expires_in":3600}"""
        let requests = ResizeArray<string> ()
        let contentTypes = ResizeArray<string> ()
        let handler (req: Interop.IncomingMessage) (res: Interop.ServerResponse) =
            let mutable acc = ""
            req.on ("data", fun chunk -> acc <- acc + Interop.bufferToString chunk) |> ignore
            req.on ("end", fun _ ->
                requests.Add acc
                contentTypes.Add (Interop.headerOf req "content-type" |> Option.defaultValue "")
                let status = if response.StartsWith "{" then 200 else 400
                res.writeHead (status, Fable.Core.JsInterop.createObj [ "content-type", box "application/json" ]) |> ignore
                res.``end`` response) |> ignore
        let server = Interop.createServer handler
        let! listening =
            Async.FromContinuations (fun (cont, _, _) -> server.listen (0, "127.0.0.1", fun () -> cont server) |> ignore)
        return
            { Url = sprintf "http://127.0.0.1:%d/token" (Interop.serverPort listening)
              SetResponse = (fun r -> response <- r)
              Requests = requests
              ContentTypes = contentTypes }
    }

let private openEphemeral () =
    async {
        let! opened = SecretStore.openStore None (KeyStore.random ())
        return expect (opened |> Result.mapError SecretStore.OpenError.describe)
    }

let private brokerTests =
    testList "broker service" [
        testCaseAsync "begin → callback exchanges the code, stores the grant, and resolve returns it" <|
            async {
                let! endpoint = startTokenEndpoint ()
                let! store = openEphemeral ()
                let broker = Broker.create (fun () -> "http://manager.local/connections/callback") store ignore
                let request = { beginRequest with TokenUrl = endpoint.Url }
                let! began = broker.Begin request
                let began = expect began
                Expect.isTrue (began.AuthorizeUrl.Contains "code_challenge=") "PKCE rode the authorize URL"
                Expect.isTrue (began.AuthorizeUrl.Contains began.State) "state rode the authorize URL"
                let! completed = broker.CompleteCallback began.State "the-code"
                Expect.equal (expect completed) request.Target "completion names the pended target"
                Expect.isTrue ((endpoint.Requests.[0]).Contains "grant_type=authorization_code") "standard grant"
                Expect.isTrue ((endpoint.Requests.[0]).Contains "code=the-code") "the code"
                Expect.equal (store.List (UserScope alice) |> List.length) 1 "one stored entry under the target"
                let! resolved = broker.Resolve request.Target
                Expect.equal (expect resolved) (OAuthConnection, "at-1") "resolves the access token"
                let! replayed = broker.CompleteCallback began.State "another-code"
                Expect.isError replayed "single-use state"
            }

        testCaseAsync "a json-dialect provider gets JSON on both the exchange and the refresh" <|
            async {
                // End to end through the broker: the dialect the session declared at begin
                // decides the content type on the wire, survives into the stored grant, and
                // still decides it when that grant renews.
                let! endpoint = startTokenEndpoint ()
                let! store = openEphemeral ()
                let broker = Broker.create (fun () -> "http://m/cb") store ignore
                let request = { beginRequest with TokenUrl = endpoint.Url; TokenDialect = JsonEncoded }
                let! began = broker.Begin request
                let began = expect began
                let! completed = broker.Complete request.Target (sprintf "the-code#%s" began.State)
                expect completed
                Expect.equal endpoint.ContentTypes.[0] "application/json" "the exchange went out as JSON"
                Expect.isTrue ((endpoint.Requests.[0]).Contains "\"grant_type\":\"authorization_code\"") "a JSON grant"
                Expect.isTrue ((endpoint.Requests.[0]).Contains (sprintf "\"state\":\"%s\"" began.State)) "state replayed in the body"

                // Age the stored grant into the refresh margin and resolve: the renewal must
                // speak the same dialect, or a working sign-in dies at its first expiry.
                let stored =
                    BrokeredOAuth
                        { AccessToken = "at-stale"
                          RefreshToken = Some "rt-stale"
                          ExpiresAt = Some (DateTimeOffset.UtcNow.AddSeconds 60.0)
                          TokenUrl = endpoint.Url
                          ClientId = "cid"
                          Dialect = JsonEncoded }
                let! seeded = store.Set request.Target (BrokeredCredentialCodec.toString stored)
                expect (seeded |> Result.map ignore)
                let! resolved = broker.Resolve request.Target
                Expect.equal (expect resolved) (OAuthConnection, "at-1") "refreshed"
                Expect.equal endpoint.ContentTypes.[1] "application/json" "the refresh went out as JSON too"
            }

        testCaseAsync "a session-supplied redirect URI rides the authorize URL and the exchange (the provider-hosted code page path)" <|
            async {
                let! endpoint = startTokenEndpoint ()
                let! store = openEphemeral ()
                let broker = Broker.create (fun () -> "http://manager.local/connections/callback") store ignore
                let request =
                    { beginRequest with
                        TokenUrl = endpoint.Url
                        RedirectUri = Some "https://provider.example/oauth/code/callback" }
                let! began = broker.Begin request
                let began = expect began
                Expect.isTrue
                    (began.AuthorizeUrl.Contains "redirect_uri=https%3A%2F%2Fprovider.example%2Foauth%2Fcode%2Fcallback")
                    "the provider's code page, not the Manager callback"
                let! completed = broker.Complete request.Target (sprintf "the-code#%s" began.State)
                expect completed
                Expect.isTrue
                    ((endpoint.Requests.[0]).Contains "redirect_uri=https%3A%2F%2Fprovider.example%2Foauth%2Fcode%2Fcallback")
                    "the exchange repeats the flow's redirect_uri exactly"
            }

        testCaseAsync "unknown state, provider refusal, and malformed responses are legible errors" <|
            async {
                let! endpoint = startTokenEndpoint ()
                let! store = openEphemeral ()
                let broker = Broker.create (fun () -> "http://m/cb") store ignore
                let! unknown = broker.CompleteCallback "never-began" "c"
                Expect.isError unknown "unknown state"
                let request = { beginRequest with TokenUrl = endpoint.Url }
                endpoint.SetResponse "refused"
                let! began = broker.Begin request
                let! refused = broker.CompleteCallback (expect began).State "c"
                Expect.isError refused "provider refusal surfaces"
                endpoint.SetResponse """{"nope":true}"""
                let! began2 = broker.Begin request
                let! malformed = broker.CompleteCallback (expect began2).State "c"
                Expect.isError malformed "malformed token response surfaces"
            }

        testCaseAsync "paste completion requires the matching pended target" <|
            async {
                let! endpoint = startTokenEndpoint ()
                let! store = openEphemeral ()
                let broker = Broker.create (fun () -> "http://m/cb") store ignore
                let request = { beginRequest with TokenUrl = endpoint.Url }
                let! began = broker.Begin request
                let! wrongTarget = broker.Complete (target (SessionScope sessionA)) (sprintf "code#%s" (expect began).State)
                Expect.isError wrongTarget "a different sign-in's code is refused (and the state burned)"
                let! began2 = broker.Begin request
                let! completed = broker.Complete request.Target (sprintf "the-code#%s" (expect began2).State)
                expect completed
                let! resolved = broker.Resolve request.Target
                Expect.equal (expect resolved) (OAuthConnection, "at-1") "stored"
                let! noState = broker.Complete request.Target "just-a-code"
                Expect.isError noState "code without a state is refused"
            }

        testCaseAsync "static put stores verbatim and never refreshes" <|
            async {
                let! store = openEphemeral ()
                let broker = Broker.create (fun () -> "http://m/cb") store ignore
                let t = target (PeerScope peer1)
                let! put = broker.Put t "sk-ant-oat01-tok"
                expect put
                let! resolved = broker.Resolve t
                Expect.equal (expect resolved) (StaticConnection, "sk-ant-oat01-tok") "verbatim"
                let! blank = broker.Put t "   "
                Expect.isError blank "blank refused"
            }

        testCaseAsync "a due grant refreshes through its own token url and the envelope updates" <|
            async {
                let! endpoint = startTokenEndpoint ()
                let! store = openEphemeral ()
                let broker = Broker.create (fun () -> "http://m/cb") store ignore
                let t = target (UserScope alice)
                // Seed a grant already inside the refresh margin, pointing at the fake.
                let stale =
                    BrokeredOAuth
                        { AccessToken = "at-stale"
                          RefreshToken = Some "rt-stale"
                          ExpiresAt = Some (DateTimeOffset.UtcNow.AddSeconds 60.0)
                          TokenUrl = endpoint.Url
                          ClientId = "cid"
                          Dialect = FormEncoded }
                let! _ = store.Set t (BrokeredCredentialCodec.toString stale)
                endpoint.SetResponse """{"access_token":"at-fresh","expires_in":3600}"""
                let! resolved = broker.Resolve t
                Expect.equal (expect resolved) (OAuthConnection, "at-fresh") "the refreshed token"
                Expect.isTrue ((endpoint.Requests.[0]).Contains "grant_type=refresh_token") "standard refresh grant"
                Expect.isTrue ((endpoint.Requests.[0]).Contains "refresh_token=rt-stale") "the stored refresh token"
                // The stored envelope updated — and kept the old refresh token (none returned).
                let! raw = store.Resolve t
                match BrokeredCredentialCodec.fromString (expect raw |> Option.defaultValue "") with
                | Ok (BrokeredOAuth g) ->
                    Expect.equal g.AccessToken "at-fresh" "envelope updated"
                    Expect.equal g.RefreshToken (Some "rt-stale") "refresh token survives an omitting response"
                | other -> failwithf "unexpected envelope: %A" other
            }

        testCaseAsync "a failed refresh is an error and keeps the old entry" <|
            async {
                let! endpoint = startTokenEndpoint ()
                let! store = openEphemeral ()
                let mutable observed : Broker.BrokerObservation list = []
                let broker = Broker.create (fun () -> "http://m/cb") store (fun o -> observed <- o :: observed)
                let t = target (UserScope alice)
                let stale =
                    BrokeredOAuth
                        { AccessToken = "at-stale"
                          RefreshToken = Some "rt-stale"
                          ExpiresAt = Some (DateTimeOffset.UtcNow.AddSeconds 60.0)
                          TokenUrl = endpoint.Url
                          ClientId = "cid"
                          Dialect = FormEncoded }
                let! _ = store.Set t (BrokeredCredentialCodec.toString stale)
                endpoint.SetResponse "refused"
                let! resolved = broker.Resolve t
                Expect.isError resolved "refresh failure surfaces"
                let! raw = store.Resolve t
                Expect.isTrue (expect raw |> Option.isSome) "the old entry survives for a reconnect"
                Expect.isTrue (observed |> List.exists (function Broker.RefreshFailed _ -> true | _ -> false)) "observed"
            }

        testCaseAsync "disconnect deletes and reports existence" <|
            async {
                let! store = openEphemeral ()
                let broker = Broker.create (fun () -> "http://m/cb") store ignore
                let t = target (UserScope alice)
                let! _ = broker.Put t "sk-ant-x"
                let! first = broker.Disconnect t
                Expect.isTrue (expect first) "existed"
                let! second = broker.Disconnect t
                Expect.isFalse (expect second) "second reports absence"
                let! resolved = broker.Resolve t
                Expect.isError resolved "nothing to resolve"
            }
    ]

// --- [Ports]: the control routes + status stream --------------------------------------------

let private caller sessionId users peers local : Control.ControlCaller =
    { SessionId = sessionId; Users = users; Peers = peers; Local = local }

/// A bare control server with the SAME pre-authorized connection handlers the Manager
/// composes (ProcessManager.connectionsApiFor), plus the ProcessManager-style status
/// broadcast: any broker change recomputes every caller's snapshot and pushes it.
let private startConnectionsServer (callers: (string * Control.ControlCaller) list) =
    async {
        let! store = openEphemeral ()
        let table = Map.ofList callers
        let hub : NotificationHub.NotificationHub<ConnectionStatusList> = NotificationHub.create ()
        let apiRef : Control.ConnectionsApi option ref = ref None
        let broadcast () =
            match apiRef.Value with
            | Some api ->
                table
                |> Map.iter (fun secret c ->
                    Async.StartImmediate (
                        async {
                            let! snapshot = api.Status c
                            hub.NotifySecret secret snapshot
                        }))
            | None -> ()
        let broker =
            Broker.create (fun () -> "http://manager.local/connections/callback") store (fun o ->
                match o with
                | Broker.Connected _ | Broker.Disconnected _ -> broadcast ()
                | _ -> ())
        let api = ProcessManager.connectionsApiFor (fun _ -> ()) store broker
        apiRef.Value <- Some api
        let dummyRegister (_: string) (_: SessionId) (_: string) : Yession.Oidc.RegisterClientResponse =
            { ClientId = "unused"; ClientSecret = "unused"; Issuer = "unused" }
        let handler (req: Interop.IncomingMessage) (res: Interop.ServerResponse) =
            if not (Control.tryHandle
                        (fun secret -> Map.tryFind secret table)
                        (fun _ _ -> async { return Ok () })
                        (fun _ _ -> async { return Ok () })
                        (fun _ _ -> Subscription.none)
                        (fun _ _ _ -> Subscription.none)
                        dummyRegister
                        None
                        (Some api)
                        hub.Register
                        ignore
                        req res) then
                res.writeHead (404, Fable.Core.JsInterop.createObj [ "content-type", box "text/plain" ]) |> ignore
                res.``end`` "not found"
        let server = Interop.createServer handler
        let! listening =
            Async.FromContinuations (fun (cont, _, _) -> server.listen (0, "127.0.0.1", fun () -> cont server) |> ignore)
        return sprintf "http://127.0.0.1:%d" (Interop.serverPort listening)
    }

let private routeTests =
    testList "connection control routes" [
        testCaseAsync "the typed client drives begin/put/resolve/disconnect; policy gates by binding" <|
            async {
                let! endpoint = startTokenEndpoint ()
                let! url =
                    startConnectionsServer
                        [ "secret-a", caller sessionA (Set.singleton alice) Set.empty false
                          "secret-b", caller sessionB Set.empty (Set.singleton peer1) false ]
                let clientA = ControlClient.connections url "secret-a"
                let clientB = ControlClient.connections url "secret-b"

                // 401 at the door.
                let! unknown = postControl (url + "/control/connections/resolve") "nope" (ControlWire.toString ControlWire.connectionResolveRequest { Target = target (UserScope alice) }) |> Async.AwaitPromise
                Expect.equal unknown.status 401 "invalid control secret"

                // A begins for its bound user; B cannot touch alice's scope.
                let request = { beginRequest with TokenUrl = endpoint.Url }
                let! began = clientA.Begin request
                Expect.isTrue ((expect began).AuthorizeUrl.Contains "code_challenge=") "began"
                let! denied = clientB.Begin request
                Expect.isError denied "unbound user denied"

                // B manages its witnessed peer's credential; A cannot resolve it.
                let! put = clientB.Put (target (PeerScope peer1)) "sk-ant-oat01-x"
                expect put
                let! resolvedB = clientB.Resolve (target (PeerScope peer1))
                Expect.equal (expect resolvedB) (StaticConnection, "sk-ant-oat01-x") "peer resolve"
                let! crossResolve = clientA.Resolve (target (PeerScope peer1))
                Expect.isError crossResolve "unwitnessed peer target denied"

                // Session scope stays per-session.
                let! putSession = clientA.Put (target (SessionScope sessionA)) "sk-ant-api03-k"
                expect putSession
                let! crossSession = clientB.Resolve (target (SessionScope sessionA))
                Expect.isError crossSession "sibling session denied"

                let! disconnected = clientB.Disconnect (target (PeerScope peer1))
                Expect.isTrue (expect disconnected) "disconnected"
            }

        testCaseAsync "the status stream sends a snapshot on subscribe and a fresh frame on change" <|
            async {
                let! url = startConnectionsServer [ "secret-a", caller sessionA (Set.singleton alice) Set.empty false ]
                let clientA = ControlClient.connections url "secret-a"
                let! _ = clientA.Put (target (UserScope alice)) "sk-ant-oat01-x"

                let frames = ResizeArray<ConnectionStatusList> ()
                let waiter : (unit -> unit) option ref = ref None
                let expectFrame (predicate: ConnectionStatusList -> bool) =
                    Async.FromContinuations (fun (cont, _, _) ->
                        if frames |> Seq.exists predicate then cont ()
                        else
                            waiter.Value <-
                                Some (fun () ->
                                    if frames |> Seq.exists predicate then
                                        waiter.Value <- None
                                        cont ()))
                let cancel =
                    ControlClient.subscribeConnections url "secret-a" (fun list ->
                        frames.Add list
                        match waiter.Value with
                        | Some check -> check ()
                        | None -> ())

                // The snapshot names the existing credential, metadata only.
                do! expectFrame (fun f -> f.Connections |> List.exists (fun s -> s.Id = target (UserScope alice) && s.Kind = StaticConnection))
                // A second credential pushes a fresh frame.
                let! _ = clientA.Put (target (SessionScope sessionA)) "sk-ant-api03-k"
                do! expectFrame (fun f -> f.Connections |> List.length = 2)
                // Disconnect shrinks it again.
                let! _ = clientA.Disconnect (target (SessionScope sessionA))
                do! expectFrame (fun f -> frames.Count >= 3 && f.Connections |> List.length = 1)
                cancel.Stop ()
            }
    ]

// --- [Ports; Native]: per-actor credentials across real processes ---------------------------
// A real Manager + real child session (YESSION_AGENT=credential-probe) + real WebRTC
// clients. Proves the whole loop: gate off before any sign-in; a paste through the
// session's /claude surface flips the gate WITHOUT a relaunch; each turn resolves the
// TURN ACTOR's credential; an unconnected actor fails legibly; a session-scoped
// credential overrides for every actor.

open Yession.Oidc
open Yession.Tests.Support

[<Emit("process.env[$0] = $1")>]
let private setEnv (name: string) (value: string) : unit = Util.jsNative

[<Emit("delete process.env[$0]")>]
let private unsetEnv (name: string) : unit = Util.jsNative

[<Emit("(process.env[$0] ?? null)")>]
let private getEnvRaw (name: string) : string option = Util.jsNative

[<Emit("process.execPath")>]
let private nodePath : string = Util.jsNative

[<Emit("""fetch($0, { method: 'POST', headers: { 'content-type': 'application/json', cookie: $1 }, body: $2 })
  .then(async r => ({ status: r.status, body: await r.text() }))""")>]
let private postJsonWithCookie (url: string) (cookie: string) (body: string) : JS.Promise<HttpReply> = Util.jsNative

[<Emit("""fetch($0, { headers: { cookie: $1 }, cache: 'no-store' }).then(async r => ({ status: r.status, body: await r.text() }))""")>]
let private getWithCookie (url: string) (cookie: string) : JS.Promise<HttpReply> = Util.jsNative

let private cookieOf (jar: OidcHttp.Jar) : string =
    jar.Cookies |> Map.toList |> List.map (fun (k, v) -> sprintf "%s=%s" k v) |> String.concat "; "

/// Poll the session's /claude status until `predicate` holds — the session learns of
/// credential changes over its control stream, so the flip is asynchronous by design.
let private awaitClaudeStatus (sessionUrl: string) (cookie: string) (predicate: string -> bool) : Async<unit> =
    let rec go attempts =
        async {
            // No peer id: the cookie is the whole identity this route reads.
            let! reply = getWithCookie (sessionUrl + "/claude") cookie |> Async.AwaitPromise
            if reply.status = 200 && predicate reply.body then return ()
            elif attempts <= 0 then return failwithf "claude status never settled; last: %d %s" reply.status reply.body
            else
                do! Async.Sleep 200
                return! go (attempts - 1)
        }
    go 50

let private e2eTests =
    testList "per-actor credentials across processes" [
        testCaseAsync "late sign-in enables the agent; each turn runs on its actor's credential; session scope overrides" <|
            async {
                let dataDir =
                    sprintf "tests/Yession.Tests/out/.data/conn-e2e-%d" (int (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) % 1000000)
                // The child must see NO ambient credentials, whatever this test process
                // inherited (CI's LiveAgent tier exports one): blank both for the spawn.
                let savedKey = getEnvRaw "ANTHROPIC_API_KEY"
                let savedToken = getEnvRaw "CLAUDE_CODE_OAUTH_TOKEN"
                setEnv "ANTHROPIC_API_KEY" ""
                setEnv "CLAUDE_CODE_OAUTH_TOKEN" ""
                setEnv "YESSION_AGENT" "credential-probe"
                let! pm =
                    ProcessManager.create
                        { ProcessManager.Options.defaults dataDir nodePath [ "app/SessionMain.js" ] with
                            Strategy = Some Strategy.localhost
                            Secrets = Some (ProcessManager.EphemeralSecrets ProcessManager.OperatorChose) }
                let record = pm.CreateSession "conn-child" "Connections child" |> expect
                let! launched = pm.Launch record.SessionId
                unsetEnv "YESSION_AGENT"
                let restore name saved =
                    match saved with
                    | Some v -> setEnv name v
                    | None -> unsetEnv name
                restore "ANTHROPIC_API_KEY" savedKey
                restore "CLAUDE_CODE_OAUTH_TOKEN" savedToken
                let port = launched |> expect
                let sessionUrl = sprintf "http://127.0.0.1:%d" port

                // Peer A signs into the session (witnessed via the bounce), then joins.
                let! openedA = OidcHttp.openSessionVia [] "/login?peer_id=browser-a" sessionUrl
                let cookieA = cookieOf openedA.Jar
                let! a = connectClient (sessionUrl + "/signal") openedA.PeerToken "browser-a" "Ada"

                // 1. Before any sign-in: the message drains to the timeline, no turn.
                do! compose a a.Hello.PeerId "hello before sign-in"
                a.Connection.SendDraft a.Hello.PeerId
                do! a.Runner.WaitFor (fun m ->
                        m.Conversation.Items
                        |> List.exists (fun i -> i.Status = Complete && i.Body.Contains "hello before sign-in"))
                // The status surface says so honestly: no agent in this session yet.
                do! awaitClaudeStatus sessionUrl cookieA (fun body -> body.Contains "\"agent\":false")

                // 2. A pastes a setup token for "all my sessions" through the session's
                //    /claude surface; the gate flips without a relaunch.
                let! putMine =
                    postJsonWithCookie
                        (sessionUrl + "/claude/token")
                        cookieA
                        """{"scope":"mine","token":"sk-ant-oat01-fake"}"""
                    |> Async.AwaitPromise
                Expect.equal putMine.status 200 (sprintf "the paste stores: %s" putMine.body)
                do! awaitClaudeStatus sessionUrl cookieA (fun body ->
                        body.Contains "\"mine\":\"static\"" && body.Contains "\"agent\":true")

                do! compose a a.Hello.PeerId "hello after sign-in"
                a.Connection.SendDraft a.Hello.PeerId
                do! a.Runner.WaitFor (fun m ->
                        m.Conversation.Items
                        |> List.exists (fun i ->
                            i.Author = ActorRef.Agent && i.Status = Complete && i.Body.Contains "credential: CLAUDE_CODE_OAUTH_TOKEN"))
                // Exactly one agent item: the pre-sign-in message triggered no turn.
                Expect.equal
                    ((a.Runner.Model ()).Conversation.Items |> List.filter (fun i -> i.Author = ActorRef.Agent) |> List.length)
                    1
                    "the gate was off for the first message"

                // 3. Peer B is a DIFFERENT browser identity that connected nothing — a new
                //    profile, a cleared store, a second device. Under this unattributed
                //    strategy every peer is the same principal, so B's turn runs on the
                //    credential A connected. This is the reconnect bug pinned: B used to be
                //    told to connect a Claude account of its own.
                let! openedB = OidcHttp.openSessionVia [] "/login?peer_id=browser-b" sessionUrl
                let! b = connectClient (sessionUrl + "/signal") openedB.PeerToken "browser-b" "Bob"
                do! compose b b.Hello.PeerId "bob on the deployment's credential"
                b.Connection.SendDraft b.Hello.PeerId
                do! b.Runner.WaitFor (fun m ->
                        m.Conversation.Items
                        |> List.exists (fun i ->
                            i.Author = ActorRef.Agent && i.Status = Complete && i.Body.Contains "credential: CLAUDE_CODE_OAUTH_TOKEN"))
                // B's own status surface agrees, without B having asserted any identity.
                let cookieB = cookieOf openedB.Jar
                do! awaitClaudeStatus sessionUrl cookieB (fun body ->
                        body.Contains "\"mine\":\"static\"" && body.Contains "\"owner\":\"local\"")

                // 4. A stores a SESSION-scoped api key: it overrides for every actor —
                //    Bob's next turn now runs on it.
                let! putSession =
                    postJsonWithCookie
                        (sessionUrl + "/claude/token")
                        cookieA
                        """{"scope":"session","token":"sk-ant-api03-fake"}"""
                    |> Async.AwaitPromise
                Expect.equal putSession.status 200 (sprintf "the session-scoped paste stores: %s" putSession.body)
                do! awaitClaudeStatus sessionUrl cookieA (fun body -> body.Contains "\"session\":\"static\"")
                do! compose b b.Hello.PeerId "bob under the session credential"
                b.Connection.SendDraft b.Hello.PeerId
                do! b.Runner.WaitFor (fun m ->
                        m.Conversation.Items
                        |> List.exists (fun i ->
                            i.Author = ActorRef.Agent && i.Status = Complete && i.Body.Contains "credential: ANTHROPIC_API_KEY"))

                // 5. Disconnect both; the status empties again.
                let! _ =
                    postJsonWithCookie (sessionUrl + "/claude/disconnect") cookieA """{"scope":"session"}"""
                    |> Async.AwaitPromise
                let! _ =
                    postJsonWithCookie (sessionUrl + "/claude/disconnect") cookieA """{"scope":"mine"}"""
                    |> Async.AwaitPromise
                do! awaitClaudeStatus sessionUrl cookieA (fun body ->
                        body.Contains "\"session\":null" && body.Contains "\"mine\":null")

                do! a.Channel.Close ()
                do! b.Channel.Close ()
                do! pm.StopAll ()
            }

        testCaseAsync "an attributed deployment shares nothing: one user's credential never runs another's turn" <|
            async {
                // The counterpart to step 3 above. Sharing is a property of deployments that
                // attribute NOBODY; where real users exist, `LocalScope` is never granted and
                // each turn runs on its own actor's credential or fails saying so. Without
                // this case the "no Claude account connected" branch has no behavioural test
                // anywhere.
                let dataDir =
                    sprintf "tests/Yession.Tests/out/.data/conn-byo-%d" (int (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) % 1000000)
                let savedKey = getEnvRaw "ANTHROPIC_API_KEY"
                let savedToken = getEnvRaw "CLAUDE_CODE_OAUTH_TOKEN"
                setEnv "ANTHROPIC_API_KEY" ""
                setEnv "CLAUDE_CODE_OAUTH_TOKEN" ""
                setEnv "YESSION_AGENT" "credential-probe"
                let! pm =
                    ProcessManager.create
                        { ProcessManager.Options.defaults dataDir nodePath [ "app/SessionMain.js" ] with
                            Strategy = Some Strategy.trustedHeaders
                            Secrets = Some (ProcessManager.EphemeralSecrets ProcessManager.OperatorChose) }
                let record = pm.CreateSession "byo-child" "BYO child" |> expect
                let! launched = pm.Launch record.SessionId
                unsetEnv "YESSION_AGENT"
                let restore name saved =
                    match saved with
                    | Some v -> setEnv name v
                    | None -> unsetEnv name
                restore "ANTHROPIC_API_KEY" savedKey
                restore "CLAUDE_CODE_OAUTH_TOKEN" savedToken
                let port = launched |> expect
                let sessionUrl = sprintf "http://127.0.0.1:%d" port

                let asUser name = [ Strategy.SubjectHeader, name ]
                let! openedAlice = OidcHttp.openSessionVia (asUser "alice@example.com") "/login" sessionUrl
                let cookieAlice = cookieOf openedAlice.Jar
                let! alice = connectClient (sessionUrl + "/signal") openedAlice.PeerToken "browser-alice" "Alice"

                // The launch was attributed, so it holds no deployment credential at all.
                let! aliceStatus = getWithCookie (sessionUrl + "/claude") cookieAlice |> Async.AwaitPromise
                Expect.isTrue (aliceStatus.body.Contains "\"owner\":\"user\"") "an attributed deployment owns by user"

                let! putMine =
                    postJsonWithCookie
                        (sessionUrl + "/claude/token")
                        cookieAlice
                        """{"scope":"mine","token":"sk-ant-oat01-alices"}"""
                    |> Async.AwaitPromise
                Expect.equal putMine.status 200 (sprintf "alice connects her own: %s" putMine.body)
                do! awaitClaudeStatus sessionUrl cookieAlice (fun body -> body.Contains "\"mine\":\"static\"")

                do! compose alice alice.Hello.PeerId "alice on her own credential"
                alice.Connection.SendDraft alice.Hello.PeerId
                do! alice.Runner.WaitFor (fun m ->
                        m.Conversation.Items
                        |> List.exists (fun i ->
                            i.Author = ActorRef.Agent && i.Status = Complete && i.Body.Contains "credential: CLAUDE_CODE_OAUTH_TOKEN"))

                // Bob is a different verified human. Alice's credential is hers, not the
                // deployment's, so his turn fails naming him rather than borrowing it.
                let! openedBob = OidcHttp.openSessionVia (asUser "bob@example.com") "/login" sessionUrl
                let cookieBob = cookieOf openedBob.Jar
                let! bob = connectClient (sessionUrl + "/signal") openedBob.PeerToken "browser-bob" "Bob"
                let! bobStatus = getWithCookie (sessionUrl + "/claude") cookieBob |> Async.AwaitPromise
                Expect.isTrue (bobStatus.body.Contains "\"mine\":null") "bob does not inherit alice's"

                do! compose bob bob.Hello.PeerId "bob without a credential"
                bob.Connection.SendDraft bob.Hello.PeerId
                // The failure reason streams as the item's body before the turn fails, so it
                // is visible in every timeline (and assertable here).
                do! bob.Runner.WaitFor (fun m ->
                        m.Conversation.Items
                        |> List.exists (fun i ->
                            i.Author = ActorRef.Agent
                            && i.Status = ConversationItemStatus.Failed
                            && i.Body.Contains "no Claude account connected for bob@example.com"))

                do! alice.Channel.Close ()
                do! bob.Channel.Close ()
                do! pm.StopAll ()
            }
    ]

// --- [Ports]: the /github sign-in routes against a stub github.com ---------------------------
// The pure tier above pins the device flow's DECISIONS (what a body decodes to, what an
// outcome folds to). This tier pins the SURFACE those decisions sit behind: who the routes
// let in, whose scope a granted token lands in, and whether one signed-in human can finish
// another's flow. The device flow is an authorization ceremony whose whole security rests on
// the device code never leaving the session — a property no pure test can observe, because it
// is about what crosses the wire.

/// A scripted github.com: `/device/code` always hands back the same code pair, `/token`
/// answers whatever the test currently wants and records what it was asked. Real GitHub
/// answers 200 for every device-flow outcome, so the stub does too — the status code is
/// never the signal (RFC 8628).
type private StubGitHub =
    { DeviceUrl : string
      TokenUrl : string
      SetTokenReply : string -> unit
      TokenRequests : ResizeArray<string> }

let private deviceCode = "dev-secret-do-not-leak"

let private startStubGitHub () : Async<StubGitHub> =
    async {
        let mutable tokenReply = """{"error":"authorization_pending"}"""
        let tokenRequests = ResizeArray<string> ()
        let handler (req: Interop.IncomingMessage) (res: Interop.ServerResponse) =
            let mutable acc = ""
            req.on ("data", fun chunk -> acc <- acc + Interop.bufferToString chunk) |> ignore
            req.on ("end", fun _ ->
                let reply =
                    if (req.url.Split('?').[0]) = "/device/code" then
                        sprintf """{"device_code":%s,"user_code":"WDJB-MJHT","verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}"""
                            (Encode.toString 0 (Encode.string deviceCode))
                    else
                        tokenRequests.Add acc
                        tokenReply
                res.writeHead (200, Fable.Core.JsInterop.createObj [ "content-type", box "application/json" ]) |> ignore
                res.``end`` reply) |> ignore
        let server = Interop.createServer handler
        let! listening =
            Async.FromContinuations (fun (cont, _, _) -> server.listen (0, "127.0.0.1", fun () -> cont server) |> ignore)
        let origin = sprintf "http://127.0.0.1:%d" (Interop.serverPort listening)
        return
            { DeviceUrl = origin + "/device/code"
              TokenUrl = origin + "/token"
              SetTokenReply = (fun r -> tokenReply <- r)
              TokenRequests = tokenRequests }
    }

/// The identity a request's cookie stands for, in this harness: `who=<name>` names a
/// user, `who=anon` is unattributed (trust-localhost) access, anything else is nobody.
/// Stands in for the OIDC round trip, which Oidc.fs already covers — what is under test
/// here is what the routes DO with an identity, not how one is established.
let private stubAuth () : SessionAuth.Auth =
    { Configure = fun _ _ _ _ -> async { return Ok () }
      IsAuthenticated = fun req -> (Interop.headerOf req "cookie").IsSome
      IdentityOf =
        fun req ->
            match Interop.headerOf req "cookie" with
            | Some cookie when cookie.StartsWith "who=" ->
                let who = cookie.Substring 4
                let attribution : Yession.SessionProcess.PeerAttribution =
                    if who = "anon" then Yession.SessionProcess.UnattributedAccess
                    else Yession.SessionProcess.AttributedUser (UserId.create who |> expect)
                Some ({ Subject = who; DisplayName = None; Attribution = attribution } : Yession.SessionProcess.CookieIdentity)
            | _ -> None
      BeginLogin = fun _ -> async { return None }
      HandleCallback = fun _ -> async { return Error (500, "not under test") }
      CookieName = "who" }

/// Every credential the routes stored, in order — the assertion surface for "whose scope
/// did that token land in". Stands in for the Manager's control connection; the broker
/// itself is proven above, so this records rather than stores.
type private RecordingConnections =
    { Client : ControlClient.SessionConnections
      Puts : ResizeArray<SecretId * string>
      Disconnects : ResizeArray<SecretId> }

let private recordingConnections () : RecordingConnections =
    let puts = ResizeArray<SecretId * string> ()
    let disconnects = ResizeArray<SecretId> ()
    { Client =
        { Begin = fun _ -> async { return Error "not under test" }
          Complete = fun _ _ -> async { return Error "not under test" }
          Put = fun target value -> async { puts.Add (target, value); return Ok () }
          Disconnect = fun target -> async { disconnects.Add target; return Ok true }
          Resolve = fun _ -> async { return Error "not under test" } }
      Puts = puts
      Disconnects = disconnects }

/// A bare server carrying only the /github handler, mounted at the origin root.
let private startGitHubRoutes (connections: ControlClient.SessionConnections) (statusOf: SecretId -> ConnectionKind option) =
    async {
        let route = GitHubConnection.routes sessionA (stubAuth ()) connections statusOf ""
        let handler (req: Interop.IncomingMessage) (res: Interop.ServerResponse) =
            if not (route req res) then
                res.writeHead (404, Fable.Core.JsInterop.createObj [ "content-type", box "text/plain" ]) |> ignore
                res.``end`` "not found"
        let server = Interop.createServer handler
        let! listening =
            Async.FromContinuations (fun (cont, _, _) -> server.listen (0, "127.0.0.1", fun () -> cont server) |> ignore)
        return sprintf "http://127.0.0.1:%d" (Interop.serverPort listening)
    }

/// Point the module at the stub for the duration of one test, and put the environment back
/// afterwards — these are process-wide and the suite runs beside others.
let private withStubGitHub (stub: StubGitHub) (clientId: string option) (body: unit -> Async<unit>) : Async<unit> =
    async {
        let names = [ "YESSION_GITHUB_DEVICE_URL"; "YESSION_GITHUB_TOKEN_URL"; "YESSION_GITHUB_CLIENT_ID" ]
        let saved = names |> List.map (fun n -> n, getEnvRaw n)
        setEnv "YESSION_GITHUB_DEVICE_URL" stub.DeviceUrl
        setEnv "YESSION_GITHUB_TOKEN_URL" stub.TokenUrl
        match clientId with
        | Some id -> setEnv "YESSION_GITHUB_CLIENT_ID" id
        | None -> unsetEnv "YESSION_GITHUB_CLIENT_ID"
        try
            do! body ()
        finally
            saved
            |> List.iter (fun (n, v) ->
                match v with
                | Some original -> setEnv n original
                | None -> unsetEnv n)
    }

let private githubRouteTests =
    testList "github sign-in routes" [
        testCaseAsync "the door: no cookie reaches nothing, and only the two scope words name a target" <|
            async {
                let! stub = startStubGitHub ()
                let recorder = recordingConnections ()
                let! url = startGitHubRoutes recorder.Client (fun _ -> None)
                do! withStubGitHub stub (Some "Iv1.test") (fun () ->
                    async {
                        // No cookie: every route is 401 before it looks at anything else. The
                        // begin route reaches github.com, so an unauthenticated caller getting
                        // past this door would make the session an open device-flow proxy.
                        let! status = getWithCookie (url + "/github") "" |> Async.AwaitPromise
                        Expect.equal status.status 401 "status is gated"
                        let! began = postJsonWithCookie (url + "/github/begin") "" """{"scope":"mine"}""" |> Async.AwaitPromise
                        Expect.equal began.status 401 "begin is gated"
                        Expect.equal stub.TokenRequests.Count 0 "nothing reached github.com"

                        // A cookie the process did not mint is no better than none.
                        let! forged = postJsonWithCookie (url + "/github/begin") "sid=forged" """{"scope":"mine"}""" |> Async.AwaitPromise
                        Expect.equal forged.status 401 "an unrecognised cookie is not an identity"

                        // Signed in, but naming a scope that is not one of the two words: the
                        // body cannot address a scope, only choose between the caller's own and
                        // this session's.
                        let! bogus = postJsonWithCookie (url + "/github/begin") "who=alice" """{"scope":"user:bob"}""" |> Async.AwaitPromise
                        Expect.equal bogus.status 400 "a scope string is not a scope"
                        Expect.isTrue (bogus.body.Contains "unknown scope choice") "and says so"

                        Expect.equal recorder.Puts.Count 0 "nothing was stored by any of it"
                    })
            }

        testCaseAsync "unattributed access owns the deployment's credential, whatever the browser calls itself" <|
            async {
                // The reconnect bug, at the surface. Under an unattributed strategy the
                // credential used to be owned by the browser's own peer id — which lives in
                // origin-partitioned localStorage, so it changed under the person holding it
                // and every new value stranded the last one's credential.
                let recorder = recordingConnections ()
                let stored = ResizeArray<SecretId> ()
                let! url = startGitHubRoutes recorder.Client (fun target -> if stored.Contains target then Some StaticConnection else None)

                let! connected =
                    postJsonWithCookie (url + "/github/token") "who=anon" """{"scope":"mine","token":"ghp_abc"}"""
                    |> Async.AwaitPromise
                Expect.equal connected.status 200 "unattributed access can connect"
                Expect.equal recorder.Puts.Count 1 "one credential stored"
                let target, _ = recorder.Puts.[0]
                Expect.equal target.Scope LocalScope "owned by the deployment, not by any browser"
                stored.Add target

                // A DIFFERENT browser — no shared storage, no shared id, nothing carried over
                // but the same deployment. Before this change it saw `"mine":null` and was
                // shown a Connect button.
                let! elsewhere = getWithCookie (url + "/github") "who=anon" |> Async.AwaitPromise
                Expect.equal elsewhere.status 200 "readable"
                Expect.isTrue (elsewhere.body.Contains "\"mine\":\"static\"") "already connected, from a browser that never connected anything"
                Expect.isTrue (elsewhere.body.Contains "\"owner\":\"local\"") "and says whose it is: the deployment's"

                // An attributed user is untouched by any of it — they own their own, and the
                // deployment's credential is not theirs to see.
                let! alicesView = getWithCookie (url + "/github") "who=alice" |> Async.AwaitPromise
                Expect.isTrue (alicesView.body.Contains "\"mine\":null") "an attributed user does not inherit it"
                Expect.isTrue (alicesView.body.Contains "\"owner\":\"user\"") "and owns by user"
            }

        testCaseAsync "begin hands the browser the user code and keeps the device code; poll paces, then connects" <|
            async {
                let! stub = startStubGitHub ()
                let recorder = recordingConnections ()
                let! url = startGitHubRoutes recorder.Client (fun _ -> None)
                do! withStubGitHub stub (Some "Iv1.test") (fun () ->
                    async {
                        let! began = postJsonWithCookie (url + "/github/begin") "who=alice" """{"scope":"mine"}""" |> Async.AwaitPromise
                        Expect.equal began.status 200 "the flow began"
                        Expect.isTrue (began.body.Contains "WDJB-MJHT") "the human is told what to type"
                        Expect.isTrue (began.body.Contains "https://github.com/login/device") "and where to type it"
                        // The device code is the half of the grant that redeems the token. It
                        // stays in the session: a browser that held it could finish the flow
                        // outside the session and keep the token for itself.
                        Expect.isFalse (began.body.Contains deviceCode) "the device code never reaches the browser"

                        // Pending: the panel is told to keep waiting, at the pace GitHub set.
                        let! pending = postJsonWithCookie (url + "/github/poll") "who=alice" """{"scope":"mine"}""" |> Async.AwaitPromise
                        Expect.equal pending.status 200 "still waiting"
                        Expect.isTrue (pending.body.Contains "\"status\":\"pending\"") "pending"
                        Expect.isTrue (pending.body.Contains "\"interval\":5") "at github's pace"
                        let request = stub.TokenRequests.[0]
                        Expect.isTrue (request.Contains "\"client_id\":\"Iv1.test\"") "the app identified itself"
                        Expect.isTrue (request.Contains deviceCode) "the session redeemed the device code it kept"
                        Expect.isTrue (request.Contains "urn:ietf:params:oauth:grant-type:device_code") "the RFC 8628 grant"

                        // slow_down widens the pace, and the wider pace STICKS: a flow that
                        // forgot it would be told to slow down forever.
                        stub.SetTokenReply """{"error":"slow_down"}"""
                        let! slowed = postJsonWithCookie (url + "/github/poll") "who=alice" """{"scope":"mine"}""" |> Async.AwaitPromise
                        Expect.isTrue (slowed.body.Contains "\"interval\":10") "widened by the spec's 5s"
                        stub.SetTokenReply """{"error":"authorization_pending"}"""
                        let! again = postJsonWithCookie (url + "/github/poll") "who=alice" """{"scope":"mine"}""" |> Async.AwaitPromise
                        Expect.isTrue (again.body.Contains "\"interval\":10") "the widened pace survives the next poll"

                        // The grant lands under the SIGNED-IN HUMAN's scope, never a scope the
                        // request named.
                        stub.SetTokenReply """{"access_token":"ghu_granted","token_type":"bearer"}"""
                        let! connected = postJsonWithCookie (url + "/github/poll") "who=alice" """{"scope":"mine"}""" |> Async.AwaitPromise
                        Expect.equal connected.status 200 "granted"
                        Expect.isTrue (connected.body.Contains "\"status\":\"connected\"") "and says so"
                        Expect.equal (List.ofSeq recorder.Puts) [ githubTarget (UserScope alice), "ghu_granted" ] "stored under alice, verbatim"

                        // The flow is spent. A replayed poll finds nothing to finish.
                        let! replay = postJsonWithCookie (url + "/github/poll") "who=alice" """{"scope":"mine"}""" |> Async.AwaitPromise
                        Expect.equal replay.status 400 "single-use"
                        Expect.equal recorder.Puts.Count 1 "and stored nothing twice"
                    })
            }

        testCaseAsync "a pending flow belongs to the target that began it — nobody else can finish it" <|
            async {
                let! stub = startStubGitHub ()
                let recorder = recordingConnections ()
                let! url = startGitHubRoutes recorder.Client (fun _ -> None)
                do! withStubGitHub stub (Some "Iv1.test") (fun () ->
                    async {
                        // Alice begins for herself. Bob is signed in too, and github.com is
                        // holding a grant that is about to be approved.
                        let! _ = postJsonWithCookie (url + "/github/begin") "who=alice" """{"scope":"mine"}""" |> Async.AwaitPromise
                        stub.SetTokenReply """{"access_token":"ghu_alices","token_type":"bearer"}"""

                        // Bob polls his own scope: there is no flow of his, so nothing happens.
                        // If pending flows were not keyed by target, Bob's poll would redeem
                        // Alice's device code and store HER token under HIS scope.
                        let! bob = postJsonWithCookie (url + "/github/poll") "who=bob" """{"scope":"mine"}""" |> Async.AwaitPromise
                        Expect.equal bob.status 400 "no flow of bob's to finish"
                        Expect.equal stub.TokenRequests.Count 0 "and bob's poll never redeemed a code"

                        // The same human's OTHER scope is a different target, so it is a
                        // different flow — the session-wide credential is not a side effect of
                        // signing in for yourself.
                        let! otherScope = postJsonWithCookie (url + "/github/poll") "who=alice" """{"scope":"session"}""" |> Async.AwaitPromise
                        Expect.equal otherScope.status 400 "session scope has no flow of its own"

                        // Alice finishes hers, and it lands where it began.
                        let! alice' = postJsonWithCookie (url + "/github/poll") "who=alice" """{"scope":"mine"}""" |> Async.AwaitPromise
                        Expect.equal alice'.status 200 "alice's flow completes"
                        Expect.equal (List.ofSeq recorder.Puts) [ githubTarget (UserScope alice), "ghu_alices" ] "under alice, and only alice"
                    })
            }

        testCaseAsync "without a configured app there is no flow to begin, and a paste is checked before it is stored" <|
            async {
                let! stub = startStubGitHub ()
                let recorder = recordingConnections ()
                let! url = startGitHubRoutes recorder.Client (fun _ -> None)
                do! withStubGitHub stub None (fun () ->
                    async {
                        // No client id: the operator has registered no App. The route says so
                        // instead of posting a half-formed grant at github.com.
                        let! began = postJsonWithCookie (url + "/github/begin") "who=alice" """{"scope":"mine"}""" |> Async.AwaitPromise
                        Expect.equal began.status 400 "nothing to begin"
                        Expect.isTrue (began.body.Contains "YESSION_GITHUB_CLIENT_ID") "names what is missing"

                        // The paste path is the day-one route, and it works with no App at all
                        // — but only for something that is actually a GitHub credential.
                        let! wrong = postJsonWithCookie (url + "/github/token") "who=alice" """{"scope":"mine","token":"sk-ant-api03-x"}""" |> Async.AwaitPromise
                        Expect.equal wrong.status 400 "a claude key is not a github one"
                        Expect.equal recorder.Puts.Count 0 "and was not stored"
                        let! pasted = postJsonWithCookie (url + "/github/token") "who=alice" """{"scope":"session","token":"ghp_pasted"}""" |> Async.AwaitPromise
                        Expect.equal pasted.status 200 "stored"
                        Expect.equal (List.ofSeq recorder.Puts) [ githubTarget (SessionScope sessionA), "ghp_pasted" ] "under the scope the human chose"

                        let! gone = postJsonWithCookie (url + "/github/disconnect") "who=alice" """{"scope":"session"}""" |> Async.AwaitPromise
                        Expect.equal gone.status 200 "disconnected"
                        Expect.equal (List.ofSeq recorder.Disconnects) [ githubTarget (SessionScope sessionA) ] "the scope the human chose"
                    })
            }

        testCaseAsync "status reports both scopes, and reports them per caller" <|
            async {
                let! stub = startStubGitHub ()
                let recorder = recordingConnections ()
                let connected = Map.ofList [ githubTarget (UserScope alice), StaticConnection ]
                let! url = startGitHubRoutes recorder.Client (fun target -> Map.tryFind target connected)
                do! withStubGitHub stub (Some "Iv1.test") (fun () ->
                    async {
                        let! forAlice = getWithCookie (url + "/github") "who=alice" |> Async.AwaitPromise
                        Expect.equal forAlice.status 200 "alice sees her own"
                        Expect.isTrue (forAlice.body.Contains "\"mine\":\"static\"") "alice is connected"
                        Expect.isTrue (forAlice.body.Contains "\"session\":null") "the session is not"
                        Expect.isTrue (forAlice.body.Contains "\"owner\":\"user\"") "as a user"

                        // The same session, a different human: status is computed from the
                        // caller's identity, so bob does not learn he is signed in because
                        // alice is.
                        let! forBob = getWithCookie (url + "/github") "who=bob" |> Async.AwaitPromise
                        Expect.isTrue (forBob.body.Contains "\"mine\":null") "bob is not connected"
                    })
            }
    ]

let tests =
    testList "Connections" [
        codecTests
        flowTests
        wireTests
        claudeTests
        githubTests
        Tag.needs "Broker service" [ Tag.Ports ] (fun () -> brokerTests)
        Tag.needs "Connection control routes" [ Tag.Ports ] (fun () -> routeTests)
        Tag.needs "GitHub sign-in routes" [ Tag.Ports ] (fun () -> githubRouteTests)
        Tag.needs "Per-actor credentials E2E" [ Tag.Ports; Tag.Native ] (fun () -> e2eTests)
    ]
