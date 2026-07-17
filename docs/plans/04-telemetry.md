# Plan 04 — OpenTelemetry: the Manager as collector, Session Processes emit

> **Status: in progress.** Steps 28–30 delivered; Steps 31–33 pending.
>
> Phase 5 · Observability. Addresses [GAPS.md](../GAPS.md) § Delivery & operations
> ("No telemetry, structured logging, or crash reporting; the Process logs to stdout")
> and the concrete blind spot found while reviewing the agent turn: `app/Agent.fs`
> received the Claude Agent SDK `result` message's `usage` block on every turn and
> **discarded it**, so the runtime had no visibility into token or cache spend. This plan
> makes the Manager an OpenTelemetry collector and the Session Processes emitters, and
> ships **agent-turn token/cache usage** as the first signal on the wire.

## Product behaviour

1. **The Manager is the telemetry sink.** It already owns the process boundary and a
   127.0.0.1 HTTP endpoint (control RPC + management UI). It gains an **OTLP/HTTP logs
   receiver** — the OpenTelemetry *collector* role — that accepts telemetry from its
   child Session Processes. Sessions never talk to an external backend directly; the
   Manager is the single egress point (later it can re-export or hand off to a real OTel
   Collector — see Non-goals).
2. **Each Session Process is an emitter.** It runs the OpenTelemetry SDK and exports OTLP
   over HTTP to the Manager, identified as its own OpenTelemetry *resource*
   (`service.name = yession-session`, `service.instance.id = <sessionId>`). A crashing or
   environment-less session still emits — telemetry does not depend on the environment
   grant.
3. **The first signal is agent-turn usage.** On every completed agent turn the session
   emits one **log record** carrying the four counts from the SDK `result.usage`:
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
  ├── OTLP logs receiver /v1/logs  ◀────────────────┤   └── OTel SDK LoggerProvider ─ POST /v1/logs ─┐
  └── collector: aggregate + log (+ later re-export)                                                │
        ▲──────────────────────────────────────────────────────────────────────────────────────────┘
        counts + ids only — no session content
