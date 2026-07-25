module Yession.Tests.Telemetry

// Plan 04, Steps 29–30. Two cheap-tier suites (in-memory exporter, no ports, no
// credentials, no native addons):
//   - the Fable.OpenTelemetry bindings resolve against the real SDK and round-trip a record;
//   - the app/Telemetry.fs emitter maps an AgentUsage onto a log record with the right
//     attributes, and never throws (disabled no-op; dead-endpoint export).

open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto
open Fable.OpenTelemetry
open Yession.Domain
open Yession.Host

// Local (not `Support.expect`) so this suite stays free of the WebRTC/native-addon import
// chain — the telemetry tests are pure in-memory and need none of the client harness.
let private expect = function Ok v -> v | Error e -> failwith e

/// Read a JS field by (possibly dotted) string key — attribute keys aren't F# identifiers.
[<Emit("$0[$1]")>]
let private field (o: obj) (key: string) : obj = jsNative

/// A logger backed by an in-memory exporter, plus the exporter for assertions.
let private inMemoryLogger () : Logger * InMemoryLogRecordExporter =
    let mem = inMemoryExporter ()
    let provider =
        loggerProvider
            (resource (createObj [ "service.name", box "yession-test" ]))
            (simpleProcessor (mem :> LogRecordExporter))
    provider.getLogger "yession-test", mem

let private bindingTests =
    testList "bindings" [
        testCase "a logger emits one record into the in-memory exporter" <| fun () ->
            let logger, mem = inMemoryLogger ()
            logger.emit (
                createObj [
                    "severityNumber", box severityInfo
                    "body", box "agent turn usage"
                    "attributes", box (createObj [ "gen_ai.usage.input_tokens", box 11 ])
                ]
            )
            Expect.equal (mem.getFinishedLogRecords ()).Length 1 "exactly one record reached the exporter"
    ]

let private emitterTests =
    testList "emitter (app/Telemetry.fs)" [
        testCase "emitTo maps an AgentUsage onto a log record with the expected attributes" <| fun () ->
            let logger, mem = inMemoryLogger ()
            let sessionId = SessionId.create "sess-x" |> expect
            let turnId = AgentTurnId.create "turn-1" |> expect
            Telemetry.emitTo logger sessionId turnId
                { InputTokens = 11
                  OutputTokens = 7
                  CacheReadTokens = 3
                  CacheCreationTokens = 5
                  Model = Some "claude-opus-4-8" }

            let records = mem.getFinishedLogRecords ()
            Expect.equal records.Length 1 "one record emitted"
            let record = records.[0]
            Expect.equal (unbox<string> (field record "body")) "agent turn usage" "body names the signal"
            let attrs = field record "attributes"
            Expect.equal (unbox<int> (field attrs "gen_ai.usage.input_tokens")) 11 "input tokens"
            Expect.equal (unbox<int> (field attrs "gen_ai.usage.output_tokens")) 7 "output tokens"
            Expect.equal (unbox<int> (field attrs "anthropic.usage.cache_read_input_tokens")) 3 "cache read tokens"
            Expect.equal (unbox<int> (field attrs "anthropic.usage.cache_creation_input_tokens")) 5 "cache creation tokens"
            Expect.equal (unbox<string> (field attrs "yession.session.id")) "sess-x" "session id (an identifier, not content)"
            Expect.equal (unbox<string> (field attrs "yession.agent.turn.id")) "turn-1" "agent turn id"
            Expect.equal (unbox<string> (field attrs "gen_ai.response.model")) "claude-opus-4-8" "model when the SDK reports it"

        testCase "the model attribute is absent when the runner reports no model" <| fun () ->
            let logger, mem = inMemoryLogger ()
            let sessionId = SessionId.create "sess-nomodel" |> expect
            let turnId = AgentTurnId.create "turn-n" |> expect
            Telemetry.emitTo logger sessionId turnId
                { InputTokens = 1; OutputTokens = 1; CacheReadTokens = 0; CacheCreationTokens = 0; Model = None }
            let attrs = field (mem.getFinishedLogRecords ()).[0] "attributes"
            Expect.isTrue (isNull (field attrs "gen_ai.response.model")) "no model key when Model = None"

        testCase "the disabled emitter is a no-op and never throws" <| fun () ->
            let turnId = AgentTurnId.create "turn-2" |> expect
            Telemetry.disabled.Emit turnId
                { InputTokens = 1; OutputTokens = 1; CacheReadTokens = 0; CacheCreationTokens = 0; Model = None }

        testCaseAsync "fromEnv without an endpoint is disabled; a dead endpoint never throws on Emit" <|
            async {
                let sessionId = SessionId.create "sess-z" |> expect
                // No YESSION_OTLP_ENDPOINT in the cheap tier -> disabled.
                let off = Telemetry.fromEnv sessionId
                off.Emit (AgentTurnId.create "t" |> expect)
                    { InputTokens = 0; OutputTokens = 0; CacheReadTokens = 0; CacheCreationTokens = 0; Model = None }
                do! off.Shutdown () |> Async.AwaitPromise

                // A real emitter to a dead endpoint: Emit enqueues (async export), never throws;
                // Shutdown flushes and clears the batch timer.
                let dead = Telemetry.create sessionId "http://127.0.0.1:1" "secret"
                dead.Emit (AgentTurnId.create "t2" |> expect)
                    { InputTokens = 2; OutputTokens = 2; CacheReadTokens = 0; CacheCreationTokens = 0; Model = None }
                do! dead.Shutdown () |> Async.AwaitPromise
            }
    ]

