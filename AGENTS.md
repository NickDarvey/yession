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

Read `.agents/skills/contributing-changes/SKILL.md` **before starting any change** — the
first time a session is asked to build, fix, refactor or alter something, before the first
edit. It decides how the work is split, and that is not a decision you can take afterwards.

Short version: split the request into the smallest increments that each stand alone —
builds, `check` passes, breaks nothing already merged — and ship each as its own PR rather
than banking them. The queue absorbs a moving master, but not conflict reconciliation, and
that tax is superlinear. Then compare what you built to what was asked; if consistent (no
interesting deviations, blockers, or uncompletable work), open PR with auto-merge,
subscribe to PR events, then watch the master pipeline after merge — auto-fix failures and
repeat the process until master is green. Deviations stop the loop and get reported instead.

**Plans are not artifacts.** Think however you need to, and say the shape of the work to the
user before starting it — but no plan document is written to this repository. `docs/plans/`
was deleted for the reason it is not coming back: a plan is right on the day it is written
and unfalsifiable ever after, so it drifts silently while reading as current. What is worth
keeping from one goes next to the code it governs, where a test can contradict it.

`master` merges through a **merge queue**: auto-merge enqueues rather than merges, a green
check never goes stale because master moved, and a check conclusion is not proof of either
outcome — read the PR's merge state. The skill's Merge semantics section is the detail.

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
`--hook`, which refreshes `devenv.local.yaml` and settles `devenv.lock` (the local override
makes devenv rewrite that committed file on every run; `.gitignore` says how, and
`tests/Yession.Tests/LockSource.fs` is what fails the pull request if a store path ever reaches
the committed copy).

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
`install-smoke`/`boot-smoke`/`example`/`clean`/`clean-docker`). The devenv scripts, the GitHub Actions
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

## Colocation

**A rule lives with the state it governs.** An invariant that holds only because a CALLER
remembered to ask first is not an invariant — it is a convention with a good reputation, and
the next caller has not read it.

`design.md` §1 says composition happens at the top. This is the other half of that sentence:
what composes at the top must have nothing left to DECIDE. A composition root that computes —
a bound, a fallback, a refusal, a subtraction — has taken a decision out of the only place it
could be tested cheaply and put it where no test can reach.

The tell is a member exported for no local reason. `SessionTerminals` briefly grew an
`Instrumented` predicate that nothing inside it used: it existed so `Host.fs` could ask "does
this terminal have blocks?", subtract a line window, and cap the answer. Meanwhile the WRITE
half of that same feature was one verb on the manager with its refusal inside it. Two halves of
one story in two shapes — and the half that had drifted upward was carrying arithmetic no cheap
test could see. A bug was living in it: closing a terminal forgot what its source had been, so
a closed device answered "this runs commands as blocks" about a recording sitting right there.

Three questions, and any `no` means it is in the wrong place:

- **Can a caller break it by not calling something first?** Then those calls are one verb.
  Take-then-write is `Write`, because an actor that could write without the lease is the second
  writer the lease exists to prevent.
- **Would testing it mean building the composition root?** Then move it down to where the state
  is. The cheap tier is the measure: a rule the cheap tier cannot reach is a rule nobody
  re-checks.
- **Does a sibling operation on the same state live somewhere else?** Then one of them has
  moved. `Write` and `Tail` sit together because the rule that admits one admits the other.

The corollary is that a seam belongs where it is USED. The terminal manager takes a transcript
reader because `Tail` needs one — not because a layer above it offered to read on its behalf.
Passing state downward is colocation; reaching upward for it is not.

## Fixing bugs

**Fix the root cause upstream; harden against the symptom downstream. Both, not either.** The
upstream fix stops THIS fault. The downstream guard stops the next one — a different fault
arriving at the same place — from being silently converted into a plausible wrong answer.

That is not belt-and-braces, which is two mechanisms for ONE requirement at one point. This is
one mechanism each at two points, and they go red at different times: upstream while the
diagnosis still exists, downstream before the damage is published.

The version regression is the shape. A transient invalid store path made `devenv shell --
version` fail; `echo "version=$(...)"` discards a substitution's exit status, so the step went
green with an empty version, `package` fell back to computing its own, and the release job
tagged the commit `v` — which then matched the computation's `--match 'v*'` and restarted the
6.13.1 line at 1.0.0. Upstream: capture under `pipefail` and refuse an empty result, so a failed
computation fails the run. Downstream: `--match 'v[0-9]*'`, and refuse a nearest tag that is not
this scheme's shape, so no malformed tag can move the version again.