```

## Why this shape (invariants it must respect)

- **Signal choice: OTel logs, not metrics (first cut).** Token counts map naturally to
  the GenAI `gen_ai.client.token.usage` *metric*, but metrics need a reader/aggregation
  pipeline on both ends. The logs signal gives one self-contained record per turn that
  the Manager can print and tally immediately — which is exactly "the first thing it
  logs are the counts." A metrics pipeline (histograms, Prometheus scrape) is a
  documented follow-up (Non-goals), reusing the same bindings and receiver plumbing.
- **Bindings, not authored JS.** The emitter uses the OpenTelemetry JavaScript SDK, but
  we consume it the way we consume Yjs and Dockerode: a new **`src/Fable.OpenTelemetry`**
  project holding F# bindings generated with `ts2fable` and hand-tweaked. The npm SDK is
  a dependency; the binding is generated F# — no authored JavaScript, so commit `f706ebf`
  ("Remove all authored JavaScript") stands. This replaces the earlier hand-rolled-codec
  sketch: the binding is the boundary on the emit side.
- **Types first at the receiver.** The Manager decodes only the narrow slice of OTLP/HTTP
  **JSON** logs it actually receives (`resourceLogs[].scopeLogs[].logRecords[]` with the
  attributes we emit) with a hand-written Thoth decode, the same discipline as
  `ControlWire`/`Serialization`. OTLP JSON (not protobuf) is chosen precisely to keep this
  decode small; a real OTel Collector could receive the identical payload.
- **The Session Process is the only writer of durable facts.** Telemetry is *not* a
  durable session fact — it is fire-and-forget observability. It must **not** become a
  `SessionEvent`, must **not** touch the event log or the Yjs doc, and a dropped export
  must never fail or stall a turn (the SDK's `BatchLogRecordProcessor` exports
  asynchronously off the turn path). The emitter lives in the host/app layer (`app/`),
  next to `EventStore` and `Control`; `Yession.SessionProcess` stays OTel-free and
  receives an injected sink (see Step 32), like it receives the `RunAgent` capability.
- **Verification is automated end-to-end.** Attribute-mapping and receiver-decode tests
  in the cheap `test` tier; a real cross-process test in the `verify` tier (a session
  runs a turn, the Manager receiver records the counts, asserted over the actual process
  boundary — including that no body text appears).
- **Capabilities are scoped, not ambient.** The Manager hands each launch the OTLP
  endpoint URL and a per-launch bearer secret via the environment contract, exactly as it
  does for control (`YESSION_CONTROL_URL`/`_SECRET`). Telemetry is enabled per Manager,
  independent of the environment `Grant`.

## The telemetry model (OpenTelemetry semantic conventions)

One **LogRecord** per completed turn, `severity = INFO`, `event.name = gen_ai.agent_turn`,
a short human `body` (e.g. `"agent turn usage"`), and the counts as attributes. Attribute
names follow the OTel **GenAI** conventions where they exist so the record is legible to
any OTLP logs backend.

- **Log attributes:**
  | Attribute | Value |
  |---|---|
  | `gen_ai.system` | `anthropic` |
  | `gen_ai.operation.name` | `agent_turn` |
  | `gen_ai.usage.input_tokens` | `int` |
  | `gen_ai.usage.output_tokens` | `int` |
  | `anthropic.usage.cache_read_input_tokens` | `int` (Anthropic extension) |
  | `anthropic.usage.cache_creation_input_tokens` | `int` (Anthropic extension) |
  | `gen_ai.response.model` | model id, when the SDK reports it |
  | `yession.session.id` | the session id (identifier, not content) |
  | `yession.agent.turn.id` | the `AgentTurnId` |
- **Resource attributes (session emitter):** `service.name=yession-session`,
  `service.namespace=yession`, `service.instance.id=<sessionId>`,
  `yession.session.id=<sessionId>`.
- **Resource attributes (Manager collector):** `service.name=yession-manager`.

The turn boundary — one completed turn → one log record — is the natural emission point;
it aligns with the existing lifecycle (`AgentTurnStarted → … → AgentMessageCompleted`).

## Interfaces & schemas introduced

1. **`Yession.Domain` — `AgentUsage` (delivered, Step 28).**
   `AgentUsage = { InputTokens; OutputTokens; CacheReadTokens; CacheCreationTokens; Model: string option }`;
   `AgentCompleted of body: string * usage: AgentUsage option` (default `None`, so
   scripted runners and Phase-1 behaviour are unaffected).

2. **`app/Agent.fs` — capture `result.usage` (delivered, Step 28).**
   The `runQuery` Emit block reads `m.usage.{input_tokens, output_tokens,
   cache_read_input_tokens, cache_creation_input_tokens}` and the model from
   `m.modelUsage`, surfaced through `RunOutcome` into `AgentUsage`.

3. **`src/Fable.OpenTelemetry` (new) — the SDK bindings.**
   `ts2fable`-generated, hand-tweaked F# bindings for the minimal surface we use:
   `@opentelemetry/api-logs` (`LoggerProvider`, `Logger`, `LogRecord`, `SeverityNumber`),
   `@opentelemetry/sdk-logs` (`LoggerProvider`, `BatchLogRecordProcessor`), the OTLP logs
   HTTP exporter (`@opentelemetry/exporter-logs-otlp-http`, JSON encoding), and
   `@opentelemetry/resources` (`resourceFromAttributes`). Pinned in `package.json` /
   `Directory.Packages.props` per the repo's central-pinning rule. Same project shape as
   `src/Fable.Dockerode`.

4. **`app/Telemetry.fs` (new) — the session emitter.**
   - Builds a `LoggerProvider` with the session `Resource`, a `BatchLogRecordProcessor`
     wrapping an OTLP logs HTTP exporter aimed at `YESSION_OTLP_ENDPOINT/v1/logs` with the
     bearer header.
   - `record : AgentTurnId -> AgentUsage -> unit` emits one LogRecord with the attributes
     above; async export means it never blocks or throws into the turn.
   - No-op sink when `YESSION_OTLP_ENDPOINT` is unset (telemetry disabled).

5. **`app/TelemetryReceiver.fs` (new) — the Manager collector.**
   - `tryHandle`: a `POST /v1/logs` route composed into the Manager's shared server
     exactly like `Control.tryHandle` (falls through on other paths). Authenticates the
     per-launch bearer, decodes the OTLP/HTTP JSON logs subset (`LogsWire.decode`), feeds a
     `Collector`.
   - `LogsWire`: hand-written Thoth **decode** for the narrow OTLP logs JSON we emit
     (resource attrs → scope logs → log records → attributes). Decode-only; the SDK owns
     the encode side.
   - `Collector`: aggregates per-session running totals and logs each received turn's
     counts to stdout (first-signal behaviour). A documented seam (`onRecord`) is where
     downstream re-export / a real Collector hand-off is added later.

6. **Environment contract additions (`ProcessManager.launch` + `SessionMain` + `AgentTurn.run`).**
   - `ProcessManager.Options` gains telemetry enablement; the Manager starts its shared
     server (and the receiver) whenever telemetry is on, independent of
     `Grant`/`ui`/`ManagerPort`.
   - On launch the Manager injects `YESSION_OTLP_ENDPOINT` (its own
     `http://127.0.0.1:<port>`) and `YESSION_OTLP_SECRET` (a per-launch bearer, minted and
     revoked on exit like the control secret).
   - `AgentTurn.run` gains an injected `emitUsage : AgentTurnId -> AgentUsage -> unit`
     sink (default `ignore`, keeping `Yession.SessionProcess` OTel-free); `SessionMain`
     reads the env, builds the `Telemetry` emitter, and passes its `record` in.

