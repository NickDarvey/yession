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
// converges over native WebRTC, and that sending — whose durable body is Markdown — renders
// as that same formatted rich text in both timelines; then proves client-side IndexedDB
// persistence by wiping the server and reloading
// (the draft can only return from the browser), and that the doc store is session-keyed.
// Event-driven throughout (WaitForFunctionAsync); Playwright's own per-action timeouts watch.

open Fable.Pyxpecto

#if !FABLE_COMPILER

open System
open System.IO
open System.Net
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
                // (Regression guard: pushing presence decorations on every render used to starve
                // y-prosemirror's rendering of REMOTE content here, so B's mirror stayed blank.)
                do! await (pageB.WaitForFunctionAsync
                            """[...document.querySelectorAll('.ProseMirror h1')].some(h => h.textContent === 'Heading one')""") |> Async.Ignore

                // B also overlays A's live caret in that read-only mirror: A's presence (a base64
                // relative position over the draft body) decodes to a caret widget + name label.
                // This lands just after the content settles (the decoration push is debounced off
                // the active-convergence window). Guards remote BODY cursors end-to-end.
                do! await (pageB.WaitForFunctionAsync
                            """!!document.querySelector('[data-rich-readonly="true"] .pm-caret')""") |> Async.Ignore

                // A sends; both timelines show the immutable message. The durable body is
                // MARKDOWN (`# Heading one`, from events not Yjs), but the timeline RENDERS it as
                // formatted rich text — the same heading the composer showed — so the sent view
                // mirrors the input: an <h1> whose text is "Heading one" (no literal `#`).
                do! awaitU (pageA.ClickAsync "[data-send-draft]")
                let inTimeline = """[...document.querySelectorAll('[data-conversation] [data-message-body] h1')].some(h => h.textContent.trim() === 'Heading one')"""
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

// --- The host-free editor rendering E2E ([Browser], no Native) ---------------------------
// Serves the static harness (app/browser/EditorHarness.fs, esbuilt to tests/browser/out/) and
// drives one Chromium page. No Session Process, no WebRTC — so this runs wherever Chromium
// exists, decoupled from the native node-datachannel addon. It guards exactly what the DOM-free
// cheap tests cannot: the input-rule → live formatting → Markdown round-trip in a real browser.

let private EDITOR_PORT = 8181
let private editorBase = sprintf "http://127.0.0.1:%d/" EDITOR_PORT
let private harnessRoot = "tests/browser"

/// A tiny read-only static file server over `HttpListener` (the harness page + its bundle).
/// Returns the listener so the caller can stop it; requests are served on a background loop.
let private serveStatic (root: string) (port: int) : HttpListener =
    let listener = new HttpListener ()
    listener.Prefixes.Add (sprintf "http://127.0.0.1:%d/" port)
    listener.Start ()
    let rec loop () =
        async {
            match! Async.Catch (listener.GetContextAsync () |> Async.AwaitTask) with
            | Choice1Of2 ctx ->
                let rel = ctx.Request.Url.AbsolutePath.TrimStart '/'
                let rel = if rel = "" then "editor-harness.html" else rel
                let path = Path.Combine (root, rel)
                if File.Exists path then
                    let bytes = File.ReadAllBytes path
                    ctx.Response.ContentType <-
                        if path.EndsWith ".js" then "text/javascript"
                        elif path.EndsWith ".html" then "text/html"
                        else "application/octet-stream"
                    ctx.Response.OutputStream.Write (bytes, 0, bytes.Length)
                else ctx.Response.StatusCode <- 404
                ctx.Response.Close ()
                return! loop ()
            | Choice2Of2 _ -> ()   // listener stopped
        }
    Async.Start (loop ())
    listener

