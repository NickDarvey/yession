# Plan — Docker backend integration tests in the verify gate

Closes the GAPS.md line: *"The Docker backend is shipped but only smoke-verified where a
daemon exists… Mounts, build specs, secret refs, and env-var refs in `EnvironmentSpec` are
typed but not yet interpreted by any backend."*

- Intent: [../../README.md](../../README.md) · Invariants: [../design.md](../design.md)
- Gaps this addresses: [../GAPS.md](../GAPS.md) §"Security & trust" (Docker), §"Testing debt"
- Capability seam: `src/Yession.Manager/Authority.fs` (`ContainerBackend`) ·
  Adapter: `app/Backends.fs` (`DockerBackend`) · Domain vocabulary:
  `src/Yession.Domain/Environment.fs`

## Problem

Two distinct gaps hide behind "smoke-verified":

1. **The smoke never gates.** `tests/Yession.Tests/Phase2.fs:629` branches on
   `DockerBackend.daemonAvailable ()` and, when `false`, returns `()` — a *pass*, not a
   reported skip. So in any run without a daemon (the dev container) the release gate is
   green having exercised zero Docker. And where a daemon *does* exist (the `verify` job's
   `ubuntu-latest` runner has one), the only assertion is `echo hello-from-docker`.
2. **The backend interprets one spec field.** `DockerBackend.create` reads `spec.Image`
   and ignores `Build`, `Mounts`, `EnvironmentVariables`, and `WorkingDirectory`. The
   domain types exist and round-trip through events, but nothing turns them into
   `docker run`/`docker exec` flags. Untested because uninterpreted.

The `verify` job (`.github/workflows/release.yml:30`) is the right home: it already runs on
`ubuntu-latest` (Docker daemon preinstalled and started), already carries a real-browser
E2E and live-agent credentials, and is the release gate.

## Approach

Three tiers, sequenced. Tier 1 makes the *existing* engine a real, non-skippable gate.
Tier 2 adds behavioural coverage of the current engine. Tier 3 is implement-then-test for
each uninterpreted spec field — each field is red until its backend interpretation lands.

Everything new is wrapped in `Tag.verify` (needs a daemon → verify tier only), consistent
with `tests/Yession.Tests/Tags.fs`.

### Tier 1 — Make the daemon a hard requirement in the release gate

Goal: in the `verify` job a missing/broken daemon is a **failure**, not a silent pass;
everywhere else it stays a reported skip. Skips are reported, never hidden — matching the
`Tag.verify` contract.

- **Add an opt-in "require" switch.** New env var `YESSION_REQUIRE_DOCKER=1`. A small test
  helper `Docker.gate` (in `Support.fs`) resolves to one of:
  - daemon present → run the test body;
  - daemon absent + `YESSION_REQUIRE_DOCKER` set → **fail** (`Expect` failure: "verify gate
    requires a Docker daemon; none reachable");
  - daemon absent + not required → emit one visible skipped case (reuse the `Tag.verify`
    skip shape), never a bare `()`.
- **Set the switch in the workflow.** In `.github/workflows/release.yml`, the `verify`
  job's "Full verification gate" step gains `env: YESSION_REQUIRE_DOCKER: "1"`, plus a
  cheap pre-step `docker version` (fail fast with a legible message if the runner ever
  ships without one). The PR-only `test` job stays daemon-free.
- **Kill the silent no-op.** Rewrite `Phase2.fs:629` to go through `Docker.gate` so its
  false-branch is a reported skip, not a pass.

Exit: on `ubuntu-latest` the Docker suite runs for real; if the daemon ever disappears the
gate goes red instead of green.

### Tier 2 — Real integration coverage of the current engine

New suite `tests/Yession.Tests/DockerIntegration.fs` (tagged verify, added to `Main.fs`),
driving the real adapter through the scoped capability (`Authority.grant registry
(DockerBackend.create ()) sessionId`) so the authority layer is exercised end-to-end, not
bypassed. Each test starts from a known image (`alpine:3`, already the adapter default) and
cleans up its own containers in a `try/finally`.

Cases:

- **Lifecycle + streaming**: start → `exec` a command that writes stdout → assert
  `CommandSucceeded 0` and the streamed chunk text → stop → assert the container is gone
  (`docker ps -a --filter` by the `yession-session` label returns nothing).
- **Non-zero exit** maps to `CommandFailed <code>` (e.g. `sh -c "exit 7"`).
- **stderr routing**: a command writing to stderr yields chunks with `Stream = Stderr`.
- **Command timeout** maps to `CommandTimedOut` — needs the adapter to honour
  `request.Timeout` (see note below); until then this case is Tier 3.
- **Session-scoped isolation**: two sessions each get a container; the label filter proves
  a handle from session A cannot address session B's container (the authority guarantee,
  now over the real engine rather than the fake).
- **Cleanup safety net**: a suite-level teardown removes any leftover
  `--filter label=yession-session` containers so a crashed test can't leak into the runner.

