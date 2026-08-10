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

A fresh Claude Code container: run `bash .claude/setup.sh` once (idempotent; minutes cold,
cheap to re-run). It installs single-user Nix with the container-specific fixes, makes every
later shell inherit it, writes the gitignored `devenv.local.yaml` that lets devenv resolve
without GitHub (the sandbox proxy blocks devenv's normal `github:cachix/devenv` fetch, so the
input is repointed at devenv's own source substituted from `cache.nixos.org`; on a laptop/CI
the committed `devenv.yaml` with the normal github input is used), puts the `devenv` CLI on
PATH, and warm-builds. The SessionStart hook (`.claude/settings.json`) re-runs it with
`--hook`, which only refreshes `devenv.local.yaml`.

Then use the task scripts (`devenv shell -- <task>`, or bare inside the shell): `check` /
`build` / `verify`. `restore`
(dotnet tools; npm only when `node_modules` is absent) is called by the others — no need to run
it by hand. Do NOT invoke `dotnet`/`fable`/`esbuild` directly to "run the suite"; go through the
scripts so tool versions and PATH match CI. Under devenv, `node_modules` is a Nix artifact — the
offline npm tree with the native node-datachannel addon baked in — symlinked in by `enterShell`,
so nothing runs an npm postinstall. Off-Nix, `restore` falls back to
`npm install --ignore-scripts`.

Preinstalled in this container, no action needed: Chromium at `$PLAYWRIGHT_BROWSERS_PATH`
(`/opt/pw-browsers`) — the `Browser` cap works. The `node-datachannel` WebRTC addon is built
from source by Nix and baked into the `nodeModules` derivation (npm cannot fetch its prebuilt
here) — the `Native` cap works too (see Testing).

## Build interface

Every Yession build function lives in `tasks.fsx` — the complete, standalone build interface
(`restore`/`build`/`start`/`dev`/`check`/`verify`/`lint`/`version`/`stage`/`package`/
`install-smoke`/`boot-smoke`/`clean`/`clean-docker`). The devenv scripts, the GitHub Actions
workflows, and the Nix `outputs` are thin wrappers over it — throw devenv and CI away and
`dotnet fsi tasks.fsx <verb>` still drives everything.

The derivations themselves (`nix/packages.nix`) have three consumers, and the difference
between them is which SOURCE they build: `flake.nix` and `devenv.nix` both build a store copy
of the repo (git-filtered for the flake, whole-directory for devenv), while
`nix/worktree.nix` evaluates in place, against the tree as it stands — `nix build --file
nix/worktree.nix nix|npm|staged|nugetDeps`. That last route is what `check Nix` drives and the
only one that can catch a `src` filter that has stopped matching what git tracks.

**No new helper scripts.** New build/dev/repo functionality is a `tasks.fsx` verb, not a shell
script. Only glue that must run where `dotnet` cannot stays outside, and the one existing
script is exactly that: `.claude/setup.sh` runs before Nix/devenv exist. Everything else —
including the headless D-Bus/keyring wrapping, which `check` arranges by re-execing itself —
is a verb. Anything that could be a verb, is a verb.

**No belt-and-braces.** When two mechanisms could satisfy the same requirement (two config
locations, a fallback beside a primary), keep ONLY the one verified working here and delete
the other. A redundant spare hides which path is live, rots unverified, and turns the next
failure into an archaeology dig.

## UI baseline

WCAG 2.0 AA is the floor for every surface, not a follow-up:

- **Contrast**: text ≥ 4.5:1 against the surface it actually sits on (3:1 only ≥ 24px, or
  ≥ 19px bold). Check every surface a token touches, not just black — the cheap-tier
  theme-contrast test (Phase4) pins the tokens in `app/tailwind.css`.
- **Keyboard**: every action is a real `<a>`/`<button>`/`<input>` (no click-only
  elements), operable by Tab/Enter/Space, with a visible focus state. A DOM swap that
  replaces the focused element must refocus its replacement, never strand focus.
- **Structure**: inputs get `<label>`s, tables get `th scope`, icon-only controls get an
  accessible name, pages declare `lang` and a title.

## Versioning

The version is computed from the commit history (policy at the top of `tasks.fsx`), never stored
in a file. Every green master push publishes `1.0.0-beta.<n>`. To move the triple, put a marker in
the commit message — for a squash-merged PR, its title or body:

```
+semver: major   (or breaking, or a BREAKING CHANGE: footer)  -> 2.0.0-beta.0
+semver: minor   (or feature)                                 -> 1.1.0-beta.0
+semver: fix     (or patch)                                   -> 1.0.1-beta.0
```

