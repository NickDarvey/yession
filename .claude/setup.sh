#!/usr/bin/env bash
# One-shot setup for a Claude Code cloud container: single-user Nix, the devenv CLI, the
# GitHub-free devenv input, and a warm build. Idempotent — safe to re-run; every step skips
# fast when already done. Laptops and CI never need this: they install devenv normally and
# use the committed devenv.yaml (see AGENTS.md Bootstrap).
#
# Also the SessionStart hook (.claude/settings.json): with --hook it refreshes
# devenv.local.yaml and settles devenv.lock — never installs, never builds — and exits
# quietly if Nix is absent.
set -euo pipefail

hook_only=false
[ "${1:-}" = "--hook" ] && hook_only=true

# Only for Claude Code remote sandboxes; everywhere else this script has no business running.
if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  $hook_only || echo "setup: not a Claude Code remote sandbox; nothing to do"
  exit 0
fi

# The container leaves USER unset, which makes nix.sh a silent no-op; and nix must trust the
# sandbox proxy's CA to fetch anything.
export USER="${USER:-$(id -un)}"
export NIX_SSL_CERT_FILE=/root/.ccr/ca-bundle.crt
export https_proxy="${https_proxy:-${HTTPS_PROXY:-}}"

repo="$(cd "$(dirname "$0")/.." && pwd)"
nix_sh="$HOME/.nix-profile/etc/profile.d/nix.sh"
nixpkgs="https://channels.nixos.org/nixos-unstable/nixexprs.tar.xz"

