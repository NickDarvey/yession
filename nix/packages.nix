# The installable Nix artifacts, defined independently of devenv so BOTH devenv.nix (as its
# `outputs`) and flake.nix (as `packages.*`) can consume them from one place. Keeping them out
# of devenv's module system is what makes `nix build .#yession` pure — no `devenv` input, no
# DEVENV_ROOT, no GitHub — so `nix profile install github:NickDarvey/yession` works for
# consumers. (The flake still uses devenv for `devShells`/`nix develop`, not for packages.)
#
# All compile/bundle/assemble logic lives in tasks.fsx; these derivations only fetch deps
# offline and drive `dotnet fsi tasks.fsx stage`, then wrap/pack the result.
{ pkgs, lib ? pkgs.lib, rev ? null }:
let
  # The native WebRTC addon, built from source (its npm prebuild is github-bound).
  node-datachannel = pkgs.callPackage ./node-datachannel.nix { };

  # claude-code is unfree; instantiate a nixpkgs that allows just that package (the agent
  # points at it so the SDK never needs its own native binary).
  claude-code = (import pkgs.path {
    inherit (pkgs.stdenv.hostPlatform) system;
    config.allowUnfreePredicate = p: lib.getName p == "claude-code";
  }).claude-code;

  # Release version via YESSION_VERSION when set (impure builds; `builtins.getEnv` is "" under the
  # pure evaluation `nix build` / `nix profile install` use). A pure build genuinely cannot know a
  # release number — `lib.cleanSource` below strips .git — so it reports the COMMIT it was built
  # from rather than a placeholder that reads like a release. flake.nix passes that rev in.
  #
  # The `0.0.0-` prefix is load-bearing twice over: `npm pack` (the `npm` output) rejects a version
  # that is not semver, and a prerelease sorts below every real release — which is exactly what an
  # untagged build off a working tree is.
  version =
    let fromEnv = builtins.getEnv "YESSION_VERSION";
    in if fromEnv != "" then fromEnv
       else if rev != null then "0.0.0-g${rev}"
       else "0.0.0-gdirty";

  # Only the files the build consumes, so editing devenv config / CI / docs doesn't invalidate
  # the (slow) F#/Fable build. README.md is kept — tasks.fsx copies it into the package.
  src =
    let root = ./..;
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
    hash = "sha256-b9zc6a1ep1xBo3uZ3UAeqmXVZ2mLEkm7V9VfcaseFMo=";
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

  # nix — the installable: two wrapped Node bins over tasks.fsx's shims, the runtime
  # node_modules, and the Nix node-datachannel addon, with the agent pointed at claude-code.
  nix = pkgs.stdenv.mkDerivation {
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

      # tasks.fsx's yession-manager shim sets YESSION_SESSION_MAIN and spawns `node session.js`,
      # which inherits YESSION_CLAUDE_PATH from this wrapper.
      makeWrapper ${pkgs.nodejs_24}/bin/node "$out/bin/yession-session" \
        --add-flags "$out/libexec/yession/bin/yession-session.js" \
        --set-default YESSION_CLAUDE_PATH ${claude-code}/bin/claude
      makeWrapper ${pkgs.nodejs_24}/bin/node "$out/bin/yession-manager" \
        --add-flags "$out/libexec/yession/bin/yession-manager.js" \
        --set-default YESSION_CLAUDE_PATH ${claude-code}/bin/claude
      runHook postInstall
    '';
    dontStrip = true;
    meta.mainProgram = "yession-manager";
  };

  # npm — the npm tarball, `npm pack`ed off the same staged package dir.
  npm = pkgs.stdenv.mkDerivation {
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
  inherit node-datachannel claude-code nodeModules staged nix npm;
}
