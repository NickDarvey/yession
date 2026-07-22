{
  # A thin bridge: it re-exposes the build outputs DECLARED in devenv.nix as flake packages,
  # so consumers get `nix build .#yession` / `nix profile install github:NickDarvey/yession` /
  # `nix run`. It declares no environment of its own — devenv.nix is the single source of truth
  # for the toolchain, the node-datachannel addon, and the outputs (installed / packaged).
  #
  # nixpkgs is a nixos.org channel tarball (not `github:NixOS/nixpkgs`) so it resolves under
  # Claude Code's default-Trusted network policy. The `devenv` input is GitHub — fine for
  # consumers; inside the github-restricted sandbox, build the outputs with the devenv CLI
  # (`devenv build outputs.installed`) instead, or `nix build .# --override-input devenv
  # "path:$(nix build --print-out-paths <nixpkgs>#devenv.src)"`.

  description = "Yession — local-first runtime where humans and AI agents collaborate in a shared session.";

  inputs = {
    nixpkgs.url = "https://channels.nixos.org/nixos-unstable/nixexprs.tar.xz";
    devenv.url = "github:cachix/devenv";
  };

  outputs = { self, nixpkgs, devenv, ... }@inputs:
    let
      systems = [ "x86_64-linux" "aarch64-linux" "x86_64-darwin" "aarch64-darwin" ];
      forAllSystems = nixpkgs.lib.genAttrs systems;
      # The devenv environment for a system — carries the evaluated config (incl. its outputs).
      shellFor = system: devenv.lib.mkShell {
        inherit inputs;
        pkgs = nixpkgs.legacyPackages.${system};
        modules = [ ./devenv.nix ];
      };
    in
    {
      packages = forAllSystems (system:
        let out = (shellFor system).config.outputs;
        in {
          default = out.installed;   # `yession` + `yession-session`
          yession = out.installed;
          packaged = out.packaged;   # the npm tarball
        });

      apps = forAllSystems (system: {
        default = {
          type = "app";
          program = "${(shellFor system).config.outputs.installed}/bin/yession";
        };
      });

      # `nix develop` gives the same devenv environment (no separate declaration).
      devShells = forAllSystems (system: {
        default = shellFor system;
      });
    };
}
