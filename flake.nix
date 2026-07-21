{
  # Nix flake for Yession — the reproducible dev toolchain and an installable package.
  #
  # `just` is the task runner (see the justfile); the devShell puts Node, the .NET SDK,
  # and just on PATH. `nix develop` (or `direnv`) gives an environment identical on a
  # laptop and in Claude Code cloud sessions.
  #
  #     nix develop          # enter the shell
  #     just                 # list tasks
  #     just build           # build everything
  #     just test Browser    # run tests with capabilities
  #
  # nixpkgs is pinned to a nixos.org channel tarball rather than `github:NixOS/nixpkgs`
  # on purpose: `*.nixos.org` is reachable from Claude Code's default-Trusted network
  # policy, while arbitrary github flake inputs are scoped to session-attached repos.
  # The tarball resolves to an exact releases.nixos.org revision + narHash in flake.lock,
  # so it is just as reproducible as a git pin — and needs no github egress.

  description = "Yession — local-first runtime where humans and AI agents collaborate in a shared session.";

  inputs = {
    nixpkgs.url = "https://channels.nixos.org/nixos-unstable/nixexprs.tar.xz";
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
            pkgs.nodejs_24        # 24.x — Browser Client + Session Process both run on Node
            pkgs.dotnet-sdk_10    # 10.0.301 — F# toolchain; Fable compiles F# -> JS
            pkgs.just             # task runner (the repo interface; replaces mise)
            pkgs.git
          ];

          DOTNET_CLI_TELEMETRY_OPTOUT = "1";
          DOTNET_NOLOGO = "1";
          DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1";

          shellHook = ''
            # Tool-installed binaries (Fable via dotnet tools, esbuild/tailwind via npm)
            # go on PATH, matching the versions the build and package steps use.
            export PATH="$PWD/node_modules/.bin:$PATH"
            export DOTNET_ROOT="${pkgs.dotnet-sdk_10}/share/dotnet"
            echo "yession dev shell — node $(node --version), dotnet $(dotnet --version)"
            echo "Run tasks with just: just build | just test Browser | just start"
          '';
        };
      });

      formatter = forAllSystems (pkgs: pkgs.nixpkgs-fmt);
    };
}