// fetch helpers for the receiver's HTTP surface.
[<Emit("fetch($0, { method: 'POST', headers: { 'content-type': 'application/json', 'authorization': $2 }, body: $1 }).then(r => r.status)")>]
let private postJson (url: string) (body: string) (auth: string) : JS.Promise<int> = jsNative

[<Emit("fetch($0).then(r => r.status)")>]
let private getStatus (url: string) : JS.Promise<int> = jsNative

/// A running receiver server: its base URL, the collector, and a close.
let private startReceiver (secret: string) : Async<string * TelemetryReceiver.Collector * (unit -> unit)> =
    async {
        let collector = TelemetryReceiver.Collector.inMemory ()
        let server =
            Interop.createServer (fun req res ->
                if not (TelemetryReceiver.tryHandle (fun s -> s = secret) collector req res) then
                    res.writeHead (404, createObj [ "content-type", box "text/plain" ]) |> ignore
                    res.``end`` "not found")
        let! url =
            Async.FromContinuations (fun (cont, _, _) ->
                server.listen (0, "127.0.0.1", fun () -> cont (sprintf "http://127.0.0.1:%d" (Interop.serverPort server)))
                |> ignore)
        return url, collector, (fun () -> server.close ignore)
    }

let private receiverTests =
    testList "receiver (app/TelemetryReceiver.fs)" [
        testCase "decode reads records and tolerates intValue as number or string" <| fun () ->
            let json =
                """{"resourceLogs":[{"scopeLogs":[{"logRecords":[
                    {"body":{"stringValue":"agent turn usage"},"attributes":[
                        {"key":"yession.session.id","value":{"stringValue":"s"}},
                        {"key":"yession.agent.turn.id","value":{"stringValue":"t"}},
                        {"key":"gen_ai.usage.input_tokens","value":{"intValue":11}},
                        {"key":"gen_ai.usage.output_tokens","value":{"intValue":"7"}}]}]}]}]}"""
            match TelemetryReceiver.LogsWire.decode json with
            | Ok [ record ] ->
                match TelemetryReceiver.TurnUsage.ofLog record with
                | Some usage -> Expect.equal (usage.InputTokens, usage.OutputTokens) (11, 7) "intValue decodes whether number or string"
                | None -> failwith "not recognised as agent-turn usage"
            | other -> failwithf "expected exactly one record, got %A" other

        testCase "a non-usage record (no yession ids) is decoded but not read as usage" <| fun () ->
            match TelemetryReceiver.LogsWire.decode """{"resourceLogs":[{"scopeLogs":[{"logRecords":[{"body":{"stringValue":"hi"},"attributes":[]}]}]}]}""" with
            | Ok [ record ] -> Expect.isTrue (TelemetryReceiver.TurnUsage.ofLog record |> Option.isNone) "no yession ids -> not usage"
            | other -> failwithf "expected one record, got %A" other

        testCase "malformed JSON is a decode error, never a crash" <| fun () ->
            match TelemetryReceiver.LogsWire.decode "{not json" with
            | Error _ -> ()
            | Ok _ -> failwith "malformed body must not decode"

        // OTLP sends resource attributes ONCE per payload, not per record. They identify the
        // emitter — `service.version` is what attributes a turn's counts to a build — so the
        // decoder folds them onto every record underneath, or that identity is lost on arrival.
        testCase "resource attributes land on every record" <| fun () ->
            let json =
                """{"resourceLogs":[{"resource":{"attributes":[
                    {"key":"service.name","value":{"stringValue":"yession-session"}},
                    {"key":"service.version","value":{"stringValue":"1.2.3-beta.4"}}]},
                  "scopeLogs":[{"logRecords":[
                    {"body":{"stringValue":"agent turn usage"},"attributes":[
                        {"key":"yession.session.id","value":{"stringValue":"s"}},
                        {"key":"yession.agent.turn.id","value":{"stringValue":"t"}}]},
                    {"body":{"stringValue":"second"},"attributes":[]}]}]}]}"""
            match TelemetryReceiver.LogsWire.decode json with
            | Ok [ first; second ] ->
                let version (r: TelemetryReceiver.ReceivedLog) = Map.tryFind "service.version" r.Attributes
                Expect.equal (version first) (Some (TelemetryReceiver.StringValue "1.2.3-beta.4")) "the usage record carries the emitter's build"
                Expect.equal (version second) (Some (TelemetryReceiver.StringValue "1.2.3-beta.4")) "so does every other record under that resource"
                match TelemetryReceiver.TurnUsage.ofLog first with
                | Some usage -> Expect.equal usage.Version (Some "1.2.3-beta.4") "usage reports the emitting build"
                | None -> failwith "not recognised as agent-turn usage"
            | other -> failwithf "expected two records, got %A" other

        testCase "a record-level attribute wins over the resource, and a missing resource is not a crash" <| fun () ->
            let json =
                """{"resourceLogs":[{"resource":{"attributes":[
                    {"key":"service.version","value":{"stringValue":"from-resource"}}]},
                  "scopeLogs":[{"logRecords":[{"body":{"stringValue":"x"},"attributes":[
                        {"key":"service.version","value":{"stringValue":"from-record"}}]}]}]},
                  {"scopeLogs":[{"logRecords":[{"body":{"stringValue":"y"},"attributes":[]}]}]}]}"""
            match TelemetryReceiver.LogsWire.decode json with
            | Ok [ overridden; noResource ] ->
                Expect.equal
                    (Map.tryFind "service.version" overridden.Attributes)
                    (Some (TelemetryReceiver.StringValue "from-record"))
                    "the more specific record-level attribute wins"
                Expect.isTrue (Map.isEmpty noResource.Attributes) "a resourceLogs entry with no resource block decodes, it does not throw"
            | other -> failwithf "expected two records, got %A" other

        testCaseAsync "round-trip: the emitter's real OTLP payload decodes at the receiver into the turn's counts" <|
            async {
                let! url, collector, close = startReceiver "rt-secret"
                let sessionId = SessionId.create "rt-sess" |> expect
                let emitter = Telemetry.create sessionId url "rt-secret"
                emitter.Emit (AgentTurnId.create "rt-turn" |> expect)
                    { InputTokens = 42; OutputTokens = 9; CacheReadTokens = 4; CacheCreationTokens = 6; Model = Some "claude-opus-4-8" }
                do! emitter.Shutdown () |> Async.AwaitPromise

                let received = collector.Received ()
                Expect.equal received.Length 1 "the receiver decoded exactly one record"
                match TelemetryReceiver.TurnUsage.ofLog received.[0] with
                | Some u ->
                    Expect.equal u.SessionId "rt-sess" "session id survives the wire"
                    Expect.equal u.TurnId "rt-turn" "turn id survives the wire"
                    Expect.equal (u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheCreationTokens) (42, 9, 4, 6) "all four counts survive"
                    Expect.equal u.Model (Some "claude-opus-4-8") "model survives"
                    // The emitter puts this on the OTel *resource*, so this asserts the whole
                    // path: resource -> OTLP payload -> decode -> folded onto the record.
                    Expect.equal u.Version (Some Version.current) "the emitting build survives the wire"
                | None -> failwith "the decoded record was not recognised as agent-turn usage"
                close ()
            }

        testCaseAsync "auth: a wrong bearer is 401; an authorized empty payload is 200; a non-telemetry path falls through" <|
            async {
                let! url, _, close = startReceiver "good-secret"
                let! badBearer = postJson (url + "/v1/logs") """{"resourceLogs":[]}""" "Bearer wrong" |> Async.AwaitPromise
                Expect.equal badBearer 401 "a wrong bearer is rejected"
                let! authorized = postJson (url + "/v1/logs") """{"resourceLogs":[]}""" "Bearer good-secret" |> Async.AwaitPromise
                Expect.equal authorized 200 "an authorized payload is accepted"
                let! fellThrough = getStatus (url + "/not-telemetry") |> Async.AwaitPromise
                Expect.equal fellThrough 404 "a non-telemetry path falls through to the composing server"
                close ()
            }
    ]

