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
  # nix.conf FIRST: the installer runs as root with no nixbld group and aborts at its final
  # profile step unless build-users-group is explicitly empty (written to the user conf, and
  # to /etc as belt-and-braces). sandbox=false because some containers lack the namespaces
  # the build sandbox needs. Then the single-user install — idempotent, so a half-failed
  # earlier attempt is repaired by simply re-running it.
  if ! [ -e "$nix_sh" ]; then
    mkdir -p "$HOME/.config/nix" /etc/nix
    printf 'experimental-features = nix-command flakes\nbuild-users-group =\nsandbox = false\n' \
      > "$HOME/.config/nix/nix.conf"
    grep -q '^build-users-group' /etc/nix/nix.conf 2>/dev/null \
      || echo 'build-users-group =' >> /etc/nix/nix.conf
    sh <(curl -L https://nixos.org/nix/install) --no-daemon
  fi

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
if src="$(nix build --no-link --print-out-paths "${nixpkgs}#devenv.src" 2>/dev/null)"; then
  printf 'inputs:\n  devenv:\n    url: path:%s?dir=src/modules\n' "$src" > "$repo/devenv.local.yaml"
  echo "setup: wrote $repo/devenv.local.yaml (devenv input -> $src)"
else
  echo "setup: could not resolve devenv.src from cache (proxy not ready?); skipping devenv.local.yaml"
fi

$hook_only && exit 0

# The devenv CLI itself, on PATH permanently — same nixpkgs the input resolution above uses.
command -v devenv >/dev/null 2>&1 \
  || nix profile add "${nixpkgs}#devenv" 2>/dev/null \
  || nix profile install "${nixpkgs}#devenv"

# Warm everything: dev shell, offline node_modules, dotnet tools, full build.
cd "$repo"
devenv shell -- build
echo "setup: done — use 'devenv shell -- <task>' (check / build / verify ...)"
