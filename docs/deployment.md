# Deployment

Three things must be settled before Yession is reachable from anywhere but the machine it runs
on: **who** the humans at this Manager are, **where** the Manager and its sessions answer, and
**how long** the credentials they connect live. All three are configured on the Manager;
sessions inherit what they need by plain env inheritance.

The interfaces below are what Yession asks of whatever sits in front of it. The integrations
are worked examples of one thing satisfying them.

---

## What the variable names tell you

Yession's environment splits three ways, and the NAME says which — so "may I set this?" is
answerable without a table.

| shape | who sets it | example |
|---|---|---|
| `YESSION_*` | **you**, the operator. Ordinary configuration. | `YESSION_MANAGER_URL`, `YESSION_IDLE_TIMEOUT` |
| `YESSION_BIN_*` | you, and it names an executable on this host. | `YESSION_BIN_GIT`, `YESSION_BIN_BWRAP` |
| `YESSION_SESSION_*` | you, per session. | `YESSION_SESSION_WORK_BACKEND`, `YESSION_SESSION_RESOURCES` |
| `YESSION_LAUNCH` | **nobody.** The Manager mints it per launch and the session decodes it. | — |

`YESSION_LAUNCH` carries the launch's control secret, which is custody of that session's
secrets and its authority to register as an OIDC client. Setting it by hand is claiming to
be a session the Manager started. It never reaches a sandboxed command — the host baseline
is an allowlist — and nothing but the Manager should ever write it.

The split matters most for the middle two. Anything under `YESSION_BIN_*` names a binary
this host will execute, so it is the operator's alone. Anything under `YESSION_SESSION_*` is
a per-session policy that happens, today, to be set once for the whole host.

What a sandbox may READ, WRITE and REACH is declared in a **resources profile** —
`YESSION_SESSION_RESOURCES` names the file — where each resource has a name, and a repo's
`yession.yaml` selects names rather than writing paths and hostnames of its own, so a repo
can never exceed what the operator declared. The operator's two acts there are deliberately
separate: `resources:` is what this host CAN offer, and `default:` is what every sandbox
gets without asking. A name declared and not defaulted is available and not granted —
offering a path never forces it on everything.

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

An unknown name — or an unknown OPTION — fails the boot loudly rather than defaulting to
anything: a silently ignored `--auth` is deny-everything, so a typo would present as a
Manager that refuses everyone. Every bin answers `--help` with what it accepts.

`trusted-headers` **replaces** `localhost` and must never compose with it. Behind a
loopback-terminating proxy every request arrives over loopback, so composing them would
authenticate a header-less request as `local` — the bypass the proxy exists to prevent.

#### What the trust rule decides about credentials

The rule does not only name subjects for the audit trail. It decides **who owns** a Claude or
GitHub account connected from inside a session for "all my sessions":

| `--auth` | "All my sessions" means |
|---|---|
| `trusted-headers` | That user. Nobody else's turn can run on it, in any session. |
| `localhost` | **This deployment.** Every visitor is the same unattributed subject, so one connection serves every session, browser and device that reaches this Manager. |

