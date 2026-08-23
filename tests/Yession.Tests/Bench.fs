module Yession.Tests.Bench

// What a person waits for, measured — and written down so the next release can be compared to
// this one.
//
// `check` proves behaviour and says nothing about time. That is the right division, but it
// leaves a whole class of change invisible: #214 replaced a 150ms caret debounce with frame
// pacing, and the reconciliation y-prosemirror runs on every caret push (`view.update` is not
// gated on `docChanged`, so it walks the WHOLE document back into Yjs) went from occasional to
// once a frame. Nothing in the repository could see that, and nothing would have seen it get
// worse.
//
// Three latencies and one budget, each swept across document SIZES. The sweep is the point:
// the concern is O(document), and a number taken at one size cannot show a complexity
// regression at all. What comes out is `dist/bench.json`; `tasks.fsx` judges it against the
// recorded history and charts it.
//
// This is a measuring run, not an asserting one. The ONLY thing this suite fails on is being
// unable to take its measurement — a page that did not load, a scenario that returned nothing.
// Judging a number against a threshold is `bench-guard`'s job, deliberately: a suite that
// failed on wall clock would be the flaky test this repo warns about, on every box that is not
// the one the baseline came from.

open Fable.Pyxpecto

#if !FABLE_COMPILER

open System
open System.IO
open System.Text.Json
open Microsoft.Playwright

open Yession.Tests.Browser

/// Its own port, away from the E2E's block: the benchmark and the gate can then run at the
/// same time on one box without one binding the other's listener out from under it.
let private BENCH_PORT = 8200

/// The sweep. Small enough to be a short message, large enough to be a long one, and an order
/// of magnitude between them so a linear cost is unmistakable in the ratio.
let private sizes = [ 200; 2_000; 20_000 ]

/// Samples per metric per size. The first few are discarded — a cold JIT and an unwarmed
/// layout are not what anybody experiences after the first keystroke.
let private samples = 30
let private warmup = 5

/// Percentile of a sorted-on-demand series, nearest-rank. p50 is what the guard watches (the
/// tail is where a shared runner's noise lives); p95 is recorded because the tail is what a
/// person actually notices.
let private percentile (p: float) (xs: float list) : float =
    match xs with
    | [] -> nan
    | _ ->
        let sorted = List.sort xs
        let rank = int (ceil (p * float sorted.Length)) - 1
        sorted.[max 0 (min (sorted.Length - 1) rank)]

/// A measured series, named for what a person was waiting for.
type private Series = { Metric : string; Size : int; Values : float list }

let private seriesFrom (json: string) (size: int) (names: (string * string) list) : Series list =
    use doc = JsonDocument.Parse json
    [ for (key, metric) in names do
        let all =
            doc.RootElement.GetProperty(key).EnumerateArray ()
            |> Seq.map (fun e -> e.GetDouble ())
            |> List.ofSeq
        // Drop the warm-up, but never everything: a series that came back short is better
        // reported short — the sample-count check below is what refuses to report it at all.
        let values = if List.length all > warmup then List.skip warmup all else all
        { Metric = metric; Size = size; Values = values } ]

/// `dist/bench.json`, in github-action-benchmark's `customSmallerIsBetter` shape — so the
/// history is readable by that tool if this repo ever wants its chart instead of ours, and so
/// the schema was designed by somebody who had already thought about it.
let private writeReport (all: Series list) (slope: float) =
    let dist = Path.Combine (Directory.GetCurrentDirectory (), "dist")
    Directory.CreateDirectory dist |> ignore
    use stream = File.Create (Path.Combine (dist, "bench.json"))
    use w = new Utf8JsonWriter (stream, JsonWriterOptions (Indented = true))
    w.WriteStartArray ()
    let point (name: string) (unit: string) (value: float) (extra: string) =
        w.WriteStartObject ()
        w.WriteString ("name", name)
        w.WriteString ("unit", unit)
        w.WriteNumber ("value", Math.Round (value, 3))
        w.WriteString ("extra", extra)
        w.WriteEndObject ()
    for s in all do
        for (p, label) in [ 0.5, "p50"; 0.95, "p95" ] do
            point
                (sprintf "%s.%s@%d" s.Metric label s.Size)
                "ms"
                (percentile p s.Values)
                (sprintf "%d samples" (List.length s.Values))
    // The one number that barely moves with the hardware it was taken on, and the only one
    // that answers the question this suite was built for: is the caret push's cost growing
    // with the document? Linear puts it near 100; a fix upstream would collapse it toward 1.
    point "caret.push.slope" "x" slope (sprintf "%d/%d chars" (List.max sizes) (List.min sizes))
    w.WriteEndArray ()
    w.Flush ()

