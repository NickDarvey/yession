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
// Markdown typed into the rich composer renders as formatted rich text (input rules), that the
// SECOND peer's composer joins that draft rather than opening a rival, that it converges over
// native WebRTC with live carets, that the second peer can co-edit AND send it — whose durable
// body is Markdown — rendering as that same formatted rich text in both timelines; then proves
// client-side IndexedDB persistence by wiping the server and reloading
// (the draft can only return from the browser), and that the doc store is session-keyed.
// Event-driven throughout (WaitForFunctionAsync); Playwright's own per-action timeouts watch.

open Fable.Pyxpecto

#if !FABLE_COMPILER

open System
open System.IO
open System.Net
open System.Net.Http
open System.Diagnostics
open System.Threading.Tasks
open Microsoft.Playwright

/// The session's address, learned from the Manager's readiness line rather than pinned.
/// Sessions are addressed by an OS-assigned port here (this fixture is deliberately the
/// UNMOUNTED shape), so the fixture reads the port it was actually given — the mounted
/// fixture below is the one that exercises a stable address.
let mutable private BASE = ""
let private dataDir = "tests/browser/.data"

/// The URL out of a "launched at http://127.0.0.1:PORT/ …" line. Same shape the packaged
/// composition test uses to learn both endpoints from stdout.
let private urlIn (line: string) =
    let m = System.Text.RegularExpressions.Regex.Match (line, "http://[0-9.:]+/")
    if m.Success then Some m.Value else None

// --- Chromium discovery -----------------------------------------------------------------
//
// The browser comes from `PLAYWRIGHT_BROWSERS_PATH` — nixpkgs' playwright-driver, pinned by
// the same lock as the toolchain (devenv.nix) — and its layout is Playwright's, which differs
// per platform and has changed name across builds:
//
//   x86_64-linux    chrome-linux64/chrome
//   aarch64-linux   chrome-linux/chrome
//   aarch64-darwin  chrome-mac-arm64/Google Chrome for Testing.app/Contents/MacOS/…
//
// So the executable is found by NAME, from a known set, under the `chromium-<revision>`
// directory. Nothing here may hardcode a revision: it moves with every Playwright bump, and a
// stale one would fail as "no Chromium" long after the browser arrived.
//
// There is deliberately no fallback to a system Chrome. The revision is pinned so the browser
// matches the client driving it; silently launching whatever `/usr/bin/google-chrome` happens
// to be would make the suite's meaning depend on the host, which is the opposite of what
// pinning is for. `CHROMIUM_PATH` remains as the explicit override for someone who means it.
let private chromiumExecutableNames =
    set [ "chrome"; "Google Chrome for Testing"; "Chromium" ]

let private chromiumPath () : string =
    let env name =
        match Environment.GetEnvironmentVariable name with
        | null | "" -> None
        | v -> Some v
    match env "CHROMIUM_PATH" with
    | Some p -> p
    | None ->
        match env "PLAYWRIGHT_BROWSERS_PATH" with
        | None ->
            failwith
                "no Chromium: PLAYWRIGHT_BROWSERS_PATH is unset (devenv.nix sets it; outside \
                 the dev shell, set CHROMIUM_PATH)"
        | Some root ->
            // `chromium-*` and not `chromium*`: `chromium_headless_shell-<rev>` sits beside it
            // and is a different, cut-down browser. The underscore is what separates them.
            let revisions =
                if Directory.Exists root then
                    try Directory.GetDirectories (root, "chromium-*") |> Array.toList |> List.sort
                    with _ -> []
                else []
            // Each revision directory is a symlink into the store, and .NET's recursive
            // enumeration does not descend one — resolve it rather than depend on that.
            let resolve (dir: string) =
                match Directory.ResolveLinkTarget (dir, true) with
                | null -> dir
                | target -> target.FullName
            let executables =
                revisions
                |> List.collect (fun dir ->
                    try
                        Directory.EnumerateFiles (resolve dir, "*", SearchOption.AllDirectories)
                        |> Seq.filter (fun f -> chromiumExecutableNames.Contains (Path.GetFileName f))
                        |> Seq.toList
                    with _ -> [])
            match executables |> List.tryFind File.Exists with
            | Some c -> c
            | None ->
                failwithf
                    "no Chromium under %s (looked in %d chromium-* revision(s) for %s); set CHROMIUM_PATH"
                    root
                    (List.length revisions)
                    (String.Join (", ", chromiumExecutableNames))

// --- Host spawn / readiness (ported): the real product entry on a test port -------------
let mutable private host : Process = null

let private startHost () : unit =
    let psi = ProcessStartInfo "node"
    psi.ArgumentList.Add "app/out/Main.js"
    // Single-machine loopback trust (the shipped default `none` denies everything and
    // the login bounce would 401 before any page ever connects — docs/plans/07).
    psi.ArgumentList.Add "--auth"
    psi.ArgumentList.Add "localhost"
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true   // stderr inherits → visible in the log
    psi.EnvironmentVariables.["YESSION_DATA_DIR"] <- dataDir
    let p = new Process (StartInfo = psi)
    let ready = TaskCompletionSource<bool> ()
    // Keep draining stdout (like the JS 'data' handler) so the pipe never blocks the host;
    // resolve readiness on the "launched at" line.
    p.OutputDataReceived.Add (fun e ->
        if e.Data <> null && e.Data.Contains "launched at" then
            match urlIn e.Data with
            | Some url ->
                BASE <- url
                ready.TrySetResult true |> ignore
            | None -> ())
    p.Start () |> ignore
    p.BeginOutputReadLine ()
    host <- p
    if not (ready.Task.Wait 30000) then failwith "host never reported readiness"
    if BASE = "" then failwith "the readiness line carried no session URL"

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

