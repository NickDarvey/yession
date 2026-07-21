# The installable Yession package: the Manager (`yession`) and Session Process
# (`yession-session`) as two wrapped Node executables, built reproducibly.
#
# The build mirrors `just build` + scripts/build.fsx, but offline:
#   1. nugetDeps — a fixed-output derivation that populates a NuGet global-packages cache
#      (the F# solution's packages AND the Fable dotnet tool). Network lives only here.
#   2. npmDeps — the npm cache (fetchNpmDeps) for the JS toolchain and bundled libraries.
#   3. the build proper — Fable-compile F#→JS, esbuild-bundle the two entries (native/self-
#      resolving deps kept external), Tailwind the stylesheet — all against the caches, no net.
#   4. wrap — assemble manager.js/session.js/assets + a node_modules holding the four runtime
#      externals, drop in the Nix-built node-datachannel addon, and wrap each bin so it runs on
#      the pinned Node with the agent pointed at nixpkgs claude-code (YESSION_CLAUDE_PATH).
{ lib
, stdenv
, dotnet-sdk_10
, nodejs_24
, fetchNpmDeps
, npmHooks
, cacert
, makeWrapper
, node-datachannel
, claude-code
, esbuild
, version ? "0.0.0-nix"
}:

let
  # Only the files the build actually consumes, so editing flake.nix, docs, CI, or the
  # justfile doesn't invalidate the (slow) F#/Fable build.
  src =
    let root = ../.;
    in lib.cleanSourceWith {
      src = lib.cleanSource root;
      filter = path: type:
        let rel = lib.removePrefix (toString root + "/") (toString path);
        in !(
          lib.hasPrefix "nix/" rel
          || lib.hasPrefix ".github/" rel
          || lib.hasPrefix "docs/" rel
          || rel == "flake.nix"
          || rel == "flake.lock"
          || rel == "justfile"
          || rel == ".gitignore"
          || lib.hasSuffix ".md" rel
        );
    };

  dotnetEnv = ''
    export HOME="$TMPDIR/home"
    mkdir -p "$HOME"
    export DOTNET_CLI_TELEMETRY_OPTOUT=1
    export DOTNET_NOLOGO=1
    export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
    export DOTNET_CLI_HOME="$HOME"
  '';

  # (1) NuGet global-packages cache — the only network step. Populated by restoring the
  # solution and the Fable tool; consumed offline by the build via NUGET_PACKAGES.
  nugetDeps = stdenv.mkDerivation {
    pname = "yession-nuget-deps";
    inherit version src;
    nativeBuildInputs = [ dotnet-sdk_10 cacert ];
    buildPhase = ''
      runHook preBuild
      ${dotnetEnv}
      export NUGET_PACKAGES="$out"
      mkdir -p "$out"
      # nixpkgs' dotnet drops nuget.org (its own "_nix" source stays offline); this FOD is the
      # one place allowed to fetch, so add nuget.org explicitly.
      cat > nuget.config <<'EOF'
      <?xml version="1.0" encoding="utf-8"?>
      <configuration>
        <packageSources>
          <clear/>
          <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3"/>
        </packageSources>
      </configuration>
      EOF
      dotnet restore Yession.slnx --configfile nuget.config
      dotnet tool restore --configfile nuget.config
      runHook postBuild
    '';
    # $out is already the cache; drop volatile metadata so the hash is stable.
    installPhase = ''
      runHook preInstall
      find "$out" -name '*.nupkg.metadata' -delete
      find "$out" -name '.lock' -delete
      runHook postInstall
    '';
    dontFixup = true;
    outputHashMode = "recursive";
    outputHashAlgo = "sha256";
    outputHash = "sha256-OmjxQe1sPUf2WS4Sq53pyQScARhQszVvjX80u/EgYX8=";
  };

  npmDeps = fetchNpmDeps {
    inherit src;
    name = "yession-npm-deps";
    hash = "sha256-KAlO9QDFE5XNUmM1202Oet2dicrkQotZJQIm9uzHHgQ=";
  };
