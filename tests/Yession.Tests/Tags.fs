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
    | Keyring     // a usable OS credential manager (Keychain / Credential Manager / Secret Service)
    | Nix         // the nix CLI, to evaluate/build this repo's derivations against the working tree
    | Srt         // OS-level confinement: bubblewrap + socat on Linux, Seatbelt on macOS
    | Pty         // the native `node-pty` addon — a real pseudo-terminal, not a pipe
    | Serial      // a real serial engine: the `serialport` addon, `udevadm`, and socat for a PTY pair
    | Jumpstarter // uv, and a resolvable Python environment for the jumpstarter example
    | Bench       // this run is MEASURING, not asserting — see `tasks.fsx bench`
    | Dogfood     // consent to the LONG self-hosting run: this repo's own suite inside the
                  // dev container it declares. Needs Docker beside it, egress, and patience.

// process.env under Node; the CLR reads it through System.Environment below. Guarded so this
// branch is dead-code-eliminated out of the .NET build path — jsNative would throw there.
[<Emit("(typeof process !== 'undefined' && process.env[$0]) || ''")>]
let private jsEnv (name: string) : string = Fable.Core.Util.jsNative

/// Read an environment variable on whichever runtime we are on.
let private getEnv (name: string) : string =
    if Compiler.isDotnet then
        match System.Environment.GetEnvironmentVariable name with null -> "" | v -> v
    else jsEnv name

// `Bench` is deliberately absent: `verify` means "every capability", and a timing suite in the
// release gate is minutes of runtime buying a number nothing in the gate asserts on. It is asked
// for by name, by `tasks.fsx bench`, and nowhere else. `Dogfood` is absent for the same shape of
// reason: the self-hosting run re-executes this whole suite inside a container whose devshell it
// first substitutes, which is tens of minutes buying a proof the gate already has piecewise —
// it is asked for by name (`check Docker Dogfood`, or a `verify.yml` dispatch naming both) when
// the container environment story changes.
let private allNeeds = [ Browser; Ports; Native; Docker; LiveAgent; Keyring; Nix; Srt; Pty; Serial; Jumpstarter ]

let private parseNeed (s: string) : Need option =
    match s.Trim().ToLowerInvariant () with
    | "browser"   -> Some Browser
    | "ports"     -> Some Ports
    | "native"    -> Some Native
    | "docker"    -> Some Docker
    | "liveagent" -> Some LiveAgent
    | "keyring"   -> Some Keyring
    | "nix"       -> Some Nix
    | "srt"       -> Some Srt
    | "pty"       -> Some Pty
    | "serial"    -> Some Serial
    | "jumpstarter" -> Some Jumpstarter
    | "bench"     -> Some Bench
    | "dogfood"   -> Some Dogfood
    | _           -> None

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

/// What the run declared, verbatim. A capability is never dropped here: `check` refuses to
/// start when the box cannot host one it was asked for (tasks.fsx `requireCapabilities`), so
/// by the time this runs the declared set is the available set. `LiveAgent` used to be
/// silently removed when no credential was present, which is how a release workflow naming a
/// secret this repository does not have shipped every version up to v5.0.0-beta.0 with the
/// live agent suite reporting a skip.
let private caps : Set<Need> = requestedCaps

let private onDotnet = Compiler.isDotnet

/// The runtime a suite runs on is fixed by whether it needs a real browser.
let private runsOnDotnet (need: Need list) : bool = List.contains Browser need

/// Include a suite only when this run hosts its runtime AND has every capability it needs.
let private canRun (need: Need list) : bool =
    onDotnet = runsOnDotnet need
    && need |> List.forall (fun n -> Set.contains n caps)

/// What this run declared, for the skip line — which now means exactly "this tier did not ask
/// for it", since a cap that was asked for and is unavailable fails the run instead.
let private declared : string =
    if Set.isEmpty caps then "none"
    else caps |> Set.toList |> List.map (sprintf "%A") |> String.concat ", "

let private reason (need: Need list) : string =
    if onDotnet <> runsOnDotnet need then
        if onDotnet then "runs on Node (Fable/JS), not the .NET CLR"
        else "runs on the .NET CLR (browser), not Node"
    else
        need
        |> List.filter (fun n -> not (Set.contains n caps))
        |> List.map (sprintf "%A")
        |> String.concat ", "
        |> fun missing -> sprintf "needs %s (this run has: %s)" missing declared

/// Include a suite only when the current run can satisfy its needs; otherwise stand in one
/// visible skip labelled with why. The suite thunk is forced only when it will run.
///
/// The stand-in is `ptestCase` (Pyxpecto `Pending`), NOT a passing no-op: it lands in the
/// run's `ignored` count instead of inflating `passed`, so the tail of a run says how much of
/// the suite never executed. A skip that reports itself as a pass is indistinguishable from
/// coverage, which is how a daemon-less `verify` used to print `383 passed, 0 ignored`.
/// A substring the run was narrowed to (`check --only …`), or empty for the whole suite.
let private only : string = (getEnv "YESSION_TEST_ONLY").Trim ()

/// Prune the tree to the cases whose FULL name contains `only`, dropping any list left with
/// nothing in it — so a narrowed run prints the handful it kept rather than a thousand skips.
///
/// The filter lives here rather than in the runner because Pyxpecto has none: its `ConfigArg`
/// is `FailOnFocused | Silent | DoNotExitWithCode` and nothing else. It lives beside `needs`
/// because they answer the same question — which of these cases does this run execute — and
/// two answers to one question in two files is how they drift.
let rec private narrow (path: string) (test: TestCase) : TestCase option =
    let full (name: string) = if path = "" then name else path + " - " + name
    let keep (name: string) = (full name).ToLowerInvariant().Contains (only.ToLowerInvariant ())
    match test with
    | SyncTest (name, _, _) -> if keep name then Some test else None
    | AsyncTest (name, _, _) -> if keep name then Some test else None
    | TestList (name, cases, focus) ->
        match cases |> List.choose (narrow (full name)) with
        | [] -> None
        | kept -> Some (TestList (name, kept, focus))
    | TestListSequential (name, cases, focus) ->
        match cases |> List.choose (narrow (full name)) with
        | [] -> None
        | kept -> Some (TestListSequential (name, kept, focus))

/// The suite this run should execute. Unchanged unless `--only` narrowed it.
///
/// A narrowing that matches nothing here is NOT an error: the two runtimes run the same
/// declaration, so `--only` aimed at a browser case legitimately matches nothing on Node. It
/// says so on its own line instead, because "0 tests run" and "everything passed" print
/// almost identically and only one of them is good news.
let narrowed (suite: TestCase) : TestCase =
    if only = "" then suite
    else
        match narrow "" suite with
        | Some kept ->
            printfn "tests: narrowed to '%s'" only
            kept
        | None ->
            printfn "tests: narrowed to '%s' — nothing here matches (check the spelling, or it may live on the other runtime)" only
            TestList ("narrowed", [], Normal)

let needs (label: string) (need: Need list) (suite: unit -> TestCase) : TestCase =
    if canRun need then suite ()
    else testList label [ ptestCase (sprintf "skipped: %s" (reason need)) <| fun () -> () ]
