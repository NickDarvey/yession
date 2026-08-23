# Plan 25 — the shell profile

A session gains one durable fact about its terminals: **where a new shell starts**. The
agent sets it with a verb, everyone reads it as a query, it survives a restart, and every
terminal opened afterwards — the agent's and the people's — starts there.

```
add_repo octocat/hello-world      -> "added octocat/hello-world (branch main) at /repos/hello-world"
set_shell_profile cwd=/repos/hello-world
execute_command "git status"      -> runs in /repos/hello-world, and so does the next one,
                                     and so does the terminal someone opens in the strip
```

Today that loop is four `cd`s a turn, one per terminal, re-issued after every restart,
and the people in the session get none of them: a human who opens a terminal lands in the
sandbox workspace while the agent's terminal is three directories away, so the two halves
of one session are looking at different trees.

## The verb, and what it carries

```fsharp
type ShellProfile =
    { /// Where a shell opened under this profile starts. `None` = the sandbox's own
      /// default, which is what every terminal did before this plan.
      WorkingDirectory : string option }
```

One field in stage 1. `set_shell_profile` takes `cwd` (optional — omitted clears it) and
`sandbox` (optional — the default one), the same two-argument shape `open_terminal` already
uses, because it is the same question asked of the same thing.

**A profile is per sandbox, not per session.** A path is only a path inside the filesystem
that has it: the `default` sandbox's workspace, a named sandbox's workspace under
`sandboxes/<name>/`, and a docker sandbox's `/repos` bind are three different trees, and one
session-wide string would be a fact that is true in one of them and a broken shell in the
others. The repos directory is shared by all of them, which is exactly why the interesting
case — a repo checkout — works everywhere without the profile having to know that.

**It is not rc text.** The temptation is a free-text `profile` the shell sources: aliases,
exports, a `cd`, anything. That is an ungated command running in every future terminal,
authored once and never seen again — around the classifier (Plan 23), around the editable
queue people read, and out of the transcript, since a shell that starts by running somebody's
script has already run it before the first prompt mark. `execute_command` is the one door,
and a profile that could carry a command line would be a second one with a lock on the inside.
So the profile is STRUCTURE — a directory now, named environment variables in stage 2 — and
each field is applied by the spawn, not typed at a prompt.

## Where it lives

The rule is `docs/../CLAUDE.md`'s colocation one: a profile that a CALLER remembered to
apply is not a profile. So the state and the verb sit in `SessionTerminals`, the module that
opens terminals, and nothing above it computes anything.

- **The event.** `ShellProfileSet { MessageId; Sandbox; WorkingDirectory : string option;
  Actor }` in `Events.fs`, with its codec and dispatch arm in `Serialization.fs` and a
  literal-JSON pin like every other payload. One event for set and clear — clearing is
  `WorkingDirectory = None`, not a second verb, because "what does a new shell do" has one
  answer at a time.
- **The projection.** `ShellProfileProjection` in a new `src/Yession.Domain/ShellProfile.fs`
  (after `Environment.fs`; it needs `SessionEvent` and `SandboxName`): a
  `Map<SandboxName, ShellProfile>` folded from the log, newest set wins, unknown events
  ignored. A pure fold, like `ReposProjection` — testable in the cheap tier with no session
  around it.
- **The verb.** `SessionTerminals.SetProfile : SandboxName -> string option ->
  Async<Result<string, string>>` — validate, append, apply, answer. It is one verb because
  the three cannot be separated: a caller able to append without validating is a caller who
  can point every future terminal at a directory that is not there.
- **Replay.** `SessionTerminals.create` already takes the terminals the log left open;
  it takes the folded profiles the same way, from the same replay, so a restarted session
  opens its next terminal where the last one started.

## What "apply" means

`SandboxExec.WorkingDirectory`, on the spawn — never a `cd` typed at the prompt.

Three reasons, and each is a bug avoided rather than a preference. A typed `cd` echoes into
the transcript on the re-arm path (`rearmers` re-types the rc bootstrap into whatever shell
is there NOW, with `ready` already true), so it would appear in the audit trail as a command
nobody ran. It has to be quoted for a path this code did not choose. And it cannot fail
visibly: `cd /gone` prints to a terminal that then carries on, whereas the spawn's cwd is
answered by the OS at the one moment something is listening.

