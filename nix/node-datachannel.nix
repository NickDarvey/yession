# node-datachannel's native WebRTC addon, built from source under Nix.
#
# The npm package ships a prebuilt `.node` (from GitHub releases) or, failing that, builds
# from source with cmake-js — its CMakeLists FetchContent's libdatachannel from GitHub at
# configure time. Neither works in a sandbox: no network, and GitHub is scoped to attached
# repos. So we patch the FetchContent out and link nixpkgs' libdatachannel and plog
# (header-only), then compile the addon against the nixpkgs Node headers. The result is the
# whole node-datachannel package with `build/Release/node_datachannel.node` populated, ready
# to drop into node_modules.
{ lib, stdenv, fetchurl, cmake, pkg-config, openssl, libdatachannel, plog, nodejs_24 }:

let
  version = "0.32.3";

  src = fetchurl {
    url = "https://registry.npmjs.org/node-datachannel/-/node-datachannel-${version}.tgz";
    hash = "sha256-cYRh1TDWequhY4EvJ3OGcQBTNijrwJ8mGJ5/pAo7SkQ=";
  };

  # Header-only; node-datachannel's C++ includes <napi.h> from here.
  nodeAddonApi = fetchurl {
    url = "https://registry.npmjs.org/node-addon-api/-/node-addon-api-7.1.1.tgz";
    hash = "sha256-sQRV0VqXfAzRehyw62eeA9k5+O+NQwLrM+H3jazHH4I=";
  };
in
stdenv.mkDerivation {
  pname = "node-datachannel";
  inherit version src;

  nativeBuildInputs = [ cmake pkg-config ];
  buildInputs = [ openssl libdatachannel plog nodejs_24 ];

  # Unpack node-addon-api's headers next to the sources where the CMakeLists expects them
  # (${CMAKE_SOURCE_DIR}/node_modules/node-addon-api).
  postUnpack = ''
    mkdir -p "$sourceRoot/node_modules/node-addon-api"
    tar -xzf ${nodeAddonApi} -C "$sourceRoot/node_modules/node-addon-api" --strip-components=1
  '';

  postPatch = ''
    # nixpkgs OpenSSL is shared; don't force static libs (there is no static crypto lib).
    substituteInPlace CMakeLists.txt \
      --replace-fail 'set(OPENSSL_USE_STATIC_LIBS TRUE)' ""

    # Replace the GitHub FetchContent of libdatachannel with the nixpkgs package.
    substituteInPlace CMakeLists.txt \
      --replace-fail 'include(FetchContent)' 'find_package(LibDataChannel REQUIRED)'
    # Drop the FetchContent_Declare / Populate / add_subdirectory block (lines between the
    # find_package we just wrote and the source-file list) — sed out the whole region.
    sed -i '/# Fetch libdatachannel/,/^endif()/d' CMakeLists.txt

    # Link nixpkgs libdatachannel (shared) instead of the vendored static target, and drop
    # the plog::plog CMake target (plog is header-only; we add its include dir below).
    substituteInPlace CMakeLists.txt \
      --replace-fail 'datachannel-static' 'LibDataChannel::LibDataChannel' \
      --replace-fail 'plog::plog' ""

    # Point the include dirs at the nixpkgs headers instead of the FetchContent build tree.
    substituteInPlace CMakeLists.txt \
      --replace-fail '${"$"}{CMAKE_BINARY_DIR}/_deps/libdatachannel-src/include' '${lib.getDev libdatachannel}/include' \
      --replace-fail '${"$"}{CMAKE_BINARY_DIR}/_deps/libdatachannel-src/deps/plog' '${plog}/include'
  '';

  # Drive cmake directly (not cmake-js) so nothing tries to download Node headers: point
  # CMAKE_JS_INC at the nixpkgs Node + node-api headers, and leave CMAKE_JS_LIB empty (on
  # Linux the addon resolves Node symbols at load time — no import library to link).
  cmakeFlags = [
    "-DCMAKE_JS_INC=${nodejs_24}/include/node"
    "-DCMAKE_JS_LIB="
    "-DCMAKE_JS_SRC="
    "-DNO_MEDIA=OFF"
    "-DNO_WEBSOCKET=OFF"
  ];

  installPhase = ''
    runHook preInstall
    # Ship the whole package (its dist/ JS loads the addon) with the built .node in place.
    mkdir -p "$out"
    cp -r "$NIX_BUILD_TOP/$sourceRoot"/. "$out/"
    mkdir -p "$out/build/Release"
    cp node_datachannel.node "$out/build/Release/node_datachannel.node"
    runHook postInstall
  '';

  meta = {
    description = "node-datachannel WebRTC addon, built from source against nixpkgs libdatachannel";
    homepage = "https://github.com/murat-dogan/node-datachannel";
    license = lib.licenses.mpl20;
    platforms = lib.platforms.unix;
  };
}
