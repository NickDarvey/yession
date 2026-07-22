{ pkgs, lib, ... }:

# The single declaration for Yession's dev environment, tasks, AND build outputs. Toolchain
# (Node 24, .NET SDK 10) from the pinned nixpkgs; tasks are devenv `scripts`; the installable
# Nix package and the npm tarball are devenv `outputs`, both produced off one staged build.
# All compile/bundle/assemble logic lives in tasks.fsx (the one authority the scripts
# and the outputs both call). flake.nix only re-exposes these outputs; it declares nothing.
let
  # The native WebRTC addon, built from source (its npm prebuild is github-bound).
  node-datachannel = pkgs.callPackage ./nix/node-datachannel.nix { };

  # claude-code is unfree; instantiate a nixpkgs that allows just that package (the agent
  # points at it so the SDK never needs its own native binary).
  claude-code = (import pkgs.path {
    inherit (pkgs.stdenv.hostPlatform) system;
    config.allowUnfreePredicate = p: lib.getName p == "claude-code";
  }).claude-code;

  # Release version via YESSION_VERSION (devenv build is impure); defaults for local builds.
  version = let v = builtins.getEnv "YESSION_VERSION"; in if v == "" then "0.0.0-nix" else v;

  # Only the files the build consumes, so editing devenv config / CI / docs doesn't invalidate
  # the (slow) F#/Fable build. README.md is kept — tasks.fsx copies it into the package.
  src =
    let root = ./.;
    in lib.cleanSourceWith {
      src = lib.cleanSource root;
      filter = path: _type:
        let rel = lib.removePrefix (toString root + "/") (toString path);
        in !(
          lib.hasPrefix "nix/" rel
          || lib.hasPrefix ".github/" rel
          || lib.hasPrefix "docs/" rel
          || lib.hasPrefix ".claude/" rel
          || rel == "flake.nix"
          || rel == "flake.lock"
          || rel == "devenv.nix"
          || rel == "devenv.yaml"
          || rel == "devenv.lock"
          || rel == ".gitignore"
          || rel == "AGENTS.md"
          || rel == "CLAUDE.md"
        );
    };

  dotnetEnv = ''
    export HOME="$TMPDIR/home"
    mkdir -p "$HOME"
    export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
    export DOTNET_CLI_HOME="$HOME"
  '';

  # NuGet global-packages cache — the only network step (a fixed-output derivation). Populated
  # by restoring the solution + the Fable tool; consumed offline by `staged` via NUGET_PACKAGES.
  nugetDeps = pkgs.stdenv.mkDerivation {
    pname = "yession-nuget-deps";
    inherit version src;
    nativeBuildInputs = [ pkgs.dotnet-sdk_10 pkgs.cacert ];
    buildPhase = ''
      runHook preBuild
      ${dotnetEnv}
      export NUGET_PACKAGES="$out"
      mkdir -p "$out"
      # nixpkgs' dotnet drops nuget.org (its "_nix" source stays offline); add it back here.
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

  npmDeps = pkgs.fetchNpmDeps {
    inherit src;
    name = "yession-npm-deps";
    hash = "sha256-KAlO9QDFE5XNUmM1202Oet2dicrkQotZJQIm9uzHHgQ=";
  };

  # node_modules as a Nix artifact: the offline npm tree (npmConfigHook installs it from npmDeps
  # with scripts ignored) with the source-built node-datachannel addon overlaid. This is how the
  # dev shell gets a COMPLETE node_modules — the native WebRTC addon included — with no npm
  # postinstall, no GitHub, and no per-file addon linking. enterShell symlinks it into place.
  nodeModules = pkgs.stdenv.mkDerivation {
    pname = "yession-node-modules";
    inherit version src npmDeps;
    nativeBuildInputs = [ pkgs.nodejs_24 pkgs.npmHooks.npmConfigHook ];
    npmFlags = [ "--ignore-scripts" ];
    dontBuild = true;
    installPhase = ''
      runHook preInstall
      # npmConfigHook populated ./node_modules from the FOD (scripts ignored, so node-datachannel
      # has its JS but no compiled addon); drop the Nix-built .node into place.
      mkdir -p node_modules/node-datachannel/build/Release
      cp ${node-datachannel}/build/Release/node_datachannel.node \
         node_modules/node-datachannel/build/Release/node_datachannel.node
      # Ship it AS `$out/node_modules` so that, once symlinked in, a package's realpath parent is
      # literally `node_modules` — Node resolves siblings (e.g. esbuild → @esbuild/linux-x64) only
      # by that name, so `$out/<pkgs>` directly would break self-resolution.
      mkdir -p "$out"
      cp -a node_modules "$out/node_modules"
      runHook postInstall
    '';
    # The addon and the npm-shipped platform binaries (esbuild, tailwind oxide) are already
    # built; don't let fixup patchelf/strip them.
    dontFixup = true;
  };

  # staged — the offline build shared by both outputs. Delegates to tasks.fsx `stage`
  # (compile + bundle + assemble dist/npm); no bundling logic is duplicated here. $out carries
  # the assembled package dir and a prod-pruned node_modules for the installable to reuse.
  staged = pkgs.stdenv.mkDerivation {
    pname = "yession-staged";
    inherit version src npmDeps;
    nativeBuildInputs = [ pkgs.dotnet-sdk_10 pkgs.nodejs_24 pkgs.npmHooks.npmConfigHook ];
    npmFlags = [ "--ignore-scripts" ];
    buildPhase = ''
      runHook preBuild
      ${dotnetEnv}
      export PATH="$PWD/node_modules/.bin:$PATH"
      # NUGET_PACKAGES must be writable (restore writes lock/temp files); copy the read-only FOD.
      export NUGET_PACKAGES="$TMPDIR/nuget-packages"
      cp -r --no-preserve=mode,ownership ${nugetDeps} "$NUGET_PACKAGES"
      cat > nuget.config <<'EOF'
      <?xml version="1.0" encoding="utf-8"?>
      <configuration>
        <packageSources><clear/></packageSources>
      </configuration>
      EOF
      dotnet tool restore
      dotnet fsi tasks.fsx stage "${version}"
      runHook postBuild
    '';
    installPhase = ''
      runHook preInstall
      mkdir -p "$out"
      cp -r dist/npm "$out/dist-npm"
      # The four runtime externals (kept out of the bundles) resolve from here at run time.
      npm prune --omit=dev --offline --no-audit --no-fund || true
      cp -r node_modules "$out/node_modules"
      runHook postInstall
    '';
    dontStrip = true;
  };

  # installed — the installable: two wrapped Node bins over tasks.fsx's shims, the runtime
  # node_modules, and the Nix node-datachannel addon, with the agent pointed at claude-code.
  installed = pkgs.stdenv.mkDerivation {
    pname = "yession";
    inherit version;
    dontUnpack = true;
    nativeBuildInputs = [ pkgs.makeWrapper ];
    installPhase = ''
      runHook preInstall
      mkdir -p "$out/bin" "$out/libexec"
      cp -r --no-preserve=mode,ownership ${staged}/dist-npm "$out/libexec/yession"
      cp -r --no-preserve=mode,ownership ${staged}/node_modules "$out/libexec/yession/node_modules"

      mkdir -p "$out/libexec/yession/node_modules/node-datachannel/build/Release"
      cp ${node-datachannel}/build/Release/node_datachannel.node \
         "$out/libexec/yession/node_modules/node-datachannel/build/Release/node_datachannel.node"

      # tasks.fsx's yession shim sets YESSION_SESSION_MAIN and spawns `node session.js`, which
      # inherits YESSION_CLAUDE_PATH from this wrapper.
      makeWrapper ${pkgs.nodejs_24}/bin/node "$out/bin/yession-session" \
        --add-flags "$out/libexec/yession/bin/yession-session.js" \
        --set-default YESSION_CLAUDE_PATH ${claude-code}/bin/claude
      makeWrapper ${pkgs.nodejs_24}/bin/node "$out/bin/yession" \
        --add-flags "$out/libexec/yession/bin/yession.js" \
        --set-default YESSION_CLAUDE_PATH ${claude-code}/bin/claude
      runHook postInstall
    '';
    dontStrip = true;
    meta.mainProgram = "yession";
  };

  # packaged — the npm tarball, `npm pack`ed off the same staged package dir.
  packaged = pkgs.stdenv.mkDerivation {
    pname = "yession-tarball";
    inherit version;
    dontUnpack = true;
    nativeBuildInputs = [ pkgs.nodejs_24 ];
    installPhase = ''
      runHook preInstall
      export HOME="$TMPDIR"
      mkdir -p "$out"
      cp -r --no-preserve=mode,ownership ${staged}/dist-npm ./pkg
      ( cd pkg && npm pack --pack-destination "$out" )
      runHook postInstall
    '';
  };
in
{
  languages.javascript.enable = true;
  languages.javascript.package = pkgs.nodejs_24;
  languages.dotnet.enable = true;
  languages.dotnet.package = pkgs.dotnet-sdk_10;

  packages = [ pkgs.git ];

  env.DOTNET_CLI_TELEMETRY_OPTOUT = "1";
  env.DOTNET_NOLOGO = "1";

  # Point node_modules at the Nix-built tree (addon baked in). Idempotent; replaces a stale
  # symlink or a leftover npm-installed dir. `restore` then skips `npm install` (dir present).
  enterShell = ''
    if [ "$(readlink node_modules 2>/dev/null)" != "${nodeModules}/node_modules" ]; then
      rm -rf node_modules
      ln -s ${nodeModules}/node_modules node_modules
    fi
    export PATH="$PWD/node_modules/.bin:$PATH"
    echo "yession — tasks: restore build start dev check verify package clean  (check <caps>: Browser Ports Native Docker LiveAgent)"
  '';

  # --- build outputs (devenv build outputs.<name>) -------------------------------------------
  # staged   : the shared offline build -> dist/npm + runtime node_modules
  # installed: the installable (yession + yession-session as wrapped Node bins)
  # packaged : the npm tarball, packed off the same staged build
  outputs.staged = staged;
  outputs.installed = installed;
  outputs.packaged = packaged;

  # --- tasks (devenv scripts) ----------------------------------------------------------------
  # Thin wrappers: every task is a verb of tasks.fsx (the complete, standalone build
  # interface). No Yession build logic lives here — delete devenv and call the fsx directly and
  # nothing is lost. `"$@"` forwards args (e.g. `check Browser Ports Native --retry 1`).

  scripts.restore.exec = ''exec dotnet fsi tasks.fsx restore'';
  scripts.build.exec = ''exec dotnet fsi tasks.fsx build'';
  scripts.start.exec = ''exec dotnet fsi tasks.fsx start'';
  scripts.dev.exec = ''exec dotnet fsi tasks.fsx dev'';
  # Named `check`, not `test`, because `test` is a shell builtin and would shadow the script.
  scripts.check.exec = ''exec dotnet fsi tasks.fsx check "$@"'';
  scripts.verify.exec = ''exec dotnet fsi tasks.fsx verify'';
  scripts.version.exec = ''exec dotnet fsi tasks.fsx version'';
  # Local package (compile + bundle + smoke + pack). For the release tarball as a Nix output,
  # use `devenv build outputs.packaged`. Usage: package 1.2.3
  scripts.package.exec = ''exec dotnet fsi tasks.fsx package "''${1:-0.0.0-dev}"'';
  scripts.clean.exec = ''exec dotnet fsi tasks.fsx clean'';
  # CI helpers (also plain tasks.fsx verbs): install-smoke <tgz>, clean-docker.
  scripts."install-smoke".exec = ''exec dotnet fsi tasks.fsx install-smoke "$@"'';
  scripts."clean-docker".exec = ''exec dotnet fsi tasks.fsx clean-docker'';
}
