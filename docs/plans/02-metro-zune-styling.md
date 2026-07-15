# Step — Metro / Zune styling for the client shell

> Phase 4 · Client presentation
> Design context: [docs/design.md](../design.md) §1 (Reactive, Types first), §2.1

## Goal

Give the Browser Client the look and motion of classic **Metro / Zune** (pre‑Windows 8):
elegant Segoe‑family typography with deliberate weights and baselines, flat high‑contrast
surfaces, a single vivid accent, and a **panorama** layout where each concern is a *place*
in a horizontal 3D canvas you pan across — so navigation is spatial, not a scroll‑stack.

Styling is authored **entirely in F#** by composing **Tailwind's own utility classes** into
typed, named values — **no CSS files and no hand‑written CSS.** Tailwind supplies the
utilities; F# supplies the composition. The pure `View` keeps emitting deterministic markup;
it just gains class names.

## Design north star

Zune / early Metro is a small set of rules applied without compromise:

- **Content over chrome.** No borders, no bevels, no gradients, no drop shadows, no
  rounded corners. Structure comes from *type* and *whitespace*, not boxes.
- **Typography is the interface.** Huge thin display headings; tiny wide‑tracked ALL‑CAPS
  labels; a clear body weight. Hierarchy is carried by weight and size, never by rules or
  fills. Everything sits on a baseline grid.
- **Authentically digital.** Flat colour, one accent, motion that is fast and directional.
- **Panorama / Pivot.** The canvas is *wider than the screen*. An oversized wordmark
  ("yession") is the backdrop and bleeds off the right edge; sections sit side‑by‑side and
  you pan horizontally; layers move at different rates (parallax) so the space reads as 3D.

### Theme tokens (registered once, from F#)

Registered in the Tailwind theme so semantic utilities resolve (`text-accent`, `bg-surface`,
`font-sans`); the config object is emitted from F#, not a CSS file.

| Token            | Value                                            | Utility → role |
|------------------|--------------------------------------------------|------|
| `bg`             | `#000000`                                        | `bg-bg` — Zune black canvas |
| `surface`/`surface-2` | `#0b0b0b` / `#141414`                       | `bg-surface` — panel washes (barely lifted) |
| `ink`            | `#ffffff`                                         | `text-ink` — primary text |
| `ink-dim`        | `#b8b8b8`                                         | `text-ink-dim` — secondary text |
| `ink-faint`      | `#6e6e6e`                                         | `text-ink-faint` — labels, metadata |
| `accent`         | `#f09609` (Zune orange)                           | `text-accent` — active, nav, focus |
| `magenta/teal/lime/violet` | `#e6007e / #00b7c3 / #a4c400 / #a200ff` | per‑pivot Metro tile accents |
| `ok`/`warn`/`err`| `#60d060 / #f09609 / #ff4040`                     | status pills only |

Font family (Tailwind `font-sans` override): `"Segoe UI", "Segoe UI Variable", "Segoe WP",
Frutiger, "Helvetica Neue", system-ui, sans-serif`. Weights via Tailwind: **`font-thin`**
(display wordmark), **`font-extralight`** (200 — titles), **`font-light`** (300 — subhead/body
lift), **`font-normal`** (400 — body), **`font-semibold`** (600 — labels/emphasis). Never bold
a heading — Metro headings get *bigger and lighter*, not heavier.

### Type scale (Tailwind sizes + arbitrary tracking)

- `display` — `font-thin text-[6rem] tracking-tighter leading-none` → the panorama wordmark.
- `title` — `font-extralight text-5xl lowercase tracking-tight` → section titles.
- `subhead` — `font-light text-xl`.
- `body` — `font-normal text-[0.9375rem] leading-relaxed`.
- `label` — `font-semibold text-[0.6875rem] tracking-[0.18em] uppercase` → the tiny caps labels.

## Architecture — compose Tailwind utilities as typed F# values

New module `Yession.Client/Style.fs` (referenced before `View.fs`). It holds *named
compositions of Tailwind utility classes* — the values below **are** Tailwind classes; F#
just names, groups, and composes them so the view has no magic strings.