The tell that you have done half: you can state the fault as a chain — X failed, so Y saw
nothing, so Z published garbage — and your change touches one link. Ask what the other links do
next time, given a different X.

## Examples

`examples/` holds integrations built the way somebody OUTSIDE this repository would build
them. An example is standalone by rule: it references nothing from `Yession.Domain` or
`Yession.Host`, carries its own project and bundle, and is not in the npm package or the Nix
installable. `dotnet fsi tasks.fsx example <name>` builds one.

That rule is the whole value, and it is easy to erode. The serial provider USED to be a third
shipped bin reaching into `Yession.Host.Interop` and the Domain's tool vocabulary — it worked,
and it demonstrated a path no reader could reproduce. If an example needs something the
product has, it copies it and owns the copy; the duplication is the point, not a smell.

An example is written in whatever the thing it integrates is written in, and the verb follows
it: `examples/serial` is F# through Fable and esbuild, `examples/jumpstarter` is a uv project
with its own pytest suite, and `example <name>` dispatches on which it finds. A provider is a
server, not a plugin — an examples directory that could only hold this repository's language
would be arguing the opposite.

The suite still tests them (the serial provider's own tests are the reason `Serial` exists as
a capability, and the jumpstarter provider's the reason `Jumpstarter` does), via a
ProjectReference from `tests/Yession.Tests` for the F# one and a spawned process for the
Python one. Both go ONE way: nothing in the product may reference an example.

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
the commit message — for a squash-merged PR, its title or description:

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
`refactor:`, ...) — convention for readers, not the version input. A PR squash-merges as PR
TITLE + PR DESCRIPTION: the description IS the commit body, and branch commit messages are
discarded. So the marker goes in the PR description, on a line of its own — and write that
description as the commit message it becomes, not as notes for a reviewer. Wrap it at 72
columns, which is where GitHub re-wraps it on the way in; write wider and the log fills with
ragged half-lines, re-flowed without regard for which of them was a marker.

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
check Jumpstarter            # + our MCP client driven against the Python example's provider,
                             #   over two real child processes. Needs uv and a CPython.
verify                       # == check Browser Ports Native Docker LiveAgent Keyring Nix Srt
                             #    Pty Serial Jumpstarter. Release gate; what CI runs on master.
                             #    Takes check's trailing args, so `verify --only "<text>"` works.
lint                         # actionlint over .github/workflows, then the F# analyzers over
                             #   every project in Yession.slnx. Runs first in the PR gate.
                             #   Exits 1 having judged the source and rejected it; exits 2
                             #   having judged NOTHING, because it does not compile — `build`.
check --only "<text>"        # narrow BOTH runtimes to cases whose full name contains <text>.
                             #   Buys back the RUNNING, not the compiling: 66s -> 44s on the
                             #   cheap tier, and far more on a tier that spawns browsers.
