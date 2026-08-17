module Yession.Tests.TestSources

// What this test suite is allowed to do to the process it runs in.
//
// One rule, and it exists because breaking it is invisible. The suite is ONE process, so a
// case that writes `process.env` writes it for every case compiled after it — and the
// half everyone forgets is putting it back. `Phase2`'s credential-leak regression planted
// `ANTHROPIC_API_KEY` and DELETED it on the way out, which is not a restore, it is a clear:
// every `LiveAgent` suite after it ran with no credential, `SessionMain` answers no credential
// by starting NO AGENT, and the live clone case got a session that accepted a message and
// never replied. No error. Nothing anywhere saying why. Four attempts to find it.
//
// `Support.withEnv` made take-and-give-back available. This makes it the only way, and the
// reason the check lives HERE rather than in a witness at the point of use is where the two
// halves happen: the mutation is in the cheap tier, and only its consequence needs
// `LiveAgent`. A guard on the consequence fires on a tier almost nobody runs, months later.
// A guard on the source fires on the pull request that writes it.
//
// No capability: it reads files. `NixSource` is the other source contract in this suite and
// needs `Nix` because it evaluates the derivation; there is nothing to evaluate here.

open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto
open System.Text.RegularExpressions

let private nodeFs : obj = importAll "node:fs"

[<Emit("$0.readdirSync($1).filter(n => n.endsWith('.fs'))")>]
let private fsharpFilesIn (fs: obj) (dir: string) : string array = jsNative

[<Emit("$0.readFileSync($1, 'utf8')")>]
let private readText (fs: obj) (path: string) : string = jsNative

let private suiteDir = "tests/Yession.Tests"

/// The one file that may write the environment, because it owns the verb that gives it back.
let private owner = "Support.fs"

// Assembled rather than written out, so this file does not match its own patterns and need
// exempting from its own scan — an exemption is a hole, and this way there is none. For the
// same reason the notes below name the SHAPE rather than quoting it: the scan is honest enough
// to catch its own documentation, which is how these four lines got written this way.
let private envWrites : Regex list =
    let env = "process" + @"\." + "env"
    [ Regex (env + @"\s*\[[^\]]*\]\s*=[^=]")                    // assignment through a subscript
      Regex (env + @"\.\w+\s*=[^=]")                            // assignment to a named member
      Regex (@"delete\s+" + env)                                // the delete form
      Regex ("Environment" + @"\." + "SetEnvironmentVariable") ] // the .NET CLR half of both

/// Every file that writes the process env, by name.
let private writersIn (dir: string) : (string * string) list =
    fsharpFilesIn nodeFs dir
    |> Array.toList
    |> List.collect (fun name ->
        let text = readText nodeFs (sprintf "%s/%s" dir name)
        text.Split '\n'
        |> Array.toList
        |> List.filter (fun line -> envWrites |> List.exists (fun r -> r.IsMatch line))
        |> List.map (fun line -> name, line.Trim ()))

let tests =
    testList "Test sources" [
        // Anti-vacuity, and the reason this suite can be believed: a scan that matches nothing
        // passes whether the contract holds or the detector is broken, and those look identical
        // in a green run. Support.fs is the known positive — if the patterns stop seeing ITS
        // writes, this goes red before the contract below can quietly stop testing anything.
        testCase "the scan can see an env write at all" <| fun () ->
            let ownWrites = writersIn suiteDir |> List.filter (fun (name, _) -> name = owner)
            Expect.isTrue
                (List.length ownWrites >= 2)
                (sprintf
                    "%s writes the env (a set and an unset at least) — matching none of them means the patterns have stopped seeing writes, not that the contract holds"
                    owner)

        testCase "no test file but Support.fs writes the process env" <| fun () ->
            let offenders = writersIn suiteDir |> List.filter (fun (name, _) -> name <> owner)
            let described =
                offenders
                |> List.map (fun (name, line) -> sprintf "  %s: %s" name line)
                |> String.concat "\n"
            Expect.isEmpty
                offenders
                (sprintf
                    "these write the process env directly, which the next suite in the same process inherits:\n%s\nUse `Support.withEnv [ \"NAME\", Some \"value\" ] (fun () -> async { … })` — it puts back exactly what was there, absence included, on the exceptional path too."
                    described)
    ]
