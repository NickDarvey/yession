module Yession.Tests.Tag

// Unified test requirements. A suite declares the capabilities it NEEDS; the harness runs it
// only when this run both hosts its runtime AND has its capabilities available, and stands in
// one visible skip otherwise.
//
//   Tag.needs "WebRTC E2E"  [Ports; Native] (fun () -> E2E.tests)     // Node, spawns process
//   Tag.needs "Docker"      [Docker]        (fun () -> Docker.tests)  // Node, needs daemon
//   Tag.needs "Browser E2E" [Browser;Native](fun () -> Browser.tests) // .NET CLR + real host
//   Tag.needs "Editor (br)" [Browser]       (fun () -> editorTests)   // .NET CLR, no host
//   Tag.needs "Domain"      []              (fun () -> Domain.tests)  // Node, cheap tier
//
// Two axes, decoupled:
//   * RUNTIME is fixed by whether the suite needs a real browser — `Browser` pins the .NET CLR
//     (where the Microsoft.Playwright driver lives); everything else runs on Node (the runtime
//     the product actually runs on). Each suite therefore runs on exactly ONE runtime.
//   * CAPABILITIES are orthogonal availability flags the RUN declares it has, via
//     `YESSION_TEST_CAPS` (e.g. `check Browser Native`). A suite runs only when
//     every need it lists is in that set. `Native` is the discriminator that lets the host-free
//     editor E2E (`[Browser]`) run wherever Chromium exists, while the WebRTC/host suites
//     (`… Native`) skip when the native `node-datachannel` addon is absent — instead of erroring.
//
// The suite is a thunk, forced only when it will actually run, so a suite is never constructed
// on a runtime that cannot host it — its module-level interop never executes.
//
// `Compiler.isDotnet` (Fable 5.2) is the runtime discriminator: a compile-time constant under
// Fable (the CLR branch is DCE'd out of the JS) and a runtime `true` on .NET.

open Fable.Core
open Fable.Pyxpecto
open Fable.Pyxpecto.Model

/// A capability a suite needs. `Browser` also pins the runtime (see `runsOnDotnet`); the rest
/// are pure availability flags the run declares through `YESSION_TEST_CAPS`.
type Need =
    | Browser     // a real browser via the Microsoft.Playwright .NET driver -> pins the .NET CLR
    | Ports       // binds TCP ports / spawns processes (HTTP, topology)
    | Native      // the native `node-datachannel` WebRTC addon (loaded by the real Session Process)
    | Docker      // a reachable Docker daemon
    | LiveAgent   // real model credentials

// process.env under Node; the CLR reads it through System.Environment below. Guarded so this
// branch is dead-code-eliminated out of the .NET build path — jsNative would throw there.
[<Emit("(typeof process !== 'undefined' && process.env[$0]) || ''")>]
let private jsEnv (name: string) : string = Fable.Core.Util.jsNative

/// Read an environment variable on whichever runtime we are on.
let private getEnv (name: string) : string =
    if Compiler.isDotnet then
        match System.Environment.GetEnvironmentVariable name with null -> "" | v -> v
    else jsEnv name

let private allNeeds = [ Browser; Ports; Native; Docker; LiveAgent ]

let private parseNeed (s: string) : Need option =
    match s.Trim().ToLowerInvariant () with
    | "browser"   -> Some Browser
    | "ports"     -> Some Ports
    | "native"    -> Some Native
    | "docker"    -> Some Docker
    | "liveagent" -> Some LiveAgent
    | _           -> None

let private hasCreds =
    getEnv "ANTHROPIC_API_KEY" <> "" || getEnv "CLAUDE_CODE_OAUTH_TOKEN" <> ""

/// The capabilities THIS run declares it has. `YESSION_TEST_CAPS` is the primary API (a
/// space/comma list, e.g. from `check Browser Native`); `YESSION_TEST_TIER=verify|all`
/// is a back-compat alias meaning "every capability".
let private requestedCaps : Set<Need> =
    let tier = getEnv "YESSION_TEST_TIER"
    if tier = "verify" || tier = "all" then Set.ofList allNeeds
    else
        (getEnv "YESSION_TEST_CAPS").Split ([| ' '; ','; ';' |], System.StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose parseNeed
        |> Set.ofArray

/// `LiveAgent` additionally requires real credentials — drop it when absent, so `-- LiveAgent`
/// without creds reports a skip rather than failing at connect time.
let private caps : Set<Need> =
    if hasCreds then requestedCaps else Set.remove LiveAgent requestedCaps

let private onDotnet = Compiler.isDotnet

/// The runtime a suite runs on is fixed by whether it needs a real browser.
let private runsOnDotnet (need: Need list) : bool = List.contains Browser need

/// Include a suite only when this run hosts its runtime AND has every capability it needs.
let private canRun (need: Need list) : bool =
    onDotnet = runsOnDotnet need
    && need |> List.forall (fun n -> Set.contains n caps)

let private reason (need: Need list) : string =
    if onDotnet <> runsOnDotnet need then
        if onDotnet then "runs on Node (Fable/JS), not the .NET CLR"
        else "runs on the .NET CLR (browser), not Node"
    else
        need
        |> List.filter (fun n -> not (Set.contains n caps))
        |> List.map (sprintf "%A")
        |> String.concat ", "
        |> sprintf "needs %s (not in YESSION_TEST_CAPS)"

/// Include a suite only when the current run can satisfy its needs; otherwise stand in one
/// visible skip labelled with why. The suite thunk is forced only when it will run.
let needs (label: string) (need: Need list) (suite: unit -> TestCase) : TestCase =
    if canRun need then suite ()
    else testList label [ testCase (sprintf "skipped: %s" (reason need)) <| fun () -> () ]