```

`lint` is separate from `check` because it guards a different thing: source judged without
running it, where running it would never reach the fault. GitHub only validates a workflow file
when it RUNS, and `release.yml` runs on master — after a merge — so a syntax error there is
invisible to PR CI and lands already broken. And a Lit template hole (`html $"""{x}"""`) is
boxed before Lit sees it, so a record or a union in one renders whatever it happens to
stringify — no test of the model can see it and no compiler warning covers it (FS3579 fires on
every hole and its only remedy, `%s`, is FS3376-illegal in a `FormattableString`). The typed
tree still knows, so `analyzers/Yession.Analyzers` asks it: the reasoning is in
`TemplateHoles.fs`.

The second rule there reads an `[<Emit>]` macro against the binding it sits on
(`EmitMacro.fs`). The string and the signature are two lines nothing checks together: name a
`$N` past the last argument and the emitted JavaScript reads `undefined`; leave an argument
unnamed and its expression is never emitted, so whatever it was going to do does not happen.
F# type-checks the signature and treats the string as a literal, the JavaScript that comes out
is valid either way, and an interop binding usually has one call site — so a test catches it
only by running that exact binding on the platform it targets. A parameter kept for a reason
other than being emitted says so with a leading underscore, which is the whole suppression
story and deliberately the language's own convention rather than one this rule invents.

The third reads the same macro from the other end (`EmitBody.fs`): Fable substitutes `$N` with
the caller's argument TEXT and pastes the result into the caller's scope, so a body that
declares a JavaScript binding can collide with a variable the caller happens to have named the
same (`const pc = $0` emitting `const pc = pc`, a temporal dead zone error that took the shell
down and reported as eight unrelated browser timeouts), and a placeholder written twice
evaluates its argument twice (a fresh peer id minted three times, so a first visit stored one
and returned another). Both are unrepresentable when the substitutions arrive as parameters of
a real function, which is what the rule asks for. It was a suite that matched `[<Emit(...)>]`
in F# source with a hand-kept list of directories to walk; that scan could not write its own
fixtures — a violating macro quoted literally in a scanned file would have been a real
violation — and needed a case asserting it had matched at least 300 emits, because a pattern
that has stopped seeing them and a codebase that obeys the rule read identically in a green
run. Reading the attribute's value off the typed tree costs none of that.

The fourth is not about interop at all (`RecordShapes.fs`): two record types carrying the same
field names make a bare `{ … = … }` build whichever was declared last, silently, at every
warning level, so a record added today re-points constructions written months ago in files its
author never opened. The remedy is `[<RequireQualifiedAccess>]` on every type in the group, and
the rule is what says which groups there are. It replaced a suite that read F# source with
regular expressions — a brace-balancer for the record body, an indent scan for where it ended,
a walk back up over doc comments for the attribute — six of whose eight cases tested that
reader rather than the rule. Two holes came with the text and the tree closes both: its label
pattern required an initial capital, so a lowercase-labelled record was invisible, and it had
no model of accessibility, so it counted records against each other that no scope can hold at
once. The population is now the assembly graph rather than a hand-kept list of directories,
which is also what lets it see a collision the old one structurally could not: neither
`Yession.Domain` nor the serial example references the other, so the pair only meets in
`tests/Yession.Tests`, which references both.

The fifth is the same fault on a different axis (`NamespaceShadowing.fs`): `open Yession.Domain`
puts every sub-namespace of it in front of a file by short name, so given a module `Terminals`
in scope and an opened namespace `Terminals`, a reference to `Terminals.foo` resolves to the
NAMESPACE when the namespace has a `foo` and falls through to the module when it does not, with
nothing said either way. Two scopes may share a short name; they may not also share a member —
which is what keeps `Yession.Domain` beside `Yession.Tests.Domain` legal. It replaced a scan
whose member pattern was anchored `^(?:type|and|module)` with no leading whitespace, so every
member of every nested module was invisible to it and a nested module never became a scope at
all; between them `Yession.App.App` did not exist as far as it was concerned, while it and the
namespace holding it both exported `Connection`. Note what a namespace is NOT: FCS gives the one
a file declares as a chain of one entity per segment, each holding no children, so the rule
derives a namespace from its members rather than reading it — which is also the only shape that
works for a referenced assembly, where namespaces do hold theirs.

The sixth is the other half of that hazard (`DomainExports.fs`), and stricter for a reason: the
domain is split so a file opens the two or three slices it needs, in no order anything fixes, and
a namespace is opened for its CONTENTS — so if two `Yession.Domain.*` namespaces both exported a
`Projection`, a file opening both would get whichever was opened last. No two of them may export
the same name at all. That is what makes the short names affordable: `TerminalProjection` and
`AuthzSubject` were prefixed to clear a flat namespace of 267 types and came off once each slice
had a namespace, and what stops `Projection` and `Subject` being ambiguous is not luck but this.
Its population is NAMED rather than derived, deliberately — `Yession.Domain.*` is the one family
this repository expects a file to open several of at once, which is a fact about how the domain
is meant to be used and not one the assembly graph knows.

The seventh is not about names at all (`EnvWrites.fs`): a process has ONE environment, so a
write to it is a write for everything that runs after — and the half that is easy to forget is
putting back what was there. A test suite is one process, and `Phase2`'s credential-leak
regression planted `ANTHROPIC_API_KEY` and DELETED it on the way out, which is a clear rather
than a restore: every `LiveAgent` suite after it ran with no credential, `SessionMain` answers
no credential by starting NO AGENT, and the live clone case got a session that accepted a
message and never replied. Nothing said why, four times. So the writes stay in one file per
assembly, which is the unit that becomes a process, and that file owns the give-back
(`Support.withEnv`). Guarding the write rather than its consequence is deliberate: the mutation
is in the cheap tier and only the consequence needs `LiveAgent`, so a guard on the consequence
fires on a tier almost nobody runs, months later. It replaced a suite whose patterns could not
tell a write from the mention of one — they matched an `[<Emit>]` macro's TEXT, so declaring the
JavaScript that assigns counted the same as running it, which is why it had to be scoped by hand
to one directory and why the product's own `Interop.setEnv` was invisible to it either way. On
the tree a declaration is a declaration and a call is a call.

The eighth reads the same expressions from the other end (`EnvReaders.fs`): a variable is read
from the environment in ONE place, and everywhere else is handed the value. A second reader is a
second DEFAULT, and two that disagree resolve into whichever the deployment reaches without
anybody choosing it. `YESSION_SESSION_AGENT_BACKEND` had two — `SessionMain` parsed it at boot
defaulting to `srt` and validated srt's tools on the strength of it, `Agent.fs` read it again
where the CLI is spawned defaulting to `host` — so a deployment that set nothing ran the agent
CLI unconfined while every statement the session made about itself said srt. Nothing was wrong at
either site; the fault was that there were two. It replaced a suite that could only ask which
files NAMED a variable, a question with false answers both ways (a comment, a list of names to
forward into a child, a `Map.tryFind` over somebody else's env), so it had to be told the two
variables it knew about and the one file each was allowed in. Reading which files READ it needs
no such table, and the population is every variable this repository names: thirty-three, two of
which were being read twice with a default apiece while the suite watched the two it had. What
makes that count honest is following a wrapper — `Tags.getEnv` chooses between the two forms by
runtime, so a rule seeing only direct reads would find it reading a variable it cannot name three
times — and stopping at bindings, which is what keeps `Support.withEnv` out of it.

`Population.fs` is what the scoping rules read: every declaration one project could name, of
the code this repository builds — its own contents entire, plus what it references, bounded to
the repository and cached per project. `Surfaces.fs` is the half the two namespace rules share:
what an `open` puts in front of a file, read once because both break the same way if it is
read wrong. `Expressions.fs` is the other side of the same idea, for a rule about what the code
DOES rather than what it could name: every call the project makes, and the binding each is
written inside — its own files only, because a referenced assembly hands over its declarations
and never its bodies, and a use is always somewhere. `Environs.fs` is what the environment rules
share on top of it: whether a call touches the process environment and which argument carries
the variable's name, which under Fable means reading the `[<Emit>]` macro, since the `$0` in
`process.env[$0]` is exactly that answer. A verdict over a whole population is the same for
every file in it, so it is reported once, on the project's last authored source file, anchored
at the declaration or the call it is about.

One rule answers for the others (`Unjudged.fs`). A declaration the compiler could not build is
not in the typed tree, so no rule sees it, each correctly reports nothing, and the run ends in
exactly the shape a clean one has — which is how `lint` came to report a product clean, and
exit 0, over a file carrying a macro that violated both emit rules. The break was one stray
indentation: a `let` at column 0 under a `namespace` is dropped along with the attribute on it,
while everything else in the file is still judged, so the run is not empty — it is short by
exactly the declaration nobody could read. The compiler already knows, and its diagnostics come
free with the type-check every rule depends on, so this reports the first error in the file and
`lint` exits 2 rather than 1: "these rules did not read this" must not arrive looking like
"these rules found nothing", and the next step is `build`, not a rule to go and fix.

Every rule carries a fixture — `analyzers/fixtures/<Rule>Fixture` — whose source says in
`// YES00n` markers which of its cases must be reported (across several files where the rule is
about how many files do something, since one file could then neither break it nor prove the rule
still sees a break), and `lint` checks the verdicts
against them in both directions on every run, counting only that rule's own diagnostics — the
two emit rules read the same macros from opposite ends, so a case one is asking about is
routinely something the other has an opinion on. That is what stops a rule going silently blind:
a loader that no longer matches the assembly name, a typed-tree shape moved by a compiler
upgrade, a project that failed to restore — each of those reports a clean product and passes.