Under `localhost` that is not a leak, it is the trust rule stated honestly: anyone who can
reach the Manager can already open and drive every session on it. It also has a cost worth
seeing before you choose it — see [below](#what---auth-localhost-costs-here).

### Credentials

How long a connected credential lives, chosen once at Manager start:

```sh
yession-manager --secrets ephemeral   # dies with this Manager
yession-manager --secrets durable     # persistence required, or refuse to boot
```

| `--secrets` | Behaviour |
|---|---|
| *(absent)* | Durable where the OS credential manager answers; in-memory, with a warning, where none does. |
| `durable` | Persistence is required. A host with no usable credential manager **refuses the boot** rather than quietly running a store that dies. |
| `ephemeral` | In-memory only, even where a credential manager is available. Any existing `secrets.json` is left untouched and unread. |

An unknown name fails the boot loudly, exactly like `--auth`.

Durable secrets ride the OS credential manager: the store's master key lives in the macOS
Keychain / Windows Credential Manager / Linux Secret Service, and the secrets themselves in one
AES-256-GCM-encrypted file in the Manager's data directory. There is deliberately no plaintext
key file and no environment-variable key — a host without a credential manager refuses
persistence instead of degrading it.

`--secrets ephemeral` is the compensating control for the `localhost` row above: it bounds a
deployment-wide credential to the life of the Manager process, so a connection is something a
human did during this boot rather than something the installation carries forever.

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

### Session lifetime

```sh
YESSION_IDLE_TIMEOUT=30m                 # or 90s, 2h; unset means never
```

`YESSION_IDLE_TIMEOUT` lets the Manager stop sessions nobody is using. A session reports
busy or idle over its control channel — a connected peer, a running turn, a command in a
terminal, a non-empty queue — and the Manager reaps on **silence**, so no single report has
to arrive. Unset means never, and is the default.

Two costs to decide on first: a reaped launch takes its OAuth client registration and
per-launch user bindings with it, so the next visitor signs in again (invisible under `--auth
localhost`, a re-bounce under `trusted-headers`); and a session wedged after readiness stops
beating and is stopped as `NeverReported` rather than diagnosed.

Beyond freeing the Node process, Yjs replica and loaded event log an idle session holds, it
buys rolling upgrades: point `YESSION_SPAWN_BIN` at a path that floats with your builds and
sessions upgrade as they idle out and relaunch, while the Manager — whose own restart evicts
everybody — is left alone until nothing is running. A MAJOR version difference refuses the
launch rather than pairing two processes whose control protocol may disagree, which drains
the running set and hands you that quiet moment.

### Addressing

A deployment is **loopback** (development: nothing set, everything on `127.0.0.1`) or
**fronted** (deployment: both set, everything public). There is no half shape — a half-set
pair is refused at boot, because either half alone points somebody at a loopback address:
sessions without their issuer bounce every remote login to `127.0.0.1`, and a public issuer
without fronted sessions registers session addresses and OAuth callbacks nobody remote can
reach.

```sh
YESSION_MANAGER_URL=https://example.com          # the Manager: scheme + host, no path
YESSION_SESSION_URL=https://example.com/s/{id}   # sessions: a template
```

#### The Manager

Scheme + host, optional port, **no path**. Its routes are origin-anchored and its issuer is a
concatenation base (`<issuer>/connections/callback`), so a prefix would only work if the proxy
stripped it again. A path here is rejected.

That constraint is what lets the Manager share an origin with its sessions: the Manager owns
`/`, sessions own `/s/*`.

The origin named here must resolve **from this host too**, not only from browsers. Every
launched session runs OIDC discovery, JWKS and token requests against it while it boots, so
split-horizon DNS — or a proxy listening only on an external interface — fails session
registration. Fatally, and by design: a session that cannot authorize its users must not
half-start.

#### Sessions

A template over two placeholders — `{id}` (the session id) and `{port}` (its OS-assigned
port), exactly the two facts the registry stream publishes. Any proxy driven by that stream
can implement any template written with them.

```sh
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

Everything already sent is on the server and safe. A deployment whose sessions move says so
in the product: the session shell emits `<meta name="yession-ephemeral-storage" content="1">`
and the client's offline copy stops promising a sync it cannot deliver. Under an `{id}`
template the tag is absent and the promise holds.

#### The registry stream

Both a proxy binding and anything else that follows sessions read the same endpoint. It
publishes full snapshots, not deltas:

```sh
curl -sN http://127.0.0.1:8321/sessions/stream
# data: {"sessions":[{"id":"local-session","name":"…","port":57239,"pid":95225}]}
```

The stream is gated by the Manager's trust rule like every other management route: under
`localhost` a same-machine binding just works; under `trusted-headers` the binding asserts its
own `x-yession-user` on the subscribe, from inside the loopback trust boundary the proxy
already defines. A binding that forgets it does not fail loudly — it gets 401s, sees no frames,
and reconciles every mapping away as though the Manager had gone. The same is true of anything
else on the loopback side that asks the Manager a question — a health check, a tracker — so
each such caller names a subject of its own.

The template is deliberately **not** on the wire. The stream carries `id` and `port`; the
deployment applies its own template, so the prefix has exactly one home. A reference
reconciler that does exactly this — the stream in, a file in the proxy's own syntax out — is
[`examples/proxy/main.mjs`](../examples/proxy/); §Tailscale below composes it.

---

## Integrations

### GitHub

A session clones over HTTPS, and watches pull requests, with a credential a person signs in
for from inside it. There is **no default client id**: you register your own GitHub App, and
until you do, the only way in is pasting a token.

Register an App (not an OAuth App), and:

- **Enable device flow.** It is the flow the session runs — the authorization-code exchange
  needs a client *secret*, which the Manager's standards-only public-client broker
  deliberately cannot carry.
- **Set no callback URL that matters and generate no client secret.** Neither is used.
- **Install it on the repositories it should reach.** A device-flow token is a
  user-to-server token, so what it can see is the intersection of the user's access and the
  App's installations. That intersection IS the access rule — nothing in Yession re-checks
  it.
- **Leave user-token expiration on.** The grant is stored with its refresh token at the
  Manager, which rotates the access token before each turn that needs one. Turning
  expiration off yields a permanent token instead, and nothing tells you which of the two
  you registered.

- **Point its webhook at your Manager, if you have declared a hook endpoint** (see
  §Webhooks). Set the URL to `<YESSION_MANAGER_URL>/hooks/github` and paste in the secret
  the manager page shows. A watched pull request then reacts in seconds instead of within
  the poll interval. Skip it and everything still works — polling is the mechanism, and a
  hook only advances its clock.

Then name its client id:

```
YESSION_GITHUB_CLIENT_ID=Iv1.0123456789abcdef
```

Unset, the sign-in surface says so in words and offers the paste path instead. A pasted
`github_pat_…`/`ghp_…` works and **bypasses the installation rule above** — it answers to
whatever the token itself was scoped to (recorded in GAPS.md). A pasted `ghu_…`/`gho_…` is
refused where the device flow is available, because it expires in hours and cannot rotate
once pasted.

Four endpoints are overridable, which is what the test suites drive rather than the live
provider: `YESSION_GITHUB_DEVICE_URL`, `YESSION_GITHUB_TOKEN_URL`, `YESSION_GITHUB_USER_URL`,
and `YESSION_GITHUB_API_URL` (the REST base a watched pull request is read from).

### Webhooks

The Manager can take signed deliveries from a service and hand them to whichever sessions
asked for them. It never reads one: a session declares a filter over the paths it cares
about, and the Manager matches without knowing what any of them mean
([ADR](decisions/2026-08-29-the-manager-relays-hooks-it-cannot-read.md)).

Declare an endpoint per service:

```
YESSION_WEBHOOK_ENDPOINTS=github
```

Each one is served at `<YESSION_MANAGER_URL>/hooks/<name>`, so this needs a **fronted**
deployment — nothing outside the machine can POST to loopback. The manager page grows a
**hook endpoints** section showing, per endpoint, the address to give the provider and the
secret it must sign with. You do not choose that secret: the Manager derives it from the key
your credential manager already holds, which is also why endpoints are refused at boot under
an ephemeral secret store — a secret that changes at every restart would break inbound
deliveries silently.

To rotate one, bump its counter and paste the new secret in:

```
YESSION_WEBHOOK_ENDPOINTS=github@1
```

Both the new secret and the one before it are accepted, so there is no window where live
deliveries are refused. Bump again to retire the old one.

Deliveries are verified as HMAC-SHA256 over the raw body, hex, in `X-Hub-Signature-256`
behind `sha256=` — the WebSub convention, which is what GitHub, Shopify and Linear follow.
Override it per endpoint as `header:encoding:prefix`:

```
YESSION_WEBHOOK_SIGNATURE_GITHUB=x-shopify-hmac-sha256:base64:
```

Not supported: schemes that sign a constructed string carrying a timestamp, which is what
Stripe (`<t>.<body>`) and Slack (`v0:<ts>:<body>`) do. Those need the scheme itself rather
than more configuration.

### Tailscale

One origin carries everything — the Manager at `/`, each session at `/s/<id>` — and one
reverse proxy of your own sits behind `tailscale serve` and answers for all of it:

```
browser ──tailnet──▶ tailscale serve ──▶ your proxy ──┬──▶ manager      /
                     TLS + identity      one mapping    └──▶ session i    /s/<id>
```

`serve` does the two things only it can — terminate TLS on the tailnet and say who is
calling — through exactly one mapping. Routing, the identity translation, and what gets
stripped all live in the proxy, in a file. The pieces are in
[`examples/proxy`](../examples/proxy/): a reconciler that renders the registry stream into the
proxy's own syntax, and a Caddyfile that composes it with the translation below. The rest of
this section is what they implement, so a deployment on another proxy can implement the same.

#### Authorizing

**Use `--auth trusted-headers`.** `tailscale serve` already knows who is calling — it asserts
the identity of the calling tailnet node on every request —

```
Tailscale-User-Login        the user's login name
Tailscale-User-Name         display name
Tailscale-User-Profile-Pic  avatar URL (empty where the account has none)
```

and it **overwrites** these on inbound requests, so a client that sends its own
`Tailscale-User-Login` does not get to choose who it is. The overwrite is what makes them
safe to trust, and what lets this integration attribute work to real people. It does **not**
touch `x-yession-*`: a client's forged `x-yession-user` arrives at the proxy intact, which is
why the translation below is also a strip.

Yession reads only its own canonical set and `serve` cannot rename headers, so the proxy
translates. With Caddy, on the Manager's route:

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

Three details are load-bearing:

- **Set what `serve` asserts; delete what it does not.** A `header_up` with a value replaces
  whatever the client sent under that name, so the three sets are also the strip for those
  three. The two Yession reads that `serve` never asserts — email and claims — must be deleted
  by name, or a client chooses its own.
- **Delete by name, not `-x-yession-*`.** Caddy applies deletions *after* sets, so the wildcard
  strips the three just asserted and the Manager sees nobody. Measured (Caddy 2.11.2); it
  reads as correct and denies every visitor.
- **The Manager must be reachable only through the proxy.** An exposed `127.0.0.1:8321` on a
  shared box lets anyone local set `x-yession-user` to whatever they like, because under
  `trusted-headers` that header *is* the subject. On a single-user machine loopback is that
  user's already; on a shared one, bind the Manager somewhere only the proxy can dial.

Session routes carry none of this: a session authenticates its visitors through the Manager as
OIDC issuer, never by header, so the proxy forwards to a session untranslated.

**Loopback callers assert themselves.** The Manager's gate is the same for a request that
never left the machine, so the reconciler, a health check and a tracker each send an
`x-yession-user` naming what they are (`examples/proxy/main.mjs --as proxy-map`). Under
`localhost` the header is read by nothing and costs nothing; under `trusted-headers` a caller
that forgets it gets 401s — and a reconciler that then treated "no frames" as "no sessions"
would unmap the deployment, which is why the reference one does not.

> Verified against a live tailnet: Tailscale 1.102.3 with HTTPS certificates, Caddy 2.11.2,
> macOS — the identity headers, the overwrite, the `x-yession-*` pass-through, the deletion
> ordering, and the full composition below with the Manager and a session behind it.

##### What `--auth localhost` costs here

It is tempting on a single-machine install, and it is the one setting whose failure mode is
silent.

`serve` terminates on loopback, so **every** tailnet visitor reaches the Manager over
`127.0.0.1` — which is exactly what the `localhost` rule trusts. The device authorization that
let them onto the tailnet is real, but Yession never sees it: every visitor becomes the single
**unattributed** subject `local`. Nothing errors — sessions open, work is saved, all of it
attributed to one shared identity.

On a personal tailnet that is coherent: there is one human, and `local` is their name. With
anyone else on it, the audit trail says `local` for work several people did, and nothing can
tell afterwards which of them did what.

It costs more than the audit trail. Because every visitor is that one subject, a Claude
account connected for "all my sessions" belongs to the **deployment**: any tailnet visitor's
agent turn runs on it, and spends against it. That is the same boundary as the rest of the
rule — they could already open your sessions — but worth deciding rather than discovering.

Two ways to bound it, and they answer different questions:

- **`--secrets ephemeral`** — keep `localhost`, but tie the credential to the Manager process.
  Someone has to have connected it during this boot; a reboot or a redeploy means connecting
  again. Use this when the sharing is fine and the permanence is not.
- **`--auth trusted-headers`** — remove the sharing outright. Each human owns their own
  credential, and nobody else's turn can run on it. Use this the moment a second person is on
  the tailnet.

#### Addressing

The ingress is one mapping, made once — `serve` config lives in tailscaled's own state and
survives reboots:

```sh
tailscale serve --bg --https=8321 9000        # everything -> your proxy
```

with the Manager started as:

```sh
YESSION_MANAGER_URL=https://host.example.ts.net:8321 \
YESSION_SESSION_URL=https://host.example.ts.net:8321/s/{id} \
  yession-manager --auth trusted-headers
```

Both URLs name the tailnet origin, not loopback. The Manager is the OIDC issuer its sessions
bounce users through, so a loopback issuer here sends every remote login to an address only
this machine can resolve.

The proxy then routes `/` to the Manager and each `/s/<id>` to that session's port — **without
stripping the mount**. A Yession session serves *under* its mount: it answers at `/s/<id>/…`
and 404s at `/`, because the mount fixes its `<base href>`, its cookie `Path` and the prefix it
removes itself. So a Caddy route is `handle`, never `handle_path`, and the upstream is the bare
port.

Use `--https` on a tailnet with certificates, and match the scheme in both URLs. Derive it from
one variable — the Manager and its sessions must land on the *same* listener, or they are two
origins and the shared-origin arrangement quietly stops being one.

Prefer `--https` for a second reason the certificate note does not name. A browser withholds
the Cache API and service workers outside a secure context, so a session reached over plain
HTTP at a non-loopback address keeps no history on the device and can never open cold with no
network — it degrades to today's behaviour rather than breaking, and the settings pane says so,
but the remedy is this flag and nothing on the client side can substitute for it. Loopback is a
secure context, so the zero-config default is unaffected.

##### Keeping the session routes in step

A session's port is OS-assigned per launch, so its route is written by a process that follows
the registry stream, not by a person. `examples/proxy/main.mjs` is that process: it renders
every running session through a template in the two placeholders into one file, and the proxy
reads the file.

```sh
node examples/proxy/main.mjs --manager http://127.0.0.1:8321 --as proxy-map \
  --out /var/lib/yession/proxy/sessions.caddy \
  --empty '# no running sessions' \
  --template '@s_{id} path /s/{id} /s/{id}/*
handle @s_{id} {
	reverse_proxy 127.0.0.1:{port}
}'
```

Caddy under `--watch` re-adapts its config every second and re-reads the import, so a rewritten
map is live within a second; a proxy that does not watch its own config gets `--reload`. Three
details earn their keep, and any reconciler written against the stream should keep them:

**Render the whole set, every frame.** A frame is the running set, not a change to it, so the
file is rewritten from scratch and a missed frame costs nothing. A session that is reaped and
relaunched keeps its path and changes its port; a diff on the path alone would leave a route
aimed at a dead port forever. Rewrite only when the rendering changed, and beside-then-rename,
so the proxy never reads half a file.

**Nothing persists between runs.** The file *is* the state, and the next frame replaces it. A
restart of the reconciler, of the proxy or of the machine needs no cleanup pass — unlike routes
pushed into an ingress's own persisted state, which outlive the sessions they point at.

**Which silence means what.** The stream ending is not the sessions ending: a Manager restart
closes it, and the reconnect's first frame is a fresh snapshot that heals whatever was missed.
A connection *refused* is — sessions cannot outlive the Manager, so that is the one case where
the map is written empty. A 401 is neither: log it, keep the map, retry.

##### Rough edges

- **A bare `/s/<id>` has no canonicalising redirect.** The shell serves, but the auth cookie's
  `Path` is `/s/<id>/`, so that one request carries no cookie. Harmless — `<base href>` makes
  every sub-fetch absolute — but worth knowing before it is discovered.
- **The session id is not percent-encoded into the path.** Ids are Docker-safe by
  construction, so nothing can currently produce a path that needs it.

### Nix

The flake ships the two bins as one installable: `packages.<system>.default` is
`yession-manager` + `yession-session`, wrapped with the native WebRTC addon built from
source and `YESSION_BIN_CLAUDE` / `YESSION_BIN_GIT` defaulted to store paths. The module
below is distilled from the deployment the Yession project itself runs — home-manager
under nix-darwin on a Mac, composed with the Tailscale integration above.

Pin the input, and leave its nixpkgs alone:

```nix
inputs.yession.url = "github:trinketworks/yession";
# Deliberately NOT `inputs.yession.inputs.nixpkgs.follows = "nixpkgs"`: the flake
# builds the native WebRTC addon from source against its own nixpkgs pin, and
# overriding that trades a cached, tested build for an untested one.
```

Then one module declares the Manager, its policy, its addresses, and the proxy in front of it
together — the three agents below are the whole of §Tailscale, with the example's files used
as they ship:

```nix
{ config, pkgs, inputs, ... }:
let
  yession = inputs.yession.packages.${pkgs.stdenv.hostPlatform.system}.default;
  # The proxy pieces, straight from the input: a Caddyfile parameterised by env,
  # and the reconciler that keeps the session routes in step.
  proxy = "${inputs.yession}/examples/proxy";
  # Scheme + host spelled once, feeding both URLs — see §Tailscale.
  origin = "https://host.example.ts.net:8321";
  managerPort = 8321;
  proxyPort = 9000;           # what `tailscale serve --bg --https=8321 9000` targets
  proxyDir = "${config.home.homeDirectory}/.local/state/yession/proxy";
  logDir = "${config.home.homeDirectory}/Library/Logs/yession";

  # The resources profile (§What the variable names tell you). Paths are written
  # as the kernel sees them — /private/etc, not /etc — because the profile
  # refuses symlinked spellings. `nix-container-store` is the name this
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
      ProgramArguments = [ "${yession}/bin/yession-manager" "--auth" "trusted-headers" ];
      RunAtLoad = true;
      KeepAlive = true;
      EnvironmentVariables = {
        HOME = config.home.homeDirectory;
        # The default data dir is RELATIVE (`.yession`), and launchd does not
        # start agents in $HOME.
        YESSION_DATA_DIR = "${config.home.homeDirectory}/.yession";
        YESSION_PORT = toString managerPort;
        YESSION_IDLE_TIMEOUT = "30m";
        YESSION_SESSION_RESOURCES = "${resources}";
        YESSION_MANAGER_URL = origin;
        YESSION_SESSION_URL = "${origin}/s/{id}";
        PATH = "/usr/bin:/bin:/usr/sbin:/sbin";
      };
      StandardOutPath = "${logDir}/manager.out.log";
      StandardErrorPath = "${logDir}/manager.err.log";
    };
  };

  # The one proxy, watching its config so a rewritten session map is live within a
  # second. Loopback only: `serve` is the only way in from the tailnet.
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

  # The session routes, from the registry stream. KeepAlive because it holds the
  # stream open for its whole life; `--as` because the stream is gated like every
  # management route and this is a loopback caller naming itself.
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