Both spawn paths take it, and that is not optional:

- `openShell` — the instrumented pty. This is the one that matters: the shell holds its
  directory across blocks, so the profile is applied once per terminal.
- the degraded per-block `Spawn` (a sandbox with no pty). A terminal there gets a fresh
  process per block and carries nothing between them, so the profile is applied per block —
  which is the same promise, kept by the only means that path has. If only the first were
  wired, a degraded terminal would silently ignore the profile, and the difference between
  the two paths would show up as "the same command answers differently in two terminals".

Attached terminals (Plan 16 part D) take nothing: their bytes come from somebody else's
process, and there is no directory of ours to start it in.

## The agent's own terminal is retired, once

`execute_command` with no terminal named runs in the agent's general-purpose terminal for
that sandbox — opened lazily, kept for the session's life. Left alone, a profile change would
be invisible in exactly the flow that motivates it: set the profile, run `pwd`, get the old
directory, conclude the tool did nothing.

So `SetProfile` closes the agent's general-purpose terminal in that sandbox, and only that
one. It is the manager's own (`agentTerminals`, minted on demand, never named by the agent),
the next command reopens it in the new directory, and nothing is lost but a shell's history.
Terminals the agent NAMED (`open_terminal`) and terminals people opened are untouched — those
were asked for, and taking somebody's shell away because a directory default changed is not a
default's business. If the general-purpose terminal is busy it is left alone too, and the
answer says so: killing a running command to change a default is the wrong trade in the one
direction that cannot be undone.

The answer states all of it, because the model has to know what it is looking at:

```
new terminals in default start in /repos/hello-world. Your command terminal was reopened
there; terminals you opened, and the people's, keep the directory they are in.
```

## Validating the directory

At set time, in the sandbox, by asking it: `/bin/sh -c 'test -d "$1"' sh <path>` through
`environmentFor sandbox`. The path rides as an argv element, never interpolated into a
command line — the one place this feature could become the second door.

Host-side `Fs.exists` would be the wrong answer twice over: under docker the path is inside a
container this process cannot see, and under srt the sandbox's read scope is not ours. A
relative path is refused outright (a shell's idea of "relative to what" is the thing being
configured). The refusal names the path and says the checkout paths `repos` reports are what
this takes, so the failure is one the model can act on rather than retry.

## Upstream and downstream, because the directory can go away

Per CLAUDE.md's fixing-bugs rule, both halves:

- **Upstream**: `ReposService.RemoveRepo` clears any profile whose cwd is inside the
  checkout it is deleting, by calling `SetProfile` with `None` — the rule and its event stay
  with the manager that owns them; what the repo service contributes is the one fact only it
  has, that this tree is about to stop existing. Its answer says which sandbox's profile it
  cleared. (There is no `remove_repo` agent tool today; the service is reached from the
  session's own maintenance paths, and the same call sites gain the clear.)
- **Downstream**: if a spawn with the profile's cwd fails anyway (deleted from a terminal, an
  unmounted volume), `openShell` retries once with the sandbox default and emits a transcript
  line saying so — the terminal opens, everybody reads why it is not where it should be, and
  the profile is left alone for a human to fix. A terminal that refuses to open because of a
  default is a worse failure than the default being wrong.

## The query

`shell_profile`, registered like `repos` and `sandboxes` — one registration, and it reaches
the agent as a `readOnlyHint` MCP tool and the people as a section on the generated settings
surface. Nobody writes a panel.

`Rows` over `sandbox` / `starts in`, one row per sandbox that has a profile, read from the
manager's current state. `SetProfile` invalidates it, which is what pushes the change to
every subscriber's stream — the same `andPublish` the repo commands use.

## Wiring, file by file