**Adding a renderable type is deliberate.** The allow-list is closed — string, `TemplateResult`,
a sequence of them, a listener, `int`, `bool` — so a hole holding anything else is an error
until somebody widens the list and says in the fixture what widening it means. That is the
point: the failure mode of this rule is silence, and a closed list fails loudly.

### Running a tier this box cannot host (`verify.yml`, on demand)

`Docker` and `LiveAgent` need a daemon and a real model credential, so no laptop reliably has
both and no pull request runs either. Before `verify.yml` existed the only thing that ever ran
them was a master push, which meant developing anything that needed one by merging it and
watching the release gate. Do not do that: a red master stops everybody else releasing, and the
live clone suite cost two of them and produced no diagnosis either time.

Dispatch the gate against the branch instead. Same job the release gate runs — `release.yml`
calls the same file — so a green run here means what a green master means:

```
gh workflow run verify.yml --ref <branch>                      # the whole gate
gh workflow run verify.yml --ref <branch> \
  -f capabilities="LiveAgent Ports Native Srt"                 # one tier
gh workflow run verify.yml --ref <branch> \
  -f capabilities="LiveAgent Ports Native Srt" -f only="add_repo"   # one tier, one case
gh run watch "$(gh run list --workflow=verify.yml -L1 --json databaseId -q '.[0].databaseId')"
```

