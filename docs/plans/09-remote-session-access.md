# Plan 09 — Remote session access (session registry stream + BYO serving)

Plan 07 made the *Manager* remotely reachable: an operator-run proxy authenticates the
human, `YESSION_MANAGER_URL` makes the OIDC issuer the proxy's origin. Sessions stayed
loopback-anchored — GAPS records it: "Remote access covers the Manager only." This plan
closes that gap the same way 07 opened it: Yession ships **no networking**. Sessions
keep their loopback bind; the operator brings the serving (Tailscale serve, Caddy,
nginx — whatever fronts their machine), driven by a small canonical contract:

1. **A session registry stream** — one SSE endpoint on the Manager: the current session
   list on subscribe, then a fresh list on every change. An operator-side *binding*
   (~20 lines, analogous to 07's header-mapping proxy config) reconciles their proxy's
   config against it.
2. **A public session origin** — `YESSION_SESSION_URL`, the session-side sibling of
   `YESSION_MANAGER_URL`: the scheme+host at which every session's port is reachable
   from outside. It threads into the two places that today hard-code loopback: the
   per-launch OAuth redirect URI and the management UI's "open" link.

Everything keeps working with neither configured — no knob, no stream consumer ⇒
byte-identical loopback behaviour, exactly the 07 "works without BYO" requirement.

## Topology

Port-mirroring, not path-mounting: the operator maps each session's OS-assigned port
1:1 at a public host (`tailscale serve --bg --http=<port> <port>`). The proxy
terminates on loopback, so sessions still see loopback peers and nothing about their
bind, auth, or asset paths changes. The rejected alternative — proxying
`/sessions/<id>/*` through the Manager — would force path-prefix awareness onto the
whole client shell (`/client.js`, `/signal`, `/events/{n}` are absolute) plus
`Location`-header rewriting, to solve a problem the mirror dissolves.

```
browser ──http──> public host:8321  ──proxy──> 127.0.0.1:8321  (Manager, per Plan 07)
browser ──http──> public host:<p>   ──proxy──> 127.0.0.1:<p>   (session, this plan)
browser <═══════════ WebRTC data channel ═══════════> session process   (direct)
```

The data channel does not traverse the proxy. `Interop.createPeerConnection` gathers
with empty `iceServers` — libdatachannel host candidates on **all** interfaces, so a
routable non-loopback address (e.g. a tailnet 100.x) rides the non-trickle SDP and the
remote browser connects to it directly; no STUN/TURN. **Precondition:** one manual
browser↔session data-channel test across the real overlay network before building. If
it fails, remote access needs TURN and this plan is the wrong shape — stop and record.

## The session registry stream

`GET /sessions/stream` on the Manager's endpoint, `text/event-stream`. The first frame
is the full current state; every subsequent frame is the full current state again —
snapshot semantics, never deltas, exactly like the three existing control reverse legs
(`/control/connections`: "the current list on subscribe, then a fresh list on every
change"). A consumer that wants instant reaction holds the stream open; a consumer
that doesn't connects, reads the first frame, disconnects — a poll. One endpoint
serves both cadences, so there is no second mechanism to rot.

Each frame is the Running sessions only:

```json
{"sessions":[{"id":"01hx...","name":"docs rewrite","port":54321,"pid":4242}]}
```

- **Emission points:** every transition of the children map (launch succeeded, child
  exited, stop) and display-name changes. Implementation mirrors `NotificationHub`:
  a hub registering one sink per subscription, frames encoded and written by the
  endpoint — unit-testable without a socket. Keep-alive comment frames on the
  existing control-SSE interval so idle streams survive middleboxes.
- **Gate:** `identify`, like every management-UI route. Under `localhost` a
  same-machine binding just works; under `trusted-headers` the binding asserts its own
  `x-yession-user` — it runs inside the loopback trust boundary the 07 threat model
  already accepts.
- **Deltas rejected:** a delta protocol needs sequence numbers, replay on reconnect,
  and a client that tracks state to apply them to. Snapshots make reconnect the
  recovery mechanism and the reconciler stateless about history.

## The binding (operator-side, not shipped)

Yession's contract ends at the stream. The binding translates registry → proxy config,
per operator, like 07's header-mapping proxy. The correctness pattern is a
**level-based reconciler**, not up/down event handlers — edge-triggered teardown dies
exactly when needed (Manager SIGKILL, reboot, binding crash between events), and
persisted proxy config (e.g. `tailscale serve --bg`) outlives all of those:

```
desired = latest stream frame (Manager unreachable ⇒ ∅)
owned   = state file: ports this binding created
current = the proxy's live config
add desired−current; remove owned∩current−desired; update state file
```

Three properties make this correct here:

- **"Manager unreachable ⇒ no sessions" is a fact, not a heuristic**: sessions never
  outlive their Manager (the stdin parent guard, kernel-enforced even on SIGKILL), so
  pruning everything owned is always safe.
- **Ownership via the state file** keeps the binding's hands off mappings the operator
  made by hand (the Manager's own port, anything else on the box).
- **Reconnect-with-backoff doubles as the poll**: each reconnect's first frame is a
  full snapshot, healing whatever was missed. A stale mapping's exposure window — an
  OS-reassigned port briefly served to the old public port — is bounded by the
  reconnect/reconcile interval.

Tailscale example (the whole binding): subscribe to the stream; per frame, diff
against `tailscale serve status -json` restricted to owned ports;
`tailscale serve --bg --http=<p> <p>` to add, `tailscale serve --http=<p> off` to
remove. Run it as a supervised agent that also fires at boot, so mappings persisted
across a reboot are pruned before the Manager launches anything.

## Public session origin

`YESSION_SESSION_URL` — scheme + host, **no port** (`http://home.example.ts.net`);
each session appends its own port. That "host serves every session port" shape *is*
the mirror contract, stated as config. Set on the Manager, it reaches children by
plain env inheritance (like the OTel variables). Unset ⇒ `http://127.0.0.1` — current
behaviour everywhere.

It threads into exactly two places:

- **The per-launch redirect URI** (`SessionMain`): register
  `<YESSION_SESSION_URL>:<port>/callback` instead of loopback. The provider stores and
  redirects to the registered value verbatim, so no validation change — the browser
  simply lands somewhere it can reach. Discovery already passes
  `allowInsecureRequests`, so a plain-HTTP non-loopback issuer works mechanically.
- **The management UI's open link** (`ManagerUi.actions`): render
  `<YESSION_SESSION_URL>:<port>/` instead of the hard-coded loopback URL.

Per-request `Host`-derived URLs were rejected: they break the exact-match redirect
registration, add a header-trust surface, and give one session two names. One
configured public origin, valid on-host too (the public name resolves locally), keeps
a session's identity single.

Already-safe pieces, verified against source: session auth cookies are namespaced by
session id (`Auth.sessionCookieName`) precisely because loopback cookies are not
port-scoped — the same property holds at a shared public host. And the attribution
chain needs **no per-session identity proxy**: sessions authenticate their users via
the OIDC bounce through the Manager, so the single Plan 07 proxy in front of the
Manager attributes users for every session; session ports are fronted by *plain*
mappings.

## Threat model

- **Exposing session ports widens who can reach them** from "this machine" to
  "whatever the operator's proxy admits" (e.g. the whole tailnet). The data surfaces
  hold: `/events` wants the auth cookie or a minted token, and every joining peer must
  present a token this process minted at `PeerHello` — an unauthenticated visitor can
  load the shell (content-free by design) and POST `/signal`, obtaining a data channel
  that the token gate then refuses. That accepted-then-refused peer connection is a
  resource nuisance, not an access path; per-IP throttling on `/signal` is a follow-up
  if it ever matters.
- **Plain HTTP at the public origin** is acceptable where the transport underneath is
  already encrypted (WireGuard-backed overlays); the cookie loses nothing it had on
  loopback. TLS at the proxy (`https` mappings + an `https://` `YESSION_SESSION_URL`)
  is the hardening follow-up and needs no Yession change.
- **The registry stream discloses topology** (ids, names, ports, pids) — gated by
  `identify` like the UI that shows the same facts.

## Deliberate scope / rejected

- **No mDNS/DNS-SD advertisement.** Multicast does not cross overlay networks, so an
  advertisement's only consumer would be a same-machine adaptor — which the registry
  stream serves without a 5353 responder dependency or duplicated state: the Manager
  already owns the session registry.
- **No shipped adaptor/sidecar and no in-Manager proxy** — the 07 stance: Yession
  defines the contract, the operator brings the infrastructure.
- **TURN/relayed WebRTC** — out of scope; the precondition test decides whether this
  plan proceeds at all.
- Retires the GAPS entry "Remote access covers the Manager only" when done.

## Verification

- Cheap tier: registry frame codec round-trips; hub emission on each children-map and
  display-name transition (sink pattern, no socket); `YESSION_SESSION_URL` URL
  assembly incl. the unset loopback default.
- Ports tier: against a real Manager — subscribe ⇒ first frame is the current
  snapshot; launch ⇒ a frame containing the new session's port; stop/child-death ⇒ a
  frame without it; reconnect ⇒ fresh snapshot first; 401 matrix under `none` /
  `localhost` / `trusted-headers`. With the knob set: the registered redirect URI and
  the rendered open link carry the public origin; unset ⇒ loopback, byte-identical.
- Manual (the precondition, then end-to-end): browser↔session data channel across the
  real overlay network; then serve-mapped session from a second device — open link
  from the Manager UI, OIDC bounce, collaborative editing over the data channel.
