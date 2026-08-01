# The same derivations as flake.nix, evaluated against the WORKING TREE.
#
# Every other route into nix/packages.nix goes through a flake, and a flake always builds a
# store COPY of its source first: `nix build .#yession` copies what git tracks (ignored files
# never reach it), and `devenv build outputs.…` / `path:.` copy the directory whole — hundreds
# of megabytes of build output on every eval. Neither is what a developer wants to check, and
# the first is why the `src` filter could silently rot: no CI job builds a working tree, so
# nothing noticed that a dev shell's `node_modules` symlink was landing in the derivation.
#
# `--file` evaluates in place: nothing is copied but the filtered `src` itself. Usage:
#
#   nix build --file nix/worktree.nix nix        # the installable, from the tree as it stands
#   nix build --file nix/worktree.nix nugetDeps  # re-derive the NuGet FOD hash
#   nix eval  --file nix/worktree.nix staged.src # what the build actually sees
#
# nixpkgs is the one flake.lock pins, read straight out of the lock rather than re-pinned here,
# so this route and `nix build .#yession` are the same nixpkgs by construction. `narHash` is the
# hash of the unpacked tree, which is exactly what `fetchTarball` wants — so this stays pure
# (no `--impure`) and hits the store copy the flake route already fetched.
let
  lock = builtins.fromJSON (builtins.readFile ../flake.lock);
  nixpkgs = lock.nodes.${lock.nodes.root.inputs.nixpkgs}.locked;
  pkgs = import (builtins.fetchTarball {
    url = nixpkgs.url;
    sha256 = nixpkgs.narHash;
  }) { };
in
import ./packages.nix { inherit pkgs; }
