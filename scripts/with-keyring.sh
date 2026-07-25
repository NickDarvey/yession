#!/usr/bin/env bash
# Run a command with a working Secret Service on a headless host: a private D-Bus
# session plus gnome-keyring's secrets component, unlocked with an empty password.
# This is how the `Keyring` test capability runs in the dev container and CI
# (`scripts/with-keyring.sh check Keyring`); on a desktop with a real credential
# manager, run the command directly instead. Both `dbus-run-session` and
# `gnome-keyring-daemon` come from devenv (devenv.nix).
set -euo pipefail

if [ $# -eq 0 ]; then
  echo "usage: $0 <command> [args...]" >&2
  exit 2
fi

# The Nix dbus package expects its config in /etc, which headless containers lack —
# hand the daemon the config that ships inside the package instead. dbus-run-session
# only accepts a bare binary name, so wrap the flag in a shim.
dbus_bin="$(readlink -f "$(command -v dbus-daemon)")"
session_conf="$(dirname "$dbus_bin")/../share/dbus-1/session.conf"
shim_dir="$(mktemp -d)"
trap 'rm -rf "$shim_dir"' EXIT
cat > "$shim_dir/dbus-daemon-shim" <<SHIM
#!/usr/bin/env bash
# dbus-run-session passes --session; replace it with the packaged config file.
args=()
for a in "\$@"; do [ "\$a" = "--session" ] || args+=("\$a"); done
exec "$dbus_bin" --config-file="$session_conf" "\${args[@]}"
SHIM
chmod +x "$shim_dir/dbus-daemon-shim"

exec dbus-run-session --dbus-daemon="$shim_dir/dbus-daemon-shim" -- bash -euo pipefail -c '
  # An isolated keyring home so runs never touch (or depend on) a real user keyring.
  export XDG_DATA_HOME="$(mktemp -d)"
  export XDG_RUNTIME_DIR="$XDG_DATA_HOME/runtime"
  mkdir -p "$XDG_RUNTIME_DIR"
  chmod 700 "$XDG_RUNTIME_DIR"
  # Unlock with an empty (newline) password on stdin — the keyring-rs CI recipe: this
  # creates + unlocks the login collection and its "default" alias on first run.
  eval "$(printf "\n" | gnome-keyring-daemon --unlock | sed "s/^/export /")"
  exec "$@"
' -- "$@"
