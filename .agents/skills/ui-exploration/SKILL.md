---
name: ui-exploration
description: How to inspect and iterate on Yession's server-rendered UI (the manager page, or any surface a running bin serves) with a real browser — boot the app, take honest screenshots at desktop and true mobile viewports, visually read them, measure layout over CDP instead of guessing, and loop until clean. Read this before reviewing, restyling, or debugging any UI, and before trusting a headless-Chromium screenshot.
---

# UI exploration

Style code is not the UI. Reading `Style.fs` tells you what was *intended*; only a
rendered page tells you what a user gets. This skill is the loop: boot the real thing,
screenshot it honestly, look at the picture, measure what surprises you, fix, repeat.

## The one trap that poisons everything: fake mobile viewports

Headless Chromium **clamps its window to a ~500px minimum width**. `--window-size=390`
silently lays the page out at 500px and `--screenshot` crops the image to 390 — you get a
390px-wide PNG of a 500px layout. Overflow bugs hide, wrapping you'd get on a phone never
happens, and two rounds of "fixes" can chase a layout that was never real (this happened;
see the manager-page redesign).

Never use `--window-size`/`--screenshot` for anything narrower than ~600px. Use the
bundled `shot.mjs`, which drives CDP `Emulation.setDeviceMetricsOverride` (a true device
viewport) and prints the ground truth alongside the capture:

```
node .agents/skills/ui-exploration/shot.mjs <url> <width> <height> <out.png>
# -> {"vw":390,"docW":390,"overflowX":false}
```

Trust the shot only when `vw` equals the width you asked for. `overflowX: true` (or
`docW > vw`) means horizontal overflow a phone user cannot scroll away when the body is
`overflow-hidden` — that is a reachability bug, not a cosmetic one.

## The loop

1. **Boot the real app.** From the repo root, inside devenv (see AGENTS.md Bootstrap):
   `node app/out/Main.js --auth localhost` with `YESSION_DATA_DIR` pointed at a scratch
   directory. Run it in the background; wait with
   `until curl -sf -o /dev/null http://127.0.0.1:8321/; do sleep 1; done`.
   The manager UI is on 8321; each session child prints its own port.

2. **Seed realistic state.** An empty table hides most layout bugs. Create sessions with
   varied name lengths through the real endpoint:
   `curl -X POST http://127.0.0.1:8321/sessions -H 'content-type: application/x-www-form-urlencoded' --data 'name=design review'`
   Exercise every state the view can render (running, stopped; the row template branches
   on them).

3. **Shoot both anchors.** 1440×900 and 390×844 minimum. Widths between them only when a
   breakpoint sits there (`max-md` = 768px).

4. **Read the PNGs with the Read tool and actually look.** A checklist that catches real
   defects: Is anything clipped or off-canvas? Does hierarchy match importance (is the
   thing a human scans for in the biggest/brightest type)? Do left edges align on one
   rail? Are actions reachable and anchored? Is there an empty state? Compare against the
   design language: the rules live in the `Style.fs` header comment and
   `docs/plans/02-metro-zune-styling.md` (88px header band, statuses are text, buttons
   are bordered Metro rectangles, one gradient in the whole product).

5. **Measure, don't guess.** When a screenshot surprises you, do not theorize from CSS —
   ask the live page. `shot.mjs` accepts an optional fifth argument, a JS expression
   evaluated in the page after load (return a JSON string):

   ```
   node .agents/skills/ui-exploration/shot.mjs http://127.0.0.1:8321/ 390 844 /dev/null \
     "JSON.stringify([...document.querySelectorAll('td')].map(t => Math.round(t.getBoundingClientRect().width)))"
   ```

   Classic finding this catches: a flex child's `min-width:auto` tracking a table's
   min-content and pushing the whole page wider than the viewport (`min-w-0` fixes it).

6. **Fix, rebuild, reshoot.** Every edit needs `devenv shell -- build` (Fable recompiles
   the changed module AND Tailwind rescans the F# sources — a class name that never
   appears as a literal in `.fs` is never generated), then a manager restart
   (`pkill -f app/out/Main.js; pkill -f app/SessionMain.js` and boot again). Verify the
   compiled output actually changed (`grep` a new class in `app/ManagerUi.js`) before
   blaming the browser. Loop to step 3 until both anchors are clean.

## Before/after evidence

Shots of the old code must come from the old code — reconstructing them from memory or
skipping them misleads. `git stash` → build → boot → shoot → `git stash pop` → build.
Use identical seeded state and identical viewports for both sides, and take them with
the same tool (a clamped "before" against an emulated "after" is not a comparison).

## Contracts that must survive a restyle

- Tests pin behavior, not looks: the `data-*` hooks and status words in
  `src/Yession.App/Dom.fs` (`Dom.Manager`), and the fragment-swap protocol (the row is
  the poll/action swap unit; the `[data-sessions]` element is the create swap unit —
  whatever carries that attribute is what gets replaced wholesale).
- Run the suite before calling it done: `devenv shell -- check Ports Native` on this
  container (see AGENTS.md Testing).
- The page must remain self-contained: inline script, locally served `/app.css`, no CDN.

## Cleanup

Kill what you booted (`pkill -f app/out/Main.js; pkill -f app/SessionMain.js`) — an old
binary serving stale HTML on 8321 is the other classic source of "my fix did nothing".
