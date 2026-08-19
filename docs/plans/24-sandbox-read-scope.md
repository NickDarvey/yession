# Plan 24 — Scoping a sandbox's reads to a directory

> **Status: investigation. Nothing implemented.** Every claim below was produced by RUNNING
> `@anthropic-ai/sandbox-runtime` 0.0.67 (the pinned version) against real bubblewrap, or by
> reading the shipped implementation where this box cannot run it (macOS). Where a claim is
> code-read rather than executed, it says so.

## The gap

A WorkSandbox confines WRITES to a list of paths and confines READS to one rule: the
invoking user's home is denied. `Sandboxes.configFor`:

```fsharp
{ DenyRead = home |> Option.toList
  AllowRead = distinct (policy.ReadPaths @ policy.WritePaths)
  AllowWrite = distinct (policy.WritePaths @ [ tmpDir; "/dev/stdout"; ... ]) }
```

srt's read model is deny-then-allow and *maximally permissive by default*
(`FsReadRestrictionConfig`: `undefined` or `denyOnly: []` means allow every read). So
everything that is not under `$HOME` is readable by any agent-issued command. Measured, in
this container, with the profile `configFor` produces today:

```
OK  cat /etc/passwd                          root:x:0:0:root:/root:/bin/bash
OK  cat /home/user/yession/CLAUDE.md          # a checkout the session was never given
OK  ls /var/log
OK  ls /                                      # every root, traversable
FAIL cat $HOME/.other-secret                  # the one thing that is denied
```

Two consequences worth naming separately, both measured:

- **Sessions can read each other.** `dataDir` defaults to `.yession` relative to the
  Manager's cwd. When the Manager does not run out of the operator's home, session *B*'s
  work sandbox reads session *A*'s `agent-home` (the CLI's `~/.claude` state), its
  `events.jsonl`, and its workspace. Probe: reading a sibling
  `sessions/other/agent-home/.credentials.json` succeeds under today's profile and fails
  under the scoped one below.