// app/Version.fs. `majorOf` gates the spawn-time skew warning, so what it must NOT do is read a
// major out of a build that has no release version — that would warn on every dev run.
let private versionTests =
    testList "version (app/Version.fs)" [
        testCase "a release version yields its major" <| fun () ->
            Expect.equal (Version.majorOf "1.0.0-beta.94") (Some 1) "prerelease of the 1.x line"
            Expect.equal (Version.majorOf "2.0.0-beta.0") (Some 2) "the first build after a breaking change"
            Expect.equal (Version.majorOf "0.0.0-g1a2b3c4") (Some 0) "a pure Nix build reports its rev off the 0.0.0 line"

        testCase "a build with no release version has no major" <| fun () ->
            Expect.equal (Version.majorOf "dev") None "an unbundled dev run"
            Expect.equal (Version.majorOf "test") None "the test tiers"
            Expect.equal (Version.majorOf "") None "empty"
            Expect.equal (Version.majorOf "not.a.version") None "malformed"

        testCase "an unbundled run reports dev, never a version-shaped placeholder" <| fun () ->
            // These tests are Fable output run straight on Node — no esbuild, so no define.
            Expect.equal Version.current "dev" "the fallback names the build path it belongs to"
    ]

let tests = testList "Telemetry" [ bindingTests; emitterTests; receiverTests; versionTests ]