## Test harness (reuse what exists — no Playwright)

A Manager↔Session e2e harness already exists; the telemetry e2e reuses it. **Playwright is
not involved** — it drives only the browser *client* E2E (`scripts/browser-e2e.fsx`); the
session/manager e2es are headless Node, run by Pyxpecto. Two levels:

1. **In-process host (cheap tier, no native addons).** `Host.startFull` + `connectInMemoryClient`
   (`tests/Yession.Tests/Support.fs`) drive a *real* session — event log, projection,
   scheduler, agent turn — over an in-memory channel pair. Full turn lifecycle
   (message → queue → drain → turn → events) with no WebRTC, no spawning, no `node-datachannel`.
   HTTP-on-localhost is cheap-tier-legal (see the un-tagged `EventsHttp.tests`). **This is
   where the first telemetry e2e lives.**
2. **Cross-process (verify tier).** `ProcessManager` spawns real `app/SessionMain.js`
   children, driven over HTTP + real WebRTC clients (`connectClient`), plus the
   shipped-bundle composition e2e (`spawnBundle`) — see `tests/Yession.Tests/Phase4.fs`.
   Needs `node-datachannel`; it is the release gate.

Two small, reusable additions:

- **In-test OTLP logs receiver** — a localhost `POST /v1/logs` server that decodes the OTLP
  JSON and records the LogRecords to a list, exposing `.Received`. Mirrors Phase4's
  `startControlServer`. This **is** the Step 31 collector, reused as the test double — one
  implementation, both roles.
- **`usage-probe` built-in agent** — a `YESSION_AGENT=usage-probe` runner in `SessionMain`
  (alongside `diagnostic`) that returns `AgentCompleted ("probe", Some <fixed usage>)` with
  no Docker and no credentials, so the cross-process e2e can assert real non-zero counts on
  the release gate.

**The e2e that would have caught the Step 29 bug.** The silent no-op (wrong exporter ctor
arg → zero records) survives every "looks-wired" check — processor registered, logger
enabled — but an e2e that asserts *a record actually arrives at the receiver* turns it into
a red build. Concretely, the cheap-tier first e2e: `Host.startFull` with `Telemetry.record`
pointed at the in-test receiver → `connectInMemoryClient` sends a message → a scripted agent
returns usage → assert **exactly one** record at the receiver, tagged with the session +
turn id, counts intact, and **no body text**. The verify-tier variant runs the same
assertion over the real spawn + env-injection path (`usage-probe` agent).

## Delivery steps

