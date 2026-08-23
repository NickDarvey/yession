---
name: contributing-changes
description: How to integrate finished planned work into master — compare implementation to plan, open a PR with auto-merge, watch CI, and drive it to a merged, green state. Read this whenever you are completing a plan and the implementation is done: before committing, opening a PR, or declaring planned work finished. Also re-read it after any CI failure on a PR you opened, since fixing and re-integrating repeats this process.
---

# Contributing changes

This is the exit ramp for planned work. When the implementation is done, don't stop at
"changes pushed" — a change isn't contributed until it's merged to master and the master
pipeline is green. This skill defines the gate for proceeding autonomously and the loop
that drives a change all the way through.

## Step 0: Integrate in the smallest increments the plan allows

Do this before writing the code, not after. A plan's Delivery steps are usually
independently shippable — each one compiles, passes `check`, and is useful on its own. When
they are, integrate each as its own PR rather than saving them up: finish step one, run this
process end to end, then start step two from the merged master.

The cost of not doing this is not style, it is rebase tax, and it compounds. The merge queue
pays the mechanical half for you — a green check no longer goes stale because master moved —
but not the half that hurts: conflict reconciliation. Five PRs landed under Plan 17's single
four-step branch, and reconciling one branch against them took longer than all four features
took to write. Each of those four steps would have merged in minutes on its own. The tax is
superlinear — twice the branch against twice the drift is four times the conflict — and
every hour of it is spent re-deciding questions already answered.

Split on the plan's own seams:

- **Ship a step when it stands alone.** It builds, `check` passes, and nothing merged is
  broken by it — even if the feature it belongs to is not yet whole. A capability behind an
  unset flag, a domain type nothing calls yet, an engine with no provider on top: all
  mergeable.
- **Keep steps together only when splitting would land something broken** — a protocol
  change and the only caller that speaks it, a rename and its call sites.
- **Bump the version once, on the step that earns it.** Four PRs is still one feature; the
  marker goes on the commit that actually moves the contract, not on each slice.

If you find yourself with several finished steps on one branch anyway, do not bundle them
out of sympathy for the work already done — open the PR for what is there, and split the
NEXT plan.

## Merge semantics: the queue

`master` merges through a **merge queue**, not directly, and squash is the only method.

- **A moving master costs you nothing now.** Branches are not required to be up to date, and
  a green check does not go stale when someone else lands first — the queue re-tests your
  change against real master itself. Rebase only for a genuine conflict.
- **Enabling auto-merge enqueues; it does not merge.** The queue builds base+PR on a
  temporary ref and runs `test` there via `pr.yaml`'s `merge_group` trigger.
- **Never infer merged-ness from a check conclusion — in either direction.** Grouping is
  `HEADGREEN`, so a batch can merge on its LAST entry's checks and carry a red one in with
  it; and an entry that fails, or never reports inside 60 minutes, is dropped from the queue
  and its PR quietly returns to open with auto-merge off. Read
  `gh pr view <n> --json state,mergedAt,mergeStateStatus` and believe that instead.
- **Silence is not progress.** A dropped entry produces no failure event. If a PR has neither
  merged nor failed by your next check-in, assume ejection and re-enqueue.
- **One push can carry several PRs**, so the `release` run in Step 4 may be a batch head that
  contains your commit rather than a run named for it.

## Step 1: Compare implementation to plan

Re-read the plan and diff it against what was actually built. You're looking for
**interesting deviations** — anything a reviewer of the plan would be surprised by:

- Approach changed (different design, different files, different mechanism than planned)
- Scope changed (planned items dropped, unplanned behavior added)
- Blockers hit, or work that turned out uncompletable
- Public behavior differs from what the plan promised

NOT interesting (proceed without asking): mechanical differences — renames, small
refactors the code forced, fixing a typo the plan contained, test scaffolding the plan
didn't enumerate.

**If there are interesting deviations, blockers, or uncompletable work: STOP.** Report
them to the user and wait. Do not open a PR — the user decides whether the deviation is
acceptable. Autonomous integration is only for work that matches its plan.

If consistent: proceed. No need to ask.

## Step 2: Verify locally, then open the PR

1. Run the checks this box can run before pushing — at minimum `check` inside devenv
   (see AGENTS.md Testing for capability tiers). Don't outsource the first failure to CI.
2. Commit on the session's designated feature branch, push with `git push -u origin <branch>`.
3. Open a PR against `master` (`mcp__github__create_pull_request`). Its description becomes
   the squashed commit body verbatim, so write it as one: summarize the plan and note the
   implementation matches. A `+semver:` marker, if the change earns one, goes here on a line
   of its own — branch commit messages are discarded at merge and never reach master.
4. Enable auto-merge: `mcp__github__enable_pr_auto_merge` (squash). Under the queue this
   enqueues the PR when it is otherwise ready — see Merge semantics above. There is no
   manual-merge fallback: direct merges to `master` are blocked by the ruleset.
5. Subscribe to the PR: `subscribe_pr_activity`. Then end the turn — events wake the
   session; never poll with sleep.

## Step 3: Watch PR CI through to merge

The PR pipeline is `.github/workflows/pr.yaml`. **Read the tier out of that file rather than
from here** — it is one line in the workflow, it grows as capabilities become cheap enough to
gate every PR on, and a copy of it in this skill went stale within two plans. A local run at a
SMALLER tier than CI's is not "what CI runs": suites are gated on the capabilities they need,
so the ones CI declares and you did not are the ones you will not have run.

- **CI green + auto-merge fires → enqueued, then merged.** Go to Step 4.
- **Ejected from the queue →** not a CI failure and it raises no event. Diagnose the queue
  entry's own `test` run, fix if it was yours, and re-enqueue.
- **CI fails →** diagnose from the logs (`mcp__github__get_job_logs`), reproduce locally
  where the capability tiers allow, fix, and repeat this process from Step 1: the fix is
  itself a change, so re-compare it to the plan. A mechanical fix (lint, missed test
  update, flaky infra) is not an interesting deviation — push it and keep going. A fix
  that would change the approach or scope IS one — stop and report instead.
- Keep looping until merged. One fix round is not the task; each new failure gets the
  same treatment.

## Step 4: Watch the master pipeline

Merging is not the finish line — `.github/workflows/release.yml` runs the full `verify`
gate (plus Docker/LiveAgent tiers PRs can't run) on every master push, and that's what
publishes the release. Your merge can break master in ways PR CI couldn't see.

PR webhooks stop at merge, so watch master actively:

1. Find the `release` workflow run for the merge commit (`mcp__github__actions_list` /
   `actions_get`).
2. If it's still running, schedule a `send_later` check-in (~15–30 min) and end the turn;
   on wake, re-check and re-arm until it concludes. Never busy-wait.
3. **Success →** done. Unsubscribe from the PR if still subscribed, tell the user the
   change is merged and released.
4. **Failure →** treat it as yours until proven otherwise. Diagnose, fix on a fresh
   branch restarted from latest master, and repeat the whole contributing-changes
   process from Step 1 for the fix — same deviation gate applies: mechanical fixes flow
   through autonomously; anything needing an interesting deviation from the original
   plan stops and gets reported. If the failure demonstrably predates the merge
   (reproduces on the parent commit), report that to the user instead of blind-fixing.

## Definition of done

Merged to master — confirmed from the PR's merge state, not from a green check — AND the
master `release` pipeline is green for (or after) the merge commit. Anything short of that,
the loop is still running — either an event subscription or a scheduled check-in must be
armed before ending the turn.
