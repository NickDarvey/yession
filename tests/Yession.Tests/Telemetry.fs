module Yession.Tests.Telemetry

// Cheap-tier telemetry suites (no ports beyond localhost, no credentials, no native addons):
//   - the Fable.OpenTelemetry bindings resolve against the real SDK and round-trip a record;
//   - the app/Telemetry.fs emitter maps an AgentUsage onto a log record with the right
//     attributes, never throws, and selects its exporters from the standard OTEL_* env;
//   - a real OTLP payload reaches a stub collector intact — with no message content.
// Every process is a direct emitter now (no Manager-side collector); the stub stands in for a
// real OpenTelemetry Collector.

open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto
open Fable.OpenTelemetry
open Yession.Domain
open Yession.Host

// Local (not `Support.expect`) so this suite stays free of the WebRTC/native-addon import
// chain — the telemetry tests are pure in-memory / localhost and need none of the client harness.
let private expect = function Ok v -> v | Error e -> failwith e

/// Read a JS field by (possibly dotted) string key — attribute keys aren't F# identifiers.
[<Emit("$0[$1]")>]
let private field (o: obj) (key: string) : obj = jsNative

[<Emit("process.env[$0] = $1")>]
let private setEnv (key: string) (value: string) : unit = jsNative

[<Emit("delete process.env[$0]")>]
let private unsetEnv (key: string) : unit = jsNative

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

        testCase "a two-processor provider tees one emit to both exporters (console + in-memory)" <| fun () ->
            // The SDK-native tee: console (stdout) + in-memory, one emit, both fire.
            let mem = inMemoryExporter ()
            let provider =
                loggerProviderMulti
                    (resource (createObj [ "service.name", box "yession-test" ]))
                    [ simpleProcessor (consoleLogExporter ())
                      simpleProcessor (mem :> LogRecordExporter) ]
            let logger = provider.getLogger "yession-test"
            logger.emit (createObj [ "severityNumber", box severityInfo; "body", box "tee"; "attributes", box (createObj []) ])
            Expect.equal (mem.getFinishedLogRecords ()).Length 1 "the in-memory leg of the tee received the record"
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
            Telemetry.disabled.Log "manager started" [ "k", box "v" ]

        testCaseAsync "OTEL_LOGS_EXPORTER=none (or OTEL_SDK_DISABLED) yields a disabled emitter" <|
            async {
                let sessionId = SessionId.create "sess-none" |> expect
                setEnv "OTEL_LOGS_EXPORTER" "none"
                let off = Telemetry.fromEnv sessionId
                off.Emit (AgentTurnId.create "t" |> expect)
                    { InputTokens = 9; OutputTokens = 9; CacheReadTokens = 0; CacheCreationTokens = 0; Model = None }
                do! off.Shutdown () |> Async.AwaitPromise
                unsetEnv "OTEL_LOGS_EXPORTER"
            }

        testCaseAsync "a dead OTLP endpoint never throws on Emit; Shutdown flushes cleanly" <|
            async {
                let sessionId = SessionId.create "sess-z" |> expect
                let dead = Telemetry.createOtlp sessionId "http://127.0.0.1:1/v1/logs"
                dead.Emit (AgentTurnId.create "t2" |> expect)
                    { InputTokens = 2; OutputTokens = 2; CacheReadTokens = 0; CacheCreationTokens = 0; Model = None }
                do! dead.Shutdown () |> Async.AwaitPromise
            }
    ]

