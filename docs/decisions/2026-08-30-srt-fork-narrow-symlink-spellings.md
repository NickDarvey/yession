# Fork srt to close grants over their spellings, narrowly, so .NET builds run sandboxed

> Decided 2026-08-30 · Supersedes
> [2026-08-28-no-srt-fork-for-symlink-metadata](2026-08-28-no-srt-fork-for-symlink-metadata.md)
> by its own "what would change this decision" clause · Related: [GAPS.md](../GAPS.md)
> "Sandboxes confine by default", [package.json](../../package.json)
> `@anthropic-ai/sandbox-runtime`

## Decision

We depend on `NickDarvey/sandbox-runtime#srt-0.0.67-symlink`, a fork of srt 0.0.67 whose
macOS profile generator differs from stock in two mechanisms, both built to be upstreamed:

- **Spelling closure.** Every configured path — read, write and socket allows, AND denies —
  is emitted in every spelling that names the same object: the as-written form, its realpath
  (guarded by srt's own `isSymlinkOutsideBoundary`, so a hostile link cannot steer the
  closure), and the fixed macOS `/private` pair map in both directions. The kernel consults
  DIFFERENT spellings per operation — `bind(2)` the canonical, lstat of a link node the
  as-written — so a rule in one spelling governed only half the syscalls reaching its object.
- **Per-node symlink metadata.** `file-read-metadata` is admitted on exactly the symlink
  components a configured allow is written through (`/tmp` for a grant under `/tmp/...`),
  walked at profile-generation time. Never per vnode-type: lstat of a link en route to
  nothing granted stays denied.

## Why the 2026-08-28 revert was right, and does not apply here

The reverted fork allowed `(vnode-type SYMLINK)` — metadata on every symlink on the host.
That made `/run` traversable, which flipped `command -v nix` to a binary that aborts under
Seatbelt. Measured on this fork: `stat /run`, `ls /run/current-system/sw/bin` and running
that nix behave IDENTICALLY to stock srt, because no grant here is spelled through `/run`.
The hazard was a property of the breadth, and the breadth is gone. The old ADR's closing
paragraph asked for exactly this shape ("emit BOTH spellings of an allow ... instead of
widening metadata host-wide. That was never built."). Now it is.

## What this buys, all measured on this host

`dotnet build` of this repository's solution — parallel, out-of-proc MSBuild nodes and all —
succeeds inside a real srt sandbox. The wall had three layers, each independently required:

1. MSBuild worker nodes bind unix sockets at `/tmp/MSBuild<pid>` — hardcoded upstream
   (`NamedPipeUtil.cs`, a deliberate dodge of the 104-byte `sun_path` limit), spelled
   through the `/tmp` symlink. Stock srt denies the lstat at that node.
2. The bind also needs `network.allowUnixSockets` covering the path.
3. And `file-write*` on the containing directory in the spelling the kernel checks
   (`/private/tmp`), which a grant written `/tmp` did not produce before the closure.

Every public report of this failure (anthropics/claude-code#39257, closed not-planned)
tried single knobs and concluded the knobs were broken; a three-layer wall where every
switch operates one layer reads that way. The ecosystem workaround is
`dangerouslyDisableSandbox` for all dotnet verbs. This fork retires that trade.

## What it costs, stated

- **Shared `/tmp` is a channel.** A sandbox granted `/tmp` write shares that sticky
  directory with every other sandbox and the host (MSBuild pipes, `/tmp/.dotnet/shm`).
  Accepted for this lightweight backend: srt is the fast, low-ceremony sandbox. Work that
  needs real mutual isolation belongs on the container backend (Docker), where `/tmp` is
  per-container — that, not more Seatbelt, is the proper fix.
- **MSBuild node reuse leaks across sandboxes**, because reused workers outlive their build
  in shared `/tmp` under the profile they were born with, and the reuse handshake does not
  encode sandbox identity. `MSBUILDDISABLENODEREUSE=1` rides with the dotnet resource so no
  worker survives its build. (Also measured: a stale reused node wedged a later build's
  scheduler for four hours.)
- **A fork of a confinement dependency.** Bounded as before: pinned to the 0.0.67 tag,
  moved only deliberately, and built to be a PR — the upstreaming retires it.

## Verification

The fork's own suite (623 pass, including upstream's hostile-symlink boundary cases, which
caught and killed an unguarded-realpath variant of the closure during development), a
seven-case profile/runtime suite in the fork, and this repository's `Srt`-tier cases:
spellings of one grant agree, `stat /tmp/` answers when a grant is spelled through it, a
policy-granted unix socket binds, and lstat of a link en route to nothing stays refused —
the last is red on the broad fork, the first three on stock srt.
