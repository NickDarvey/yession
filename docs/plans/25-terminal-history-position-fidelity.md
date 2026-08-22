# Plan 25 — Terminal history as position × fidelity

> **Status: proposed.** Grew out of the closed-terminal playback tryout
> ([notes](../notes/closed-terminal-playback-ux.md)), which found the pane's play state
> machine encoding two orthogonal axes — POSITION (which terminal, which command) and
> FIDELITY (text or playback) — as destinations the reader travels between, spread over
> four model fields each reducer half-clears, under vocabulary that names implementation
> nouns ("Back to blocks"). It also found the recordings themselves broken under any
> player. Three stages, each independently shippable as its own PR, ordered so each stands
> alone. The governing sentence, from the notes: **position is navigation, fidelity is a
> mode, and the mode never moves you.**

Every player-behavior claim below was verified against the bundled asciinema-player 3.17.0
source (`node_modules/asciinema-player/dist/core-BSn7GtYU.js`, cited by line) and against
the live app during the tryout. The pane state is pure in-memory view state — never
synced, never persisted (verified across `Serialization.fs`, `Sync.fs`, and every
`localStorage` write) — so the model redesign has **no migration surface**. Its
compatibility surface is exactly three things: the `PaneTab.key` string grammar
(`PaneShell.toChatItem` parses it back into a chat selector; `PaneReplays` keys its mount
dictionary on it), the `data-*` hooks in `Dom.fs`, and the three test files that pin them.

## Stage 1 — the recording is correct under any player

PR: `fix: record block output as a tty shows it, and put replay chapters on the player's
clock`. One commit body carries the patch marker on a line of its own (see Versioning).

### The two faults

**Captured bytes.** Block-mode commands on a terminal without an instrumented shell run
through pipes (`SessionProcess/Terminals.fs:1374-1400` → `Sandboxes.fs` `Spawn`,
stdout/stderr `data` handlers), so their output reaches the transcript with bare `\n` — no
tty driver ever applied ONLCR. The `"i"` command echo (`command + "\n"`,
`Terminals.fs:1315`) has the same shape on both paths. A VT — the player, and the Session
Process emulator that produces keyframes — moves DOWN without carriage return on LF, so
every replay staircases: each line starts where the previous ended and wraps off the right
edge. The keyframe sidecar has the staircase baked in (`\x1b[7C`/`\x1b[49C` line starts
observed in `…keys.jsonl`). The text views split on `\n` and look right, which is exactly
why this shipped: the cheap read hid the broken one. Instrumented-shell blocks and live
mode ride the pty and are already `\r\n`-clean.

**Player options.** The player idle-compresses EVENTS at load (`timeLimiter`,
core:296-338). `startAt` is given raw and converted correctly (`effectiveStartAt = startAt
− limiterOutput.offset`, core:330). But option-supplied `markers` are multiplexed in
AFTER compression, still on the raw clock (core:316-318) — one mismatch that produces
every symptom the tryout logged at once: the raw-timed markers become the last events, so
the displayed duration is the RAW duration; playback trudges through dead air in real time
to reach them; the step-out lands ~20s before its block; and the chapter UI drops the last
marker (`m[0] < duration`, strict, `asciinema-player-ui.js:3220`). Markers embedded as
standard asciicast `"m"` EVENTS ride the limiter and land compressed-correct — and
supplying the option STRIPS in-file `"m"` events, so it is one mechanism or the other,
never both. Poster: an `npt` poster feeds events while `t < target` (strict,
non-inclusive; `syncActiveSegmentToTime`, core:1643-1660) — so a beyond-duration poster
saturates to the final frame (no clamping math needed anywhere), but a poster at EXACTLY
the last event's time shows the frame *before* it: today's DVR pinned-edge poster is one
frame early, which the existing harness case cannot see because it asserts earlier text.

### The fix, at two points (per "Fixing bugs": root cause upstream, harden downstream)

