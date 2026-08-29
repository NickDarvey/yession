# A session learns about its pull requests by asking GitHub, not by being told

> Decided 2026-08-27 · Supersedes nothing · Related:
> [app/GitHubPrs.fs](../../app/GitHubPrs.fs) — the two endpoints and the poll,
> [src/Yession.Domain/PrFacts.fs](../../src/Yession.Domain/PrFacts.fs) — the vocabulary,
> [src/Yession.Domain/PrWatches.fs](../../src/Yession.Domain/PrWatches.fs) — the durable
> baseline, [deployment.md](../deployment.md) §GitHub — how an operator registers the App

## Decision

A session watches a pull request by **polling GitHub for it**, with two conditional GETs
(the pull request, then its head commit's check runs), spending the credential of whoever
started the watch. Every 15 seconds while its checks are pending, every 60 otherwise. Transitions — merged, closed, reopened, checks
green, checks red — are appended to the session's own log and read back on the timeline;
current state is the `github_prs` query.

We do **not** register webhooks, and the Manager gains nothing: it brokers a credential it
still cannot name, and its Session-notification channel stays the producerless transport it
was. `FetchPr` is a one-function seam, so a pushed transport later replaces an
implementation rather than a design.

## Why not webhooks?

Three separate reasons, any one of which is enough:

- **A repo webhook needs admin on that repo.** The ordinary case is contributing to a
  repository you do not administer, where `POST /repos/{o}/{r}/hooks` is simply refused.
  A feature that works only on repos you own is not the feature.
- **Inbound delivery needs a deployment GitHub can reach.** The zero-configuration
  deployment is loopback (`http://127.0.0.1:{port}`, see deployment.md §Addressing), which
  github.com cannot POST to. Webhooks would have served the fronted deployments and
  silently not the default one.
- **It would have put GitHub in the Manager.** Only the Manager has a stable public
  origin, so ingress would have landed there — and the Manager is the one component that
  has never learned which provider it brokers.

The GitHub **Events** API is not the fallback it looks like: it omits `check_run`,
`check_suite` and `status`, which is most of what a watcher is waiting for.

## Where would webhooks belong, then?

With a **hosted dispatching service**, which is also what would let somebody run Yession
without registering a GitHub App of their own. The shape: one App-level webhook on the
hosted App, a relay each session dials **out** to, and deliveries matched to the sessions
entitled to them. Outbound means it works in every deployment shape, loopback included —
which is exactly what inbound ingress could not do.

That service does not exist, and until it does, registering your own App is not the
fallback path but the only one — and the one a security-conscious operator would choose
anyway.

## Why two intervals?

Because the two waits are not the same wait. A settled watch is waiting for something that
has not started — a push, a merge — and nobody acts on either inside a minute. A watch whose
checks are PENDING is one somebody is sitting in front of, with a suite in flight that is
about to say something, and fifteen seconds there is the difference between noticing and
having moved on.

Splitting them is what makes the fast cadence affordable. GitHub does not count a `304`
against the primary rate limit, so a settled watch is free at any interval: two conditional
requests that both answer 304, against a budget of 5000 an hour. A pending watch is not
free, because its checks endpoint really is moving — four polls a minute, which puts the
ceiling around ten pull requests with live suites at once per credential. A single interval
would have had to pick one of those two facts to serve.

Both halves are always asked, and an earlier version of this document said the opposite: it
claimed an unmoved pull request "short-circuits its checks request entirely (the checks URL
is keyed by the head sha, so an unmoved PR cannot have moved checks)". That is false. The
runs ON a sha go queued to completed while the sha sits still, which is precisely what CI
finishing looks like — so the short-circuit could hold a watch at `pending` for the whole
life of a build that had already gone green.

Rate limiting is honoured by GitHub's own `x-ratelimit-reset` rather than a backoff invented
here: the provider names the moment it will answer again, and any number we picked would be
a guess against it.

## Why is the baseline in the log rather than in memory?

Because the process restarts, and both wrong answers are bad: re-announcing a merge that was
announced yesterday, or silently swallowing one that happened while the process was down.

So a watch records its `Initial` snapshot and every transition it announces, and the
comparison baseline is folded from those events (`PrWatchesProjection`). A restart re-folds
the same log and lands in the same place: nothing already said is said twice, and anything
that changed in the gap is still detected, because the log still says the state before it.

That is also what makes the wake safe — a woken turn is a debt found in the log after the
last turn started, so one transition can wake at most one turn, ever.

## Why does a pull request changing wake the agent, when a tool roster changing does not?

`AgentFacts.fs` refuses to wake on an MCP roster change, and the reason is credentials: that
event belongs to `ActorRef.System`, and `System` is not an authority the agent can call
tools on. Waking on it would mean acting on somebody's credential for something they never
asked for.

A watch is the opposite. It was started by an attributed party, in a gated act, and the poll
that noticed spent that same party's credential. The wake runs as them, on a thing they
explicitly asked to be told about — which is the property the roster change lacked, not a
rule being bent for it.

## What would change it

- **The dispatching service existing.** Then `FetchPr` gains a second implementation, and
  polling becomes the fallback for deployments that do not use it — not deleted, because
  a self-hosted operator with no relay still needs it.
- **Session-direct ingress under the fronted shape.** A fronted session is publicly
  addressable already, so a hook could POST straight at a `/hooks/<token>` route the
  session serves and verifies itself — no Manager involvement at all. The
  admin-permission problem does not move, so App-level delivery would still be the
  mechanism; the shape that would have needed the Manager to relay
  ([ADR](2026-08-28-deployment-fronts-everything-or-nothing.md)) no longer exists.
- **GitHub streaming PR state.** If the provider offered a subscription a client could hold
  open, this becomes the reconnect path rather than the mechanism.
- **A watcher that needs seconds, not fifteen.** Lowering the pending interval again
  spends the ceiling above rather than buying latency for free, so the answer at that point
  is a pushed transport, not a smaller number.
