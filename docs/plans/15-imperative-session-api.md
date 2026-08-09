# Plan 15 — The imperative session API: commands the agent runs, queries everyone reads

> **Status: stage 1 shipped** — the query registry, its two projections (generated MCP
> tools and a generated settings surface over one multiplexed SSE stream), and the repos
> migration that retired the Repos panel's write actions. Stages 2 (named WorkSandboxes
> and named credential forwarding) and 3 (approval gates for commands in general) follow.
> Builds on Plan 14's repo manager and Plan 13's one-door terminals.
>
> Deviations taken while implementing stage 1 are in
> [What stage 1 shipped](#what-stage-1-shipped) at the foot of this document.

Plan 14 shipped one repo manager with two interfaces: the agent's MCP verbs and a Repos
panel that could drive every one of them. That is symmetric, and symmetry is the wrong
shape. Two interfaces over one function means two of everything — two authorization
paths, two sets of inputs to validate, two places a mutation's record can be forgotten —
for a surface the human already has a better way to reach: **asking the agent**.

So this plan makes the split asymmetric, and makes the asymmetry the contract:

- **Commands mutate, and only the agent runs them.** A human who wants a repo added says
  so in the conversation. The mutation lands as an attributed event, so the timeline is
  the record — the same act-line Plan 14 already renders.
- **Queries read, and everyone gets them.** One registry, projected twice: to the agent as
  MCP tools, to the browser as a **generated** read-only settings surface. Nobody writes a
  panel for a new query; declaring it is what puts it on the screen.

This is agent-first design that stays cheap for a human to verify. It is also the
substrate the declarative form needs: a `yession.yaml` executor is a fold of the file into
these commands at boot, which is why every command here is written to be **ensure-shaped**
(convergent, re-runnable, silent when already satisfied) rather than imperative-once.

## What a query is, exactly

A query is a **nullary, read-only, shape-declared** view of session state.

- **Nullary** is what makes the generated UI honest. A parameterised read needs a form,
  a form is an input, and an input on a read-only surface is a command wearing a
  disguise. `repo_status octo/hello` is a fine agent tool and it is *not* a query; it
  stays exactly what Plan 14 made it.
- **Read-only** is declared, not inferred: the MCP tool carries `readOnlyHint: true`
  (`ToolAnnotations`, which the Agent SDK's `tool()` plumbs through as `_extras.annotations`).
  That is the MCP spec's own marker, so identifying queries in a **third-party** MCP server
  — the machine-queue broker on the other branch — needs no yession-specific convention.
  Where the transport carries `outputSchema` too, the shape rides there; the in-process SDK
  server has no slot for it today, so the shape is rendered into the description.
- **Shape-declared** is what lets one renderer draw all of them. A query declares
  `Rows` (a table), `Fields` (a record), or `Value` (a scalar), and the renderer maps
  each to the one markup that fits. The WCAG floor — `th scope`, an accessible name,
  contrast on the tokens — is implemented once, in the renderer, instead of once per
  panel.

Queries are values, so they are also the invalidation unit: a capability that changes
state calls `Invalidate "repos"`, and every subscriber gets that query's fresh value.

## Architecture

```
                     ┌──────────────────────────────────────────┐
  agent (MCP) ◄──────┤  QueryRegistry  (Session Process)        ├──────► browser
   readOnlyHint      │   name → shape × (unit -> Async<Value>)  │   GET  /queries
   tools, generated  │   Invalidate name → push                 │   GET  /queries/<name>
   from the registry └──────────────────────────────────────────┘   SSE  /queries/stream
                              ▲                                       (ONE stream, all queries)
        commands ─────────────┘  Invalidate
   (agent-only MCP tools, each appending an attributed event)
```

**Live updates are multiplexed.** One `EventSource` per session carries every query's
changes as `{"name":..., "value":...}` frames, with a snapshot burst on subscribe. A
stream per query would cost a connection per registered query and grow with the registry;
this costs one, forever, and the browser's fold is `Map.add name value`. The transport is
the repo's existing `Sse.stream` over a `Push` hub, so a query registry plugs in with no
adapter. (MCP's native equivalent is resource subscriptions; the agent re-invokes the
tool instead, which is what an agent does anyway between turns. Named as deferred.)

**Commands stay agent-only.** They are the existing typed `AgentCapabilities` — this plan
adds to them rather than inventing a parallel mechanism — and each appends its own event
so the act-line appears in the timeline (and therefore in the agent's context pack, since
the pack is built from the same projection).

## Stage 1 — the registry, its two projections, and the repos migration

- `src/Yession.Domain/Queries.fs` — `QueryName` (validated), `QueryShape`
  (`Value | Fields of columns | Rows of columns`), `QueryValue`, the JSON codec both ends
  share, and `QueryValue.describe` (the agent's text rendering).
- `app/Queries.fs` — the registry: `Register`, `List`, `Read`, `Invalidate`, and a
  `Subscribe` in the repo's `Push` vocabulary. `routes` serves `/queries`,
  `/queries/<name>`, and `/queries/stream`, cookie-gated beside the connection panels.
- `app/Agent.fs` — queries become MCP tools **generated from the registry** (an array
  passed into the emit) rather than another positional `$N`. This also retires the
  renumbering hazard for every future query.
- `src/Yession.App/View.fs` — ONE generic `queriesSection` in settings, rendering each
  registered query by its shape. `reposSection` and the `RepoAdd`/`RepoRemove`/
  `RepoSwitchBranch` actions are deleted; `RepoList`/`Repo of RepoPanelAction` routes and
  the `/repos*` handler go with them.
- Repos migrate: `list_repos` becomes the `repos` query (Rows: repo, branch, dirty);
  `add_repo`/`switch_branch`/`remove` stay agent commands and invalidate it.

## Stage 2 — named WorkSandboxes and named credential forwarding

- `SandboxName` (validated, private ctor — the `RepoRef` idiom). A registry of running
  sandboxes keyed by it; the boot WorkSandbox becomes `default`, unchanged in behavior.
- `start_work_sandbox { name, forward }` is **ensure-shaped**: same name + same config
  returns the running one silently and records nothing; same name + *different* config is
  a legible error naming the difference ("stop it first"), never a silent recreate —
  recreating kills whatever is running inside it. `stop_work_sandbox { name }`.
- `forward` is a **list of credential names** (`["github"]`), never a bool. Each resolves
  for the turn human under Plan 08 precedence and enters that sandbox's policy env at
  spawn. The event records **which names, and whose credential** — never a value. This
  lands Plan 14's deferred "credential forwarding into the WorkSandbox" item, which is
  what makes `git push` from a terminal work.
- Terminals gain an optional sandbox name (default `default`). Without this a named
  sandbox has no door — `execute_command` is the only way to run anything, so it must be
  able to say *where*.
- Query `work_sandboxes` — Rows: name, backend, state, forwarding, started-by, started-at
  — read from process truth, the way `list_repos` reads the filesystem's answer.

## Stage 3 — approval gates, as a property of commands in general

Plan 14 deliberately gated nothing; Plan 13 gated `execute_command` alone, inside the
terminal. Neither generalizes. This stage makes the gate a property every command has and
most decline to use:

- `Gate = Auto | RequiresHuman`, default `Auto` — today's behavior for every existing
  command, unchanged.
- A gated call parks: a `CommandPending` event renders in the timeline with approve/deny
  (the queued-command surface Plan 13 already built), and the MCP call yields until a
  human resolves it. Denial comes back to the model as a legible tool error, for the same
  reason Plan 13's refusals do: a command that vanishes silently gets retried another way.
- Configured by `YESSION_GATED_COMMANDS` (empty by default) until `yession.yaml` takes it
  over.

## Stated risks

- A forwarded credential lives in a sandbox's env for that sandbox's lifetime, readable by
  everyone in the session and everything running in it. Session = shared trust boundary,
  as Plan 14 stated it; revoking at the provider does not claw back what was injected.
- Retiring the panel's command buttons means a human with no working agent cannot add a
  repo. Accepted: that is the same session in which nothing else works either.

## Deferred

- The `yession.yaml` executor that folds a file into these commands (the plan this API
  exists to serve).
- External MCP servers listed into the registry — the convention is stated and the shape
  DSL is the only thing in the way; a JSON-Schema-subset renderer is the missing piece.
- MCP resource subscriptions for the agent side of live updates.
- Per-query authorization (today every signed-in session member reads every query).

## Verification

- `check` (cheap): shape → description emission; codec round-trips; the registry's
  invalidation; `SandboxName` validation; the ensure-diff decision; the gate state machine;
  SSR of the generated section for each shape (and that the repos buttons are gone).
- `check Ports`: `/queries` and `/queries/<name>` behind the cookie; the multiplexed
  stream delivering a snapshot then an invalidation frame; a gated command parking and
  resuming across an approval.
- `check Srt`: a forwarded credential reaches the named sandbox's env and nothing else;
  same-name idempotence; the config-diff refusal.
- `verify` on master stays the release gate; `lint` first.
- Each stage takes the version marker its own change warrants rather than saving one for
  the end: every stage here adds a user-facing capability, so each carries `+semver: minor`
  on its own line in a commit body on its branch (the PR description is discarded by the
  squash).

## What stage 1 shipped

Deviations and concretions against the sections above:

- **There is ONE route, and it is the stream.** The plan named `/queries`, `/queries/<name>`
  and `/queries/stream`; what shipped is `/queries`, an SSE stream whose opening burst is
  the snapshot — the declarations, then every query's current value, then a value whenever
  a command invalidates one. A snapshot fetch beside it would have answered nothing the
  burst does not, and would have raced it (connect after the fetch, miss the update in
  between). Deleting it also deleted the `stream`-vs-query-name path collision.

  These are `RetainedHub`'s semantics at per-query grain — the same contract every other
  SSE leg in the repo already keeps: current state on subscribe, and every later frame a
  WHOLE value rather than a delta, so a reconnect is the entire recovery protocol and one
  connect-read-disconnect is a poll. Two differences, both deliberate: the opening state
  spans a frame per query rather than one frame, and nothing is retained — each frame is
  READ when it is sent, because the truth is the filesystem's and the process table's, and
  a retained snapshot could hand a reconnecting client an answer that is already wrong.
- **A query is nullary, and that is enforced by the type.** `QueryDef` carries no input at
  all. It is what keeps the generated surface honest: a parameterised read needs a form, and
  a form on a read-only surface is a command in disguise. `repo_status octo/hello` therefore
  stayed an ordinary agent tool.
- **The registry holds a capability to the shape it declared.** `QueryValue.fits` runs on
  every read, so a capability answering a `Rows` query with a scalar is an error at the
  registry rather than a case every renderer downstream has to carry.
- **A failed read pushes nothing.** A stream frame says what a query answers, and "it
  failed" is not an answer — a subscriber keeps its last good value and the failure surfaces
  in the agent's tool call, where somebody can act on it.
- **`list_repos` became the `repos` query rather than keeping its name.** A nullary noun
  reads the same to a model and matches the section it renders as; the `list_` prefix was
  the shape of a tool, not of a view.
- **The SDK's `tool()` carries `annotations` but not `outputSchema`**, so `readOnlyHint`
  ships as the real MCP annotation and the shape rides the tool DESCRIPTION, emitted from
  the same `QueryShape` the renderer draws. The convention is unaffected: a third-party
  server is identified by the annotation, which is the part the spec defines.
- **Queries are generated into the agent's tool array**, replacing `list_repos` at the
  emit's `$12`. Adding a query no longer touches `Agent.fs`, which also retires the
  positional-renumbering hazard for every query after this one.
- **The browser uses `EventSource`**, not the repo's fetch-based SSE reader: it is the
  platform's own client, it reconnects itself, and it carries the session cookie
  same-origin — which is the whole authentication story for a cookie-gated route.
- **`ReposViewState` lost `Busy` and `Error` rather than keeping them unused.** The surface
  has no actions, so nothing can be in flight; the absence is the design.