if ! $hook_only; then
  # The container runs as root with no nixbld group — the installer aborts at its final
  # profile step unless build-users-group is explicitly empty, so write nix.conf first.
  # Idempotent: a half-failed earlier install is repaired by simply re-running this.
  if ! [ -e "$nix_sh" ]; then
    mkdir -p /etc/nix
    grep -q '^build-users-group' /etc/nix/nix.conf 2>/dev/null \
      || echo 'build-users-group =' >> /etc/nix/nix.conf
    sh <(curl -L https://nixos.org/nix/install) --no-daemon
  fi
  mkdir -p "$HOME/.config/nix"
  grep -q '^experimental-features' "$HOME/.config/nix/nix.conf" 2>/dev/null \
    || echo 'experimental-features = nix-command flakes' >> "$HOME/.config/nix/nix.conf"

  # Future shells get nix + devenv without any ceremony. Fresh Claude Code Bash calls read
  # no rc file, so rc snippets aren't enough — PATH-level wrappers carry the env instead.
  for rc in "$HOME/.bashrc" "$HOME/.profile"; do
    grep -qs 'written by .claude/setup.sh' "$rc" || cat >> "$rc" <<'RC'
# Nix in a Claude Code cloud container (written by .claude/setup.sh)
export USER="${USER:-$(id -un)}"
export NIX_SSL_CERT_FILE=/root/.ccr/ca-bundle.crt
[ -e "$HOME/.nix-profile/etc/profile.d/nix.sh" ] && . "$HOME/.nix-profile/etc/profile.d/nix.sh"
RC
  done
  for tool in nix devenv; do
    cat > "/usr/local/bin/$tool" <<WRAP
#!/usr/bin/env bash
# Wrapper written by .claude/setup.sh: the env nix needs in this container.
export USER="\${USER:-\$(id -un)}"
export NIX_SSL_CERT_FILE=/root/.ccr/ca-bundle.crt
export https_proxy="\${https_proxy:-\${HTTPS_PROXY:-}}"
# ...and no_proxy must be CLEARED, which is not an oversight to fix later.
#
# Every egress from this container is TLS-intercepted, and the proxy's CA lives in the
# SYSTEM trust store. A Nix build cannot see that store: a fixed-output derivation runs
# sandboxed with SSL_CERT_FILE pinned to nixpkgs' \`cacert\` (Mozilla roots only), and no
# impure env var can override it from out here. So a builder that connects DIRECT gets the
# interception certificate, fails to validate it, and dies with curl 60.
#
# Going through the proxy is the path that works: it CONNECTs straight through to hosts like
# registry.npmjs.org without intercepting them, so the real certificate arrives and the
# Mozilla roots accept it. The ambient no_proxy — which names registry.npmjs.org — pushes
# builders off exactly that path, and this wrapper inherits the caller's environment, so it
# has to be unset rather than merely not set.
#
# The symptom this fixes is \`fetchNpmDeps\` being unable to fetch anything, which made
# adding one npm dependency look impossible in this container.
unset no_proxy NO_PROXY
export PATH="\$HOME/.nix-profile/bin:\$PATH"
exec "\$HOME/.nix-profile/bin/$tool" "\$@"
WRAP
    chmod +x "/usr/local/bin/$tool"
  done
fi

if ! [ -e "$nix_sh" ]; then
  echo "setup: nix not installed yet; run .claude/setup.sh (without --hook) first"
  exit 0
fi
. "$nix_sh"

# devenv.local.yaml repoints the devenv input at devenv's OWN source substituted from
# cache.nixos.org, because the sandbox proxy blocks the github:cachix/devenv fetch the
# generated flake would otherwise make. Gitignored; laptop/CI use the committed devenv.yaml.
# A pin needs a GC ROOT, not just a path. This built with `--no-link`, which roots nothing:
# the source sat on the collector's dead list from the moment its path was written into the
# lock. One `nix-collect-garbage` — or nix collecting on its own when this container's fixed
# disk allowance runs low — and every `devenv shell -- <task>` in the repo dies with
# `error: path '/nix/store/…-source' is not valid`, about a file devenv was TOLD to use and
# nothing was keeping. `--out-link` registers an indirect root under /nix/var/nix/gcroots/auto,
# so what the lock points at lives exactly as long as the link does.
mkdir -p "$repo/.devenv"
pinned() { sed -n 's|.*url: path:\(/nix/store/[^?]*\).*|\1|p' "$repo/devenv.local.yaml" 2>/dev/null; }
if src="$(nix build --out-link "$repo/.devenv/devenv-src" --print-out-paths "${nixpkgs}#devenv.src" 2>/dev/null)"; then
  printf 'inputs:\n  devenv:\n    url: path:%s?dir=src/modules\n' "$src" > "$repo/devenv.local.yaml"
  echo "setup: wrote $repo/devenv.local.yaml (devenv input -> $src, rooted at .devenv/devenv-src)"
elif prev="$(pinned)" && [ -n "$prev" ] && ! nix path-info "$prev" >/dev/null 2>&1; then
  # Nothing resolved AND what a previous session wrote is gone. A dead pin is worse than no
  # pin: devenv answers every command with nix's `not valid` about a path nobody can explain,
  # where the committed input at least fails saying what it could not fetch.
  rm -f "$repo/devenv.local.yaml"
  echo "setup: could not resolve devenv.src, and the pin from a previous session ($prev) is gone; removed devenv.local.yaml"
else
  echo "setup: could not resolve devenv.src from cache (proxy not ready?); keeping the existing devenv.local.yaml"
fi

# devenv rewrites devenv.lock on every run, re-adding a `devenv` node that names THIS
# container's /nix/store path (see .gitignore for why the committed lock carries nixpkgs only).
# That left a tracked file permanently modified: noise in every `git status`, and one
# `git commit -a` away from pinning the repo's devenv input to a path no other checkout has.
#
# A clean filter states what is actually true — that node is not part of the file's TRACKED
# content — so git hashes the working copy without it and the file matches HEAD. Nothing to
# see, and nothing to stage by accident once it is installed. Local to this clone
# (`.git/info/attributes`, never the committed `.gitattributes`), so a laptop or CI checkout is
# untouched by any of it. An upstream nixpkgs bump still shows, still merges.
#
# What this canNOT do is hold the rule, and it was believed to. It is installed by THIS script,
# which runs at session start; a container whose session began before the filter existed has a
# tracked file devenv rewrites and no filter to hash it through. Two commits went out of one
# such window with the store path in them. `required` below closes the case where the filter
# runs and fails; absent is a third state and it is fail-open by construction. The rule itself
# is held by `tests/Yession.Tests/LockSource.fs`, which reads the committed lock on every pull
# request and cannot be missing from anybody's clone.
mkdir -p "$repo/.git/info"
grep -qs '^devenv\.lock filter=devenv-lock$' "$repo/.git/info/attributes" \
  || echo 'devenv.lock filter=devenv-lock' >> "$repo/.git/info/attributes"
git -C "$repo" config filter.devenv-lock.clean "python3 -c 'import json,sys; d=json.load(sys.stdin); d[\"nodes\"].pop(\"devenv\",None); d[\"nodes\"].get(\"root\",{}).get(\"inputs\",{}).pop(\"devenv\",None); json.dump(d,sys.stdout,indent=2); sys.stdout.write(chr(10))'"
# A clean filter that FAILS is IGNORED by default: git prints `error: external filter ...
# failed` into the middle of its output and stages the unfiltered content anyway — the store
# path, in the index, from a run that looked like it worked. That is the same bug arriving by
# a different route, and the route is real: this runs before Nix exists, so the filter's
# `python3` is whatever the image has. `required` makes the failure fatal instead, so the
# guard fails closed rather than open.
#
# It governs BOTH directions, and an UNDEFINED smudge counts as a failure — so the identity
# one has to be spelled out. Without it, `required` turns every checkout of this file into
# `fatal: devenv.lock: smudge filter devenv-lock failed` and the file is not written at all.
# `cat` writes exactly what git already wrote when no smudge was defined.
git -C "$repo" config filter.devenv-lock.smudge cat
git -C "$repo" config filter.devenv-lock.required true

# Git re-hashes through the filter but does not record the result, so the file keeps reporting
# as modified until something stages it. Staging it is a no-op once filtered — and it is done
# ONLY when there is no real change left after filtering, so a deliberate `devenv update` is
# left in the working tree to be reviewed rather than silently added.
#
# `devenv info` first, because the settle has to happen to a lock that ALREADY carries the
# node: settling a pristine one achieves nothing and the session's first real devenv command
# dirties it all over again. It costs ~0.1s, it is the cheapest devenv verb that resolves
# inputs (`version` does not), and once the node is there devenv leaves the file alone — so
# this runs once per container and every later command finds the tree clean.
#
# The one thing it does not cover: a `git checkout` that rewrites devenv.lock puts back the
# node-less blob, and the next devenv command re-adds the node, so the file reports modified
# again until the next session start settles it. Committing it is still impossible — the filter
# is what guarantees that, and it holds whatever the stat cache believes.
settle_lock() {
  ( cd "$repo" && command -v devenv >/dev/null 2>&1 && devenv info >/dev/null 2>&1 ) || true
  git -C "$repo" diff --quiet -- devenv.lock 2>/dev/null && git -C "$repo" add -- devenv.lock 2>/dev/null || true
}
settle_lock

$hook_only && exit 0

# The devenv CLI itself, on PATH permanently — same nixpkgs the input resolution above uses.
# Test the profile binary, not PATH: the wrapper written above shadows it, so `command -v`
# would always succeed and this install would never run.
[ -x "$HOME/.nix-profile/bin/devenv" ] \
  || nix profile add "${nixpkgs}#devenv" 2>/dev/null \
  || nix profile install "${nixpkgs}#devenv"

# Warm everything: dev shell, offline node_modules, dotnet tools, full build.
cd "$repo"
devenv shell -- build
settle_lock
echo "setup: done — use 'devenv shell -- <task>' (check / build / verify ...)"
