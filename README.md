# Yession

A local-first runtime where humans and AI agents work together in a shared **session**.

A session is a conversation and a workspace at the same time. It should feel like a
shared working room, not a private chatbot thread: humans take part, watch, and step in,
while agents work through explicit, scoped capabilities inside isolated environments.

For now you create a session by hand. Later it might come from work-intake systems like
Slack or Linear. The early slices prove the fundamentals first, before any of that.

## Why it's shaped this way

A handful of constraints drive the whole design:

- **Local first.** A session runs on a local node; clients connect to the local Session
  Process over WebRTC.
- **Reactive.** State changes through explicit transitions driven by events, with no
  shared mutable state across components.
- **Types first.** Invariants, valid states, and allowed transitions live in F# types,
  checked at compile time where possible.
- **Ylmish is the sync boundary.** Elmish owns the model; Ylmish encodes selected Elmish
  state into Yjs and back. Yjs is not the product model.
- **Durable facts are events.** Collaborative editing state lives in Yjs; durable session
  history lives in an append-only event log the Session Process owns.
- **Capabilities are scoped, not ambient.** Authority is handed out explicitly and
  composed at the application boundary.
- **Verification is automated end to end.** Manual testing doesn't count.

The full reasoning, and the invariants that have to survive code review, are in
[docs/design.md](docs/design.md).

### Runtime targets

- **Browser Client** — F#/Fable running in the browser.
- **Session Process** — F# on Node, hosting the event log, the Yjs document, the Elmish
  loop, the agent runtime, and the WebRTC protocol.
- **Session Manager** — owns container and environment authority.

## Getting started

[mise](https://mise.jdx.dev) manages the toolchain and is the way into the repo: every
workflow is a mise task. Versions (Node, .NET, Fable) are pinned in [mise.toml](mise.toml)
and installed for you.

```sh
mise install     # install the pinned toolchain (Node, .NET)
mise run restore # install all dependencies (npm + .NET tools)
mise tasks       # list every available task
```

Yession ships as one npm package with two commands: `yession` (the Manager) and
`yession-session` (a Session Process). `npm i -g <release-tarball>` pulls the
platform-native pieces (the WebRTC transport and the agent's native Claude Code binary) on
install; Node ≥24 is the only prerequisite. `yession` serves a management UI (default
http://127.0.0.1:8321) to create, launch, resume, and stop sessions, each in its own
process.

The core tasks are `restore`, `build`, `start`, `dev`, `test`, `verify`, `package`, and
`clean`. Run them through `mise run <task>` (or `mise exec -- <cmd>`) so the pinned
versions are always used. `build` type-checks the F# solution and Fable-compiles the
Session Process host; `start` runs it.

Tests declare what they need in code, not by folder — `Ports`, `Docker`, `LiveAgent`,
`Browser`, or nothing for a pure suite — and the harness runs each one only where those
needs are met, skipping cleanly otherwise. There are two tiers:

- `test` is the cheap tier: pure, model, and protocol suites on Node. Fast, no ports or
  credentials. This is what PRs run.
- `verify` is the release gate: everything above plus the port-bound suites, the live
  agent, Docker, and the real-browser E2E.

Almost everything runs on Node. .NET is a build tool, and the tests exercise the same
JavaScript the product ships. The one exception is the browser E2E, which runs on the .NET
CLR so it can drive Chromium through Playwright against a live Session Process.

Versions are pinned centrally: npm packages in [package.json](package.json), and NuGet
packages (including [Ylmish](Directory.Packages.props), the Elmish↔Yjs sync boundary) in
[Directory.Packages.props](Directory.Packages.props).
