module Yession.Tests.Tag

// Unified test requirements (replaces the old `Tag.verify` tier flag + the `#if`-gated
// runtime split). A suite declares the capabilities it NEEDS; the harness runs it only when
// the current run can satisfy them, and stands in one visible skip otherwise. Every need pins
// the runtime that can host it, so a suite runs on exactly ONE runtime — never twice, never
// hidden.
//
//   Tag.needs "WebRTC E2E"  [Ports]   (fun () -> E2E.tests)      // Node,   verify tier
//   Tag.needs "Docker"      [Docker]  (fun () -> Docker.tests)   // Node,   verify + daemon
//   Tag.needs "Browser E2E" [Browser] (fun () -> Browser.tests)  // .NET CLR, verify tier
//   Tag.needs "Domain"      []        (fun () -> Domain.tests)   // Node,   cheap tier
//
// The suite is a thunk, forced only when it will actually run, so a suite is never even
// constructed on a runtime that cannot host it — its module-level interop never executes.
//
// `Compiler.isDotnet` (Fable 5.2) is the runtime discriminator: a compile-time constant under
// Fable (the CLR branch is dead-code-eliminated out of the JS) and a runtime `true` on .NET.
// That is what lets one shared test list serve both runtimes without a `#if` in `Main`.

open Fable.Core
open Fable.Pyxpecto
open Fable.Pyxpecto.Model

/// A capability a suite needs. Each need pins the runtime that can host it (see `satisfies`).
type Need =
    | Browser     // a real browser via the Microsoft.Playwright .NET driver -> .NET CLR
    | Ports       // binds TCP ports / spawns processes (WebRTC, HTTP, topology) -> Node
    | Docker      // a reachable Docker daemon -> Node
    | LiveAgent   // real model credentials -> Node

// process.env under Node; the CLR reads it through System.Environment below. Guarded so this
// branch is dead-code-eliminated out of the .NET build path — jsNative would throw there.
[<Emit("(typeof process !== 'undefined' && process.env[$0]) || ''")>]
let private jsEnv (name: string) : string = Fable.Core.Util.jsNative

/// Read an environment variable on whichever runtime we are on.
let private getEnv (name: string) : string =
    if Compiler.isDotnet then
        match System.Environment.GetEnvironmentVariable name with null -> "" | v -> v
    else jsEnv name

/// This run executes on the .NET CLR (vs Fable/JS on Node).
let private onDotnet = Compiler.isDotnet

/// Verify tier active? (`YESSION_TEST_TIER=verify|all`, the release gate.)
let verifyEnabled =
    let t = getEnv "YESSION_TEST_TIER"
    t = "verify" || t = "all"

let private hasCreds =
    getEnv "ANTHROPIC_API_KEY" <> "" || getEnv "CLAUDE_CODE_OAUTH_TOKEN" <> ""

/// Can this run satisfy one need? Each arm fixes the runtime, so no test runs on both.
let private satisfies =
    function
    | Browser   -> onDotnet && verifyEnabled
    | Ports     -> not onDotnet && verifyEnabled
    | Docker    -> not onDotnet && verifyEnabled
    | LiveAgent -> not onDotnet && verifyEnabled && hasCreds

/// A suite with no needs is a cheap, pure suite: it runs on Node (the runtime the product
/// actually runs on), never on the CLR — so it too executes exactly once.
let private canRun =
    function
    | []    -> not onDotnet
    | needs -> needs |> List.forall satisfies

let private reason =
    function
    | []    -> "runs on Node (Fable/JS), not the .NET CLR"
    | needs -> needs |> List.map (sprintf "%A") |> String.concat ", " |> sprintf "needs %s"

/// Include a suite only when the current run can satisfy its needs; otherwise stand in one
/// visible skip labelled with why. The suite thunk is forced only when it will run.
let needs (label: string) (need: Need list) (suite: unit -> TestCase) : TestCase =
    if canRun need then suite ()
    else testList label [ testCase (sprintf "skipped: %s" (reason need)) <| fun () -> () ]
