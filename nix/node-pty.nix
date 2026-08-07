# node-pty's native addon, built from source under Nix (Plan 13, stage 2c).
#
# A pipe is not a tty: no line discipline, no SIGWINCH, and most full-screen programs refuse
# or degrade. `node-pty` is what gives the host backend a real pty, and it is a native addon,
# so it lands here the way `node-datachannel` does — built from source and overlaid into the
# `nodeModules` derivation — rather than through an npm install that would fetch a prebuilt.
#
# It has to be built rather than downloaded for a plainer reason than node-datachannel's:
# node-pty 1.1.0 ships prebuilds for darwin and win32 only. There is no Linux `.node` in the
# tarball at all, so on the platform CI actually runs, `npm install` would compile it anyway
# — and its `install` script (`node scripts/prebuild.js || node-gyp rebuild`) reaches the
# network to decide which. Both are things a sandboxed build cannot do.
#
# Simpler than node-datachannel in one respect: node-pty vendors no third-party C++ library,
# so there is no FetchContent to patch out. It needs exactly one thing on top of its own
# sources — node-addon-api's headers — and the Node headers to compile against.
{ lib, stdenv, fetchurl, python3, nodejs_24 }:

let
  version = "1.1.0";

  src = fetchurl {
    url = "https://registry.npmjs.org/node-pty/-/node-pty-${version}.tgz";
    hash = "sha256-x1F/GQg93LBfJ2kEaA6ysRprXsq3eLjk5WhabWRbP2A=";
  };

  # Header-only. `binding.gyp` resolves it by RUNNING
  # `node -p "require('node-addon-api').targets"`, so it has to be a real resolvable package
  # under node_modules rather than an include path we could pass in.
  nodeAddonApi = fetchurl {
    url = "https://registry.npmjs.org/node-addon-api/-/node-addon-api-7.1.1.tgz";
    hash = "sha256-sQRV0VqXfAzRehyw62eeA9k5+O+NQwLrM+H3jazHH4I=";
  };
in
stdenv.mkDerivation {
  pname = "node-pty";
  inherit version src;

  nativeBuildInputs = [ python3 nodejs_24 ];

  postUnpack = ''
    mkdir -p "$sourceRoot/node_modules/node-addon-api"
    tar -xzf ${nodeAddonApi} -C "$sourceRoot/node_modules/node-addon-api" --strip-components=1
  '';

  # node-gyp caches downloaded headers under $HOME and will try to fetch them if it cannot
  # find a nodedir; give it a writable HOME and point it at the nixpkgs Node so it never
  # looks at the network.
  buildPhase = ''
    runHook preBuild
    export HOME="$TMPDIR/home"
    mkdir -p "$HOME"
    ${nodejs_24}/lib/node_modules/npm/node_modules/node-gyp/bin/node-gyp.js rebuild \
      --nodedir=${nodejs_24} \
      --build-from-source
    runHook postBuild
  '';

  # Ship the whole package — its `lib/` JS is what `require('node-pty')` loads, and it looks
  # for the addon at `build/Release/pty.node`, where node-gyp has already put it. The
  # spawn-helper beside it is not optional: the unix backend execs it to set the controlling
  # terminal before the child starts.
  installPhase = ''
    runHook preInstall
    mkdir -p "$out"
    cp -r . "$out/"
    runHook postInstall
  '';

  dontFixup = true;

  meta = {
    description = "node-pty's native pty addon, built from source against the nixpkgs Node";
    homepage = "https://github.com/microsoft/node-pty";
    license = lib.licenses.mit;
    platforms = lib.platforms.unix;
  };
}