// The open draft is a ProseMirror editable (`.ProseMirror`) inside the editable
// (`data-rich-readonly="false"`) body-mount host — and it is whichever draft this peer has open,
// which may be someone else's: the composer joins the message already being written. Collapsed
// drafts are read-only one-line summaries.
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
                // One isolated context per peer: the peer id is stable per browser
                // PROFILE now (localStorage, docs/plans/07), so two pages in one context
                // would be one peer — a single human in two tabs — not the two distinct
                // collaborators this flow verifies.
                let! contextA = await (browser.NewContextAsync ())
                let! contextB = await (browser.NewContextAsync ())
                let! a = await (contextA.NewPageAsync ())
                let! bb = await (contextB.NewPageAsync ())
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

                // B converges: it renders A's draft as the same formatted heading.
                // (Regression guard: pushing presence decorations on every render used to starve
                // y-prosemirror's rendering of REMOTE content here, so B's mirror stayed blank.)
                do! await (pageB.WaitForFunctionAsync
                            """[...document.querySelectorAll('.ProseMirror h1')].some(h => h.textContent === 'Heading one')""") |> Async.Ignore

                // And B JOINED it rather than opening a rival blank: the composer B is in is A's
                // draft, which is why the "new message" way out is offered at all.
                do! await (pageB.WaitForFunctionAsync """!!document.querySelector('[data-draft-new]')""") |> Async.Ignore

                // B overlays A's live caret in it: A's presence (a base64 relative position over
                // the draft body) decodes to a caret widget + name label. This lands just after
                // the content settles (the decoration push is debounced off the active-convergence
                // window). Guards remote BODY cursors end-to-end.
                do! await (pageB.WaitForFunctionAsync """!!document.querySelector('.pm-caret')""") |> Async.Ignore

                // B CO-EDITS A's draft — the collaboration the read-only mirror used to forbid —
                // and A sees the words appear in the draft it started.
                do! awaitU (pageB.ClickAsync composer)
                do! awaitU (pageB.Keyboard.PressAsync "End")
                do! awaitU (pageB.Keyboard.TypeAsync " and two")
                let coEdited =
                    """[...document.querySelectorAll('.ProseMirror h1')].some(h => h.textContent === 'Heading one and two')"""
                let! _ = await (pageA.WaitForFunctionAsync coEdited)

                // B sends A's draft: any co-editor may. Both timelines show the immutable message.
                // The durable body is MARKDOWN (`# Heading one and two`, from events not Yjs), but
                // the timeline RENDERS it as formatted rich text — the same heading the composer
                // showed — so the sent view mirrors the input: an <h1>, no literal `#`.
                do! awaitU (pageB.ClickAsync "[data-send-draft]")
                let inTimeline = """[...document.querySelectorAll('[data-conversation] [data-message-body] h1')].some(h => h.textContent.trim() === 'Heading one and two')"""
                let! _ = await (pageA.WaitForFunctionAsync inTimeline)
                do! await (pageB.WaitForFunctionAsync inTimeline) |> Async.Ignore
            }

        // Terminals (Plan 13) in a real browser: the one part of the panel that only a
        // browser can exercise — the `<input>` bound to a `Y.Text` root. Everything under it
        // (the slot rule, the queue, the approval gate, the drain, the transcript) is covered
        // in the cheap tier; what is under test here is the binding itself, and that the
        // command really runs in the session's sandbox.
        testCaseAsync "a command typed in the terminal composer converges, runs in the sandbox, and both peers see the block" <|
            async {
                // The column starts shut, so the header control is the way back in — and
                // that this can find it is the test that one exists at all.
                do! awaitU (pageA.Locator("[data-terminal-toggle='show']").First.ClickAsync ())
                // `.First`: a session with no terminal open offers "new" twice — in the tab
                // strip and in the empty state — and either will do.
                do! awaitU (pageA.Locator("[data-terminal-new]").First.ClickAsync ())

                // Opening is a command; the terminal reaches BOTH peers as an event, so B
                // learns about it without having asked for anything.
                let hasTab = """!!document.querySelector('[data-terminal-tab]')"""
                let! _ = await (pageA.WaitForFunctionAsync hasTab)
                let! _ = await (pageB.WaitForFunctionAsync hasTab)

                // A types a command with REAL key events, so the input's binding is what
                // writes the CRDT — one minimal edit per keystroke, not a wholesale replace.
                let composerInput = "[data-terminal-input^='term-draft:']:not([readonly])"
                let! _ = await (pageA.WaitForSelectorAsync composerInput)
                do! awaitU (pageA.ClickAsync composerInput)
                do! awaitU (pageA.Keyboard.TypeAsync "echo hello-terminal")

                // B sees A writing it — the terminal's version of watching a draft, and the
                // proof that the binding pushes a remote edit back into the input's value.
                let mirrored =
                    """[...document.querySelectorAll('[data-terminal-input]')].some(i => i.value === 'echo hello-terminal')"""
                let! _ = await (pageB.WaitForFunctionAsync mirrored)

                // Sending runs it: a human's command needs no approval under the default
                // mode, so the drain takes it straight away and the sandbox really runs it.
                do! awaitU (pageA.ClickAsync "[data-terminal-send]")
                let blockRan =
                    """[...document.querySelectorAll('[data-terminal-block]')]
                         .some(b => b.textContent.includes('echo hello-terminal')
                                 && b.getAttribute('data-terminal-block-status') === 'ok')"""
                let! _ = await (pageA.WaitForFunctionAsync blockRan)
                let! _ = await (pageB.WaitForFunctionAsync blockRan)

                // And its OUTPUT arrived — over the terminal frames on A, and (for B, whose
                // panel was never opened) through the same fold either way.
                let hasOutput =
                    """[...document.querySelectorAll('[data-terminal-output]')].some(o => o.textContent.includes('hello-terminal'))"""
                let! _ = await (pageA.WaitForFunctionAsync hasOutput)
                do! await (pageB.WaitForFunctionAsync hasOutput) |> Async.Ignore

                // The composer emptied on send, so the next command starts from a clean line.
                do!
                    await (pageA.WaitForFunctionAsync
                            "document.querySelector(\"[data-terminal-input^='term-draft:']:not([readonly])\")?.value === ''")
                    |> Async.Ignore
            }

        // Plan 11. THE discriminating check for the manager origin: this fixture sets no
        // YESSION_MANAGER_URL, so `PublicAccess.managerUrl` alone answers None here and an
        // implementation that used it would emit no tag and silently drop the client's
        // offer to reopen a stopped session on every single-machine deployment. Only the
        // fallback to the Manager's own endpoint makes this pass — and it has to be a real
        // origin, so the test fetches it.
        testCaseAsync "the shell carries a manager origin that actually answers" <|
            async {
                let! origin =
                    await (pageA.EvaluateAsync<string> ("""() => document.querySelector('meta[name="yession-manager"]')?.getAttribute('content')"""))
                Expect.isFalse (String.IsNullOrEmpty origin) "the bootstrap page must embed the Manager's origin"
                Expect.isTrue (origin.StartsWith "http") (sprintf "expected an origin, got: %s" origin)
                // The client appends `/sessions/{id}/open` to this, so it must be an origin
                // root with no trailing slash — otherwise the URL it builds has a double one.
                Expect.isFalse (origin.EndsWith "/") "no trailing slash: the client concatenates a path onto it"
                let! sessionId =
                    await (pageA.EvaluateAsync<string> ("""() => document.querySelector('meta[name="yession-session"]')?.getAttribute('content')"""))
                // And it is the Manager, not something else that happens to answer: its
                // management page lists this very session.
                let! page = await (pageA.Context.APIRequest.GetAsync origin)
                Expect.equal page.Status 200 "the embedded origin serves the management UI"
                let! body = await (page.TextAsync ())
                Expect.isTrue (body.Contains sessionId) "and it knows the session whose shell pointed here"
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

        testCaseAsync "a first-visit browser connects as the peer id it keeps" <|
            async {
                // The peer a browser SIGNS IN as (the id riding the login bounce, which the
                // Manager witnesses into the launch) must be the peer it KEEPS (the id in
                // localStorage that every later load asserts) — otherwise the whole
                // peer-scoped surface is denied for the life of the launch. The break was
                // invisible to the HTTP tests, which pass one id through by hand: it needs a
                // FIRST VISIT in a real browser, which is what a fresh context is.
                let! context = await (browser.NewContextAsync ())
                let! page = await (context.NewPageAsync ())
                page.SetDefaultTimeout 20000.0f
                try
                    let! _ = await (page.GotoAsync BASE)
                    // Nothing may be evaluated until the login bounce has settled (it destroys
                    // the execution context); `connected` is only true back on the shell.
                    let! _ = await (page.WaitForFunctionAsync connected)

                    // Sign a credential in for "all my sessions" — the peer's own scope — from
                    // settings, exactly as a human does. The control is the sidebar's `settings`
                    // pivot: its own accessible name is the word it shows, so the hook — which is
                    // the contract — is what to click. (`data-settings-toggle="prompt"` marks the
                    // calls to action that also lead there; `open` is the pivot alone.)
                    do! awaitU (page.ClickAsync "[data-settings-toggle='open']")
                    let! _ = await (page.WaitForSelectorAsync "[data-claude-connect]")
                    do! awaitU (page.ClickAsync "[data-claude-connect]")

                    // The flow settles either into the paste-the-code step (the broker minted a
                    // provider authorize URL — no network involved) or into a legible error.
                    let! _ =
                        await (page.WaitForFunctionAsync
                                """!!document.querySelector('[data-claude-authorize]')
                                   || !!document.querySelector('[data-claude-error]')""")
                    let! error =
                        await (page.EvaluateAsync<string>
                                "() => document.querySelector('[data-claude-error]')?.textContent ?? ''")
                    Expect.equal error "" "connecting must not be refused for the browser's own peer"
                finally
                    context.CloseAsync () |> ignore
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
                        // A stylesheet served as `application/octet-stream` is ignored by
                        // the browser, silently — which makes every layout measured on this
                        // page a fiction, and looks exactly like CSS that does not work.
                        elif path.EndsWith ".css" then "text/css"
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
                // `**bold**` -> a <strong> mark; `- ` -> a bullet list <ul><li>. The new line is
                // Alt+Enter here because the harness mounts the editor as the COMPOSER does,
                // where plain Enter sends (asserted below).
                do! awaitU (page.Keyboard.PressAsync "Alt+Enter")
                do! awaitU (page.Keyboard.TypeAsync "text with **bold** now")
                let! _ = await (page.WaitForFunctionAsync "!!document.querySelector('.ProseMirror strong')")
                do! awaitU (page.Keyboard.PressAsync "Alt+Enter")
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

        testCaseAsync "Enter sends, Shift+Enter breaks the line, Alt+Enter opens a paragraph" <|
            async {
                let server = serveStatic harnessRoot (EDITOR_PORT + 2)
                let! pw = await (Playwright.CreateAsync ())
                let! br =
                    await (pw.Chromium.LaunchAsync (
                        BrowserTypeLaunchOptions (ExecutablePath = chromiumPath ())))
                let! page = await (br.NewPageAsync ())
                page.SetDefaultTimeout 15000.0f
                let! _ = await (page.GotoAsync (sprintf "http://127.0.0.1:%d/" (EDITOR_PORT + 2)))
                let! _ = await (page.WaitForSelectorAsync ".ProseMirror")

                do! awaitU (page.ClickAsync ".ProseMirror")
                do! awaitU (page.Keyboard.TypeAsync "first line")
                // Enter asks to send, and — the half that matters — leaves the document
                // exactly as it was. A binding that sends AND splits the block would look
                // right in a screenshot and lose a paragraph into every message.
                do! awaitU (page.Keyboard.PressAsync "Enter")
                let! _ = await (page.WaitForFunctionAsync "window.__sends === 1")
                let! afterSend = await (page.EvaluateAsync<string> "() => window.__md()")
                Expect.stringContains afterSend "first line" "the text is untouched by the send"
                Expect.isFalse (afterSend.Trim().Contains "\n\n") "Enter inserted no new block"

                // Shift+Enter breaks the LINE: a <br> inside the block it was already in, so
                // the paragraph is still one paragraph. This is the half a single Enter could
                // never express, and it has to survive Markdown to be worth anything — the
                // serializer writes a trailing backslash and the parser reads it back.
                do! awaitU (page.Keyboard.PressAsync "Shift+Enter")
                do! awaitU (page.Keyboard.TypeAsync "same paragraph")
                let! _ = await (page.WaitForFunctionAsync "!!document.querySelector('.ProseMirror br')")
                let! _ =
                    await (page.WaitForFunctionAsync
                        "document.querySelectorAll('.ProseMirror > p').length === 1")
                let! broken = await (page.EvaluateAsync<string> "() => window.__md()")
                Expect.stringContains broken "first line" "the text before the break survived"
                Expect.stringContains broken "same paragraph" "and the text after it"
                Expect.isFalse (broken.Trim().Contains "\n\n") "a line break is not a paragraph break"

                // Alt+Enter is where the PARAGRAPH went: a second block, and no second send.
                do! awaitU (page.Keyboard.PressAsync "Alt+Enter")
                do! awaitU (page.Keyboard.TypeAsync "second block")
                let! _ =
                    await (page.WaitForFunctionAsync
                        "document.querySelectorAll('.ProseMirror > p').length === 2")
                let! md = await (page.EvaluateAsync<string> "() => window.__md()")
                Expect.stringContains md "first line" "the first block survived"
                Expect.stringContains md "second block" "Alt+Enter opened a second block"
                let! sends = await (page.EvaluateAsync<int> "() => window.__sends")
                Expect.equal sends 1 "neither Shift+Enter nor Alt+Enter sent"

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
                // `ControlOrMeta`, not `Control`: select-all is Cmd+A on macOS, where Ctrl+A is
                // the emacs "start of line" binding instead — so this selected nothing, the
                // range stayed empty, and the highlight this test waits for never rendered.
                // Invisible until the Browser tier could run on a Mac at all.
                do! awaitU (page.Keyboard.PressAsync "ControlOrMeta+a")

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

        // The replay (Plan 13, stage 3e). Everything else about it is pinned DOM-free — the
        // `.cast` rebuild against the real file, the closed tab, the retention gap — but not
        // this: whether `asciinema-player`'s named export resolves through the bundle and
        // actually plays what was recorded. An import that silently failed would leave every
        // other test green and the feature dead in the browser.
        testCaseAsync "a recorded terminal replays in a real player, and prints what it printed" <|
            async {
                let server = serveStatic harnessRoot (EDITOR_PORT + 3)
                let! pw = await (Playwright.CreateAsync ())
                let! br =
                    await (pw.Chromium.LaunchAsync (
                        BrowserTypeLaunchOptions (ExecutablePath = chromiumPath ())))
                let! page = await (br.NewPageAsync ())
                page.SetDefaultTimeout 15000.0f
                let! _ = await (page.GotoAsync (sprintf "http://127.0.0.1:%d/" (EDITOR_PORT + 3)))

                // The player took the mount and built its own DOM there.
                let! _ = await (page.WaitForSelectorAsync "#replay .ap-player")
                // …with the transport controls that ARE the audit-read affordance: a replay
                // you cannot pause or seek is a video of a terminal, not a record of one.
                let! _ = await (page.WaitForSelectorAsync "#replay .ap-control-bar")
                // …and the chapter marks (Plan 14, stage 4), which are what make a
                // whole-terminal recording navigable by what ran in it. Asserted here
                // because a marker option the player silently ignored would leave every
                // DOM-free test green and the chapters absent.

                // Then play it, and wait for the recording's own output to appear on the
                // screen. This is the assertion that spans the whole stage: bytes the Session
                // Process wrote, encoded as asciicast, rebuilt by `TranscriptReplay.cast`,
                // and rendered by the player.
                let! _ = await (page.WaitForSelectorAsync "#replay .ap-overlay-start")
                do! awaitU (page.ClickAsync "#replay .ap-overlay-start")
                let! _ =
                    await (page.WaitForFunctionAsync
                        "document.querySelector('#replay').textContent.includes('total 0')")

                // …and the chapter marks (Plan 14, stage 4), which are what make a
                // whole-terminal recording navigable by what ran in it. Asserted after play
                // rather than before, because the recording's metadata — its duration, and
                // therefore where a marker sits on the bar — is not known until it loads.
                let! _ =
                    await (page.WaitForFunctionAsync
                        "document.querySelectorAll('#replay .ap-marker').length === 1")

                do! awaitU (br.CloseAsync ())
                pw.Dispose ()
                server.Stop ()
            }

        // Terminal work in the chat, and the pane's tabs (Plan 14, stages 1-2). Host-free,
        // like the editor and the replay beside it: what needs a real browser here is not the
        // Session Process but the DOM swaps — where FOCUS goes when a chip in the chat opens
        // a tab in the pane, and whether the tab strip is a tablist the arrow keys walk.
        // Neither is visible to a rendered string, and both are the WCAG floor rather than a
        // nicety: a chip that opens a pane and leaves focus behind, or a close that strands
        // focus on a control it just removed, is exactly the failure the floor names.
        testCaseAsync "a chat chip opens a pane tab that plays, the strip walks, and closing hands focus back" <|
            async {
                let server = serveStatic harnessRoot (EDITOR_PORT + 4)
                let! pw = await (Playwright.CreateAsync ())
                let! br =
                    await (pw.Chromium.LaunchAsync (
                        BrowserTypeLaunchOptions (ExecutablePath = chromiumPath ())))
                let! page = await (br.NewPageAsync ())
                page.SetDefaultTimeout 15000.0f
                let! _ = await (page.GotoAsync (sprintf "http://127.0.0.1:%d/" (EDITOR_PORT + 4)))

                // The chip the harness model's one block puts in the chat.
                let! _ = await (page.WaitForSelectorAsync "#shell [data-chat-block]")
                do! awaitU (page.ClickAsync "#shell [data-chat-block]")

                // A tab opened, showing that block.
                let showingBlock =
                    """document.querySelector('#shell [data-pane-panel]')?.getAttribute('data-pane-panel')?.startsWith('block:') === true"""
                let! _ = await (page.WaitForFunctionAsync showingBlock)

                // Focus followed it into the pane. Asserted BEFORE anything is played,
                // because pressing play is itself a focus move.
                let! _ = await (page.WaitForFunctionAsync """document.activeElement?.hasAttribute('data-pane-panel') === true""")

                // The strip is a real tablist: an arrow key walks it. MANUAL activation, so
                // walking does not swap the panel under the reader per keypress.
                do! awaitU (page.FocusAsync "#shell [data-pane-tab^='block:']")
                do! awaitU (page.Keyboard.PressAsync "ArrowLeft")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.activeElement?.getAttribute('data-pane-tab')?.startsWith('terminal:') === true""")
                let! _ = await (page.WaitForFunctionAsync showingBlock)

                // The block's recording is PLAYED, not printed: the real player, over the
                // ranged cast the model built, inside the tab the chip opened. A stream
                // renderer would show a cursor-moving program as garbage, which is the whole
                // reason the transcript was written as asciicast.
                let! _ = await (page.WaitForSelectorAsync "#shell [data-pane-replay] .ap-overlay-start")
                do! awaitU (page.ClickAsync "#shell [data-pane-replay] .ap-overlay-start")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.querySelector('#shell [data-pane-block]')?.textContent.includes('total 0') === true""")

                // Closing the tab hands focus back to the chip that opened it, rather than
                // stranding it on a control that has just left the document.
                do! awaitU (page.ClickAsync "#shell [data-pane-tab-close]")
                let! _ = await (page.WaitForFunctionAsync """!document.querySelector('#shell [data-pane-block]')""")
                let! _ = await (page.WaitForFunctionAsync """document.activeElement?.hasAttribute('data-chat-block') === true""")

                do! awaitU (br.CloseAsync ())
                pw.Dispose ()
                server.Stop ()
            }
        // The phone (Plan 14, stage 5). Headless Chromium clamps its WINDOW to ~500px, which
        // is why a naive narrow screenshot lies; Playwright's viewport is a real CDP device
        // metrics override, so 390 here is 390. The two things this asserts are the two the
        // plan is about: the pane takes the whole column rather than sitting over the chat
        // as a dismissible overlay, and nothing overflows sideways — an overflow a phone
        // user cannot scroll away is a reachability bug, not a cosmetic one.
        testCaseAsync "on a phone the pane IS the column, the strip stays, and the chat is one control away" <|
            async {
                let server = serveStatic harnessRoot (EDITOR_PORT + 5)
                let! pw = await (Playwright.CreateAsync ())
                let! br =
                    await (pw.Chromium.LaunchAsync (
                        BrowserTypeLaunchOptions (ExecutablePath = chromiumPath ())))
                // ViewportSize alone. `IsMobile` additionally asks Chromium to fit the
                // layout to a device window, and measured here that lands at 648px rather
                // than 390 — the very lie the ui-exploration skill warns about, arriving
                // through a different door.
                let! ctx =
                    await (br.NewContextAsync (
                        BrowserNewContextOptions (ViewportSize = ViewportSize (Width = 390, Height = 844))))
                let! page = await (ctx.NewPageAsync ())
                page.SetDefaultTimeout 15000.0f
                let! _ = await (page.GotoAsync (sprintf "http://127.0.0.1:%d/" (EDITOR_PORT + 5)))

                // Ground truth first: the viewport really is the width we asked for.
                let! width = await (page.EvaluateAsync<int> "() => window.innerWidth")
                Expect.equal width 390 "a true phone viewport, not a clamped window"

                // The pane starts off screen, as it does for a fresh client.
                let! _ =
                    await (page.WaitForFunctionAsync
                        "document.querySelector('#shell [data-terminal-panel]').getBoundingClientRect().left >= window.innerWidth - 1")

                // A chip brings it on, and it takes the WHOLE column.
                do! awaitU (page.ClickAsync "#shell [data-chat-block]")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """(() => {
                             const r = document.querySelector('#shell [data-terminal-panel]').getBoundingClientRect()
                             return r.left <= 1 && Math.round(r.width) === window.innerWidth
                           })()""")
                // …with the tab strip retained, which is what keeps phone and desktop one
                // mental model rather than two surfaces that happen to share a codebase.
                let! _ = await (page.WaitForSelectorAsync "#shell [role='tablist'] [data-pane-tab]")

                // Nothing overflows sideways.
                let! overflows =
                    await (page.EvaluateAsync<bool> "() => document.documentElement.scrollWidth > window.innerWidth + 1")
                Expect.isFalse overflows "no horizontal overflow a phone user cannot scroll away"

                // Nor is the header cut off vertically. The phone's band is a compressed one
                // and it carries something the desktop's does not — the session id, in flow
                // below the title — so it is the one place the heading can outgrow its band.
                // At 64px it did: the title's box started ON the band's top edge, which on a
                // phone reads as a heading sliced off by the browser. What is asserted is the
                // containment, not the number: whatever the band becomes, what it holds has
                // to fit inside it.
                let! headerFits =
                    await (page.EvaluateAsync<bool>
                            """() => {
                                 const band = document.querySelector('#shell header').getBoundingClientRect()
                                 const title = document.querySelector('#shell [data-session-title]').getBoundingClientRect()
                                 const id = document.querySelector('#shell [data-session-id]').getBoundingClientRect()
                                 return title.top > band.top && id.bottom <= band.bottom
                               }""")
                Expect.isTrue headerFits "the header's title and id sit inside the band, not on its edges"

                // And the way back to the chat is a control, not a dismissal: it returns
                // focus to the chip that opened the pane.
                do! awaitU (page.ClickAsync "#shell [data-terminal-toggle='hide']")
                let! _ =
                    await (page.WaitForFunctionAsync
                        "document.querySelector('#shell [data-terminal-panel]').getBoundingClientRect().left >= window.innerWidth - 1")
                let! _ = await (page.WaitForFunctionAsync """document.activeElement?.hasAttribute('data-chat-block') === true""")

                do! awaitU (br.CloseAsync ())
                pw.Dispose ()
                server.Stop ()
            }
        // The live viewport (Plan 14, stage 6). What only a browser can answer here is the
        // KEYSTROKE TRANSLATION: a `KeyboardEvent` is not a byte stream, and turning one
        // into what a pty expects — printable characters as themselves, Ctrl-<key> as the
        // control code, the keys with no character at all as their escape sequences — is the
        // whole of what a terminal front end does with a keyboard.
        testCaseAsync "the holder types into the live screen, and the keys reach it as a pty expects" <|
            async {
                let server = serveStatic harnessRoot (EDITOR_PORT + 6)
                let! pw = await (Playwright.CreateAsync ())
                let! br =
                    await (pw.Chromium.LaunchAsync (
                        BrowserTypeLaunchOptions (ExecutablePath = chromiumPath ())))
                let! page = await (br.NewPageAsync ())
                page.SetDefaultTimeout 15000.0f
                let! _ = await (page.GotoAsync (sprintf "http://127.0.0.1:%d/" (EDITOR_PORT + 6)))

                // The column starts shut, as it does for a fresh client.
                do! awaitU (page.ClickAsync "#shell [data-terminal-toggle='show']")
                // The terminal the harness holds the lease on renders its screen, and the
                // screen shows what the program drew.
                do! awaitU (page.ClickAsync "#shell [data-terminal-tab='term-live']")
                let screen = "#shell [data-terminal-screen='term-live']"
                let! _ = await (page.WaitForSelectorAsync screen)
                let! _ =
                    await (page.WaitForFunctionAsync
                        (sprintf "document.querySelector(%s).textContent.includes('vim ~/notes')" "\"#shell [data-terminal-screen='term-live']\""))

                // It is a Tab stop, because its whole purpose is having the keyboard.
                do! awaitU (page.FocusAsync screen)
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.activeElement?.getAttribute('data-terminal-screen') === 'term-live'""")

                // The screen is composed by a REAL emulator in a real browser: the
                // Session Process's snapshot seeds it, and the records the client already
                // holds are folded on top. This is the only tier that runs xterm in the
                // browser at all — and it exists because a browser-only module resolution
                // failure in exactly this path reached a release job while the cheap tier
                // and this one were both green.
                // Seeded with something the assertion below does NOT look for: what is
                // being proven is the FOLD — "earlier output" exists only in the transcript
                // records, never in the screen the model was built with — so a client that
                // rendered the snapshot and folded nothing would fail here.
                do! awaitU (page.EvaluateAsync ("() => window.__snapshot('term-live', 0, 'session start\\r\\n')"))
                let! _ =
                    await (page.WaitForFunctionAsync
                        (sprintf "document.querySelector(%s).textContent.includes('earlier output')" "\"#shell [data-terminal-screen='term-live']\""))

                do! awaitU (page.Keyboard.TypeAsync "ls")
                do! awaitU (page.Keyboard.PressAsync "ArrowUp")
                do! awaitU (page.Keyboard.PressAsync "Control+c")
                do! awaitU (page.Keyboard.PressAsync "Backspace")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                let! typed = await (page.EvaluateAsync<string> "() => window.__typed || ''")
                Expect.equal typed "ls\u001b[A\u0003\u007f\r" "printable, escape, control code, delete, carriage return"

                // Tab is SENT rather than moving focus out of the terminal mid-session, and
                // the shift is that `preventDefault` fires for everything the terminal takes.
                let! before = await (page.EvaluateAsync<string> "() => document.activeElement?.getAttribute('data-terminal-screen')")
                do! awaitU (page.Keyboard.PressAsync "Tab")
                let! after = await (page.EvaluateAsync<string> "() => document.activeElement?.getAttribute('data-terminal-screen')")
                Expect.equal after before "Tab types a tab; it does not leave the terminal"

                do! awaitU (br.CloseAsync ())
                pw.Dispose ()
                server.Stop ()
            }
        // The DVR (Plan 14, stage 7). What only a browser can answer: that rewinding a LIVE
        // terminal really mounts a player over what it has recorded so far — the same player
        // and the same cast a finished terminal's replay uses, which is what "rewound like
        // live TV, through the same mechanism" has to mean — that it lands ON the pinned
        // edge rather than at the recording's start, that focus survives the control swap,
        // and that playing off the pinned end catches the reader back up to live by itself.
        testCaseAsync "a live terminal rewinds to its pinned edge, and playing off it catches back up" <|
            async {
                let server = serveStatic harnessRoot (EDITOR_PORT + 7)
                let! pw = await (Playwright.CreateAsync ())
                let! br =
                    await (pw.Chromium.LaunchAsync (
                        BrowserTypeLaunchOptions (ExecutablePath = chromiumPath ())))
                let! page = await (br.NewPageAsync ())
                page.SetDefaultTimeout 15000.0f
                let! _ = await (page.GotoAsync (sprintf "http://127.0.0.1:%d/" (EDITOR_PORT + 7)))

                do! awaitU (page.ClickAsync "#shell [data-terminal-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-terminal-tab='term-live']")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-terminal-screen='term-live']")

                // Rewinding replaces the live screen with the recording, in a real player.
                do! awaitU (page.ClickAsync "#shell [data-terminal-rewind='term-live']")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-pane-replay='terminal:term-live'] .ap-player")
                let! _ = await (page.WaitForFunctionAsync """!document.querySelector("#shell [data-terminal-screen='term-live']")""")

                // The swap removed the pressed Rewind button from the document; focus must
                // land on the control that replaced it, never on `body`.
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.activeElement?.getAttribute('data-terminal-live') === 'term-live'""")

                // It lands AT the pinned edge: the poster is the screen as it stood at the
                // pin, shown before anyone presses play — not a blank player parked at 0:00.
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.querySelector("#shell [data-pane-replay='terminal:term-live']")?.textContent.includes('earlier output') === true""")

                // Playing off the pinned end IS catching up: the player's `ended` drops the
                // rewind by itself — live screen back, player down, focus handed to the
                // Rewind control that replaced the pane's face.
                do! awaitU (page.ClickAsync "#shell [data-pane-replay='terminal:term-live'] .ap-overlay-start")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-terminal-screen='term-live']")
                let! _ = await (page.WaitForFunctionAsync """!document.querySelector("#shell [data-pane-replay='terminal:term-live']")""")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.activeElement?.getAttribute('data-terminal-rewind') === 'term-live'""")

                // And the way back works by hand too: rewind again, jump to live again.
                do! awaitU (page.ClickAsync "#shell [data-terminal-rewind='term-live']")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-pane-replay='terminal:term-live'] .ap-player")
                do! awaitU (page.ClickAsync "#shell [data-terminal-live='term-live']")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-terminal-screen='term-live']")
                let! _ = await (page.WaitForFunctionAsync """!document.querySelector("#shell [data-pane-replay='terminal:term-live']")""")

                do! awaitU (br.CloseAsync ())
                pw.Dispose ()
                server.Stop ()
            }
    ]

