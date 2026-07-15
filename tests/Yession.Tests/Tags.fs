module Yession.Tests.Tag

// Test tiers as tags in code, not folders (Phase 4, Step 21). Expensive suites —
// anything that binds real ports, spawns processes, drives a browser, needs a Docker
// daemon, or calls a live model — are wrapped in `Tag.verify` and run only in the
// verify tier (`YESSION_TEST_TIER=verify`, the release gate). The default tier stays
// cheap, fast, and credential-free. A tagged suite excluded from the current tier is
// reported as one visible skipped case — skips are reported, never hidden.

open Fable.Pyxpecto
open Fable.Pyxpecto.Model

[<Fable.Core.Emit("process.env[$0] || ''")>]
let private env (name: string) : string = Fable.Core.Util.jsNative

let private tier = env "YESSION_TEST_TIER"

/// Is the verify tier active in this run?
let verifyEnabled = tier = "verify" || tier = "all"

let private nameOf =
    function
    | SyncTest (name, _, _) -> name
    | AsyncTest (name, _, _) -> name
    | TestList (name, _, _) -> name
    | TestListSequential (name, _, _) -> name

/// Include `test` only in the verify tier; otherwise stand in one visible skip.
let verify (test: TestCase) : TestCase =
    if verifyEnabled then test
    else
        testList (nameOf test) [
            testCase "skipped: verify tier (run with YESSION_TEST_TIER=verify)" <| fun () -> ()
        ]
