namespace Alpha.Beta.Widgets

/// One scope of every shape the rule has an opinion about. A line marked `// YES005` MUST be
/// reported; every other declaration here MUST NOT be. `lint` reads those markers and compares
/// them to what the analyzer actually said, in both directions — a rule that has started
/// rejecting disjoint scopes is as broken as one that has gone blind.
///
/// The marker sits on the shared MEMBER rather than on either scope, because that is where the
/// rule reports: a namespace has no single declaration to point at, and the member is the thing
/// that has to be renamed.
///
/// The violations are written out as real scopes. That is most of the point of moving this off
/// a scan of F# source: the suite this replaced read every `.fs` file under `app`, `src`,
/// `tests` and `examples`, so a scope written down the page here would have been a real scope
/// in a real scanned file. Its fixtures were therefore assembled from escaped string literals
/// and only ever tested the reader against text.

// --- reported: a namespace and a module, one short name, one shared member -------------------
//
// A file that opens `Alpha.Beta` and has `Other.Place.Widgets` in scope means two things by
// `Widgets`, and `Widgets.Gadget` silently resolves to the namespace.

type Gadget = // YES005
    { Size: int }

/// Not shared, so not the fault. Here so the reported member is the shared one rather than
/// everything the namespace holds.
type Cog =
    { Teeth: int }

namespace Other.Place

module Widgets =

    let Gadget () = 2 // YES005

    let unrelated () = 3

// --- not reported: the same short name over disjoint members ---------------------------------
//
// This is the case the codebase already relies on four times over — `Yession.Domain` beside the
// suite's `Yession.Tests.Domain`, `Yession.Manager` beside `Yession.Host.Manager`. The
// reference falls through to the module and the build is clean; sharing the name alone is not
// the fault, and a rule that said otherwise would be unusable.

namespace Gamma.Delta.Doohickey

type Sprocket =
    { Length: int }

namespace Other.Place

module Doohickey =

    let nothingInCommon () = 4

// --- not reported: two namespaces --------------------------------------------------------
//
// Deliberately out of scope for this rule, which is about a namespace beside a MODULE. Two
// feature namespaces exporting one name is a stricter rule with a different population, and it
// is not this one.

namespace Epsilon.One.Thing

type Shared =
    { Weight: int }

namespace Epsilon.Two.Thing

type Shared =
    { Height: int }

// --- not reported: a module nothing outside can open -----------------------------------------
//
// The hazard is what an `open` of a shared parent puts in front of an arbitrary file. A scope
// sealed where no other file can name it is not something an arbitrary file can open, so it
// shadows nothing.

namespace Zeta.Sealed

module private Widgets =

    let Gadget () = 5
