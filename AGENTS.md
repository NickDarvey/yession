# Agents

## Communication

Respond terse like smart caveman. All technical substance stay. Only fluff die.

Rules:
- Drop: articles (a/an/the), filler (just/really/basically), pleasantries, hedging
- Fragments OK. Short synonyms. Technical terms exact. Code unchanged.
- Pattern: [thing] [action] [reason]. [next step].
- Not: "Sure! I'd be happy to help you with that."
- Yes: "Bug in auth middleware. Fix:"

Switch level: /caveman lite|full|ultra|wenyan
Stop: "stop caveman" or "normal mode"

Auto-Clarity: drop caveman for security warnings, irreversible actions, user confused. Resume after.

Boundaries: code/commits/PRs written normal.

## Contributing changes

Read `.claude/skills/contributing-changes/SKILL.md` when completing a plan to integrate
changes. Short version: compare implementation to plan; if consistent (no interesting
deviations, blockers, or uncompletable work), open PR with auto-merge, subscribe to PR
events, then watch the master pipeline after merge — auto-fix failures and repeat the
process until master is green. Deviations stop the loop and get reported instead.

## Bootstrap

The dev environment, tasks, and build outputs are all declared in **devenv.nix**: Node 24 +
.NET SDK 10, the tasks (devenv `scripts`), and the Nix package + npm tarball (devenv `outputs`).
On a laptop / in CI: `devenv shell` drops you in with `node`, `dotnet`, and the task scripts on
PATH.

A fresh Claude Code container: install Nix ONCE (single-user, no daemon), then enter devenv.

```
sh <(curl -L https://nixos.org/nix/install) --no-daemon      # installs /nix + ~/.nix-profile
. ~/.nix-profile/etc/profile.d/nix.sh                          # every shell (or re-login)
mkdir -p ~/.config/nix && echo 'experimental-features = nix-command flakes' >> ~/.config/nix/nix.conf
export NIX_SSL_CERT_FILE=/root/.ccr/ca-bundle.crt https_proxy="$HTTPS_PROXY"   # trust proxy CA
scripts/devenv-local.sh                                       # write devenv.local.yaml (see below)
nix shell 'https://channels.nixos.org/nixos-unstable/nixexprs.tar.xz#devenv' \
  -c devenv shell -- build                                    # enter devenv, build everything
```

**Why devenv works here without GitHub.** devenv's generated flake normally fetches
`github:cachix/devenv`, which this sandbox blocks (the GitHub proxy scopes fetches to attached
repos, and `add_repo` refuses cross-owner repos). So `devenv.yaml` pins nixpkgs to a
`nixos.org` channel tarball (allowed under the default-Trusted policy — `*.nixos.org` is, GitHub
isn't), and `scripts/devenv-local.sh` (run automatically by the SessionStart hook in
`.claude/settings.json`) writes a gitignored `devenv.local.yaml` that repoints the `devenv`
input at devenv's **own source substituted from `cache.nixos.org`** — zero GitHub. On a
laptop/CI the committed `devenv.yaml` (normal github input) is used and the hook no-ops.

Then use the task scripts (inside `devenv shell`): `check` / `build` / `verify`. `restore`
(dotnet tools; npm only when `node_modules` is absent) is called by the others — no need to run
it by hand. Do NOT invoke `dotnet`/`fable`/`esbuild` directly to "run the suite"; go through the
scripts so tool versions and PATH match CI. Under devenv, `node_modules` is a Nix artifact — the
offline npm tree with the native node-datachannel addon baked in — symlinked in by `enterShell`,
so nothing runs an npm postinstall and there's no per-file addon linking. Off-Nix, `restore`
falls back to `npm install --ignore-scripts`.
Every Yession build function lives in `tasks.fsx` — it's the complete, standalone build
interface (`restore`/`build`/`start`/`dev`/`check`/`verify`/`version`/`stage`/`package`/
`install-smoke`/`boot-smoke`/`clean`/`clean-docker`). The devenv scripts and the GitHub Actions
workflows are thin wrappers over it, and the Nix `outputs` call it too — throw devenv and CI
away and `dotnet fsi tasks.fsx <verb>` still drives everything.

Preinstalled, no action: Chromium at `$PLAYWRIGHT_BROWSERS_PATH` (`/opt/pw-browsers`) — the
`Browser` cap works here. The `node-datachannel` WebRTC addon is NOT built by npm (its prebuilt
lives on GitHub releases, which the proxy blocks); Nix builds it from source and bakes it into
the `nodeModules` derivation the dev shell symlinks in (and into the `outputs`) — so the
`Native` tier just works (see devenv.nix / Testing).

## Testing

Tests gated by CAPABILITIES the run declares, not folders (`tests/Yession.Tests/Tags.fs`). A
suite runs only when this environment has every capability it needs; otherwise it reports a
skip — never an error. Pass the caps THIS box has as args:

```
check                        # cheap tier: pure/model/protocol on Node. Every PR. Fast.
check Browser                # + host-free rich-editor E2E. Needs only Chromium.
check Ports Native           # + WebRTC/host suites. Need the node-datachannel addon.
verify                       # == check Browser Ports Native Docker LiveAgent. Release gate.
```

Capabilities:
- `Browser` — Chromium via the .NET Playwright driver. Pins the .NET CLR runtime.
- `Ports` — binds TCP ports / spawns processes.
- `Native` — the native `node-datachannel` WebRTC addon, loaded by the real Session Process.
  Built from source by Nix (`node-datachannel` in devenv.nix, against nixpkgs
  libdatachannel + plog) and baked into the `nodeModules` derivation the dev shell symlinks in,
  so `Native`-tagged suites (all host-spawning ones, incl. the real WebRTC data-channel E2E) RUN
  here. Outside Nix the addon is absent and they skip cleanly.
- `Docker` — a reachable daemon. `LiveAgent` — real model credentials.

To eyeball a rich-editor change in a real browser without any of the WebRTC machinery:
`check Browser` (drives Chromium against `tests/browser/editor-harness.html`). The full
two-peer WebRTC E2E runs where the Nix-built `Native` addon is present (CI, `verify`).
