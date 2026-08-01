# Docker backend integration tests in the verify gate (as built)

Closes the GAPS.md line: *"The Docker backend is shipped but only smoke-verified where a
daemon exists… Mounts, build specs, secret refs, and env-var refs in `EnvironmentSpec` are
typed but not yet interpreted by any backend."*

- Intent: [../../README.md](../../README.md) · Invariants: [../design.md](../design.md)
- Capability seam: `src/Yession.Manager/Authority.fs` (`ContainerBackend`) ·
  Adapter: `app/Backends.fs` (`DockerBackend`) · Bindings: `src/Fable.Dockerode/` ·
  Domain vocabulary: `src/Yession.Domain/Environment.fs`

## What was wrong

1. **The smoke never gated.** `Phase2.fs` returned `()` when no daemon — a *pass*, not a
   reported skip — and its only assertion when it ran was `echo hello-from-docker`.
2. **The backend interpreted one field.** `DockerBackend` read `spec.Image` and dropped
   `Build`, `Mounts`, `EnvironmentVariables`, `WorkingDirectory`, and `request.Timeout`.
3. **Shelling out.** The adapter spawned the `docker` CLI rather than using an SDK.

## What shipped

### Docker via an SDK, not the CLI
`src/Fable.Dockerode/` is a standalone Fable bindings project wrapping the **`dockerode`**
npm package (Docker ships no official Node SDK; only Go and Python are official). It mirrors
how `Fable.Yjs` wraps `yjs`: bindings are their own layer, referenced by
`app/Yession.Host.fsproj`. `DockerBackend` was rewritten onto it — `ping`, `createVolume`,
`createContainer`/`start`, `exec` + `demuxStream`, `remove`, `pull`, `buildImage`. dockerode
is kept external in the esbuild bundle and added to the package manifest (`scripts/build.fsx`),
so `npm i -g` pulls it.

### Session ids are Docker-safe by construction
`SessionId.mint ()` (`src/Yession.Domain/Identity.fs`) generates a 128-bit id in Crockford
base32 (26 chars) — already a legal Docker object name, so the **container and its named
workspace volume are named by the id verbatim**, no sanitization. `SessionId.create` is
tightened to the Docker-safe charset, making "a session id is always a valid Docker name" an
enforced invariant. Ids are minted server-side in the management UI (`app/ManagerUi.fs`); the
fixed CLI session keeps its `YESSION_SESSION` id so a restart resumes the same volume. No
migration (pre-release).

### The named workspace volume
Every Docker session gets a named volume (= the session id) mounted at the working directory
(or `/workspace`), labelled `yession-session=<id>`. It **persists across container restarts**
by design; a crash-leftover container of the same name is force-removed before re-create.

### The gate is now hard in CI
> Superseded: `Support.Docker.gate` is gone. The daemon gate is now the `Docker` capability
> itself — `check` probes for a daemon and drops the cap when there is none, so the suites
> report a Pyxpecto `ignored` skip through `Tag.needs [Docker]`. `YESSION_REQUIRE_DOCKER`
> still keeps the cap (and so the hard failure) on the release gate. Original text below.

`Support.Docker.gate` replaces the silent no-op: daemon present → run; absent under
`YESSION_REQUIRE_DOCKER` (set on the `verify` job) → **fail**; absent otherwise → reported
skip. The suite sits under `Tag.verify`, so the cheap PR tier never reaches it. The `verify`
job gained a `docker version` fail-fast and an `always()` cleanup of labelled containers +
volumes. It runs on the VM host (no `container:` key), so dockerode reaches the daemon over
the socket directly — **no docker-in-docker**; the workflow comments the socket-mount recipe
should the job ever be containerized.

### Coverage — `tests/Yession.Tests/DockerIntegration.fs`
Driven through the scoped capability (`Authority.grant`). Tier 2: lifecycle + stdout stream +
removed-by-label, non-zero exit → `CommandFailed`, stderr routing, per-session labelling.
Tier 3: env-var refs (`PlainValue`, plus per-command env), working dir (spec + override),
command timeout → `CommandTimedOut`, the workspace named volume persisting across a restart,
`HostPath` mounts RW vs RO, build spec (`tests/fixtures/docker/Dockerfile`), and secret refs
(resolved from a process-env store; an unresolved ref fails `Start` legibly).

## Files
- `src/Fable.Dockerode/Fable.Dockerode.fsproj`, `Dockerode.fs` — new bindings.
- `src/Yession.Domain/Identity.fs` — `SessionId.mint`, `Base32Crockford`, tightened `create`.
- `app/Backends.fs` — `DockerBackend` rewritten onto dockerode; interprets every spec field.
- `app/ManagerUi.fs` — mint on create; dropped the `id` form input.
- `package.json`, `scripts/build.fsx`, `Yession.slnx`, `app/Yession.Host.fsproj` — wiring.
- `tests/Yession.Tests/Support.fs` — `Docker.gate`.
- `tests/Yession.Tests/DockerIntegration.fs` (+ `Main.fs`, fsproj) — the suite.
- `tests/Yession.Tests/Phase2.fs` — smoke routed through the gate.
- `tests/Yession.Tests/Domain.fs` — `SessionId.mint`/charset tests.
- `tests/fixtures/docker/Dockerfile` — build-spec fixture.
- `.github/workflows/release.yml` — fail-fast, `YESSION_REQUIRE_DOCKER`, cleanup.

## Out of scope (still gaps)
A real secret store (the resolver reads process env, local-dev only); non-Linux daemons; and
`darwin`/Windows Docker are unaddressed.
