# Deployment

Two things must be settled before Yession is reachable from anywhere but the machine it runs
on: **who** the humans at this Manager are, and **where** the Manager and its sessions answer.
Both are configured on the Manager; sessions inherit what they need by plain env inheritance.

The interfaces below are what Yession asks of whatever sits in front of it. The integrations
are worked examples of one thing satisfying them.

---

## Interfaces

### Authorizing

Yession does not authenticate anyone itself. It names a **trust rule** — how a request's
subject is established — chosen once at Manager start:

```sh
yession-manager --auth localhost         # single machine
yession-manager --auth trusted-headers   # an authenticating proxy in front
```

| `--auth` | Behaviour |
|---|---|
| *(absent)* / `none` | Denies every request. Choosing a trust rule is an explicit operator act, so an exposed endpoint can never fall back to an unintended one. |
| `localhost` | Any loopback request is the single unattributed subject `local`. |
| `trusted-headers` | The proxy in front asserts the user in canonical `x-yession-*` headers, trusted verbatim. |

An unknown name fails the boot loudly rather than defaulting to anything.

`trusted-headers` **replaces** `localhost` and must never compose with it. Behind a
loopback-terminating proxy every request arrives over loopback, so composing them would
authenticate a header-less request as `local` — the bypass the proxy exists to prevent.

#### The header scheme

The proxy translates whatever its authenticator produces into Yession's canonical headers.
Values are UTF-8; Node lowercases names.

```
x-yession-user            REQUIRED — the subject, a stable unique user identifier.
                          Absent or blank ⇒ 401.
x-yession-user-name       optional display name
x-yession-user-email      optional email
x-yession-user-picture    optional avatar URL
x-yession-user-claims     optional JSON object of additional claims, carried opaquely
                          — recorded, not yet policy
```

The Manager is also the OIDC issuer its sessions bounce users through, so
`YESSION_MANAGER_URL` (below) has to name the origin a browser can actually reach. Getting
that wrong sends remote logins to an address only the host can resolve.

### Addressing

```sh
YESSION_MANAGER_URL=https://example.com          # the Manager: scheme + host, no path
YESSION_SESSION_URL=https://example.com/s/{id}   # sessions: a template
```

Set **both or neither**. Sessions reachable remotely while the Manager that authorizes their
users answers only on loopback would bounce every remote login to `127.0.0.1`, so that
combination is refused at boot rather than shipped as a surprise.

#### The Manager

Scheme + host, optional port, **no path**. Its routes are origin-anchored and its issuer is a
concatenation base (`<issuer>/connections/callback`), so a prefix would only work if the proxy
stripped it again. A path here is rejected.

That constraint is what lets the Manager share an origin with its sessions: the Manager owns
`/`, sessions own `/s/*`.

#### Sessions

A template over two placeholders — `{id}` (the session id) and `{port}` (its OS-assigned
port), exactly the two facts the registry stream publishes. Any proxy driven by that stream
can implement any template written with them.

```sh
# unset                                       -> http://127.0.0.1:{port}   loopback default
YESSION_SESSION_URL=https://example.com:{port}         # a port mirrored per session
YESSION_SESSION_URL=https://{id}.sessions.example.com  # a subdomain per session
YESSION_SESSION_URL=https://example.com/s/{id}         # a path per session
```

A bare origin with no placeholder is refused — every session would share one address. `{port}`
may appear in the authority but never in the **path**: a session must know its mount before it
binds a port, because the mount fixes its `<base href>`, its cookie `Path`, and the prefix it
strips off incoming requests.

#### Prefer `{id}`

A template naming `{port}` cannot give a session a stable address: the OS assigns a fresh port
per launch, so a session that stops and comes back arrives at an origin the browser has never
seen. Browser storage is partitioned by origin, so anything its user wrote **while it was
away** is stranded in a database nothing will open again.

Everything already sent is safe — it is on the server — so this is a real constraint of not
path-mounting rather than a defect. Yession says so in the product: a deployment whose
sessions move emits `<meta name="yession-ephemeral-storage" content="1">` on the session
shell, and the client's offline copy tells the truth instead of promising a sync it cannot
deliver. Under an `{id}` template the tag is absent and the promise holds.

#### The registry stream

Both a proxy binding and anything else that follows sessions read the same endpoint. It
publishes full snapshots, not deltas:

```sh
curl -sN http://127.0.0.1:8321/sessions/stream
# data: {"sessions":[{"id":"local-session","name":"…","port":57239,"pid":95225}]}
```

The template is deliberately **not** on the wire. The stream carries `id` and `port`; the
deployment applies its own template, so the prefix has exactly one home.

---

## Integrations

### Tailscale

One listener carries everything: the Manager at `/`, each session at `/s/<id>`.

#### Authorizing

**Use `--auth trusted-headers`.** `tailscale serve` already knows who is calling — it asserts
the identity of the calling tailnet node on every request —