// --- A path-mounted session in a real browser (docs/plans/10) ---------------------------

let private MOUNT_PROXY_PORT = 8186
let private MOUNT_MANAGER_PORT = 8188
/// The session's own loopback port, learned from the readiness line. The PUBLIC address is
/// `/s/<id>` on the proxy and does not contain it — which is the whole point of the shape
/// under test, and why nothing here may pin it.
let mutable private mountSessionPort = 0
let private MOUNT_SESSION = "mounted"
let private mountDataDir = "tests/browser/.data-mounted"

/// The operator's proxy in miniature: whatever arrives at the public port is forwarded to
/// the session's loopback port with the PATH UNCHANGED, so the session sees — and strips —
/// its own `/s/<id>` prefix. That is exactly the contract Plan 10 states, and the reason
/// this test can exist without depending on any proxy's rewriting behaviour.
/// `sessionPort` is a THUNK, read per request rather than captured: a session that is
/// killed and relaunched keeps its public path and gets a new loopback port, and the whole
/// point of path-mounting is that the address does not move when that happens. A proxy that
/// captured the port would forward to a dead one, which is the operator's reconciler bug in
/// miniature.
let private startMountProxy (publicPort: int) (sessionPort: unit -> int) : HttpListener =
    let listener = new HttpListener ()
    listener.Prefixes.Add (sprintf "http://127.0.0.1:%d/" publicPort)
    listener.Start ()
    // No auto-redirect: a `Location` must reach the browser untouched, which is the
    // half of the flow that proves the session's redirects resolve against its mount.
    let client = new HttpClient (new HttpClientHandler (AllowAutoRedirect = false, UseCookies = false))
    let copyHeader (reply: HttpResponseMessage) (ctx: HttpListenerContext) (name: string) =
        let values =
            match reply.Headers.TryGetValues name with
            | true, vs -> List.ofSeq vs
            | _ ->
                match reply.Content.Headers.TryGetValues name with
                | true, vs -> List.ofSeq vs
                | _ -> []
        for v in values do ctx.Response.Headers.Add (name, v)
    let rec loop () =
        async {
            match! Async.Catch (listener.GetContextAsync () |> Async.AwaitTask) with
            | Choice1Of2 ctx ->
                Async.Start (
                    async {
                        try
                            let target = sprintf "http://127.0.0.1:%d%s" (sessionPort ()) ctx.Request.RawUrl
                            use request = new HttpRequestMessage (HttpMethod ctx.Request.HttpMethod, target)
                            if ctx.Request.HasEntityBody then
                                use buffer = new MemoryStream ()
                                ctx.Request.InputStream.CopyTo buffer
                                let content = new ByteArrayContent (buffer.ToArray ())
                                match ctx.Request.ContentType with
                                | null | "" -> ()
                                | contentType -> content.Headers.TryAddWithoutValidation ("content-type", contentType) |> ignore
                                request.Content <- content
                            match ctx.Request.Headers.["Cookie"] with
                            | null | "" -> ()
                            | cookie -> request.Headers.TryAddWithoutValidation ("cookie", cookie) |> ignore
                            let! reply = client.SendAsync request |> Async.AwaitTask
                            ctx.Response.StatusCode <- int reply.StatusCode
                            copyHeader reply ctx "Location"
                            copyHeader reply ctx "Set-Cookie"
                            copyHeader reply ctx "Cache-Control"
                            match reply.Content.Headers.ContentType with
                            | null -> ()
                            | contentType -> ctx.Response.ContentType <- string contentType
                            let! bytes = reply.Content.ReadAsByteArrayAsync () |> Async.AwaitTask
                            ctx.Response.OutputStream.Write (bytes, 0, bytes.Length)
                        with _ -> ctx.Response.StatusCode <- 502
                        ctx.Response.Close ()
                    })
                return! loop ()
            | Choice2Of2 _ -> ()   // listener stopped
        }
    Async.Start (loop ())
    listener

