module EmitMacroFixture.Emits

open Fable.Core
open Fable.Core.JsInterop

/// One binding of every shape the rule has an opinion about. A line marked `// YES002` MUST
/// be reported; every other macro here MUST NOT be. `lint` reads those markers and compares
/// them to what the analyzer actually said, in both directions — a rule that has started
/// rejecting correct macros is as broken as one that has gone blind.
///
/// The marker sits on the ATTRIBUTE rather than the binding because that is where the rule
/// reports: the string is the half that is wrong, and a diagnostic on the signature would
/// send a reader to the line that is fine.

// Correct: every argument named, every name an argument.

[<Emit("$0.style.setProperty($1, $2)")>]
let setStyleProperty (el: obj) (name: string) (value: string) : unit = jsNative

[<Emit("$0 + $0")>]
let twice (x: string) : string = jsNative

// A `unit` parameter is not an argument, so a macro that names nothing is right.

[<Emit("Date.now()")>]
let now () : float = jsNative

// Neither is a value with no parameters at all.

[<Emit("(m => ({ level: m[1].length }))")>]
let headingAttrs: obj = jsNative

// `$` before something that is not a digit is not a placeholder: a regex end anchor, and
// the property name Lit marks its own values with.

[<Emit("/=\"?$/.test($0)")>]
let endsWithAssignment (html: string) : bool = jsNative

[<Emit("($0 != null && $0._$litType$ !== undefined)")>]
let isTemplate (v: obj) : bool = jsNative

// Fable's other macro syntax still names slots the ordinary way: `...` spreads the rest of
// the arguments from a placeholder, and a `{{ }}` block emits its contents conditionally.
// Neither hides a reference from the rule, and neither invents one.

[<Emit("$0.format($1...)")>]
let format (fmt: obj) (args: obj[]) : string = jsNative

[<Emit("$0{{ = $1}}")>]
let assign (target: obj) (value: obj) : unit = jsNative

// A parameter the macro has no use for says so the way F# says it anywhere else.

[<Emit("window.__typed = $1")>]
let recordTyped (_terminal: string) (data: string) : unit = jsNative

// An instance member's `$0` is the receiver, so its own parameters start at 1.

type Element =
    [<Emit("$0.setAttribute($1, $2)")>]
    abstract SetAttribute: name: string * value: string -> unit

// Wrong: a placeholder past the last argument. Fable substitutes nothing, and the emitted
// JavaScript reads `undefined`.

[<Emit("$0($1)")>] // YES002
let callWith (f: obj) : obj = jsNative

// Wrong: an argument no placeholder names. Fable emits nothing for it, so the expression at
// the call site is never evaluated.

[<Emit("$0.close()")>] // YES002
let closeWith (handle: obj) (reason: string) : unit = jsNative

// Wrong in both directions at once, which is what an off-by-one on a receiver looks like: the
// member's own parameter is skipped and a slot past the end is read in its place.

type Node =
    [<Emit("$0.appendChild($2)")>] // YES002
    abstract Append: child: obj -> unit
