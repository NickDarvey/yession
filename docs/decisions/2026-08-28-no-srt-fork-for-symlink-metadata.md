# Declare what a sandbox needs, rather than fork srt to allow symlink metadata

> Decided 2026-08-28 · Supersedes the fork carried between #345 and #356 · Related:
> [GAPS.md](../GAPS.md) "Sandboxes confine by default", [yession.yaml](../../yession.yaml)
> `env:`, [package.json](../../package.json) `@anthropic-ai/sandbox-runtime`

## Decision

We depend on stock `@anthropic-ai/sandbox-runtime` and accept that a symlink INSIDE a granted
directory stays denied. What a sandbox actually needed from the unreadable file is declared
instead — for this repository's own build, `NIX_CONFIG` as an env leaf in `yession.yaml`.

The fork that fixed the symlink case is reverted, not shelved. It worked, and it broke
something else.

## The limitation this leaves standing

Since #330 a resource must name the path the kernel will check, which fixed a grant written as
`/etc/ssl/cert.pem`. It does nothing for a symlink one level further in. On a nix-darwin host
`/private/etc/nix/nix.conf` is a link to `/etc/static/nix/nix.conf`, whose own path starts at
the denied `/etc` node — so granting `/private/etc/nix` yields a directory the sandbox can
list and a file it cannot read (`Operation not permitted`).

Resolving every link under every granted tree is not a policy this code can compute. The
honest fix is srt allowing the symlink nodes on the way to a granted path.

## What was built, and why it is gone

A fork of srt 0.0.67 admitting `file-read-metadata` on SYMLINK vnodes. It does work:
`nix.conf` becomes readable by both spellings, and a link to a target nothing grants still
stays denied.

It also makes `/run` traversable, and on a nix-darwin host that changes what `command -v nix`
resolves to — from the working Lix at `/nix/var/nix/profiles/default/bin/nix` to
`/run/current-system/sw/bin/nix`, which aborts under Seatbelt. Measured both ways:

| Build | Reading `nix.conf` | Running `nix` |
|---|---|---|
| Stock srt | denied | works (Lix) |
| Forked srt | works, both spellings | `Abort trap: 6`, exit 134 |

So the fork trades a file nobody can read for a binary nobody can run, on the same host. That
is not an improvement, and a fork of a confinement dependency is expensive to hold besides:
every upgrade has to re-argue it.

## Why it was also unnecessary

What the sandbox wanted from `nix.conf` was two experimental-features flags, and those are a
fact about THIS repository's build rather than about anybody's machine — so they belong in
`yession.yaml`, which is where a repo says what its sandboxes need:

```yaml
env:
  NIX_CONFIG: "experimental-features = nix-command flakes"
```

On UNPATCHED srt, with that leaf, `nix --version`, `nix store ping` and `nix eval` all work.
The limitation stands and costs nothing here.

Note the shape of the argument, because it generalises: a sandbox that cannot READ a config
usually does not need the file, it needs what the file would have said. Declaring the latter
is narrower than granting the former, and it works on every host rather than the ones whose
paths happen to be spelled the same way.

## What would change this decision

- **srt allowing symlink nodes on the way to a granted path**, upstream — the same one-line
  widening `/usr/bin/git` → `/var/select` wants. Then this ADR is obsolete rather than wrong.
- **A need that cannot be re-expressed as a declaration.** If some sandbox genuinely must read
  a linked file whose content nobody can restate, the trade above is worth re-measuring.

If it is ever worth carrying a patch again, prefer a different shape: emit BOTH spellings of
an allow — the written form beside the canonical one — which fixes the mismatch at the paths
an operator actually granted, instead of widening metadata host-wide. That was never built.
