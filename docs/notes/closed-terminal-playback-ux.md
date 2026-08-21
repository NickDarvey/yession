# Closed-terminal playback — UX tryout notes (2026-08-21)

How this was exercised: real app booted in the dev container (`node app/out/Main.js --auth
localhost`, scratch `YESSION_DATA_DIR`, `YESSION_SANDBOX_NESTED=weak`), one session, one
terminal, three block commands (`ls -la`, a colored `printf`, `echo … && sleep 1 && echo …`),
terminal killed from the list. Then every playback path clicked through in headless Chromium
over CDP (real mouse/key events, focus emulation on), at 1440×900 and a true 390×844 viewport.
All positions/timers below were read from the live page, and the recording/keyframe bytes from
the transcript sidecars.

What was tried: chat chip → block tab → *Play recording* → *Back to output* → *Play whole
terminal* → *Back to blocks*; closed terminal's *↑ play the recording*; the terminal list's
closed-row verbs; the same flows on the phone viewport.

## Broken

1. **Every replay renders staircased garbage.** Block-mode output is recorded with bare `\n`
   (no `\r`): transcript records read `"total 4\ndrwxr-xr-x …"`, and the keyframe sidecar
   serializes the resulting screen with the staircase baked in
   (`\x1b[7C`/`\x1b[49C` line starts in `…keys.jsonl`). A VT — the player, and the Session
   Process emulator producing keyframes — moves down WITHOUT carriage return on LF, so every
   line starts where the previous one ended and wraps off the right edge. The text views split
   on `\n` and look right; every player view (block range, whole terminal, on both desktop and
   phone) shows the garble. Upstream fix is at transcript append (or pty-ize block exec);
   downstream, the emulator feeding keyframes has the same bytes and the same problem.

2. **A chat chip tapped while the pane shows the terminal list looks dead.**
   `OpenPaneTabMsg` (Model.fs:1440) sets `PaneChoice` but does not clear `TerminalList`, so the
   pane header retitles and the body keeps showing the list — no tab, no block, nothing
   apparent. `SelectFromListMsg` right below it clears the flag, and its comment states exactly
   why. Measured: chip click → `data-pane-panel` absent, list still mounted. This is the state
   the phone screenshot in the task was taken from (list face up), so it is a path a real
   reader actually hits.

3. **“Play whole terminal” does not land on the block.** Three desktop runs: playback starts
   at ~01:45 when the printf block's marker sits at 02:06, and the ~21s gap plays as dead air
   in real time. One phone run instead landed at/near the final frame. Never once on the
   block. Related observations from the same mounts: `idleTimeLimit 2` (Replay.fs) has no
   visible effect — the 0:23 and 1:44 idle gaps play out at 1×, total duration shows raw 02:07
   — and only 2 of the 3 block markers render (`.ap-marker` count; the `echo` marker at
   ~127.6s of a ~128.6s cast is missing). Suspect the raw-clock `startAt`/`markers` and
   `idleTimeLimit` disagree about whose timeline they are on; needs a look at how
   asciinema-player 3.x combines those options.

## Rough edges

4. **The first row of a chat group hides under its sticky author header.** The header
   (`sticky top-0 z-10 bg-bg pt-6 -mt-6`) paints over the top 16px of the first chip even with
   the scroller at `scrollTop 0` (measured: header box 112–160, chip 144–172;
   `elementFromPoint` at the chip's upper half returns the header, so those pixels also
   swallow the tap). Visible in every desktop screenshot as `$ ls -la` clipped behind
   WARM-IBEX. Phone overlaps by a few px (max-md paddings) — visually fine, tap target thinned.

5. **Focus strands on the two swaps that replace the pressed control with a different
   surface.** After *Play whole terminal* (block tab → terminal tab player) and after killing
   a terminal from the list, `document.activeElement` is `<body>`. The neighbours do it right:
   *Play recording*/*Back to output* relabel in place and keep focus; *Back to blocks* hands
   focus to *↑ play the recording*; a chip hands focus to the panel. UI-baseline rule: a swap
   that removes the focused element refocuses its replacement.

6. **“CLOSED — CLOSED BY A PEER” discards who.** `Commands.fs:55` hardcodes the reason string
   `"closed by a peer"`; the closing `peerId` is in scope and dropped, so no client can ever
   attribute the close. The attribution invariant (a thing that was done is attributed to
   whoever did it) says this should be a name.

7. **Sub-second commands make zero-length recordings.** A fast block's scrub bar reads
   `00:00-00:00` and play is a flash. Text-first design absorbs it, but the player timeline
   reads broken; consider not offering *Play recording* under some minimum recorded duration,
   or showing the range's wall-clock span.

## Worked well

- Closed terminal's read: full scrollback with colors, *↑ play the recording* at the top of
  history, *Back to blocks* floating in the reader's slot, closed band at the bottom.
- Chip → block tab → output-as-text-first, recording one press away; pin/preview semantics;
  the pane on the phone (column swap, actions reachable, focus into the panel).
- Player chrome: control bar, keyboard shortcuts, fullscreen; chapters concept (when the
  markers render).
