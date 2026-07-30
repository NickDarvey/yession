# Serving Yession over Tailscale

A worked setup for reaching a Manager *and its sessions* from another device, using
Tailscale as the operator-supplied serving layer. Yession ships no networking: this is
configuration plus one small binding you run yourself.

Two independent concerns, in order. **Addressing** makes the Manager and every session
reachable under a name that resolves off-host. **Authorizing** decides who, having
reached them, is allowed in. Addressing alone leaves you with whatever `--auth` you
already had — which for the common single-machine default means something specific and
worth reading before you expose anything.

The design rationale lives in [Plan 09](plans/09-remote-session-access.md) (addressing)
and [Plan 07](plans/07-byo-user-authorization.md) (authorizing). This document does not
restate it; it records what an operator actually has to do, and what bit us doing it.

## Precondition: prove the data channel first

The management UI and each session's page ride the proxy. **The collaborative editing
does not** — the browser talks to the session process over a WebRTC data channel
directly. `Interop.createPeerConnection` gathers with empty `iceServers`: host
candidates only, no STUN and no TURN ([GAPS](GAPS.md)). It works on an overlay network
where the session host's addresses route directly, which a tailnet is — but that is a
property of your deployment, not a guarantee.

Prove it before building anything, with one hand-made mapping:

```sh
tailscale serve --bg --http=<session-port> <session-port>   # port from the Manager UI
```

From a second tailnet device: open the session, then **type in the editor and confirm
it syncs**. HTTP 200 on the page proves only the proxy. If sync never connects, stop —
remote access needs TURN and everything below is moot. Clean up with
`tailscale serve --http=<session-port> off`.

## Addressing

### The two public origins

Yession hard-codes loopback in exactly two places, and each has an override. Both are
set on the **Manager**; sessions inherit them as child processes.

| Variable | Value | Port? |
|---|---|---|
| `YESSION_MANAGER_URL` | The Manager's own public URL — the OIDC **issuer** | **Yes** — one concrete endpoint |
| `YESSION_SESSION_URL` | The origin every session's port is reachable at | **No** — each session appends its own |

```sh
YESSION_MANAGER_URL=http://host.tailnet.ts.net:8321
YESSION_SESSION_URL=http://host.tailnet.ts.net
```

**Set both, even with `--auth localhost`.** This is the trap. Plan 07 introduces
`YESSION_MANAGER_URL` as part of bringing your own authenticator, and Plan 09 adds
`YESSION_SESSION_URL` for sessions — so it reads as though a deployment that is not
doing trusted-headers only needs the latter. It does not. Opening a session triggers an
OIDC bounce to the Manager's issuer *whatever* `--auth` is set to, and with
`YESSION_MANAGER_URL` unset the issuer falls back to `endpointUrl` — a literal
`http://127.0.0.1:<port>`. The symptom is precise and confusing: the session page loads
fine over the tailnet, then login redirects the remote browser to **its own** loopback.

Setting only `YESSION_SESSION_URL` gets you a working open link and a broken login.

### The Manager mapping

One fixed mapping, made once:

```sh
tailscale serve --bg --http=8321 8321
```

Two notes from doing this for real:

- **Check what already owns the port.** A reverse proxy bound to `:80` on all
  interfaces shadows a `--http=80` mapping: the request never reaches Tailscale's
  handler, and you get a confusing empty `200` from the other server instead of the
  Manager. Mirroring the Manager's own port (8321) avoids the collision entirely and
  matches the port-mirroring shape sessions already use.
- **HTTPS needs tailnet certs.** Without them `tailscale cert <host>` answers
  `your Tailscale account does not support getting TLS certs`, and a serve that wants
  443 *blocks on provisioning* rather than failing. Enable HTTPS Certificates in the
  admin console (DNS), then use `--https` mappings and `https://` origins. Plain HTTP
  over a tailnet is still WireGuard-encrypted between peers.

### The session binding

Sessions get OS-assigned ports that change on every launch, so their mappings have to
follow the Manager's registry. Subscribe to `/sessions/stream` and reconcile.

The stream's contract makes this easy: a new subscriber is handed the current snapshot
**immediately**, then a fresh **full** frame on every launch, exit and rename — never
deltas. So each frame is applied wholesale, and a reconnect is the entire recovery
protocol.

