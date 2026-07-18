module Yession.Tests.Browser

// The real-browser E2E, as a Pyxpecto suite (the F# replacement for scripts/browser-e2e.fsx).
// Pyxpecto is multi-runtime: this file compiles for BOTH targets, but the browser flow only
// exists on the .NET CLR, where the Microsoft.Playwright driver lives. Under Fable (JS on
// Node) there is no Playwright, so the flow is `#if`-compiled out and a single visible case
// records where it moved. Run it with:
//
//     dotnet run --project tests/Yession.Tests/Yession.Tests.fsproj
//
// It launches two Chromium peers against a real Session Process (app/out/Main.js), verifies
// Markdown typed into the rich composer renders as formatted rich text (input rules), that it
// converges over native WebRTC, and that sending serializes it back to Markdown in both
// timelines; then proves client-side IndexedDB persistence by wiping the server and reloading
// (the draft can only return from the browser), and that the doc store is session-keyed.
// Event-driven throughout (WaitForFunctionAsync); Playwright's own per-action timeouts watch.

open Fable.Pyxpecto

#if !FABLE_COMPILER

open System
open System.IO
open System.Diagnostics
open System.Threading.Tasks
open Microsoft.Playwright

let private PORT = 8180
let private BASE = sprintf "http://127.0.0.1:%d/" PORT
let private dataDir = "tests/browser/.data"

// --- Chromium discovery (ported from browser-e2e.fsx) ----------------------------------
let private chromiumPath () : string =
    let env name =
        match Environment.GetEnvironmentVariable name with
        | null | "" -> None
        | v -> Some v
    match env "CHROMIUM_PATH" with
    | Some p -> p
    | None ->
        let root = env "PLAYWRIGHT_BROWSERS_PATH" |> Option.defaultValue "/opt/pw-browsers"
        let fromRoot =
            if Directory.Exists root then
                try Directory.EnumerateFiles (root, "chrome", SearchOption.AllDirectories) |> Seq.toList
                with _ -> []
            else []
        let candidates = fromRoot @ [ "/usr/bin/chromium"; "/usr/bin/chromium-browser"; "/usr/bin/google-chrome" ]
        match candidates |> List.tryFind File.Exists with
        | Some c -> c
        | None -> failwith "no Chromium found; set CHROMIUM_PATH"

// --- Host spawn / readiness (ported): the real product entry on a test port -------------
let mutable private host : Process = null

let private startHost () : unit =
    let psi = ProcessStartInfo "node"
    psi.ArgumentList.Add "app/out/Main.js"
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true   // stderr inherits → visible in the log
    psi.EnvironmentVariables.["YESSION_PORT"] <- string PORT
    psi.EnvironmentVariables.["YESSION_DATA_DIR"] <- dataDir
    let p = new Process (StartInfo = psi)
    let ready = TaskCompletionSource<bool> ()
    // Keep draining stdout (like the JS 'data' handler) so the pipe never blocks the host;
    // resolve readiness on the "launched at" line.
    p.OutputDataReceived.Add (fun e ->
        if e.Data <> null && e.Data.Contains "launched at" then ready.TrySetResult true |> ignore)
    p.Start () |> ignore
    p.BeginOutputReadLine ()
    host <- p
    if not (ready.Task.Wait 30000) then failwith "host never reported readiness"

let private killHost () : unit =
    try if host <> null then host.Kill true with _ -> ()

// --- Shared browser state across the sequential cases -----------------------------------
let mutable private playwright : IPlaywright = null
let mutable private browser : IBrowser = null
let mutable private pageA : IPage = null
let mutable private pageB : IPage = null

