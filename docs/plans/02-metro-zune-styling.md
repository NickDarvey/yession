# Step — Metro / Zune styling for the client shell

> **Status: delivered.** (`bd15c5d`, #2; carried through the Fable.Lit rewrite `4814815`, #4)
>
> Phase 4 · Client presentation
> Design context: [docs/design.md](../design.md) §1 (Reactive, Types first), §2.1

## Goal

Give the Browser Client a working-room UX with a classic **Metro / Zune** (pre‑Windows 8)
visual language. The **anatomy is Slack/Cursor** — a session sidebar, a conversation
column, queued messages stacked above a composer — because the user is *collaborating in
a session*, not browsing media. The **skin and motion are Zune**: elegant Segoe‑family
typography with deliberate weights and baselines, flat black surfaces, a blue/green technocool accent pair,
and fast directional motion.

Styling is authored **entirely in F#** by composing **Tailwind's own utility classes** into
typed, named values — **no CSS files and no hand‑written CSS.** Tailwind supplies the
utilities; F# supplies the composition. The pure `View` keeps emitting deterministic markup;
it just gains class names.

## Design north star

Zune / early Metro as a *skin discipline*, applied to a workspace shape people already know:

- **Content over chrome.** No borders‑as‑decoration, no bevels, no shadows, no rounded
  corners. Structure comes from type, whitespace, and one hairline where a region
  genuinely ends.
- **Typography is the interface, on a real grid.** A thin lowercase wordmark; tiny
  wide‑tracked ALL‑CAPS labels for metadata; light 300 body. Hierarchy by weight and
  size, never by fills. Never bold a heading. **Everything sits on a 4px baseline
  rhythm** — type scale `11/16 · 13/16 · 15/24 · 28/32 · 32/36` (size/line‑height, px),
  all vertical paddings and gaps multiples of 4. The sidebar wordmark and the main
  header share one 88px header band (flex‑end, common bottom padding) so their
  baselines align across the hairline.
- **Affordance is unambiguous.** *Statuses are text*: colored caps with at most a small
  dot — never filled, never boxed. *Buttons are bordered Metro rectangles*: transparent,
  hover brightens the border, press fills solid. Nothing else carries a border.
- **Technocool colour.** Zune‑black ground; **blue** (`#1ba1e2`) is interactive and the
  agent's voice; **green** (`#a8dd00`) is live/ok and the human pulse. People are
  identified by **tiny square display pics** (never round; generated two‑tone
  blue/green‑family checkers until real avatars exist), not by name colours. The
  **blue→green gradient appears exactly once** — the composer's left focus edge —
  Zune's orange→pink signature, recast.
- **Motion is directional and fast.** New timeline/queue items arrive with a short
  translate‑up + fade (~180ms, sharp ease‑out); the composer focus edge grows from
  the top; press states fill. No idle ambient motion; `motion-reduce:` respected.

### Theme tokens (registered once, from F#)

Registered in the Tailwind theme so semantic utilities resolve (`text-blue`,
`bg-surface`, `font-sans`); the config object is emitted from F#, not a CSS file.

| Token            | Value                          | Utility → role |
|------------------|--------------------------------|------|
| `bg` / `panel`   | `#000000` / `#0a0a0a`          | canvas / sidebar & strips |
| `surface`/`surface-2` | `#111111` / `#191919`     | cards, composer, hover lift |
| `hair`           | `#1f1f1f`                      | the only rule colour (region boundaries) |
| `ink`            | `#ffffff`                      | primary text |
| `ink-dim`        | `#b4b4b4`                      | secondary text |
| `ink-faint`      | `#6a6a6a`                      | labels, metadata |
| `blue`           | `#1ba1e2` (Metro cyan)         | interactive: buttons, focus rings, agent voice, streaming caret |
| `green`          | `#a8dd00` (Zune lime)          | live/ok statuses, queue editability edge, wordmark tick |
| `err`            | `#ff4a4a`                      | failed statuses, destructive hover only |
| *(gradient)*     | `blue → green`, 180°           | **once**: the composer focus edge (registered as a `bg-` utility) |

Font family (Tailwind `font-sans` override): `"Segoe UI", "Segoe UI Variable", "Segoe WP",
Frutiger, "Helvetica Neue", system-ui, sans-serif`. Mono (`font-mono` override) for command
lines: `"Cascadia Code", Consolas, monospace`. Weights: **200** wordmark/headings, **300**
body & message text, **400** default, **600** caps labels only.

### Type scale (4px baseline rhythm — sizes paired with explicit line-heights)

- `wordmark` — `font-extralight text-[32px] leading-[36px] tracking-tight lowercase` → "yession." in the sidebar.
- `heading` — `font-extralight text-[28px] leading-[32px] lowercase` → the column header (session name).
- `body` — `font-light text-[15px] leading-6` → message text (15/24).
- `small` — `font-light text-[13px] leading-4` → hints, ghost drafts, queue inputs.
- `label` — `font-semibold text-[11px] leading-4 tracking-[0.18em] uppercase text-ink-faint` →
  authors, section labels, offsets, statuses; tabular figures (`tabular-nums`) wherever digits align.

Message internal rhythm: one 16px meta line + 8px gap + n×24px body lines, on a
`20px avatar column · 12px gutter · content` grid (avatar nudged −2px to sit on cap height).

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
│  mise run build⟳│ ▍ gradient edge grows on focus ─────────────────┘ │
└─────────────────┴──────────────────────────────────────────────────┘
```

Section mapping (all eight existing `View` sections survive; `data-*` hooks unchanged):

- **Sidebar** (`section.connection` + `.offsets` + `.environment` + `.commands`):
  wordmark, connection state + offsets line (a product invariant, styled as a quiet
  tabular line — not hidden), presence list with tiny square display pics, environment
  status (text, not a pill), and the command log as compact mono cards. Commands may
  *also* render inline in the timeline as Cursor‑style cards where they belong to an
  agent turn (the projection already interleaves by offset; presentation‑only choice).
- **Timeline** (`section.timeline`): the main scroll, pinned to bottom like every chat
  surface. Avatar‑column grid, author caps‑label + time, light body text; streaming
  messages get a blue caret and dimmed body; arrival animation translate‑up + fade.
- **Agent activity strip** (`section.agent`): a slim 48px bar between timeline and queue
  — blue pulse square, "agent is responding", turn id, **Interrupt** (bordered, danger
  hover) on the right.
- **Queue** (`section.queue`): Cursor's queued‑messages pattern, stacked above the
  composer. Each 40px row: tiny avatar, inline‑editable input, reorder/delete icon
  buttons revealed on hover/focus. Left edge carries a 2px hairline that turns green on
  hover — it encodes editability.
- **Composer** (`section.draft`): the primary draft as a flat surface; on focus a 2px left edge grows top-to-bottom in the
  blue→green gradient (the design's one gradient);
  **Send** is a bordered blue button; other peers' in‑progress drafts shown as an
  avatar + "X is drafting…" ghost line beneath (their content is already synced state).

### Mobile (≤ 780px)

The workspace collapses to the conversation: the sidebar becomes an off‑canvas drawer
(translate‑X, scrim, 220ms sharp ease‑out) toggled by subtle chevrons (one in the sidebar band, one floated in the header gutter so the title stays on the content column);
header drops to 64px; timeline/queue/composer go full‑width with 16px gutters; the
composer hint and the activity strip's turn id hide. All breakpoint behaviour is
Tailwind `max-md:` / `md:` variants — still zero authored CSS.

## Architecture — compose Tailwind utilities as typed F# values

New module `Yession.App/Style.fs` (referenced before `View.fs`). It holds *named
compositions of Tailwind utility classes* — the values below **are** Tailwind classes; F#
just names, groups, and composes them so the view has no magic strings.

```fsharp
module Style =
    /// Join Tailwind utility groups into a class attribute value.
    let cls (groups: string list) : string = String.concat " " groups

    // Semantic groups — each is a string of real Tailwind utilities.
    let label    = "font-semibold text-[11px] leading-4 tracking-[0.18em] uppercase text-ink-faint"
    let body     = "font-light text-[15px] leading-6 text-ink"
    let statusOk = "font-semibold text-[11px] leading-4 tracking-[0.14em] uppercase text-green"    // text, never boxed
    let btn      = "border border-[#2e2e2e] text-ink-dim hover:border-ink hover:text-ink active:bg-ink active:text-bg font-semibold text-[11px] leading-4 tracking-[0.16em] uppercase px-3.5 py-[7px] transition"
    let sidebar  = "w-[280px] shrink-0 bg-panel border-r border-hair flex flex-col px-6 pb-5 overflow-y-auto max-md:fixed max-md:inset-y-0 max-md:left-0 max-md:z-40 max-md:-translate-x-full max-md:transition-transform"
    let queueRow = "flex items-center gap-3 bg-surface h-10 px-3 border-l-2 border-hair hover:border-green hover:bg-surface-2 transition"
    let arrive   = "animate-arrive motion-reduce:animate-none"   // registered keyframes, see headTags
    // …one value per role: wordmark / heading / timeline / msg / activity / composer / btn / pill.

    /// The <head> tags that deliver Tailwind + register the theme (colors, fonts, and the
    /// 'arrive'/'pulse' keyframes all live in this F#-emitted config object). No CSS file.
    let headTags = """
      <script src="https://cdn.tailwindcss.com"></script>
      <script>tailwind.config={theme:{extend:{
        colors:{bg:'#000',panel:'#0a0a0a',surface:'#111','surface-2':'#191919',hair:'#1f1f1f',
          ink:'#fff','ink-dim':'#b4b4b4','ink-faint':'#6a6a6a',blue:'#1ba1e2',green:'#a8dd00',err:'#ff4a4a'},
        backgroundImage:{grad:'linear-gradient(90deg,#1ba1e2,#a8dd00)'},   // the one gradient
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

1. **`src/Yession.App/Style.fs`** *(new)* — the named Tailwind‑utility groups, `cls`,
   and `headTags`. Add to `Yession.App.fsproj` **before** `View.fs`.
2. **`src/Yession.App/View.fs`** — restructure `render` into the workspace layout
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
