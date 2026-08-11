{ pkgs, lib, ... }:

# The single declaration for Yession's dev environment and tasks. The toolchain is the pinned
# nixpkgs (see `languages.*` below); tasks are devenv `scripts`. The installable Nix package and
# the npm tarball are derivations in nix/packages.nix (imported below, re-exposed as `outputs`
# and consumed directly by flake.nix); all compile/bundle/assemble logic lives in tasks.fsx.
let
  # The installable artifacts + the node-datachannel addon + the Nix node_modules tree, defined
  # outside devenv's module system so flake.nix can build them without pulling devenv (that is
  # what keeps `nix build .#yession` pure). Imported here for `outputs` and the dev node_modules.
  yession = import ./nix/packages.nix { inherit pkgs lib; };
in
{
  languages.javascript.enable = true;
  languages.javascript.package = pkgs.nodejs_24;
  languages.dotnet.enable = true;
  languages.dotnet.package = pkgs.dotnet-sdk_10;

  # dbus + gnome-keyring back the `Keyring` test capability on headless LINUX hosts:
  # `check Keyring` re-execs itself under a private, empty-password-unlocked Secret
  # Service when no session bus exists (see tasks.fsx keyringWrapper).
  #
  # Linux-only, and not as a preference — gnome-keyring has no darwin build, so listing it
  # unconditionally made the whole shell fail to EVALUATE on macOS ("Refusing to evaluate
  # package 'gnome-keyring' … not available on the requested hostPlatform"). Not a broken
  # test: `nix develop` could not be entered at all, so nothing on a Mac could reach the
  # toolchain through the documented path.
  #
  # Nothing is lost there. `needsKeyringWrap` (tasks.fsx) is gated on `IsLinux`, because a
  # desktop already has a Secret Service — the real Keychain — and `check Keyring` drives
  # that instead. The wrapper exists for hosts with no session bus, which macOS never is.
  #
  # bubblewrap + socat + ripgrep are the srt backend's Linux dependencies (and back the `Srt`
  # test capability). bubblewrap and socat are Linux-only for the same reason gnome-keyring is:
  # no darwin build, and macOS confines with Seatbelt, which ships with the OS. ripgrep is
  # listed with them because it is the same story — srt scans for its mandatory denies with it,
  # on Linux only.
  #
  # eudev backs the `Serial` capability alongside socat, and is Linux-only because the need
  # is: `SerialPort.list()` shells out to `udevadm` on Linux to learn a port's vendor and
  # product, and without it enumeration throws rather than answering. eudev rather than
  # systemd because it is the standalone udev — its `udevadm info` answers from sysfs with no
  # daemon running, which a container has none of. socat is what makes the rest testable with
  # no hardware: a PTY pair is a serial port at both ends.
  # uv + a CPython back the `Jumpstarter` capability: the jumpstarter example is a uv project,
  # and the suite that drives it runs `uv run --project`. UV_PYTHON_DOWNLOADS=never below is
  # what makes this interpreter the one it uses — otherwise uv fetches an interpreter of its
  # own, which is a second, unpinned toolchain arriving over the network mid-run.
  packages =
    [ pkgs.git pkgs.actionlint pkgs.uv pkgs.python312 ]
    ++ lib.optionals pkgs.stdenv.hostPlatform.isLinux
         [ pkgs.dbus pkgs.gnome-keyring pkgs.bubblewrap pkgs.socat pkgs.ripgrep pkgs.eudev ];

  # Name the confinement tools for the srt backend exactly as the installable's wrappers do
  # (nix/packages.nix), so a dev-shell run and an installed run confine through the same
  # binaries. Empty on darwin, where Seatbelt needs neither — the backend reads a blank the
  # same as unset.
  env.YESSION_BWRAP_PATH =
    lib.optionalString pkgs.stdenv.hostPlatform.isLinux "${pkgs.bubblewrap}/bin/bwrap";
  env.YESSION_SOCAT_PATH =
    lib.optionalString pkgs.stdenv.hostPlatform.isLinux "${pkgs.socat}/bin/socat";
  env.YESSION_RIPGREP_PATH =
    lib.optionalString pkgs.stdenv.hostPlatform.isLinux "${pkgs.ripgrep}/bin/rg";

  env.UV_PYTHON = "${pkgs.python312}/bin/python3.12";
  env.UV_PYTHON_DOWNLOADS = "never";

  env.DOTNET_CLI_TELEMETRY_OPTOUT = "1";
  env.DOTNET_NOLOGO = "1";

  # The Chromium the Browser-tier E2Es drive, from nixpkgs — so it is pinned by the same lock
  # as every other tool here, present before the suite starts, and fetched by Nix rather than
  # by the test run.
  #
  # `browsers-chromium`, not `browsers`: this repo launches Chromium and nothing else, and the
  # full set adds Firefox and WebKit — about a gigabyte no suite here ever opens.
  #
  # It replaces `npx playwright install --with-deps chromium`, and the improvement is not
  # tidiness. That command reached outside the build: `--with-deps` installs SYSTEM packages
  # (apt, as root) as a side effect of running tests, which works on a CI image and on nothing
  # else. nixpkgs instead patchelfs the same upstream build against an explicit X/GTK/NSS
  # closure and wraps it with SSL_CERT_FILE and FONTCONFIG_FILE, so on Linux it needs neither
  # apt nor root; on darwin it is the same app bundle Playwright would have downloaded, where
  # `--with-deps` was never anything but a no-op.
  #
  # Same browser either way: this nixpkgs pins playwright-driver 1.61.1 — the exact version
  # tasks.fsx pinned for npx, and the pair Microsoft.Playwright 1.61.0 expects. That agreement
  # is the point of pinning a revision at all, and it is why the test may not fall back to
  # some other Chromium that happens to be installed.
  env.PLAYWRIGHT_BROWSERS_PATH = "${pkgs.playwright-driver.browsers-chromium}";

  # devenv's CLI-vs-modules skew banner, printed on EVERY task invocation, can never be
  # actionable here: the CLI always comes from the pinned nixpkgs (`nix profile install
  # nixpkgs#devenv` in both workflows, and .claude/setup.sh does the same), so nobody in this
  # repo picks a devenv version to keep in sync. Worse, it currently misfires — nixpkgs' devenv
  # 2.2.0 source ships a stale `src/modules/latest-version` reading 2.1.2, so the banner fires
  # against devenv's own source and `devenv update` cannot silence it.
  devenv.warnOnNewVersion = false;

  # Point node_modules at the Nix-built tree (addon baked in). Idempotent; replaces a stale
  # symlink or a leftover npm-installed dir. `restore` then skips `npm install` (dir present).
  enterShell = ''
    if [ "$(readlink node_modules 2>/dev/null)" != "${yession.nodeModules}/node_modules" ]; then
      rm -rf node_modules
      ln -s ${yession.nodeModules}/node_modules node_modules
    fi
    export PATH="$PWD/node_modules/.bin:$PATH"
${lib.optionalString pkgs.stdenv.hostPlatform.isLinux ''
    # The jumpstarter example installs manylinux wheels, and `grpcio`'s extension is compiled
    # against the system C++ runtime. Nix's loader does not search /usr/lib, so inside this
    # shell `import grpc` dies with "libstdc++.so.6: cannot open shared object file" — on a
    # box where that library is right there in /usr/lib and every non-Nix Python finds it.
    # Naming it from this nixpkgs keeps the interpreter pinned instead of handing resolution
    # back to whatever the host happens to ship.
    #
    # APPENDED here rather than declared as `env.LD_LIBRARY_PATH`, because devenv's own dotnet
    # module already owns that option (it puts icu4c on it) and two definitions of one env
    # option is an evaluation error, not a merge.
    export LD_LIBRARY_PATH="''${LD_LIBRARY_PATH:+$LD_LIBRARY_PATH:}${lib.makeLibraryPath [ pkgs.stdenv.cc.cc.lib pkgs.zlib ]}"
''}
    # The task list orients someone who just landed in the shell. In front of a one-off
    # `devenv shell -- <task>` it is pure noise, printed above every check, build and CI log.
    # devenv says which this is: DEVENV_CMDLINE is a bare `shell` interactively, `shell -- …`
    # for a task.
    case "''${DEVENV_CMDLINE:-}" in
      *" -- "*) ;;
      *) echo "yession — tasks: restore build start dev check verify lint package clean  (check <caps>: Browser Ports Native Docker LiveAgent Keyring Nix Srt)" ;;
    esac
  '';

  # --- build outputs (devenv build outputs.<name>) -------------------------------------------
  # Same derivations flake.nix builds, re-exposed so `devenv build outputs.<name>` works too.
  # staged = shared base; nix = the installable bins; npm = the tarball.
  outputs.staged = yession.staged;
  outputs.nix = yession.nix;
  outputs.npm = yession.npm;

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
  # actionlint over .github/workflows — release.yml is otherwise only validated when it runs,
  # which is on master, after a merge.
  scripts.lint.exec = ''exec dotnet fsi tasks.fsx lint'';
  scripts.version.exec = ''exec dotnet fsi tasks.fsx version'';
  # Local package (compile + bundle + smoke + pack). For the release tarball as a Nix output,
  # use `devenv build outputs.npm`. Usage: package [1.2.3] — with no argument tasks.fsx computes
  # the version from the commit history, so there is no default to duplicate here.
  scripts.package.exec = ''exec dotnet fsi tasks.fsx package "$@"'';
  scripts.clean.exec = ''exec dotnet fsi tasks.fsx clean'';
  # CI helpers (also plain tasks.fsx verbs): install-smoke <tgz>, clean-docker.
  scripts."install-smoke".exec = ''exec dotnet fsi tasks.fsx install-smoke "$@"'';
  scripts."clean-docker".exec = ''exec dotnet fsi tasks.fsx clean-docker'';
}
