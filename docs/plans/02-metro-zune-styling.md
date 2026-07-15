# Step — Metro / Zune styling for the client shell

> Phase 4 · Client presentation
> Design context: [docs/design.md](../design.md) §1 (Reactive, Types first), §2.1

## Goal

Give the Browser Client the look and motion of classic **Metro / Zune** (pre‑Windows 8):
elegant Segoe‑family typography with deliberate weights and baselines, flat high‑contrast
surfaces, a single vivid accent, and a **panorama** layout where each concern is a *place*
in a horizontal 3D canvas you pan across — so navigation is spatial, not a scroll‑stack.

Styling is authored **entirely in F#** as composable utility atoms (a Tailwind‑shaped
API), with **no hand‑written CSS files and no CSS toolchain**. The pure `View` keeps
emitting deterministic markup; it just gains class names.

## Design north star

Zune / early Metro is a small set of rules applied without compromise:

- **Content over chrome.** No borders, no bevels, no gradients, no drop shadows, no
  rounded corners. Structure comes from *type* and *whitespace*, not boxes.
- **Typography is the interface.** Huge thin display headings; tiny wide‑tracked ALL‑CAPS
  labels; a clear body weight. Hierarchy is carried by weight and size, never by rules or
  fills. Everything sits on a baseline grid.
- **Authentically digital.** Flat colour, one accent, motion that is fast and directional.
- **Panorama / Pivot.** The canvas is *wider than the screen*. An oversized wordmark
  ("Yession") is the backdrop and bleeds off the right edge; sections sit side‑by‑side and
  you pan horizontally; layers move at different rates (parallax) so the space reads as 3D.

### Concrete tokens (defined once, in F#)

| Token            | Value                                            | Role |
|------------------|--------------------------------------------------|------|
| `bg`             | `#000000`                                        | Zune black canvas |
| `surface`        | `#0b0b0b` / `#141414`                             | panel washes (barely lifted) |
| `ink`            | `#ffffff`                                         | primary text |
| `ink-dim`        | `#b8b8b8`                                         | secondary text |
| `ink-faint`      | `#6e6e6e`                                         | labels, metadata |
| `accent`         | `#f09609` (Zune orange)                           | active state, nav, focus |
| `accent-alt`     | `#e6007e` (Metro magenta) *(optional per‑pivot)* | section accents |
| `ok` / `warn` / `err` | `#60d060` / `#f09609` / `#ff4040`           | status text only |

Type stack: `"Segoe UI", "Segoe UI Variable", "Segoe WP", Frutiger, "Helvetica Neue", system-ui, sans-serif`.
Weights used: **200** (display/light), **300** (subhead), **400** (body), **600** (emphasis).
Never bold a heading — Metro headings get *bigger and lighter*, not heavier.

### Type scale & baseline

An 8px baseline grid; a modular type scale locked to it:

- `display` — 72–96px / weight 200 / -0.02em tracking → the panorama wordmark.
- `title` — 40px / weight 200 → section titles ("timeline", "queue", …), lowercase.
- `subhead` — 20px / weight 300.
- `body` — 15px / weight 400 / 1.5 line-height.
- `label` — 11px / weight 600 / +0.18em tracking / uppercase → the tiny caps labels.

## Architecture — a typed atomic styling engine in F#

New module `Yession.Client/Style.fs` (referenced before `View.fs`). It is the whole
styling system; there is no `.css` file anywhere.

```fsharp
module Style =
    /// A utility atom: a stable class name plus the rule it stands for.
    type Atom = { Name: string; Rule: string }   // Rule is the body only, e.g. "font-weight:200"

    /// Compose atoms into a class attribute value (Tailwind-style, but typed values).
    let cls (atoms: Atom list) : string = atoms |> List.map (fun a -> a.Name) |> String.concat " "

    // Curated, static atom set — the "utilities". Names are deterministic strings.
    let displayText = { Name = "t-display"; Rule = "font-weight:200;font-size:5rem;letter-spacing:-.02em;line-height:1" }
    let label       = { Name = "t-label";   Rule = "font-weight:600;font-size:.6875rem;letter-spacing:.18em;text-transform:uppercase" }
    let accentText  = { Name = "c-accent";  Rule = "color:#f09609" }
    let panPanel    = { Name = "l-panel";   Rule = "scroll-snap-align:start;min-width:min(78vw,34rem);padding:6rem 3rem" }
    // …one atom per utility, grouped: typography / colour / spacing / layout / motion / state.

    /// Fold every atom + keyframes + the base reset into one stylesheet string.
    let stylesheet : string = /* emit ".t-display{font-weight:200;…}\n…" + @media + @keyframes */
```

- **Typed, not stringly.** Views compose `cls [ Style.title; Style.inkDim ]` — atoms are
  F# values, so a typo is a compile error and refactors are safe. This is the Tailwind
  *composition* model without the magic strings.