let mutable private mountedHost : Process = null

/// The product entry, told it is fronted: the Manager at a loopback origin (which is also
/// the OIDC issuer a session fetches discovery against, so it must resolve HERE), and
/// sessions mounted under a path at the proxy's port.
let private startMountedHost () : unit =
    let psi = ProcessStartInfo "node"
    psi.ArgumentList.Add "app/out/Main.js"
    psi.ArgumentList.Add "--auth"
    psi.ArgumentList.Add "localhost"
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    psi.EnvironmentVariables.["YESSION_MANAGER_PORT"] <- string MOUNT_MANAGER_PORT
    psi.EnvironmentVariables.["YESSION_SESSION"] <- MOUNT_SESSION
    psi.EnvironmentVariables.["YESSION_DATA_DIR"] <- mountDataDir
    psi.EnvironmentVariables.["YESSION_MANAGER_URL"] <- sprintf "http://127.0.0.1:%d" MOUNT_MANAGER_PORT
    psi.EnvironmentVariables.["YESSION_SESSION_URL"] <- sprintf "http://127.0.0.1:%d/s/{id}" MOUNT_PROXY_PORT
    let p = new Process (StartInfo = psi)
    let ready = TaskCompletionSource<bool> ()
    p.OutputDataReceived.Add (fun e ->
        if e.Data <> null && e.Data.Contains "launched at" then
            // The Manager reports the session's LOOPBACK address here, which is what the
            // proxy must forward to; the browser never sees it.
            match urlIn e.Data |> Option.map Uri with
            | Some uri ->
                mountSessionPort <- uri.Port
                ready.TrySetResult true |> ignore
            | None -> ())
    p.Start () |> ignore
    p.BeginOutputReadLine ()
    mountedHost <- p
    if not (ready.Task.Wait 30000) then failwith "mounted host never reported readiness"
    if mountSessionPort = 0 then failwith "the readiness line carried no session port"