**(a) ONLCR at capture.** A new pure function beside the transcript vocabulary it
protects, cheap-tier reachable:

```fsharp
// src/Yession.Domain/Transcript.fs
/// The tty driver's ONLCR, applied at CAPTURE for sources that never had a tty: a lone
/// LF becomes CRLF; an LF already preceded by CR — in this chunk or carried from the
/// previous one — is untouched, so pty bytes pass through unchanged.
module Onlcr =
    /// `endedWithCr`: whether the previous chunk of this stream ended in '\r'.
    /// Returns the normalized text and the carry for the next chunk.
    val normalize : endedWithCr: bool -> data: string -> string * bool
```

Applied in `emit` (`SessionProcess/Terminals.fs:866`) — the single choke point that both
appends to the transcript and feeds the emulator, so transcript, screen, and keyframe
sidecar agree by construction (the fold invariant pinned at `tests/Terminals.fs:329` holds
with no new test):

- `TranscriptOutput | TranscriptStderr` — normalize with a per-terminal carry
  (`LiveTerminal` gains `mutable OutputEndedCr : bool`, precedent `mutable Shell`); one
  flag for the interleaved o/e stream, which is the order `emit` sees it.
- `TranscriptInput` — normalize with carry `false` (the echo becomes `command + "\r\n"`,
  which is what a tty echoes on Enter); does not touch the o/e carry.
- `TranscriptResize` — untouched.
- Normalization runs BEFORE the retention `admit`, so the cap counts the bytes the
  transcript actually keeps.

The chunk-boundary carry is a deliberate one-byte cost: the transcript is the audit
artifact, and tolerating `\r\r\n` at a split would write a byte the terminal never
printed. NOT applied at cast build — the chunk↔disk byte-identity invariant
(`tests/Terminals.fs:1219-1250`, and the HTTP-cached immutable chunks it exists for) stays
untouched. **Old recordings stay staircased**: the store is append-only and is not
rewritten; said in the `emit` comment rather than discovered later.

**(b) Markers as cast events; poster epsilon.**

```fsharp
// src/Yession.Domain/Serialization.fs, module TranscriptReplay
/// Markers spliced in as standard asciicast "m" events, on the recording's own clock.
/// The player idle-compresses EVENTS at load; an option-supplied marker list stays on
/// the raw clock and lands in the dead air the compression removed. A marker sorts
/// before records at the same time.
val castWithMarkers :
    TranscriptHeader -> (int * TranscriptRecord) list -> markers: (float * string) list -> string
// cast header records = castWithMarkers header records []   — byte identity preserved.
```

The marker line is encoded locally (`[t, "m", label]`); `TranscriptKind` and the disk
format are untouched — the sidecar never carries an `"m"`, only the client-assembled cast
does. Consequences through the client:

- `PaneReplay` (Model.fs:263-278) loses its `Markers` field — embedding them AND listing
  them would be two mechanisms for one fact; the cheap tier asserts the cast text instead.
- `ClientModel.paneReplay`'s TerminalTab arm builds with `castWithMarkers`. Side benefit:
  a block completing changes the cast text, so `PaneReplays`' cast-keyed remount now picks
  up marker growth for free.
- `Replay.fs` drops the `markers` option (which would strip the events); `startAt` stays
  raw (the player converts it, core:330); `idleTimeLimit 2` stays and now actually
  governs playback.
- Both posters mean "the final frame" and stay raw `npt` with a `+ 0.001` epsilon so the
  strict-`<` feed includes the final event: the StretchTab still (Model.fs:857) and the
  TerminalTab pinned edge (Model.fs:896).
- Documented residuals, in the code where they bite: a marker at exactly the cast's LAST
  event time is still dropped from the chapter list by the UI's strict filter (a final
  block that printed nothing) — the event stays in the cast; and the control-bar timer
  shows the raw poster time until first interaction (core:1662).

### Tests