let private forwardingTests =
    testList "forwarding to a collector (stub stands in for a real OTel Collector)" [
        // The resource block is optional in OTLP, and the decoder must not assume it — a payload
        // without one is a decode with no resource attributes, never a throw.
        testCase "a payload with no resource block still decodes" <| fun () ->
            let json = """{"resourceLogs":[{"scopeLogs":[{"logRecords":[{"body":{"stringValue":"x"},"attributes":[]}]}]}]}"""
            match OtlpStub.decode json with
            | [ record ] ->
                Expect.isTrue (Map.isEmpty record.Resource) "no resource block means no resource attributes"
                Expect.equal (OtlpStub.resourceAttr "service.version" record) None "and nothing to read off it"
            | other -> failwithf "expected one record, got %d" (List.length other)

        testCaseAsync "round-trip: the emitter's real OTLP payload reaches the collector with counts + ids, no content" <|
            async {
                let! stub = OtlpStub.start ()
                let sessionId = SessionId.create "rt-sess" |> expect
                let emitter = Telemetry.createOtlp sessionId stub.Url
                emitter.Emit (AgentTurnId.create "rt-turn" |> expect)
                    { InputTokens = 42; OutputTokens = 9; CacheReadTokens = 4; CacheCreationTokens = 6; Model = Some "claude-opus-4-8" }
                do! emitter.Shutdown () |> Async.AwaitPromise

                let received = stub.Received ()
                Expect.equal received.Length 1 "the collector received exactly one record"
                match OtlpStub.turnUsage received.[0] with
                | Some u ->
                    Expect.equal u.SessionId "rt-sess" "session id survives the wire"
                    Expect.equal u.TurnId "rt-turn" "turn id survives the wire"
                    Expect.equal (u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheCreationTokens) (42, 9, 4, 6) "all four counts survive"
                    Expect.equal u.Model (Some "claude-opus-4-8") "model survives"
                    Expect.equal received.[0].Body "agent turn usage" "the body names the signal — never message content"
                | None -> failwith "the received record was not recognised as agent-turn usage"
                // The emitting BUILD, off the resource — this is the whole path: code default ->
                // OTel resource -> OTLP payload -> decode. Without it a collector cannot tell two
                // releases' counts apart.
                Expect.equal
                    (OtlpStub.resourceAttr "service.version" received.[0])
                    (Some Version.current)
                    "the record names the build that emitted it"
                Expect.equal
                    (OtlpStub.resourceAttr "service.name" received.[0])
                    (Some "yession-session")
                    "alongside the identity it already carried"
                stub.Close ()
            }

        testCaseAsync "OTEL_LOGS_EXPORTER=otlp + OTEL_EXPORTER_OTLP_ENDPOINT routes fromEnv to the collector" <|
            async {
                let! stub = OtlpStub.start ()
                let sessionId = SessionId.create "env-sess" |> expect
                setEnv "OTEL_LOGS_EXPORTER" "otlp"
                setEnv "OTEL_EXPORTER_OTLP_ENDPOINT" stub.Url
                let emitter = Telemetry.fromEnv sessionId
                emitter.Emit (AgentTurnId.create "env-turn" |> expect)
                    { InputTokens = 5; OutputTokens = 6; CacheReadTokens = 0; CacheCreationTokens = 0; Model = None }
                do! emitter.Shutdown () |> Async.AwaitPromise
                unsetEnv "OTEL_LOGS_EXPORTER"
                unsetEnv "OTEL_EXPORTER_OTLP_ENDPOINT"

                match stub.Received () |> List.choose OtlpStub.turnUsage with
                | [ u ] ->
                    Expect.equal u.SessionId "env-sess" "the env-configured emitter reached the collector"
                    Expect.equal (u.InputTokens, u.OutputTokens) (5, 6) "counts intact"
                | other -> failwithf "expected one record via the env-configured exporter, got %d" (List.length other)
                stub.Close ()
            }

        // Env beats the code default, per the standard OTel precedence `resourceOf` implements.
        // This is precisely why `service.version` must NOT go into the OTEL_RESOURCE_ATTRIBUTES
        // the Manager injects into a child: doing so would override the child's own build with
        // the Manager's, and a version skew would become invisible instead of obvious.
        testCaseAsync "OTEL_RESOURCE_ATTRIBUTES overrides the built-in service.version" <|
            async {
                let! stub = OtlpStub.start ()
                let sessionId = SessionId.create "ovr-sess" |> expect
                setEnv "OTEL_LOGS_EXPORTER" "otlp"
                setEnv "OTEL_EXPORTER_OTLP_ENDPOINT" stub.Url
                setEnv "OTEL_RESOURCE_ATTRIBUTES" "service.version=set-by-operator"
                let emitter = Telemetry.fromEnv sessionId
                emitter.Emit (AgentTurnId.create "ovr-turn" |> expect)
                    { InputTokens = 1; OutputTokens = 1; CacheReadTokens = 0; CacheCreationTokens = 0; Model = None }
                do! emitter.Shutdown () |> Async.AwaitPromise
                unsetEnv "OTEL_LOGS_EXPORTER"
                unsetEnv "OTEL_EXPORTER_OTLP_ENDPOINT"
                unsetEnv "OTEL_RESOURCE_ATTRIBUTES"

                match stub.Received () with
                | r :: _ ->
                    Expect.equal
                        (OtlpStub.resourceAttr "service.version" r)
                        (Some "set-by-operator")
                        "an operator-set resource attribute wins over the code default"
                | [] -> failwith "no record reached the collector"
                stub.Close ()
            }
    ]