- **Static & deterministic.** The atom set is fixed (like Tailwind's generated classes), so
  `stylesheet` is a constant. `View.render` stays a pure total function that only emits
  *class names* — server bootstrap and browser render remain byte‑identical (design.md §1).
- **One injection point.** `View.page` puts `<style>{Style.stylesheet}</style>` in
  `<head>`. The browser only ever swaps `#app` innerHTML (see `Browser.fs` `setHtml`), and
  `<head>` is outside `#app`, so the sheet survives every re‑render with zero special handling.
  No new HTTP route, no build step, no external stylesheet — the CSS text is a *product of
  F# composition*, never authored by hand.
- Pseudo‑classes, `@media`, `@keyframes`, and `transform` (needed for hover, parallax,
  panning) are all expressible this way — which rules out the inline‑`style=""` alternative
  (it cannot express `:hover`/`@keyframes`).

## Spatial layout — the panorama

Map the eight existing sections onto a horizontal panorama. Reading left→right *is* the
navigation:

```
┌────────────────────────────────────────── pan → ──────────────────────────────────────────┐
│  yession            session ▸ draft  ▸  queue  ▸  timeline ▸ agent ▸ environment ▸ commands │
│  (display wordmark, bleeds off right edge; parallax backdrop)                               │
└─────────────────────────────────────────────────────────────────────────────────────────────┘
```

- **Track**: `#app` becomes a horizontal `scroll-snap` container (`overflow-x:auto`,
  `scroll-snap-type: x mandatory`), each `<section>` a snap panel (`l-panel`). One vivid
  accent per pivot header (magenta/orange/teal cycling à la Metro tiles), title lowercase.
- **3D depth via parallax.** The oversized `yession` wordmark and the section titles live on
  layers that translate slower than the content on scroll — CSS `perspective` +
  `transform: translateZ()` on layered children, or a `scroll-timeline`/`translateX` parallax.
  Fast ease‑out on hover/press (Metro tilt: `active:scale(.98)`).
- **Section mapping**: connection+offsets fold into a slim **status rail** (persistent, top
  or left, tiny caps); draft / queue / timeline / agent / environment / commands each become
  a panorama panel with a `title` heading and `label` metadata.

### Constraint to resolve: scroll state vs full re‑render

`Browser.fs` replaces `#app` innerHTML on every model change, which would reset the
panorama's `scrollLeft`. There is already a precedent to reuse: `focusedEditor` /
`refocusEditor` snapshot and restore the focused textarea across re‑render. **Recommended:**
mirror that pattern — capture `#app.scrollLeft` before `setHtml` and restore it after (a
~4‑line addition in `setState`). *Alternative* (larger): make the panorama shell a
persistent DOM element and let the pure view fill only the panel contents. Recommend the
scroll‑restore mirror first; it stays within the existing pattern and keeps the view pure.

## File-by-file changes

1. **`src/Yession.Client/Style.fs`** *(new)* — the atom set, `cls`, and `stylesheet`. Add to
   `Yession.Client.fsproj` **before** `View.fs`.
2. **`src/Yession.Client/View.fs`** — add `cls [...]` class attributes to every element
   (status rail, panels, titles, labels, drafts, queue, timeline, messages, agent, commands).
   Inject `<style>Style.stylesheet</style>` into `page`'s `<head>`. Markup structure changes
   only where the panorama needs wrapper layers; the `data-*` hooks stay untouched so
   `Browser.fs` delegation and the E2E selectors keep working.
3. **`app/browser/Browser.fs`** — add `scrollLeft` capture/restore around `setHtml` in
   `setState`, mirroring the focus‑preservation code.
4. *(No change)* `app/Signalling.fs` — still serves `View.page`; the sheet rides in `<head>`.

## Verification (automated, per design.md §2.2)

- `mise run build` type‑checks the solution and Fable‑compiles the host — proves the F#
  atom engine and the reworked view compile and that class names are pure/total.
- The existing WebRTC/UI E2E suite must stay green: keep all `data-*` attributes and the
  section class hooks so selectors resolve. Add a UI‑checklist assertion that the served `/`
  contains `<style>` with the panorama atoms and that `#app` carries the panorama track
  class — cheap regression that styling shipped.
- Browser E2E: assert the wordmark renders and horizontal scroll‑snap panels exist; assert
  `scrollLeft` is preserved across a model update (guards the re‑render fix).
- Visual truth is manual‑only by nature; keep it out of the gate but attach a reference
  mock (companion artifact) as the design target.

## Decisions & alternatives

- **"No CSS" = no authored CSS / no toolchain, not "no stylesheet".** Browsers require CSS;
  here every rule is *generated from F# atoms*, never hand‑written, and there is no
  Tailwind/PostCSS build. If instead you want literally zero `<style>` (inline `style=""`
  only), say so — but that forfeits `:hover`, `@keyframes`, and parallax, so it is not
  recommended for the Metro motion. **Recommended: generated sheet from F# atoms.**
- **Static atom set** (Tailwind‑style fixed utilities) over JIT‑collected‑per‑render:
  keeps `stylesheet` constant and the view pure; no per‑render style re‑injection.
- **Scroll‑restore mirror** over persistent‑shell refactor for the panorama state — smaller,
  reuses the focus‑preservation precedent, keeps `View.render` a total function.

## Rollout

1. Land `Style.fs` (atoms + sheet) with a couple of throwaway usages; `mise run build`.
2. Restyle `View.fs` section by section (status rail → panels → items); keep `data-*` hooks.
3. Add scroll preservation in `Browser.fs`.
4. Extend the UI checklist / browser E2E; `mise run test`.
5. Tune tokens against the reference mock (weights, baselines, accent cadence, parallax).
</content>
</invoke>
