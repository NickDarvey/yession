module Fable.OpenTelemetry

// Fable bindings to the OpenTelemetry JS SDK — the *logs* signal only, the slice the
// session-process telemetry emitter uses (Plan 04). Mirrors Fable.Dockerode: a distinct
// binding layer over an npm package that declares nothing beyond what we call. A ts2fable
// pass over the packages' .d.ts is the starting point; this is the hand-trimmed surface.
//
// Fable-only: `dotnet build` type-checks it; Fable emits the JS that runs on Node. Anything
// that returns a promise surfaces as `JS.Promise<_>` for `Async.AwaitPromise` at the call
// site. Handles we only ever pass back into the SDK are opaque empty interfaces.
//
// Packages (pinned centrally in package.json): @opentelemetry/{api,api-logs,sdk-logs,
// resources,exporter-logs-otlp-http}.

open Fable.Core
open Fable.Core.JsInterop

/// A logger: emits one already-built log record. The record is a plain object
/// (`{ severityNumber; body; attributes; ... }`) — the emitter builds it; the binding
/// stays shape-agnostic about the record body.
type [<AllowNullLiteral>] Logger =
    abstract emit: record: obj -> unit

/// Opaque handles: we construct these and pass them back into the SDK, never inspect them.
type [<AllowNullLiteral>] Resource = interface end
type [<AllowNullLiteral>] LogRecordProcessor = interface end
type [<AllowNullLiteral>] LogRecordExporter = interface end

/// The provider: hands out loggers and flushes / shuts down its processors.
type [<AllowNullLiteral>] LoggerProvider =
    abstract getLogger: name: string -> Logger
    abstract forceFlush: unit -> JS.Promise<unit>
    abstract shutdown: unit -> JS.Promise<unit>

/// In-memory exporter (tests): finished records accumulate in memory for assertions. It is
/// a `LogRecordExporter`, so it drops straight into a processor.
type [<AllowNullLiteral>] InMemoryLogRecordExporter =
    inherit LogRecordExporter
    abstract getFinishedLogRecords: unit -> obj array
    abstract reset: unit -> unit

[<Emit("new $0($1)")>]
let private newWith (ctor: obj) (opts: obj) : 'a = jsNative

[<Emit("new $0()")>]
let private newEmpty (ctor: obj) : 'a = jsNative

let private loggerProviderCtor : obj = import "LoggerProvider" "@opentelemetry/sdk-logs"
let private batchProcessorCtor : obj = import "BatchLogRecordProcessor" "@opentelemetry/sdk-logs"
let private simpleProcessorCtor : obj = import "SimpleLogRecordProcessor" "@opentelemetry/sdk-logs"
let private inMemoryExporterCtor : obj = import "InMemoryLogRecordExporter" "@opentelemetry/sdk-logs"
let private otlpExporterCtor : obj = import "OTLPLogExporter" "@opentelemetry/exporter-logs-otlp-http"
let private resourceFromAttributes : obj -> Resource = import "resourceFromAttributes" "@opentelemetry/resources"

/// The OTel logs severity number for INFO (9 in the data model), read from the SDK enum so
/// it tracks the package rather than a magic literal.
let severityInfo : int = unbox (import "SeverityNumber" "@opentelemetry/api-logs")?INFO

/// A Resource from a flat attribute bag, e.g. `{ "service.name": "yession-session" }`.
let resource (attributes: obj) : Resource = resourceFromAttributes attributes

/// A LoggerProvider wired to one processor over the given resource (SDK 2.x config form).
let loggerProvider (resource: Resource) (processor: LogRecordProcessor) : LoggerProvider =
    newWith loggerProviderCtor (createObj [ "resource", box resource; "processors", box [| processor |] ])

// Both processors take an options object `{ exporter; ... }` (SDK 2.x) — NOT the bare
// exporter. Passing the exporter directly silently no-ops: the export throws on
// `undefined.export` and the processor swallows it via the global error handler.

/// Batch processor: exports asynchronously off the caller's path (production).
let batchProcessor (exporter: LogRecordExporter) : LogRecordProcessor =
    newWith batchProcessorCtor (createObj [ "exporter", box exporter ])

/// Simple processor: exports per record (tests).
let simpleProcessor (exporter: LogRecordExporter) : LogRecordProcessor =
    newWith simpleProcessorCtor (createObj [ "exporter", box exporter ])

/// OTLP/HTTP logs exporter (JSON) posting to `url`, with the given headers object.
let otlpLogExporter (url: string) (headers: obj) : LogRecordExporter =
    newWith otlpExporterCtor (createObj [ "url", box url; "headers", box headers ])

/// In-memory exporter for tests.
let inMemoryExporter () : InMemoryLogRecordExporter = newEmpty inMemoryExporterCtor