(Or the Actions tab → verify → Run workflow, which takes the same two fields.)

**Reach for it when** a suite you are writing needs a capability this box lacks, or a release
run failed inside one and you need another look. **Narrow it with `only`** — that is the
difference between a 9-minute gate and a run that answers one question, and it is also how a
live case stays inside the Node suite's shared 240s budget (`tasks.fsx`), which a long
per-case deadline otherwise blows for every suite at once.

Two edges worth knowing before the first attempt. GitHub only offers a `workflow_dispatch` for
workflows on the DEFAULT branch, so the file must be on master before a branch can be
dispatched against it — a new dispatchable workflow lands on its own, ahead of whatever needs
it. And the credential is a repository secret, so a run dispatched from a fork gets none: the
tier fails rather than skipping, which is the point (see below).

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
  RUNNING it, not by looking for it — installed is not the same as permitted. This container
  cannot create the nested user namespace the strict profile needs, so the suites run here
  only under `YESSION_NESTED_SANDBOX=weak check Srt`; unset, `check Srt` refuses to start.

  **`Srt` is not only the escape probes.** The default work sandbox IS srt, so every suite
  that runs a real command needs it — including the browser case that types one into a
  terminal composer. On a box that cannot host a sandbox those do not fail, they HANG: the
  command never runs, the block never reaches `ok`, and a Playwright timeout eventually
  reports, in effect, "this machine is not a machine this test can run on". Which is what a
  capability says in one line and a skip. If a suite you are writing runs a command, it needs
  `Srt`.

  **When to set the variable, and when it is a lie.** Set it to give a suite a sandbox to
  RUN in — `YESSION_NESTED_SANDBOX=weak check Browser Ports Native Srt` is how this container
  runs the whole PR tier, and what is under test there (a composer binding, a command really
  having run) is untouched by profile strength. Do NOT set it to turn a red run green when
  the CONFINEMENT is the thing under test: `weak` is srt's `enableWeakerNestedSandbox`, so a
  green escape probe under it is a green for a profile production never uses. Strict is CI's
  job — `pr.yaml` clears `kernel.apparmor_restrict_unprivileged_userns` so the probes run for
  real there — and weaker confinement in production is the operator's decision, never a way
  to get a passing session here.
- `Jumpstarter` — uv, and an interpreter it can resolve the `examples/jumpstarter` lock
  against. Probed by BUILDING that environment (`uv sync --frozen`), because "uv is on PATH"
  and "this box can assemble that environment" are different questions and only the second
  one is the suite's. devenv provides uv and a CPython, and pins the interpreter through
  `UV_PYTHON`/`UV_PYTHON_DOWNLOADS=never` so uv never fetches a second, unpinned toolchain.
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

**A test pins one invariant, and goes red only when that invariant breaks.** Two promises, and
a test earns its place by keeping both. The first is what it PROVES; the second is what its red
MEANS. A suite whose red can also mean "something moved" is a suite people re-run instead of
read.

One invariant is one arrangement, one action, one assertion of the consequence — and a name
that says which invariant. A case that asserts three things fails as one, so its red names
none of them, and the two behaviours nobody touched get dragged into every revision of the
third. Split it: three cases cost three names and buy three verdicts. What may repeat across
them is the SETUP, hoisted into a helper — asserting that a call both succeeded and returned
the right body is one invariant seen from two angles, but asserting that an open terminal reads
back, a closed one still does, and a shell refuses, is three tests wearing one name.

Then, so that red means what it says:

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

