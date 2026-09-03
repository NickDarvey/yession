# A reverse proxy in front

The pieces that put one reverse proxy — yours — in front of a Yession deployment, so that
everything outside the machine reaches the Manager and every session through a single
address, and the identity whatever authenticated the caller established arrives at the
Manager as the canonical `x-yession-*` headers it reads.

Nothing here knows anything about Yession beyond two documented facts
([deployment.md](../../docs/deployment.md)): the registry stream at `/sessions/stream`, and
the two placeholders — `{id}` and `{port}` — a session template is written in. A deployment
in another environment keeps `main.mjs` and swaps the Caddyfile.

| | |
|---|---|
| [main.mjs](main.mjs) | Follows the registry stream and renders every running session through a template into one file, atomically, level-based. Proxy-agnostic: the template is the proxy's own syntax. |
| [caddy/Caddyfile](caddy/Caddyfile) | One Caddy site behind `tailscale serve`: sessions from the file above, the Manager for everything else, and the Tailscale identity headers translated on the way in. |

## The shape

```
browser ──tailnet──▶ tailscale serve ──▶ caddy :9000 ──┬──▶ manager :8321      /
   (TLS, identity)     one mapping        (this dir)     ├──▶ session  :51321    /s/<id>
                                                         └──▶ session  :57626    /s/<id>
                                              ▲
                                    sessions.caddy  ◀── main.mjs ◀── /sessions/stream
```

`serve` does what only it can — terminate TLS on the tailnet and say who is calling — and
exactly one mapping. Everything a person would want to read or change is in the proxy:
which paths go where, what becomes of the identity, what is stripped. Adding a session
touches nothing but the map file, which is why the map is a file rather than a sequence of
commands against the ingress: it is one write, it is atomic, and Caddy re-reads it on its
own under `--watch`.

## Running it

The Manager, fronted at the tailnet origin and trusting the headers the proxy asserts:

```sh
YESSION_MANAGER_URL=https://host.example.ts.net:8321 \
YESSION_SESSION_URL=https://host.example.ts.net:8321/s/{id} \
  yession-manager --auth trusted-headers
```

The map, following the Manager. Under `trusted-headers` a header-less subscriber gets a 401
and no frames, so the map asserts a subject of its own from inside the loopback trust boundary
the proxy defines — `--as` names it; under `localhost` the header is read by nothing:

```sh
node main.mjs --manager http://127.0.0.1:8321 --as proxy-map \
  --out /var/lib/yession/proxy/sessions.caddy \
  --empty '# no running sessions' \
  --template '@s_{id} path /s/{id} /s/{id}/*
handle @s_{id} {
	reverse_proxy 127.0.0.1:{port}
}'
```

`--empty` is what the file says when nothing runs. Caddy warns about an empty import, and
under `--watch` it would say so once a second for as long as the deployment is idle; a
comment is a file with nothing in it that no proxy minds.

The proxy, watching its config so a rewritten map is live within a second:

```sh
YESSION_PROXY_PORT=9000 \
YESSION_PROXY_MANAGER=127.0.0.1:8321 \
YESSION_PROXY_SESSIONS='/var/lib/yession/proxy/sessions*.caddy' \
  caddy run --config caddy/Caddyfile --adapter caddyfile --watch
```

It listens on loopback (`YESSION_PROXY_BIND` to change that, and read the Caddyfile's
reason first): anything that can dial the proxy directly can write its own
`Tailscale-User-Login`, so only `serve` may.

And the ingress, made once — `serve` config persists in tailscaled's own state:

```sh
tailscale serve --bg --https=8321 9000
```

A proxy that does not watch its own config gets `--reload '<command>'` on `main.mjs`, run
after every write — `nginx -s reload`, for one.

## What it demonstrates

**The map is level-based.** A frame is the whole running set, so the file is rendered from
scratch every time; a missed frame costs nothing and there is no state to persist between
runs. Rewritten only when the rendering changed, and always beside-then-rename, so a reader
never sees half a file.

**Which silence means what.** The stream ending is not the sessions ending — a Manager
restart closes it, and the reconnect's first frame is a fresh snapshot — so the map is left
alone. A connection *refused* is: sessions are the Manager's children and cannot outlive it,
so that is the one case where the map is written empty. And a 401 is neither; it is logged
and retried, because a misconfigured header must never unmap a deployment.

**The identity translation has one home, and an ordering trap.** `serve` overwrites the
`Tailscale-User-*` headers on every request, which is what makes them trustworthy; the
Caddyfile SETS the three Yession reads from them — a set replaces what the client sent — and
DELETES the two it reads that `serve` does not assert. Named one by one, not `-x-yession-*`:
Caddy applies deletions after sets, so the wildcard would strip what was just asserted and
the Manager would see nobody. Measured, which is how it was found.

**Sessions get no identity.** A session authenticates its visitors through the Manager as
OIDC issuer, not by header, so the session routes carry none of the above — and a proxy that
can only forward, not translate, still fronts sessions correctly.

Verified against a live tailnet: Tailscale 1.102.3 with HTTPS certificates, Caddy 2.11.2,
macOS. `tests/Yession.Tests/ProxyMap.fs` drives `main.mjs` against a real Manager and a real
session; the Caddyfile is checked by hand, since the suite has no tailnet.