A marker counts ANYWHERE in the message — subject, body, or footer — but must be a line of its
own, with nothing else on it. Prose that mentions one mid-sentence never moves the version, and
neither do the examples above (the trailing `-> 2.0.0-beta.0` keeps those lines from standing
alone). The corollary: do NOT paste that table bare into a commit or PR body, because then it
does bump.

`BREAKING CHANGE:` is the exception — it is read only from the footer, the last
blank-line-separated block. It is a conventional-commits trailer, and it is the one marker that
moves MAJOR; scanning the whole body for it once cut a spurious major tag off line-wrapped prose.

**When to bump.** A breaking change to the Manager ↔ Session API (the protocol between the
`yession` and `yession-session` bins — the Manager tolerates anything but a MAJOR mismatch) is
a major bump. Otherwise standard semver: new user-facing capability → minor, bug fix → patch.
The same policy applies once the version leaves beta. A plain `feat:` subject does NOT bump —
a tag is cut per green master push, so nearly every release would; only an explicit marker
moves the triple.

**Commit / PR messages.** Subjects follow conventional-commit style (`feat:`, `fix:`, `ci:`,
`refactor:`, ...) — that is convention for readers, not the version input. PRs squash-merge with
the PR title as the commit subject and the CONSTITUENT COMMIT MESSAGES concatenated as the body
— the PR DESCRIPTION is discarded, so a marker that lives only there never reaches master (how
the Plan 08 feature shipped as beta.114 instead of 1.1.0-beta.0). Put the marker on a line of
its own in a COMMIT body on the branch; squash concatenation preserves commit bodies verbatim.

`version` needs full history: it refuses a shallow clone rather than emitting an
already-released number (`git fetch --unshallow --tags`). `YESSION_VERSION` overrides the
computation — how the Nix derivations (no `.git` in their source) are told what they are.

**Version reporting.** Both bins answer `--version`; a session reports its build to the Manager
on the spawn readiness line; every process puts it on its OTel resource as `service.version`.
That attribute is a CODE default, deliberately not part of the `OTEL_RESOURCE_ATTRIBUTES` the
Manager injects into a child — env wins, so injecting it would make sessions report the
Manager's version and hide the skew. A build that cannot know a release version says what it
is instead — `dev` unbundled, `test` under `check`, `0.0.0-g<rev>` from Nix. Never invent a
version-shaped placeholder.

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
check Keyring                # + the OS-credential-manager suite. Headless, check re-execs
                             #   itself under a private D-Bus session + gnome-keyring.
check Srt                    # + the sandbox escape probes: read/write/egress denial through
                             #   real bubblewrap. See Srt below for this container's profile.
check Nix                    # + the build-source contract, then builds the installable from
                             #   the WORKING TREE and boots it. Minutes; the only gate on it.
verify                       # == check Browser Ports Native Docker LiveAgent Keyring Nix Srt.
                             #    Release gate; what CI runs on master.
lint                         # actionlint over .github/workflows. Runs first in the PR gate.
```

`lint` is separate from `check` because it guards a different thing: GitHub only validates a
workflow file when it RUNS, and `release.yml` runs on master — after a merge — so a syntax
error there is invisible to PR CI and lands already broken. The PR gate runs `lint` first to
catch that class of break in seconds.

**Asking for a capability requires it.** A tier names what it wants, and `check` refuses to
start — naming every missing one and how to get it — when this box cannot host something it
was asked for. It does NOT quietly drop the capability and let its suites report a skip: that
reads as prudence and behaves as a blind spot (a release workflow naming a secret this
repository does not have shipped every version up to v5.0.0-beta.0 with the live agent suite
skipped, green, and never once a real agent turn). So `check Docker` on a daemon-less box is
an error, not a skip, and `verify` runs only where the whole gate can run — which is what a
release gate means. What is still a skip is the capability a tier never asked for, and the
RUNTIME partition (a Node suite on the .NET CLR and vice versa).

Capabilities:
- `Browser` — Chromium via the .NET Playwright driver. Pins the .NET CLR runtime.
- `Ports` — binds TCP ports / spawns processes.
- `Native` — the native `node-datachannel` WebRTC addon, loaded by the real Session Process.
  Present under Nix (built from source, baked into the `nodeModules` derivation the dev shell
  symlinks in), so `Native`-tagged suites (all host-spawning ones, incl. the real WebRTC
  data-channel E2E) RUN here. Outside Nix the addon is absent and they skip cleanly.
- `Docker` — a reachable daemon, probed with `docker info` (the same socket / DOCKER_HOST the
  backend uses, so it answers the question the suites care about rather than "is the socket
  file there"). The dev container has none, so ask for `Docker` here and the run refuses.
- `LiveAgent` — real model credentials: `ANTHROPIC_API_KEY` or `CLAUDE_CODE_OAUTH_TOKEN`.
  release.yml passes the repository secret; absent, a tier that asked for it fails.
- `Keyring` — a usable OS credential manager (the secrets KEK lives there). On a desktop,
  `check Keyring` drives the genuine Keychain / Credential Manager / Secret Service; headless
  (this container, CI), it re-execs itself under a private D-Bus session + gnome-keyring
  unlocked with an empty password (both from devenv).
- `Srt` — OS-level confinement: bubblewrap + socat on Linux, Seatbelt on macOS. Probed by
  RUNNING it, not by looking for it — installed is not the same as permitted. This
  container cannot create the nested user namespace the strict profile needs, so the suites
  run here only under `YESSION_SANDBOX_NESTED=weak check Srt`; unset, `check Srt` refuses to
  start. Never set that variable to make a session pass — weaker confinement is the
  operator's decision, and production defaults to strict.
- `Nix` — the nix CLI (probed like Docker). Covers the ONE thing no CI job can: the
  derivations built against the WORKING TREE.
  Every CI route (`nix build .#yession`, darwin-package, package-nix) evaluates a flake, whose
  source copy git already filtered — so a `src` filter that lets the dev shell's `node_modules`
  symlink or 176MB of `obj/`/Fable output into the derivation is green everywhere in CI and
  broken on the laptop. `check Nix` asserts the source contract (`NixSource.fs`), then builds
  `nix/worktree.nix` and boot-smokes the result — which is also what re-checks the NuGet FOD
  hash, the other thing a devenv-only `check` cannot see.

