module Yession.Host.Telemetry

// Plan 04, Step 30: the Session Process telemetry emitter. One OpenTelemetry *log record*
// per completed agent turn, carrying the token/cache counts — never message content.
// Fire-and-forget: a dropped export must never fail or stall a turn, so `Emit` swallows
// everything and the batch processor exports asynchronously off the turn path. Lives in the
// host layer over Fable.OpenTelemetry; `Yession.SessionProcess` stays OTel-free and receives
// `Emit` as an injected sink (wired through `Host.startFull` in Step 32).

open Fable.Core
open Fable.Core.JsInterop
open Yession.Domain

/// The binding layer (module `Fable.OpenTelemetry`), qualified for clarity in app code.
module OpenTelemetry = Fable.OpenTelemetry

[<Emit("Promise.resolve()")>]
let private resolved () : JS.Promise<unit> = jsNative

/// The turn-usage sink plus a graceful flush. `Emit turnId usage` records one log record;
/// `Shutdown` flushes the batch processor (call on process exit so buffered records ship).
type Emitter =
    { Emit : AgentTurnId -> AgentUsage -> unit
      Shutdown : unit -> JS.Promise<unit> }

/// Telemetry off: every emit is a no-op, shutdown resolves immediately.
let disabled : Emitter = { Emit = (fun _ _ -> ()); Shutdown = resolved }

let private serviceName = "yession-session"

/// Build and emit one log record on `logger` for a completed turn. Attribute names follow
/// the OTel GenAI conventions; the `cache_*` pair is an Anthropic extension. Identifiers
/// only — no message body, prompt, or completion ever appears here.
let emitTo (logger: OpenTelemetry.Logger) (sessionId: SessionId) (turnId: AgentTurnId) (usage: AgentUsage) : unit =
    let attributes =
        [ "gen_ai.system", box "anthropic"
          "gen_ai.operation.name", box "agent_turn"
          "gen_ai.usage.input_tokens", box usage.InputTokens
          "gen_ai.usage.output_tokens", box usage.OutputTokens
          "anthropic.usage.cache_read_input_tokens", box usage.CacheReadTokens
          "anthropic.usage.cache_creation_input_tokens", box usage.CacheCreationTokens
          "yession.session.id", box (SessionId.value sessionId)
          "yession.agent.turn.id", box (AgentTurnId.value turnId) ]
        @ (match usage.Model with
           | Some model -> [ "gen_ai.response.model", box model ]
           | None -> [])
    logger.emit (
        createObj
            [ "severityNumber", box OpenTelemetry.severityInfo
              "body", box "agent turn usage"
              "attributes", box (createObj attributes) ])

/// The resource identifying this session process as an OTel resource. `service.version` is what
/// attributes a record to a BUILD — without it the counts below cannot be told apart across
/// releases.
let private resourceFor (sessionId: SessionId) : OpenTelemetry.Resource =
    OpenTelemetry.resource (
        createObj
            [ "service.name", box serviceName
              "service.version", box Version.current
              "service.namespace", box "yession"
              "service.instance.id", box (SessionId.value sessionId)
              "yession.session.id", box (SessionId.value sessionId) ])

/// An emitter exporting OTLP logs (JSON) to `endpoint`/v1/logs with a bearer secret. The
/// batch processor exports asynchronously off the turn path; `Emit` additionally swallows
/// any synchronous throw, so telemetry can never break a turn.
let create (sessionId: SessionId) (endpoint: string) (secret: string) : Emitter =
    let url = endpoint.TrimEnd '/' + "/v1/logs"
    let headers = createObj [ "Authorization", box ("Bearer " + secret) ]
    let exporter = OpenTelemetry.otlpLogExporter url headers
    let provider = OpenTelemetry.loggerProvider (resourceFor sessionId) (OpenTelemetry.batchProcessor exporter)
    let logger = provider.getLogger serviceName
    { Emit = fun turnId usage -> try emitTo logger sessionId turnId usage with _ -> ()
      Shutdown = fun () -> provider.shutdown () }

/// The emitter configured from the Manager's spawn contract: `YESSION_OTLP_ENDPOINT` and
/// `YESSION_OTLP_SECRET`. An absent endpoint ⇒ `disabled` (telemetry off, pure no-op), so a
/// session launched without a telemetry-enabled Manager still runs normally.
let fromEnv (sessionId: SessionId) : Emitter =
    match Interop.envOr "YESSION_OTLP_ENDPOINT" "" with
    | "" -> disabled
    | endpoint -> create sessionId endpoint (Interop.envOr "YESSION_OTLP_SECRET" "")