| Step | Title | Interfaces / deliverable | Automated verification |
|---|---|---|---|
| 28 ✅ | **Capture usage from the SDK** | `AgentUsage` in Domain; `AgentCompleted` carries optional usage; `app/Agent.fs` reads `result.usage` + model | Delivered `6f220a5`: `dotnet build` (0 errors) + `dotnet fable` of tests both green; live-agent smoke (verify tier) confirms real values |
| 29 ✅ | **`Fable.OpenTelemetry` bindings** | `src/Fable.OpenTelemetry` — hand-trimmed bindings for the logs SDK (`api-logs`/`sdk-logs`) + OTLP HTTP exporter + `resources`; deps pinned in `package.json`; `Telemetry.fs` smoke (in-memory exporter) in the cheap tier | Delivered: `dotnet build` + `dotnet fable` green; the compiled binding drives the real `@opentelemetry/sdk-logs` — one emit → one record with attributes preserved. **Gotcha found & encoded:** `Simple`/`BatchLogRecordProcessor` take `{ exporter }` (SDK 2.x), not the bare exporter — the bare form silently no-ops |
| 30 ✅ | **Session emitter** | `app/Telemetry.fs` — `Emitter { Emit; Shutdown }`: `LoggerProvider`/`Resource` + batch OTLP exporter; `Emit : AgentTurnId -> AgentUsage -> unit` is the injectable sink (for `Host.startFull`/`AgentTurn.run` in Step 32); `fromEnv` returns `disabled` when `YESSION_OTLP_ENDPOINT` is unset; `Emit` swallows all throws | Delivered: cheap-tier suite (5/5) run through Pyxpecto against the real SDK — `emitTo` maps every attribute (incl. model present/absent); `disabled` + dead-endpoint `create` never throw. Suite is import-clean (no WebRTC), so it runs standalone and in the PR gate |
| 31 | **Manager receiver + collector (reusable)** | `app/TelemetryReceiver.fs` `tryHandle` composed into the shared server; `LogsWire.decode`; `Collector` aggregates + logs; per-launch bearer auth. **The same receiver, with a `.Received` list, is the in-test double** (Support helper) | `test` tier: `LogsWire.decode` of a real exporter payload → expected counts; route returns 401 on bad secret, 200 + record on good; collector running-total assertion |
| 32 | **First e2e (cheap) + env wiring** | Wire the emit sink through `Host.startFull` → `AgentTurn.run`. **Cheap-tier e2e:** in-process host + `Telemetry.record` → in-test receiver, `connectInMemoryClient` sends a message, scripted agent returns usage, assert one record (session+turn id, counts, no body). Plus `ProcessManager` injects `YESSION_OTLP_ENDPOINT`/`_SECRET` (bearer minted/revoked on launch/exit); receiver starts when telemetry enabled; `SessionMain` builds + injects the emitter | `test` tier: the cheap e2e above — **this is the regression guard for the Step 29 bug class** (silent no-op → zero records → red) |
| 32b | **Cross-process e2e (verify)** | `usage-probe` built-in agent in `SessionMain`; ProcessManager spawns a real child with telemetry on | **verify** tier: launch a real session (`usage-probe`), drive one turn via `connectClient`, assert the Manager collector recorded the four counts tagged with session + turn id and **no body text** — over the real spawn + env-injection path |
| 33 | **Docs & GAPS update** | README observability note; flip the GAPS "no telemetry" line to "agent-turn token/cache usage over OTLP logs; Manager is the collector"; `mise` task if one helps run the receiver standalone | n/a |

## Automated verification (tiers)

- **`test` (cheap, PR gate):** bindings smoke (in-memory exporter); emitter
  attribute-mapping + failure isolation; `LogsWire.decode` + receiver auth + collector
  aggregation; **and the first telemetry e2e** — in-process host + `connectInMemoryClient`
  drive a real turn, `Telemetry.record` posts to the in-test receiver over localhost HTTP,
  asserting one record with the session + turn id and counts, no body. WebRTC-free, so it
  runs on every PR. This is the regression guard for the Step 29 silent-no-op class.
- **`verify` (release gate):** the cross-process e2e — `ProcessManager` spawns a real
  `SessionMain` child with telemetry on and the `usage-probe` agent; a `connectClient` turn
  drives the full spawn + env-injection + OTLP-over-the-wire path, asserting the Manager
  collector recorded the counts tagged with session + turn id and **no session content**.
  The credential-gated live path additionally confirms real `result.usage` values are
  non-zero.

## Non-goals (later)

- **Metrics pipeline.** A follow-up can add the `gen_ai.client.token.usage` histogram via
  the OTel metrics SDK (same bindings), for Prometheus scrape / dashboards. Logs ship
  first.
- **Downstream re-export / real Collector hand-off.** The Manager collector logs and
  aggregates in-process first. Forwarding to an OTLP backend or piping to a real OTel
  Collector binary sits behind the `Collector.onRecord` seam.
- **Traces.** Spans for the turn lifecycle (`AgentTurnStarted`→`AgentMessageCompleted`)
  are a natural extension once the SDK bindings exist, but are out of scope here.
- **Command / environment / transport signals.** The plumbing is generic; only agent-turn
  usage is wired now.
- **Persistence of telemetry.** Counts live in memory in the collector; the container is
  ephemeral. Durable telemetry belongs with downstream re-export, not here.

## Open decisions

1. **Exporter — DECIDED: OpenTelemetry JS SDK via `src/Fable.OpenTelemetry` (ts2fable +
   hand-tweak), logs signal, OTLP/HTTP JSON.** Reflected throughout above.
2. **Signal — DECIDED: logs first, metrics later.** Per-turn LogRecord now; the GenAI
   token-usage histogram is a documented follow-up (Non-goals).
3. **Collector scope now: log-only (recommended) vs immediate downstream re-export.** The
   plan ships log-only with an `onRecord` seam; if an OTLP backend or a real OTel Collector
   is already available, Step 31 could target it directly.
4. **OTLP encoding on the wire: JSON (chosen) vs protobuf.** JSON keeps the Manager decode
   small and dependency-free; the SDK exporter supports both. Revisit only if a downstream
   backend needs protobuf.
