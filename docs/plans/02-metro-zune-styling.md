# Step — Metro / Zune styling for the client shell

> Phase 4 · Client presentation
> Design context: [docs/design.md](../design.md) §1 (Reactive, Types first), §2.1

## Goal

Give the Browser Client a working-room UX with a classic **Metro / Zune** (pre‑Windows 8)
visual language. The **anatomy is Slack/Cursor** — a session sidebar, a conversation
column, queued messages stacked above a composer — because the user is *collaborating in
a session*, not browsing media. The **skin and motion are Zune**: elegant Segoe‑family
typography with deliberate weights and baselines, flat black surfaces, one vivid accent,
and fast directional motion.

Styling is authored **entirely in F#** by composing **Tailwind's own utility classes** into
typed, named values — **no CSS files and no hand‑written CSS.** Tailwind supplies the
utilities; F# supplies the composition. The pure `View` keeps emitting deterministic markup;
it just gains class names.

## Design north star

Zune / early Metro as a *skin discipline*, applied to a workspace shape people already know:

- **Content over chrome.** No borders‑as‑decoration, no bevels, no gradients, no shadows,
  no rounded corners. Structure comes from type, whitespace, and one hairline where a
  region genuinely ends.
- **Typography is the interface.** A thin lowercase wordmark; tiny wide‑tracked ALL‑CAPS
  labels for metadata (author, section, status); light 300 body for message text.
  Hierarchy by weight and size, never by fills. Never bold a heading — Metro headings get
  *bigger and lighter*, not heavier.
- **Authentically digital.** Zune‑black ground, flat colour, a single orange accent for
  *your* voice and focus; teal reserved for the agent's voice.
- **Motion is directional and fast.** New timeline/queue items arrive with a short
  translate‑up + fade (~180ms, sharp ease‑out); the composer focus is an accent underline
  that grows from the left; press states scale down slightly. No idle ambient motion.

### Theme tokens (registered once, from F#)

Registered in the Tailwind theme so semantic utilities resolve (`text-accent`,
`bg-surface`, `font-sans`); the config object is emitted from F#, not a CSS file.

| Token            | Value                          | Utility → role |
|------------------|--------------------------------|------|
| `bg` / `panel`   | `#000000` / `#0a0a0a`          | canvas / sidebar & strips |
| `surface`/`surface-2` | `#111111` / `#191919`     | cards, composer, hover lift |
| `hair`           | `#1f1f1f`                      | the only rule colour (region boundaries) |
| `ink`            | `#ffffff`                      | primary text |
| `ink-dim`        | `#b4b4b4`                      | secondary text |
| `ink-faint`      | `#6a6a6a`                      | labels, metadata |
| `accent`         | `#f09609` (Zune orange)        | your voice, focus, queue affordances |
| `agent`          | `#00b7c3` (teal)               | the agent's voice & activity |
| `ok` / `err`     | `#60d060` / `#ff4a4a`          | status pills / destructive hover only |

Font family (Tailwind `font-sans` override): `"Segoe UI", "Segoe UI Variable", "Segoe WP",
Frutiger, "Helvetica Neue", system-ui, sans-serif`. Mono (`font-mono` override) for command
lines: `"Cascadia Code", Consolas, monospace`. Weights: **200** wordmark/headings, **300**
body & message text, **400** default, **600** caps labels only.

### Type scale

- `wordmark` — `font-extralight text-4xl tracking-tight lowercase` → "yession." in the sidebar.
- `heading` — `font-extralight text-3xl lowercase` → the column header ("session").
- `body` — `font-light text-[0.9375rem] leading-relaxed` → message text.
- `label` — `font-semibold text-[0.65rem] tracking-[0.18em] uppercase text-ink-faint` →
  authors, section labels, offsets; tabular figures (`tabular-nums`) wherever digits align.

## Layout — a session workspace

```
┌ sidebar 280px ──┬ main ────────────────────────────────────────────┐
│ yession.        │ session                              UP TO DATE  │
│ session name    │ ┌ timeline (scrolls, pinned to bottom) ────────┐ │
│ ● connected     │ │  QUIET-HARBOR-42  14:02                       │ │
│ offsets · state │ │  message body …                               │ │
│ ────────────    │ │  AGENT  14:02                                 │ │
│ PEOPLE          │ │  reply … + inline command card                │ │
│  you · peers    │ └───────────────────────────────────────────────┘ │
│  agent          │ ▍agent is responding · turn a1f-9c   [Interrupt] │
│ ────────────    │ QUEUED · 2 — editable until the agent takes them │
│ ENVIRONMENT     │ ▸ you    | queued message …            ↑ ↓ ✕    │
│  running        │ ▸ peer   | queued message …            ↑ ↓ ✕    │
│ COMMANDS        │ ┌ composer ────────────────────────────────────┐ │
│  mise run test ✓│ │ Message the session…                  [Send] │ │
│  mise run build⟳│ └ accent underline grows on focus ─────────────┘ │
└─────────────────┴──────────────────────────────────────────────────┘
```

