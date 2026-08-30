module Yession.Tests.SandboxBackends

// A sandbox backend is read from the environment ONCE, and every other place that needs it
// is handed the value.
//
// The rule exists because the other shape failed silently, in the direction that costs the
// most. `YESSION_SESSION_AGENT_BACKEND` had two readers: `SessionMain` parsed it at boot,
// defaulting to `srt`, and used that to decide the session was confined — it even validated
// srt's tools on the strength of it, fail-closed, so a box without bubblewrap refused to
// start. `Agent.fs` then read the SAME variable again where the CLI is actually spawned,
// defaulting to `host`. Both defaults were written to be right; neither author saw the
// other. A deployment that set nothing therefore ran the agent CLI unconfined while every
// statement the session made about itself — its boot checks, its logs, docs/GAPS.md — said
// srt. Nothing was wrong at either site. The fault was that there were two.
//
// So this is not a style rule about env access. A second reader of a confinement switch is
// a second DEFAULT, and two defaults that disagree resolve into the weaker one without
// anybody choosing it. One reader cannot disagree with itself.
//
// What the fix leaves behind is a value passed downward: `SessionMain` parses, and
// `Agent.runWith` takes a `SandboxBackend` it cannot second-guess. This pins that shape
// against the next person who needs the backend somewhere new and reaches for the
// environment rather than the parameter — which is the cheap thing to do, and reads as
// harmless right up until the two defaults differ.
//
// Comments are exempt: naming the variable in prose is how the rest of the codebase
// explains itself, and a mention cannot decide anything.
//
// No capability: it reads files, like `TestSources` beside it.

open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto

let private nodeFs : obj = importAll "node:fs"

[<Emit("$0.readdirSync($1, { recursive: true }).filter(n => n.endsWith('.fs'))")>]
let private fsharpFilesUnder (fs: obj) (root: string) : string array = jsNative

[<Emit("$0.readFileSync($1, 'utf8')")>]
let private readText (fs: obj) (path: string) : string = jsNative

/// Build output and vendored code: none of it is anybody here's to hold to a rule.
let private generated = [ "/out/"; "/obj/"; "/bin/"; "fable_modules"; "node_modules" ]

/// The product's own source. The suite is excluded deliberately: a test that drives one
/// backend on purpose names it, and that is not a deployment reading configuration.
let private roots = [ "app"; "src" ]

let private sourcesUnder (root: string) =
    fsharpFilesUnder nodeFs root
    |> Array.toList
    |> List.map (fun name -> sprintf "%s/%s" root name)
    |> List.filter (fun path -> generated |> List.forall (fun bad -> not (path.Contains bad)))

/// Everything up to the first `//` on a line. Crude on purpose — it can only ever ignore a
/// read, never invent one, so the rule stays conservative.
let private code (line: string) =
    match line.IndexOf "//" with
    | -1 -> line
    | at -> line.Substring (0, at)

/// The files that name the variable in code, one entry PER SITE — so two reads in one file
/// show up as two entries and a failure names the files that disagree. Deliberately not
/// line numbers: this must go red when a second reader appears, and never merely because
/// somebody added a line above the first one.
let private readsOf (variable: string) =
    let needle = sprintf "\"%s\"" variable
    [ for path in List.collect sourcesUnder roots do
        let text = readText nodeFs path
        for line in text.Split '\n' do
            if (code line).Contains needle then yield path ]

let tests =
    testList "a sandbox backend" [
        testCase "the agent backend is read in one place" <| fun () ->
            let sites = readsOf "YESSION_SESSION_AGENT_BACKEND"
            Expect.equal
                sites
                [ "app/SessionMain.fs" ]
                "the agent CLI's confinement is decided once, at boot, and passed to `Agent.runWith`; a second reader is a second default, and last time the two disagreed the CLI ran unconfined"

        testCase "the work backend is read in one place" <| fun () ->
            let sites = readsOf "YESSION_SESSION_WORK_BACKEND"
            Expect.equal
                sites
                [ "app/SessionMain.fs" ]
                "the work sandbox's confinement is decided once, at boot, and passed to whoever creates a sandbox"
    ]
