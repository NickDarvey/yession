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

let tests = testList "Telemetry" [ bindingTests; emitterTests ]