Note: the adapter's `Execute` currently passes `0.0` timeout to `docker` and does not thread
`request.Timeout`/`Environment`/`WorkingDirectory` into `docker exec`. Wiring `-w`, `-e`,
and a timeout is small and belongs with the cases that assert them (Tier 3).

### Tier 3 — Interpret the typed spec fields, each with its integration test

For every field, one commit: extend `DockerBackend` to translate the field into
`docker run`/`docker exec` flags, then a verify-tagged test that asserts the observable
effect inside the container. Ordered by cost/value.

1. **Env-var refs (`PlainValue`)** — `spec.EnvironmentVariables` and `request.Environment`
   become `-e KEY=VALUE` on `run`/`exec`. Test: `exec` `printenv KEY` returns the value.
2. **Working directory** — `spec.WorkingDirectory`/`request.WorkingDirectory` become `-w`.
   Test: `exec` `pwd` returns the set path.
3. **Command timeout** — thread `request.Timeout` into the spawn timeout. Test: a
   `sleep`-longer-than-timeout command returns `CommandTimedOut` (pairs with Tier 2).
4. **Mounts** — `ContainerMount` → `--mount`/`-v` on `run`, covering:
   - `HostPath` RO and RW (write from a temp host dir, read inside; RW round-trips, RO
     write fails);
   - `NamedVolume` (persists across a stop/start of a fresh container on the same volume);
   - `SessionWorkspace` (a per-session named volume or host dir the Manager owns — define
     the mapping here; test that two commands in one session share it).
5. **Build spec (`ContainerBuildSpec`)** — when `spec.Build` is set, `docker build` a tiny
   fixture context (checked into `tests/fixtures/docker/`) and run the resulting image.
   Test: the built image runs a command proving the Dockerfile's `RUN` took effect.
6. **Secret refs (`SecretRef`)** — smallest viable secret store (env-injected map keyed by
   `SecretName`, documented as local-dev only, matching GAPS.md's secret note), resolved to
   `-e KEY=<secret>` at `run`. Test: the referenced secret reaches the container's env; an
   unresolved `SecretRef` fails `start` with a legible reason. (Larger design — a real
   store — stays out of scope; this closes the "typed but uninterpreted" gap only.)

Each Tier 3 test lands **with** its implementation in the same commit, so `verify` stays
green step to step (no long-lived red).

## Workflow changes (`.github/workflows/release.yml`)

- `verify` job: add a `docker version` step (fail-fast) before the gate; add
  `YESSION_REQUIRE_DOCKER: "1"` to the gate step's `env`; add an `if: always()` cleanup
  step removing `label=yession-session` containers so runner state never leaks between
  jobs.
- `test` (PR) job: unchanged — stays cheap and daemon-free; the Docker suite reports its
  single skip there.
- `mise.toml`: no task changes required — the suite rides the existing `verify` task via
  the `Tag.verify`/`YESSION_TEST_TIER=verify` path. Optionally add a `mise run
  test-docker` convenience task (verify tier + `YESSION_REQUIRE_DOCKER=1`) for local runs
  against a developer daemon.

## Files touched

- `app/Backends.fs` — interpret `EnvironmentVariables`, `WorkingDirectory`, `Timeout`,
  `Mounts`, `Build`, `SecretRef` in `DockerBackend` (Tier 3).
- `tests/Yession.Tests/Support.fs` — `Docker.gate` helper (Tier 1).
- `tests/Yession.Tests/DockerIntegration.fs` — new suite (Tiers 2–3); registered in
  `tests/Yession.Tests/Main.fs`.
- `tests/Yession.Tests/Phase2.fs` — route the existing smoke through `Docker.gate`.
- `tests/fixtures/docker/` — minimal Dockerfile context for the build-spec test.
- `.github/workflows/release.yml` — daemon fail-fast, `YESSION_REQUIRE_DOCKER`, cleanup.
- Optional: a secret-store module (Tier 3.6) — smallest thing that resolves `SecretName`.

## Risks / decisions to confirm

- **Runner daemon assumption**: GitHub-hosted `ubuntu-latest` ships a running daemon;
  self-hosted/`act` runners may not. The `YESSION_REQUIRE_DOCKER` switch is scoped to the
  hosted `verify` job precisely so other environments degrade to a reported skip.
- **Image pulls add wall-clock and network** to `verify`. Mitigate by standardising on the
  small `alpine:3` already used; the build-spec fixture derives from it.
- **`SessionWorkspace` semantics are undefined today** — this plan is where that mapping
  gets pinned (per-session named volume vs. Manager-owned host dir). Flagging for a
  decision before Tier 3.4.
- **Secret store scope**: Tier 3.6 delivers only enough to interpret `SecretRef` for
  local-dev; a real secret store remains a separate, later gap.

## Verification

- Tier 1 done: `verify` job runs the Docker suite for real; a deliberately broken daemon
  turns the gate red; the PR `test` job shows one reported skip.
- Tiers 2–3 done: each field has a green verify-tagged test asserting its in-container
  effect; the GAPS.md Docker line is narrowed to only what's explicitly out of scope
  (real secret store, non-Linux daemons).
