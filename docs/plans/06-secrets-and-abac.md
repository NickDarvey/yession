# Plan 06 — Secrets in the Manager, and an ABAC authorization layer

> **Status: delivered.** Addresses [GAPS.md](../GAPS.md) § Security & trust
> ("**Secrets**: `SecretRef` resolves from a process-env store (local-dev only …); there
> is no real secret store yet") and builds directly on
> [04-session-authorization.md](04-session-authorization.md): the Manager is already the
> OIDC provider that authenticates users into sessions — this plan makes it remember
> *which user it verified into which launch*, and puts that composite identity to work
> authorizing the first real Manager-owned resource: secrets.

## Product behaviour

1. **Sessions persist secrets.** An agent (or the session process on its behalf) can
   store a named secret with the Manager — a deploy token it was handed, a credential it
   minted — and it survives session restarts and Manager restarts. Session-scoped
   secrets belong to exactly one session.
2. **Users will have secrets too.** Future sign-in features (Claude, GitHub — new
   `AuthenticationStrategy` values per Plan 04's seam) produce user-scoped credentials.
   The store, the wire, and the policy all speak user scope from day one; the only thing
   missing is a writer (sessions cannot write user scope — the user-facing surface is a
   recorded follow-up).
3. **Secrets are write-only through the API.** The control channel offers
   `set` / `list` / `delete` — **there is no route that returns a secret value, for any
   scope**. Tool results, the event log, and the Yjs doc are all synced, persisted,
   model-visible surfaces; a value-returning route would turn one prompt injection into
   exfiltration. Values leave the store exactly one way —
4. **— injection into a launched environment.** A `SecretRef` in an `EnvironmentSpec`
   env var (the seam that already exists) resolves Manager-side at container start:
   the value goes straight into the Docker container's environment, never through the
   agent loop, the session process, or back over the control channel.
5. **Persistence rides the OS credential manager.** The store's master key (KEK) lives
   in the platform credential store — macOS Keychain, Windows Credential Manager,
   Linux Secret Service — and the secrets themselves live in one AES-256-GCM-encrypted
   file in the Manager's data directory. **No usable credential manager → no
   persistence**: the Manager runs an in-memory store that dies with it, says so loudly
   at boot, and never falls back to a plaintext key file or an environment-variable key.

## Topology

```text
                              Manager
  OS credential manager        ├── KeyStore (@napi-rs/keyring)
  (Keychain / Credential  ◀───▶│     KEK: 32 random bytes, created on first run
   Manager / Secret Service)   │     imported per start: WebCrypto AES-GCM,
                               │     extractable = false  (never exportable in-process)
                               ├── SecretStore ── <DataDir>/secrets.json
                               │     per-entry AES-256-GCM ciphertext, AAD-bound
                               │     to scope+name; metadata cleartext (it IS the list)
                               ├── launchUsers: control-secret ─▶ verified user subs
                               │     (recorded at ID-token issuance, dies with launch)
                               ├── Policy.authorize (pure, default-deny)
                               │
     session (child)           │
       ControlClient ──POST──▶ /control/secrets/set|list|delete     (403 on Deny)
       agent tools: set_secret / list_secrets / delete_secret       (never a value)
       EnvironmentSpec ──POST─▶ /control/start
                               └── resolveEnv: SecretRef ─▶ authorize ─▶ decrypt ─▶
                                     container env   (session ▶ user ▶ process-env)
```

## Why this shape (invariants it must respect)

- **The Manager owns authority; capabilities are scoped, not ambient**
  ([design.md](../design.md) §3, §5). Secrets are Manager state behind the control
  boundary. The session-side capability record is pre-bound to the session's own scope —
  the client type cannot even express another session's secrets — and the server never
  trusts it: every call re-resolves the per-launch control secret to a caller and passes
  the pure policy.
- **The composite identity is Manager-verified, never self-asserted.** Plan 04's
  provider had the subject and the client (= session) in hand at `POST /token` and
  forgot them. Now it records the pair, keyed by the launch's control secret —
  `launchUsers` — and revokes it with the launch, exactly like the client registration
  it derives from ("a secret dies with its launch, and so does its client" — and now,
  so does its user binding). `AuthzSubject = { Session; Users }` is built only from
  that Manager-side state. Durable secrets, per-login access.
- **ABAC as one pure function.** `Policy.authorize : AuthzRequest -> Decision` lives in
  the Domain, is total and default-deny, and is exercised as a full decision table in
  the cheap test tier. Subjects, actions, and resources are DUs, so the next
  Manager-owned resource adds cases, not mechanisms. Enforcement happens where
  authority already crosses the boundary: the control route arms, and the injection
  resolver.
- **The non-extractable-key discipline extends to the KEK.** Plan 04 pinned the signing
  key: generated on WebCrypto with `extractable = false`, export throws. The KEK gets
  the same treatment — persisted only by the OS credential manager, imported each start
  as a non-extractable AES-GCM `CryptoKey`, export-rejection pinned by test. Node's
  WebCrypto alone cannot do the persistence half (it has no OS keystore; a
  non-extractable key cannot be serialized *by design*) — that is precisely why the
  keyring is in the loop.
- **Per-entry ciphertext, identity-bound.** Each secret is encrypted separately with
  AAD binding it to `scope + name`: a ciphertext transplanted onto another entry fails
  GCM authentication; `list` never touches the cipher; values are decrypted only at
  injection. Metadata (names, scopes, timestamps) is cleartext on disk **by design** —
  it is exactly what the list route serves. The envelope follows the `ManagerCodec`
  discipline: hand-written Thoth codec, `Version` migration hook, atomic
  tmp→fsync→rename writes, loud corruption, and a `KekId` so "sealed by a different
  key" fails distinctly from "corrupt".
- **Refusing persistence beats degrading it.** A host without a credential manager gets
  the same store semantics in memory (values still ciphertext under a per-boot random
  non-extractable key), one loud warning, and an untouched `secrets.json` if a previous
  durable run left one. No plaintext key file, no env-var KEK, ever.
- **Verification is automated end-to-end, per capability tier.** The policy table,
  codecs, and cipher/store laws run in the cheap tier; the route authorization matrix
  and binding lifecycle over a real Manager + child in the Ports tier; container
  injection in the Docker tier; and the OS credential manager itself behind a new
  **`Keyring`** capability — a real set/get/delete round-trip through
  `@napi-rs/keyring`, runnable in the dev container via a `dbus` + `gnome-keyring`
  wrapper and on any desktop against the genuine OS store.

## Surfaces

Manager control channel (per-launch secret in `x-yession-control`, like every control
route; 401 unknown secret, 403 policy deny / no store, 400 malformed, 500 store
failure):

```text
POST /control/secrets/set      { scope, name, value } -> secret metadata (no value)
POST /control/secrets/list     { scope }              -> { secrets: metadata[] }
POST /control/secrets/delete   { scope, name }        -> { deleted }
(no /control/secrets/get — deliberately not a route)
```

Session-side: `ControlClient.secretsCapabilities` (pre-bound to the session's scope) →
`AgentCapabilities.{SetSecret, ListSecrets, DeleteSecret}` → agent tools
`mcp__yession__set_secret` / `list_secrets` / `delete_secret`.

Injection: `SecretRef name` in `EnvironmentSpec.EnvironmentVariables` resolves at
`/control/start` time with precedence **session-scoped → user-scoped (bound users) →
Manager process env** (the old env store survives as an explicit lowest-precedence
fallback; any store entry shadows it).

## v1 policy

```text
session S, its own SessionScope S secrets:   set / list / delete / inject   PERMIT
session S, UserScope u, u ∈ bound(S):        list / inject                  PERMIT
session S, UserScope u, any write:                                          DENY
anything cross-session, unbound, or unmatched:                              DENY (default)
```

Under the shipped `Strategy.localhost` every login is the single `sub = "local"`, so
day-one user scope is one user; real strategies change who `sub` is, not the policy.

## Verification

Cheap tier: the full policy decision table; codec round-trips (envelope + wire) and
loud-failure cases; AES-GCM round-trip / tamper / AAD-transplant / wrong-KEK; KEK
durable-before-first-use; ephemeral-store zero-file-I/O; interleaved-set ordering; KEK
non-extractability (export rejects). Ports tier: binding lifecycle over the real OIDC
bounce (absent → present → revoked); the route matrix over a real Manager + child
(metadata-only responses asserted by raw body inspection, cross-session 403 by raw
HTTP, get-route 404, restart durability vs. ephemeral loss). Keyring tier: the real
`@napi-rs/keyring` round-trip (`scripts/with-keyring.sh check Keyring` in the dev
container; the genuine OS store elsewhere). Docker tier: `SecretRef` injection of
stored session- and user-scoped values, binding-gated, env fallback shadowed.

## Deliberate scope (recorded in GAPS)

No shared/Manager-global secret scope (ambient authority with no owner). No user-facing
management surface for user-scoped secrets yet — sessions cannot write them and the
management UI has no login; the Claude/GitHub sign-in strategies are the intended
writers. User bindings are launch-lifetime (re-login re-forms them). Hosts without a
credential manager run in-memory only. `LocalProcessBackend` still performs no env-var
injection. The process-env fallback remains. No KEK rotation or recovery (a lost KEK
orphans the store loudly; the operator deletes the file). Multi-user same-name
injection precedence is unresolved until a real multi-user strategy lands. The
environment routes stay behind their existing capability grant; folding them under
`Policy.authorize` is the follow-up that proves the layer's reuse.
