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

The toolchain comes from a [Nix flake](flake.nix); [`just`](https://just.systems) is the
task runner. `nix develop` gives an environment — Node 24, the .NET SDK 10, `just` — that is
identical on a laptop and in a Claude Code cloud session, with nothing curl-bootstrapped.

```sh
nix develop        # enter the dev shell (Node, .NET, just on PATH)
just               # list every task
just restore       # install all dependencies (npm + .NET tools)
just build         # build everything
```

No Nix? Any Node 24 + .NET SDK 10 will do — install [`just`](https://just.systems), then run
the same `just` tasks. Nix just pins those versions for you (`dotnet-sdk_10` = 10.0.301,
`nodejs_24`); bump the nixpkgs pin in `flake.nix` to move them.

Yession ships two ways, side by side, each giving the two commands `yession` (the Manager)
and `yession-session` (a Session Process). Either way `yession` serves a management UI
(default http://127.0.0.1:8321) to create, launch, resume, and stop sessions, each in its
own process.

- **npm package.** `npm i -g <release-tarball>` pulls the platform-native pieces (the WebRTC
  transport and the agent's native Claude Code binary) on install; Node ≥24 is the only
  prerequisite.
- **Nix package.** Reproducible and self-contained — the native WebRTC addon is built from
  source, the agent points at nixpkgs `claude-code`, and nothing runs an npm postinstall:

  ```sh
  nix run    github:NickDarvey/yession          # run the Manager without installing
  nix profile install github:NickDarvey/yession # add yession + yession-session to your profile
  ```

  Or add the flake as an input and put `yession.packages.<system>.default` in a NixOS
  `environment.systemPackages` / home-manager `home.packages` list.

The core tasks are `restore`, `build`, `start`, `dev`, `test`, `verify`, `package`, and
`clean` — run them as `just <task>` inside `nix develop`. Capabilities pass as arguments:
`just test Browser`. `build` type-checks the F# solution and Fable-compiles the Session
Process host; `start` runs it.

### Cloud sessions (Claude Code on the web)

To get the same environment in a cloud session, set the environment's **setup script** to
install Nix (`sh <(curl -L https://nixos.org/nix/install) --no-daemon`, with flakes enabled)
and warm the shell (`nix develop --command true`). `*.nixos.org` and `cache.nixos.org` are in
the default Trusted network allowlist, so the flake resolves and substitutes without any
extra allowed domains — nixpkgs is pinned to a `nixos.org` channel tarball rather than a
`github:` input for exactly that reason.

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
