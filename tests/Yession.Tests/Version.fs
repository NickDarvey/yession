module Yession.Tests.Version

// app/Version.fs and the version half of the spawn contract. Cheap tier: pure functions over
// strings, no ports, no processes.
//
// The load-bearing property here is what these functions must NOT do. `majorOf` gates the
// spawn-time skew REFUSAL (Plan 11), so reading a major out of a build that has no release
// version would refuse every dev run; and the readiness line's `version` field is an
// addition to an existing contract, so a session bundle built before it existed has to keep
// launching.

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

// Plan 11 turned the skew warning into a refusal, because a deployment that floats its
// session binary can otherwise pair two processes that no longer agree and discover it as
// some unrelated symptom later.
let private skewTests =
    testList "Spawn.majorSkewBetween" [
        testCase "matching majors launch" <| fun () ->
            Expect.equal (Spawn.majorSkewBetween "1.4.0-beta.2" (Some "1.9.0-beta.7")) None "same major, different minor"
            Expect.equal (Spawn.majorSkewBetween "1.4.0-beta.2" (Some "1.4.0-beta.2")) None "identical"

        testCase "a differing major is refused, and the message names both builds" <| fun () ->
            match Spawn.majorSkewBetween "1.4.0-beta.2" (Some "2.0.0-beta.0") with
            | None -> failwith "a major mismatch must be refused"
            | Some reason ->
                Expect.isTrue (reason.Contains "1.4.0-beta.2") "should name the manager's build"
                Expect.isTrue (reason.Contains "2.0.0-beta.0") "should name the session's build"

        testCase "a downgrade is skew too — the comparison is not directional" <| fun () ->
            Expect.isSome (Spawn.majorSkewBetween "2.0.0-beta.0" (Some "1.4.0-beta.2")) "older session, newer manager"

        // Everything below is a build that cannot state a release version. Refusing those
        // would break every local run and the whole test suite, which is why `majorOf`
        // answering None has to mean "do not compare" rather than "assume mismatch".
        testCase "a build with no release version is never compared" <| fun () ->
            Expect.equal (Spawn.majorSkewBetween "dev" (Some "2.0.0-beta.0")) None "unbundled manager"
            Expect.equal (Spawn.majorSkewBetween "test" (Some "2.0.0-beta.0")) None "the test tiers"
            Expect.equal (Spawn.majorSkewBetween "1.4.0-beta.2" (Some "dev")) None "unbundled session"

        testCase "a session that reports no version at all still launches" <| fun () ->
            Expect.equal (Spawn.majorSkewBetween "1.4.0-beta.2" None) None "a bundle older than the version field"
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

let tests = testList "Version" [ currentTests; majorTests; skewTests; readinessTests ]