// --- Manager audit records (Plan 06 telemetry) -------------------------------------------

let private auditTests =
    let sessionId = SessionId.create "audit-sess" |> expect
    let name = SecretName.create "deploy-token" |> expect
    let id : SecretId = { Scope = SessionScope sessionId; Name = name }
    let stringAttr key (r: SecretStore.Audit.Record) =
        match Map.tryFind key r.Attributes with
        | Some (SecretStore.Audit.StringValue s) -> Some s
        | _ -> None
    testList "audit (Manager in-process records)" [
        testCase "every constructor carries event.name, service.name, and its severity" <| fun () ->
            let alice = UserId.create "alice" |> expect
            let cases =
                [ SecretStore.Audit.secretSet sessionId id true, "yession.secret.set", 9
                  SecretStore.Audit.secretSet sessionId id false, "yession.secret.set", 13
                  SecretStore.Audit.secretDelete sessionId id true, "yession.secret.delete", 9
                  SecretStore.Audit.secretList sessionId (SessionScope sessionId) 2, "yession.secret.list", 9
                  SecretStore.Audit.authzDeny sessionId (SecretAction SetSecret) (SecretResource id) "why", "yession.authz.deny", 13
                  SecretStore.Audit.inject sessionId name "session", "yession.secret.inject", 9
                  SecretStore.Audit.injectMiss sessionId name "none left", "yession.secret.inject", 13
                  SecretStore.Audit.storeOpen "durable" "in-memory" true 0, "yession.secrets.store_open", 9
                  SecretStore.Audit.storeEphemeral, "yession.secrets.store_open", 13
                  SecretStore.Audit.storeInaccessible "/tmp/x", "yession.secrets.store_open", 13
                  SecretStore.Audit.storeOpenFailed "corrupt" "detail", "yession.secrets.store_open_failed", 17
                  SecretStore.Audit.bindingRecorded sessionId alice, "yession.auth.binding_recorded", 9
                  SecretStore.Audit.bindingRevoked sessionId, "yession.auth.binding_revoked", 9
                  SecretStore.Audit.controlUnauthorized "/control/secrets/set", "yession.control.unauthorized", 13 ]
            for r, expectedName, severity in cases do
                Expect.equal (stringAttr "event.name" r) (Some expectedName) "event.name"
                Expect.equal (stringAttr "service.name" r) (Some "yession-manager") "service.name"
                Expect.equal r.Severity severity (sprintf "severity of %s" expectedName)

        testCase "the deny record keeps the old printfn's full sentence and attributes" <| fun () ->
            let r = SecretStore.Audit.authzDeny sessionId (SecretAction SetSecret) (SecretResource id) "not the owning session"
            Expect.equal r.Body "secrets: DENY SetSecret for session audit-sess: not the owning session" "printfn parity"
            Expect.equal (stringAttr "yession.authz.action" r) (Some "SetSecret") "action attr"
            Expect.equal (stringAttr "yession.secret.name" r) (Some "deploy-token") "resource name attr"
            Expect.equal (stringAttr "yession.secret.scope" r) (Some "session") "scope attr"

        // The only two rendered-string pins — everything else asserts attributes.
        testCase "format renders one deterministic INFO line" <| fun () ->
            let line = SecretStore.Audit.format (SecretStore.Audit.inject sessionId name "session")
            Expect.equal
                line
                "audit INFO yession.secret.inject yession.inject.source=session yession.secret.name=deploy-token yession.session.id=audit-sess :: secret injected into environment"
                "stable field order (Map is key-sorted)"
        testCase "format renders one deterministic WARN line" <| fun () ->
            let line = SecretStore.Audit.format (SecretStore.Audit.controlUnauthorized "/control/start")
            Expect.equal
                line
                "audit WARN yession.control.unauthorized yession.http.path=/control/start :: invalid control secret"
                "stable WARN rendering"
    ]

let tests = testList "Telemetry" [ bindingTests; emitterTests; forwardingTests; auditTests ]