// Task -> Async adapters (this whole file is CLR-only, so Async.AwaitTask is available).
let private await (t: Task<'a>) : Async<'a> = Async.AwaitTask t
let private awaitU (t: Task) : Async<unit> = Async.AwaitTask t

// Browser-evaluated predicate strings: JS by necessity — they run inside Chromium via CDP.
let private connected = """document.querySelector('[data-connection]')?.textContent === 'Connected'"""

// The local peer's rich composer is a ProseMirror editable (`.ProseMirror`) inside the
// editable (`data-rich-readonly="false"`) body-mount host; peers' drafts are read-only mirrors.
let private composer = """[data-rich-readonly="false"] .ProseMirror"""

let tests =
    testList "Browser E2E" [
        testCaseAsync "markdown typed in the rich composer renders formatted, converges, and sends as markdown" <|
            async {
                if Directory.Exists dataDir then Directory.Delete (dataDir, true)
                startHost ()
                let! pw = await (Playwright.CreateAsync ())
                playwright <- pw
                let! b =
                    await (playwright.Chromium.LaunchAsync (
                        BrowserTypeLaunchOptions (
                            ExecutablePath = chromiumPath (),
                            // Headless sandboxes stall ICE gathering when host candidates hide behind mDNS.
                            Args = [| "--disable-features=WebRtcHideLocalIpsWithMdns" |])))
                browser <- b
                let! a = await (browser.NewPageAsync ())
                let! bb = await (browser.NewPageAsync ())
                pageA <- a
                pageB <- bb
                let! _ = await (pageA.GotoAsync BASE)
                let! _ = await (pageB.GotoAsync BASE)

                // Both browser peers reach Connected over native WebRTC.
                let! _ = await (pageA.WaitForFunctionAsync connected)
                let! _ = await (pageB.WaitForFunctionAsync connected)

                // A types Markdown into its rich composer with REAL key events, so the input
                // rules fire: "# " turns the block into a heading rendered live as an <h1> —
                // the syntax itself is never left as literal text (Linear-style WYSIWYG).
                let! _ = await (pageA.WaitForSelectorAsync composer)
                do! awaitU (pageA.ClickAsync composer)
                do! awaitU (pageA.Keyboard.TypeAsync "# Heading one")
                let renderedHeading =
                    """document.querySelector('[data-rich-readonly="false"] .ProseMirror h1')?.textContent === 'Heading one'"""
                let! _ = await (pageA.WaitForFunctionAsync renderedHeading)

                // B converges: it renders A's draft (read-only) as the same formatted heading.
                do! await (pageB.WaitForFunctionAsync
                            """[...document.querySelectorAll('.ProseMirror h1')].some(h => h.textContent === 'Heading one')""") |> Async.Ignore

                // A sends; both timelines show the immutable message — serialized back to
                // MARKDOWN (`# Heading one`), from events (not Yjs).
                do! awaitU (pageA.ClickAsync "[data-send-draft]")
                let inTimeline = """[...document.querySelectorAll('[data-conversation] [data-message-body]')].some(m => m.textContent.trim() === '# Heading one')"""
                let! _ = await (pageA.WaitForFunctionAsync inTimeline)
                do! await (pageB.WaitForFunctionAsync inTimeline) |> Async.Ignore
            }

        testCaseAsync "a browser-persisted draft survives a full server wipe" <|
            async {
                // Client-side persistence (Step 20): A types a NEW draft (its composer cleared
                // when the first one sent), then the server is killed and its data wiped. After
                // A reloads against the fresh server, the draft can only have come back from the
                // browser's IndexedDB — and it re-syncs to B via the server.
                let! _ = await (pageA.WaitForFunctionAsync """document.querySelectorAll('[data-rich-readonly="false"] .ProseMirror').length === 1""")
                do! awaitU (pageA.ClickAsync composer)
                do! awaitU (pageA.Keyboard.TypeAsync "persisted in the browser")
                let hasDraft =
                    """[...document.querySelectorAll('.ProseMirror')].some(p => p.textContent === 'persisted in the browser')"""
                let! _ = await (pageA.WaitForFunctionAsync hasDraft)

                host.Kill true
                host.WaitForExit ()
                if Directory.Exists dataDir then Directory.Delete (dataDir, true)
                startHost ()

                let! _ = await (pageA.ReloadAsync ())
                let! _ = await (pageA.WaitForFunctionAsync connected)
                let! _ = await (pageA.WaitForFunctionAsync hasDraft)

                let! _ = await (pageB.ReloadAsync ())
                let! _ = await (pageB.WaitForFunctionAsync connected)
                do! await (pageB.WaitForFunctionAsync hasDraft) |> Async.Ignore
            }

        testCaseAsync "the doc store is keyed by session" <|
            async {
                // The store is keyed by SESSION (embedded in the served page), not by address.
                let! sessionId =
                    await (pageA.EvaluateAsync<string> ("""() => document.querySelector('meta[name="yession-session"]')?.getAttribute('content')"""))
                Expect.isFalse (String.IsNullOrEmpty sessionId) "the bootstrap page must embed the session id"
                let! dbNames =
                    await (pageA.EvaluateAsync<string[]> ("""() => indexedDB.databases().then(dbs => dbs.map(d => d.name))"""))
                Expect.isTrue
                    (Array.contains (sprintf "yession/session/%s" sessionId) dbNames)
                    (sprintf "expected a session-keyed doc store, found: %s" (String.Join (", ", dbNames)))
            }

        testCaseAsync "shut down the browser peers and the host" <|
            async {
                if browser <> null then do! awaitU (browser.CloseAsync ())
                if playwright <> null then playwright.Dispose ()
                killHost ()
                if Directory.Exists dataDir then
                    try Directory.Delete (dataDir, true) with _ -> ()
            }
    ]

#else

// Fable (JS on Node): Playwright is a .NET driver and does not exist here, so the flow above
// is compiled out. This stub only exists so the module compiles under Fable; it is never
// forced — `Tag.needs "Browser E2E" [Browser]` fails on Node and reports the skip itself.
let tests : Fable.Pyxpecto.Model.TestCase = testList "Browser E2E" []

#endif
