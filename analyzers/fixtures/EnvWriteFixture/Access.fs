module EnvWriteFixture.Access

open Fable.Core
open Fable.Core.JsInterop

/// The macros an access is made of, and not one write between them. Nothing here may be
/// reported: declaring the JavaScript that assigns is not running it, and a rule that could
/// not tell those apart is the rule this replaced — its patterns read the attribute's TEXT, so
/// the file that owned the verb looked like the file that abused it.

[<Emit("process.env[$0] = $1")>]
let set (name: string) (value: string) : unit = jsNative

[<Emit("delete process.env[$0]")>]
let clear (name: string) : unit = jsNative

[<Emit("process.env[$0] || $1")>]
let read (name: string) (fallback: string) : string = jsNative

/// Spreading this environment into a CHILD's is not a write of this one. It subscripts
/// nothing, so there is no name to point at and no process here whose environment changed.
[<Emit("$0({ ...process.env, LANG: $1 })")>]
let spawn (run: obj) (lang: string) : unit = jsNative

/// A comparison is a read, whatever the `=` count suggests.
[<Emit("process.env[$0] === $1")>]
let holds (name: string) (value: string) : bool = jsNative