let editorTests =
    testList "Editor rendering (browser)" [
        testCaseAsync "Markdown typed in the rich editor renders formatted and round-trips to Markdown" <|
            async {
                let server = serveStatic harnessRoot EDITOR_PORT
                let! pw = await (Playwright.CreateAsync ())
                let! br =
                    await (pw.Chromium.LaunchAsync (
                        BrowserTypeLaunchOptions (ExecutablePath = chromiumPath ())))
                let! page = await (br.NewPageAsync ())
                page.SetDefaultTimeout 15000.0f
                let! _ = await (page.GotoAsync editorBase)
                let! _ = await (page.WaitForSelectorAsync ".ProseMirror")

                // Type Markdown with REAL key events so the input rules fire: "# " turns the
                // block into a heading rendered live as <h1> — the syntax is never left literal.
                do! awaitU (page.ClickAsync ".ProseMirror")
                do! awaitU (page.Keyboard.TypeAsync "# Heading one")
                let! _ = await (page.WaitForFunctionAsync "document.querySelector('.ProseMirror h1')?.textContent === 'Heading one'")
                // `**bold**` -> a <strong> mark; `- ` -> a bullet list <ul><li>.
                do! awaitU (page.Keyboard.PressAsync "Enter")
                do! awaitU (page.Keyboard.TypeAsync "text with **bold** now")
                let! _ = await (page.WaitForFunctionAsync "!!document.querySelector('.ProseMirror strong')")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                do! awaitU (page.Keyboard.TypeAsync "- item one")
                let! _ = await (page.WaitForFunctionAsync "!!document.querySelector('.ProseMirror ul li')")

                // The document serializes back to Markdown (the durable form the drain snapshots).
                let! md = await (page.EvaluateAsync<string> "() => window.__md()")
                Expect.stringContains md "# Heading one" "heading serialized to markdown"
                Expect.stringContains md "**bold**" "bold serialized to markdown"
                Expect.stringContains md "* item one" "bullet serialized to markdown"

                do! awaitU (br.CloseAsync ())
                pw.Dispose ()
                server.Stop ()
            }

        testCaseAsync "a remote peer's selection renders as a caret widget, label, and highlight" <|
            async {
                let server = serveStatic harnessRoot (EDITOR_PORT + 1)
                let! pw = await (Playwright.CreateAsync ())
                let! br =
                    await (pw.Chromium.LaunchAsync (
                        BrowserTypeLaunchOptions (ExecutablePath = chromiumPath ())))
                let! page = await (br.NewPageAsync ())
                page.SetDefaultTimeout 15000.0f
                let! _ = await (page.GotoAsync (sprintf "http://127.0.0.1:%d/" (EDITOR_PORT + 1)))
                let! _ = await (page.WaitForSelectorAsync ".ProseMirror")

                // Give the editor some content, then select a RANGE (not a bare caret) so the
                // reported selection has distinct anchor/head — the editor relays it via
                // `reportFocus`, which the harness stashes.
                do! awaitU (page.ClickAsync ".ProseMirror")
                do! awaitU (page.Keyboard.TypeAsync "hello world")
                do! awaitU (page.Keyboard.PressAsync "Control+a")

                // Replay that selection as a REMOTE peer's cursor. The decorations are built from
                // its relative positions: a caret widget + name label at `head`, and a translucent
                // highlight across the (non-empty) range.
                do! awaitU (page.EvaluateAsync "() => window.__pushRemote('remote-peer')")
                let! _ = await (page.WaitForFunctionAsync "!!document.querySelector('.ProseMirror .pm-caret')")
                let! _ =
                    await (page.WaitForFunctionAsync
                        "[...document.querySelectorAll('.ProseMirror .pm-caret-label')].some(l => l.textContent === 'remote-peer')")
                // The selection highlight is an inline decoration carrying our translucent colour.
                let! _ =
                    await (page.WaitForFunctionAsync
                        "[...document.querySelectorAll('.ProseMirror [style*=\"background-color\"]')].length > 0")

                do! awaitU (br.CloseAsync ())
                pw.Dispose ()
                server.Stop ()
            }
    ]

#else

// Fable (JS on Node): Playwright is a .NET driver and does not exist here, so the flows above
// are compiled out. These stubs only exist so the module compiles under Fable; they are never
// forced — the `[Browser]` need fails on Node and reports the skip itself.
let tests : Fable.Pyxpecto.Model.TestCase = testList "Browser E2E" []
let editorTests : Fable.Pyxpecto.Model.TestCase = testList "Editor rendering (browser)" []

#endif
