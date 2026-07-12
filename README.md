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

Core tasks: `restore`, `build`, `start`, `dev`, `test`, `clean`. Prefer
`mise run <task>` (or `mise exec -- <cmd>`) over invoking `node`/`dotnet` directly so
the pinned versions are always used. `restore`, `build`, `start`, `dev`, and `test` are
all live. `build` type-checks the F# solution and Fable-compiles the Session Process
host; `start` runs it (serving http://127.0.0.1:8080); `test` Fable-compiles a single test
project and runs the whole suite (domain/protocol units + a real WebRTC end-to-end test)
on Node with [Pyxpecto](https://github.com/Freymaurer/Fable.Pyxpecto). .NET is a build
tool only — nothing runs on the CLR; tests exercise the same JavaScript the product runs.

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
- **Progress & blockers tracker:** [docs/plans/TODO.md](docs/plans/TODO.md)

## Status

Implementation under way. Phase 1 steps 00–07 are complete: the shared domain, the
append-only event log, the process model & conversation projection, the multiplexed
WebRTC transport (real libdatachannel data channel + HTTP bootstrap/signalling) with a
token-gated handshake and presence events, the client Elmish shell, collaborative draft
sync through the Ylmish/Yjs boundary (two clients converge on one draft over real
WebRTC; `DraftStarted` is appended as the durable fact), and the send flow (`SendDraft`
snapshots the body into an immutable `MessageSent`; every append is advertised to all
peers), and read-only client event consumption (offset-paged catch-up, including after a
reconnect; the conversation timeline renders from the event projection alone). The
model/protocol suites and the event-driven WebRTC E2Es are green. See the
[tracker](docs/plans/TODO.md) for the current step and any blockers.