```bash
#!/usr/bin/env bash
# Reconcile `tailscale serve` mappings against the Manager's session registry.
# Needs: bash, curl, jq, tailscale. Run supervised, restarted on exit, at boot too.
set -uo pipefail

MANAGER=http://127.0.0.1:8321
STATE="${XDG_STATE_HOME:-$HOME/.local/state}/yession-serve-ports"
IDLE_RECHECK=60          # frames come only on transitions; idle ⇒ check for drift
RECONNECT=2

mkdir -p "$(dirname "$STATE")"
owned="$(sort -u "$STATE" 2>/dev/null | sed '/^$/d')"

served() { tailscale serve status -json 2>/dev/null | jq -r '.TCP // {} | keys[]' | sort -u; }

reconcile() {                       # $1 = desired ports, sorted, one per line, may be empty
  local desired="$1" current add prune p
  # Tailscale down ⇒ everything looks unserved. Pruning is already safe (it requires a
  # port to BE served), so this is about not retrying adds that cannot succeed.
  tailscale status --json 2>/dev/null | jq -e '.BackendState == "Running"' >/dev/null || return
  current="$(served)"

  # Forget bookkeeping for mappings that are gone and not coming back (e.g. `serve reset`).
  owned="$(comm -12 <(printf '%s\n' "$owned") <(printf '%s\n%s\n' "$current" "$desired" | sort -u | sed '/^$/d'))"

  add="$(comm -23 <(printf '%s\n' "$desired") <(printf '%s\n' "$current"))"
  # ONLY ports we created: owned AND served, minus desired.
  prune="$(comm -23 <(comm -12 <(printf '%s\n' "$owned") <(printf '%s\n' "$current")) <(printf '%s\n' "$desired"))"

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
    elif [ $? -gt 128 ]; then
      [ "$framed" = 1 ] && reconcile "$last"     # idle: re-check for outside drift
    else
      break                                       # EOF: stream closed
    fi
  done
  exec 3<&-
  # A closed stream is NOT "the Manager is gone" — reconnecting re-snapshots and fixes
  # everything, so pruning here would unmap and instantly remap a healthy session. Only
  # a connect that never yielded a frame means unreachable, and sessions cannot outlive
  # their Manager, so that genuinely is the empty desired set.
  [ "$framed" = 0 ] && reconcile ""
  sleep "$RECONNECT"
done
```

Four properties are load-bearing; changing any of them breaks it in a way testing on a
quiet machine will not reveal:

- **Ownership is not defensive coding.** The Manager's own 8321 mapping is *served* and
  is never *desired*, so a plain `current − desired` prune tears it down on the first
  tick. Only ports in the state file are ever unmapped.
- **Prune requires the port to be currently served.** That single condition is what
  makes a Tailscale outage harmless: with the backend down nothing looks served, so
  nothing can be unmapped.
- **Unreachable Manager ⇒ empty desired ⇒ prune everything owned.** Not a heuristic:
  sessions cannot outlive their Manager (the parent guard holds even on SIGKILL), so
  their mappings are certainly stale.
- **Run it at boot.** Serve config persists across reboots, so mappings for sessions
  that died with the last boot must be pruned before the Manager relaunches anything.

Two things worth hardening if you supervise it yourself: wrap each `tailscale serve`
call with a timeout (a serve that wants HTTPS on a tailnet without cert support blocks
indefinitely, and a wedged call stalls the loop for good), and if you poll for that
timeout, poll finely — a one-second granularity rounds every mapping change up to a
full second and is the difference between ~140 ms and ~1.1 s of reaction.

## Authorizing

Addressing changes *who can reach* the Manager, not *who gets in*. Decide this
deliberately — the two options differ in what appears in your event log, not just in
security posture.

### Option A — `--auth localhost` (single trusted tailnet)

`tailscale serve` terminates on loopback and proxies in, so **every request arrives as
a loopback request**. With `--auth localhost` that means everyone who can reach the
tailnet name is authenticated as the single **unattributed** subject `local`. There is
no per-user distinction: every session, every event, one actor.

For a personal tailnet that is a coherent choice and needs no extra moving parts. For a
shared one it is almost certainly not what you want, and the failure is silent — nothing
errors, everything is simply attributed to `local`.

### Option B — `--auth trusted-headers` (attributed identity)

`tailscale serve` injects identity headers but cannot rename them. Verified on
Tailscale 1.98.9:

```
Tailscale-User-Login:       someone@example.com
Tailscale-User-Name:        Someone Example
Tailscale-User-Profile-Pic: <url, may be empty>
```

A small rewriting proxy maps those to Yession's canonical `x-yession-*` scheme —
[Plan 07](plans/07-byo-user-authorization.md) defines the scheme and gives a Caddy
config. Start the Manager with `--auth trusted-headers` and point
`YESSION_MANAGER_URL` at the proxy's origin.

Two rules that are easy to get wrong:

- **`trusted-headers` replaces `localhost`; they must never compose.** Behind a
  loopback-terminating proxy every request is loopback, so a composed strategy would
  authenticate any header-less request as the local user — precisely the hole you
  installed the proxy to close.
- **The rewriting proxy goes in front of the Manager only.** Session ports stay *plain*
  mappings. Sessions authenticate their users via the OIDC bounce through the Manager,
  so one proxy attributes users for every session; a per-session identity proxy is
  unnecessary (see Plan 09, "Public session origin").

## Verifying the whole thing

1. `tailscale serve status` lists the Manager mapping and one mapping per running session.
2. From a second tailnet device, the Manager UI loads at `http://host.tailnet.ts.net:8321/`.
3. The UI's **open** link points at `host.tailnet.ts.net:<port>` — not `127.0.0.1`. If it
   is loopback, `YESSION_SESSION_URL` is unset.
4. Clicking through login lands back on the tailnet name. If it redirects to `127.0.0.1`,
   `YESSION_MANAGER_URL` is unset — see the trap above.
5. Typing in the editor syncs. That is the data channel, and it never touched the proxy.
6. Stopping a session removes its mapping; the Manager's own mapping survives.