- Cheap, new: an `Onlcr` list — lone LF→CRLF; CRLF idempotent; boundary `"a\r"` then
  `"\nb"` yields no doubled CR; lone `\r` untouched; `"\n\n"`; empty; carry round-trips.
- Cheap, new: "pipe-captured output is recorded as a tty would have shown it" — the
  scripted environment prints bare `\n`, the transcript holds `\r\n`.
- Cheap, updated: the pinned byte expectations move to `\r\n`
  (`tests/Terminals.fs:1650-1652`, `PtyIntegration.fs:274-280`); `Timeline.fs:576` asserts
  the `"m"` lines in the cast text (one per block, at the block's time, before same-time
  records); `:610` expects the poster epsilon; `Markers`-field assertions go with the field.
- Browser, new — the tripwire: a second harness mount over an idle-gappy cast (events at
  0.0/0.1, then 30.0; a marker at 30.0; `StartAt = Some 30.0`) asserting BOTH markers
  render and the post-gap text appears within a short explicit timeout (≤10s — the raw
  clock could only show it after ~28s of dead air). Red today; also the alarm if a player
  upgrade re-opens the option semantics.
- Browser, updated: the DVR case can now assert the pin frame itself — true for the first
  time with the epsilon.

## Stage 2 — one PaneMode

PR: `fix: the pane's playback state is one mode, not four fields agreeing`. Patch marker
in a commit body (bug fixes; no new capability). Behavior-preserving except the four state
bugs — no hook, label, or key changes in this PR, so **the Browser tier must pass
untouched: that is the gate.**

### The type

```fsharp
// src/Yession.App/Model.fs, beside PaneTab
/// Which read of a tab the pane shows: the reader's POSITION (which tab, and — for a
/// terminal — where in its history) and FIDELITY (text or playback) as one fact.
/// Rules that are NOT reader choice stay derived: a stretch always plays, and a closed
/// terminal whose recording is its only read plays without appearing here
/// (`playsRecording` is where choice and rule meet, unchanged).
type TabMode =
    /// A tab's text read.
    | Reading of PaneTab
    /// A tab's playback read, chosen by the reader.
    | Watching of PaneTab
    /// A terminal's playback entered FROM one of its blocks: starts at that command.
    /// The block's IDENTITY, not a transcript line — the seq and its time are derived
    /// reads (`paneReplay`), so the hint cannot go stale against the projection.
    | WatchingFrom of TerminalId * BlockId
    /// A LIVE terminal watched behind its edge — the DVR — with the transcript length
    /// the rewind pinned. Exists ONLY on a whole-terminal watch, by construction.
    | WatchingBehind of TerminalId * pin: int

/// The pane's one face.
type PaneMode =
    | OnTab of TabMode
    /// The census — a DESTINATION, never a mask. It remembers the tab-mode it covered,
    /// so glancing at the list and toggling back resumes the read (a DVR pin included) —
    /// the one thing the old boolean's masking did right, kept on purpose.
    | OnList of resume: TabMode option

// ClientModel:
//   Pane : PaneMode option      // None = nothing chosen yet; selectedPane resolves the
//                               // default exactly as today.
// Pins and TerminalsOpen unchanged. PaneChoice, PanePlaying, PaneRewound, TerminalList: deleted.
```

Alternatives rejected, and why:

- The notes' sketch `Watching of {Tab; StartLine option; Pin option}` — two options make
  four combinations, two of them designed; a pin beside a `BlockTab` is representable
  nonsense. The case split makes pin-only-on-whole-terminal structural.
- Two model fields `Position × Fidelity` — the axes made literal, but it re-creates
  "fields agreeing": a fidelity flip has to remember to drop a pin, which is exactly the
  partial-clear disease this plan exists to kill.
- `OnList` without `resume` — kills the mask but also kills "glance at the list, come back
  to my rewind"; a regression nobody asked for.
- Storing "plays" as a flag per tab — duplicates the derived rules (stretch-always,
  `ReplayIsTheRead`); two sources disagreeing, in new clothes.