- **srt's implicit write defaults punch two holes in the denied home.**
  `getDefaultWritePaths()` unconditionally adds `~/.npm/_logs` and `~/.claude/debug` to
  `allowOnly`, and the read-deny tmpfs re-binds every allowed WRITE path it wiped
  (`pushReadDenyDirMounts`). So both directories are readable *and* writable from inside a
  work sandbox whose policy denies the home they sit in. Measured: `cat
  $HOME/.claude/debug/probe.txt` → `debug-secret`; `echo pwned > $HOME/.claude/debug/pwned`
  → succeeds. The home those defaults resolve against is `os.homedir()`, which on POSIX
  follows `$HOME` — the Session Process's own, verified with `HOME=/tmp/fakehome node -e
  'os.homedir()'` → `/tmp/fakehome`. So a scoped `HOME` on the Session Process moves them
  somewhere harmless.

The agent CLI's own sandbox has the same read rule (`AgentSandbox.policyFor` names its
scratch HOME and nothing else, and `configFor` turns that into deny-home + allow-scratch).
It matters less only because `Agent.fs` passes `tools: []` — the CLI has no built-in Read
or Bash, and every file touch goes through the session's MCP tools into the WorkSandbox. It
is still the same one-line fix, and it is defence in depth against the day that changes.

`docs/GAPS.md` says the confined CLI "reads and writes only its scratch HOME **of the
operator's files**" — accurate as written, and it is the qualifier doing the work. Nothing
records that the rest of the filesystem is readable.

## What srt can actually do

Verified against 0.0.67:

- **`denyRead: ["/"]` is a first-class shape, not an accident.** On Linux
  `generateFilesystemArgs` expands a root deny into the direct children of `/` (skipping
  `proc`, `dev`, `sys`, which are remounted by the caller), tmpfs's each, then re-binds the
  allowed write paths and `allowRead` paths the tmpfs wiped. The expansion is
  `readdirSync('/')` at WRAP time, i.e. per spawn — a root that appears later is denied
  without a config change. On macOS `generateReadRules` special-cases it too: `(deny
  file-read* (subpath "/"))` plus `(allow file-read* (literal "/"))` so `allowWithinDeny`
  subpaths stay reachable. (Linux executed here; macOS read-only — this box has no
  Seatbelt.)
- **`allowRead` beats `denyRead`** for directories; for a FILE listed in `denyRead`, only an
  exact `allowRead` match re-allows it, so `allowRead: ["/etc"]` cannot silently un-deny
  `denyRead: ["/etc/shadow"]`.
- **Globs**: `allowRead` globs are expanded on Linux; `allowWrite`/`denyWrite` globs are
  dropped there (bubblewrap has no glob support).
- **Per-spawn config wins over the config the manager was initialized with.** This is the
  one that makes any of it usable here: yession initializes srt's process-wide
  `SandboxManager` from whichever sandbox came up first and passes each sandbox's own
  config as `customConfig` per spawn. Probe — manager initialized permissive, one spawn with
  the init config and one with a scoped `customConfig`:

  ```
  init-config: ROAMS
  per-spawn:   SCOPED
  ```

  So read scope is genuinely per sandbox, exactly as write scope already is. No change to
  the singleton, and the egress union gap (GAPS) does not extend to files.

## Option A — deny `/`, allow back the runtime (recommended)

`DenyRead = ["/"]`, `AllowRead = ReadPaths @ WritePaths @ runtimePaths`.

Measured with `allowRead` = workspace + `/usr /bin /sbin /lib /lib64 /opt/node22` + the srt
package directory + `/tmp/claude` + a carved `/etc` (`ssl`, `ca-certificates`, `passwd`,
`group`, `hosts`, `resolv.conf`, `nsswitch.conf`, `localtime`, `ld.so.cache`,
`alternatives`):

```
OK  cat marker              workspace-file      OK  node --version   v22.22.2
OK  echo hi > w && cat w    hi                  OK  npm --version    10.9.7
OK  git init/commit/log     97df2b1 x           OK  python3          1
OK  tar/grep/sed/awk        all fine            OK  openssl version  OpenSSL 3.0.13
OK  ls /home                (empty)             OK  ls /root         (empty)
OK  ls /var                 (empty)             FAIL ls /home/user/yession   no such file
OK  cat /etc/shadow         no such file        OK  ls /etc          only the ten carved names
```

Cost: none measurable. Wrap+spawn of `sh -c true`, five runs each:

```
today      wrap/total ms:  22/45 22/48 20/42 20/39 17/37
deny-root  wrap/total ms:  19/43 20/46 19/43 20/45 21/49
```

Egress is untouched — the same curl probes behave identically under both profiles, so
srt's proxy plumbing does not live anywhere the root deny hides.

Two things the allow-back list MUST contain, and one of them is not obvious:

1. **srt's own package directory.** The wrapped command execs a vendored helper
   (`vendor/seccomp/<arch>/apply-seccomp`) from inside `node_modules/@anthropic-ai/
   sandbox-runtime`. Deny `/` without allowing that path and every command dies with
   `bash: .../vendor/se...: No such file or directory` — exit 127, before it runs. Under
   the Nix installable this is inside the store, so allowing the store covers it; under
   `npm i -g yession` it is the global prefix.
2. **Everything on the sandbox's `PATH`, plus what those binaries link against.** Under
   Nix that is one entry (`/nix/store`, read-only by construction); on a distro it is
   `/usr /bin /sbin /lib /lib64` and whatever `/opt` prefix holds the interpreter — the
   `node --version` failure above appeared the moment `/opt/node22` was dropped from the
   list, which is the shape of every future report of "my tool stopped working".

So the runtime list is deployment-specific and cannot be guessed by this code. It wants to
be a value: a platform default (Linux: the list above; macOS: the Seatbelt equivalent),
plus the store prefix of `process.execPath` and the srt package dir computed at boot, plus
an operator override (`YESSION_WORK_READ_PATHS` / `YESSION_AGENT_READ_PATHS`, comma- or
space-separated — the shape `YESSION_WORK_DOMAINS` already uses). Assembled in
`Sandboxes.fs` as a pure function so the cheap tier can pin it, exactly as `egressFor` is.

The failure mode is loud and local: a missing allow-back path means a command cannot find
its interpreter, not a session that silently confines nothing. That is the right direction
to be wrong in, and it is the opposite of the direction today's profile is wrong in.

## Option B — deny a curated set of roots

`denyRead: ["/home", "/root", "/srv", "/mnt", "/media", "/var", "/opt", ...]`, no
allow-back beyond what the policy already names. No runtime list to maintain, nothing to
break when a deployment installs its tools somewhere unusual.

It fails open, and structurally so: a root the list does not name is readable, the list is
static config while `denyRead: ["/"]` is re-expanded from `readdirSync('/')` at every
spawn, and the first operator to bind-mount `/projects` into the host gets no error and no
confinement. Worth having only as the escape hatch under Option A (an operator who cannot
enumerate their runtime sets `YESSION_WORK_READ_PATHS` to something broad), never as the
default.

## Option C — the docker backend

Already implemented, already the answer for anyone who wants a filesystem the host does not
share: the container sees its own userland and the repos bind mount. It is not the
sub-second path, which is why srt is the default, and it does not remove the question for
the srt backend.

## What scoping does NOT close

- **`/proc` and `/sys` stay visible** — srt's root-deny expansion skips them by design
  (`rootSkip`), and under `YESSION_SANDBOX_NESTED=weak` the host's `/proc` is not replaced.
  Probed for the obvious escapes: `/proc/1/root/...` does not resolve to the host root, and
  the Session Process's environment is not readable from inside (a marker env var on the
  parent did not appear in any `/proc/*/environ`) — bwrap gives the sandbox its own pid
  namespace, so the only environs on offer are its own. Not a bypass; still not scoped.
- **The clone sandbox.** `FilesystemConfinement.Unconfined` sets srt's
  `filesystem.disabled`, which drops the read policy AND the write policy
  (`allowOnly: ['/']`) for that spawn. Scoping reads leaves that hole exactly as GAPS
  already records it.
- **srt's default write paths** (`~/.npm/_logs`, `~/.claude/debug`) follow the Session
  Process's `HOME`, so scoping reads does not move them; a `denyWrite` closes the write but
  the read re-bind survives (the write bind re-exposes the path on top of the tmpfs and the
  deny lands as `--ro-bind path path`). The fix is the Session Process's `HOME`, which is a
  separate one-line change and worth doing whichever option lands.

## Suggested shape, if this is taken up

1. `SandboxPolicy` grows the read scope as data — a `ReadScope` of `ScopedTo of string list`
   / `UnrestrictedRead`, rather than a bare bool, so the docker and host backends can keep
   ignoring it without a second meaning for `[]`.
2. `Sandboxes.runtimeReadPaths : Map<string,string> -> string list` — platform default +
   `process.execPath` prefix + srt package dir + operator override. Pure, cheap-tier tested.
3. `configFor` maps a scoped policy to `DenyRead = ["/"]`, `AllowRead = read @ write @
   runtime`. One place, both sandboxes.
4. `SrtIntegration` gains the denial cases the probes above already are: a checkout outside
   the policy is unreadable, a sibling session's `agent-home` is unreadable, `/etc/shadow`
   is unreadable, and a plain command still runs. They are denial assertions, which is what
   that suite is for.
5. GAPS: replace "reads and writes only its scratch HOME of the operator's files" with what
   is actually true, and record `/proc`, `/sys` and the clone sandbox as the residue.

The whole of step 3 is two lines; steps 1, 2 and 4 are where the work is, and step 2 is the
only one with a judgement call in it.