in
stdenv.mkDerivation {
  pname = "yession";
  inherit version src;

  nativeBuildInputs = [
    dotnet-sdk_10
    nodejs_24
    esbuild
    npmHooks.npmConfigHook
    makeWrapper
  ];

  # npmConfigHook wires npm at the fetched cache; keep its own install off scripts (the native
  # node-datachannel build can't run here — we supply that addon from Nix).
  inherit npmDeps;
  npmFlags = [ "--ignore-scripts" ];

  buildPhase = ''
    runHook preBuild
    ${dotnetEnv}
    # npm-installed CLIs (tailwindcss) on PATH; npmConfigHook has populated node_modules.
    export PATH="$PWD/node_modules/.bin:$PATH"
    # NUGET_PACKAGES must be writable — `dotnet tool restore`/restore write lock and temp
    # files into it — so copy the read-only cache FOD into a writable dir (the Nix store is
    # read-only, which fails hard in a sandboxed build).
    export NUGET_PACKAGES="$TMPDIR/nuget-packages"
    cp -r --no-preserve=mode,ownership ${nugetDeps} "$NUGET_PACKAGES"
    # Offline NuGet: no online source, resolve everything from the pre-populated cache.
    cat > nuget.config <<'EOF'
    <?xml version="1.0" encoding="utf-8"?>
    <configuration>
      <packageSources><clear/></packageSources>
    </configuration>
    EOF

    dotnet tool restore
    dotnet build Yession.slnx --no-restore || dotnet build Yession.slnx
    dotnet fable app/main/Yession.Host.Main.fsproj -o app/out
    dotnet fable app/browser/Yession.Browser.fsproj -o app/out/browser

    # Bundle the two entries to single ESM files, keeping the native / self-resolving deps
    # external (they must load from node_modules, never bundled), matching scripts/build.fsx.
    banner='import { createRequire as __createRequire } from "module"; const require = __createRequire(import.meta.url);'
    externals="--external:node-datachannel --external:@anthropic-ai/claude-agent-sdk --external:zod --external:dockerode"
    esbuild app/out/Main.js        --bundle --platform=node --format=esm --banner:js="$banner" $externals --outfile=stage/manager.js
    esbuild app/SessionMain.js     --bundle --platform=node --format=esm --banner:js="$banner" $externals --outfile=stage/session.js
    esbuild app/out/browser/Browser.js --bundle --format=esm --minify --outfile=stage/assets/client.js
    tailwindcss -i app/tailwind.css -o stage/assets/app.css --minify
    runHook postBuild
  '';

  installPhase = ''
    runHook preInstall
    mkdir -p "$out/libexec/yession" "$out/bin"
    cp stage/manager.js stage/session.js "$out/libexec/yession/"
    cp -r stage/assets "$out/libexec/yession/assets"
    # The bundles are ESM; mark the directory so Node treats manager.js/session.js as modules.
    echo '{ "type": "module", "private": true }' > "$out/libexec/yession/package.json"

    # Runtime node_modules: the four externals kept out of the bundles resolve from here (the
    # rest of package.json's deps are inlined into the bundles, but carrying them is harmless).
    # Reuse the node_modules npmConfigHook already populated offline; drop dev-only tooling.
    npm prune --omit=dev --offline --no-audit --no-fund || true
    cp -r node_modules "$out/libexec/yession/node_modules"

    # The one native runtime dependency, built by Nix (never by npm here).
    mkdir -p "$out/libexec/yession/node_modules/node-datachannel/build/Release"
    cp ${node-datachannel}/build/Release/node_datachannel.node \
       "$out/libexec/yession/node_modules/node-datachannel/build/Release/node_datachannel.node"

    # Wrap the two bins on the pinned Node. The Manager spawns the Session Process as a child;
    # point it at the wrapped session bin, and point the agent at nixpkgs claude-code so the
    # SDK never needs its own native binary.
    makeWrapper ${nodejs_24}/bin/node "$out/bin/yession-session" \
      --add-flags "$out/libexec/yession/session.js" \
      --set-default YESSION_CLAUDE_PATH ${claude-code}/bin/claude

    makeWrapper ${nodejs_24}/bin/node "$out/bin/yession" \
      --add-flags "$out/libexec/yession/manager.js" \
      --set-default YESSION_SESSION_BIN "$out/bin/yession-session" \
      --set-default YESSION_CLAUDE_PATH ${claude-code}/bin/claude
    runHook postInstall
  '';

  dontStrip = true;

  meta = {
    description = "Local-first runtime where humans and AI agents collaborate in a shared session";
    homepage = "https://github.com/NickDarvey/yession";
    mainProgram = "yession";
    platforms = lib.platforms.unix;
  };
}
