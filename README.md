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

The dev environment, tasks, and build outputs are all declared by [devenv](https://devenv.sh)
([devenv.nix](devenv.nix)): the toolchain (Node + .NET SDK), the tasks (devenv scripts), and
the installable Nix package + npm tarball (devenv outputs). Exact versions are pinned via
nixpkgs in [devenv.nix](devenv.nix) (`nodejs_24`, `dotnet-sdk_10`).

```sh
devenv shell       # enter the environment (Node, .NET on PATH)
build              # compile everything
check              # run the cheap test tier (check Browser / Ports Native / … for more)
start              # run the Session Process locally
```

Tasks are devenv scripts: `restore`, `build`, `start`, `dev`, `check` (tests; capabilities pass
as args — `check Browser`), `verify`, `package`, `clean`. Each is a thin wrapper over one place,
[`tasks.fsx`](tasks.fsx) — the complete, standalone build interface. The devenv
scripts, the GitHub Actions workflows, and the Nix outputs all call it; `dotnet fsi
tasks.fsx <verb>` drives everything on its own if you throw devenv and CI away.

Yession ships two ways, side by side, each giving the commands `yession-manager` (the Manager)
and `yession-session` (a Session Process). Either way `yession-manager` serves a management UI
(default http://127.0.0.1:8321) to create, launch, resume, and stop sessions, each in its own
process.

- **npm package** — `outputs.packaged`. `npm i -g <release-tarball>` pulls the platform-native
  pieces (the WebRTC transport and the agent's native Claude Code binary) on install; Node ≥24
  is the only prerequisite. Build it locally with `devenv build outputs.packaged`.
- **Nix package** — the `installed` derivation. Reproducible and self-contained: the native
  WebRTC addon is built from source, the agent points at nixpkgs `claude-code`, nothing runs an
  npm postinstall. Build/install:

  ```sh
  nix build          github:NickDarvey/yession#yession  # build the two wrapped bins
  nix run            github:NickDarvey/yession           # run the Manager (yession-manager)
  nix profile install github:NickDarvey/yession          # add yession-manager + yession-session
  ```

  The installable derivations live in [`nix/packages.nix`](nix/packages.nix), and
  [`flake.nix`](flake.nix) builds `packages.<system>.{default,yession,packaged}` from it
  directly — no devenv involved, so `nix build` / `nix profile install` are pure (only the
  nixpkgs input). devenv.nix imports the same file for `devenv build outputs.<name>`, so the two
  never diverge. Add the flake as an input and put `yession.packages.<system>.default` in a NixOS
  `environment.systemPackages` / home-manager `home.packages` list.

### Cloud sessions (Claude Code on the web)

Set the environment's **setup script** to install Nix (`sh <(curl -L
https://nixos.org/nix/install) --no-daemon`, flakes enabled). `*.nixos.org` and
`cache.nixos.org` are in the default Trusted network allowlist, so nixpkgs (a `nixos.org`
channel tarball, not a `github:` input) resolves and substitutes with no extra allowed domains.

devenv itself would normally fetch `github:cachix/devenv`, which the sandbox blocks. The
committed [`.claude/settings.json`](.claude/settings.json) runs
[`scripts/devenv-local.sh`](scripts/devenv-local.sh) on session start, which writes a
gitignored `devenv.local.yaml` repointing the `devenv` input at devenv's own source
**substituted from `cache.nixos.org`** — so `devenv shell` works with zero GitHub access.
On a laptop / in CI the hook no-ops and the normal `github:` input is used.

Tests declare what they need in code, not by folder — `Ports`, `Docker`, `LiveAgent`,
`Browser`, or nothing for a pure suite — and the harness runs each one only where those
needs are met, skipping cleanly otherwise. There are two tiers:

- `check` is the cheap tier: pure, model, and protocol suites on Node. Fast, no ports or
  credentials. This is what PRs run. (`check Browser`, `check Ports Native`, … add tiers.)
- `verify` is the release gate: everything above plus the port-bound suites, the live
  agent, Docker, and the real-browser E2E.

Almost everything runs on Node. .NET is a build tool, and the tests exercise the same
JavaScript the product ships. The one exception is the browser E2E, which runs on the .NET
CLR so it can drive Chromium through Playwright against a live Session Process.

Versions are pinned centrally: npm packages in [package.json](package.json), and NuGet
packages (including [Ylmish](Directory.Packages.props), the Elmish↔Yjs sync boundary) in
[Directory.Packages.props](Directory.Packages.props).
