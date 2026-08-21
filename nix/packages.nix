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
  # nixpkgs builds libdatachannel `-DUSE_NICE=ON`, and that backend tears its ICE transport down
  # from the destroying thread while libnice's shared glib loop may already be dispatching a
  # receive for it — a use-after-free that crashed the Native-tagged suites intermittently. The
  # patch moves the detach onto the loop thread; see the patch header for the diagnosis.
  libdatachannel = pkgs.libdatachannel.overrideAttrs (old: {
    patches = (old.patches or [ ]) ++ [ ./libdatachannel-nice-teardown.patch ];
  });

  # The native WebRTC addon, built from source (its npm prebuild is github-bound).
  node-datachannel = pkgs.callPackage ./node-datachannel.nix { inherit libdatachannel; };

  node-pty = pkgs.callPackage ./node-pty.nix { };

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

  # The build's source: the TRACKED tree, minus the tracked files the build does not consume.
  # Two filters, both load-bearing.
  #
  # 1. `.gitignore`, compiled to a filter from the repo's own file (`nix-gitignore`, pure — it
  #    reads the patterns, it never shells out to git). What git ignores is by construction not
  #    source, and this is the ONLY filter that makes a build off a working tree equal a build
  #    off a fresh checkout. Without it a local build carried ~176MB of the dev shell's output —
  #    dotnet `obj/`/`bin/`, Fable's `.js` emitted beside the F# sources, `app/out`, `dist/`,
  #    `.devenv` — which invalidated the (slow) F#/Fable build on every local `check` and let
  #    stale emitted JS into a derivation that then regenerates it. Worst of it: `node_modules`
  #    is a SYMLINK to ${nodeModules}/node_modules in a dev shell (devenv's enterShell), and that
  #    store path is also a build input of `staged` — so the copy landed a live symlink to a
  #    read-only directory exactly where `staged` copies, and the build died on
  #      cp: cannot create directory './node_modules/node_modules'
  #    No CI job can see any of this: every one of them builds a flake source copy, which git
  #    already filtered. It reproduces only where a working tree reaches the derivation —
  #    `nix build --file nix/worktree.nix …`, `devenv build outputs.…`, `nix build path:.#…`.
  #    `check Nix` is the gate that now covers exactly that route (tasks.fsx).
  #
  # 2. The tracked-but-not-consumed list below, so editing devenv config / CI / docs doesn't
  #    invalidate the build. README.md is kept — tasks.fsx copies it into the package.
  src =
    let
      root = ./..;
      unignored = pkgs.nix-gitignore.gitignoreFilter (builtins.readFile ../.gitignore) root;
      notConsumed = [
        "nix" ".github" "docs" ".claude" ".agents"
        "flake.nix" "flake.lock" "devenv.nix" "devenv.yaml" ".gitignore" "AGENTS.md" "CLAUDE.md"
      ];
      # A directory is matched as itself, not only through its contents: returning false for the
      # directory prunes the walk there and leaves no empty `nix/` and `docs/` behind to suggest
      # the filter half-worked.
      isNotConsumed = rel: lib.any (entry: rel == entry || lib.hasPrefix (entry + "/") rel) notConsumed;
    in lib.cleanSourceWith {
      src = lib.cleanSource root;
      filter = path: type:
        let rel = lib.removePrefix (toString root + "/") (toString path);
        in unignored path type && !(isNotConsumed rel);
    };

  dotnetEnv = ''
    export HOME="$TMPDIR/home"
    mkdir -p "$HOME"
    export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
    export DOTNET_CLI_HOME="$HOME"
  '';

  # NuGet global-packages cache — the only network step (a fixed-output derivation). Populated
  # by restoring the solution + the Fable tool; consumed offline by `staged` via NUGET_PACKAGES.
  #
  # NO `version` in the name. A fixed-output derivation's store path comes from its NAME and
  # its HASH, so carrying the version there moved the path every commit — and this is the one
  # derivation that reaches the NETWORK, so every build re-downloaded the whole NuGet cache
  # from nuget.org and inherited nuget.org's bad days (a 503 here fails the build with
  # NU1301, having nothing to do with the change being built). The content is pinned by
  # `outputHash`; what it is called is not part of that guarantee.
  nugetDeps = pkgs.stdenv.mkDerivation {
    name = "yession-nuget-deps";
    inherit src;
    nativeBuildInputs = [ pkgs.dotnet-sdk_10 pkgs.cacert ];
    # The one derivation here that reaches the network, so the one that has to be told how to
    # leave the box. A sandboxed fixed-output build gets a cleared environment; without the
    # proxy variables passed through, NuGet dials out directly and a box that only egresses
    # through a proxy answers with `NU1301 … 503`, which reads like nuget.org having a bad day
    # rather than a build that never reached it. .NET's HttpClient picks these up on its own.
    impureEnvVars = lib.fetchers.proxyImpureEnvVars;
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
    outputHash = "sha256-Xiy8amjuMcCkkoq8noW92T9tU1RSrNxFVSxUSxWV4xY=";
  };

  # The npm manifests, alone. What `node_modules` IS depends on these two files and the addon —
  # not on the F# sources and not on the version. Handing the full `src` to the derivations below
  # made every source edit a cache miss on a multi-gigabyte tree: `nix eval` on `nodeModules.drvPath`
  # changed when a comment was appended to app/Version.fs, so a dev shell rebuilt the whole tree
  # after every edit (and, in a dev container, exhausted the disk). The version was the same trap
  # one level up: with `rev` passed (flake, CI) the derivation NAME moved every commit, so CI could
  # never reuse it either.
  npmManifests = pkgs.runCommand "yession-npm-manifests" { } ''
    mkdir -p "$out"
    cp ${../package.json} "$out/package.json"
    cp ${../package-lock.json} "$out/package-lock.json"
  '';

  npmDeps = pkgs.fetchNpmDeps {
    src = npmManifests;
    name = "yession-npm-deps";
    hash = "sha256-cJM85ao284hQtBm9eBPZCoYkBkrlf9NQfHzNvCGkIlU=";
  };

  # node_modules as a Nix artifact: the offline npm tree (npmConfigHook installs it from npmDeps
  # with scripts ignored) with the source-built node-datachannel addon overlaid. This is how the
  # dev shell gets a COMPLETE node_modules — the native WebRTC addon included — with no npm
  # postinstall, no GitHub, and no per-file addon linking. enterShell symlinks it into place.
  nodeModules = pkgs.stdenv.mkDerivation {
    # No `version` and no source beyond the manifests: both would move on every commit without
    # changing a byte of what this builds, and this is the derivation the dev shell depends on.
    name = "yession-node-modules";
    src = npmManifests;
    inherit npmDeps;
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
      # node-pty is replaced WHOLE rather than patched with a .node, unlike the addon above.
      # Its unix backend execs a `spawn-helper` binary that sits beside the addon, so the
      # built package is a pair and dropping half of it in would leave a pty that opens and
      # then cannot start a child.
      rm -rf node_modules/node-pty
      cp -r ${node-pty} node_modules/node-pty
      chmod -R u+w node_modules/node-pty
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
    inherit version src;
    nativeBuildInputs = [ pkgs.dotnet-sdk_10 pkgs.nodejs_24 ];
    # Reuse the cached tree rather than installing a second one. `src` here is the whole
    # source — correct, because this derivation COMPILES it — but npm's tree does not depend
    # on a line of F#, so running npmConfigHook here re-did a multi-gigabyte install on every
    # source change. A COPY, not a symlink: the install phase prunes it with `npm prune`, and
    # the Nix store is read-only.
    buildPhase = ''
      runHook preBuild
      ${dotnetEnv}
      cp -a ${nodeModules}/node_modules ./node_modules
      chmod -R u+w node_modules
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

  # The srt backend confines with bubblewrap, reaches its filtering proxy through socat (the
  # network namespace is unshared, so a Unix-socket bridge is the only way out), and finds the
  # files it must deny outright with ripgrep. All three are NAMED rather than left to PATH: srt
  # treats an explicit path as a directive and reports it missing, where a PATH lookup would
  # silently pick up someone else's build — or find nothing, and fail a sandbox that had no
  # business depending on the host's incidental tools. macOS confines with Seatbelt, which ships
  # with the OS and needs none of them — hence Linux only.
  #
  # `YESSION_GIT_PATH` below is the same argument and NOT Linux-only, because macOS is
  # where PATH's git is worst: `/usr/bin/git` there is a shim that resolves a developer
  # directory before it is git, through files a scoped sandbox denies.
  srtToolFlags = lib.optionalString pkgs.stdenv.isLinux ''
    \
        --set-default YESSION_BWRAP_PATH ${pkgs.bubblewrap}/bin/bwrap \
        --set-default YESSION_SOCAT_PATH ${pkgs.socat}/bin/socat \
        --set-default YESSION_RIPGREP_PATH ${pkgs.ripgrep}/bin/rg'';

  # nix — the installable: the two wrapped Node bins over tasks.fsx's shims, the runtime
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
        --set-default YESSION_CLAUDE_PATH ${claude-code}/bin/claude \
        --set-default YESSION_GIT_PATH ${pkgs.git}/bin/git ${srtToolFlags}
      makeWrapper ${pkgs.nodejs_24}/bin/node "$out/bin/yession-manager" \
        --add-flags "$out/libexec/yession/bin/yession-manager.js" \
        --set-default YESSION_CLAUDE_PATH ${claude-code}/bin/claude \
        --set-default YESSION_GIT_PATH ${pkgs.git}/bin/git ${srtToolFlags}
      runHook postInstall
    '';
    dontStrip = true;
    meta.mainProgram = "yession-manager";
  };

  # serial-provider — the EXAMPLE, built as its own installable.
  #
  # Deliberately not part of `nix` (the product installable), and deliberately its own
  # derivation rather than another bin bolted onto that one: an example ships on its own terms
  # or it is not an example. `nix build .#serial-provider` is how you get a runnable copy —
  # which is also what lets a machine run it as a service without vendoring the build.
  serial-provider = pkgs.stdenv.mkDerivation {
    pname = "serial-provider";
    inherit version;
    inherit src;
    nativeBuildInputs = [ pkgs.dotnet-sdk_10 pkgs.nodejs_24 pkgs.makeWrapper ];
    buildPhase = ''
      runHook preBuild
      ${dotnetEnv}
      cp -a ${nodeModules}/node_modules ./node_modules
      chmod -R u+w node_modules
      export PATH="$PWD/node_modules/.bin:$PATH"
      export NUGET_PACKAGES="$TMPDIR/nuget-packages"
      cp -r --no-preserve=mode,ownership ${nugetDeps} "$NUGET_PACKAGES"
      cat > nuget.config <<'EOF'
      <?xml version="1.0" encoding="utf-8"?>
      <configuration>
        <packageSources><clear/></packageSources>
      </configuration>
      EOF
      dotnet tool restore
      dotnet fsi tasks.fsx example serial
      runHook postBuild
    '';
    installPhase = ''
      runHook preInstall
      mkdir -p "$out/bin" "$out/libexec/serial-provider"
      cp examples/serial/dist/main.js "$out/libexec/serial-provider/main.js"
      # `serialport` is an optional native dep the provider imports lazily; absent, it reports
      # no devices rather than failing to start. Not carried here, so this build is the
      # degraded one until somebody installs it beside the bundle — which is honest: the addon
      # is per-platform and the example is not the place to pin one.
      makeWrapper ${pkgs.nodejs_24}/bin/node "$out/bin/serial-provider" \
        --add-flags "$out/libexec/serial-provider/main.js"
      runHook postInstall
    '';
    dontStrip = true;
    meta.mainProgram = "serial-provider";
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
  # nugetDeps is exposed for one reason: its `outputHash` can only be re-derived by building it
  # (`nix build --file nix/worktree.nix nugetDeps`), and a hash you cannot rebuild on demand is
  # a hash nobody updates until a release job fails.
  inherit libdatachannel node-datachannel node-pty claude-code nugetDeps nodeModules staged nix npm serial-provider;
}
