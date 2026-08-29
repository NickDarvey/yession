module EmitBodyFixture.Bodies

open Fable.Core
open Fable.Core.JsInterop

/// One macro of every shape the rule has an opinion about. A line marked `// YES003` MUST be
/// reported; every other macro here MUST NOT be. `lint` reads those markers and compares them
/// to what the analyzer actually said, in both directions — a rule that has started rejecting
/// safe bodies is as broken as one that has gone blind.
///
/// The marker sits on the ATTRIBUTE rather than the binding because that is where the rule
/// reports: the string is the half that is wrong.
///
/// The violations below are written out. That is the point of moving this off a scan of F#
/// source: the suite this replaced could not quote one, because a violating macro written
/// literally in a scanned file would have been a real violation of the contract it was
/// checking, so its fixtures were assembled from concatenated fragments and only ever tested
/// the detector against strings, never against a real binding.

// --- safe: the substitutions arrive as parameters of a real function -------------------------

[<Emit("(function (peer) { const p = peer; return p.id })($0)")>]
let safePeerId (peer: obj) : string = jsNative

[<Emit("(async function (handle) { const h = handle; return h.close() })($0)")>]
let safeClose (handle: obj) : obj = jsNative

// --- safe: nothing is substituted, so there is no caller text to collide with ----------------

[<Emit("(() => { const now = Date.now(); return now })()")>]
let now () : float = jsNative

// --- safe: a declaration inside a comment is prose, not a binding ----------------------------
//
// Prose warning about this rule necessarily contains examples of breaking it, and one of the
// macros in `examples/serial/src/Ws.fs` carries exactly that. Reading a comment as code would
// make the warning itself the violation.

[<Emit("// const peer = someCaller\n$0.x")>]
let lineCommented (v: obj) : obj = jsNative

[<Emit("/* const peer = someCaller */ $0.y")>]
let blockCommented (v: obj) : obj = jsNative

// --- safe: substitutes, declares nothing, repeats nothing ------------------------------------

[<Emit("$0.close()")>]
let close (handle: obj) : unit = jsNative

// A `$` before something that is not a digit is not a substitution, and a spread names its
// slot once like any other placeholder.

[<Emit("/=\"?$/.test($0)")>]
let endsWithAssignment (html: string) : bool = jsNative

[<Emit("$0.format($1...)")>]
let format (fmt: obj) (args: obj) : string = jsNative

// --- reported: a declaration that can collide with the caller's own variable ------------------

[<Emit("(() => { const peer = $0; return peer.id })()")>] // YES003
let peerId (peer: obj) : string = jsNative

// `function` and `class` shadow exactly as `const` does.

[<Emit("(() => { function go() { return $0 } return go() })()")>] // YES003
let viaFunction (v: obj) : obj = jsNative

// --- reported: a placeholder read more than once evaluates its argument more than once -------

[<Emit("$0.a && $0.b")>] // YES003
let both (v: obj) : bool = jsNative

// --- reported: both faults at once, which is two diagnostics on one range --------------------

[<Emit("(() => { const a = $0; return a.x + $0.y })()")>] // YES003
let declaresAndRepeats (v: obj) : obj = jsNative