```
Tailscale-User-Login        the user's login name
Tailscale-User-Name         display name
Tailscale-User-Profile-Pic  avatar URL
```

and it **overwrites** these on inbound requests, so a client that sends its own
`Tailscale-User-Login` does not get to choose who it is. That is what makes them safe to trust,
and it is the whole reason this integration can attribute work to real people rather than to a
shared subject.

Yession reads only its own canonical set and `serve` cannot rename headers, so a small
rewriting proxy sits between them. With Caddy:

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

Point the Manager's own mapping at Caddy rather than at the Manager, leaving session paths
untouched — sessions authenticate through the Manager as OIDC issuer, not by header:

```sh
tailscale serve --bg --http=8321 9000     # / -> caddy -> manager
```

and start the Manager with `--auth trusted-headers`.

Two details are load-bearing. `header_up -tailscale-*` strips the upstream headers after
translating them, so nothing downstream can read an identity Yession did not sanction. And the
Manager must be reachable **only** through the proxy — an exposed `127.0.0.1:8321` on a shared
box lets anyone local set `x-yession-user` to whatever they like, because under
`trusted-headers` that header *is* the subject.

> The identity headers and the overwrite behaviour above are verified against a live tailnet
> (Tailscale 1.98, plain HTTP). The Caddy composition is not: the directives come from
> [plan 07](plans/07-byo-user-authorization.md) and have not been stood up end to end.

##### What `--auth localhost` costs here

It is tempting on a single-machine install, and it is the one setting whose failure mode is
silent.

`serve` terminates on loopback, so **every** tailnet visitor reaches the Manager over
`127.0.0.1` — which is exactly what the `localhost` rule trusts. The device authorization that
let them onto the tailnet is real, but Yession never sees it: every visitor becomes the single
**unattributed** subject `local`. Nothing errors. Sessions open, work is saved, and all of it
is attributed to one shared identity.

On a personal tailnet that is coherent — there is one human, and `local` is their name. On a
tailnet with anyone else on it, it means the audit trail says `local` for work several people
did, and there is no way to tell afterwards which of them did what.

#### Addressing

```sh
# the Manager's own mapping, made once
tailscale serve --bg --http=8321 8321

# one session
tailscale serve --bg --http=8321 --set-path=/s/$id "http://127.0.0.1:$port/s/$id"

# remove it
tailscale serve --http=8321 --set-path=/s/$id off
```

with the Manager started as:

```sh
YESSION_MANAGER_URL=http://host.example.ts.net:8321 \
YESSION_SESSION_URL=http://host.example.ts.net:8321/s/{id} \
  yession-manager --auth trusted-headers
```

Both URLs name the tailnet origin, not loopback. The Manager is the OIDC issuer its sessions
bounce users through, so a loopback issuer here sends every remote login to an address only
this machine can resolve.

**The mount appears twice on purpose.** `--set-path` *strips* its prefix before proxying, but
a Yession session serves **under** its mount — it answers at `/s/<id>/…` and 404s at `/`.
Repeating the mount in the proxy target puts back exactly what `--set-path` removed. Serving
under its own mount is deliberate on the session's part: it does not assume a stripping proxy
in front of it.

Use `--https` instead of `--http` on a tailnet with certificates, and match the scheme in both
URLs. Derive it from one variable — the Manager and its sessions must land on the *same*
listener, or they are two origins and the shared-origin arrangement quietly stops being one.

##### Keeping mappings in step

Subscribe to the registry stream and reconcile level-based, applying the whole desired set per
frame. Two details earn their keep.

**Re-apply rather than diff on the path alone.** A session that is reaped and relaunched keeps
its path and changes its port, so "already served" would leave a handler aimed at a dead port
forever. `serve` is idempotent for an unchanged pair.

**Prune by prefix.** Everything under `/s/` belongs to the reconciler; the Manager's `/` does
not, so it cannot be torn down by a naive "current minus desired". That makes ownership
structural and retires any state file recording which mappings were created:

```sh
tailscale serve status -json \
  | jq -r '(.Web["host.example.ts.net:8321"].Handlers // {}) | keys[] | select(startswith("/s/"))'
```

A frame that never arrives is not an empty set. A Manager restart ends the stream, and
reconnecting yields a fresh snapshot that heals whatever was missed. Only a connect that
produces **no** frame at all means the Manager is unreachable — and since sessions cannot
outlive it, that is when the desired set is genuinely empty.

##### Rough edges

- **A bare `/s/<id>` has no canonicalising redirect.** The shell serves, but the auth cookie's
  `Path` is `/s/<id>/`, so that one request carries no cookie. Harmless — `<base href>` makes
  every sub-fetch absolute — but worth knowing before it is discovered.
- **The session id is not percent-encoded into the path.** Ids are Docker-safe by
  construction, so nothing can currently produce a path that needs it.