Section mapping (all eight existing `View` sections survive; `data-*` hooks unchanged):

- **Sidebar** (`section.connection` + `.offsets` + `.environment` + `.commands`):
  wordmark, session name, connection dot + offsets line (a product invariant, styled as a
  quiet tabular line — not hidden), presence list (you in orange, agent in teal),
  environment pill, and the command log as compact mono cards. Commands may *also* render
  inline in the timeline as Cursor‑style cards where they belong to an agent turn (the
  projection already interleaves by offset; presentation‑only choice).
- **Timeline** (`section.timeline`): the main scroll, pinned to bottom like every chat
  surface. Author caps‑label + time, light body text; streaming messages get a teal caret
  and dimmed body; arrival animation translate‑up + fade.
- **Agent activity strip** (`section.agent`): a slim bar between timeline and queue —
  pulse, "agent is responding", turn id, **Interrupt** on the right.
- **Queue** (`section.queue`): Cursor's queued‑messages pattern, stacked above the
  composer. Each row: author label, inline‑editable input, reorder/delete tools revealed
  on hover/focus. Left edge carries a 2px hairline that turns accent on hover — the one
  "border" in the design, and it encodes editability.
- **Composer** (`section.draft`): the primary draft as a flat surface with the growing
  accent underline on focus; Send in accent; other peers' in‑progress drafts shown as a
  "X is drafting…" ghost line beneath (their content is already synced state).

## Architecture — compose Tailwind utilities as typed F# values

New module `Yession.Client/Style.fs` (referenced before `View.fs`). It holds *named
compositions of Tailwind utility classes* — the values below **are** Tailwind classes; F#
just names, groups, and composes them so the view has no magic strings.

```fsharp
module Style =
    /// Join Tailwind utility groups into a class attribute value.
    let cls (groups: string list) : string = String.concat " " groups

    // Semantic groups — each is a string of real Tailwind utilities.
    let label    = "font-semibold text-[0.65rem] tracking-[0.18em] uppercase text-ink-faint"
    let body     = "font-light text-[0.9375rem] leading-relaxed text-ink"
    let sidebar  = "w-[280px] shrink-0 bg-panel border-r border-hair flex flex-col px-6 py-7 overflow-y-auto"
    let queueRow = "flex items-center gap-4 bg-surface px-4 py-2 border-l-2 border-hair hover:border-accent hover:bg-surface-2 transition"
    let arrive   = "animate-arrive motion-reduce:animate-none"   // registered keyframes, see headTags
    // …one value per role: wordmark / heading / timeline / msg / activity / composer / btn / pill.

    /// The <head> tags that deliver Tailwind + register the theme (colors, fonts, and the
    /// 'arrive'/'pulse' keyframes all live in this F#-emitted config object). No CSS file.
    let headTags = """
      <script src="https://cdn.tailwindcss.com"></script>
      <script>tailwind.config={theme:{extend:{
        colors:{bg:'#000',panel:'#0a0a0a',surface:'#111','surface-2':'#191919',hair:'#1f1f1f',
          ink:'#fff','ink-dim':'#b4b4b4','ink-faint':'#6a6a6a',accent:'#f09609',agent:'#00b7c3',
          ok:'#60d060',err:'#ff4a4a'},
        fontFamily:{sans:['Segoe UI','Segoe UI Variable','Segoe WP','Frutiger','Helvetica Neue','system-ui','sans-serif'],
          mono:['Cascadia Code','Consolas','monospace']},
        keyframes:{arrive:{from:{opacity:0,transform:'translateY(10px)'},to:{opacity:1,transform:'none'}},
          pulse2:{'0%,100%':{opacity:.25},'50%':{opacity:1}}},
        animation:{arrive:'arrive .18s cubic-bezier(.1,.9,.2,1)',pulse2:'pulse2 1.1s ease-in-out infinite'}
      }}}</script>"""
```

- **Compose, don't author.** Views write `cls [ Style.queueRow; Style.arrive ]`; each value
  is a bundle of Tailwind utilities. No `.css` file, no `@apply`, no `<style>` rules — only
  Tailwind's utilities and one theme‑config object, both emitted from F#.
- **Typed & deterministic.** The value set is fixed and named in F#, so a typo is a compile
  error and refactors are safe. `View.render` stays a pure total function that only emits
  *class names* — server bootstrap and browser render remain byte‑identical (design.md §1).
