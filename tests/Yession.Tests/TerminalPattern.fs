module Yession.Tests.TerminalPattern

// The pattern subset a terminal wait accepts (Plan 25, step 9).
//
// What is under test is not "does this behave like a regex" — it deliberately does not, in
// ways the refusals name. It is two promises: that the supported constructs mean what a
// caller expects, and that NO pattern can cost more than linear time. The second is the whole
// reason this exists instead of a `RegExp`, and it has a test that would hang rather than
// fail if it were untrue.

open Fable.Pyxpecto
open Yession.Domain

let private compiled (pattern: string) =
    match TerminalPattern.compile pattern with
    | Ok compiled -> compiled
    | Error reason -> failwithf "expected %s to compile, got: %s" pattern reason

let private matches (pattern: string) (text: string) =
    match TerminalPattern.matches (compiled pattern) text with
    | Ok answer -> answer
    | Error fault -> failwithf "the matcher would not answer: %s" fault

let private refusal (pattern: string) =
    match TerminalPattern.compile pattern with
    | Ok _ -> failwithf "expected %s to be refused" pattern
    | Error reason -> reason

let tests =
    testList "Terminal wait patterns (Plan 25)" [

        // The property that justifies the whole approach, and the reason it is not a RegExp.
        // A backtracking engine takes exponential time on this: 30 `a`s and no `b` is billions
        // of paths, and on Node there is no match timeout to stop it — it would wedge the
        // event loop that owns every terminal in the session. Simulating all threads at once
        // makes it one linear pass. If this ever regresses, the suite HANGS rather than fails,
        // which is itself the signal.
        testCase "a pattern that would hang a backtracking engine simply answers" <| fun () ->
            let evil = String.replicate 30 "a"
            Expect.isFalse (matches "(a+)+$" (evil + "b")) "no match, and it came back to say so"
            Expect.isTrue (matches "(a+)+$" evil) "and it still matches what it should"

        testCase "literal text matches where it appears" <| fun () ->
            Expect.isTrue (matches "login: " "U-Boot 2024.01\nlogin: ") "anywhere in the text"
            Expect.isFalse (matches "login: " "U-Boot 2024.01\n") "and not where it does not"

        testCase "a dot matches any character except a newline" <| fun () ->
            // A console is lines. A `.` that ate them would let `a.b` match across a screen.
            Expect.isTrue (matches "a.c" "abc") "within a line"
            Expect.isFalse (matches "a.c" "a\nc") "never across one"

        testCase "a character class matches its members, and a negated one everything else" <| fun () ->
            Expect.isTrue (matches "[#$>] $" "root@box:~# ") "the shell prompt case this exists for"
            Expect.isTrue (matches "[a-z0-9_]+" "abc_123") "ranges compose"
            Expect.isFalse (matches "[^0-9]" "5") "a negated class excludes"
            Expect.isTrue (matches "[^0-9]" "x") "and admits the rest"

        testCase "quantifiers repeat what precedes them" <| fun () ->
            Expect.isTrue (matches "ab*c" "ac") "star takes none"
            Expect.isTrue (matches "ab*c" "abbbc") "or many"
            Expect.isFalse (matches "ab+c" "ac") "plus needs one"
            Expect.isTrue (matches "colou?r" "color") "and optional takes either"
            Expect.isTrue (matches "colou?r" "colour") "spelling"

        testCase "alternation takes either branch" <| fun () ->
            Expect.isTrue (matches "yes|no" "no") "the second"
            Expect.isTrue (matches "(cat|dog)s" "dogs") "grouped, with what follows applying to both"
            Expect.isFalse (matches "(cat|dog)s" "dog") "and what follows is still required"

        testCase "anchors bind to a line, not to the whole transcript" <| fun () ->
            // The distinction that makes them usable on a console: a `^` meaning "start of
            // everything this device ever said" would match once, ever.
            Expect.isTrue (matches "^login: " "boot\nlogin: ") "a line start, mid-transcript"
            Expect.isFalse (matches "^login: " "boot login: ") "and not mid-line"
            Expect.isTrue (matches "done$" "done\nmore") "a line end, mid-transcript"

        testCase "an escape means the character it names" <| fun () ->
            Expect.isTrue (matches "U-Boot \\d+\\.\\d+" "U-Boot 2024.01") "digits and an escaped dot"
            Expect.isFalse (matches "\\d" "x") "a digit class is digits"

        // Each refusal NAMES the construct. A caller told "unsupported pattern" has to guess
        // which part; one told "a backreference" has learned the rule.
        testCase "a backreference is refused, and called one" <| fun () ->
            Expect.stringContains (refusal "(a)\\1") "backreference" "named"

        testCase "a lookahead is refused, and called one" <| fun () ->
            Expect.stringContains (refusal "foo(?=bar)") "lookahead" "named"

        testCase "a lookbehind is refused, and called one" <| fun () ->
            Expect.stringContains (refusal "(?<=foo)bar") "lookbehind" "named"

        testCase "a non-greedy quantifier is refused, and called one" <| fun () ->
            Expect.stringContains (refusal "a+?") "non-greedy" "named"

        testCase "a refusal says what the subset IS, not only what it is not" <| fun () ->
            // Half a refusal sends a caller back to guessing. The other half is the menu.
            let reason = refusal "(a)\\1"
            Expect.stringContains reason "`*`" "the quantifiers it does take"
            Expect.stringContains reason "anchors" "and the anchors"

        testCase "a malformed pattern is an answer, never a crash" <| fun () ->
            Expect.stringContains (refusal "(unclosed") "never closed" "a group left open"
            Expect.stringContains (refusal "[a-z") "never closed" "a class left open"
            Expect.stringContains (refusal "") "empty" "and nothing at all"

        // The guard for a bug in THIS code rather than in a caller's pattern. A budget that
        // only ever runs in a scenario we have engineered to be impossible is untested code,
        // so it is exercised directly: a budget too small to finish must produce the fault,
        // not a wrong answer.
        //
        // It matters that this is a fault rather than "no match". A matcher that gave up and
        // said false would look exactly like a device that never spoke, and would send
        // somebody to check their board instead of this file.
        testCase "a matcher that runs past its budget says so, rather than answering wrongly" <| fun () ->
            let program = compiled "abc"
            match TerminalPattern.matchesWithin 2 program "abc" with
            | Ok answer -> failwithf "expected the budget to stop it, got %b" answer
            | Error fault ->
                Expect.stringContains fault "linear" "it names the property that was broken"
                Expect.stringContains fault "the matcher was" "and says whose fault it is"

        testCase "a budget the simulation can live within answers normally" <| fun () ->
            // The other half: a guard that fired early would turn every honest match into a
            // fault, and the case above could not tell the difference.
            match TerminalPattern.matchesWithin 100000 (compiled "abc") "xxabcxx" with
            | Ok answer -> Expect.isTrue answer "found, well inside the budget"
            | Error fault -> failwithf "the budget stopped an ordinary match: %s" fault

        testCase "a pattern longer than the cap is refused rather than run" <| fun () ->
            // Linear in the pattern as well as the input, so the bound is what keeps the work
            // per character something a person can reason about.
            let huge = String.replicate (TerminalPattern.MaxLength + 1) "a"
            Expect.stringContains (refusal huge) "longer than" "and says so"
    ]
