# Plan 04 — OpenTelemetry: the Manager as collector, Session Processes emit

> **Status: proposed.** Not yet delivered.
>
> Phase 5 · Observability. Addresses [GAPS.md](../GAPS.md) § Delivery & operations
> ("No telemetry, structured logging, or crash reporting; the Process logs to stdout")
> and the concrete blind spot found while reviewing the agent turn: `app/Agent.fs`
> receives the Claude Agent SDK `result` message's `usage` block on every turn and
> **discards it**, so the runtime has no visibility into token or cache spend. This plan
> makes the Manager an OpenTelemetry collector and the Session Processes emitters, and
> ships **agent-turn token/cache usage** as the first signal on the wire.

## Product behaviour

1. **The Manager is the telemetry sink.** It already owns the process boundary and a
   127.0.0.1 HTTP endpoint (control RPC + management UI). It gains an **OTLP/HTTP
   receiver** — the OpenTelemetry *collector* role — that accepts telemetry from its
   child Session Processes. Sessions never talk to an external backend directly; the
   Manager is the single egress point (later it can re-export to Prometheus/an OTLP
   backend — see Non-goals).
2. **Each Session Process is an emitter.** It exports OTLP over HTTP to the Manager,
   identified as its own OpenTelemetry *resource* (`service.name = yession-session`,
   `service.instance.id = <sessionId>`). A crashing or environment-less session still
   emits — telemetry does not depend on the environment grant.
3. **The first signal is agent-turn usage.** On every completed agent turn the session
   records the four token counts from the SDK `result.usage`:
   `input_tokens`, `output_tokens`, `cache_read_input_tokens`,
   `cache_creation_input_tokens`. The Manager logs them (to stdout, matching today's
   logging story) and holds a per-session running total, sliceable by session and turn.
4. **No session content ever crosses the wire.** Telemetry carries counts and
   identifiers (session id, agent-turn id, model id) only — never message bodies,
   prompts, or completions. This is the same non-negotiable the control channel already
   enforces (`app/Control.fs`: "NO session content crosses this channel").

## Topology & process contract

```
yession (Manager binary)                         yession-session (Session Process binary)
  ├── management UI     http://127.0.0.1:8321       ├── event log + doc store  <data>/sessions/<id>/
  ├── control endpoint  /control/*                  ├── agent turn (Scheduler ▶ AgentTurn.run ▶ Agent.run)
  ├── OTLP receiver      /v1/metrics  ◀─────────────┤   └── OTLP exporter ── POST /v1/metrics ─┐
  └── collector: aggregate + log (+ later re-export)                                          │
        ▲──────────────────────────────────────────────────────────────────────────────────┘
        counts + ids only — no session content
```

## Why this shape (invariants it must respect)

