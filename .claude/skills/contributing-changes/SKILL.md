---
name: contributing-changes
description: How to integrate finished planned work into master — compare implementation to plan, open a PR with auto-merge, watch CI, and drive it to a merged, green state. Read this whenever you are completing a plan and the implementation is done: before committing, opening a PR, or declaring planned work finished. Also re-read it after any CI failure on a PR you opened, since fixing and re-integrating repeats this process.
---

# Contributing changes

This is the exit ramp for planned work. When the implementation is done, don't stop at
"changes pushed" — a change isn't contributed until it's merged to master and the master
pipeline is green. This skill defines the gate for proceeding autonomously and the loop
that drives a change all the way through.

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
3. Open a PR against `master` (`mcp__github__create_pull_request`). The body should
   summarize the plan and note that the implementation matches it.
4. Enable auto-merge: `mcp__github__enable_pr_auto_merge` (squash). If the repo refuses
   (auto-merge disabled or no branch protection), fall back to merging manually with
   `mcp__github__merge_pull_request` once PR CI is green.
5. Subscribe to the PR: `subscribe_pr_activity`. Then end the turn — events wake the
   session; never poll with sleep.

## Step 3: Watch PR CI through to merge

The PR pipeline is `.github/workflows/pr.yaml` (`check Browser Ports Native`).

- **CI green + auto-merge fires → merged.** Go to Step 4.
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

Merged to master AND the master `release` pipeline is green for (or after) the merge
commit. Anything short of that, the loop is still running — either an event subscription
or a scheduled check-in must be armed before ending the turn.
