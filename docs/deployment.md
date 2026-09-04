# Deployment

Three things are settled on the Manager before Yession is reachable from another machine:
**who** the humans are, **where** the Manager and its sessions answer, and **how long**
connected credentials live. Sessions inherit all of it by env.

The interfaces are what Yession asks of whatever sits in front of it; the integrations are
worked examples of satisfying them.

---

## Options and variables

What the Manager **decides** it takes as an option; what it **passes down** stays in the
environment, because inheritance is how a child gets it. So a mistyped setting is refused
where it is a decision, and `--help` is the list.

```sh
yession-manager --help
```

| shape | who sets it | example |
|---|---|---|
| an option | you, the operator, for this Manager | `--port`, `--idle-timeout`, `--data-dir` |
| `YESSION_MANAGER_URL` / `YESSION_SESSION_URL` | you; every session reads them too | see §Addressing |
| `YESSION_BIN_*` | you, or the package; names an executable a session runs | `YESSION_BIN_GIT`, `YESSION_BIN_BWRAP` |
| `YESSION_SESSION_*` | you; per-session policy, today set once per host | `YESSION_SESSION_WORK_BACKEND`, `YESSION_SESSION_RESOURCES` |
| `YESSION_LAUNCH` | nobody — the Manager mints it per launch | — |

A variable that moved onto the command line is **refused**, not ignored: a boot whose
environment still sets one stops and names the option to write instead. A setting nothing
reads is the behaviour you asked for silently not happening, which is how four renames cost
this project weeks of missing reaping, unfollowed upgrades and refused sandboxes before
anybody noticed.

`YESSION_LAUNCH` is the launch's control secret — custody of the session's secrets and its
right to register as an OIDC client — so setting it by hand is impersonating a session, and it
never reaches a sandboxed command.

`YESSION_SESSION_RESOURCES` names a **resources profile**: named resources a repo's
`yession.yaml` selects by name, so a repo cannot exceed what the operator declared. `resources:`
is what the host can offer; `default:` is what every sandbox gets unasked. Declared and not
defaulted means available and not granted.

---

## Interfaces

### Authorizing

Yession authenticates nobody itself; it names a **trust rule** for how a request's subject is
established, once, at start:

```sh
yession-manager --auth localhost         # single machine
yession-manager --auth trusted-headers   # an authenticating proxy in front
```

| `--auth` | Behaviour |
|---|---|
| *(absent)* / `none` | Denies every request, so an exposed endpoint cannot fall back to an unintended rule. |
| `localhost` | Any loopback request is the single unattributed subject `local`. |
| `trusted-headers` | The proxy in front asserts the user in canonical `x-yession-*` headers, trusted verbatim. |

An unknown name or option fails the boot, because a silently ignored `--auth` would be a
Manager that refuses everyone. Every bin answers `--help`.

`trusted-headers` replaces `localhost`; they never compose, because behind a
loopback-terminating proxy every request is loopback and a header-less one would become
`local` — the bypass the proxy exists to prevent.

#### What the trust rule decides about credentials

The rule also decides who **owns** a Claude or GitHub account connected for "all my sessions":

| `--auth` | "All my sessions" means |
|---|---|
| `trusted-headers` | That user; nobody else's turn runs on it. |
| `localhost` | This deployment; one connection serves every visitor, browser and device. |

