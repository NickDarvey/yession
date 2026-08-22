# Plan 26 — removing a repo

The agent gains the verb it has been told to use for two plans and never had. `add_repo`
answers an unreadable checkout with

```
octo/hello is already checked out here, but git cannot read that checkout: <reason>.
That is what an interrupted clone leaves behind — remove_repo it, then add it again.
```

and `remove_repo` does not exist. A model reading that either invents a tool call that
fails, or reaches for `execute_command "rm -rf"` — which deletes the tree without the event
that says the repo is gone, so the projection, the `repos` query and the timeline all go on
describing a checkout nobody can read.

The service verb is already there and already tested (`ReposService.RemoveRepo`, deleting
the checkout and appending `RepoRemoved`). What is missing is the door.

## The verb

```
remove_repo { repo, force? }
```

Gated like `add_repo` and `switch_branch`: through `capabilities.RunGated`, so the
classifier sees it, a refusal renders REFUSED, and the act lands in the timeline attributed
to the agent on the turn human's authority.

**A dirty checkout is refused, and `force` is the second decision.** Deleting uncommitted
work is the one thing here that cannot be undone by re-running the verb — a re-clone brings
back the commits and nothing else. So the refusal names the repo and says that `force` will
delete it, and the model has to say so again. The check belongs in `app/Repos.fs`, with the
checkout: a caller who could delete without asking is the caller this exists to stop, and
the listing already knows (`RepoListing.Dirty`).

**An UNREADABLE checkout needs no `force`**, and that asymmetry is the point rather than an
oversight. Clearing one is what this verb was advertised for before it existed; git can say
nothing about what is uncommitted in a repository it cannot open, so there is no work to
protect and nothing a second decision could be about. Refusing there would leave an
interrupted clone with no way out — the exact state `add_repo` sends people here from.

`force` also reaches the GATE, in the summary (`remove_repo octo/hello (deleting
uncommitted changes)`), because that is the sentence a person reading the queue has to see
before it happens rather than after.

## The other half of Plan 25

Plan 25 shipped its downstream guard — a shell that cannot start in the profile's directory
opens in the sandbox default and says why — and deliberately left the upstream half out,
because `RemoveRepo` had no caller to hang it on. This plan gives it one, so the rule lands
here:

**a profile pointing inside a checkout is cleared when the checkout goes.**

Three pieces, each where the fact it needs already is:

- `ReposService.RemoveRepo` answers with the path it deleted **as a terminal in this session
  sees it** (`config.VisibleAt`, the same path `RepoListing.Path` reports) rather than
  `unit`. That is the one fact only the repo service has, and it is the only path a profile
  could be holding — the git sandbox's own path is not visible to any terminal.
- `SessionTerminals.ClearProfilesUnder : ActorRef -> string -> Async<SandboxName list>`
  decides WHICH profiles are affected and appends a `ShellProfileSet` clear for each. The
  arithmetic lives with the state it governs, not in the composition root: `app/Host.fs` and
  `app/Commands.fs` compute nothing.
- `ShellProfile.isInside` is that arithmetic, as a named function rather than an inline
  test, because it has a wrong answer worth pinning: a bare `StartsWith` makes
  `/repos/octo/hello-world` look like it is inside `/repos/octo/hello`, and clearing that
  sibling's profile would be silent.

Unlike `SetProfile`, this validates nothing and retires no terminal. It is not somebody
deciding where terminals should start; it is a place they already start in ceasing to
exist. Terminals open in it keep running — a shell whose directory is deleted is a fact of
the filesystem, not ours to tidy — and the next one to open lands somewhere that does.
- The dispatch arm in `app/Commands.fs` joins them, exactly as it already joins
  `service.AddRepo` to `Repos.queryName`, and invalidates `ShellProfile.queryName` when
  anything was cleared.

The answer says both halves, because the model has to know its next terminal moved:

```
removed octo/hello. New terminals in default start where the sandbox puts them again.
```

## Wiring, file by file

| File | Change |
| --- | --- |
| `src/Yession.Domain/Agent.fs` | `RemoveRepo : RepoRef -> bool -> Async<Result<CommandOutcome, string>>` on `AgentCapabilities`, refusing in `none` |
| `src/Yession.Domain/ShellProfile.fs` | `isInside`, the directory-boundary comparison |
| `src/Yession.Domain/AgentTools.fs` | the `remove_repo` descriptor and body |
| `src/Yession.SessionProcess/Terminals.fs` | `ClearProfilesUnder` on the manager, beside `SetProfile` |
| `app/Repos.fs` | `RemoveRepo` takes `force`, refuses a dirty checkout, answers with the visible path |
| `app/Commands.fs` | `removeRepoTool` in `dispatch` + the gated binding |

## Deliberately not here

- **No panel button.** Plan 15's asymmetry: a human who wants a repo removed asks, and the
  mutation lands in the timeline attributed. The `repos` query still shows what is there.
- **No recursive profile rules.** A profile inside a checkout is cleared; a profile that
  merely mentions the repo name is not. The comparison is a path prefix on a directory
  boundary, and nothing cleverer.
- **No un-remove.** `add_repo` is the way back, and it is one call.

## Tests

Cheap tier:

- the registry declares `remove_repo`
- an unmentioned `force` reaches the capability as a decision NOT to delete uncommitted work
- an explicit `force` reaches it too
- `isInside` holds for the tree itself and anything under it, either side's trailing slash
  notwithstanding
- ... and refuses a sibling that merely shares a prefix, and a parent

The terminal tier (over a scripted sandbox, so still cheap):

- a tree going away takes the profiles pointing into it
- `ClearProfilesUnder` answers with the sandboxes it cleared, so its caller can say so
- a profile in a prefix-sharing sibling is left alone, and still points where it did
- the next terminal after a cleared profile opens where the sandbox puts it

`Srt` tier (where the repo verbs already run, over a real checkout):

- a checkout with uncommitted changes is refused, the refusal names the repo and `force`,
  and the checkout is still there afterwards
- `force` removes it
- removing answers with the path the LISTING reported, which is what a profile could hold