`WatchingBehind` deliberately survives its terminal closing (the mode is the reader's
ask); the resolved read `rewoundTo` keeps its `IsOpen` filter, so a close under a rewound
reader degrades to the whole recording exactly as today (`Timeline.fs:863` stays green).

### Messages

One new message states the whole next mode; six collapse into it:

| gone | becomes |
|---|---|
| `SelectTerminalMsg t` | `ShowInPaneMsg (Reading (TerminalTab t))` |
| `SelectPaneTabMsg tab` | `ShowInPaneMsg (Reading tab)` |
| `OpenPaneTabMsg tab` | `ShowInPaneMsg (Reading tab)` — preview semantics unchanged |
| `SelectFromListMsg t` | `ShowInPaneMsg (Reading (TerminalTab t))` — leaves the list because the WHOLE mode is stated, not because a flag was remembered |
| `PlayRecordingMsg (tab, None)` | `ShowInPaneMsg (Watching tab)` |
| `PlayRecordingMsg (TerminalTab t, Some seq)` | `ShowInPaneMsg (WatchingFrom (t, blockId))` |
| `JumpToLiveMsg t` | `ShowInPaneMsg (Reading (TerminalTab t))` — it was always the same act |

Kept: `RewindTerminalMsg of TerminalId` (its reducer pins `KnownLength` — the rule lives
with the state, per Colocation), `ToggleTerminalListMsg`, `TogglePinMsg`,
`ToggleTerminalsMsg`. The transition table, total (every row writes the whole `Pane`; all
set `TerminalsOpen = true`):

| message | next `Pane` |
|---|---|
| `ShowInPaneMsg m` | `Some (OnTab m)` |
| `RewindTerminalMsg t` | `Some (OnTab (WatchingBehind (t, feed.KnownLength)))` |
| `ToggleTerminalListMsg` from `OnTab m` / `None` | `Some (OnList (Some m))` / `Some (OnList None)` |
| `ToggleTerminalListMsg` from `OnList resume` | `resume` restored as `OnTab`, else `None` |

The four state bugs die by construction: a chip over the list face — `ShowInPaneMsg`
yields `OnTab`, the list cannot survive it; the list rewind — its click handler
(`View.fs:2048-2051`) becomes the single `RewindTerminalMsg`, no second message to cancel
it; `PlayRecordingMsg` clearing nothing — every entry states the whole mode; the mask —
`TerminalList : bool` no longer exists, the render switches on the mode.

### Derived reads

`TabMode.tab : TabMode -> PaneTab` projects every case to its tab (the three terminal
cases to `TerminalTab t`). Then: `showsList` = `Pane` is `OnList`; `selectedPane` keeps its
fallback chain, resolving `OnTab` from the mode's tab and `OnList` from its resume;
`rewoundTo` matches `WatchingBehind` then applies the existing `IsOpen` filter;
`playsRecording`'s "chosen" arm becomes "the mode is a `Watching*` whose tab keys equal
this tab's" — the stretch-always and `ReplayIsTheRead` arms untouched; `paneReplay`'s
`StartAt` reads `WatchingBehind` → pinned edge, `WatchingFrom (t, b)` → the block's
`FromSeq` through the projection → `timeOf`, else `None`. `paneTabs`, `missingKeyframe`,
`playable`: unchanged formulas. `PaneReplays.fs`'s catch-up dispatch becomes
`ShowInPaneMsg (Reading (TerminalTab t))`. `PaneTab.key`, `PaneShell.toChatItem`, the
replay dictionary: untouched.

### Tests

- Mechanical updates across the pinned lists: `Timeline.fs` 344-415, 575-710 (the step-out
  case `:587` constructs `WatchingFrom (t, blockId)` and keeps asserting the landing time;
  `:601` still asserts the hint dies when anything else is chosen), 724-800, 804-945
  (`:905` "choosing anything else ends the rewind" now holds by construction and stays as
  the pin), 1061-1125 (`:1101` asserts `showsList = false`), 1127-1220; `Acceptance.fs`
  fixtures `:153-159` (`Pane = None`), `:262`, and the list renders (`Pane = Some (OnList
  None)`).
