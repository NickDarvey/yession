namespace Yession.App

open Lit

/// The client's icons, as inline SVG.
///
/// They used to be text: `✕`, `↑`, `↓`, `‹`, `✓`, `✗`. A glyph renders only if the reader's
/// machine has a font carrying it, and the `font-ui` face this design asks for (`Monaspace Argon`
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
    // The terminal list's verbs (Plan 20, stage 0), drawn to the same rule as everything
    // above: straight segments only. A power ring or a refresh loop would be the obvious
    // glyph for two of these and would be the first curve in the set — so each says its
    // meaning in the geometry this vocabulary actually has.
    //
    /// Step back to what is behind: the transport bar a playhead runs into. The triangle is
    /// CLOSED — open, its two strokes read as a letter K at 14px rather than as a playhead,
    /// which is the whole difference between a glyph and a decoration.
    let private rewindPath = "M12 3.5 L5.5 8 L12 12.5 Z M3.5 4 L3.5 12"
    /// Play, as an outline — a closed terminal is a recording, and this is the mark that
    /// says so. Never filled, because nothing in this set is.
    let private playPath = "M5.75 3.5 L12 8 L5.75 12.5 Z"
    /// Stop. What killing a terminal does, in the one shape that has always meant it.
    let private stopPath = "M4 4 L12 4 L12 12 L4 12 Z"
    /// Go back INTO something: an arrow meeting the wall it reconnects to.
    let private attachPath = "M2.5 8 L10 8 M6.75 4.75 L10 8 L6.75 11.25 M12.5 3.5 L12.5 12.5"
    /// A list. What the terminal list's toggle shows, and it needs no other reading.
    let private listPath = "M3.5 4.5 L12.5 4.5 M3.5 8 L12.5 8 M3.5 11.5 L12.5 11.5"
    /// A tack: a diamond head on a needle. ONE glyph for both states of the control — this
    /// set has no fills, so a "filled pin" would be its first, and state here is carried the
    /// way every other state in this design is: by colour, with `aria-pressed` saying it to
    /// anything that cannot see colour.
    ///
    /// Drawn as a head and a needle rather than the side-on pin's head/shaft/shoulder/point,
    /// which was four strokes in a 14px box: looked at on a real screen, its two bars merged
    /// and the whole thing read as a ⊤.
    let private pinPath = "M8 2.5 L11.5 6 L8 9.5 L4.5 6 Z M8 9.5 L8 13.5"
    /// An archive box: a lid across the top, the body under it, and a notch in the middle of
    /// the lid for the hand. Deliberately NOT a downward arrow or a tray with one — this set
    /// already spends arrows on direction, and an arrow here would read as "download".
    let private archivePath =
        "M2.5 3.5 L13.5 3.5 L13.5 6.5 L2.5 6.5 Z M3.5 6.5 L3.5 12.5 L12.5 12.5 L12.5 6.5 M6.5 9 L9.5 9"

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

    /// The terminal list's row verbs. Each is rendered only where the terminal's state
    /// affords it (`Affordances`), so the icon a row wears is itself the statement
    /// of what that terminal can do — which is why none of them needs a word beside it.
    let rewind = stroked "w-3.5 h-3.5" rewindPath
    let stop = stroked "w-3.5 h-3.5" stopPath
    let attach = stroked "w-3.5 h-3.5" attachPath
    let list = stroked "w-3.5 h-3.5" listPath
    /// Retire this session from the working list. A row-riding verb like the three above, and
    /// it needs no word for the same reason: it is rendered only on a row that can be
    /// archived, and an archived row wears the word `Unarchive` instead.
    let archive = stroked "w-3.5 h-3.5" archivePath
    /// Keep this tab, or stop keeping it. Never an `✕`: on a tab strip that glyph means
    /// "gone", and this control's whole point is that what it releases keeps running.
    let pin = stroked "w-3.5 h-3.5" pinPath
    /// The pin at the size a tab wears it: 14px of glyph beside 11px caps sat four pixels
    /// proud of the line it was marking. Same shape, sized and aligned to the text it
    /// follows, exactly as `checkSm` is.
    let pinSm = stroked "w-3 h-3 inline-block align-[-1px]" pinPath

    /// Inline with a status word: smaller, and nudged onto the caps baseline. `-3px`, not
    /// `-1px`: the strokes sit inside the 12px box with ~3px of clear space under them (the
    /// check's lowest vertex lands at y≈8.4 of 12), so at -1px the paint floated a couple of
    /// pixels above the digits beside it — measured against the caps line in a tally.
    let checkSm = stroked "w-3 h-3 inline-block align-[-3px]" checkPath
    let crossSm = stroked "w-3 h-3 inline-block align-[-3px]" crossPath
    /// The mark a row wears when what it holds is a RECORDING rather than a terminal you
    /// can type into. Sits on the caps baseline beside the row's state, like the two above.
    let playSm = stroked "w-3 h-3 inline-block align-[-1px]" playPath
