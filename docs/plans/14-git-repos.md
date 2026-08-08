# Plan 14 — Git repos: bootstrap clones beside the agent, shared into the WorkSandbox

> **Status: in progress.**

The agent needs repos to work on, and the WorkSandbox will eventually be configured *from*
a repo (`.yession.yml`, a devcontainer, a CLAUDE.md) — a chicken-and-egg the runtime could
not express: nothing could clone before an environment existed, and the environment spec is
frozen at boot. This plan gives the session a **repos directory** both sandboxes see and a
**read-only bootstrap surface** for getting repos into it, and deliberately nothing more.

## The shape: a read-only airlock, and one door for everything irreversible

The agent gets typed git verbs — `add_repo` (clone), `list_repos`, `switch_branch`,
`fetch`, `status`, `log`, `diff` — and **no commit, no push, no history mutation**. Anything
irreversible happens in the WorkSandbox through `execute_command`, where Plan 13's approval
gate, block visibility, and asciicast transcript already exist. That keeps the one-door
invariant intact: adding a git surface adds no second execution path for changes that
matter, and "push needs approval" costs nothing because the only push path is the one that
already has approval.

The same function has two interfaces: the agent's MCP verbs and the Repos panel in the
session UI both call the one Process-side repo manager, and every change — add, remove,
branch switch — is an event, projected into the conversation timeline so humans and the
agent's context both see the history.

## Where git runs

Git executes inside a **sibling srt sandbox** under the agent backend
(`YESSION_AGENT_SANDBOX`): the same `SrtSandbox.create` the WorkSandbox path uses, with a
policy of exactly the repos dir (read+write) and `github.com` egress. Confinement is
per-spawn argv rewriting, so each verb is its own short-lived confined process. Under
`host` the verbs run unconfined — `host` is the explicitly lax choice everywhere, and this
plan follows the operator's word rather than special-casing git.

Repo-controlled execution is disabled *as well as* confined — every invocation pins
`GIT_CONFIG_GLOBAL=/dev/null`, `GIT_CONFIG_SYSTEM=/dev/null`, `GIT_TERMINAL_PROMPT=0`,
`GIT_ALLOW_PROTOCOL=https`, and per-invocation config forcing `core.hooksPath` to an empty
directory, `core.fsmonitor=false`, and `protocol.ext.allow=never`. The WorkSandbox can
write the repos dir (that is the point), so a poisoned `.git/config` is assumed and made
inert rather than trusted-by-placement.

The repo argument is a validated `owner/repo` — the clone URL is constructed
(`https://github.com/{owner}/{repo}.git`), so there is no free-form remote and no SSRF
surface.

## The directory

`<dataDir>/repos/<owner>/<repo>`, created at session boot:

- host/srt WorkSandbox: added to the sandbox policy's write paths beside the workspace;
- docker WorkSandbox: bind-mounted at `/repos` beside the named workspace volume;
- the git sandbox: its only read/write path.

One host directory for all three backends, so a bootstrap clone is visible in the
WorkSandbox the moment it lands, with no container recreation and no copy step. It lives
in the session data dir, so it survives idle reaping and relaunch for free; `list_repos`
reports each repo's branch and dirty state on resume — drift is told, not silently fixed.

## Sign-in: the device flow, because the broker is a public client

Users sign into GitHub individually, from the session's settings panel, exactly like the
Claude sign-in — same scope choice ("this session" / "all my sessions"), same storage, same
per-actor resolution (session-scoped credential ▸ the acting human's own ▸ none, failing
legibly).

The Plan 08 broker is deliberately PKCE-public-client-only, and GitHub's authorization-code
exchange demands the App's client secret — so this connection uses the **device flow**
(RFC 8628), which needs only the client id: the session asks github.com for a user code,
the human approves it in their own tab, the session polls the token endpoint, and the
resulting token is stored through the broker's existing paste path as a static credential.
The Manager is untouched and never learns the service. Pasting a PAT works identically.

The operator registers their own GitHub App (device flow enabled, **user-token expiration
disabled** — a static credential cannot be refreshed) and sets `YESSION_GITHUB_CLIENT_ID`.

A GitHub App user-to-server token reaches the intersection of the user's access and the
App's installations — so "a session may only add repos the App covers" is enforced by the
credential itself, with no policy code. The token reaches exactly one place: the env of the
single confined git invocation that needs it, per operation, never the sandbox policy env,
never the WorkSandbox, never the transcript.

## Stated risks (session = shared trust boundary)

- One user's private repo, once added, is readable by everyone in the session and by
  everything in the WorkSandbox; revocation at GitHub does not claw back bytes on disk.
  The panel says so at add time; the `RepoAdded` event names who added it.
- A pasted PAT bypasses the App-installation scope rule.
- The stored token does not rotate (device flow + static storage). Revoke at GitHub.
- srt's egress allowlist is per process (GAPS): allowing `github.com` for git extends to
  the WorkSandbox's reachable set. Accepted — terminal git legitimately wants it.
- Under `YESSION_AGENT_SANDBOX=host`, git verbs run unconfined, by the operator's explicit
  choice of `host`.

## Deliberate scope / deferred

- `.yession.yml` → EnvironmentSpec wiring (the follow-up plan; this plan gets the file
  into the checkout, nothing consumes it yet).
- Credential forwarding into the WorkSandbox (terminal `git push`): later, configurable
  via `.yession.yml`. In this plan terminals do local git only.
- Installation-token swap (verify with the user token, fetch with a short-lived
  `contents:read` installation token) — the token provider sits behind a seam for exactly
  this; needs App-private-key custody, deferred.
- Broker confidential-client + refresh support (expiring user tokens).
- Commit attribution machinery (author = requesting user, `Co-Authored-By: Claude`) —
  lands with the terminal-side commit flow, not here.

## Delivery

1. **The GitHub connection** — `app/GitHubConnection.fs` (device flow + paste over the
   broker's `Put`), `GitHubStatus`/`GitHub` routes, the settings-panel section, pure flow
   tests + a Ports-tier stub-endpoint exchange.
2. **Repos vocabulary and visibility** — `RepoRef`/`SessionRepo`/`ReposProjection`,
   `RepoAdded`/`RepoRemoved`/`RepoBranchSwitched` events with codecs and timeline
   projection, `<dataDir>/repos`, WorkSandbox policy + docker mount.
3. **The repo manager and the verbs** — the confined git runner, the Process-side
   manager both interfaces share, the seven MCP tools, Srt-tier confinement suite
   against local fixtures.
4. **The panel and the record** — the Repos settings section, timeline rendering,
   GAPS entries, this document's status flip.