- New invariants, cheap tier: a chip tapped over the list face shows the block (bug 2's
  tombstone); the list's rewind is ONE message and watches behind live; entering any read
  replaces the whole mode (a rewind on A dies when B is watched); leaving the list resumes
  the read it covered, pin included.
- Known pre-existing gap, noted in `PaneReplays`' module comment rather than fixed here:
  a `StartAt`-only change over an unchanged cast does not remount.

## Stage 3 — the reader's verbs

PR: `feat: watch or read anywhere, and show a command in its terminal`. Minor marker in a
commit body (user-facing capability).

### The vocabulary

The notes name three reader intents; the controls follow them. POSITION verbs: a chat chip
(this command's output), **Show in terminal** (this command in its surroundings — the
terminal's TEXT scrollback, scrolled to and briefly highlighting the block), a list row
(this terminal). ONE FIDELITY toggle everywhere: **Watch / Show output** — on a live
terminal **Watch** is the rewind and the way back is **Live**. Toggling fidelity never
changes position, so the step-out focus strand disappears structurally. Retired labels:
*Play recording*, *Back to output*, *↑ play the recording*, *↑ replay from the start*,
*Back to blocks*, *Play whole terminal*, *Jump to live*.

### Model

```fsharp
// TabMode gains the anchor case:
| ReadingAt of TerminalId * BlockId   // the terminal's TEXT scrollback, positioned at
                                      // (and highlighting) this block — "show in terminal"

module TabMode =
    /// The other fidelity at the SAME position — what the one toggle dispatches. Total;
    /// positions with only one read (a stretch; ReplayIsTheRead) never render the
    /// toggle, so those rows are unreachable but still stated.
    let toggled : TabMode -> TabMode
    // Reading t          <-> Watching t
    // ReadingAt (t, b)   <-> WatchingFrom (t, b)     — the anchor survives the flip
    // WatchingBehind t _ --> Reading (TerminalTab t) — "Live"; the pin dies with the watch
```

"Play whole terminal"'s intent is now *Show in terminal → Watch*: `ReadingAt` toggles to
`WatchingFrom` and the anchor carries the start — no payload rides a message, and the way
back is the same toggle, which never moved the reader.

### The reveal (new mechanism — none exists today)

`ViewActions` gains `RevealBlock : TerminalId -> BlockId -> unit`;
`PaneShell.revealBlock` implements it (no-op in SSR/tests): in the same rAF shape as
`toPane` — model first, Lit renders, then
`scrollIntoView` on `[data-terminal-scrollback][data-terminal-id=…] [data-terminal-block=…]`
plus a transient highlight class (one `tailwind.css` utility + `Style.fs` token; a brief
pulse on theme tokens). It runs AFTER the render's `restoreSurfaceScroll`
(`Browser.fs:1582`), so the one-shot reveal wins over the fresh-surface bottom-pin default,
and later renders re-sample the revealed position as an ordinary reading position — the
pin restore and the reveal never fight.

### Controls, per surface

- **Chat chip** — unchanged: `ShowInPaneMsg (Reading blockTab)` + `FocusPane`.
- **Block tab** — *Watch / Show output*: one relabeling button (hook `data-pane-watch`,
  value = the face it will show, the `terminalListToggle` contract), dispatching
  `ShowInPaneMsg (TabMode.toggled current)`; focus stays because the control does. *Show
  in terminal*: hook `data-pane-show-in-terminal`, dispatch `ShowInPaneMsg (ReadingAt (t,
  b))` + `RevealBlock` + `FocusPane` (the pressed control leaves with the tab — the same
  hand-off a chip makes). Offered wherever the terminal has a scrollback read — including
  OPEN block-mode terminals, a deliberate widening over today's closed-only step-out,
  because the context intent is as real there.
- **Terminal panel** — the four ways in and out collapse to ONE bar toggle that never
  leaves the document (beside `take`, where live-mode rewind already sits): hook
  `data-terminal-watch`, value = face — live+recorded → *Watch* (`RewindTerminalMsg`);
  `WatchingBehind` → *Live* (`ShowInPaneMsg (Reading …)`); closed+playable+has blocks →
  *Watch* / *Show output* (`ShowInPaneMsg` of the toggle); `ReplayIsTheRead` and stretches
  render no toggle — only one read exists. Every manual swap keeps focus because the
  control persists. Deleted: both `↑` entries, the *Back to blocks* float, the *Jump to
  live* button (the `data-terminal-behind` label stays).
- **Focus plumbing** — `toDvrControl`'s one-of-four selector collapses to
  `[data-terminal-watch=…]`; rename `FocusDvr → FocusWatch` (same stranded-focus guard —
  it still serves the automatic catch-up, where the player unmounts under the reader).
- **List** — row unchanged (`Reading`); the rewind verb keeps its hook and dispatches the
  single `RewindTerminalMsg`; aria-label moves to the Watch vocabulary.

`Dom.fs`: `panePlay → paneWatch`, `panePlayWhole → paneShowInTerminal`,
`terminalPlay`/`terminalBlocks`/`terminalRewind`/`terminalLive` → one `terminalWatch`;
`terminalBehind`, `terminalListRewind`, `paneReplay` stay.

### Tests

- Cheap, new: `toggled` is total and keeps its position (`ReadingAt ↔ WatchingFrom`
  round-trips with the anchor intact; `WatchingBehind → Reading` drops the pin); the
  anchor feeds `paneReplay.StartAt` across a fidelity flip.
- Cheap, updated: renamed hooks through `Acceptance.fs:622-643` and any Timeline render
  assertions.
- Browser, updated: the chip case swaps `data-pane-play` for `data-pane-watch`; the DVR
  case moves to the bar toggle and its stranded-focus assertions become "focus never
  moved" for manual swaps (the automatic catch-up still asserts the stranded hand-off).
- Browser, new: *show in terminal scrolls the terminal's history to the block, without
  stranding focus* — the block's top sits inside the scroller's viewport (a bounding-rect
  check: a position promise, not a style assertion), `activeElement` is the panel. The
  harness terminal needs enough records to overflow, so the scroll is observable.
- Not tested: the highlight's look (a screenshot question), label copy (not a promise).

### Open questions, decided here so the implementation never re-derives them

- Moving the terminal's watch entry from the top-of-scrollback `↑` slot to the bar trades
  that slot's discoverability for one persistent control that keeps focus. Decided: the
  bar — two controls for one act is the spare CLAUDE.md forbids. If it tests badly in the
  hand, the bar stays the mechanism and a passive "history starts here" line may return as
  furniture, never as a second control.
- *Show in terminal* on open terminals is a behavior widening, flagged above; gate it to
  closed terminals only if strict parity is wanted for the first cut.
- A reveal racing a streaming record on the same surface can lose to the bottom re-pin;
  rare on the surfaces that offer the verb, accepted, and the Browser case catches a
  systemic regression.

## Protocol and versioning

Nothing here touches the Manager ↔ Session contract: **no major bump**. Stage 1 and stage
2 are fixes and carry a `+semver: patch` marker on a line of its own in a commit body on
the branch; stage 3 lands user-facing capability and carries `+semver: minor` the same way
(markers in COMMIT bodies, never only the PR description, which squash discards).

## Out of scope

Already in the notes, each a separate small change: the sticky author header clipping a
group's first chip; "closed — closed by a peer" discarding the closer's identity
(`Commands.fs:55`); the zero-length scrub bar on sub-second commands; stderr (`"e"`)
events never rendered by the player's poster/seek feed (upstream player behavior).
