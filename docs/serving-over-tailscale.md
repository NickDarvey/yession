# Serving over Tailscale

A Manager and its sessions bind loopback, and Yession ships no networking. Reaching them from
another device is two things: **addressing** — making them reachable under a name that resolves
off-host — and **authorizing** — deciding who, having reached them, gets in. The rationale lives
in [Plan 09](plans/09-remote-session-access.md) and
[Plan 07](plans/07-byo-user-authorization.md); this is the setup.

Collaborative editing rides a WebRTC data channel straight to the session process, never the
proxy — host candidates only, no STUN/TURN ([GAPS](GAPS.md)) — which is why an overlay network
works at all, and verified on a tailnet.

## Addressing

**Prerequisites**

- Tailscale on the Manager's host, with a MagicDNS name for it.

**Steps**

```sh
YESSION_MANAGER_URL=http://host.tailnet.ts.net:8321   # the OIDC issuer — WITH the port
YESSION_SESSION_URL=http://host.tailnet.ts.net        # each session appends its own port
tailscale serve --bg --http=8321 8321                 # the Manager's one fixed mapping
```

Set both, even with `--auth localhost`. Plan 07 introduces `YESSION_MANAGER_URL` for BYO
authenticators and Plan 09 adds `YESSION_SESSION_URL` for sessions, so a deployment doing neither
reads as though it needs only the latter — but opening a session bounces through the Manager's
OIDC issuer whatever `--auth` says, and unset it falls back to a literal `http://127.0.0.1:<port>`.
The session page then loads fine over the tailnet and login redirects the browser to its own
loopback.

Mirror the Manager's own port rather than 80: anything already bound to `:80` on all interfaces
shadows a `--http=80` mapping, answering with an empty `200` of its own instead of the Manager.
For HTTPS, enable HTTPS Certificates in the admin console first — without them a serve that wants
443 blocks on provisioning rather than failing, and `tailscale cert` names the cause.

### Session mappings

Session ports are OS-assigned and change every launch, so they follow the Manager's registry.
`/sessions/stream` hands a subscriber the current snapshot on connect, then a full frame on every
launch, exit and rename — never deltas — so each frame is applied wholesale and a reconnect is the
entire recovery protocol.

```bash
#!/usr/bin/env bash
# Reconcile `tailscale serve` against the Manager's session registry.
# Needs bash, curl, jq, tailscale. Run supervised, and at boot.
set -uo pipefail

MANAGER=http://127.0.0.1:8321
STATE="${XDG_STATE_HOME:-$HOME/.local/state}/yession-serve-ports"
IDLE_RECHECK=60      # frames come only on transitions; idle ⇒ look for outside drift
RECONNECT=2

mkdir -p "$(dirname "$STATE")"
owned="$(sort -u "$STATE" 2>/dev/null | sed '/^$/d')"

served() { tailscale serve status -json 2>/dev/null | jq -r '.TCP // {} | keys[]' | sort -u; }

reconcile() {   # $1 = desired ports, sorted, one per line, possibly empty
  local desired="$1" current add prune p
  tailscale status --json 2>/dev/null | jq -e '.BackendState == "Running"' >/dev/null || return
  current="$(served)"
  owned="$(comm -12 <(printf '%s\n' "$owned") \
                    <(printf '%s\n%s\n' "$current" "$desired" | sort -u | sed '/^$/d'))"
  add="$(comm -23 <(printf '%s\n' "$desired") <(printf '%s\n' "$current"))"
  prune="$(comm -23 <(comm -12 <(printf '%s\n' "$owned") <(printf '%s\n' "$current")) \
                    <(printf '%s\n' "$desired"))"
  for p in $add; do
    tailscale serve --bg --http="$p" "$p" >/dev/null 2>&1 &&
      owned="$(printf '%s\n%s\n' "$owned" "$p" | sed '/^$/d' | sort -u)"
  done
  for p in $prune; do
    tailscale serve --http="$p" off >/dev/null 2>&1 &&
      owned="$(printf '%s\n' "$owned" | sed "/^$p\$/d")"
  done
  printf '%s\n' "$owned" | sed '/^$/d' > "$STATE"
}

while :; do
  exec 3< <(curl -sN --no-buffer --connect-timeout 5 "$MANAGER/sessions/stream" 2>/dev/null)
  framed=0 last=""
  while :; do
    if IFS= read -r -t "$IDLE_RECHECK" line <&3; then
      case "$line" in
        "data: "*)
          last="$(printf '%s' "${line#data: }" | jq -r '.sessions[].port' | sort -u | sed '/^$/d')"
          framed=1; reconcile "$last" ;;
      esac
    elif [ $? -gt 128 ]; then reconcile "$last"   # idle
    else break                                    # stream closed
    fi
  done
  exec 3<&-
  [ "$framed" = 0 ] && reconcile ""
  sleep "$RECONNECT"
done
```

Only ports in the state file are ever unmapped: the Manager's own mapping is served and never
desired, so a plain `current − desired` prune tears it down on the first tick. Pruning also
requires the port to be currently served, which is what makes a Tailscale outage harmless —
nothing looks served, so nothing can be lost. A connect that never yields a frame means the
Manager is unreachable, and sessions cannot outlive their Manager, so that genuinely is the empty
desired set; a merely closed stream is not, since reconnecting re-snapshots. Run it at boot too,
because serve config survives reboots.

Wrap each `tailscale serve` call in a timeout if you supervise it yourself — a serve that wants
HTTPS without cert support blocks forever and wedges the loop — and poll that timeout finely, or
you round every mapping change up to a full second.

## Authorizing

`tailscale serve` terminates on loopback, so with `--auth localhost` every tailnet visitor is the
single **unattributed** subject `local`: one actor across every session and event log. Coherent
for a personal tailnet; for a shared one nothing errors, it simply all reads as `local`.

For attributed identity use `--auth trusted-headers` with a proxy that renames Tailscale's
headers — `Tailscale-User-Login`, `Tailscale-User-Name`, `Tailscale-User-Profile-Pic` (1.98.9) —
into the canonical `x-yession-*` scheme, which
[Plan 07](plans/07-byo-user-authorization.md) defines and gives a Caddy config for. It **replaces**
`localhost` and must never compose with it, and it belongs in front of the Manager only: sessions
authenticate their users through the Manager's OIDC bounce, so session ports stay plain mappings.
