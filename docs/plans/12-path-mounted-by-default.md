# Plan 12 — Path-mounted by default, and the promise the client can keep

> **Status: implemented.** Supersedes the port-pinning half of
> [Plan 11](11-idle-session-reaping.md).

## An addressing choice, not a resource to manage

Plan 11 pinned a port per session so a session's origin would survive a relaunch — IndexedDB
is partitioned by origin, and a port is part of one. It worked, and it bought that stability
with a reserved range an operator had to size, keep clear, and never exhaust. Machinery whose
only job was to hold an address still.

[Plan 10](10-mounted-sessions.md) had already shipped the better answer without anyone
adopting it. A `{id}` template gives a session an address derived from the session:

```
YESSION_SESSION_URL=https://example.com/s/{id}
```

The mount reaches `<base href>`, the auth cookie's `Path`, and the prefix stripped off every
request; redirects are relative; the OAuth redirect URI is already the public mounted URL.
Nothing needed building. So the port range, `SessionPorts`, `PortRange`,
`SessionRecord.Port`, and the boot-time allocation and migration that maintained them are all
deleted, and a session's port goes back to being what it always was — an implementation
detail of the process, never part of its address.

The registry's `ManagerState` codec is unknown-field tolerant, so a `manager.json` written
with `port` still loads; the field is simply ignored. No migration.

## Where the address still moves, the client says so

Deleting the guarantee without replacing it would have been fine for path-mounted
deployments and quietly wrong everywhere else. The zero-config default is
`http://127.0.0.1:{port}` — a session there *does* come back at a new origin, and everything
its user wrote offline *is* stranded.

That was already true before this plan, and the client was already claiming otherwise. The
promise appeared three times: the reconnect card's *"Your work is saved here and syncs when
it comes back"*, `Dom.Text.localFallback`'s *"everything is saved locally and syncs when the
session is back"* in four banner states, and a comment in the browser's reopen handler. On a
stopped session the card and the banner rendered **together**, so it was stated twice on the
one screen where it mattered — and on the default deployment both were false.

One derived fact fixes all of it:

```fsharp
// src/Yession.Domain/PublicAccess.fs
val sessionAddressIsStable : PublicAccess -> bool   // the template never names {port}
```

It rides to the client the way the Manager origin does — a `<meta>` tag on the shell, read
once into a static `ClientModel` field, never a message and never folded — and it is emitted
**only when storage is ephemeral**, so a path-mounted shell says nothing about storage at
all. Absence is the good case, the same idiom the manager-origin tag established.

The copy then follows from the fact rather than from an assumption: the banner states the
local-first promise only where it can be kept, the card says what reopening will actually
cost, and the banner no longer restates either while the card is showing. The card is the
more specific message and wins.

## Ownership becomes structural

The tailnet reconciler mapped one tailnet port per session and kept a state file recording
which ports it had created — because the Manager's own mapping sat in the served set and a
naive "current minus desired" prune would have torn it down.

Path mounting dissolves that. Every mapping the reconciler creates lives under `/s/`, the
Manager's handler is `/`, and "ours" becomes a prefix test over
`serve status -json`'s web handlers. The state file is deleted: nothing needs to persist,
because the served set already says which paths exist.

One thing the frame does not carry is the mount — the registry publishes `id` and `port`, so
the reconciler applies this deployment's own template. And mappings are re-applied rather
than diffed on the path alone: a reaped session keeps its path and changes its port, so
"already served" would leave a handler aimed at a dead port forever.

## What this does not solve

- **A bare `/s/<id>` has no canonicalising redirect.** The shell serves, but the auth
  cookie's `Path` is `/s/<id>/`, so that one request carries no cookie. Harmless — the
  `<base href>` makes every sub-fetch absolute — but it is a rough edge.
- **`SessionTemplate.mount` does not percent-encode the id.** Session ids are Docker-safe by
  construction, so nothing can currently produce a path that needs it.
- **The Manager cannot itself be path-mounted** — `PublicAccess` rejects a path in
  `YESSION_MANAGER_URL`. That is what lets it share an origin with its sessions: the Manager
  is `/`, sessions are `/s/*`.
