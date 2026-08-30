module RecordShapeFixture.Shapes

/// One record of every shape the rule has an opinion about. A line marked `// YES004` MUST be
/// reported; every other declaration here MUST NOT be. `lint` reads those markers and compares
/// them to what the analyzer actually said, in both directions — a rule that has started
/// rejecting safe declarations is as broken as one that has gone blind.
///
/// The marker sits on the declaration line, because that is where the rule reports: the type
/// that can still capture a construction is the one at fault, and it is reported at its own
/// site rather than as one line naming a group.
///
/// The violations are written out as real declarations. That is most of the point of moving
/// this off a scan of F# source: the suite this replaced read every `.fs` file under `app`,
/// `src`, `tests` and `examples`, so a fixture written down the page here would have been a
/// real collision in a real scanned file. Its fixtures were therefore assembled from escaped
/// string literals and only ever tested the reader against text, never against a declaration
/// the compiler had accepted.

// --- reported: two bare types carrying one field set -----------------------------------------
//
// The original fault, in miniature. `SecretId` was scope-and-name; a second wire type was
// given those same two labels, and constructions already written became the wrong type.

type Captured = // YES004
    { Scope: string
      Name: string }

type Capturing = // YES004
    { Scope: string
      Name: string }

// --- reported: only the bare one, when the group is half guarded -----------------------------
//
// `Careful` cannot capture anything — its labels are out of unqualified scope. `Careless` still
// can, and is the only one that has to change.

[<RequireQualifiedAccess>]
type Careful =
    { Left: int
      Right: int }

type Careless = // YES004
    { Left: int
      Right: int }

// --- reported: the field set is what matters, not the order it is written in -----------------

type Ordered = // YES004
    { First: int
      Second: int }

type Reordered = // YES004
    { Second: int
      First: int }

// --- reported: lowercase labels are labels ---------------------------------------------------
//
// The scan this replaced matched a label with `[A-Z]\w*`, so a record with a lowercase label
// was invisible to it and the rule silently did not apply. Nothing about the ambiguity cares
// how the label is capitalised.

type Lowered = // YES004
    { count: int
      label: string }

type AlsoLowered = // YES004
    { count: int
      label: string }

// --- reported: a private type still meets what is visible inside its own module --------------
//
// `Exposed` is reachable from inside `Inner`, so a file there can bring both label sets into
// one scope and build the wrong one.

module Inner =

    type private Hidden = // YES004
        { Token: string
          Ttl: int }

type Exposed = // YES004
    { Token: string
      Ttl: int }

// --- not reported: two private types sealed in different modules -----------------------------
//
// Neither module can name the other's type, so no scope holds both label sets and no
// construction is ambiguous. This is the live case in the suite — five modules declare a
// private `{ status; body }` for a `fetch` binding to return — and the scan this replaced
// reported it, having matched `private` and thrown it away.
//
// It is also why accessibility is a question the rule asks rather than one the population
// answers by omission: these two ARE both in the population, and `meet` is what separates
// them.

module Sealed =

    type private Reply =
        { status: int
          body: string }

    /// Kept honest: the type is used, so it cannot be dropped as dead.
    let private replied: Reply = { status = 200; body = "" }

    let status () = replied.status

module AlsoSealed =

    type private Reply =
        { status: int
          body: string }

    let private replied: Reply = { status = 204; body = "" }

    let status () = replied.status

// --- not reported ----------------------------------------------------------------------------

/// Every type in the group is qualified, which is the fix the rule asks for.
[<RequireQualifiedAccess>]
type GuardedOne =
    { Weight: float }

[<RequireQualifiedAccess>]
type GuardedTwo =
    { Weight: float }

/// Nothing else carries these, so nothing can be captured.
type Solitary =
    { OnlyHere: int
      AndAlsoHere: bool }

/// A subset is not the set. A record expression must give every field, so these two cannot be
/// built from the same construction.
type Superset =
    { OnlyHere: int
      AndAlsoHere: bool
      AndOneMore: string }

/// A union has no field labels to capture anything with, whatever its cases are called.
type NotARecord =
    | Scope of string
    | Name of string

/// An anonymous record belongs to no declaration, so there is nothing to qualify and nothing
/// that could reach back and re-point it.
let anonymous = {| Scope = "a"; Name = "b" |}
