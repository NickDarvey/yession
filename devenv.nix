{ pkgs, ... }:

# The Yession dev environment and task interface. Toolchain (Node 24, .NET SDK 10) from the
# pinned nixpkgs; tasks are devenv `scripts` (no separate task runner). The native
# node-datachannel WebRTC addon is built by Nix and linked into node_modules by `restore`, so
# the Native test tier works here. All build/package logic lives in scripts/build.fsx (the one
# authority that the Nix package also uses); the scripts below are thin wrappers over it.
let
  node-datachannel = pkgs.callPackage ./nix/node-datachannel.nix { };
in
{
  languages.javascript.enable = true;
  languages.javascript.package = pkgs.nodejs_24;
  languages.dotnet.enable = true;
  languages.dotnet.package = pkgs.dotnet-sdk_10;

  packages = [ pkgs.git ];

  env.YESSION_NDC_ADDON = "${node-datachannel}/build/Release/node_datachannel.node";
  env.DOTNET_CLI_TELEMETRY_OPTOUT = "1";
  env.DOTNET_NOLOGO = "1";

  enterShell = ''
    export PATH="$PWD/node_modules/.bin:$PATH"
    echo "yession — tasks: restore build start dev check verify package clean  (check <caps>: Browser Ports Native Docker LiveAgent)"
  '';

  # Install deps (npm + .NET tools) and link the Nix-built node-datachannel addon.
  # --ignore-scripts keeps it deterministic (skips node-datachannel's github-bound prebuild).
  scripts.restore.exec = ''
    set -euo pipefail
    npm install --ignore-scripts
    dotnet tool restore
    if [ -n "''${YESSION_NDC_ADDON:-}" ] && [ -f "$YESSION_NDC_ADDON" ]; then
      mkdir -p node_modules/node-datachannel/build/Release
      install -m 0755 "$YESSION_NDC_ADDON" node_modules/node-datachannel/build/Release/node_datachannel.node
    fi
  '';

  # Compile everything (type-check + Fable both entries + client bundle + stylesheet).
  scripts.build.exec = ''
    set -euo pipefail
    restore
    dotnet fsi scripts/build.fsx compile
  '';

  # Start the Session Process locally (http://127.0.0.1:8080).
  scripts.start.exec = ''
    set -euo pipefail
    build
    node app/out/Main.js
  '';

  # Watch mode: recompile and restart on change.
  scripts.dev.exec = ''
    set -euo pipefail
    restore
    dotnet fable watch app/main/Yession.Host.Main.fsproj -o app/out --runWatch node app/out/Main.js
  '';

  # Run tests. Default = cheap tier. Pass capabilities as args: `check Browser`, `check Ports
  # Native`, etc. Suites self-skip when their capabilities aren't present. (Named `check`, not
  # `test`, because `test` is a shell builtin and would shadow the script.)
  scripts.check.exec = ''
    set -euo pipefail
    restore
    caps="$*"
    export YESSION_TEST_CAPS="$caps"
    dotnet build Yession.slnx

    # Browser output is needed by the host-spawning Node suites and the editor Browser E2E.
    case " $caps " in
      *" Ports "*|*" Native "*|*" Docker "*|*" LiveAgent "*|*" Browser "*)
        dotnet fable app/browser/Yession.Browser.fsproj -o app/out/browser ;;
    esac

    # Host-spawning Node suites drive the assembled npm package — stage it (compile + bundle).
    case " $caps " in
      *" Ports "*|*" Native "*|*" Docker "*|*" LiveAgent "*)
        dotnet fsi scripts/build.fsx stage 0.0.0-test ;;
    esac

    # The Node (Fable/JS) test path — always runs; self-skips suites whose caps/runtime don't match.
    dotnet fable tests/Yession.Tests/Yession.Tests.fsproj -o tests/Yession.Tests/out
    dotnet fsi scripts/run-tests.fsx tests/Yession.Tests/out/Main.js 240000

    # The .NET CLR (Playwright) test path — only when a Browser-tagged suite is enabled.
    case " $caps " in
      *" Browser "*)
        ./node_modules/.bin/esbuild app/out/browser/EditorHarness.js --bundle --format=esm --outfile=tests/browser/out/harness.js
        dotnet run --project tests/Yession.Tests/Yession.Tests.fsproj ;;
    esac
  '';

  # The FULL verification gate (every capability + real-browser E2E). Gates releases.
  scripts.verify.exec = ''check Browser Ports Native Docker LiveAgent'';

  # Assemble the npm package (one package, two bins) and npm-pack it. Usage: package 1.2.3
  # restore first so the boot smoke can load the native addon from node_modules.
  scripts.package.exec = ''
    set -euo pipefail
    restore
    dotnet fsi scripts/build.fsx package "''${1:-0.0.0-dev}"
  '';

  # Remove build artifacts and installed dependencies.
  scripts.clean.exec = ''
    set -euo pipefail
    rm -rf node_modules app/out tests/Yession.Tests/out dist
    find . -type d -name bin -prune -exec rm -rf {} +
    find . -type d -name obj -prune -exec rm -rf {} +
    find . -type d -name fable_modules -prune -exec rm -rf {} +
  '';
}
