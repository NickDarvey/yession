# One function builds every git spawn, so a probe cannot gate verbs it does not run as

> Decided 2026-08-28 · Supersedes nothing · Related: [GAPS.md](../GAPS.md) "Every binary a
> confined spawn execs is NAMED", `app/Repos.fs` (`gitExec`, `hardenedEnv`, `unusableGit`),
> `tests/Yession.Tests/GitIntegration.fs` "a global git config in the operator's home never
> reaches a verb"

## Decision

Every git the repo verbs spawn — the start-up probe included — is built by one function that
carries the hardened environment. A probe that runs git differently from the verbs is not a
probe of the verbs.

The regression test plants a MALFORMED config rather than an unreadable one.

## What went wrong

`YESSION_BIN_GIT` names git for a confined spawn, and unset the verbs fall back to PATH so an
off-Nix install does not regress. An `npm i -g yession` on macOS is exactly where that
fallback is wrong, so the git sandbox proves `git --version` before any verb runs one, and
refuses with a sentence naming `YESSION_BIN_GIT` and this host's resources profile instead of
passing the host binary's excuse through.

That probe then refused a git that worked, because it was the one git spawn built without the
hardened env. `git --version` ran with an EMPTY env, which is an env no verb ever runs with:

1. git resolves its global config path before it does anything at all.
2. It tolerates `EACCES` there and treats every other errno as fatal.
3. Seatbelt answers `EPERM`.

So on a macOS host whose operator has a `~/.config/git/config` — a home-manager install always
does — the probe died `fatal: unable to access … (Operation not permitted)`, exit 128, and
every repo verb was refused for the sandbox's whole lifetime, in words blaming the binary and
the read scope. Neither was at fault.

Taking the refusal's own advice would have made it worse: declaring `~/.config/git` as a
resource and defaulting it hands back part of the home the read scope exists to deny, to fix a
probe that should never have been reading it.

## Why Linux could not catch it

srt denies a read on Linux by mounting emptiness over the path, so a denied config reads as
ENOENT and git shrugs. Seatbelt denies it in place, and `EPERM` is fatal.

Any suite that plants an UNREADABLE file and expects git to fail is therefore green on the
platform CI runs, no matter which env the spawn carries — the assertion passes for the wrong
reason and pins nothing. That is why this shipped.

So the regression test plants a malformed config somewhere the sandbox may read. A config git
PARSES is a fault it reports as its own, which is exactly what an unhardened spawn hands back
to whoever asked — and it fails identically on both platforms. The test pins that no git
spawned here reads the operator's global config, not the errno that made the difference
visible.

## The general rule

This is the colocation rule in AGENTS.md, on a seam that looked too small to have one. Two
call sites built a spawn independently; one of them was a check on the other. The invariant —
"a git spawned here carries the hardened env" — held only because each caller remembered, and
the caller that forgot was the one whose job was to catch mistakes.

A probe belongs to the thing it probes. If it constructs its subject differently, it is
testing a different subject.

## What would change this decision

- **srt reporting a denied read as ENOENT on macOS too**, which would make the platforms agree
  and remove the reason the fault was invisible. It would not remove the reason for the single
  spawn builder.
