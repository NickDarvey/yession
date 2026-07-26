# Plan 07 — BYO user authorization (trusted-header identity)

Operators bring their own authenticator: a reverse proxy they run themselves (first
target: Tailscale) sits in front of the Manager's HTTP port, authenticates the human,
and asserts the identity to Yession as HTTP headers. Yession defines the header scheme
and threads the resulting user identity through the existing OIDC bounce into event
attribution and secrets; it ships no sidecar. Everything keeps working with no proxy at
all — actors are then identified by their peer connection, exactly as before.

Delivered together with this plan: the actor-terminology unification
(`UserId` / `PeerRef` / `UserRef` — see docs/design.md §6), the management-UI gate, the
public-issuer override, stable browser peer ids, and peer-scoped secrets.

## Strategies

`AuthenticationStrategy` (Plan 04's seam) now has three values, selected by a CLI
argument at Manager start — `yession-manager --auth <name>`:

| `--auth` | Behaviour |
|---|---|
| *(absent)* / `none` | Denies every request. The default: choosing a trust rule is an explicit operator act; an exposed endpoint can never fall back to an unintended one. |
| `localhost` | Any loopback request is the single **unattributed** subject `local` (the pre-07 behaviour; what `start`/`dev` pass). |
| `trusted-headers` | The proxy in front asserts the user in canonical `x-yession-*` headers, trusted verbatim. |

An unknown name fails the boot loudly. `trusted-headers` **replaces** `localhost` and
must never compose with it: behind a loopback-terminating proxy every request arrives
over loopback, so composition would authenticate header-less requests as `local`.

### The header scheme

The proxy translates whatever its authenticator gives it into Yession's canonical
headers (values are UTF-8; Node lowercases names):

```
x-yession-user            REQUIRED — the subject (a stable, unique user identifier).
                          Absent/blank ⇒ 401.
x-yession-user-name       optional display name
x-yession-user-email      optional email
x-yession-user-picture    optional avatar URL
x-yession-user-claims     optional JSON object of additional claims, carried opaquely
                          (e.g. Tailscale app capabilities) — recorded, not yet policy
```

Example — Tailscale: `tailscale serve` adds `Tailscale-User-Login` /
`Tailscale-User-Name` / `Tailscale-User-Profile-Pic` but cannot rename headers, so a
small rewriting proxy sits between it and the Manager. Caddy:

```caddyfile
:9000 {
    reverse_proxy 127.0.0.1:8321 {
        header_up x-yession-user         {header.Tailscale-User-Login}
        header_up x-yession-user-name    {header.Tailscale-User-Name}
        header_up x-yession-user-picture {header.Tailscale-User-Profile-Pic}
        header_up -tailscale-*
    }
}
```

with `tailscale serve --bg 9000`, and the Manager started with
`--auth trusted-headers` and `YESSION_MANAGER_URL` set to the tailnet origin (see
"Public issuer" below).

### Attributed vs unattributed

`AuthenticationOutcome` distinguishes them structurally — never by comparing subject
strings:

```fsharp
type AuthenticationOutcome =
    | Attributed of UserClaims           // a real, durable user: events may attribute to it
    | Unattributed of subject: string    // shared access, nobody in particular (localhost)
    | Denied of reason: string
```

Both non-denied outcomes issue a code and an ID token; the token carries
`yession_attribution: "user" | "unattributed"` (plus `name`/`email`/`picture` when
attributed) so the session RP knows which it has, and unattributed subjects still bind
into `launchUsers` — `user:local` secret injection is unchanged.

## Attribution chain (Manager-verified end to end)

```
proxy headers ─→ strategy ─→ code grant ─→ signed ID token (yession_attribution)
      ─→ session RP validates ─→ cookie (CookieIdentity) ─→ /me mints a peer token
         CARRYING the attribution ─→ PeerHello presents the token ─→ PeerJoined.User
      ─→ scheduler's actorFor: MessageSent.Author = UserRef user
```

`PeerHello` itself never carries user identity — the peer token does, server-side —
so nothing self-asserted ever enters `UserRef` (the Plan 06 invariant, extended to
events). Drafts, queue entries, presence, and `PeerJoined`/`PeerLeft` envelope actors
stay keyed by `PeerId`: those are connection facts; attribution is applied at the
durable-append boundary (`MessageSent`, interrupt envelopes) via the Host's
peer→user map, which is derived from `PeerJoined.User` in the durable log — replayed
at boot, so restarts keep attributing a departed peer's still-queued messages.

Under `localhost`, every hop carries `UnattributedAccess` and events are byte-identical
to pre-07 behaviour — the "works without BYO auth" requirement holds structurally.

## Management UI gate

Every UI route authenticates per-request through the same strategy (route first, then
one `identify` call; `Denied` ⇒ 401). Control routes keep their per-launch secret;
OIDC routes keep their own flow. Under `localhost` the UI behaves exactly as before;
under the default `none` it denies — the Plan 02 "control hardening is deliberately
minimal" caveat for the UI is retired.

## Public issuer

`YESSION_MANAGER_URL` (→ `Options.PublicUrl`) overrides the issuer that discovery,
`/authorize`, and `/token` URLs derive from — required off-host, where browsers must
reach the Manager through the proxy's origin. Loopback default unchanged. Proxying
*sessions* (OS-assigned ports, loopback-bound, per-launch redirect URIs) is a recorded
follow-up in GAPS.

## Stable peer ids + peer-scoped secrets

The browser mints its `PeerId` once and keeps it in localStorage (browser-wide key —
it names the browser profile, not a session), so colours, draft slots, and peer scopes
survive reloads. Private-mode storage denial falls back to a per-load id.

`SecretScope` gains `PeerScope of PeerId` (codec kind `"peer"`, AAD `peer:<id>`).
Authorization leans on the OIDC relationship rather than a new channel: the browser
sends `peer_id` on `/login`, the session forwards it to `/authorize`, the code grant
carries it, and at `/token` the Manager records the peer into the launch
(`launchPeers`, exactly like `launchUsers` — launch-lifetime, Manager-witnessed at
auth time). `Policy.authorize` permits peer-scope operations only for a session whose
live launch has that peer bound; injection precedence becomes
session ▸ bound users ▸ witnessed peers ▸ process env (`SecretRef` stays name-only).

Stated trade-off: the peer id is still browser-asserted at the bounce — a peer scope
namespaces the asserting browser's own secrets within the deployment's trust boundary.
Under a real strategy, `UserScope` is the durable home; peer scope is the unattributed
deployment's counterpart.

## Threat model

- **Header forgery = loopback access.** Anyone who can reach the Manager's port
  directly can assert any identity — the same boundary as the localhost model. The
  proxy must be the only non-local path in, and MUST strip inbound `x-yession-*`
  headers from client requests before adding its own.
- Plaintext trusted headers match Tailscale's model (the tailnet authenticates; the
  proxy is trusted infrastructure). A signed-JWT header strategy — proxy signs the
  claims, Manager verifies against an operator JWKS via the already-bound
  `Fable.Jose.jwtVerify`/`createLocalJWKSet` — is the hardening follow-up, as a fourth
  strategy value.
