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

  # dbus + gnome-keyring back the `Keyring` test capability on headless hosts:
  # `check Keyring` re-execs itself under a private, empty-password-unlocked Secret
  # Service when no session bus exists (see tasks.fsx keyringWrapper).
  packages = [ pkgs.git pkgs.dbus pkgs.gnome-keyring pkgs.actionlint ];

  env.DOTNET_CLI_TELEMETRY_OPTOUT = "1";
  env.DOTNET_NOLOGO = "1";

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
    # The task list orients someone who just landed in the shell. In front of a one-off
    # `devenv shell -- <task>` it is pure noise, printed above every check, build and CI log.
    # devenv says which this is: DEVENV_CMDLINE is a bare `shell` interactively, `shell -- …`
    # for a task.
    case "''${DEVENV_CMDLINE:-}" in
      *" -- "*) ;;
      *) echo "yession — tasks: restore build start dev check verify lint package clean  (check <caps>: Browser Ports Native Docker LiveAgent Keyring)" ;;
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
