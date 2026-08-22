#!/usr/bin/env bash
# One-shot setup for a Claude Code cloud container: single-user Nix, the devenv CLI, the
# GitHub-free devenv input, and a warm build. Idempotent — safe to re-run; every step skips
# fast when already done. Laptops and CI never need this: they install devenv normally and
# use the committed devenv.yaml (see AGENTS.md Bootstrap).
#
# Also the SessionStart hook (.claude/settings.json): with --hook it only refreshes
# devenv.local.yaml — never installs, never builds — and exits quietly if Nix is absent.
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
echo "setup: done — use 'devenv shell -- <task>' (check / build / verify ...)"
