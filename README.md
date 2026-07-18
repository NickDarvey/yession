# Yession

A local-first runtime where humans and AI agents collaborate inside a shared **session**.

A session is both a **conversation** and a **workspace**. The experience should feel
like a shared working room, not a private chatbot thread: humans participate, observe,
and intervene, while agents work through explicit, scoped capabilities and isolated
environments.

In v1 a session is created manually. Later it may be created from work-intake systems
such as Slack or Linear. The first product slices deliberately prove the fundamentals
before introducing external workflow integrations.

## Why it is shaped this way

The runtime is built around a small number of non-negotiable architectural constraints:

- **Local first** — a session runs on a local node; clients connect to the local
  Session Process over WebRTC.
- **Reactive** — the system is explicit state transitions driven by events, with no
  shared mutable state across components.
- **Types first** — invariants, valid states, and allowed transitions are encoded in
  F# types and checked at compile time where possible.
- **Ylmish is the sync boundary** — Elmish owns the model; Ylmish encodes selected
  Elmish state into Yjs and back. Yjs is not the product model.
- **Durable facts are events** — collaborative editing state lives in Yjs; durable
  session history lives in a Session Process-owned append-only event log.
- **Capabilities are scoped, not ambient** — authority is delegated explicitly and
  composed at the application boundary.
- **Verification is automated end-to-end** — manual testing is never a substitute.

The full rationale, system design, and the architectural invariants that must survive
code review live in [docs/design.md](docs/design.md).

## Runtime targets

- **Browser Client** — F#/Fable running in the browser.
- **Session Process** — F# on Node, hosting the event log, the Yjs document, the
  Elmish loop, the agent runtime, and the WebRTC protocol.
- **Session Manager** — introduced in Phase 2; owns container/environment authority.

## Getting started

[mise](https://mise.jdx.dev) manages the toolchain and is the repository's interface:
every workflow runs through a mise task. The toolchain (Node, .NET, Fable) is pinned in
[mise.toml](mise.toml) and installed automatically.

```sh
mise install     # install the pinned toolchain (Node, .NET)
mise run restore # install all dependencies (npm + .NET tools)
mise tasks       # list every available task
```

Yession ships as one npm package with two commands, `yession` (the Manager) and
`yession-session` (a Session Process) — `npm i -g <release-tarball>` and npm pulls the
platform-native pieces (WebRTC transport, and the agent's native Claude Code binary) on
install; Node ≥24 is the only prerequisite. `yession` serves a management UI (default
http://127.0.0.1:8321) to create, launch, resume, and stop sessions, each in its own
process.

Core tasks: `restore`, `build`, `start`, `dev`, `test`, `verify`, `package`, `clean`. Prefer
`mise run <task>` (or `mise exec -- <cmd>`) over invoking `node`/`dotnet` directly so
the pinned versions are always used. `build` type-checks the F# solution and
Fable-compiles the Session Process host; `start` runs it. There is one test project, run by
[Pyxpecto](https://github.com/Freymaurer/Fable.Pyxpecto). Each suite declares what it *needs*
in code (not folders) — `Tag.needs "…" [Ports] …`, `[Docker]`, `[LiveAgent]`, `[Browser]`, or
`[]` for a pure suite — and the harness runs it only where those needs are met, reporting one
visible skip otherwise (`tests/Yession.Tests/Tags.fs`). Every need pins a runtime, so a suite
runs on exactly one and nothing runs twice:

- `test` is the cheap tier — pure/model/protocol suites on Node; fast, no ports, browser, or
  credentials. What PRs run.
- `verify` is the full release gate — the same suites plus the port-bound ones (real WebRTC,
  HTTP, process topology), live agent, Docker, and the real-browser E2E.

Almost everything runs on Node: .NET is a build tool and the tests exercise the same
JavaScript the product runs. The one exception is the real-browser E2E (`Browser.fs`, need
`[Browser]`), which Pyxpecto runs on the .NET CLR (`dotnet run --project tests/Yession.Tests`)
because it drives Chromium through the Microsoft.Playwright .NET driver against a live Session
Process. The harness discriminates the runtime with Fable's `Compiler.isDotnet`, so the one
shared test list serves both Node and the CLR with no conditional compilation in `Main.fs`.

The Session Process is F# compiled to JavaScript by [Fable](https://fable.io) and run on
Node (the `app/` host). Its WebRTC transport uses
[node-datachannel](https://github.com/murat-dogan/node-datachannel).

Dependency versions are pinned centrally: npm packages in [package.json](package.json)
and NuGet packages (including [Ylmish](Directory.Packages.props), the Elmish↔Yjs sync
boundary) via [Directory.Packages.props](Directory.Packages.props). F# projects added
from delivery step 00 onward reference NuGet packages by name; versions are governed
centrally.

## Roadmap

Delivery is organised into incremental phases and steps. Each step has a clear outcome,
the schemas/interfaces it introduces, and automated verification.

- **Design fundamentals:** [docs/design.md](docs/design.md)
- **Delivery plan (Phase 1 + Phase 2 steps):** [docs/plans/00-init/](docs/plans/00-init/)
- **Phase 3 plan (turn scheduling):** [docs/plans/01-turn-scheduling.md](docs/plans/01-turn-scheduling.md)
- **Phase 4 plan (Manager process split, delivered):** [docs/plans/02-manager-process.md](docs/plans/02-manager-process.md)
- **One draft per client (collaboration refinement, delivered):** [docs/plans/03-one-draft-per-client.md](docs/plans/03-one-draft-per-client.md)
- **Telemetry (OpenTelemetry — Manager as collector, sessions emit):** [docs/plans/04-telemetry.md](docs/plans/04-telemetry.md)
- **Progress & blockers tracker:** [docs/plans/TODO.md](docs/plans/TODO.md)
- **Known gaps (honest inventory):** [docs/GAPS.md](docs/GAPS.md)
