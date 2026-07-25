namespace Fable.Keyring

// Bindings for `@napi-rs/keyring` (the Rust `keyring-rs` behind N-API): the OS
// credential managers — macOS Keychain, Windows Credential Manager, Linux Secret
// Service — behind one `Entry`. This declares only the slice the Manager's KeyStore
// uses and nothing more; nothing above that seam knows the library exists. The npm
// package ships per-platform prebuilds as plain registry-hosted optionalDependencies,
// so it loads without a build step wherever npm resolved it.

open Fable.Core

[<AllowNullLiteral>]
type Entry =
    /// Throws when no credential is stored under (service, name), and on backend
    /// errors (no Secret Service daemon, locked keychain) — callers wrap in Result.
    abstract getPassword : unit -> string
    abstract setPassword : password: string -> unit
    abstract deletePassword : unit -> bool

[<AutoOpen>]
module Entry =

    [<Import("Entry", "@napi-rs/keyring")>]
    let private entryCtor : obj = jsNative

    [<Emit("new $0($1, $2)")>]
    let private construct (ctor: obj) (service: string) (name: string) : Entry = jsNative

    /// Construct an entry handle. Constructing grants nothing and may itself throw on
    /// hosts with no usable credential store — callers guard it.
    let entry (service: string) (name: string) : Entry = construct entryCtor service name
