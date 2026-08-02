# Addressing

Where Yession is reachable from outside the machine it runs on. Two environment variables set
on the **Manager**; sessions inherit them by plain env inheritance.

```sh
YESSION_MANAGER_URL=https://example.com          # the Manager: scheme + host, no path
YESSION_SESSION_URL=https://example.com/s/{id}   # sessions: a template
```

Set **both or neither**. Sessions reachable remotely while the Manager that authorizes their
users answers only on loopback would bounce every remote login to `127.0.0.1`, so that
combination is refused at boot rather than shipped as a surprise.

## The Manager

Scheme + host, optional port, **no path**. Its routes are origin-anchored and its OIDC issuer
is a concatenation base (`<issuer>/connections/callback`), so a prefix would only work if the
proxy stripped it again. A path here is rejected.

That constraint is what lets the Manager share an origin with its sessions: the Manager owns
`/`, sessions own `/s/*`.

## Sessions

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

### Prefer `{id}`

A template naming `{port}` cannot give a session a stable address: the OS assigns a fresh port
per launch, so a session that stops and comes back arrives at an origin the browser has never
seen. Browser storage is partitioned by origin, so anything its user wrote **while it was
away** is stranded in a database nothing will open again.

Everything already sent is safe — it is on the server — so this is a real constraint of not
path-mounting rather than a defect. Yession says so in the product: a deployment whose
sessions move emits `<meta name="yession-ephemeral-storage" content="1">` on the session
shell, and the client's offline copy tells the truth instead of promising a sync it cannot
deliver. Under an `{id}` template the tag is absent and the promise holds.

## Tailscale

One listener carries both: the Manager at `/`, each session at `/s/<id>`.

```sh
# the Manager's own mapping, made once
tailscale serve --bg --http=8321 8321

# one session
tailscale serve --bg --http=8321 --set-path=/s/$id "http://127.0.0.1:$port/s/$id"

# remove it
tailscale serve --http=8321 --set-path=/s/$id off
```

**The mount appears twice on purpose.** `--set-path` *strips* its prefix before proxying, but
a Yession session serves **under** its mount — it answers at `/s/<id>/…` and 404s at `/`.
Repeating the mount in the proxy target puts back exactly what `--set-path` removed. Serving
under its own mount is deliberate on the session's part: it does not assume a stripping proxy
in front of it.

Use `--https` instead of `--http` for a tailnet with certificates, and match the scheme in
`YESSION_SESSION_URL`. Derive it from one variable — the Manager and its sessions must land on
the *same* listener, or they are two origins and the shared-origin arrangement quietly stops
being one.

### Keeping mappings in step

The Manager publishes running sessions as a stream of full snapshots:

```sh
curl -sN http://127.0.0.1:8321/sessions/stream
# data: {"sessions":[{"id":"local-session","name":"…","port":57239,"pid":95225}]}
```

Reconcile level-based, not incrementally: apply the whole desired set per frame. Two details
earn their keep.

**Re-apply rather than diff on the path alone.** A session that is reaped and relaunched keeps
its path and changes its port, so "already served" would leave a handler aimed at a dead port
forever. `serve` is idempotent for an unchanged pair.

**Prune by prefix.** Everything under `/s/` is the reconciler's; the Manager's `/` is not, so
it cannot be torn down by a naive "current minus desired". That makes ownership structural and
retires any state file recording which mappings were created:

```sh
tailscale serve status -json \
  | jq -r '(.Web["example.com:8321"].Handlers // {}) | keys[] | select(startswith("/s/"))'
```

The prefix is not on the wire — the stream carries `id` and `port`, and the deployment applies
its own template — so the reconciler holds the one copy of it.

A frame that never arrives is not an empty set: a Manager restart ends the stream, and
reconnecting yields a fresh snapshot that heals whatever was missed. Only a connect that
produces **no** frame at all means the Manager is unreachable, and since sessions cannot
outlive it, that is when the desired set is genuinely empty.

## Rough edges

- **A bare `/s/<id>` has no canonicalising redirect.** The shell serves, but the auth cookie's
  `Path` is `/s/<id>/`, so that one request carries no cookie. Harmless — `<base href>` makes
  every sub-fetch absolute — but worth knowing before it is discovered.
- **The session id is not percent-encoded into the path.** Ids are Docker-safe by
  construction, so nothing can currently produce a path that needs it.
