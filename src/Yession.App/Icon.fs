namespace Yession.App

open Lit

/// The client's icons, as inline SVG.
///
/// They used to be text: `✕`, `↑`, `↓`, `‹`, `✓`, `✗`. A glyph renders only if the reader's
/// machine has a font carrying it, and the `font-sans` stack this design asks for (`Segoe UI`
/// first) is a WINDOWS stack — everywhere else the browser falls through to whatever it has,
/// which may substitute a differently-styled glyph, an emoji, or a tofu box. The buttons stayed
/// clickable and their `aria-label`s stayed correct, but a delete button that shows a tofu box
/// is a broken button.
///
/// Drawn instead: one 16px grid, `currentColor` (so `hover:`/`active:` colour them like text),
/// square caps and a 1.5 stroke — Metro's geometry, no curves, no fills. `aria-hidden` because
/// every icon here sits inside a control that carries its own accessible name.
module Icon =

    /// A stroked path on the shared 16px grid. `size` is the Tailwind box (icons are `block`,
    /// so they never inherit a text baseline gap inside a grid-centred button), `weight` the
    /// stroke — the icons carry two, for the same reason the type carries several: 1.5 for the
    /// working controls, and a lighter 1.1 for the marks that ride display type, whose word is
    /// set extralight and would be shouted down by a UI-weight stroke.
    let private strokedAt (weight: string) (size: string) (d: string) : TemplateResult =
        html
            $"""<svg class="{size} block" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="{weight}"
                     stroke-linecap="square" stroke-linejoin="miter" aria-hidden="true" focusable="false"><path d="{d}"></path></svg>"""

    let private stroked = strokedAt "1.5"

    // --- Paths (one place, so the two sizes of an icon can never drift) ---------------------

    let private closePath = "M4 4 L12 12 M12 4 L4 12"
    let private upPath = "M8 12.5 L8 4 M4.25 7.75 L8 4 L11.75 7.75"
    let private downPath = "M8 3.5 L8 12 M4.25 8.25 L8 12 L11.75 8.25"
    let private leftPath = "M9.75 3.5 L5.25 8 L9.75 12.5"
    let private rightPath = "M6.25 3.5 L10.75 8 L6.25 12.5"
    // Taller and narrower than the chrome chevrons: a mark set to the proportions of the
    // extralight lowercase word it travels with, not to a 24px button.
    let private pivotLeftPath = "M10.25 1.5 L4.5 8 L10.25 14.5"
    let private pivotRightPath = "M5.75 1.5 L11.5 8 L5.75 14.5"
    let private checkPath = "M3.5 8.25 L6.5 11.25 L12.5 5.25"
    let private crossPath = closePath
    let private sendPath = "M2.5 8 L12.5 8 M8.75 4.25 L12.5 8 L8.75 11.75"

    // --- The vocabulary ----------------------------------------------------------------------
    // 14px inside a 24px icon button; 12px where an icon rides a caps-label line.

    let close = stroked "w-3.5 h-3.5" closePath
    let up = stroked "w-3.5 h-3.5" upPath
    let down = stroked "w-3.5 h-3.5" downPath
    let left = stroked "w-3.5 h-3.5" leftPath
    let right = stroked "w-3.5 h-3.5" rightPath
    let send = stroked "w-3.5 h-3.5" sendPath

    /// The marks the sidebar's pivots travel with (`Style.navPivot`): tall, thin, and lighter
    /// than any working control, because they sit beside a 19px extralight word.
    let pivotLeft = strokedAt "1.1" "w-4 h-4" pivotLeftPath
    let pivotRight = strokedAt "1.1" "w-4 h-4" pivotRightPath

    /// Inline with a status word: smaller, and nudged onto the caps baseline.
    let checkSm = stroked "w-3 h-3 inline-block align-[-1px]" checkPath
    let crossSm = stroked "w-3 h-3 inline-block align-[-1px]" crossPath
