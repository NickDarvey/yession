module Yession.Tests.EmitSources

// What an `[<Emit(...)>]` body is allowed to assume about its substitutions.
//
// One rule, and it exists because breaking it is invisible until it is expensive. Fable
// substitutes `$0`, `$1`, … with the caller's argument TEXT, not a value. Two things follow
// that nobody expects while writing JS inside a string:
//
//   * a declaration can COLLIDE with the caller's own variable. `const pc = $0` at a call site
//     that passes a variable named `pc` emits `const pc = pc` — a temporal dead zone
//     ReferenceError. It took the whole shell down and reported as eight browser cases timing
//     out with nothing in common; the fixture's captured `pageerror` is the only reason it was
//     found in minutes rather than days.
//   * a repeated placeholder EVALUATES its argument again. `$0` written three times evaluated a
//     fresh peer-id mint three times, so a first visit stored one id and returned another, and
//     every peer-scoped call was denied for the life of that launch.
//
// Both were written down as comments beside the emits that caused them. That is what we had
// instead of a check, and it did not work: the emit in `examples/serial/src/Ws.fs` broke the
// rule its own comment stated, twice, and one of those was a live error.
//
// The rule makes both unrepresentable rather than describing them. When the substitutions
// arrive as real function parameters, a parameter binds INSIDE the function while the argument
// is evaluated OUTSIDE it, exactly once — so nothing inside can collide with a caller's
// identifier however it is spelled, and nothing is evaluated twice:
//
//     (() => { const peer = $0; ... })()     ->     (function (pc) { ... })($0)
//
// It applies only to an emit that actually substitutes something. With no `$n` there is no
// text to collide with and nothing to evaluate twice, so a body that takes no arguments is
// free to declare whatever it likes.
//
// No capability: it reads files. `TestSources` is the other contract of this shape and says
// the same thing about why a guard on the SOURCE beats a guard on the consequence — this one
// fires on the pull request that writes the emit, not on the tier that eventually runs it.

open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto
open System.Text.RegularExpressions

let private nodeFs : obj = importAll "node:fs"

// Node 20+ walks a tree for us; the repo pins 24. One expression, no declarations, nothing
// repeated — this file obeys the rule it enforces.
[<Emit("$0.readdirSync($1, { recursive: true }).filter(n => n.endsWith('.fs'))")>]
let private fsharpFilesUnder (fs: obj) (root: string) : string array = jsNative

[<Emit("$0.readFileSync($1, 'utf8')")>]
let private readText (fs: obj) (path: string) : string = jsNative

/// Where first-party F# lives. `examples/` is in, and not as an afterthought: one of the two
/// collisions this rule found was there, and it was the live one.
let private roots = [ "app"; "src"; "tests"; "examples" ]

/// Build output and vendored code. The Fable libraries under `fable_modules` carry hundreds of
/// emits that are not ours to hold to anything.
let private generated = [ "/out/"; "/obj/"; "/bin/"; "fable_modules"; "node_modules" ]

// Assembled rather than written out, so this file does not match its own patterns and need
// exempting from its own scan — an exemption is a hole, and this way there is none. It is also
// the only way to write the fixtures below: a violating emit quoted literally here would BE a
// violating emit in a scanned file.
let private q = "\""
let private triple = q + q + q

let private emitPattern =
    @"\[<(?:Fable\.Core\.)?Emit\(\s*(?:"
    + triple + "(.*?)" + triple
    + @"|" + q + @"((?:[^" + q + @"\\]|\\.)*)" + q
    + @")\s*\)>\]"

let private emits = Regex (emitPattern, RegexOptions.Singleline)

/// A JS binding form. `function`/`class` are here because a named function declaration shadows
/// exactly as a `const` does.
let private declaration = Regex (@"\b(?:const|let|var|function|class)\s+[A-Za-z_$][\w$]*")

/// The safe shape: the substitutions arrive as parameters of a real function.
let private parameterised = Regex (@"\(\s*(?:async\s+)?function\s*\([^)]*[A-Za-z_$]")

let private placeholder = Regex (@"\$(\d+)")

/// JS comments are prose, and prose about this rule necessarily contains examples of breaking
/// it. Strip them before looking for declarations — without this the check goes red on the very
/// comment that warns about the thing.
let private withoutComments (js: string) =
    let noBlocks = Regex.Replace (js, @"/\*.*?\*/", "", RegexOptions.Singleline)
    Regex.Replace (noBlocks, @"//[^\n]*", "")

