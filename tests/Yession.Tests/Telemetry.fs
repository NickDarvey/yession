module Yession.Tests.Telemetry

// Plan 04, Step 29: a smoke over the Fable.OpenTelemetry bindings — wire a LoggerProvider to
// an in-memory exporter, emit one record, and assert it lands. Proves the binding surface
// resolves against the real SDK and round-trips a record. Cheap tier: in-memory, no ports,
// no network, no credentials.

open Fable.Pyxpecto
open Fable.Core.JsInterop
open Fable.OpenTelemetry

let tests =
    testList "Telemetry bindings (Fable.OpenTelemetry)" [
        testCase "a logger emits one record into the in-memory exporter" <| fun () ->
            let mem = inMemoryExporter ()
            let provider =
                loggerProvider
                    (resource (createObj [ "service.name", box "yession-session-test" ]))
                    (simpleProcessor (mem :> LogRecordExporter))
            let logger = provider.getLogger "yession-test"

            logger.emit (
                createObj [
                    "severityNumber", box severityInfo
                    "body", box "agent turn usage"
                    "attributes",
                    box (
                        createObj [
                            "gen_ai.usage.input_tokens", box 11
                            "gen_ai.usage.output_tokens", box 7
                        ]
                    )
                ]
            )

            let finished = mem.getFinishedLogRecords ()
            Expect.equal finished.Length 1 "exactly one record reached the exporter"
    ]