- **Types first, hand-written codecs at every boundary.** The OTLP payload gets a
  hand-written Thoth codec over a small, typed subset of the OTLP/HTTP JSON schema —
  the same discipline as `ControlWire`/`Serialization`. We do **not** pull in the
  OpenTelemetry JavaScript SDK (see Open decisions): it would reintroduce authored-JS
  and a large npm dependency surface that commit `f706ebf` ("Remove all authored
  JavaScript") deliberately shed. A ~200-line OTLP/HTTP JSON exporter keeps us
  standards-compliant (a real OTel Collector could receive the identical payload) with
  zero JS SDK.
- **The Session Process is the only writer of durable facts.** Telemetry is *not* a
  durable session fact — it is fire-and-forget observability. It must **not** become a
  `SessionEvent`, must **not** touch the event log or the Yjs doc, and a dropped export
  must never fail or stall a turn. The exporter lives in the host/app layer
  (`app/`), next to `EventStore` and `Control`, not in `Yession.Domain`.
- **Verification is automated end-to-end.** A pure codec round-trip test in the cheap
  `test` tier; a real cross-process test in the `verify` tier (a session runs a turn,
  the Manager receiver records the measurement, asserted over the actual process
  boundary).
- **Capabilities are scoped, not ambient.** The Manager hands each launch the OTLP
  endpoint URL and a per-launch bearer secret via the environment contract, exactly as
  it does for control (`YESSION_CONTROL_URL`/`_SECRET`). Telemetry is enabled per
  Manager, independent of the environment `Grant`.

## The telemetry model (OpenTelemetry semantic conventions)

Follow the OTel **GenAI** semantic conventions so the signal is legible to any OTLP
backend without yession-specific dashboards.

- **Metric:** `gen_ai.client.token.usage` (histogram, unit `{token}`), one measurement
  per turn per token type.
- **Attributes:**
  | Attribute | Value |
  |---|---|
  | `gen_ai.system` | `anthropic` |
  | `gen_ai.operation.name` | `agent_turn` |
  | `gen_ai.token.type` | `input` \| `output` \| `cache_read` \| `cache_creation` |
  | `yession.session.id` | the session id (identifier, not content) |
  | `yession.agent.turn.id` | the `AgentTurnId` |
  | `gen_ai.response.model` | model id, when the SDK reports it |

  `input`/`output` are standard; `cache_read`/`cache_creation` are an Anthropic
  extension of `gen_ai.token.type` (documented in the code where they're defined).
- **Resource attributes (session emitter):** `service.name=yession-session`,
  `service.namespace=yession`, `service.instance.id=<sessionId>`,
  `yession.session.id=<sessionId>`.
- **Resource attributes (Manager collector):** `service.name=yession-manager`.

The turn boundary — one completed turn → one export of four measurements — is the
natural emission point; it aligns with the existing lifecycle
(`AgentTurnStarted → … → AgentMessageCompleted`).

## Interfaces & schemas introduced

1. **`Yession.Domain` — surface usage from the runner (small, pure).**
   The runner capability must be able to report usage so the real SDK adapter and the
   scripted test runner stay interchangeable (`Domain/Agent.fs` contract).
   - New record `AgentUsage = { InputTokens; OutputTokens; CacheReadTokens; CacheCreationTokens; Model: string option }`.
   - `AgentCompleted` carries an optional `AgentUsage` (default `None`, so scripted
     runners and Phase-1 behaviour are unaffected):
     `AgentCompleted of body: string * usage: AgentUsage option`.

2. **`app/Agent.fs` — stop discarding `result.usage`.**
   In the `runQuery` Emit block, on `m.type === 'result'` read
   `m.usage.{input_tokens, output_tokens, cache_read_input_tokens, cache_creation_input_tokens}`
   and `m.model`, thread them through the `RunOutcome` interface (add the fields
   alongside `ok`/`body`/`reason`), and populate `AgentUsage` in `run`. This is
   **item 1** — the first thing that flows.

3. **`app/Telemetry.fs` (new) — the OTLP/HTTP JSON exporter (session side).**
   - `MetricsWire`: hand-written Thoth codec for the minimal OTLP
     `ExportMetricsServiceRequest` JSON (resource → scope → metric → histogram
     data point → attributes). Encode only; the Manager decodes the same shape.
   - `Exporter`: `record : AgentTurnId -> AgentUsage -> unit` that batches and
     `POST`s `application/json` to `YESSION_OTLP_ENDPOINT/v1/metrics` with the
     bearer secret; failures are swallowed and counted, never surfaced to the turn.
     No-op when `YESSION_OTLP_ENDPOINT` is unset (telemetry disabled).

4. **`app/TelemetryReceiver.fs` (new) — the OTLP receiver (Manager side).**
   - `tryHandle`: a route handler for `POST /v1/metrics`, composed into the Manager's
     existing shared server exactly like `Control.tryHandle` (falls through on
     non-telemetry paths). Authenticates the per-launch bearer, decodes with
     `MetricsWire`, feeds a `Collector`.
   - `Collector`: aggregates per-session running totals and logs each received turn's
     counts to stdout (first-signal behaviour). A documented seam (`onMeasurement`) is
     where downstream re-export is added later.

5. **Environment contract additions (`ProcessManager.launch` + `SessionMain`).**
   - `ProcessManager.Options` gains a telemetry flag / receiver enablement; the Manager
     starts its shared server (and the receiver) whenever telemetry is on, independent
     of `Grant`/`ui`/`ManagerPort`.
   - On launch the Manager injects `YESSION_OTLP_ENDPOINT` (its own
     `http://127.0.0.1:<port>`) and `YESSION_OTLP_SECRET` (a per-launch bearer, minted
     and revoked on exit like the control secret).
   - `SessionMain` reads them (`Interop.envOr`), constructs the `Exporter`, and wires
     `record` into the turn-completion path.

## Delivery steps

| Step | Title | Interfaces / deliverable | Automated verification |
|---|---|---|---|
| 28 | **Capture usage from the SDK** | `AgentUsage` in Domain; `AgentCompleted` carries optional usage; `app/Agent.fs` reads `result.usage` + `model` and stops discarding it | Adapter shape covered by the live agent smoke (verify tier, credential-gated); Domain compiles with the widened case; scripted runners default `None` |
| 29 | **OTLP/HTTP JSON codec** | `MetricsWire` encode for `ExportMetricsServiceRequest` (resource/scope/metric/histogram/attributes) | Pure round-trip unit test (`test` tier): encode a known turn → decode → equal; snapshot one payload and assert it parses as valid OTLP JSON |
| 30 | **Session exporter** | `app/Telemetry.fs` `Exporter.record`; no-op when unset; failure isolation | Unit test that `record` never throws on a dead endpoint; a fake HTTP sink receives the expected `/v1/metrics` body |
| 31 | **Manager receiver + collector** | `app/TelemetryReceiver.fs` `tryHandle` composed into the shared server; `Collector` aggregates + logs; per-launch bearer auth | Route unit test (401 on bad secret, 200 + measurement on good); collector running-total assertion |
| 32 | **Env contract + wiring** | `ProcessManager` injects `YESSION_OTLP_ENDPOINT`/`_SECRET`, mints/revokes the bearer on launch/exit; receiver starts when telemetry enabled; `SessionMain` wires `record` into turn completion | Cross-process **verify**-tier test: launch a session with telemetry on, drive one turn (diagnostic or scripted agent), assert the Manager collector recorded four measurements tagged with the session + turn id and **no body text anywhere in the payload** |
| 33 | **Docs & GAPS update** | README observability note; flip the GAPS "no telemetry" line to "token/cache usage over OTLP; Manager is the collector"; `mise` task if one helps run the receiver standalone | n/a |

## Automated verification (tiers)

- **`test` (cheap, PR gate):** `MetricsWire` codec round-trip; exporter failure
  isolation; receiver auth + collector aggregation — all pure/in-process, no ports,
  no credentials.
- **`verify` (release gate):** the Step 32 end-to-end — a real Session Process emits
  over a real socket to a real Manager receiver across the process boundary, with an
  explicit assertion that the payload contains the counts and ids but **no session
  content**. Reuses the diagnostic agent (`YESSION_AGENT=diagnostic`) so it needs no
  model credentials; the credential-gated live path additionally confirms real
  `result.usage` values are non-zero.

## Non-goals (later)

- **Downstream re-export.** The Manager collector logs and aggregates in-process first.
  Forwarding to Prometheus (scrape endpoint) or an OTLP backend is a follow-up behind
  the `Collector.onMeasurement` seam.
- **Traces and logs.** This plan ships one **metric**. Spans for the turn lifecycle
  (`AgentTurnStarted`→`AgentMessageCompleted`) and structured logs are a natural
  extension once the exporter/receiver plumbing exists, but are out of scope here.
- **Command / environment / transport metrics.** The plumbing is generic; only
  agent-turn usage is wired now.
- **Persistence of telemetry.** Counts live in memory in the collector; the container
  is ephemeral. Durable metrics belong with downstream re-export, not here.

## Open decisions

1. **Exporter implementation — DECIDED: the OpenTelemetry JS SDK via generated F#
   bindings.** Generate bindings with `ts2fable` and hand-tweak them, following the
   existing `Fable.Dockerode` / `Fable.Yjs` pattern (a `src/Fable.OpenTelemetry`
   project). Prefer the OTel **logs** signal (the `@opentelemetry/api-logs` /
   `sdk-logs` beta) so the counts emit as structured log records — the Manager runs the
   OTLP **logs** receiver. This supersedes the earlier hand-rolled-codec sketch: no
   hand-written OTLP codec; the binding is the boundary. `MetricsWire`/`Telemetry.fs`
   in the steps below become the binding project plus thin F# wiring over it.
2. **Collector scope now: log-only (recommended) vs immediate downstream re-export.**
   The plan ships log-only with a re-export seam; if a Prometheus/OTLP backend already
   exists, Step 31 could target it directly.
3. **Signal shape: histogram per GenAI semconv (recommended) vs plain counters.**
   Histogram is standards-aligned and lets the collector derive sums; counters are
   simpler if only running totals are ever wanted.