```fsharp
module Style =
    /// Join Tailwind utility groups into a class attribute value.
    let cls (groups: string list) : string = String.concat " " groups

    // Semantic groups — each is a string of real Tailwind utilities.
    let title   = "font-extralight text-5xl lowercase tracking-tight text-ink"
    let label   = "font-semibold text-[0.6875rem] tracking-[0.18em] uppercase text-ink-faint"
    let panel   = "snap-start shrink-0 min-w-[min(80vw,40rem)] px-14 pt-36 pb-16 overflow-y-auto"
    let card    = "bg-surface hover:bg-surface-2 active:scale-[.985] transition p-5 flex flex-col gap-2"
    let btnGhost = "text-ink-faint hover:text-ink text-[0.6875rem] font-semibold tracking-[0.14em] uppercase px-2 py-1"
    // …one value per role: rail / wordmark / panel / title / label / card / field / btn / pill / motion.

    /// The <head> tags that deliver Tailwind + register the theme. No CSS file.
    let headTags = """
      <script src="https://cdn.tailwindcss.com"></script>
      <script>tailwind.config={theme:{extend:{
        colors:{bg:'#000',surface:'#0b0b0b','surface-2':'#141414',ink:'#fff','ink-dim':'#b8b8b8',
          'ink-faint':'#6e6e6e',accent:'#f09609',magenta:'#e6007e',teal:'#00b7c3',lime:'#a4c400',
          violet:'#a200ff',ok:'#60d060',warn:'#f09609',err:'#ff4040'},
        fontFamily:{sans:['Segoe UI','Segoe UI Variable','Segoe WP','Frutiger','Helvetica Neue','system-ui','sans-serif']}
      }}}</script>"""
```

- **Compose, don't author.** Views write `cls [ Style.card; Style.label ]`; each value is a
  bundle of Tailwind utilities. No `.css` file, no `@apply`, no `<style>` block with rules —
  only Tailwind's utilities and one theme‑config object, both emitted from F#.
- **Typed & deterministic.** The value set is fixed and named in F#, so a typo is a compile
  error and refactors are safe. `View.render` stays a pure total function that only emits
  *class names* — server bootstrap and browser render remain byte‑identical (design.md §1).
- **Tailwind delivery = the Play CDN (a script, not a stylesheet).** `cdn.tailwindcss.com`
  generates utilities at runtime and **watches the DOM**, so the `#app` innerHTML swap in
  `Browser.fs` gets styled automatically — no per‑render step. This keeps the "no CSS file"
  constraint literally true. *Local‑first caveat:* the Play CDN is a network script; for a
  fully offline session, vendor that JS and serve it like `client.js` (a `/tailwind.js`
  route) — still a script, still no CSS file. Recommend Play CDN now, vendor‑JS when offline
  matters.
- Pseudo‑classes (`hover:`, `active:`, `focus-visible:`), responsive/`motion-reduce:`
  variants, arbitrary transforms (`[perspective:1000px]`, `[transform:translateZ(...)]`) for
  parallax — all are ordinary Tailwind utilities, so the Metro motion stays in‑composition.

## Spatial layout — the panorama

Map the eight existing sections onto a horizontal panorama. Reading left→right *is* the
navigation:

```
┌────────────────────────────────────────── pan → ──────────────────────────────────────────┐
│  yession            session ▸ draft  ▸  queue  ▸  timeline ▸ agent ▸ environment ▸ commands │
│  (display wordmark, bleeds off right edge; parallax backdrop)                               │
└─────────────────────────────────────────────────────────────────────────────────────────────┘
```

- **Track**: `#app` becomes `flex snap-x snap-mandatory overflow-x-auto`; each `<section>` a
  `Style.panel` (`snap-start shrink-0 min-w-[...]`). One vivid accent per pivot header
  (magenta/orange/teal cycling à la Metro tiles), title lowercase.
