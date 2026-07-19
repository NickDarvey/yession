{
  # Nix flake: a pure, reproducible dev toolchain for Yession.
  #
  # The repo interface is `mise run <task>` (see mise.toml). Normally mise is
  # bootstrapped with `curl | sh` and downloads its own pinned Node/.NET — both
  # impure. This flake provides Node, the .NET SDK, and mise itself from nixpkgs,
  # and sets MISE_DISABLE_TOOLS so mise defers to the nix-provided toolchain
  # instead of fetching anything. The documented `mise run build|test|verify`
  # commands work unchanged, with nothing curled or downloaded out of band.
  #
  #     nix develop        # enter the shell, then: mise run build
  #
  # Toolchain matches mise.toml's pins by major version (.NET 10.0.301 exactly,
  # Node 24.x). Bump the nixpkgs pin below to move them.

  description = "Yession — local-first runtime where humans and AI agents collaborate in a shared session.";

  inputs = {
    # Pinned to a nixpkgs commit that carries dotnet-sdk_10 = 10.0.301 (the
    # mise.toml pin) and nodejs_24. Reproducible without a flake.lock; still
    # `nix flake update`-able.
    nixpkgs.url = "github:NixOS/nixpkgs/6368bc923cec55a5f78960ade0cb4dd99580e087";
  };

  outputs = { self, nixpkgs }:
    let
      systems = [ "x86_64-linux" "aarch64-linux" "x86_64-darwin" "aarch64-darwin" ];
      forAllSystems = f: nixpkgs.lib.genAttrs systems (system: f nixpkgs.legacyPackages.${system});
    in
    {
      devShells = forAllSystems (pkgs: {
        default = pkgs.mkShell {
          packages = [
            pkgs.nodejs_24
            pkgs.dotnet-sdk_10
            pkgs.mise
            pkgs.git
          ];

          # mise reads mise.toml's [tools] (node, dotnet). Disabling those two
          # tools makes mise use whatever's on PATH — i.e. the nix-provided
          # Node and .NET — rather than installing its own pinned copies.
          MISE_DISABLE_TOOLS = "node,dotnet";

          DOTNET_CLI_TELEMETRY_OPTOUT = "1";
          DOTNET_NOLOGO = "1";
          DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1";

          shellHook = ''
            # Trust this repo's mise config non-interactively (no prompt).
            mise trust --quiet "$PWD/mise.toml" 2>/dev/null || true

            echo "yession dev shell — toolchain from nix (no mise bootstrap needed)"
            echo "  node    $(node --version)"
            echo "  dotnet  $(dotnet --version)"
            echo "  mise    $(mise --version | head -n1)"
            echo "Run the repo tasks: mise run restore | build | test | verify"
          '';
        };
      });

      formatter = forAllSystems (pkgs: pkgs.nixpkgs-fmt);
    };
}
