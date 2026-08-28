# A deployment fronts everything or nothing

> Decided 2026-08-28 · Supersedes the `ManagerOnly` shape (Plan 07's remote management,
> kept through Plan 09) · Related:
> [src/Yession.Domain/PublicAccess.fs](../../src/Yession.Domain/PublicAccess.fs),
> [deployment.md](../deployment.md) §Addressing,
> [2026-08-27-pr-state-by-polling.md](2026-08-27-pr-state-by-polling.md) — whose webhook
> reasoning leaned on the removed shape existing

## Decision

`PublicAccess` has two shapes: **loopback** (development — nothing configured, Manager and
sessions on `127.0.0.1`) and **fronted** (deployment — `YESSION_MANAGER_URL` and
`YESSION_SESSION_URL` both set, everything public). The third shape, `ManagerOnly`
(Manager fronted, sessions on loopback), is deleted; setting exactly one of the pair is
now refused at boot in either direction, where before only the sessions-without-Manager
half was.

We do **not** add a default session template to make a lone `YESSION_MANAGER_URL`
bootable. A template names the operator's proxy topology, and a default would be a guess
about infrastructure this process cannot see.

## Why it went

Nothing branched on it. All four references were or-patterns inside `PublicAccess.fs` —
`ManagerOnly` behaved as `Fronted` for the issuer and as `Loopback` for everything else,
which is not a shape so much as a seam between two.

That seam produced the one configuration that half-works: the only shape registering
`http://127.0.0.1:<port>/callback` OAuth redirect URIs against a **remote** issuer. The
Manager's page loads from anywhere; every session on it is unreachable from the browser
that loaded it. `deployment.md` already told operators "Set **both or neither**" — the
code honored that in one direction and this change makes it honest in both.

And it is the shape that fights where deployments are going: instances that each carry
their own ingress and egress — on a cluster, without a single point of failure. Every
inbound design sketched against `ManagerOnly` ended with the Manager relaying bytes to
sessions hiding behind it; under two shapes that contortion has nothing to serve, because
a fronted session is addressable itself and a loopback one is deliberately not.

## What it costs

The Plan 07 workflow — manage sessions remotely, run them on the host's loopback — no
longer boots. It was the shape's original job, and anyone using it fronts their sessions
too (one more variable, and the proxy the Manager already required) or manages locally.

## What would change it

- **That workflow returning as a real need.** It comes back as a deliberate shape with a
  stated story for what a remote browser is supposed to do with a session it cannot
  reach — never as the accidental result of setting one variable and not the other.
- **A session template with a sane zero-config default.** If sessions ever acquired a
  public address derivable without operator input, the fronted shape could tolerate a
  lone `YESSION_MANAGER_URL` — the refusal exists because today that default would be a
  fabrication.