let private table (all: Series list) (slope: float) =
    printfn ""
    printfn "  %-16s %8s %9s %9s" "metric" "chars" "p50 (ms)" "p95 (ms)"
    for s in all do
        printfn
            "  %-16s %8d %9.2f %9.2f"
            s.Metric s.Size (percentile 0.5 s.Values) (percentile 0.95 s.Values)
    printfn "  %-16s %8s %9.1fx" "caret.push slope" "20k/200" slope
    printfn ""

let tests =
    testList "Editor performance" [
        testCaseAsync "the editor and the collaboration path, swept by document size" <|
            async {
                let server = serveStatic harnessRoot BENCH_PORT
                let! pw = await (Playwright.CreateAsync ())
                let! br =
                    await (pw.Chromium.LaunchAsync (
                        BrowserTypeLaunchOptions (ExecutablePath = chromiumPath ())))
                let! page = await (br.NewPageAsync ())
                let evidence = watching page
                page.SetDefaultTimeout 30000.0f

                let body =
                    reporting "Editor performance" page evidence <| async {
                        let! _ = await (page.GotoAsync (sprintf "http://127.0.0.1:%d/" BENCH_PORT))
                        let! _ = await (page.WaitForSelectorAsync "#peer-b .ProseMirror")

                        let collected = ResizeArray<Series> ()
                        for size in sizes do
                            // Seed through the real relay, and check it actually landed: a
                            // sweep whose sizes all ended up the same is a sweep measuring one
                            // size three times, and it would look like a beautifully flat line.
                            let! seeded = await (page.EvaluateAsync<int> (sprintf "() => window.__benchSeed(%d)" size))
                            if seeded < size / 2 then
                                failwithf
                                    "seeding asked for ~%d characters and the co-editor's doc holds %d — the sweep is not sweeping"
                                    size seeded
                            printfn "  seeded %d chars -> co-editor doc holds %d" size seeded

                            // Typing is driven from OUT here, with real key events, so what is
                            // timed includes the browser's own dispatch. One drive yields both
                            // halves of a keystroke: the author's echo and the co-editor's screen.
                            do! awaitU (page.EvaluateAsync "() => window.__benchReset()")
                            do! awaitU (page.ClickAsync "#peer-b .ProseMirror")
                            do! awaitU (page.Keyboard.PressAsync "ControlOrMeta+End")
                            do! awaitU (page.Keyboard.TypeAsync (String.replicate samples "x"))
                            let! typing = await (page.EvaluateAsync<string> "() => window.__benchTyping()")
                            collected.AddRange (seriesFrom typing size [ "type", "type"; "receive", "receive" ])

                            // The caret push, timed twice: the work it sets off, and the wait
                            // until it is on screen.
                            let! carets = await (page.EvaluateAsync<string> (sprintf "() => window.__benchCarets(%d)" samples))
                            collected.AddRange (seriesFrom carets size [ "push", "caret.push"; "paint", "caret.paint" ])

                        let all = List.ofSeq collected
                        let pushAt size =
                            all
                            |> List.tryFind (fun s -> s.Metric = "caret.push" && s.Size = size)
                            |> Option.map (fun s -> percentile 0.5 s.Values)
                            |> Option.defaultValue nan
                        let slope = pushAt (List.max sizes) / pushAt (List.min sizes)

                        // Anti-vacuity. Every one of these has a silent failure that produces a
                        // perfectly plausible report: a scenario that never ran leaves an empty
                        // series, and an empty series percentiles to NaN, which JSON-serialises
                        // and charts as if it were a measurement.
                        for s in all do
                            if List.length s.Values < 5 then
                                failwithf
                                    "%s@%d collected %d samples — too few to take a percentile from, so this report would be fiction"
                                    s.Metric s.Size (List.length s.Values)
                        if Double.IsNaN slope || Double.IsInfinity slope then
                            failwith "the caret.push slope is not a number — one end of the sweep produced nothing"

                        table all slope
                        writeReport all slope
                    }

                // Teardown unconditionally, then re-raise — the shape every browser case here
                // uses, and for the same reason: a listener left bound outlives the run.
                let! outcome = Async.Catch body
                do! awaitU (br.CloseAsync ())
                pw.Dispose ()
                server.Stop ()
                match outcome with
                | Choice1Of2 () -> ()
                | Choice2Of2 e -> raise e
            }
    ]

#else

let tests : Fable.Pyxpecto.Model.TestCase = testList "Editor performance" []

#endif