| File | Change |
| --- | --- |
| `src/Yession.Domain/Events.fs` | `ShellProfileSet` case + payload |
| `src/Yession.Domain/Serialization.fs` | codec, encode arm, decode arm |
| `src/Yession.Domain/ShellProfile.fs` (new) | `ShellProfile`, `ShellProfileProjection` |
| `src/Yession.Domain/Yession.Domain.fsproj` | compile after `Environment.fs` |
| `src/Yession.Domain/Conversation.fs` | the timeline note ("new terminals in default start in …") |
| `src/Yession.Domain/Agent.fs` | `SetShellProfile` on `AgentCapabilities`, refusing in `none` |
| `src/Yession.Domain/AgentTools.fs` | the `set_shell_profile` descriptor and body |
| `src/Yession.SessionProcess/Terminals.fs` | profiles map, `SetProfile`, cwd on both spawns, the retire rule, the fallback |
| `app/Host.fs` | pass the replayed profiles into `create`; expose nothing new (`Terminals` is already on the record) |
| `app/Commands.fs` | `setShellProfileTool` in `dispatch` + the gated binding, `Terminals` getter on `CommandServices` |
| `app/ShellProfile.fs` (new) | the `shell_profile` `QueryDef` + `query (terminals)` registration, beside `Repos.query` and `WorkSandboxes.query` in shape and in reason: the registration belongs to whoever owns the answer, and `QueryRegistration` is a Host-layer type the Session Process cannot see |
| `app/SessionMain.fs` | fill the `Terminals` getter; register the query |

It is a gated command, like `add_repo` and `start_work_sandbox`: it goes through
`capabilities.RunGated`, so the classifier sees it, a refusal renders as REFUSED through
`renderCommandOutcome`, and the act lands in the timeline attributed to the agent with the
turn human's authority. A verb that changes what every future terminal does is exactly the
kind of act the gate exists for.

## Stage 2 — environment variables

The same profile grows `EnvironmentVariables : Map<string, EnvironmentVariableRef>`, reusing
`Environment.fs`'s existing `PlainValue | SecretRef`, and they ride `SandboxExec.Env` (merged
over the sandbox policy env, request wins) on the same two spawns. Because they ride the
spawn rather than a typed `export`, there is no echo problem at all — nothing reaches the
transcript.

`SecretRef` is what makes it shippable: a plain value is recorded verbatim in the event log
and visible to everyone in the session, so a token belongs in `set_secret` and a REFERENCE to
it in the profile, resolved at open through the same `resolveSecretRef` seam the sandbox
policy uses. The tool description says which is which, and the query prints a ref by name and
never by value.

Separate stage, separate PR: stage 1 is independently shippable and answers the question that
prompted the plan.

## Deliberately not here

- **No rc text, no aliases, no shell functions.** See above: that is a second execution door.
- **No `cwd` argument on `open_terminal`.** A per-terminal override is a different feature
  (and a smaller one); the profile is about what happens when nobody says.
- **No change to `EnvironmentSpec`.** The sandbox's own working directory is fixed at
  creation, and changing it means recreating the sandbox — which kills everything running in
  it. The profile is the layer over a LIVE sandbox, which is why it can be set mid-turn.
- **No retroactive move.** Terminals already open keep their directory. A shell's cwd is
  state its user is relying on, and the one terminal we do reopen is the one nobody named.

## Tests

Cheap tier — the fold and the vocabulary, no session:

- a set replaces the previous profile for its sandbox and leaves other sandboxes alone
- a clear (`None`) returns that sandbox to no profile
- `ShellProfileSet` round-trips, and its literal JSON is pinned
- the registry declares `set_shell_profile`, and a call with unreadable arguments is an
  `Error` (the call did not happen) while a refused one renders REFUSED (it did)
- `shell_profile` answers in the shape it declares

The terminal tier (where the existing terminal suites run, over a recording sandbox):

- a terminal opened after a profile is set spawns with that working directory — the invariant
  the whole plan exists for
- the degraded per-block path spawns with it too
- a profile whose directory does not exist is refused, and the profile is unchanged
- a relative path is refused
- replaying a log that contains a `ShellProfileSet` into a fresh manager still opens
  terminals there — the restart promise
- setting a profile retires the agent's idle general-purpose terminal (the next
  `execute_command` runs in a different terminal id) and does NOT retire a busy one, a named
  agent terminal, or a person's
- a spawn that fails on the profile's directory opens the terminal in the sandbox default and
  records the reason in the transcript

Pty tier, one case, because it is the only one that proves the promise end to end: set the
profile, open a real instrumented shell, run `pwd`, read the profile's directory back.