Under `localhost` that is the rule stated honestly — anyone who reaches the Manager can
already drive every session — but see [what it costs](#what---auth-localhost-costs-here)
before choosing it.

### Credentials

How long a connected credential lives, chosen once at start:

```sh
yession-manager --secrets ephemeral   # dies with this Manager
yession-manager --secrets durable     # persistence required, or refuse to boot
```

| `--secrets` | Behaviour |
|---|---|
| *(absent)* | Durable where the OS credential manager answers; in-memory, with a warning, where none does. |
| `durable` | A host with no usable credential manager refuses the boot rather than run a store that dies. |
| `ephemeral` | In-memory even where a credential manager exists; an existing `secrets.json` is left unread. |

Unknown names fail the boot, as for `--auth`.

Durable secrets are one AES-256-GCM file in the data directory, keyed from the macOS
Keychain / Windows Credential Manager / Linux Secret Service. There is no key file and no key
variable, so a host without a credential manager refuses persistence instead of degrading it.

`--secrets ephemeral` is the compensating control for `localhost`: the deployment-wide
credential lives only as long as this Manager process.

#### The header scheme

The proxy translates its authenticator's output into these; values UTF-8, names lowercased
by Node:

```
x-yession-user            REQUIRED — the subject, a stable unique user identifier.
                          Absent or blank ⇒ 401.
x-yession-user-name       optional display name
x-yession-user-email      optional email
x-yession-user-picture    optional avatar URL
x-yession-user-claims     optional JSON object of additional claims, carried opaquely
                          — recorded, not yet policy
```

The Manager is also the OIDC issuer sessions bounce users through, so `YESSION_MANAGER_URL`
must be an origin browsers can reach, or remote logins land on an address only the host
resolves.

### Session lifetime

```sh
yession-manager --idle-timeout 30m       # or 90s, 2h; absent means never
```

The Manager stops sessions nobody is using. A session reports busy or idle over its control
channel (a connected peer, a running turn, a command, a non-empty queue) and the Manager reaps
on **silence**, so no single report has to arrive. Absent means never.

Two costs: a reaped launch loses its OAuth client registration and user bindings, so the next
visitor signs in again (invisible under `localhost`, a re-bounce under `trusted-headers`); and a
session wedged after readiness is stopped as `NeverReported` rather than diagnosed.

Reaping also buys rolling upgrades: point `--spawn-bin` at a path that floats with your
builds and sessions upgrade as they idle out, while the Manager — whose restart evicts
everybody — waits until nothing runs. A MAJOR version difference refuses the launch, since the
control protocol may disagree, which drains the running set and hands you that moment.

### Addressing

A deployment is **loopback** (nothing set, everything on `127.0.0.1`) or **fronted** (both set,
everything public). A half-set pair is refused at boot, because either half alone points
somebody at loopback: sessions without their issuer bounce remote logins to `127.0.0.1`; a
public issuer without fronted sessions registers callbacks nobody remote can reach.

```sh
YESSION_MANAGER_URL=https://example.com          # the Manager: scheme + host, no path
YESSION_SESSION_URL=https://example.com/s/{id}   # sessions: a template
```

#### The Manager

Scheme + host, optional port, **no path** — its routes are origin-anchored and its issuer is a
concatenation base, so a prefix is rejected. That is what lets it share an origin with its
sessions: the Manager owns `/`, sessions own `/s/*`.

The origin must resolve **from this host too**: every session runs OIDC discovery, JWKS and
token requests against it at boot, and a session that cannot authorize its users refuses to
half-start. Split-horizon DNS or a proxy on an external interface only fails registration.

#### Sessions

A template over `{id}` (the session id) and `{port}` (its OS-assigned port) — exactly what the
registry stream publishes, so any proxy driven by the stream can implement any template:

```sh
YESSION_SESSION_URL=https://example.com:{port}         # a port mirrored per session
YESSION_SESSION_URL=https://{id}.sessions.example.com  # a subdomain per session
YESSION_SESSION_URL=https://example.com/s/{id}         # a path per session
```

A template with no placeholder is refused, since every session would share one address.
`{port}` may appear in the authority but never the **path**: a session must know its mount
before it binds, because the mount fixes its `<base href>`, cookie `Path` and the prefix it
strips.

#### Prefer `{id}`

A `{port}` template gives a session a new origin per launch, and browser storage is partitioned
by origin — so anything written while the session was away is stranded in a database nothing
reopens. The shell then emits `<meta name="yession-ephemeral-storage" content="1">` and the
client stops promising a sync it cannot deliver; under `{id}` the tag is absent and the
promise holds. Everything already sent is on the server either way.

#### The registry stream

Full snapshots, never deltas, so a subscriber applies each frame whole and a missed one costs
nothing:

```sh
curl -sN http://127.0.0.1:8321/sessions/stream
# data: {"sessions":[{"id":"local-session","name":"…","port":57239,"pid":95225}]}
```

It is gated by the trust rule like every management route, so under `trusted-headers` every
loopback caller — a proxy binding, a health check, a tracker — asserts its own
`x-yession-user`, or gets 401s and no frames.

The template is not on the wire: the stream carries `id` and `port`, the deployment applies
its template, and the prefix has one home. [`examples/proxy/main.mjs`](../examples/proxy/) is a
reference reconciler; §Tailscale composes it.

---

## Integrations

### GitHub

A session clones over HTTPS and watches pull requests with a credential a person signs in for
from inside it. There is no default client id: register your own GitHub App, or the only way
in is pasting a token.

Register an App (not an OAuth App), and:

- **Enable device flow** — the flow the session runs; the code exchange needs a client secret
  the Manager's public-client broker cannot carry.
- **Set no callback URL that matters, generate no client secret** — neither is used.
- **Install it on the repositories it should reach** — a device-flow token sees the
  intersection of the user's access and the App's installations, and nothing in Yession
  re-checks that.
- **Leave user-token expiration on** — the Manager holds the refresh token and rotates before
  each turn; off yields a permanent token, and nothing tells you which you registered.
- **Point its webhook at `<YESSION_MANAGER_URL>/hooks/github`** if you declared a hook
  endpoint (§Webhooks), with the secret the manager page shows; polling still works without
  it, a hook only advances its clock.

Then name the client id:

```
YESSION_GITHUB_CLIENT_ID=Iv1.0123456789abcdef
```

Unset, the sign-in surface says so and offers the paste path. A pasted `github_pat_…`/`ghp_…`
works and **bypasses the installation rule** (recorded in GAPS.md); a pasted `ghu_…`/`gho_…` is
refused where device flow exists, since it expires in hours and cannot rotate.

`YESSION_GITHUB_DEVICE_URL`, `YESSION_GITHUB_TOKEN_URL`, `YESSION_GITHUB_USER_URL` and
`YESSION_GITHUB_API_URL` override the four endpoints, which is how the test suites drive it.

### Webhooks

The Manager takes signed deliveries and hands them to sessions that asked, without reading
them: a session declares a filter over paths, the Manager matches
([ADR](decisions/2026-08-29-the-manager-relays-hooks-it-cannot-read.md)).

```
YESSION_WEBHOOK_ENDPOINTS=github
```

Each is served at `<YESSION_MANAGER_URL>/hooks/<name>`, so this needs a fronted deployment.
The manager page shows, per endpoint, the address and the secret to sign with. The secret is
derived from the credential manager's key, not chosen — which is why endpoints are refused
under an ephemeral store: a secret that changed every restart would break deliveries silently.

Rotate by bumping the counter; both the new secret and the one before are accepted until you
bump again:

```
YESSION_WEBHOOK_ENDPOINTS=github@1
```

Verification is HMAC-SHA256 over the raw body, hex, in `X-Hub-Signature-256` behind `sha256=`
(the WebSub convention: GitHub, Shopify, Linear). Override per endpoint as
`header:encoding:prefix`:

```
YESSION_WEBHOOK_SIGNATURE_GITHUB=x-shopify-hmac-sha256:base64:
```

Not supported: schemes signing a timestamped string (Stripe `<t>.<body>`, Slack
`v0:<ts>:<body>`) — those need the scheme, not configuration.

### Tailscale

One origin — the Manager at `/`, each session at `/s/<id>` — behind one reverse proxy of your
own:

```
browser ──tailnet──▶ tailscale serve ──▶ your proxy ──┬──▶ manager      /
                     TLS + identity      one mapping    └──▶ session i    /s/<id>
```

`serve` does what only it can (TLS on the tailnet, who is calling) through one mapping;
routing, identity translation and stripping live in the proxy, in a file. The pieces are in
[`examples/proxy`](../examples/proxy/); this section is what they implement, for a deployment
on another proxy.

#### Authorizing

**Use `--auth trusted-headers`.** `serve` asserts the calling tailnet user on every request and
**overwrites** any a client sent, which is what makes these trustworthy:

```
Tailscale-User-Login        the user's login name
Tailscale-User-Name         display name
Tailscale-User-Profile-Pic  avatar URL (empty where the account has none)
```

It does not touch `x-yession-*`, so a forged `x-yession-user` reaches the proxy intact — the
translation below is also the strip. `serve` cannot rename headers, so the proxy does; with
Caddy, on the Manager's route:

```caddyfile
reverse_proxy 127.0.0.1:8321 {
	header_up x-yession-user         {header.Tailscale-User-Login}
	header_up x-yession-user-name    {header.Tailscale-User-Name}
	header_up x-yession-user-picture {header.Tailscale-User-Profile-Pic}
	header_up -x-yession-user-email
	header_up -x-yession-user-claims
	header_up -tailscale-*
}
```

- **Set what `serve` asserts, delete what it does not** — a set replaces the client's value, so
  the three sets are the strip for those three; email and claims must be deleted by name or a
  client chooses its own.
- **Delete by name, not `-x-yession-*`** — Caddy applies deletions after sets, so the wildcard
  strips what was just asserted and the Manager sees nobody (measured, Caddy 2.11.2).
- **The Manager is reachable only through the proxy** — under `trusted-headers` the header
  *is* the subject, so on a shared box bind the Manager where only the proxy can dial.

Session routes carry none of this: a session authenticates through the Manager as OIDC issuer,
never by header.

**Loopback callers assert themselves** — the reconciler, a health check, a tracker each send an
`x-yession-user` naming what they are (`main.mjs --as proxy-map`), because the gate is the same
for a request that never left the machine. Under `localhost` the header is read by nothing.

> Verified against a live tailnet: Tailscale 1.102.3 with HTTPS certificates, Caddy 2.11.2,
> macOS — the identity headers, the overwrite, the `x-yession-*` pass-through, the deletion
> ordering, and the composition below with a Manager and a session behind it.

##### What `--auth localhost` costs here

`serve` terminates on loopback, so every tailnet visitor is loopback and becomes the one
unattributed subject `local`. Nothing errors; the audit trail says `local` for everyone, and a
Claude account connected for "all my sessions" belongs to the deployment — any visitor's turn
spends against it. Coherent on a personal tailnet; a silent failure the moment there is a
second person.

Two bounds, answering different questions:

- **`--secrets ephemeral`** — keep the sharing, lose the permanence: the credential dies with
  this Manager process.
- **`--auth trusted-headers`** — lose the sharing: each human owns their own credential.

#### Addressing

One mapping, made once (`serve` config persists in tailscaled's state):

```sh
tailscale serve --bg --https=8321 9000        # everything -> your proxy
```

```sh
YESSION_MANAGER_URL=https://host.example.ts.net:8321 \
YESSION_SESSION_URL=https://host.example.ts.net:8321/s/{id} \
  yession-manager --auth trusted-headers
```

Both URLs name the tailnet origin, because the Manager is the issuer sessions bounce users
through and a loopback issuer sends remote logins to this machine only.

The proxy routes `/s/<id>` to the session's port **without stripping the mount**: a session
serves under its mount (it answers at `/s/<id>/…` and 404s at `/`), so in Caddy the route is
`handle`, never `handle_path`, with the bare port upstream.

Use `--https` where the tailnet issues certificates, and derive the scheme in both URLs from
one variable, or the Manager and its sessions become two origins. `--https` also matters
without certificates as the reason: a browser withholds the Cache API and service workers
outside a secure context, so over plain HTTP a session keeps no history on the device and
cannot open cold offline. Loopback is a secure context, so the default is unaffected.

##### Keeping the session routes in step

A session's port is OS-assigned per launch, so a process — not a person — writes its route.
`examples/proxy/main.mjs` renders every running session through a template into one file the
proxy reads:

```sh
node examples/proxy/main.mjs --manager http://127.0.0.1:8321 --as proxy-map \
  --out /var/lib/yession/proxy/sessions.caddy \
  --empty '# no running sessions' \
  --template '@s_{id} path /s/{id} /s/{id}/*
handle @s_{id} {
	reverse_proxy 127.0.0.1:{port}
}'
```

Caddy under `--watch` re-adapts every second and re-reads the import; a proxy that does not
watch its config gets `--reload`. Any reconciler against the stream should keep three rules:

- **Render the whole set, every frame** — a frame is the running set, so a reaped-and-relaunched
  session (same path, new port) can never leave a route aimed at a dead port. Rewrite only on
  change, beside-then-rename, so the proxy never reads half a file.
- **Nothing persists between runs** — the file is the state and the next frame replaces it, so
  no restart needs a cleanup pass, unlike routes pushed into an ingress's own persisted state.
- **Which silence means what** — a stream ending is a Manager restart (keep the map; the
  reconnect's snapshot heals it); a connection refused is no Manager (write it empty; sessions
  cannot outlive him); a 401 is a misconfigured header (log, keep, retry).

##### Rough edges

- **A bare `/s/<id>` has no canonicalising redirect** — the auth cookie's `Path` is `/s/<id>/`,
  so that one request carries no cookie; harmless because `<base href>` makes every sub-fetch
  absolute.
- **The session id is not percent-encoded into the path** — ids are Docker-safe by
  construction, so nothing can yet produce one that needs it.

### Nix

`packages.<system>.default` is `yession-manager` + `yession-session`, wrapped with the native
WebRTC addon built from source and `YESSION_BIN_CLAUDE` / `YESSION_BIN_GIT` defaulted to store
paths. The module below is distilled from the deployment the project itself runs — home-manager
under nix-darwin, composed with §Tailscale.

Pin the input and leave its nixpkgs alone, because the addon is built against that pin:

```nix
inputs.yession.url = "github:trinketworks/yession";
# NOT `inputs.yession.inputs.nixpkgs.follows = "nixpkgs"`: that trades a cached,
# tested native build for an untested one.
```

Three agents are the whole of §Tailscale, with the example's files used as they ship:

```nix
{ config, pkgs, inputs, ... }:
let
  yession = inputs.yession.packages.${pkgs.stdenv.hostPlatform.system}.default;
  proxy = "${inputs.yession}/examples/proxy";   # the Caddyfile and the reconciler
  origin = "https://host.example.ts.net:8321";  # scheme + host once, feeding both URLs
  managerPort = 8321;
  proxyPort = 9000;           # what `tailscale serve --bg --https=8321 9000` targets
  proxyDir = "${config.home.homeDirectory}/.local/state/yession/proxy";
  logDir = "${config.home.homeDirectory}/Library/Logs/yession";

  # The resources profile. Paths as the kernel sees them (/private/etc, not /etc):
  # the profile refuses symlinked spellings. `nix-container-store` is what this
  # repository's own yession.yaml reaches with `wants:`.
  resources = pkgs.writeText "yession-resources.yaml" ''
    version: 1
    resources:
      nix-container-store:
        volume: { name: yession-nix, at: /nix }
      ca:
        mount: { from: /private/etc/ssl/cert.pem, mode: read }
        env:
          SSL_CERT_FILE: /private/etc/ssl/cert.pem
          NIX_SSL_CERT_FILE: /private/etc/ssl/cert.pem
  '';
in
{
  launchd.agents.yession-manager = {
    enable = true;
    config = {
      ProgramArguments = [
        "${yession}/bin/yession-manager"
        "--auth" "trusted-headers"
        "--port" (toString managerPort)
        # The default data dir is RELATIVE (`.yession`) and launchd does not start in $HOME.
        "--data-dir" "${config.home.homeDirectory}/.yession"
        "--idle-timeout" "30m"
      ];
      RunAtLoad = true;
      KeepAlive = true;
      EnvironmentVariables = {
        HOME = config.home.homeDirectory;
        YESSION_SESSION_RESOURCES = "${resources}";
        # The two addresses stay variables: every session reads them too.
        YESSION_MANAGER_URL = origin;
        YESSION_SESSION_URL = "${origin}/s/{id}";
        PATH = "/usr/bin:/bin:/usr/sbin:/sbin";
      };
      StandardOutPath = "${logDir}/manager.out.log";
      StandardErrorPath = "${logDir}/manager.err.log";
    };
  };

  # The proxy, loopback only, re-reading the session map under --watch.
  launchd.agents.yession-proxy = {
    enable = true;
    config = {
      ProgramArguments = [
        "${pkgs.caddy}/bin/caddy" "run"
        "--config" "${proxy}/caddy/Caddyfile" "--adapter" "caddyfile" "--watch"
      ];
      RunAtLoad = true;
      KeepAlive = true;
      EnvironmentVariables = {
        HOME = config.home.homeDirectory;
        YESSION_PROXY_PORT = toString proxyPort;
        YESSION_PROXY_MANAGER = "127.0.0.1:${toString managerPort}";
        YESSION_PROXY_SESSIONS = "${proxyDir}/sessions*.caddy";
        XDG_DATA_HOME = "${proxyDir}/caddy";
        XDG_CONFIG_HOME = "${proxyDir}/caddy";
      };
      StandardOutPath = "${logDir}/proxy.out.log";
      StandardErrorPath = "${logDir}/proxy.err.log";
    };
  };

  # The session routes. KeepAlive: it holds the stream open for its whole life.
  launchd.agents.yession-proxy-map = {
    enable = true;
    config = {
      ProgramArguments = [
        "${pkgs.nodejs_24}/bin/node" "${proxy}/main.mjs"
        "--manager" "http://127.0.0.1:${toString managerPort}"
        "--as" "proxy-map"
        "--out" "${proxyDir}/sessions.caddy"
        "--empty" "# no running sessions"
        "--template" ''
          @s_{id} path /s/{id} /s/{id}/*
          handle @s_{id} {
            reverse_proxy 127.0.0.1:{port}
          }
        ''
      ];
      RunAtLoad = true;
      KeepAlive = true;
      EnvironmentVariables.HOME = config.home.homeDirectory;
      StandardOutPath = "${logDir}/proxy-map.out.log";
      StandardErrorPath = "${logDir}/proxy-map.err.log";
    };
  };
}
```

Four choices in it are deliberate:

- **User agents, not root LaunchDaemons** — durable secrets ride the login Keychain, which only
  a login session has.
- **The profile and the Caddyfile ride the store** — one switch moves policy and process
  together, one rollback restores both, and no live file drifts.
- **The session map does not** — it is written by a process from state that changes per launch,
  so it lives under `.local/state` and nothing else writes there.
- **A plist change restarts the Manager, and that evicts every session** — so for frequent
  upgrades point `--spawn-bin` at a symlink you promote — a constant string, so the
  agent's `ProgramArguments` do not change when the input does — and let sessions roll onto
  new builds as they idle (§Session lifetime). The proxy
  and the map ride out a Manager restart: the map empties while it is unreachable and refills
  on the first frame after.
