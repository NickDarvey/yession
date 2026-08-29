# The Manager relays hooks it cannot read

> Decided 2026-08-29 · Supersedes nothing · Related:
> [src/Yession.Domain/Webhooks.fs](../../src/Yession.Domain/Webhooks.fs) — the filter,
> [app/WebhookRelay.fs](../../app/WebhookRelay.fs) — the endpoints and the fan-out,
> [deployment.md](../deployment.md) §Webhooks — how an operator declares one,
> [2026-08-27-pr-state-by-polling.md](2026-08-27-pr-state-by-polling.md) — whose third
> reason for refusing webhooks this answers

## Decision

The Manager serves **hook endpoints** — `POST /hooks/<name>`, one per service an operator
declares. It verifies that a delivery is signed, matches it against **filters sessions
declared**, and forwards it to those sessions over the notification leg it already had. It
never learns which service sent a delivery, what the event means, or what any field is
called.

A filter is **data**: a conjunction of equalities over dotted paths into the delivery, which
is addressed as one document (`headers.x-github-event`, `body.repository.full_name`). The
signing secret is **derived** from the KEK the credential manager already holds, never
stored and never configured.

## Why data and not code

A session shipping the Manager a predicate would be shipping it code, and code has to be
interpreted. The Manager would then have to implement every construct the code could
contain — which makes it a **version ceiling on the sessions it supervises**: a session
upgraded to emit a construct an older Manager has not learned stops working against it.
That is the opposite of why the Manager is kept ignorant.

So the language is made small enough to have no versions at all. Every operator that could
be added — a disjunction, a negation, a pattern — is one more thing two builds can disagree
about, and equality is the one that cannot be read two ways. There is nothing here to
extend, and that is the feature.

The filter and the payload go together. A content-free ping would need no filter language
at all; the filter earns its keep precisely because the delivery rides with it, and that is
also what makes the relay useful to the next integration rather than only to the first.

## Why the Manager verifies and the session does not

Two different callers, two different postures.

A **delivery** arrives from the internet, so the Manager checks it at the door: HMAC-SHA256
over the raw bytes, against every secret the endpoint currently accepts. Verifying is over
opaque bytes, so it teaches the Manager nothing — unlike parsing, which is confined to
resolving the paths a session named.

A **subscription** arrives over the authenticated control channel from a child this Manager
spawned, and that child already holds whatever credential it would act on. So the Manager
takes its word. Sessions are trusted; the Manager is kept minimal for a different reason —
a smaller single point of failure, and sessions that upgrade on their own.

## Why the secret is derived rather than configured

Because the provider lets us choose it. GitHub's instruction when registering a webhook is
"type a string to use as a `secret` key", so the Manager generates one and the operator
pastes it in while they are already in the settings page typing the URL. Nothing to find,
nothing to record, and no plaintext secret in the state file.

`HMAC(KEK, "yession-webhook:<name>:<rotation>")` is stable across restarts because the KEK
is, and written nowhere because it does not have to be. A rotation is bumping the counter:
the previous secret stays accepted until the counter moves again, so there is no window
where live deliveries are refused.

The corollary is a refusal. An ephemeral secret store mints a fresh KEK every boot, so every
secret an operator pasted into a provider would silently stop working at the next restart —
and only for inbound deliveries, which is the least visible way to break. Declaring
endpoints without a durable store is refused at boot instead.

## What this costs

The Manager gained its first knowledge of anything provider-shaped: one header name, one
HMAC, and a JSON walk. It is bounded to `WebhookRelay.fs` and it buys the thing a GitHub App
makes unavoidable — an App has exactly **one** webhook, so ingress cannot be session-direct
and has to land somewhere stable.

What the Manager still does not have is a provider. `repository.full_name` appears once in
this repository, in the session that watches pull requests.

## What would change it

- **A scheme that signs a constructed string.** Stripe signs `<timestamp>.<body>` and reads
  it out of a structured header; Slack signs `v0:<ts>:<body>`. More configuration does not
  reach those — they need the scheme itself. The relay forwards a delivery's headers, so
  the alternative is a session verifying its own endpoint, and that is not built.
- **A filter that genuinely needs disjunction.** That is the moment to ask what it is really
  for, not the moment to add an operator.
- **Multi-tenant filters.** A session may declare any filter, so on a shared Manager one
  user's session could ask for another's deliveries. Recorded in GAPS with the exit — bind a
  subscription to what the subscriber's credential can see.
