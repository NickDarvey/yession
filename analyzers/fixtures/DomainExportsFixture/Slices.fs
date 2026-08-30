namespace Yession.Domain.FixtureAlpha

/// Feature slices of the kind the rule governs. A line marked `// YES006` MUST be reported;
/// every other declaration here MUST NOT be. `lint` reads those markers and compares them to
/// what the analyzer actually said, in both directions.
///
/// The marker sits on each declaration that carries the shared name, because that is where the
/// rule reports: every one of them is a place the ambiguity could be resolved, and a namespace
/// has no line of its own to point at.
///
/// These are real `Yession.Domain.*` namespaces rather than a stand-in, because the population
/// the rule names is exactly that family. The suite this replaced could not do it: it scanned
/// `app`, `src`, `tests` and `examples` for real, so a fixture slice written down the page would
/// have been a real slice, and its fixtures were assembled from escaped string literals
/// instead. This project is not in the solution, so its namespaces are the fixture's and reach
/// nothing else.

/// Shared with FixtureBeta, and the two are not interchangeable.
type Projection = // YES006
    { Rows: int }

/// Nothing else in the family carries this.
type AlphaOnly =
    { Depth: int }

namespace Yession.Domain.FixtureBeta

type Projection = // YES006
    { Items: string list }

type BetaOnly =
    { Width: int }

// --- not reported: a name shared with a namespace OUTSIDE the family --------------------------
//
// The rule names its population deliberately. A file is expected to open several domain slices
// at once, which is what makes an ambiguity between them likely enough to forbid outright;
// nothing says the same about a namespace that merely exists.

namespace Elsewhere.Entirely

type AlphaOnly =
    { Unrelated: bool }

// --- not reported: one slice, one name --------------------------------------------------------
//
// A slice may carry whatever it likes as long as no sibling carries it too. This is the case
// that has to keep working, because it is every declaration in the domain.

namespace Yession.Domain.FixtureGamma

type GammaOnly =
    { Height: int }

module GammaOnly =

    let zero = { Height = 0 }