/// The placeholders a body substitutes, and how often each appears.
let private substitutions (body: string) =
    placeholder.Matches body
    |> Seq.map (fun m -> m.Groups.[1].Value)
    |> Seq.countBy id
    |> List.ofSeq

let private declares (body: string) = declaration.IsMatch (withoutComments body)
let private repeats (body: string) = substitutions body |> List.exists (fun (_, n) -> n > 1)
let private safe (body: string) = parameterised.IsMatch body

/// Does this body take a risk the parameterised form would remove?
let private hazardous (body: string) =
    not (List.isEmpty (substitutions body)) && not (safe body) && (declares body || repeats body)

type private Emit = { File : string; Line : int; Body : string }

let private emitsIn (root: string) : Emit list =
    fsharpFilesUnder nodeFs root
    |> Array.toList
    |> List.map (fun name -> sprintf "%s/%s" root name)
    |> List.filter (fun path -> generated |> List.forall (fun bad -> not (path.Contains bad)))
    |> List.collect (fun path ->
        let text = readText nodeFs path
        emits.Matches text
        |> Seq.map (fun m ->
            let body = if m.Groups.[1].Success then m.Groups.[1].Value else m.Groups.[2].Value
            { File = path
              Line = (text.Substring (0, m.Index)).Split('\n').Length
              Body = body })
        |> List.ofSeq)

let private allEmits = lazy (roots |> List.collect emitsIn)

let private describe (found: Emit list) =
    found
    |> List.map (fun e -> sprintf "  %s:%d" e.File e.Line)
    |> String.concat "\n"

let private remedy =
    "Take the substitutions as parameters of a real function, so a parameter binds inside it "
    + "while the argument is evaluated outside it, once:\n"
    + "  (function (peer, onDead) { ... })($0, $1)"

// The fixtures, assembled for the reason the patterns are: written out, they would be real
// violations in a real scanned file, and this suite would fail on itself.
let private shadowingFixture = "(() => { const peer = " + "$0" + "; return peer.x })()"
let private repeatingFixture = "$0" + ".a && " + "$0" + ".b"
// No placeholder in the comment: a `$n` there is substituted like any other text, so one
// written twice really is two substitutions and the repetition rule is right to say so. What
// this fixture isolates is the DECLARATION being prose.
let private commentedFixture = "// const peer = someCaller\n" + "$0" + ".x"

let tests =
    testList "Emit bodies" [
        // Anti-vacuity, and the reason this suite can be believed: a scan that matches nothing
        // passes whether the contract holds or the regex has stopped seeing emits, and those
        // look identical in a green run.
        testCase "the scan can see emit bodies at all" <| fun () ->
            Expect.isTrue
                (List.length allEmits.Value > 300)
                (sprintf
                    "found only %d emit bodies across %s — matching none of them would mean the pattern has stopped seeing emits, not that the contract holds"
                    (List.length allEmits.Value) (String.concat ", " roots))

        testCase "the detector still recognises a body that declares against its substitution" <| fun () ->
            Expect.isTrue (hazardous shadowingFixture) "the shadowing shape must still be detected"

        testCase "the detector still recognises a body that repeats a substitution" <| fun () ->
            Expect.isTrue (hazardous repeatingFixture) "the repetition shape must still be detected"

        testCase "a declaration inside a JS comment is not a declaration" <| fun () ->
            // Prose warning about this rule contains examples of breaking it; `Ws.fs` carries
            // one. Reading a comment as code would make the warning itself the violation.
            Expect.isFalse
                (hazardous commentedFixture)
                "a commented-out declaration is prose, not a binding"

        testCase "an emit that declares a JS binding takes its substitutions as parameters" <| fun () ->
            let offenders =
                allEmits.Value
                |> List.filter (fun e ->
                    not (List.isEmpty (substitutions e.Body)) && not (safe e.Body) && declares e.Body)
            Expect.isEmpty
                offenders
                (sprintf
                    "these declare a JS binding while substituting the caller's text, so a declaration can collide with the caller's own variable and emit `const x = x`:\n%s\n%s"
                    (describe offenders) remedy)

        testCase "an emit that repeats a substitution takes its substitutions as parameters" <| fun () ->
            let offenders =
                allEmits.Value
                |> List.filter (fun e -> not (safe e.Body) && repeats e.Body)
            Expect.isEmpty
                offenders
                (sprintf
                    "these use one placeholder more than once, so the caller's argument expression is evaluated more than once:\n%s\n%s"
                    (describe offenders) remedy)
    ]