- **3D depth via parallax.** The oversized `yession` wordmark and section titles sit on layers
  that translate slower than content — Tailwind arbitrary utilities (`[perspective:1000px]`,
  `[transform-style:preserve-3d]`, `[transform:translateZ(-1px)_scale(2)]`) or a slower
  `translate-x` on the wordmark layer. Fast ease‑out on hover/press (`active:scale-[.98]`).
- **Section mapping**: connection+offsets fold into a slim **status rail** (persistent, top,
  tiny caps); draft / queue / timeline / agent / environment / commands each become a panorama
  panel with a `Style.title` heading and `Style.label` metadata.

### Constraint to resolve: scroll state vs full re‑render

`Browser.fs` replaces `#app` innerHTML on every model change, which would reset the
panorama's `scrollLeft`. There is already a precedent to reuse: `focusedEditor` /
`refocusEditor` snapshot and restore the focused textarea across re‑render. **Recommended:**
mirror that pattern — capture `#app.scrollLeft` before `setHtml` and restore it after (a
~4‑line addition in `setState`). *Alternative* (larger): make the panorama track a persistent
DOM element and let the pure view fill only the panel contents. Recommend the scroll‑restore
mirror first; it stays within the existing pattern and keeps the view pure.

## File-by-file changes

1. **`src/Yession.Client/Style.fs`** *(new)* — the named Tailwind‑utility groups, `cls`, and
   `headTags`. Add to `Yession.Client.fsproj` **before** `View.fs`.
2. **`src/Yession.Client/View.fs`** — add `cls [...]` class attributes to every element
   (status rail, panels, titles, labels, drafts, queue, timeline, messages, agent, commands).
   Inject `Style.headTags` into `page`'s `<head>`. Markup structure changes only where the
   panorama needs wrapper layers; the `data-*` hooks stay untouched so `Browser.fs` delegation
   and the E2E selectors keep working.
3. **`app/browser/Browser.fs`** — add `scrollLeft` capture/restore around `setHtml` in
   `setState`, mirroring the focus‑preservation code.
4. *(No change)* `app/Signalling.fs` — still serves `View.page`; Tailwind rides in `<head>`.

## Verification (automated, per design.md §2.2)

- `mise run build` type‑checks the solution and Fable‑compiles the host — proves `Style.fs`
  and the reworked view compile and that class names stay pure/total.
- The existing WebRTC/UI E2E suite must stay green: keep all `data-*` attributes and the
  section hooks so selectors resolve. Add a UI‑checklist assertion that the served `/` head
  loads Tailwind and registers the theme, and that `#app` carries the panorama track classes
  — a cheap regression that styling shipped.
- Browser E2E: assert the wordmark renders and horizontal snap panels exist; assert
  `scrollLeft` is preserved across a model update (guards the re‑render fix).
- Visual truth is manual‑only by nature; keep it out of the gate but use the reference mock
  (companion) as the design target.

## Decisions & alternatives

- **"No CSS" = no CSS files / no authored CSS — compose Tailwind's utilities instead.**
  Deliver Tailwind via the Play CDN (a script, not a stylesheet) so the constraint holds
  literally; compose its utilities as typed F# values. **Recommended.**
- **Play CDN now; vendor the Tailwind JS when offline matters.** Vendoring keeps local‑first
  intact and is still a script, not a CSS file. Rejected: the Tailwind *CLI build* and the
  *standalone* both emit a `.css` file — against the constraint.
- **Named utility groups in F#** over scattering raw class strings through the view: typed,
  refactorable, single source per role — the same reason Tailwind users extract components.
- **Scroll‑restore mirror** over persistent‑shell refactor for panorama state — smaller,
  reuses the focus‑preservation precedent, keeps `View.render` a total function.

## Rollout

1. Land `Style.fs` (utility groups + `headTags`) with a couple of throwaway usages; `mise run build`.
2. Restyle `View.fs` section by section (status rail → panels → items); keep `data-*` hooks.
3. Add scroll preservation in `Browser.fs`.
4. Extend the UI checklist / browser E2E; `mise run test`.
5. Tune tokens against the reference mock (weights, baselines, accent cadence, parallax).
