# Plan 25 — Manager & session UX fixes

Follow-up to `docs/ux-review-2026-08-21.md`, scoped by review: P0, P1 (#2, #3 — naming cut
from the manager side), P2 (#6, #7, #9). Items #4 (port URLs), #5 (identity), #8 (terminal
start feedback) are explicitly out of scope. Each step below is independently shippable —
its own PR, in order, per `.agents/skills/contributing-changes/SKILL.md`.

## P0 investigated: why created sessions vanish (and why it doesn't repro interactively)

`ProcessManager.createSession` (`app/ProcessManager.fs:887`) writes the registry
(`ManagerStore.save`, `state <- next`) and republishes MCP sets — but never calls
`publishSessions ()`. Every other registry write does (`setDisplayName`, `archive`,
`unarchive`), as do launch and exit. The `sessions` RetainedHub therefore retains a
**pre-create** list until the next launch/exit/rename.

Consequences, confirmed against a live manager:

- `GET /` (SSR) and the `POST /sessions` answer render from `pm.Sessions ()` = `viewsNow ()`
  — both include the new session.
- `/sessions/rows` (EventSource) hands every **new subscriber** the retained snapshot —
  which omits it. So a page opened (or reconnecting) after a create and before any other
  lifecycle event swaps the row away moments after SSR painted it; the session is then
  unlaunchable and unarchivable from that page.

Why it "works for me": the creator's own page swaps the table from the POST answer and no
SSE frame arrives to overwrite it (nothing published); the immediate launch that follows
publishes a list that includes the session. The bug needs a page load / stream reconnect in
the create→launch gap. The review's walkthrough opened a fresh browser per step, so it hit
that window on every load.

Retraction while here: review item #10 ("active chip links to `?show=none`") is not a bug —
chip hrefs are *toggle targets* (`SessionQuery.toggling`), deliberate and consistent.

## Step 1 — publish on every registry write (P0)

- **Root cause, upstream:** extract one commit verb inside `ProcessManager`:
  `commit next = ManagerStore.save statePath next; state <- next; publishSessions ()`, and
  route `createSession`, `setDisplayName`, `archive`, `unarchive` through it. That is the
  colocation rule (a caller that could write without publishing is the bug this was):
  durable-before-visible and visible stay one verb, and the next registry write cannot
  forget. `createSession` keeps its `publishMcpServers ()` after the commit.
- **Test (cheap tier, beside the existing ProcessManager suites):** two cases —
  a sink registered *before* `CreateSession` receives a list containing the new record
  (status `NotRunning`); a sink registered *after* is current at once. Regress by removing
  the publish and watch both go red.
- No second delivery mechanism (e.g. recomputing `viewsNow` at subscribe): that is
  belt-and-braces — it would hide exactly this class of missed publish behind a
  fresh-looking first frame.

## Step 2 — create launches and opens (#2)

The primary flow should be one act: create → working session.

- `POST /sessions` (browser form path) answers `303 See Other` →
  `/sessions/{id}/open` — the existing stable route (`ManagerUi.fs:777`) that launches a
  stopped session and serves the opening page. Make the create form a real form submit
  (drop its fetch/swap handler); other open pages learn about the new session from the rows
  stream, which Step 1 makes correct.
- Callers that POST with an explicit `id` (automation, tests) keep today's fragment answer
  if the 303 breaks them — decide at implementation from actual usages; prefer one
  behaviour if nothing depends on the fragment.
- Browser-tier check: the manager-page suite's create case follows the redirect and lands
  on a running session. The Launch button stays for stopped/archived rows — unchanged.

## Step 3 — naming moves into the session (#3, manager side cut)

- Remove the name input from the manager's create form (form becomes the CREATE button);
  `POST /sessions` stops reading `name`. `DisplayName` already defaults to the minted id.
- The session's own title input remains the one naming surface; it already reports back to
  the Manager (`setDisplayName` via the control channel), so the manager list shows the
  reported title — the id until somebody names it.
- Fold in the tab-identity half of #3: `document.title` follows the session title
  (`"<title> — yession"`, id-fallback) wherever the title state updates in
  `src/Yession.App`. The manager page keeps its own title.

## Step 4 — chat empty state and composer affordance (#7, absorbs #13)

- Empty feed renders a quiet empty-state line (e.g. "nothing said yet — write below")
  instead of a black expanse; disappears with the first event.
- Composer gets placeholder text (ProseMirror decoration or
  `.ProseMirror:empty::before` CSS) so the input reads as an input, especially at 390px.
- Set `white-space: pre-wrap` on the editor root — clears the ProseMirror console warning
  (#13) and the multi-space rendering risk in the same file.
- Verification by screenshot (ui-exploration), both anchors. No new UI test: an empty-state
  element and placeholder copy are design, not invariants.

## Step 5 — first message clipped on desktop (#6)

- Reproduce with the ui-exploration driver at 1440×900 (clip is absent at 390×844),
  measure the overlapping boxes over CDP — suspects are the log container
  (`Style.fs:698`) top padding vs the header, and first-child margin behaviour under
  scroll-to-bottom.
- Fix the layout cause, reshoot both anchors, and re-check with a seeded multi-message
  feed so the fix isn't an artifact of the one-message case.

## Step 6 — 401 noise at load (#9)

- Every session page load logs `401 /queries` + `401 /me` before the handshake, then
  succeeds. Root-cause the ordering in `src/Yession.App` (App.fs boot sequence vs
  `SessionAuth`): either these fetches wait for the auth state they depend on, or the
  probe endpoint answers 200 with "anonymous" when that is an expected first answer.
- Fix upstream (sequence or contract), not by swallowing errors: a fetch that can 401 for
  a *real* reason must keep saying so. Acceptance: a clean console on a normal load.

## Versioning

Bug-fix steps (1, 5, 6) carry `+semver: fix` on a commit body line; steps 2–4 change
user-facing behaviour of the create flow and session surface — `+semver: minor` on the
step-2 branch, the rest ride along as fixes.
