# Seed NuGet's migration marker into the sandbox's own home, rather than widen the deny

> Decided 2026-08-29 · Supersedes nothing · Related:
> [2026-08-28-no-srt-fork-for-symlink-metadata.md](2026-08-28-no-srt-fork-for-symlink-metadata.md),
> [GAPS.md](../GAPS.md) "Sandboxes confine by default", [yession.yaml](../../yession.yaml)
> `files:`, `Sandboxes.SessionLayout.prepareHome`, `HomePath`

## Decision

A repo seeds `.local/share/NuGet/Migrations/1` into its sandbox's private home with
`yession.yaml`'s `files:` key (#358). Nothing about the read deny changes, and no path of the
operator's is granted or mounted.

## The failure this fixes

.NET's named mutexes `stat("/tmp/")` — hardcoded, so redirecting `TMPDIR` does not reach it —
and the SDK takes one on first use, from `NuGet.Common.Migrations`. On macOS `/tmp` is a
symlink, so it is denied for the reason the symlink ADR above records, and `check` inside an
srt sandbox dies before compiling anything:

```
System.IO.IOException: ... 'NuGet-Migrations'. One or more system calls failed:
stat("/tmp/", ...) == -1; errno == EPERM
```

An operator cannot grant their way out of it. Declaring `/tmp` is refused as non-canonical
(#330); declaring `/private/tmp` is accepted and leaves `stat("/tmp/")` refused, measured.
`DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1` does not skip it either — set and verified present in
the sandbox's env, same failure.

## What was tried, so it is not tried again

The deny is `["/"]`, and srt denies that subpath literally on macOS, which is what catches the
link. Three variants, each measured in a real sandbox:

| Variant | `stat("/tmp/")` | `/etc/passwd` |
|---|---|---|
| Deny `/`'s non-symlink children instead | succeeds | **readable** |
| …plus deny `<link>/**` for each | refused | denied |
| …plus deny `<link>/?**` (`^/etc/[^/].*$`) | succeeds | **readable** |

Seatbelt matches the path as WRITTEN, not canonicalised, so a `/private` deny does not cover
`/etc/...` — that is the first row. The second row fails for a different reason: `globToRegex`
turns `**` into `.*`, which matches the empty string, so the trailing slash lands back inside
the deny. The third row's glob deny simply stops applying, for a reason not visible from
outside Seatbelt.

Reverted rather than shipped on the third reading: two of the three states are a leak, and the
difference between them is not understood. A confinement change nobody can explain is not one
to carry.

## The fix, which widens nothing

The mutex is only taken when NuGet's migration marker is ABSENT, so the whole path is
avoidable. The marker is not a grant — it is a file in the private home this session already
makes for the sandbox, which `prepareHome` seeds before anything runs and `HomePath` refuses
to let escape:

```yaml
files:
  ".local/share/NuGet/Migrations/1": ""
```

`MigrationRunner.GetMigrationsDirectory` resolves `$HOME/.local/share` absent an
`XDG_DATA_HOME`, and `Run` checks the marker BEFORE constructing the mutex. With it,
`dotnet --info`, `restore`, `build` and `fsi` all exit 0 and `/tmp/.dotnet` is never touched.

The marker is not a lie on a fresh HOME. `Migration1` deletes legacy `v3-cache` /
`plugins-cache` and fixes permissions on EXISTING NuGet directories; a home nothing has used
has none of that to do.

Two env settings are separate from the mutex and still wanted:

- `CLAUDE_CODE_TMPDIR` on the Session Process, or MSBuild writes its response file to the
  shared `/tmp/claude` srt bakes in by default and the compiler then cannot find it
  (`FSC error FS3194`).
- `NUGET_PACKAGES` at an operator's cache, or the sandbox's private HOME means every sandbox
  re-downloads.

An earlier version of this fix pointed `XDG_DATA_HOME` at a read-only operator directory
holding the same marker. It worked, and #358 replaced it: a file the repo seeds into a home it
already owns needs no operator resource, no mount, and no second place for the path to drift.

## Two things to know before granting your way out of it instead

- The path is `mkdtemp("/tmp/.dotnet.XXXXXX")`, so it wants write on `/tmp` ITSELF unless
  `/tmp/.dotnet` already exists — not a grant anyone should write.
- The shared-memory location is a CoreCLR PAL constant: `TMPDIR` pointed elsewhere is ignored,
  measured.

## What would change this decision

- **NuGet wrapping the mutex construction.** It wraps the migration WORK in `catch { }` but
  constructs the mutex outside every catch — it tolerates a migration that fails and not a
  mutex it cannot create. One `try` there would fix every sandboxed .NET on macOS, and delete
  the need for the marker.
- **CoreCLR honouring `TMPDIR`** for named-mutex shared memory.