- No `nonce` in ID tokens yet — unchanged from Plan 04.

## Deliberate scope / follow-ups

- User-facing secret-write surface (policy rows for `AuthzSubject { Session = None }`
  + a `/secrets` UI page) — deferred; `UserScope` still has no writer.
- Tailscale app capabilities → authorization rules (`UserClaims.Extra` carries them
  opaquely; `AuthzSubject` gaining claims is the extension point).
- Remote session access through the proxy; `SessionRecord` owner field / per-user
  session lists; client display of verified names/avatars beyond `UserId.value`.
- Session-side re-login UX when a launch's bindings die (launch-lifetime bindings
  unchanged from Plan 06).

## Verification

- Cheap tier: `ActorRef`/`PeerJoined` codec round-trips; strategy unit tests (all
  three, including `ofName`); `PeerTokens`/`CookieSessions` attribution; pump tests
  (attributed hello ⇒ `PeerJoined.User`); policy decision-table rows (witnessed /
  unwitnessed peers); injection precedence including the peer level.
- Ports tier: the full trusted-header bounce over a real Manager + child session
  (synthetic `x-yession-user` at `/authorize` ⇒ ID-token claims, `launchUsers`,
  events attributing `UserRef`); UI 401 matrix under `none` and `trusted-headers`.
- Manual: `--auth trusted-headers` + `curl -H 'x-yession-user: nick@example.com'` vs
  no header (401); no `--auth` ⇒ everything denies; `--auth localhost` ⇒ pre-07
  behaviour throughout.
