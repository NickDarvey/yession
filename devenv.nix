{ pkgs, ... }:

# The Yession dev environment. Toolchain (Node 24, .NET SDK 10) from the pinned nixpkgs;
# `just` is the task runner. The native node-datachannel WebRTC addon is built by Nix and
# linked into node_modules by `just restore`, so the Native test tier works here.
let
  node-datachannel = pkgs.callPackage ./nix/node-datachannel.nix { };
in
{
  languages.javascript.enable = true;
  languages.javascript.package = pkgs.nodejs_24;
  languages.dotnet.enable = true;
  languages.dotnet.package = pkgs.dotnet-sdk_10;

  packages = [ pkgs.just pkgs.git ];

  env.YESSION_NDC_ADDON = "${node-datachannel}/build/Release/node_datachannel.node";
  env.DOTNET_CLI_TELEMETRY_OPTOUT = "1";
  env.DOTNET_NOLOGO = "1";

  enterShell = ''
    export PATH="$PWD/node_modules/.bin:$PATH"
  '';
}
