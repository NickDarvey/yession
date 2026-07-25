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

Read `.agents/skills/contributing-changes/SKILL.md` when completing a plan to integrate
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
# Write nix.conf FIRST. The installer runs as root here with no `nixbld` group, and even
# --no-daemon fails at the final profile step unless build-users-group is explicitly empty.
mkdir -p ~/.config/nix && printf 'experimental-features = nix-command flakes\nbuild-users-group =\nsandbox = false\n' > ~/.config/nix/nix.conf
sh <(curl -L https://nixos.org/nix/install) --no-daemon      # installs /nix + ~/.nix-profile
. ~/.nix-profile/etc/profile.d/nix.sh                          # every shell (or re-login)
export NIX_SSL_CERT_FILE=/root/.ccr/ca-bundle.crt https_proxy="$HTTPS_PROXY"   # trust proxy CA
scripts/devenv-local.sh                                       # write devenv.local.yaml (see below)
nix shell 'https://channels.nixos.org/nixos-unstable/nixexprs.tar.xz#devenv' \
  -c devenv shell -- build                                    # enter devenv, build everything
```

If the installer already ran and failed with `the group 'nixbld' ... does not exist`, /nix is
populated but the profile is missing: write the nix.conf above, then
`/nix/store/*-nix-2.*/bin/nix-env -i /nix/store/*-nix-2.*` to create `~/.nix-profile`.

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
interface (`restore`/`build`/`start`/`dev`/`check`/`verify`/`lint`/`version`/`stage`/`package`/
`install-smoke`/`boot-smoke`/`clean`/`clean-docker`). The devenv scripts and the GitHub Actions
workflows are thin wrappers over it, and the Nix `outputs` call it too — throw devenv and CI
away and `dotnet fsi tasks.fsx <verb>` still drives everything.

## Versioning

The version is computed from the commit history (policy at the top of `tasks.fsx`), never stored
in a file. Every green master push publishes `1.0.0-beta.<n>`. To move the triple, put a marker in
the commit message — for a squash-merged PR, its title or body:

```
+semver: major   (or breaking, or a BREAKING CHANGE: footer)  -> 2.0.0-beta.0
+semver: minor   (or feature)                                 -> 1.1.0-beta.0
+semver: fix     (or patch)                                   -> 1.0.1-beta.0
```

A marker is read ONLY from the footer — the last blank-line-separated block of the message — and
must be a line of its own there. So put it last. Prose discussing a marker anywhere above it
(including this section's examples) never moves the version.

A plain `feat:` does NOT bump — a tag is cut per push, so nearly every release would. `version`
needs full history: it refuses a shallow clone rather than emitting an already-released number
(`git fetch --unshallow --tags`). `YESSION_VERSION` overrides the computation, which is how the
Nix derivations (their source has no `.git`) are told what they are.

Both bins answer `--version`, a session reports its build to the Manager on the spawn readiness
line (the Manager warns on a MAJOR mismatch only), and every process puts it on its OTel resource
as `service.version` — so a turn's counts can be attributed to a build at the collector. That
attribute is a CODE default and deliberately not part of the `OTEL_RESOURCE_ATTRIBUTES` the
Manager injects into a child: env wins, so injecting it would make sessions report the Manager's
version and hide the skew. A build that cannot know a release version says what it is instead —
`dev` unbundled, `test` under `check`, `0.0.0-g<rev>` from Nix. Never invent a version-shaped
placeholder.

Preinstalled, no action: Chromium at `$PLAYWRIGHT_BROWSERS_PATH` (`/opt/pw-browsers`) — the
`Browser` cap works here. The `node-datachannel` WebRTC addon is NOT built by npm (its prebuilt
lives on GitHub releases, which the proxy blocks); Nix builds it from source and bakes it into
the `nodeModules` derivation the dev shell symlinks in (and into the `outputs`) — so the
`Native` tier just works (see devenv.nix / Testing).

## Finding F# symbols

Never search for a bare name. F# reuses one identifier across several unrelated symbols:
`SessionId` is a type, its companion module, a DU case constructor, and twelve record
fields. `rg '\bSessionId\b'` returns 321 hits; only 97 are the type.

Search for the **declaration form** instead — it is anchored and unambiguous:

```
rg '^type SessionId\b'                      # the type
rg '^module SessionId\b'                    # its companion module (usually just below)
rg '^\s*(type|module|let|and)\s+Foo\b'      # any declaration of Foo, when unsure which
```

Then scope to the owning file, because short member names repeat across sibling modules —
`rg '^\s+let value\b' src/Yession.Domain/Identity.fs` returns nine hits, one per identity
type, disambiguated only by their pattern (`let value (SessionId s) = s`).

Two properties make this reliable:

- **Compile order is explicit.** Each `.fsproj` lists `<Compile Include>` in order, and a
  symbol is always declared in that file or an earlier one — read the `.fsproj` to bound
  the search.
- **Scoping is strictly top-down.** Within a file, a definition precedes every use, so
  going down, the first match is the declaration.

The one thing text search cannot recover is a type F# inferred rather than wrote: `let x =
foo bar` has no annotation to find. Follow the right-hand side to its declaration, or write
the type you expect and let `check` tell you if you are wrong.

## Testing

Tests gated by CAPABILITIES the run declares, not folders (`tests/Yession.Tests/Tags.fs`). A
suite runs only when this environment has every capability it needs; otherwise it reports a
skip — never an error. Pass the caps THIS box has as args:

```
check                        # cheap tier: pure/model/protocol on Node. Every PR. Fast.
check Browser                # + host-free rich-editor E2E. Needs only Chromium.
check Ports Native           # + WebRTC/host suites. Need the node-datachannel addon.
bash scripts/with-keyring.sh check Keyring   # + the OS-credential-manager suite, headless.
verify                       # == check Browser Ports Native Docker LiveAgent Keyring. Release
                             #    gate; CI wraps it in with-keyring.sh for the Keyring cap.
lint                         # actionlint over .github/workflows. Runs first in the PR gate.
```

`lint` is separate from `check` because it guards a different thing: a workflow file is only
validated by GitHub when it RUNS, and `release.yml` runs on master — after a merge. A syntax error
there is invisible to PR CI and lands already broken (a colon-space in an unquoted step name once
took every master release down at startup, zero jobs). The PR gate runs `lint` first, so that
class of break is caught in seconds rather than after merging.

Capabilities:
- `Browser` — Chromium via the .NET Playwright driver. Pins the .NET CLR runtime.
- `Ports` — binds TCP ports / spawns processes.
- `Native` — the native `node-datachannel` WebRTC addon, loaded by the real Session Process.
  Built from source by Nix (`node-datachannel` in devenv.nix, against nixpkgs
  libdatachannel + plog) and baked into the `nodeModules` derivation the dev shell symlinks in,
  so `Native`-tagged suites (all host-spawning ones, incl. the real WebRTC data-channel E2E) RUN
  here. Outside Nix the addon is absent and they skip cleanly.
- `Docker` — a reachable daemon. `LiveAgent` — real model credentials.
- `Keyring` — a usable OS credential manager (Plan 06: the secrets KEK lives there). On a
  desktop, `check Keyring` drives the genuine Keychain / Credential Manager / Secret Service;
  headless (this container, CI), wrap the run in `scripts/with-keyring.sh` — a private D-Bus
  session + gnome-keyring (both devenv packages) unlocked with an empty password.

To eyeball a rich-editor change in a real browser without any of the WebRTC machinery:
`check Browser` (drives Chromium against `tests/browser/editor-harness.html`). The full
two-peer WebRTC E2E runs where the Nix-built `Native` addon is present (CI, `verify`).
