{
  # Installable packages and a dev shell for Yession.
  #
  # `packages.*` are built directly from nix/packages.nix — NO devenv involved — so
  # `nix build .#yession` / `nix profile install github:NickDarvey/yession` / `nix run` are
  # pure and resolve with only the nixpkgs input (no `github:cachix/devenv`, no DEVENV_ROOT).
  # `devShells.default` is the devenv environment (for `nix develop`); it, and only it, uses the
  # `devenv` input. devenv.nix imports the same nix/packages.nix, so the two never diverge.
  #
  # nixpkgs is a nixos.org channel tarball (not `github:NixOS/nixpkgs`) so it resolves under
  # Claude Code's default-Trusted network policy.

  description = "Yession — local-first runtime where humans and AI agents collaborate in a shared session.";

  inputs = {
    nixpkgs.url = "https://channels.nixos.org/nixos-unstable/nixexprs.tar.xz";
    devenv.url = "github:cachix/devenv";
  };

  outputs = { self, nixpkgs, devenv, ... }@inputs:
    let
      systems = [ "x86_64-linux" "aarch64-linux" "x86_64-darwin" "aarch64-darwin" ];
      forAllSystems = nixpkgs.lib.genAttrs systems;
      packagesFor = system: import ./nix/packages.nix {
        pkgs = nixpkgs.legacyPackages.${system};
      };
    in
    {
      packages = forAllSystems (system:
        let p = packagesFor system;
        in {
          default = p.installed;   # `yession-manager` + `yession-session`
          yession = p.installed;
          packaged = p.packaged;   # the npm tarball
        });

      apps = forAllSystems (system: {
        default = {
          type = "app";
          program = "${(packagesFor system).installed}/bin/yession-manager";
        };
      });

      # `nix develop` gives the devenv environment (the only output that uses the devenv input).
      devShells = forAllSystems (system: {
        default = devenv.lib.mkShell {
          inherit inputs;
          pkgs = nixpkgs.legacyPackages.${system};
          modules = [ ./devenv.nix ];
        };
      });
    };
}
