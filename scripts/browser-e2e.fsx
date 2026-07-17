// Real-browser E2E (the F# replacement for tests/browser/run.mjs). Launches two Chromium
// peers via the Microsoft.Playwright .NET driver against a real Session Process
// (app/out/Main.js), verifies they converge over native WebRTC and see the sent message in
// both timelines, then proves client-side IndexedDB persistence by wiping the server and
// reloading (the draft can only return from the browser), and that the doc store is
// session-keyed. Event-driven throughout (WaitForFunctionAsync); one overall watchdog.
//
// Browsers are NOT installed here — ExecutablePath points at a discovered Chromium
// (CHROMIUM_PATH override, else PLAYWRIGHT_BROWSERS_PATH/'/opt/pw-browsers', else /usr/bin).
// The browser-evaluated predicate strings below are JS by necessity — they run inside
// Chromium via CDP, exactly as in any Playwright binding.
//
//     dotnet fsi scripts/browser-e2e.fsx

#r "nuget: Microsoft.Playwright, 1.61.0"

open System
open System.IO
open System.Diagnostics
open System.Threading.Tasks
open Microsoft.Playwright

let PORT = 8180
let BASE = sprintf "http://127.0.0.1:%d/" PORT
let dataDir = "tests/browser/.data"

// --- Chromium discovery (ported from run.mjs) ------------------------------------------
let chromiumPath () : string =
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
        | None ->
            eprintfn "no Chromium found; set CHROMIUM_PATH"
            exit 4

// --- Host spawn / readiness (ported): the real product entry on a test port ------------
let mutable host : Process = null

let startHost () : unit =
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

// --- The test flow ---------------------------------------------------------------------
let run () : Task =
    task {
        let! playwright = Playwright.CreateAsync ()
        let! browser =
            playwright.Chromium.LaunchAsync (
                BrowserTypeLaunchOptions (
                    ExecutablePath = chromiumPath (),
                    // Headless sandboxes stall ICE gathering when host candidates hide behind mDNS.
                    Args = [| "--disable-features=WebRtcHideLocalIpsWithMdns" |]))
        try
            let! pageA = browser.NewPageAsync ()
            let! pageB = browser.NewPageAsync ()
            let! _ = pageA.GotoAsync BASE
            let! _ = pageB.GotoAsync BASE

            // Both browser peers reach Connected over native WebRTC.
            let connected = """document.querySelector('[data-connection]')?.textContent === 'Connected'"""
            let! _ = pageA.WaitForFunctionAsync connected
            let! _ = pageB.WaitForFunctionAsync connected

            // A types in its always-present composer; B converges (renders as a peer's draft).
            let! _ = pageA.WaitForSelectorAsync "textarea[data-draft-input]"
            do! pageA.FillAsync ("textarea[data-draft-input]", "hello from a real browser")
            let! _ = pageB.WaitForFunctionAsync """[...document.querySelectorAll('textarea[data-draft-input]')].some(t => t.value === 'hello from a real browser')"""

            // A sends; both timelines show the immutable message (from events, not Yjs).
            do! pageA.ClickAsync "[data-send-draft]"
            let inTimeline = """[...document.querySelectorAll('[data-conversation] [data-message-body]')].some(m => m.textContent === 'hello from a real browser')"""
            let! _ = pageA.WaitForFunctionAsync inTimeline
            let! _ = pageB.WaitForFunctionAsync inTimeline
            printfn "browser E2E: two real browser peers converged and saw the sent message"

            // Client-side persistence (Step 20): A types a NEW draft (its composer cleared
            // when the first one sent), then the server is killed and its data wiped. After
            // A reloads against the fresh server, the draft can only have come back from the
            // browser's IndexedDB — and it re-syncs to B via the server.
            let! _ = pageA.WaitForFunctionAsync """document.querySelectorAll('textarea[data-draft-input]').length === 1"""
            do! pageA.FillAsync ("textarea[data-draft-input]", "persisted in the browser")
            let! _ = pageA.WaitForFunctionAsync """[...document.querySelectorAll('textarea[data-draft-input]')].some(t => t.value === 'persisted in the browser')"""

            host.Kill true
            host.WaitForExit ()
            if Directory.Exists dataDir then Directory.Delete (dataDir, true)
            startHost ()

            let! _ = pageA.ReloadAsync ()
            let! _ = pageA.WaitForFunctionAsync connected
            let! _ = pageA.WaitForFunctionAsync """[...document.querySelectorAll('textarea[data-draft-input]')].some(t => t.value === 'persisted in the browser')"""

            let! _ = pageB.ReloadAsync ()
            let! _ = pageB.WaitForFunctionAsync connected
            let! _ = pageB.WaitForFunctionAsync """[...document.querySelectorAll('textarea[data-draft-input]')].some(t => t.value === 'persisted in the browser')"""
            printfn "browser E2E: the draft survived a full server wipe via the browser doc store"

            // The store is keyed by SESSION (embedded in the served page), not by address.
            let! sessionId =
                pageA.EvaluateAsync<string> ("""() => document.querySelector('meta[name="yession-session"]')?.getAttribute('content')""")
            if String.IsNullOrEmpty sessionId then failwith "the bootstrap page must embed the session id"
            let! dbNames =
                pageA.EvaluateAsync<string[]> ("""() => indexedDB.databases().then(dbs => dbs.map(d => d.name))""")
            if not (Array.contains (sprintf "yession/session/%s" sessionId) dbNames) then
                failwithf "expected a session-keyed doc store, found: %s" (String.Join (", ", dbNames))
            printfn "browser E2E: the doc store is keyed by session (%s)" sessionId
        finally
            browser.CloseAsync().GetAwaiter().GetResult ()
            try host.Kill true with _ -> ()
    }

// --- Watchdog + entry ------------------------------------------------------------------
if Directory.Exists dataDir then Directory.Delete (dataDir, true)
startHost ()

let flow = run ()
let completed = Task.WaitAny [| flow; Task.Delay 120_000 |]
if completed = 1 then
    eprintfn "browser E2E watchdog fired after 120000ms"
    (try host.Kill true with _ -> ())
    exit 3

flow.GetAwaiter().GetResult ()   // rethrow any assertion failure → non-zero exit
exit 0