Four choices in it are load-bearing:

- **A user agent, not a root LaunchDaemon.** The default `--secrets` behaviour is durable
  through the OS credential manager, and on macOS that means the login Keychain — which
  only a login session has. Run the Manager where the credential manager answers, or
  §Credentials' fallback rules decide for you.
- **The profile rides the store.** `pkgs.writeText` gives the sandbox policy the same
  lifecycle as the process that enforces it: one switch moves both, one rollback restores
  both, and there is no live file for a later hand-edit to drift. The Caddyfile rides it
  the same way, from the input itself: a bump of the input is a bump of the proxy's rules.
- **The session map does not.** It is the one live file here, and deliberately so — it is
  written by a process, not a person, from state that changes on every launch. It lives
  under `.local/state`, beside the rest of the deployment's runtime state, and nothing
  else writes there.
- **A plist change restarts the Manager, and a Manager restart evicts every session.**
  Nix redeploys by rewriting the agent's plist whenever anything above changes — the
  store path of the bin included. For frequent upgrades, §Session lifetime's advice
  applies doubly: point `YESSION_SPAWN_BIN` at a path outside the store that floats with
  your builds (a symlink you promote), keep the agent's `ProgramArguments` constant, and
  sessions roll onto new builds as they idle out — reserving the eviction for changes to
  the Manager itself. The proxy and the map are unaffected by a Manager restart: the
  map empties while the Manager is unreachable and refills from the first frame after.
