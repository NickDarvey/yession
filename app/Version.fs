module Yession.Host.Version

// What build am I? Both bins answer this — over `--version`, and across the spawn handshake — so
// a running Yession is never anonymous.
//
// The version is a COMPILE-TIME constant: `tasks.fsx stage` passes esbuild
// `--define:YESSION_BUILD_VERSION="<version>"` when it bundles `manager.js` and `session.js`, so
// the identifier below is substituted for a literal. Nothing is read from disk at run time — the
// bundles deliberately never depend on a package.json being next to them.
//
// Every build states what it actually is; there are no placeholder versions:
//   1.0.0-beta.95   a release (GitVersion, see GitVersion.yml)
//   0.0.0-g1a2b3c4  a pure Nix build — .git is stripped from its source, so it reports its rev
//   test            the test tiers (`check` stages with this)
//   dev             an unbundled run of the Fable output (`tasks.fsx start` / `dev`)

open Fable.Core

/// This build's version. `dev` is the honest answer for an unbundled run of the Fable output:
/// it is the ONLY path with no esbuild define. `typeof` guards the identifier so such a run
/// cannot ReferenceError — esbuild substitutes inside the `typeof` too, which is harmless.
[<Emit("typeof YESSION_BUILD_VERSION !== 'undefined' ? YESSION_BUILD_VERSION : 'dev'")>]
let current : string = jsNative

/// The major component, for skew detection. `None` for anything that is not a release version
/// (`dev`, `test`, malformed input) — a build that cannot state a release version is never
/// compared against one.
let majorOf (version: string) : int option =
    match version.Split '.' with
    | [||] -> None
    | parts ->
        match System.Int32.TryParse parts.[0] with
        | true, major -> Some major
        | _ -> None
