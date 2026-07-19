# Session authorization: the Manager as an OIDC provider (delivered)

Users get into a session through a standard OpenID Connect authorization-code + PKCE
flow: the **Manager is the provider (OP)** and each **Session Process is a client
(RP)**. This replaces the shared session token entirely — no token in the open link,
none in `PeerHello`, none in the event-chunk URLs the browser fetches.

## Why this shape

- **Sessions are spawned ad hoc, so clients are registered dynamically.** A session
  cannot be pre-registered at spawn: its port is OS-assigned, so its `redirect_uri`
  only exists once it listens. Instead the session registers itself after listen over
  the control channel it already has — `POST /control/register-client`, authenticated
  by the per-launch `x-yession-control` secret. The registration is bound to that
  secret and revoked with it: *a secret dies with its launch, and so does its client.*
- **The standard libraries do the protocol.** The session's whole client side —
  discovery, PKCE, the code exchange, ID-token validation against the Manager's JWKS —
  is the certified `openid-client`; the Manager signs ID tokens with `jose`. Bindings
  live in `src/Fable.Jose` and `src/Fable.OpenIdClient` (the `Fable.Dockerode`
  pattern). Only the provider's own state machine is hand-written, isolated in
  `src/Yession.Oidc` and conformance-tested (`tests/Yession.Tests/Oidc.fs`).
- **Authentication is a strategy.** WHO the human is, is decided by an
  `AuthenticationStrategy` value plugged into the provider. This slice ships
  `Strategy.localhost` (any loopback request is the single `local` user — locality
  already is the boundary); an upstream OIDC integration is a second strategy value,
  not a provider change.
- **Offline-first survives.** The shell (`GET /`) stays ungated and cached
  (`max-age=86400`) — it is a pure function of the session id with no content and no
  secrets. Authorization gates the DATA surfaces, and the browser client renavigates:
  it probes `GET /me`; 200 hands it a peer token, 401 sends it through `GET /login`
  (the OIDC bounce, back to the cached shell), and a network failure means offline —
  the cached shell, the IndexedDB doc, and the browser-cached event chunks keep
  working read-only.

## The flow

```text
browser            session (RP)                 manager (OP)
   |  GET /            |                            |
   |<-- cached shell   |                            |
   |  GET /me          |                            |
   |<-- 401            |                            |
   |  GET /login       |                            |
   |<------------- 302 to /authorize?...PKCE ------>|   strategy authenticates (localhost)
   |<------------- 302 to /callback?code&state -----|
   |  GET /callback    |-- POST /token (secret+verifier) -->|
   |                   |<-- { id_token (EdDSA), ... } ------|   openid-client validates vs /jwks
   |<-- Set-Cookie; 302 /                           |
   |  GET /me          |                            |
   |<-- { peerToken }  |                            |
   |  PeerHello{Token=peerToken} over WebRTC        |
```

## Keys and lifetimes

The Manager's signing keypair is generated per start through jose on Node's built-in
WebCrypto with `extractable = false`: the private key is a **non-extractable
CryptoKey** — no code path can serialize it (the conformance suite pins that export
throws) — living only in process memory and dying with the process. Nothing signed
outlives it: children die with the Manager (parent guard). All other auth state is
equally in-memory: client registrations (revoked with their control secret),
authorization codes (single-use, burned even on failed redeem, 60 s), the session's
pending logins (single-use, 5 min), cookies and peer tokens (process lifetime).
Cookies are HttpOnly, `SameSite=Lax`, and namespaced per session
(`yession_auth_<sessionId>`) because 127.0.0.1 cookies are not port-scoped.

## Surfaces

Manager (shared endpoint, with the control RPC + management UI):
`GET /.well-known/openid-configuration`, `GET /jwks` (public key only),
`GET /authorize` (code + PKCE S256; 400 for unvalidated clients — never a redirect),
`POST /token` (RFC 6749 §5.2 error taxonomy), `POST /control/register-client` (DCR).
The control channel now exists for every launch; environment routes answer 403 for
launches without an environment grant.

Session: `GET /` (ungated, cached), `GET /login` (302 into the bounce),
`GET /callback`, `GET /me` (cookie → `{ peerToken, sub }`), `GET /events/{n}` (cookie,
or a minted `?token=` for headless clients; full chunks now `private, max-age=…,
immutable` so the browser cache still replays offline), `POST /signal` (ungated — the
channel grants nothing until a `PeerHello` carries a minted token).

## Verification

Pure tier: PKCE S256 against the RFC 7636 Appendix B vector, the RFC 6749 error
taxonomy, code/registration/login lifecycles, discovery-document shape (OIDC
Discovery §3), cookie/form parsing, key non-extractability. Ports tier: the real OP
endpoint driven as a raw OAuth client with a second, independent jose verification of
the issued ID token; and the composed flow — a real Manager + child Session Process,
the full cookie-jar login bounce, gated data surfaces, the ungated cached shell, a
forged DCR refused, and revocation on stop. The certified `openid-client` running
inside every session is itself a standing conformance check of the provider.

## Deliberate scope (recorded in GAPS)

Identity attribution (ID-token claims → `PeerId`/`ActorRef`) is a follow-up; display
names stay self-asserted. No `nonce` until the upstream strategy lands. The
management UI has no login. Trust-localhost is the only strategy.
