# justfile — the repository interface. Run tasks with `just <task>` inside `nix develop`.
#
# Toolchain (Node, .NET) comes from the Nix devShell (flake.nix); Fable is a .NET tool and
# esbuild/tailwind are npm devDependencies, both restored by `just restore` and put on PATH
# by the devShell. Run `just` with no args to list tasks.

# List available tasks.
default:
    @just --list

# Install all dependencies (npm + .NET tools).
#
# `--ignore-scripts` keeps restore deterministic and network-frugal: it skips native
# postinstalls (notably node-datachannel's, which downloads a per-platform prebuilt from
# GitHub releases or falls back to a cmake build). esbuild and tailwind still work — their
# binaries ship as platform packages, not build scripts. The one native runtime dependency,
# node-datachannel's WebRTC addon, is supplied by Nix for the Native test tier and the
# packaged build (see flake.nix), so it never needs npm to compile it.
restore:
    #!/usr/bin/env bash
    set -euo pipefail
    npm install --ignore-scripts
    dotnet tool restore
    # In the Nix dev shell, link the Nix-built node-datachannel addon into node_modules so the
    # Native tier and `just start` work. Outside Nix (var unset), this is a no-op — run
    # `npm rebuild node-datachannel` if you need the addon there.
    if [ -n "${YESSION_NDC_ADDON:-}" ] && [ -f "$YESSION_NDC_ADDON" ]; then
      mkdir -p node_modules/node-datachannel/build/Release
      install -m 0755 "$YESSION_NDC_ADDON" node_modules/node-datachannel/build/Release/node_datachannel.node
    fi

# Build everything: type-check (.NET), Fable-compile both entries, bundle the browser client.
build: restore
    dotnet build Yession.slnx
    dotnet fable app/main/Yession.Host.Main.fsproj -o app/out
    dotnet fable app/browser/Yession.Browser.fsproj -o app/out/browser
    esbuild app/out/browser/Browser.js --bundle --format=esm --minify --outfile=app/out/public/client.js
    # Tailwind, built locally into a served stylesheet (no CDN); scans the F# sources.
    tailwindcss -i app/tailwind.css -o app/out/public/app.css --minify

# Start the Session Process locally (http://127.0.0.1:8080).
start: build
    node app/out/Main.js

# Run the Session Process in watch mode (recompiles and restarts on change).
dev: restore
    dotnet fable watch app/main/Yession.Host.Main.fsproj -o app/out --runWatch node app/out/Main.js

# Run tests. Default = cheap tier. Pass capabilities as args: `just test Browser`,
# `just test Ports Native`, etc. Suites self-skip when their capabilities aren't present.
# (Capabilities: Browser Ports Native Docker LiveAgent — see mise history / Tags.fs.)
test *caps: restore
    #!/usr/bin/env bash
    set -euo pipefail
    caps="{{caps}}"
    export YESSION_TEST_CAPS="$caps"
    dotnet build Yession.slnx

    # The Fable-compiled browser output is needed by the host-spawning Node suites (the app
    # they run) and by the editor Browser E2E (its harness bundle) — build it once if either
    # applies.
    case " $caps " in
      *" Ports "*|*" Native "*|*" Docker "*|*" LiveAgent "*|*" Browser "*)
        dotnet fable app/browser/Yession.Browser.fsproj -o app/out/browser ;;
    esac

    # Host-spawning Node suites drive the real app entry + assembled npm package.
    case " $caps " in
      *" Ports "*|*" Native "*|*" Docker "*|*" LiveAgent "*)
        dotnet fable app/main/Yession.Host.Main.fsproj -o app/out
        esbuild app/out/browser/Browser.js --bundle --format=esm --minify --outfile=app/out/public/client.js
        tailwindcss -i app/tailwind.css -o app/out/public/app.css --minify
        dotnet fsi scripts/build.fsx 0.0.0-test ;;
    esac

    # The Node (Fable/JS) test path — always runs; self-skips suites whose caps/runtime don't match.
    dotnet fable tests/Yession.Tests/Yession.Tests.fsproj -o tests/Yession.Tests/out
    dotnet fsi scripts/run-tests.fsx tests/Yession.Tests/out/Main.js 240000

    # The .NET CLR (Playwright) test path — only when a Browser-tagged suite is enabled. The
    # editor E2E serves app/browser/EditorHarness.fs, esbuilt to tests/browser/out/harness.js.
    case " $caps " in
      *" Browser "*)
        esbuild app/out/browser/EditorHarness.js --bundle --format=esm --outfile=tests/browser/out/harness.js
        dotnet run --project tests/Yession.Tests/Yession.Tests.fsproj ;;
    esac

# Run the FULL verification gate (every capability + real-browser E2E). Gates releases.
verify:
    just test Browser Ports Native Docker LiveAgent

# Assemble the npm package (one package, two bins) and npm-pack it. Usage: just package 1.2.3
package version="0.0.0-dev": build
    dotnet fsi scripts/build.fsx {{version}}

# Remove build artifacts and installed dependencies.
clean:
    rm -rf node_modules
    rm -rf app/out tests/Yession.Tests/out
    find . -type d -name bin -prune -exec rm -rf {} +
    find . -type d -name obj -prune -exec rm -rf {} +
    find . -type d -name fable_modules -prune -exec rm -rf {} +