- **Tailwind delivery = the Play CDN (a script, not a stylesheet).** `cdn.tailwindcss.com`
  generates utilities at runtime and **watches the DOM**, so the `#app` innerHTML swap in
  `Browser.fs` gets styled automatically — no per‑render step. *Local‑first caveat:* the
  Play CDN is a network script; for a fully offline session, vendor that JS and serve it
  like `client.js` (a `/tailwind.js` route) — still a script, still no CSS file. Recommend
  Play CDN now, vendor‑JS when offline matters.
- Pseudo‑classes (`hover:`, `focus-within:`, `active:`), `motion-reduce:`, keyframes via
  the theme config, arbitrary values where the scale doesn't reach — all ordinary Tailwind,
  so the Metro motion stays in‑composition.

### Constraint to resolve: scroll state vs full re‑render

`Browser.fs` replaces `#app` innerHTML on every model change, which resets the timeline's
`scrollTop`. A chat surface needs the standard behaviour: **pinned to bottom while the user
is at (or near) the bottom; position preserved when they've scrolled up to read.** There is
a precedent to mirror: `focusedEditor` / `refocusEditor` snapshot and restore the focused
textarea across re‑render. Do the same for the timeline — before `setHtml`, record
`scrollTop` and whether it was at‑bottom; after, restore position or re‑pin (~8 lines in
`setState`). *Alternative* (larger): a persistent DOM shell with per‑section patching —
not needed yet; keep the view pure.

## File-by-file changes

1. **`src/Yession.Client/Style.fs`** *(new)* — the named Tailwind‑utility groups, `cls`,
   and `headTags`. Add to `Yession.Client.fsproj` **before** `View.fs`.
2. **`src/Yession.Client/View.fs`** — restructure `render` into the workspace layout
   (sidebar wrapper around connection/offsets/environment/commands; main column with
   timeline, activity strip, queue, composer) and add `cls [...]` classes throughout.
   Inject `Style.headTags` into `page`'s `<head>`. All `data-*` hooks stay untouched so
   `Browser.fs` delegation and the E2E selectors keep working.
3. **`app/browser/Browser.fs`** — timeline scroll preservation (pin‑to‑bottom / restore)
   around `setHtml` in `setState`, mirroring the focus‑preservation code.
4. *(No change)* `app/Signalling.fs` — still serves `View.page`; Tailwind rides in `<head>`.

## Verification (automated, per design.md §2.2)

- `mise run build` type‑checks the solution and Fable‑compiles the host — proves `Style.fs`
  and the reworked view compile and that class names stay pure/total.
- The existing WebRTC/UI E2E suite must stay green: keep all `data-*` attributes so
  selectors resolve. Add a UI‑checklist assertion that the served `/` head loads Tailwind
  and registers the theme, and that the workspace regions (sidebar, timeline, queue,
  composer) are present — a cheap regression that styling shipped.
- Browser E2E: assert the timeline stays pinned to bottom across a model update when at
  bottom, and preserves position when scrolled up (guards the re‑render fix).
- Visual truth is manual‑only by nature; keep it out of the gate but use the reference
  mock (companion file) as the design target.

## Decisions & alternatives

- **Slack/Cursor anatomy, Zune skin.** A panorama/pivot layout was considered and
  rejected: it is a media‑browsing metaphor and fights the job (watching a conversation,
  editing a queue, intervening fast). The session is one room; navigation is vertical
  time, not horizontal space. Zune survives in type, colour, and motion.
- **"No CSS" = no CSS files / no authored CSS — compose Tailwind's utilities instead.**
  Deliver Tailwind via the Play CDN (a script, not a stylesheet); compose its utilities as
  typed F# values. Keyframes/theme live in the F#-emitted config object. **Recommended.**
- **Play CDN now; vendor the Tailwind JS when offline matters.** Vendoring keeps
  local‑first intact and is still a script, not a CSS file. Rejected: the Tailwind *CLI
  build* and the *standalone* both emit a `.css` file — against the constraint.
- **Named utility groups in F#** over scattering raw class strings through the view:
  typed, refactorable, single source per role.
- **Pin‑to‑bottom scroll preservation** over a persistent‑shell refactor — smaller,
  mirrors the focus‑preservation precedent, keeps `View.render` a total function.

## Rollout

1. Land `Style.fs` (utility groups + `headTags`) with a couple of throwaway usages; `mise run build`.
2. Restructure `View.fs` into the workspace layout, section by section (sidebar → timeline
   → activity strip → queue → composer); keep `data-*` hooks.
3. Add timeline scroll preservation in `Browser.fs`.
4. Extend the UI checklist / browser E2E; `mise run test`.
5. Tune against the reference mock (weights, baselines, motion timing, accent discipline).