**A failure that cannot say why is a failure you will pay for repeatedly.** Most tiers here
fail legibly — an assertion prints what it expected. The browser tier does not: a case can only
fail by a wait never settling, so its red says which wait, never which fault. Three unrelated
defects in one feature (a call eliminated as dead code, an opaque redirect, a precache that
could not have happened) all printed the same thirty-second timeout, and each cost a full run
of the gate to tell apart — while the page had been saying which was which the whole time.

So instrument the boundary rather than guessing across it, and instrument it to STAY:

- **Fixtures keep what the thing under test said** — console, page errors, failed requests —
  and print it when a case fails (`Browser.fs`: `watching` / `reporting`). Free on green, and
  it is the only way to read a red run in CI, where nothing can be attached afterwards.
- **Anything that decides silently is made to say so.** A service worker choosing between the
  network and a kept copy leaves no trace anywhere; one `console.debug` at that branch is the
  difference between reading the answer and inferring it.
- **Throwaway instrumentation is a smell.** If you add a probe to answer a question, and the
  question could be asked again, the probe belongs in the fixture — not in a scratch file you
  delete. The rule is the same one the tests follow: what was verified once by hand is written
  down so it keeps being verified.

The tell that you are about to pay for this: you are about to run the gate again to test a
hypothesis. When looking is expensive, guessing and verifying cost the same, so guessing wins
each time and loses overall. Make looking cheap first — `check --only "<part of a test name>"`
narrows both runtimes to the cases that match.

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
is FOR.

The tell that you are about to write one anyway: the assertion quotes a class name, or it
would still pass if the surface rendered upside down in the dark. Both mean the test knows how
the view is BUILT rather than what it PROMISES. Write the promise, or write nothing.

**A screenshot and a test answer different questions.** The screenshot answers *is this any
good* — is it crowded, is the hierarchy right, does the eye land where it should. That is a
judgement about the design as it stands today, it has no answer that stays true, and writing
it down as an assertion is how the suite fills up with tests nobody believes. Go and look
(the `ui-exploration` skill drives real ones), decide, and move on without writing anything.

A test answers *is this still true* — and needing a browser to SEE the answer says nothing
about whether it is worth pinning. Visibility, focus, stacking, overflow, contrast: these are
promises that only a rendered page can settle, and they are exactly the promises that break
silently, because every cheap tier reads the markup and the markup still looks right. A
whole-page render contains both mounts of a notice and calls it fine; a person sees a screen
saying the same thing twice. So when the invariant is real and only a browser can observe it,
the browser tier is where it goes. "I looked once and it was fine" is not a substitute — it
is the state every regression starts from.

(This paragraph replaced a rule that said the opposite — *if a screenshot would settle it, it
is not a test* — which read as licence to delete one. The invariant deleted under it was
"whatever is wrong with the connection is on the screen exactly once", verified by eye across
four states and then thrown away. It was a promise, and one no markup test can reach.)

**What makes a browser test brittle is the COUPLING, not the browser.** That deleted test
failed on its first run for a reason that had nothing to do with its invariant: it counted
occurrences of the words *not connected* and *reconnecting*, and the surface that was showing
said *session stopped*. The invariant was right; the assertion had been written against the
copy. Assert on what a browser can measure that a redesign does not move:

- **Hooks, and how many of them are VISIBLE** — one `data-*` per report, however many mounts
  it has. Count the mounts a person can see, never the words in them. Beware the cheap
  visibility checks: `offsetParent` is null for anything `position: fixed`, and a bounding
  rect stays non-zero for an element clipped to nothing by an `overflow-hidden` parent (this
  is how a collapsed nav's contents measure as on screen). Hit-test the element's own centre
  and ask whether what is painted there belongs to it.
- **Geometry** — that a fixed bar does not cover the header under it, that nothing overflows
  the viewport sideways. Compare measurements, never pixel-match a screenshot: a reference
  image fails on a font tweak, which is the coupling all over again.
- **Computed values** — that a focus ring actually paints, that a token resolves to the
  contrast it promises.

The same "regress it and watch it go red" rule applies, and it is worth more here than
anywhere: a browser test that cannot fail is expensive silence.

One caveat, because loosening a UI assertion is how it quietly stops testing: a whole-page
render contains every surface at once, so a bare `.Contains "swift-heron"` is satisfied by the
ROSTER while the element under test still prints a raw id. Scope the assertion — to the
element, or to the phrase around it — then confirm it by REGRESSING the behaviour and watching
it go red. Decoupling from incidental copy is only an improvement while the test still fails
for the right reason; a test loosened into vacuity is worse than the brittle one it replaced,
because it reads as coverage.
