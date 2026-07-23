#!/usr/bin/env bash
# Generate devenv.local.yaml so `devenv` runs without any GitHub fetch.
#
# devenv's generated flake normally pulls `github:cachix/devenv`, which Claude Code cloud
# sandboxes block (the GitHub proxy scopes archive fetches to attached repos, and `add_repo`
# refuses cross-owner repos). This repoints the `devenv` input at devenv's OWN source
# substituted from cache.nixos.org — no GitHub. On a laptop / in CI the committed devenv.yaml
# (normal github input) is used, so this script no-ops there.
#
# Runs from the SessionStart hook (.claude/settings.json); safe and idempotent to run by hand.
set -euo pipefail

# Only in Claude Code remote sandboxes (where GitHub flake fetches are blocked).
if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

# Nix is installed by the environment setup script; if it isn't ready yet, skip quietly.
if ! command -v nix >/dev/null 2>&1; then
  echo "devenv-local: nix not on PATH yet; skipping (install nix in the env setup script)"
  exit 0
fi

dir="${CLAUDE_PROJECT_DIR:-$(pwd)}"
nixpkgs="https://channels.nixos.org/nixos-unstable/nixexprs.tar.xz"

# devenv's source, substituted from cache.nixos.org — same nixpkgs the devenv CLI comes from,
# so the module version matches. If it can't be resolved (proxy not ready), skip without error.
if ! src="$(nix build --no-link --print-out-paths "${nixpkgs}#devenv.src" 2>/dev/null)"; then
  echo "devenv-local: could not resolve devenv.src from cache; skipping"
  exit 0
fi

printf 'inputs:\n  devenv:\n    url: path:%s?dir=src/modules\n' "$src" > "$dir/devenv.local.yaml"
echo "devenv-local: wrote $dir/devenv.local.yaml (devenv input -> $src)"
