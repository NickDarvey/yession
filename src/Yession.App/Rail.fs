namespace Yession.App

/// Where a stroke sits on the landmark rail, given where its message actually is.
///
/// The rail used to be an INDEX: strokes spaced by rank on a log scale, so a stroke was never
/// level with the message it pointed at. That was defensible on its own and unreadable beside
/// the per-item controls it shared a margin with — two columns of near-identical dashes, one
/// placed by the conversation and one by arithmetic, and nothing on the screen saying which
/// was which.
///
/// So it is a MAP where it can be and an index where it cannot. Read as one function of how
/// far a message's top sits above the fold:
///
///   in the exact zone   the stroke is level with the message, to the pixel
///   above it            eased, asymptotically to the rail's top
///   below the fold      the same easing mirrored, into a band at the rail's foot
///
/// The two easings share a shape — `budget * (1 - e^(-travel/budget))` — chosen because it
/// covers an unbounded travel in `budget` pixels AND arrives with a derivative of exactly 1.
/// That second property is what makes both seams invisible: a stroke leaving the exact zone
/// begins to lag rather than jumping, so scrolling moves the rail continuously.
///
/// Nothing here knows what a message or a scrollport is. It is arithmetic, and it lives where
/// a cheap test can reach it rather than in the browser layer that measures its inputs — the
/// measuring is three lines, and this is the part that can be wrong.
module Rail =

    /// The band at the rail's foot, holding everything below the fold. Small, because a chat
    /// sits at its newest message: marks below the fold are what you have scrolled PAST, and
    /// they matter enough to be visible and not enough to spend the rail on.
    ///
    /// It is also the reason the exact zone can be exact. A stroke's own place is measured
    /// from the rail's bottom, so a band reserved under the exact zone would shift every
    /// stroke in it by exactly this much — unless the zone starts here, which is what the
    /// `foot ≤ aboveFold` arm below arranges.
    let private footOf (height: float) : float = min 12.0 (height * 0.1)

    /// How much of the rail is exact, above the foot: everything but the top quarter or so of
    /// the screen. Read from the other end, that is where the lag STARTS — a message is level
    /// with its stroke until it is on its way off the top, which is what a person scrolling
    /// describes.
    ///
    /// It is a trade, and this is the side of it worth buying. What the rest of the rail is
    /// for is everything scrolled PAST, which arrives compressed however much room it has, so
    /// a head band spends the rail on resolution nobody reads. What the exact zone buys is the
    /// promise: a message you can see has its stroke on it. Three quarters is where that
    /// promise covers the whole reading area — including the middle, which is where the rail's
    /// own jump puts a message — while leaving enough head for a long session's history to
    /// stay ordered and distinct rather than smearing into one line.
    let private stuckOf (height: float) : float = height * 0.72

    /// `budget * (1 - e^(-travel/budget))`: maps `[0, ∞)` onto `[0, budget)`, with a
    /// derivative of 1 at the origin.
    let private ease (budget: float) (travel: float) : float =
        if budget <= 0.0 then 0.0 else budget * (1.0 - exp (-travel / budget))

    /// Where one stroke goes, as a distance above the rail's bottom.
    ///
    /// `height` is the rail's, and `aboveFold` is the rail's own bottom edge minus the
    /// message's top — positive while the message's top is on screen, negative once it has
    /// gone under. Measured against the RAIL rather than the scrollport, though the two
    /// stand on the same box: it is what makes the exact zone exact by construction rather
    /// than by two elements agreeing about where their bottoms are.
    ///
    /// In the exact zone the answer IS `aboveFold`, and that is the whole trick: a stroke
    /// placed `r` above the rail's bottom is at the same y as a point `r` above the
    /// message's own measurement, so the two are the same line.
    let place (height: float) (aboveFold: float) : float =
        if height <= 0.0 then 0.0
        else
            let foot = footOf height
            let stuck = stuckOf height
            let head = height - foot - stuck
            let ceiling = foot + stuck
            if aboveFold < foot then foot - ease foot (foot - aboveFold)
            elif aboveFold <= ceiling then aboveFold
            else ceiling + ease head (aboveFold - ceiling)

    /// Keep strokes apart, and in order.
    ///
    /// Everything scrolled away shares one band, so a long session's older marks arrive on top
    /// of each other — ordered, but as a smear rather than as marks. Walking up from the
    /// newest and refusing to place one closer than `gap` to the one below fixes that where it
    /// happens, and is a no-op in the exact zone, where the conversation has already spread
    /// them out.
    ///
    /// Takes and returns the rail's own order: oldest first, which is the order the timeline
    /// holds them in and the order the strokes are rendered in.
    let spaced (gap: float) (height: float) (places: float list) : float list =
        places
        |> List.rev
        |> List.fold
            (fun (kept, floor) here ->
                let placed = min height (max here floor)
                placed :: kept, placed + gap)
            ([], 0.0)
        |> fst
