module Yession.Tests.Version

// app/Version.fs and the version half of the spawn contract. Cheap tier: pure functions over
// strings, no ports, no processes.
//
// The load-bearing property here is what these functions must NOT do. `majorOf` gates the
// spawn-time skew warning, so reading a major out of a build that has no release version would
// warn on every dev run; and the readiness line's `version` field is an addition to an existing
// contract, so a session bundle built before it existed has to keep launching.

open Fable.Pyxpecto
open Yession.Host

let private currentTests =
    testList "Version.current" [
        testCase "an unbundled run reports dev, never a version-shaped placeholder" <| fun () ->
            // These tests are Fable output run straight on Node — no esbuild, so no
            // `--define:YESSION_BUILD_VERSION`, which is exactly the `dev` case.
            Expect.equal Version.current "dev" "the fallback names the build path it belongs to"
    ]

let private majorTests =
    testList "Version.majorOf" [
        testCase "a release version yields its major" <| fun () ->
            Expect.equal (Version.majorOf "1.0.0-beta.94") (Some 1) "prerelease of the 1.x line"
            Expect.equal (Version.majorOf "2.0.0-beta.0") (Some 2) "the first build after a breaking change"
            Expect.equal (Version.majorOf "0.0.0-g1a2b3c4") (Some 0) "a pure Nix build reports its rev off the 0.0.0 line"

        testCase "a build with no release version has no major" <| fun () ->
            Expect.equal (Version.majorOf "dev") None "an unbundled dev run"
            Expect.equal (Version.majorOf "test") None "the test tiers"
            Expect.equal (Version.majorOf "") None "empty"
            Expect.equal (Version.majorOf "not.a.version") None "malformed"
    ]

let private readinessTests =
    testList "the readiness line's version field" [
        testCase "a readiness line without a version is still a valid readiness line" <| fun () ->
            Expect.equal
                (Spawn.parseReadyVersion """{"yession":"ready","port":1234}""")
                None
                "an older session bundle reports no version — and is not compared against one"
            Expect.equal
                (Spawn.parseReadyVersion """{"yession":"ready","port":1234,"version":"2.0.0-beta.1"}""")
                (Some "2.0.0-beta.1")
                "a current bundle reports its build"
            Expect.equal (Spawn.parseReadyVersion "not json at all") None "a log line is not a readiness line"
    ]

let tests = testList "Version" [ currentTests; majorTests; readinessTests ]