To eyeball a rich-editor change in a real browser without any of the WebRTC machinery:
`check Browser` (drives Chromium against `tests/browser/editor-harness.html`). The full
two-peer WebRTC E2E runs where the Nix-built `Native` addon is present (CI, `verify`).

To inspect or iterate on a server-rendered surface (the manager page) with real
screenshots, read `.agents/skills/ui-exploration/SKILL.md` first — headless Chromium's
window-size clamp makes naive mobile screenshots lie; the skill's CDP driver does not.

### Writing tests

High signal, non-brittle. A test earns its place by failing when behavior regresses — and only
then:

- Assert observable behavior and contracts, not implementation detail (private state, call
  order, exact log text, incidental DOM structure). A refactor that preserves behavior must
  not break a green test.
- Deterministic: no real-time sleeps, no ordering luck, no reliance on anything a declared
  capability doesn't provide. A flaky test is worse than none — it trains everyone to
  ignore red.
- When verifying interesting behavior by hand (a bug fix, a protocol edge, a rendering
  quirk), write the check down as a lasting test instead: the manual run proves it once, the
  test keeps proving it. Verify-once throwaways stay out.
- Tag suites with the MINIMUM capabilities they truly need, so they run in the cheapest tier
  that can host them and skip (never error) everywhere else.

**A UI test pins an invariant, never a design.** A rendered surface is the most-revised thing
in the product. A test that asserts what it currently LOOKS like will be deleted by the next
person to improve it, and until they do, its red says "your redesign is wrong" when it means
"something moved" — which is how a suite stops being believed. So test only what has to hold
true no matter how the screen is redrawn:

- **Availability** — a destructive control is not offered over nothing; the composer is never
  taken away by a network fault; an action a person is entitled to is reachable.
- **Identity and attribution** — one person wears one name on every surface at once; an
  author is never a raw id; a thing that was said is attributed to whoever said it.
- **The accessibility floor** — keyboard-operable controls, accessible names, contrast held.
  Pinned ONCE and centrally (the chrome-consistency and theme-contrast suites), never
  re-asserted per surface.

Not: which element marks an empty state, what a style token's Tailwind string contains,
whether a control recedes by border or by opacity, the presence of a decorative mark, the copy
of a label that is not itself a promise. Those are the design changing, which is what a design
is FOR. **If a screenshot would settle it, it is not a test** — look at the screen (the
`ui-exploration` skill drives real ones) and move on.

The tell that you are about to write one anyway: the assertion quotes a class name, or it
would still pass if the surface rendered upside down in the dark. Both mean the test knows how
the view is BUILT rather than what it PROMISES. Write the promise, or write nothing.

One caveat, because loosening a UI assertion is how it quietly stops testing: a whole-page
render contains every surface at once, so a bare `.Contains "swift-heron"` is satisfied by the
ROSTER while the element under test still prints a raw id. Scope the assertion — to the
element, or to the phrase around it — then confirm it by REGRESSING the behaviour and watching
it go red. Decoupling from incidental copy is only an improvement while the test still fails
for the right reason; a test loosened into vacuity is worse than the brittle one it replaced,
because it reads as coverage.
