# One reverse proxy in front, and the map that feeds it

> Decided 2026-09-03 · Supersedes the per-session `tailscale serve --set-path` binding
> `deployment.md` §Tailscale used to prescribe · Related:
> [deployment.md](../deployment.md) §Tailscale, [examples/proxy](../../examples/proxy),
> [2026-08-28-deployment-fronts-everything-or-nothing.md](2026-08-28-deployment-fronts-everything-or-nothing.md)

## Decision

A fronted deployment puts **one reverse proxy of the operator's choosing** between the
ingress and Yession. The ingress (`tailscale serve`, here) does only what nothing else can —
terminate TLS and establish who is calling — through a single mapping to that proxy. The
proxy routes `/` to the Manager and `/s/<id>` to each session's port, and translates the
ingress's identity headers into the canonical `x-yession-*` set in the one place that can
also strip what a client sent.

Yession's part is two documented facts and one example. The facts: the registry stream, and
the two placeholders a session template is written in. The example (`examples/proxy`):
a proxy-agnostic process that renders the stream through a template into one file, and a
Caddyfile that reads that file and does the translation. Both are copied and owned by a
deployment, not shipped in the bins.

What Yession does **not** do:

- **Relay session traffic through the Manager.** The Manager stays a supervisor and an
  issuer; a session is addressable itself. A relay would make every proxy configuration a
  one-liner, and it is the shape the fronts-everything decision already declined for the
  reasons it gave there — it fights where deployments are going.
- **Drive the ingress's own per-path API.** The deployment this project runs on did that for
  six weeks: a reconciler that ran `tailscale serve --set-path` per session. It worked, and it
  was the wrong layer. Each change was a CLI call against a daemon (100 ms–1 s, with a
  watchdog for the times it hung); the routing lived in tailscaled's state where nothing
  versioned it; and `serve` cannot rename a header, so the one thing the whole arrangement
  was for — attributing work to the person who did it — needed a second proxy anyway. Once
  there is a proxy, the ingress should have one mapping and the proxy should have the rest.

## The contract, stated

For a proxy to front Yession it needs:

1. One upstream for the Manager, and the Manager's origin in `YESSION_MANAGER_URL`.
2. One upstream per running session, at whatever `YESSION_SESSION_URL` promises. The port
   comes from `/sessions/stream`; the path or host from the template. `examples/proxy/main.mjs`
   is the reference reconciler, and a deployment may write its own against the same stream.
3. Under `--auth trusted-headers`: SET `x-yession-user` (and the optional name, email,
   picture, claims) from what the ingress verified, DELETE every `x-yession-*` a client could
   have sent that the proxy does not set, and be the only non-loopback path to the Manager.
4. Loopback callers inside that boundary — the map, a health check, a tracker — assert a
   subject of their own on management routes, because the gate is the same for them.

## What it costs

A second hop on every request, and one more long-running process (the proxy) plus one small
one (the map). Against that: the ingress configuration collapses to a line, the routing and
the identity rules are in a file under version control, and adding a session is one atomic
write the proxy picks up on its own.

Anyone who ran the per-session `serve` binding re-points its one Manager mapping at the
proxy and retires the reconciler; the URLs the Manager was started with do not change.

## What would change it

- **Sessions on stable addresses.** If a session listened where its id alone said — a Unix
  socket under the data directory, say — a proxy could dial it from the request path with no
  map at all, and `main.mjs` would have nothing to do. That is the follow-up this decision
  points at; today a session's port is OS-assigned per launch, so the map is the honest
  minimum.
- **An ingress that translates headers.** If `serve` (or its successor) could rename a header,
  a deployment wanting attribution and nothing else could go back to a single hop. The map
  would still be needed for the session routes, so the proxy would still be the simpler place.
