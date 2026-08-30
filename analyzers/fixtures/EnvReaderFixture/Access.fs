module EnvReaderFixture.Access

open Fable.Core
open Fable.Core.JsInterop

/// The forms a read is made of, and nothing here names a variable — so nothing here may be
/// reported, however many times `process.env` is written below.

[<Emit("process.env[$0] || $1")>]
let read (name: string) (fallback: string) : string = jsNative

[<Emit("process.env[$0] = $1")>]
let write (name: string) (value: string) : unit = jsNative

/// A wrapper hands its own parameter straight to a reader, which makes it a reader too — in
/// the slot that parameter arrives in. The variable is named at ITS call sites, never here,
/// and a rule that looked only for direct reads would find this file reading a variable it
/// cannot name and every caller reading nothing at all.
let setting (name: string) = read name ""

[<Literal>]
let Shared = "FIXTURE_SHARED"