let mountedTests =
    testList "Path-mounted session (browser)" [
        testCaseAsync "a session served under a path boots, signs in, and connects over WebRTC" <|
            async {
                if Directory.Exists mountDataDir then Directory.Delete (mountDataDir, true)
                startMountedHost ()
                let proxy = startMountProxy MOUNT_PROXY_PORT (fun () -> mountSessionPort)
                // Teardown in `finally`: a failing assertion used to skip it and leave the
                // Manager, its session child and the proxy holding ports 8186-8188, so one
                // red run could poison whatever ran next (the failing CI run showed exactly
                // that, as "Terminate orphan process" lines).
                let mutable browserToClose : IBrowser option = None
                let mutable playwrightToDispose : IPlaywright option = None
                try
                    let publicUrl = sprintf "http://127.0.0.1:%d/s/%s/" MOUNT_PROXY_PORT MOUNT_SESSION
                    let! pw = await (Playwright.CreateAsync ())
                    playwrightToDispose <- Some pw
                    let! br =
                        await (pw.Chromium.LaunchAsync (
                            BrowserTypeLaunchOptions (
                                ExecutablePath = chromiumPath (),
                                Args = [| "--disable-features=WebRtcHideLocalIpsWithMdns" |])))
                    browserToClose <- Some br
                    let! context = await (br.NewContextAsync ())
                    let! page = await (context.NewPageAsync ())
                    page.SetDefaultTimeout 20000.0f

                    // Everything below happens at the PUBLIC path. Nothing in the browser was
                    // told about a prefix: the shell's `<base href>` is the only thing making
                    // its relative routes resolve under the mount.
                    let! _ = await (page.GotoAsync publicUrl)

                    // NOTHING may be evaluated until the page has settled. On a 401 from `me`
                    // the client RENAVIGATES through the login bounce (session -> manager ->
                    // `<mount>/callback` -> `./` -> the shell), and an `EvaluateAsync` racing
                    // that navigation dies with "Execution context was destroyed" — which is
                    // exactly how this test passed locally and broke master. `connected` is only
                    // true on the shell after the bounce, and `WaitForFunctionAsync` re-arms
                    // across navigations, so it is the one safe thing to await first.
                    let! _ = await (page.WaitForFunctionAsync connected)

                    let! baseHref = await (page.EvaluateAsync<string> "() => document.querySelector('base')?.getAttribute('href')")
                    Expect.equal baseHref (sprintf "/s/%s/" MOUNT_SESSION) "the shell declares its mount"

                    // No assertion here that the bundle was fetched under the mount: reaching
                    // `connected` above already required it. The bundle IS the client, and a
                    // root-anchored URL would have hit the proxy's root and 404'd, so nothing
                    // would have run to set the flag. Scraping `performance` entries for the
                    // bundle's path restated that, and only added a second place that had to
                    // know how the bundle is addressed — which is what broke when it became
                    // `client.<digest>.js`.

                    // The auth cookie is scoped to this session's mount, not the whole origin.
                    let! cookies = await (context.CookiesAsync ())
                    let sessionCookie =
                        cookies |> Seq.tryFind (fun c -> c.Name.StartsWith "yession_auth_")
                    match sessionCookie with
                    | None -> failwith "no session auth cookie was set"
                    | Some cookie ->
                        Expect.equal cookie.Path (sprintf "/s/%s/" MOUNT_SESSION) "scoped to the mount, not shared with siblings"

                    // Client-side persistence across a full server wipe (Step 20), which is
                    // only observable where the ADDRESS survives the restart. This used to
                    // live in the unmounted fixture and passed because that fixture pinned
                    // the session's port; Plan 13 deleted the pinning, so the property now
                    // belongs where it actually holds — and proving it here is the point of
                    // path-mounting rather than an accident of it.
                    let composerSel = """[data-rich-readonly="false"] .ProseMirror"""
                    let! _ = await (page.WaitForSelectorAsync composerSel)
                    do! awaitU (page.ClickAsync composerSel)
                    do! awaitU (page.Keyboard.TypeAsync "persisted across the wipe")
                    let hasDraft =
                        """[...document.querySelectorAll('.ProseMirror')].some(p => p.textContent === 'persisted across the wipe')"""
                    let! _ = await (page.WaitForFunctionAsync hasDraft)

                    mountedHost.Kill true
                    mountedHost.WaitForExit ()
                    if Directory.Exists mountDataDir then Directory.Delete (mountDataDir, true)
                    startMountedHost ()

                    // The SAME url — the session came back on a different loopback port and
                    // the proxy followed it, which the browser never saw. So the origin is
                    // unchanged, its IndexedDB is still this session's, and the draft can
                    // only have come from there: the server's copy was deleted.
                    let! _ = await (page.ReloadAsync ())
                    let! _ = await (page.WaitForFunctionAsync connected)
                    do! await (page.WaitForFunctionAsync hasDraft) |> Async.Ignore
                finally
                    browserToClose |> Option.iter (fun b -> b.CloseAsync () |> ignore)
                    playwrightToDispose |> Option.iter (fun p -> p.Dispose ())
                    proxy.Stop ()
                    try mountedHost.Kill true with _ -> ()
            }
    ]

#else

// Fable (JS on Node): Playwright is a .NET driver and does not exist here, so the flows above
// are compiled out. These stubs only exist so the module compiles under Fable; they are never
// forced — the `[Browser]` need fails on Node and reports the skip itself.
let tests : Fable.Pyxpecto.Model.TestCase = testList "Browser E2E" []
let editorTests : Fable.Pyxpecto.Model.TestCase = testList "Editor rendering (browser)" []
let mountedTests : Fable.Pyxpecto.Model.TestCase = testList "Path-mounted session (browser)" []

#endif
