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

Everything — the dev environment, the tasks, and the two install channels — is declared once by
[devenv](https://devenv.sh) ([devenv.nix](devenv.nix)) and pinned through nixpkgs, so what you
hack on and what you install are built the same way. The toolchain (Node 24, .NET SDK 10) comes
from that declaration; you never install it by hand.

### Developing

**Prerequisites**

- [devenv](https://devenv.sh) (and the Nix it runs on, flakes enabled) — it brings the pinned
  Node 24 and .NET SDK 10, so there's nothing else to install.

**Steps**

```sh
devenv shell       # enter the environment (Node, .NET on PATH)
build              # compile everything
check              # run the cheap test tier (check Browser / Ports Native / … for more)
start              # run the Session Process locally
```

Tasks are devenv scripts — `restore`, `build`, `start`, `dev`, `check` (tests; capabilities pass
as args, e.g. `check Browser`), `verify`, `package`, `clean` — each a thin wrapper over
[`tasks.fsx`](tasks.fsx), the complete, standalone build interface. The devenv scripts, the
GitHub Actions workflows, and the Nix outputs all call it; `dotnet fsi tasks.fsx <verb>` drives
everything on its own if you throw devenv and CI away.

### Installing

Yession ships two ways, side by side. Either gives you two commands — `yession-manager` (the
Manager) and `yession-session` (a Session Process) — and `yession-manager` serves a management UI
(default http://127.0.0.1:8321) to create, launch, resume, and stop sessions, each in its own
process.

#### Installing with npm

**Prerequisites**

- Node ≥24 — the only prerequisite.

**Steps**

```sh
npm i -g <release-tarball>   # installs yession-manager + yession-session
```

npm pulls the platform-native pieces — the WebRTC transport and the agent's native Claude Code
binary — through optional dependencies on install, so that one command is all it takes. Build the
tarball locally with `devenv build outputs.npm`.

#### Installing with Nix

**Prerequisites**

- Nix, flakes enabled.

**Steps**

```sh
nix profile install github:NickDarvey/yession          # add yession-manager + yession-session
nix run             github:NickDarvey/yession           # run the Manager without installing
nix build           github:NickDarvey/yession#yession   # just build the two wrapped bins
```

The Nix package is reproducible and self-contained: the native WebRTC addon is built from source,
the agent points at nixpkgs `claude-code`, and nothing runs an npm postinstall. The installable
derivations live in [`nix/packages.nix`](nix/packages.nix); [`flake.nix`](flake.nix) builds
`packages.<system>.{default,yession,npm}` from it directly — no devenv, so `nix build` /
`nix profile install` are pure (only the nixpkgs input) — and devenv exposes the same derivations
as `outputs.{staged,nix,npm}`. To pin it in a system, add the flake as an input and put
`yession.packages.<system>.default` in a NixOS `environment.systemPackages` / home-manager
`home.packages` list.

### Deploying

Out of the box the Manager and its sessions answer on loopback and trust nobody. To reach them
from anywhere else, settle two things — who the humans are, and where everything answers:

```sh
YESSION_MANAGER_URL=https://example.com          # the Manager: scheme + host, no path
YESSION_SESSION_URL=https://example.com/s/{id}   # sessions: a template over {id} / {port}

yession-manager --auth trusted-headers           # or --auth localhost on one machine
```

[`docs/deployment.md`](docs/deployment.md) has the interfaces in full — the trust rules and
the canonical `x-yession-*` header scheme, the session template, and why `{id}` keeps a
session's browser storage across restarts where `{port}` cannot — followed by a worked
Tailscale binding.

### Cloud sessions (Claude Code on the web)

Set the environment's **setup script** to `bash .claude/setup.sh`. It installs single-user
Nix (flakes enabled, with the container-specific fixes), puts the `devenv` CLI on PATH, and
warm-builds. `*.nixos.org` and `cache.nixos.org` are in the default Trusted network
allowlist, so nixpkgs (a `nixos.org` channel tarball, not a `github:` input) resolves and
substitutes with no extra allowed domains.

devenv itself would normally fetch `github:cachix/devenv`, which the sandbox blocks. So
[`.claude/setup.sh`](.claude/setup.sh) also writes a gitignored `devenv.local.yaml`
repointing the `devenv` input at devenv's own source **substituted from `cache.nixos.org`**
— `devenv shell` works with zero GitHub access — and the committed
[`.claude/settings.json`](.claude/settings.json) re-runs it with `--hook` on session start
to refresh that file. On a laptop / in CI the script no-ops and the normal `github:` input
is used.

### Testing

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

Dependency versions are pinned centrally: npm packages in [package.json](package.json), and NuGet
packages (including [Ylmish](Directory.Packages.props), the Elmish↔Yjs sync boundary) in
[Directory.Packages.props](Directory.Packages.props).

## Versioning

Yession's own version is computed from the commit history, never stored in a file. Master is
trunk, and every green push publishes `1.0.0-beta.<n>`. To move the major/minor/patch triple, put
a marker in the commit message — for a squash-merged PR, its title or body:

```
Rework the Manager control protocol (#42)

+semver: major
```

`+semver: major` (or `breaking`, or a `BREAKING CHANGE:` footer) → `2.0.0-beta.0`; `+semver: minor`
(`feature`) and `+semver: fix` (`patch`) move the other two. A marker only counts in the footer —
the last blank-line-separated block — on a line of its own, so a commit that merely talks about
markers can't accidentally cut a major release. A plain
`feat:` does *not* bump on its own — a tag is cut on every push, so nearly every release would. `dotnet fsi tasks.fsx version`
prints what the current commit would publish; the policy itself is at the top of
[tasks.fsx](tasks.fsx).

Both binaries report their build — `yession-manager --version`, `yession-session --version` — and a
session states it on the readiness line it prints at startup, so the Manager can warn when it has
just launched a session from a different major version. Each process also carries it into
telemetry as the OpenTelemetry `service.version` resource attribute, so token counts arriving at a
collector can be attributed to the build that produced them. Builds that cannot know a release version
say so rather than inventing one: `dev` for an unbundled dev run, `test` for the test tiers, and
`0.0.0-g<rev>` for a Nix build (whose source has no `.git`). Set `YESSION_VERSION` to override the
computation — that is how the Nix derivations are told what they are, and how a past release is
rebuilt.
