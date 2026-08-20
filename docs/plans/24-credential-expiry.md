# Plan 24 — a credential that stopped working says so

[Plan 08](08-connections-and-claude-auth.md) recorded it as a follow-up: *"Refresh-failure
surfacing beyond per-turn errors (a panel health state)."* [Plan 21](21-expiring-tokens.md)
recorded it again, and said why it now mattered more: *"A refresh still broadcasts no
connection-status frame, so the panel shows a healthy connection whose refresh is failing until
a turn says otherwise. Unchanged by this plan, and now more reachable — worth an indicator when
someone hits it."*

Somebody hit it. A GitHub credential on the author's own deployment expired on 15 August and
the panel went on showing a green dot reading `all my sessions (static)` for four days, while
`add_repo` hung behind a keychain prompt (fixed separately in #212) and then failed fast and
silently. Nothing anywhere said "sign in again".

## Why nothing could say it

The fact was dropped at four successive layers, and one of them could never have held it:

| | |
|---|---|
| `BrokerState.refreshExpired` | consulted only INSIDE the `needsRefresh` branch, which made a finished grant's detection depend on the ACCESS token's clock |
| `BrokerObservation.RefreshFailed` | fired, was audited, and did not re-broadcast — only a WRITE moved the status frame |
| `ConnectionStatus` | `{ Id; Kind; UpdatedAt }`, with nowhere to put a health |
| the session's status cache | kept `Map<SecretId, ConnectionKind>`, discarding even the `UpdatedAt` that survived |

And underneath all four: **a static token states no lifetime at all.** `BrokeredStatic` has no
expiry model by design — it is used as-is until a provider rejects it — so no amount of
Manager-side inference was ever going to know. That was the kind that died here.

## Detection, from both ends

**What the Manager can work out.** `BrokerFlow.beyondRefresh` is the whole "sign in again" rule
in one place, so a resolve and a status answer it identically rather than each deciding for
itself. It composes `refreshExpired` with the other way a grant reaches the same dead end — an
expired access token with no refresh token behind it, which `needsRefresh` says `false` for
precisely because there is nothing to refresh with. `Resolve` now asks it FIRST and
unconditionally, so a grant whose refresh token lapsed behind a still-live access token is
refused here rather than dying at the provider as an opaque 401.

**What only the spender can know.** `Reject` records what a provider said, gated by
`ResolveCredential` — the only caller who can have been refused is one entitled to spend it. The
mark lives in Manager memory rather than the encrypted envelope: writing it there would rewrite
`secrets.json` on every 401 for a fact the next use re-learns. Clearing it is part of
`storeCredential`, not something each caller remembers, because signing in again is the remedy
the panel offers and a mark surviving that write would send somebody straight back to it.

Two consumers report:

- **GitHub** asks GitHub. git's stderr cannot answer — `Repository not found` is what github.com
  says both for a private repo a token may not see and for a repo that is not there, so a dead
  credential and a typo are the same sentence. One authenticated request, only on a path that
  has already failed, and only a flat 401 counts.
- **Claude** reads the catalogue lookup's own status, which is already an authenticated call —
  no second request, nothing to parse.

**A refresh that fails now distinguishes "try later" from "sign in again".** A refresh token can
be revoked long before its stated expiry, which no clock predicts; RFC 6749 §5.2 names that
`invalid_grant`, a standard code rather than a dialect, so reading it keeps the broker's promise
never to learn which service it brokered. A 5xx or a dropped socket stays a retry — being wrong
the other way sends someone to re-authorize because their network blipped.

## Saying it once

Three surfaces, one derivation (`ClientModel.signInRequired`), so they cannot disagree:

- **the panel row** turns from green-and-kind to the fault style reading `sign in again`, with
  the provider's own words under it;
- **the agent's roster row** follows the credential's health rather than its presence. It read
  `ready` in green over a Claude credential the next turn would fail on, because
  `agentAvailable` answers whether a credential is STORED;
- **one prompt over the timeline**, and it is the only new BUTTON. The other two are statuses.
  A call to action repeated is wallpaper — the rule the `noAgent*` block and its acceptance case
  already encode.

The prompt is on screen at every width, not only while the sidebar is collapsed the way the
header's "no agent" stand-in behaves. The two differ because their subjects do: an absent agent
is a state you chose and can see in the roster, while a credential that died is news, and news
that reaches you only if a column happens to be collapsed is news that does not reach you.

It is silent while the session leg is down, for a reason rather than for tidiness: signing in
runs against the session, so offering it to somebody who cannot reach the session is offering a
button that cannot work. The degraded strip owns that moment, which is the one-strip-at-a-time
promise it has always made.

## `RevealSettings`

`ToggleSettings` toggles, and a prompt that is on screen while the settings face is already open
would shut the panel it is pointing at. `RevealSettings` is the same move in one direction only
— `settings-open` is set rather than flipped, so pressing it twice is pressing it once — and it
moves focus only when the face actually arrived, because stealing focus into a panel somebody is
already reading is the prompt reaching in after them.

The nav pivots stay toggles. A pivot is a two-way control; a call to action is not.

## Stated, not fixed

**The Claude leg is narrower than the GitHub one.** `ModelCatalogue.cached` keeps the first
success for the session's life, so the catalogue probe catches a credential that was already
dead rather than one that dies after a good lookup. For a brokered grant the Manager covers that
anyway — it sees the refresh refused — leaving only a pasted `sk-ant-` key, which cannot refresh
at all, able to die unseen mid-session. Recorded rather than traded: closing it would mean either
re-probing the provider on a cadence nobody asked for, or parsing the Agent SDK's failure prose,
which is not a contract.

**A rejection does not survive a Manager restart.** It is in memory by choice, and the first verb
after a restart is told again. A panel that reads green for one verb after a restart is the cost
of not re-encrypting the secret store on every 401.

## The paste leg

Removed the way in. `classifyPasted` accepted `ghu_`/`gho_` — user tokens that live about eight
hours and cannot rotate once stored as `BrokeredStatic`. That is exactly how the credential here
was created: it landed 39 minutes before Plan 21's grant leg shipped, so it went in through the
paste path and was dead by morning. Where an App is configured those two kinds are now refused,
naming Connect GitHub, which runs the same authorization and stores the refresh token. Where none
is configured, paste is the only path there is and they are still accepted.
