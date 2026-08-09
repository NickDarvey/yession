# Plan 15 — The imperative session API: commands the agent runs, queries everyone reads

> **Status: stages 1, 2 and 3 shipped.** Stage 1: the query registry, its two projections
> (generated MCP tools and a generated settings surface over one multiplexed SSE stream),
> and the repos migration that retired the Repos panel's write actions. Stage 2: named
> WorkSandboxes with ensure semantics, named credential forwarding, terminals bound to a
> sandbox, and the `work_sandboxes` query. Stage 3: ONE approval gate, of which the
> terminal's is a case — one mode, one subject, one pending list, one handle, one resume
> verb, one card at two mount points. Builds on Plan 14's repo manager and Plan 13's
> one-door terminals.
>
> Deviations are in [What stage 1 shipped](#what-stage-1-shipped) and
> [What stage 2 shipped](#what-stage-2-shipped) at the foot of this document; stage 3's
> are recorded inline with its sub-stages, where the reasoning they belong to is.

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

## Stage 3 — one approval gate, of which the terminal's is a case

Plan 14 deliberately gated nothing; Plan 13 gated `execute_command` alone, inside the
terminal. A gate for commands in general is not a second mechanism beside that one — it is
the same mechanism with the terminal-shaped parts taken out. The evidence that the
generalization is real rather than forced: `TerminalApprovalMode` needs **no new case** to
serve both, and both sides already hand the agent the same handle type (`QueueId`).

### What this stage corrects in its own earlier draft

The first draft of this section said `Gate = Auto | RequiresHuman`, a `CommandPending`
**event**, an MCP call that "yields until a human resolves it", and `YESSION_GATED_COMMANDS`
as the configuration. Three of those four contradict Plan 13, which is shipped and right:

| draft | Plan 13, shipped | which holds |
| --- | --- | --- |
| pending is an event | pending is SYNCED state (`TerminalQueue`); only outcomes are events | Plan 13. A proposal is editable, mergeable and withdrawable before it resolves, and an append-only log cannot hold a value with those three properties. |
| the call blocks until resolved | bounded wait, then a handle and a NAMED status | Plan 13. An unbounded wait ties a turn to a sleeping human, and `TerminalCommandAwaitingApproval` exists precisely so a silent pause is not read as failure. |
| an env var configures it | a synced `ApprovalMode` register per subject, live-editable, re-deciding every waiting entry | Plan 13. The env var survives as the register's boot SEED, which is also the seam `yession.yaml` takes over. |

### The shared vocabulary

**One mode, unchanged.** `TerminalApprovalMode` becomes `ApprovalMode`; the cases
(`AutoRun | ApproveAgent | ApproveAll`) and `requiresApproval mode author` are untouched.

**One subject** — what a gate is *about*, and the key the mode is stored under:

```fsharp
type GateSubject =
    | ForTerminal of TerminalId
    | ForCommand of string          // the MCP tool name

module GateSubject =
    /// The default is per KIND, which is how today's behaviour survives on both sides:
    /// a terminal is reviewed, a command is not.
    let defaultMode = function ForTerminal _ -> ApproveAgent | ForCommand _ -> AutoRun
```

`TerminalModes : Map<TerminalId, ApprovalMode>` becomes `Gates : Map<GateSubject, ApprovalMode>` —
one map, one default rule, one control. For a `ForCommand` subject, `ApproveAgent` and
`ApproveAll` currently collapse (stage 1 made every command agent-only, so there is no other
author to distinguish). The three-case type stays anyway: a `yession.yaml`-authored call has a
different author, and that is the whole reason the distinction exists.

**One pending entry.** `TerminalQueued` already IS this, minus two fields:

```fsharp
type PendingPayload =
    /// A shell command LINE, its text in a `Y.Text` root — editable in place, which IS
    /// the approval UX Plan 13 built.
    | CommandLine
    /// A structured call: tool name plus its rendered arguments. Read-only for now; see
    /// Deferred.
    | CommandCall of tool: string * summary: string

type PendingAct =
    { QueueId : QueueId
      Subject : GateSubject
      Author : ActorRef
      Order : float
      Payload : PendingPayload
      ApprovedBy : PeerId option
      RejectedBy : PeerId option
      RejectedReason : string option }
```

`TerminalQueue : Map<QueueId, TerminalQueued>` becomes `Pending : Map<QueueId, PendingAct>`.

The doc roots are renamed with it — `terminalQueue` → `pending`, `terminalModes` → `gates` —
and a doc written before that is moved over by a **boot migration** (`migrateGateRoots`, run
where `removeEmptyDrafts` runs, when no peer is connected). Not the optional-field compat
`TerminalOpened.Sandbox` uses, and the difference is the point: a decoder that reads both
shapes is two live locations for one fact, which is the spare CLAUDE.md forbids. What forces
a migration rather than a bare rename is narrower and worth stating — dropping the old
`terminalModes` on the floor would put a terminal somebody set to `ApproveAll` back on the
default, LESS gated than what they asked for, silently.

**One handle.** `execute_command` already answers with `Handle : QueueId`; a gated command
answers with the same, so `read_terminal_block` becomes `check_pending : QueueId -> ...`
serving both. The agent learns ONE protocol — ran, or awaiting-with-a-handle, or
refused-by-someone-for-a-reason — instead of one per capability.

**One combinator**, applied where capabilities are composed (`Host.fs`) rather than at the
MCP emit, so the gate holds for every caller and `Agent.fs` stays a renderer:

```fsharp
type GateOutcome<'a> =
    | Ran of 'a
    | Awaiting of QueueId
    | Refused of by: ActorRef * reason: string option

Gates.run : Gates -> GateSubject -> ActorRef -> summary: string
          -> (unit -> Async<Result<'a, string>>) -> Async<GateOutcome<'a>>
```

### What stays different, and why that is honest rather than lazy

- **The drain.** A terminal keeps its serial scheduler, because a shell has one working
  directory and one stdin. `Gates.run` is that scheduler with the serialization removed:
  write the pending entry, watch it, act when it resolves. The shared kernel is the pending
  register and the resolution watcher; the queue's total order is not shared, which is why
  `Order` and the reorder controls stay meaningful only for terminal subjects.
- **Editability.** A command line is characters and can be fixed in place before approval.
  Structured arguments are typed, and editing them needs a form per command — the
  JSON-Schema-subset renderer this plan already deferred once. So a `CommandCall` card offers
  approve and reject and nothing else, and "edit a structured proposal" is named in Deferred
  rather than half-built.
- **Approving is not a command.** A human toggling a gate mode, approving, or rejecting
  mutates SYNCED state, which is collaborative by design (drafts, queue, title, modes) — it
  does not append an event and it is not a command in the sense stage 1 made agent-only. The
  asymmetry holds: commands append events; verdicts are what a human is *for*.

### The record

- **Refusal** gets its own event, `CommandRefused { MessageId; Subject; Command; Author;
  RejectedBy; Reason }`, folded into the timeline as an `ActNote` ("nick rejected
  `add_repo octo/hello` — wrong org"). This mirrors `TerminalCommandRejected` → `BlockRejected`
  for its reason: a refusal that simply vanishes is indistinguishable from a bug, from both a
  human's side and the model's.
- **Approval** rides the command's own event: `ApprovedBy : ActorRef option` on each act event,
  exactly as `TerminalBlockStarted` already carries it. The alternative — a standalone
  `CommandApproved` event, and no per-command work at all — was rejected because it detaches
  the approver from the thing approved and spends two events on one act.

### The surface

One card, extracted from today's `terminalQueue` body: subject chip, author, status token,
body (an editable input for `CommandLine`, a rendered `<code>` for `CommandCall`), then the
same verdict row. Two mount points, from the one component:

- the **chat column**, below the timeline and above the composer — the slot queued messages
  already occupy — showing every pending act, terminal ones included, with the chip on. This
  is what brings terminal approval into the conversation, and it costs no new surface.
- the **terminal panel**, filtered to that terminal, chip off, reorder on.

It does not go INSIDE `TimelineProjection`: that is a fold over events, and a pending act is
not one. Pending is the tail, ordered by `Order`; acts enter the timeline when they resolve,
as a block chip or an act-line, which is what already happens.

Two things the card has to get right against the WCAG floor: verdict buttons need accessible
names that disambiguate across many cards (`aria-label="Approve add_repo octo/hello"`), and
resolving one REMOVES it — the stranded-focus case CLAUDE.md names — so the card must hand
focus on.

### Sub-stages

- **3a** — rename and widen: `ApprovalMode`, `GateSubject`, `Pending`, `Gates`, and the boot
  migration. No behaviour change; the whole point is that the diff is a rename. The one shape
  that is not a rename is the terminal drain's plan, which now carries `(TerminalId *
  PendingAct)` pairs: a `PendingAct` names a `GateSubject`, so a plan of bare acts would make
  every consumer re-answer "which terminal" with a default or a `failwith` — a lie about a
  value the plan's own filter already proved. Command acts wait in the same map and the drain
  skips them: no shell, no order to hold, and their own gate resolves them.
- **3b** — the gate, the `check_pending` unification, `CommandRefused` and `ApprovedBy`, and
  `YESSION_GATED_COMMANDS` seeding the register. Three concretions the sketch above did not
  have:

  - **Every mutating command answers `Result<CommandOutcome, string>`**, not its own typed
    value. A gate has three answers and a listing has one, so the rendering that used to sit
    in the MCP adapter moved into the thunk the gate wraps — which is where it has to be,
    since what the gate carries IS what the agent reads. The payoff is one renderer for every
    gated command instead of one per command, so the next command cannot invent its own
    vocabulary for "somebody has to approve this".
  - **The watcher is detached from the MCP call.** An approval must take effect whether or
    not an agent turn is still waiting, or a human pressing approve would watch nothing
    happen. The call observes; a background continuation acts.
  - **A parked command does NOT survive a restart, and says so.** A terminal command does,
    because the doc holds its whole payload — a line of text a cold drain can run. A
    structured call's arguments are typed and the entry holds only what a human was shown, so
    the thing that would carry it out is the continuation this process is holding.
    `sweepAtBoot` therefore refuses whatever was still parked, attributed to the session and
    with the reason, rather than leaving a card up whose approve button can never do anything.
    Buying durability instead would mean putting the raw arguments in the doc and dispatching
    by tool name — named in Deferred rather than half-built.
- **3c** — the one card, both mounts, and the mode control generalized from the terminal's
  existing `<select>`. Two concretions:

  - **The gate settings surface lists the commands somebody has CONFIGURED**, not every
    command the session has. Gating one nobody has named needs the browser to know what
    commands exist, and nothing declares that to it — the read half of this plan does exactly
    that for queries, and doing it for commands is the same shape. Deferred rather than faked
    with a hard-coded list that goes stale the first time a command is added.
  - **A verdict hands focus on** (`ViewActions.FocusAfterVerdict`): to the next proposal's
    primary control, else the list, else the timeline. Approving REMOVES the card the button
    was on, which is the stranded-focus case CLAUDE.md names — and worse here than for the
    DVR's pair, because a reviewer working down a list loses their place on every decision.

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
- Restart-durable command acts: the raw arguments in the doc plus a dispatch-by-tool-name
  table, which is what would let a cold process carry out an approval it did not propose.
  Today a restart refuses them, visibly (`CommandGates.sweepAtBoot`).
- Editing a structured proposal before approving it. A `CommandCall` card is approve-or-reject;
  making its arguments editable needs the same JSON-Schema-subset renderer the external-server
  item above is waiting on, and a half-built form is worse than a legible refusal.

## Verification

- `check` (cheap): shape → description emission; codec round-trips; the registry's
  invalidation; `SandboxName` validation; the ensure-diff decision; SSR of the generated
  section for each shape (and that the repos buttons are gone). For the gate: the mode
  decision over both subject kinds, the per-kind default, the `PendingAct` codec round-trip
  INCLUDING an entry written before this stage, and the refusal's act-line.
- `check Ports`: `/queries` behind the cookie; the multiplexed stream delivering a snapshot
  then an invalidation frame; a gated command parking and resuming across an approval, and
  the same for a refusal reaching the model as an error.
- `check Browser`: the one card rendered at both mount points, and the verdict controls
  reachable and named at each.
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


## What stage 2 shipped

- **`execute_command` gained the sandbox, and that is what makes the feature real.** A
  started sandbox nobody can run in is a listing. The tool's target became a
  `CommandTarget` (`InTerminal id | InSandbox name`, `None` = the default sandbox's agent
  terminal), and the Host keeps one agent terminal PER SANDBOX — a single cell would have
  quietly run a command meant for `test` in whichever sandbox opened first.
- **`ConversationItemKind.RepoNote` became `ActNote`.** The sandbox events fold into the
  timeline for the repo notes' reason, and a kind per capability would have been a renderer
  per capability. Mechanical rename; the markup hook is `data-act-note`.
- **The refusal names both sides and the way out.** Same name with different forwarding
  answers with what is running, what was asked for, and `stop_work_sandbox` — because the
  alternative (recreating) kills whatever is inside, and a caller that is told only "no"
  will try the same thing again.
- **Forwarding lists are normalised** (trimmed, lowercased, deduped, sorted) before they
  are compared, so `["GitHub","github"]` is the same ask as `["github"]` rather than a
  configuration change nobody made.
- **A credential the caller does not have refuses the start.** Coming up without it would
  turn a legible "sign in first" into a `git push` failure much later, somewhere with less
  context.
- **`default` keeps its entry when stopped**, losing only its configuration: it is the
  sandbox a terminal that names nothing lands in, so the NAME has to stay reachable. Every
  other name leaves the registry entirely, which is what makes stop-then-start-differently
  work.
- **Each named sandbox gets its own workspace** (`<dataDir>/sandboxes/<name>/workspace`)
  and its own backend object name; `default` keeps the path it always had. The repos
  directory is shared by all of them, which is what it is for.
- **`TerminalOpened.Sandbox` decodes as optional**, defaulting to `default`: a log written
  before this stage has terminals that ran in the one sandbox the session had, so that is
  not a guess.
