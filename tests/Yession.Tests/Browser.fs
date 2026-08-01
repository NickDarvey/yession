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

let private PORT = 8180
let private BASE = sprintf "http://127.0.0.1:%d/" PORT
let private dataDir = "tests/browser/.data"

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
    // A single-port range pins the one session in this fixture to a known address, which
    // is what the navigation below needs. (Plan 11 replaced the Manager's `YESSION_PORT`
    // with per-session pinning; a range of one expresses the same fixture requirement.)
    psi.EnvironmentVariables.["YESSION_SESSION_PORTS"] <- string PORT
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
    ]

// --- A path-mounted session in a real browser (docs/plans/10) ---------------------------

let private MOUNT_PROXY_PORT = 8186
let private MOUNT_SESSION_PORT = 8187
let private MOUNT_MANAGER_PORT = 8188
let private MOUNT_SESSION = "mounted"
let private mountDataDir = "tests/browser/.data-mounted"

/// The operator's proxy in miniature: whatever arrives at the public port is forwarded to
/// the session's loopback port with the PATH UNCHANGED, so the session sees — and strips —
/// its own `/s/<id>` prefix. That is exactly the contract Plan 10 states, and the reason
/// this test can exist without depending on any proxy's rewriting behaviour.
let private startMountProxy (publicPort: int) (sessionPort: int) : HttpListener =
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
                            let target = sprintf "http://127.0.0.1:%d%s" sessionPort ctx.Request.RawUrl
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
    psi.EnvironmentVariables.["YESSION_SESSION_PORTS"] <- string MOUNT_SESSION_PORT
    psi.EnvironmentVariables.["YESSION_MANAGER_PORT"] <- string MOUNT_MANAGER_PORT
    psi.EnvironmentVariables.["YESSION_SESSION"] <- MOUNT_SESSION
    psi.EnvironmentVariables.["YESSION_DATA_DIR"] <- mountDataDir
    psi.EnvironmentVariables.["YESSION_MANAGER_URL"] <- sprintf "http://127.0.0.1:%d" MOUNT_MANAGER_PORT
    psi.EnvironmentVariables.["YESSION_SESSION_URL"] <- sprintf "http://127.0.0.1:%d/s/{id}" MOUNT_PROXY_PORT
    let p = new Process (StartInfo = psi)
    let ready = TaskCompletionSource<bool> ()
    p.OutputDataReceived.Add (fun e ->
        if e.Data <> null && e.Data.Contains "launched at" then ready.TrySetResult true |> ignore)
    p.Start () |> ignore
    p.BeginOutputReadLine ()
    mountedHost <- p
    if not (ready.Task.Wait 30000) then failwith "mounted host never reported readiness"

let mountedTests =
    testList "Path-mounted session (browser)" [
        testCaseAsync "a session served under a path boots, signs in, and connects over WebRTC" <|
            async {
                if Directory.Exists mountDataDir then Directory.Delete (mountDataDir, true)
                startMountedHost ()
                let proxy = startMountProxy MOUNT_PROXY_PORT MOUNT_SESSION_PORT
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

                    // The bundle was fetched from under the mount — had it been root-anchored it
                    // would have hit the proxy's root and 404'd, and the client could not have
                    // reached `connected` above at all.
                    let! assets =
                        await (page.EvaluateAsync<string[]> """() =>
                            performance.getEntriesByType('resource').map(e => new URL(e.name).pathname)""")
                    Expect.isTrue
                        (assets |> Array.exists (fun p -> p = sprintf "/s/%s/client.js" MOUNT_SESSION))
                        "the client bundle was fetched under the mount"

                    // The auth cookie is scoped to this session's mount, not the whole origin.
                    let! cookies = await (context.CookiesAsync ())
                    let sessionCookie =
                        cookies |> Seq.tryFind (fun c -> c.Name.StartsWith "yession_auth_")
                    match sessionCookie with
                    | None -> failwith "no session auth cookie was set"
                    | Some cookie ->
                        Expect.equal cookie.Path (sprintf "/s/%s/" MOUNT_SESSION) "scoped to the mount, not shared with siblings"
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
