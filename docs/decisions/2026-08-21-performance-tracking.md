# Measure what a person waits for; fail only on a big regression

> Decided 2026-08-21 · Supersedes nothing · Related:
> [tasks.fsx](../../tasks.fsx) `bench` / `bench-guard` / `bench-publish`,
> [tests/Yession.Tests/Bench.fs](../../tests/Yession.Tests/Bench.fs),
> [app/browser/EditorHarness.fs](../../app/browser/EditorHarness.fs) — the two-peer surface,
> [.github/workflows/bench.yml](../../.github/workflows/bench.yml)

## Decision

Four numbers, swept across three document sizes, measured in a real browser on the two-peer
harness, recorded on an orphan `bench` branch and charted onto every release:

| Metric | The question a person is asking |
|---|---|
| `type` | Does typing feel instant? |
| `receive` | How fast do I see my collaborator? |
| `caret.push` | What does drawing their caret cost? |
| `caret.paint` | Does their cursor track? |

Plus one derived: **`caret.push.slope`** = cost(20k chars) ÷ cost(200 chars).

A **pull request** fails when a p50 exceeds `max(3× baseline, baseline + 2ms)` — or 2× for the
slope — confirmed by a second measurement. A **release** records and charts and never blocks.

## Why these numbers

A collaborative editor's user experience is three latencies and one budget. Everything else —
throughput, allocation counts, bundle size — is a proxy for something a person does not
directly feel.

The **size sweep is the whole design**. This came out of #214, whose open question was that
y-prosemirror reconciles the entire document back into Yjs on every caret push (`view.update`
is not gated on `docChanged`), and frame pacing made that happen more often. That is a
COMPLEXITY worry, and a measurement at one document size cannot show a complexity regression at
all. The slope is what turns four latencies into an answer.

**It answered the question immediately, and not the way the worry assumed.** At 200 / 2,000 /
20,000 characters the caret push costs 0.30 / 0.30 / 0.40ms — a slope of about 1.3, not 100.
y-prosemirror's `updateYFragment` identity-matches unchanged subtrees through its mapping, so a
hundredfold larger document costs a third more, not a hundred times more. The cost is real,
sublinear, and about 2% of a frame. Worth watching; not worth the redesign it nearly got.

**Not the Event Timing API.** `PerformanceEventTiming.duration` is rounded to 8ms for privacy —
a fine threshold for judging one interaction, useless for watching a trend. `keydown.timeStamp`
shares `performance.now()`'s time origin and its resolution, and includes the browser's own
dispatch.

## Why a big regression fails and a small one does not

A millisecond budget on a shared CI runner is the flaky test this repository warns about: it
goes red for reasons nobody changed, everyone learns to re-run it, and it stops being read. A
3× one is far outside anything scheduling noise produces.

Four rules keep it honest:

- **The baseline is the median of the last ten recorded points**, not a number in a file.
  Median so one noisy release cannot move it; recorded so it tracks reality. Under three points
  it records only — a guard with nothing to compare against must not invent something.
- **p50, not p95.** The tail is where a runner's noise lives. p95 is charted, not enforced.
- **An additive floor**, because 3× of a sub-millisecond metric is noise. 2ms, calibrated
  against measured jitter (`caret.push` varies about 0.1ms between runs). It was 8ms first —
  half a frame, on the reasoning that below that nobody can tell — which made every metric here
  untrippable, because they are all smaller than that. A floor above the thing it protects is
  an off switch.
- **Re-measure once before failing.** The dominant flake mode is a single unlucky sample, and
  the scenario costs seconds.

## Why the pull request fails and the release does not

A regression should be caught by the person who caused it, on their own pull request, where a
red is cheap and actionable and the diff that caused it is right there. A red master stops
everybody else releasing, and a benchmark is never a good enough reason to hold delivery.
Anything that got past the pull-request guard is a chart to read, not a release to block.

Mechanically: `continue-on-error` cannot go on a job that `uses:` a reusable workflow, so the
tolerance lives on the steps inside `bench.yml`, keyed off whether anything is being guarded.

## Why the history is an orphan branch

It has to live somewhere every run can read and one run per release can append to. A file on
master would be a commit everybody rebases past and — worse — a push to master, which triggers
`release.yml` (`branches: [master]`) and releases again. An orphan branch shares no history with
master, triggers nothing, and keeps a full git log of every measurement ever taken.

The recorded shape is **github-action-benchmark's `customSmallerIsBetter`** array, so adopting
that tool later is a drop-in rather than a migration. The chart is hand-authored SVG in
`tasks.fsx`: no new dependency, and a release asset has to read on GitHub in both themes, which
one `prefers-color-scheme` block handles and no chart library reliably does.

## What would change this

- **The floor stops fitting.** These metrics are 0.3–17ms today. If the product's numbers move
  an order of magnitude, 2ms is either noise or an off switch again — recalibrate against
  measured jitter, not against intuition.
- **A metric stops being able to move.** `caret.paint` is pinned at one frame, which is the
  good news and also means it only ever reports a regression, never an improvement. If pacing
  changes, check it can still say something.
- **The guard goes red for reasons nobody changed.** Then the thresholds are inside the noise
  floor of whatever runner CI is using now, and the answer is a wider threshold or a quieter
  runner — never a re-run habit.
- **Somebody wants an interactive chart.** The stored shape is already
  github-action-benchmark's; that is the door, and it was left open deliberately.
