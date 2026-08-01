module Yession.Tests.Reaper

// The idle-reap decision (Plan 11). Every rule lives in a pure function, so all of this
// runs on virtual time — no sleeps, no timers, no ordering luck.

open System
open Fable.Pyxpecto
open Yession.Domain
open Yession.Manager

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

let private now = DateTimeOffset (2026, 8, 1, 12, 0, 0, TimeSpan.Zero)
let private window = TimeSpan.FromMinutes 30.0

let private launch (id: string) (lastBusyMinutesAgo: float) (everReported: bool) : LaunchActivity =
    { SessionId = SessionId.create id |> expect
      LastBusyAt = now.AddMinutes (-lastBusyMinutesAgo)
      EverReported = everReported }

let private reaped (plan: (SessionId * ReapReason) list) =
    plan |> List.map (fun (id, reason) -> SessionId.value id, ReapReason.describe reason)

let private tests' =
    testList "Reaper.plan" [
        testCase "a busy session is never reaped" <| fun () ->
            // Still reporting busy, so LastBusyAt keeps moving up to the present.
            Expect.equal (Reaper.plan now window [ launch "alpha" 0.0 true ] |> reaped) [] "just reported busy"
            Expect.equal (Reaper.plan now window [ launch "alpha" 29.0 true ] |> reaped) [] "inside the window"

        testCase "a session idle for the whole window is reaped" <| fun () ->
            Expect.equal
                (Reaper.plan now window [ launch "alpha" 31.0 true ] |> reaped)
                [ "alpha", "idle" ]
                "past the window"

        // The boundary is the documented one: idle FOR the timeout is reaped BY it.
        testCase "the boundary is inclusive" <| fun () ->
            Expect.equal (Reaper.plan now window [ launch "alpha" 30.0 true ] |> reaped) [ "alpha", "idle" ] "exactly 30m"
            Expect.equal
                (Reaper.plan now window [ launch "alpha" 29.999 true ] |> reaped)
                []
                "a hair inside is still inside"

        // The reversal from the first draft of this plan: silence is not a defence. A
        // launch too old to report, or one that has wedged, is exactly what needs stopping.
        testCase "a launch that never reported is reaped, and says so" <| fun () ->
            Expect.equal
                (Reaper.plan now window [ launch "alpha" 31.0 false ] |> reaped)
                [ "alpha", "never-reported" ]
                "silence is not immunity"

        testCase "a launch that never reported still gets the whole window first" <| fun () ->
            // LastBusyAt is seeded at launch, so this is a session started 10 minutes ago
            // that has said nothing. It is not yet due.
            Expect.equal (Reaper.plan now window [ launch "alpha" 10.0 false ] |> reaped) [] "not due yet"

        testCase "the reason distinguishes silence from a reported idle" <| fun () ->
            Expect.equal
                (Reaper.plan now window [ launch "quiet" 31.0 false; launch "idle" 31.0 true ] |> reaped)
                [ "quiet", "never-reported"; "idle", "idle" ]
                "two reaps, two reasons"

        testCase "only the due sessions are named" <| fun () ->
            let launches =
                [ launch "busy" 0.0 true
                  launch "stale" 45.0 true
                  launch "fresh" 5.0 true
                  launch "wedged" 60.0 false ]
            Expect.equal
                (Reaper.plan now window launches |> reaped)
                [ "stale", "idle"; "wedged", "never-reported" ]
                "the two overdue ones, in the order given"

        testCase "nothing running is nothing to do" <| fun () ->
            Expect.equal (Reaper.plan now window [] |> reaped) [] "empty"

        // A zero window would mean "reap everything at once", which is a configuration
        // mistake rather than a decision this function should second-guess — but it must at
        // least be total and predictable.
        testCase "a zero window reaps everything, without error" <| fun () ->
            Expect.equal
                (Reaper.plan now TimeSpan.Zero [ launch "alpha" 0.0 true ] |> reaped)
                [ "alpha", "idle" ]
                "0 >= 0"

        testCase "a clock that went backwards does not reap" <| fun () ->
            // NTP correction, or a laptop waking with a corrected clock: LastBusyAt is in
            // the future, so the difference is negative and nothing is due. The next report
            // repairs it.
            Expect.equal (Reaper.plan now window [ launch "alpha" -5.0 true ] |> reaped) [] "negative age"
    ]

let private windowTests =
    testList "IdleWindow.parse" [
        testCase "unset is off — reaping is never acquired by upgrading" <| fun () ->
            Expect.equal (IdleWindow.parse "" |> expect) None "blank"
            Expect.equal (IdleWindow.parse "   " |> expect) None "whitespace"

        testCase "the units are s, m and h" <| fun () ->
            Expect.equal (IdleWindow.parse "90s" |> expect) (Some (TimeSpan.FromSeconds 90.0)) "seconds"
            Expect.equal (IdleWindow.parse "30m" |> expect) (Some (TimeSpan.FromMinutes 30.0)) "minutes"
            Expect.equal (IdleWindow.parse "2h" |> expect) (Some (TimeSpan.FromHours 2.0)) "hours"
            Expect.equal (IdleWindow.parse "30M" |> expect) (Some (TimeSpan.FromMinutes 30.0)) "case-insensitive"

        testCase "a bare number is seconds" <| fun () ->
            Expect.equal (IdleWindow.parse "45" |> expect) (Some (TimeSpan.FromSeconds 45.0)) "no unit"

        // The misreading that matters: 30 seconds where 30 minutes was meant would reap
        // sessions out from under people, and would look like a bug in the reaper.
        testCase "an unknown or malformed duration is rejected, not guessed at" <| fun () ->
            Expect.isError (IdleWindow.parse "30x") "unknown unit"
            Expect.isError (IdleWindow.parse "half an hour") "prose"
            Expect.isError (IdleWindow.parse "m") "unit with no number"
            Expect.isError (IdleWindow.parse "30 m") "embedded space"
            Expect.isError (IdleWindow.parse "-5m") "negative"

        testCase "zero is rejected — off already has a spelling" <| fun () ->
            Expect.isError (IdleWindow.parse "0") "bare zero"
            Expect.isError (IdleWindow.parse "0m") "zero minutes"
    ]

let tests = testList "Idle reaping (Plan 11)" [ tests'; windowTests ]
