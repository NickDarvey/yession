# Plan 10 — Mounted sessions (one public address, stated once)

Plan 09 made sessions remotely reachable by **port mirroring**: the operator maps each
session's OS-assigned port 1:1 at a public host, and `YESSION_SESSION_URL` names the
scheme+host to hang those ports off. It works, and it stays supported. This plan removes
two things it left behind: a configuration pair that could disagree, and the restriction
that a session must own the root of its origin.

Nothing here is new networking. Yession still ships none — the operator brings the proxy,
Yession states where things are.

## The configuration was a pair that could disagree

Two loose strings with opposite formats and no validation:

- `YESSION_MANAGER_URL` carried its own port (`https://home.ts.net:8321`).
- `YESSION_SESSION_URL` had to OMIT one, because every consumer appended the session's
  port itself. So `http://home.ts.net:8443` silently produced `http://home.ts.net:8443:54321`,
  and the operator learned about it as a login redirect that went nowhere.

Of the four set/unset combinations, three are legal deployments and the fourth is always
broken:

| `MANAGER_URL` | `SESSION_URL` | |
|---|---|---|
| unset | unset | single machine — the default |
| set | unset | remote management, sessions used on the host (what Plan 07 shipped) |
| set | set | both fronted |
| unset | set | **broken**: a session bounces every login through the Manager, so a remote browser is sent to `127.0.0.1` to sign in |

A warning would treat the legal "manager only" row and the broken row alike. Instead
`PublicAccess` (`src/Yession.Domain/PublicAccess.fs`) is one value with three cases, and
`PublicAccess.create` is its only constructor: the fourth row is rejected once, at boot,
and nothing downstream can hold it. `ManagerOrigin` and `SessionTemplate` are private, so
the union cannot carry an unvalidated address either.

Sessions are described by a **template** over `{id}` and `{port}` — precisely the two
facts the Plan 09 registry stream already publishes, so any binding driven by that stream
can implement any template written with them:

```
# unset                                        -> http://127.0.0.1:{port}   (loopback)
YESSION_SESSION_URL=https://home.ts.net:{port}         # port mirroring (Plan 09)
YESSION_SESSION_URL=https://{id}.sessions.example.com  # a subdomain per session
YESSION_SESSION_URL=https://example.com/s/{id}         # a path per session
```

A bare origin with no placeholder is refused rather than silently meaning "append the
port". `Url` and `Mount` are derived together from the one template, so the address a
browser is given and the prefix a session serves under cannot disagree.

## Why path-mounting, having rejected it

Plan 09 rejected `/sessions/<id>/*` on two grounds. Both have been paid off:

- *"forces path-prefix awareness onto the whole client shell (`/client.js`, `/signal`,
  `/events/{n}` are absolute)"* — true of about ten call sites. `SessionRoute`
  (`src/Yession.App/Routes.fs`) now declares every path once, for the server that matches
  it, the shell that emits it, and the browser that fetches it. `relative` never emits a
  leading slash and is the only renderer, so a root-anchored URL has no spelling; the
  browser resolves against the shell's `<base href>`.
- *"plus `Location`-header rewriting"* — dissolves once the session emits its own
  redirects relative to its mount. It knows its mount.

What it buys, beyond taste: one hostname, one certificate, one public port. Port mirroring
cannot be deployed at all on ingress that will not bind arbitrary ports — Cloudflare
Tunnel, most managed platforms — because it needs a public port per session.

The operator chooses. A template with no path keeps the mount empty, and an unfronted
deployment is byte-identical to before this plan.

## The proxy contract

**The proxy forwards the public path unchanged; the session strips its own prefix.**

The opposite contract — proxy strips, session serves at root — would make correctness
depend on per-proxy rewriting semantics that cannot be verified in this repository. This
one is testable with no proxy at all: request `/s/<id>/client.js` against a session and
assert 200.

The mount reaches exactly three places, all from the same string:

- **`<base href>`** in the shell (`Ssr.page`), a REQUIRED parameter so no caller can render
  the document and forget it. Every relative route resolves against it.
- **The auth cookie's `Path`.** A real narrowing where sessions share a host: a
  path-mounted session's cookie is no longer sent to its siblings. Unchanged at an origin
  root, where the id in the cookie's NAME still carries the separation cookies cannot get
  from a port (`Cookies.sessionCookieName`).
- **`SessionRoute.parseUnder`**, which strips the prefix off incoming requests.

### `{port}` may not appear in a path

Everything a session fixes at boot depends on the mount, and its port is only assigned
when it binds. So the mount derives from the session id alone and a template that puts
`{port}` in its path is refused, naming the reason. `{port}` in the authority is
untouched: that is port mirroring, where the mount is empty anyway.

The excluded shape — path-mounting by internal port — would publish the very ports a proxy
exists to hide.

## Deliberate scope / rejected

- **The Manager stays at an origin root.** Its routes are origin-anchored and its issuer is
  a concatenation base (`<issuer>/connections/callback`), so a path prefix would work only
  if the proxy stripped it again. Refused at parse rather than shipped as an unverified
  maybe.
- **No per-request `Host`-derived addressing.** Plan 09's reasons stand: it breaks
  exact-match redirect registration, adds a header-trust surface, and gives one session two
  names.
- **The Manager does not become a proxy.** It publishes the registry; the operator's proxy
  maps the paths.

## A standing requirement, now written down

**A fronted Manager's URL must resolve from its own host**, not just from browsers. The
Manager's public origin is the OIDC issuer, and a launched session fetches discovery,
JWKS, and tokens against it (`SessionAuth.Configure`). Split-horizon DNS, or a proxy
listening only on an external interface, fails session registration — fatally, by design,
since a session that cannot authorize users must not half-start.

This was always true; Plan 09 mentioned it only in passing for sessions. It surfaced
concretely here: the registry-stream E2E had been relying on the now-refused
sessions-fronted-Manager-loopback combination, and once refused, the child fetched
discovery against a hostname that did not resolve.

Separating issuer *identity* from issuer *reachability* — fetch over loopback, present the
public string — is a possible follow-up, not done here.

## Verification

- Cheap tier: the `PublicAccess.create` matrix (three legal rows, the refused row,
  malformed templates, the `{port}`-in-path refusal); mount derivation per topology; cookie
  scoping; the `SessionRoute` contract — no route renders root-anchored, every route
  round-trips, method mismatch is not a route; and the mounted round trip, that what
  `<base href>` makes a browser ask for is exactly what `parseUnder` claims.
- Ports/Native: the registry stream and the real login chain against spawned sessions,
  with a fronted Manager at a loopback origin on a known port.
- Browser tier: real Chromium at a session's PUBLIC prefixed path, through a
  path-preserving proxy — the shell declares `<base href="/s/<id>/">`, the bundle is
  fetched from under the mount (read back from `performance.getEntriesByType`), the login
  bounce completes at the public address, a WebRTC data channel opens through the prefix,
  and the auth cookie's `Path` is the mount. So `<base href>` resolution is demonstrated in
  a browser, not argued from unit round-trips.

  That test also broke master once, and the reason is worth keeping: it evaluated
  `document.querySelector('base')` immediately after `GotoAsync`, while the client was
  renavigating through the login bounce, so its execution context could be destroyed
  mid-evaluate. It passed locally every time and failed on the first CI run. Anything
  driving this flow must await the navigation-tolerant `WaitForFunctionAsync` FIRST and
  evaluate only after — and a green local run is not evidence that a browser test is
  free of that race.
