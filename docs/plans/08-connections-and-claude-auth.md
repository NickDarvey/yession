# Plan 08 — Connections: a standards-only broker, and Claude sign-in over it

Sign in with a Claude account from inside a session; run each agent turn on the
credential of the human who triggered it. The Manager's part is deliberately
service-agnostic: a **connection broker** that speaks OAuth-the-standard (RFC 6749/7636)
over the encrypted secret store, parameterized entirely by data the session sends. The
Manager never learns which service it brokered — every Claude-specific fact lives in the
session (`app/ClaudeConnection.fs`). The next connection (GitHub, …) is new session-side
data + UI, zero Manager changes.

## Decisions

1. **Sign-in scope, chosen at connect time.** *This session only* → the credential is
   stored under `SessionScope` of that session. *All my sessions* → under the signing
   actor's own scope (`UserScope` / `PeerScope`), usable from — and replaceable by —
   any session that owner is signed into. No global scope exists; the stored credential
   is last-write-wins by the store's own upsert.
2. **No pseudo-user.** `CredentialOwner = UserOwner of UserId | PeerOwner of PeerId`
   (`src/Yession.Domain/Connections.fs`) — exactly the split `ActorRef`/`SecretScope`
   already carry. Attributed deployments never handle an anonymous case; localhost
   deployments own credentials by witnessed browser peer.
3. **The broker lives in the Manager, standards-only.** Weighed against session-owned
   PKCE/exchange: (a) refresh-token custody — session-owned exchange forces the resolve
   surface to return whole grants so sessions can refresh, putting long-lived refresh
   tokens in every session that uses a credential; (b) refresh-rotation races — an
   "all my sessions" credential refreshed independently by several sessions breaks
   providers that rotate refresh tokens on use. One Manager-side lazy refresher solves
   both. Everything the broker knows is standard: PKCE S256, authorization-code grant,
   refresh grant; endpoints/client-id/scopes arrive per flow as
   `ConnectionBeginRequest` data. Its one owned constant is its own public callback URL.
4. **The stable callback is the Manager's.** Session ports are OS-assigned per launch;
   the Manager port is fixed. `GET /connections/callback?code&state` on the Manager
   origin is what any provider's registered redirect URI pins to; the single-use
   `state` — minted only for a target the policy permitted at begin — is the whole
   authorization. No provider name in the path.
5. **Turn credential precedence**: session-scoped credential (an explicit "this
   session" choice, overriding for every actor) ▸ the TURN ACTOR's own credential
   (`CurrentMessage.Author` → `CredentialOwner.ofActor`) ▸ ambient env (documented
   last resort; feeds CI's LiveAgent tier). No borrowing across actors: an unconnected
   actor's turn fails legibly, pointing at the Connections panel.
6. **Status is metadata, values move once.** The third control reverse leg
   (`GET /control/connections`, `NotificationHub<ConnectionStatusList>`) streams
   value-free statuses; the access token crosses the control channel only at
   `/control/connections/resolve`, when a turn actually runs — the ONE deliberate
   exception to "no value-returning route", still policy-gated per target.

## Shape

```
browser panel ── /claude* (session, cookie-gated) ── control /control/connections/* ── broker ── provider
     ▲                                                        │                          │
     └── polls GET /claude ◄── session status cache ◄── SSE GET /control/connections     └── encrypted store
provider redirect ──────────► GET /connections/callback (Manager, public, state-keyed)
```

- Domain: `Connections.fs` (`CredentialOwner`, `ConnectionKind`, `ConnectionStatus`);
  `Authorization.fs` gains the `ConnectionAction` family — every action (including the
  write) permits exactly where the caller IS the target scope's owner (own session /
  bound user / witnessed peer). Generic secret rules unchanged.
- Manager pure: `BrokerState.fs` — `BrokeredCredential` (`BrokeredOAuth` grant with
  `tokenUrl`+`clientId` captured at exchange time, or `BrokeredStatic`), codec,
  `BrokerFlow` (authorize URL, grant bodies, token-response decode, 5-min refresh
  margin, refresh-token merge), `PendingFlows` (state → flow, single-use, 10-min TTL).
  Refresh tokens exist only in this envelope, Manager-side — enforcement by placement.
- Manager service: `app/Broker.fs` (`Begin`/`CompleteCallback`/`Complete`(paste)/
  `Put`/`Disconnect`/`StatusOf`/`Resolve` with lazy refresh); `connectionsApiFor` in
  `ProcessManager` pre-composes `Policy.authorize` + audit; routes + SSE leg in
  `Control.fs`; the public callback page in `ProcessManager`; status broadcast on
  credential change AND on new launch bindings; sinks die with the launch.
- Session: `ClaudeConnection.fs` — endpoints/client id (overridable via
  `YESSION_CLAUDE_*` env for tests), reserved name `claude-code`, pasted-token
  classification (`sk-ant-oat01-…` → `CLAUDE_CODE_OAUTH_TOKEN`, `sk-ant-…` →
  `ANTHROPIC_API_KEY`), scope→target mapping, `/claude*` browser routes (gated like
  `/me`; unattributed owners self-assert their peer id exactly like `/login?peer_id=`
  — the Manager's policy is the authority). `SessionMain` keeps a live status cache
  off the SSE leg; the agent gate is a thunk read at every drain, so a mid-session
  sign-in enables turns without relaunch; `Agent.runWith` gives the SDK's spawned CLI
  exactly the resolved credential (both ambient vars removed).
- Client: `Claude` view state + sidebar panel (`data-claude-panel`): scope choice,
  Connect (authorize link, code-paste completion), paste-a-token, per-scope
  disconnect; polls `GET /claude` while a flow awaits its callback.

## Trust notes

- Localhost peers self-assert `peerId` at the session routes; the Manager authorizes
  against its own witnessed set, so a forged id outside the launch is denied — the
  same boundary as the login bounce.
- A witnessed peer's session can also delete peer-scoped entries via the generic
  secret routes; that authority is identical to `disconnect` — acceptable.
- Field-verified: the Claude client REJECTS unregistered redirect URIs ("Redirect URI …
  is not supported by client"), so the Claude flow redirects to Anthropic's own
  code-display page (`https://console.anthropic.com/oauth/code/callback`) and completes
  by paste — `ConnectionBeginRequest.RedirectUri` carries this per flow, standards-only.
  The Manager's `/connections/callback` remains the anchor for connectors whose clients
  CAN register it.
- Field-verified: Anthropic's token endpoint does NOT accept the standards-mandated
  `application/x-www-form-urlencoded` grant body (RFC 6749 §4.1.3) — it answers
  `invalid_request_error: "Invalid request format"`. Its own clients post JSON, with
  `state` replayed in the body (hence the `code#state` paste). So "standards-only" is the
  broker's DEFAULT, not its only dialect: `ConnectionBeginRequest.TokenDialect` carries
  the encoding per flow, exactly like `RedirectUri` above, and the grant records it so
  refreshes speak it too. Provider knowledge stays session-side; the broker still never
  learns which service it brokered.
- Anthropic's ToS restricts subscription OAuth tokens to Claude Code/claude.ai;
  using them to drive the Agent SDK is the operator's call (acknowledged when this
  plan was set). API keys via the same paste surface are the sanctioned path.

## Follow-ups (recorded, deliberate)

- Panel live push (another tab's sign-in appears only on the next poll; the agent
  gate itself is already live via the control stream).
- Refresh-failure surfacing beyond per-turn errors (a panel health state).
- A second connection (GitHub) — session-side module + panel section only.
