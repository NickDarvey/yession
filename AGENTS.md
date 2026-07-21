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

## Bootstrap

The repo interface is `just <task>`; the toolchain (Node 24, .NET SDK 10) comes from a Nix
flake (`flake.nix`). On a box with Nix: `nix develop` drops you in a shell with `node`,
`dotnet`, and `just` on PATH — no other install.

A fresh container without Nix: install it ONCE (single-user, no daemon), then enter the shell.

```
sh <(curl -L https://nixos.org/nix/install) --no-daemon      # installs /nix + ~/.nix-profile
. ~/.nix-profile/etc/profile.d/nix.sh                          # every shell (or re-login)
mkdir -p ~/.config/nix && echo 'experimental-features = nix-command flakes' >> ~/.config/nix/nix.conf
nix develop --command just build                              # enter shell, build everything
```

Behind Claude Code's egress proxy also export `NIX_SSL_CERT_FILE=/root/.ccr/ca-bundle.crt`
and `https_proxy="$HTTPS_PROXY"` so Nix trusts the proxy CA and reaches `cache.nixos.org`.
nixpkgs is pinned to a `nixos.org` channel tarball (not a `github:` input) precisely so it
resolves under the default-Trusted network policy, where `*.nixos.org` is allowed but arbitrary
GitHub repos are not.

Then use tasks (`just` with no args lists them): `just test` / `build` / `verify`. `restore`
(npm + dotnet tools) is a dependency of the others — no need to run it by hand. Do NOT invoke
`dotnet`/`fable`/`esbuild` directly to "run the suite"; go through `just` so tool versions and
PATH match CI. `restore` uses `npm install --ignore-scripts` — deterministic, github-free.

Preinstalled, no action: Chromium at `$PLAYWRIGHT_BROWSERS_PATH` (`/opt/pw-browsers`) — the
`Browser` cap works here. The `node-datachannel` WebRTC addon is NOT built by npm (its prebuilt
lives on GitHub releases, which the proxy blocks); it is supplied by Nix for the `Native` tier
and the packaged build (see flake.nix / Testing).

## Testing

Tests gated by CAPABILITIES the run declares, not folders (`tests/Yession.Tests/Tags.fs`). A
suite runs only when this environment has every capability it needs; otherwise it reports a
skip — never an error. Pass the caps THIS box has as args:

```
just test                    # cheap tier: pure/model/protocol on Node. Every PR. Fast.
just test Browser            # + host-free rich-editor E2E. Needs only Chromium.
just test Ports Native       # + WebRTC/host suites. Need the node-datachannel addon.
just verify                  # == just test Browser Ports Native Docker LiveAgent. Release gate.
```

Capabilities:
- `Browser` — Chromium via the .NET Playwright driver. Pins the .NET CLR runtime.
- `Ports` — binds TCP ports / spawns processes.
- `Native` — the native `node-datachannel` WebRTC addon, loaded by the real Session Process.
  Built from source by Nix (`packages.node-datachannel` in flake.nix, against nixpkgs
  libdatachannel + plog); in the Nix dev shell `just restore` links it into `node_modules`, so
  `Native`-tagged suites (all host-spawning ones, incl. the real WebRTC data-channel E2E) RUN
  here — unlike the old mise container. Outside Nix the addon is absent and they skip cleanly.
- `Docker` — a reachable daemon. `LiveAgent` — real model credentials.

To eyeball a rich-editor change in a real browser without any of the WebRTC machinery:
`just test Browser` (drives Chromium against `tests/browser/editor-harness.html`). The full
two-peer WebRTC E2E runs where the Nix-built `Native` addon is present (CI, `just verify`).
