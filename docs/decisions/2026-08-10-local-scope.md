# An unattributed deployment owns credentials as itself, not as a browser

> Decided 2026-08-10 · Superseded by nothing · Supersedes in part: the connections
> design's decisions 1–2, and peer-scoped secrets from the trusted-header identity
> design · Related: [deployment.md](../deployment.md) `--auth localhost`, `--secrets`

## Decision

`SecretScope` gains a fourth case, `LocalScope`: **unattributed access to this deployment**,
as one principal. Under `--auth localhost` a connection credential signed in for "all my
sessions" is owned by it, and `PeerScope` leaves the connection path entirely.

A second knob, `--secrets`, lets the operator bound how long such a credential lives:
`ephemeral` forces an in-memory store even where a credential manager exists.

## The failure this fixes

On a `--auth localhost` deployment the operator had to click **Connect Claude** again and
again. Three separate credentials had accumulated in one `secrets.json` over ten days, one per
browser identity, and only the current one was ever readable.

The chain:

1. `Strategy.localhost` returns `Unattributed "local"`, so the cookie carries
   `UnattributedAccess`.
2. `ClaudeConnection.ownerOf` therefore fell back to `PeerOwner <browser peer id>`.
3. That peer id lives in `localStorage['yession/peer-id']`, which the browser partitions **by
   origin**. A new origin, a cleared store, a second browser or device mints a new one — and a
   peer-scoped credential is only readable in a launch that exact peer signed into.

So ownership was pinned to a value that changes under the person holding it. Worse, it changed
*silently*: the panel simply showed a Connect button again, which reads as "you never
connected" rather than "your credential is behind an id this browser no longer has".

## Why a scope and not a pseudo-user

The Manager already binds `UserId "local"` into `launchUsers` on every localhost login, and
`UserScope` already outranks `PeerScope` in the resolution walk — so simply writing the
credential to `UserScope "local"` would have worked with no new plumbing at all.

It was rejected because it is a lie that happens to typecheck. `local` is not a user; it is the
absence of one. A pseudo-user is indistinguishable in the store, the policy, the audit trail
and the wire from a real human who happens to be called `local`, and every future rule about
users would have had to carve it out by string comparison. `LocalScope` is a distinct case, so
the compiler enumerates every place that has to decide about it — which is how the codec, the
policy, the audit attributes and the injection walk all got their answer during this change
rather than after it.

The connections design's decision 2 said "no pseudo-user", and this keeps that promise; what it
revises is the conclusion drawn from it, that an unattributed deployment must therefore own by
browser peer.

## What it costs

**One connection serves the whole deployment.** Any visitor's agent turn runs on it and spends
against it. On a `tailscale serve` deployment that is every tailnet visitor, because `serve`
terminates on loopback and `localhost` trusts loopback.

This is the trust rule stated honestly rather than a new hole — anyone who can reach a
`localhost` Manager can already open and drive every session on it. But it is now written down
in `deployment.md` instead of being implied, with both exits named: `--secrets ephemeral` to
bound the lifetime, `--auth trusted-headers` to remove the sharing.

**Event attribution is deliberately unchanged.** Authors stay `PeerRef` under localhost. Making
the peer token carry a user would have collapsed every author in the transcript to one name and
destroyed the one distinction an unattributed deployment can still draw. Credentials are shared;
who said what is not.

**Existing `PeerScope` connection entries are orphaned.** They stay in `secrets.json`,
encrypted and untouched, and nothing migrates them. `PeerScope` was dropped from `turnTargets`
too, not merely from the write path — left half-live, a stale token in some old browser's peer
scope would shadow the new one at turn time. The operator's single corrective action is to
click Connect once, which is exactly what a migration would have been trying to save.

`PeerScope` remains a first-class scope for generic secrets. It has simply stopped
being a place a *connection* lives.

## The property that makes it cheap

`connectionsApiFor.Status` serves only the caller's own readable scopes, and the session filters
turn candidates against exactly that set. So `turnTargets` names `LocalScope` on **every** turn
without knowing how the deployment authenticates: an attributed launch is never granted local
access, never sees the scope in its status stream, and drops the candidate before anything is
resolved.

That is why this needed no new spawn-time fact, no env var and no control-channel field. The
Manager's readable set stays the single authority; a second copy of that judgement in the
session could only drift from it. The design that threaded a "shared subject" down to each
session was drafted and discarded for exactly this reason.

## What would change it

- A multi-user deployment that wants separate accounts without a proxy in front. There is no
  answer for it here by construction — the honest answer is `--auth trusted-headers`.
- A generic secret worth holding at deployment scope. The policy permits `LocalScope` for
  connection actions only; widening it is a deliberate act, and `SecretStore.Audit`'s
  `inject … "local"` arm is already there to record it.
