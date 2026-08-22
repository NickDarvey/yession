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

// --- What the page saw (so a failure can say more than "timed out") ----------------------
//
// A browser case can only fail one way: a wait that never settles. That failure names the WAIT
// and never the reason, so three separate faults in the service worker — a registration
// eliminated as dead code, an opaque redirect from the sign-in bounce, a precache that could
// not have happened — all presented as the same thirty-second timeout, and each cost a full
// run of the gate to tell apart. The page had been saying which was which the whole time.
//
// So: keep what it says, and print it when a case fails. It costs nothing on the green path
// and it is the only way to read a red one in CI, where nothing can be attached afterwards.

type private Evidence () =
    let lines = ResizeArray<string> ()
    /// Bounded: a page that is failing tends to say the same thing very fast, and a thousand
    /// identical lines is not more evidence than fifty.
    member _.Note (text: string) =
        lock lines (fun () -> if lines.Count < 100 then lines.Add text)
    member _.Lines = lock lines (fun () -> List.ofSeq lines)

/// Listen to everything the page can tell us.
let private watching (page: IPage) =
    let ev = Evidence ()
    page.Console.Add (fun m ->
        if m.Type = "error" || m.Type = "warning" then ev.Note (sprintf "console %s: %s" m.Type m.Text))
    page.PageError.Add (fun e -> ev.Note ("pageerror: " + e))
    page.RequestFailed.Add (fun r -> ev.Note (sprintf "requestfailed: %s %s" r.Url r.Failure))
    // NOT the service worker's own console: Playwright .NET 1.61 exposes `IWorker.Console`,
    // but only for web workers (`page.Workers`) — there is no `IBrowserContext.ServiceWorkers`
    // to reach a service worker through. A worker that wants to be heard here has to say it
    // somewhere the page can see; `console.debug` in it is for a human with devtools open.
    ev

/// Run a case; if it throws, print what the page saw before letting the failure through.
let private reporting (label: string) (page: IPage) (ev: Evidence) (body: Async<unit>) : Async<unit> =
    async {
        try
            do! body
        with e ->
            printfn "=== %s failed — what the page saw ===" label
            for line in ev.Lines do printfn "  %s" line
            // The page's own state, best-effort: on a navigation failure there is no page left
            // to ask, and that answer is itself worth printing.
            let! state =
                async {
                    try
                        return!
                            await (page.EvaluateAsync<string>
                                    """async () => JSON.stringify({
                                         url: location.href,
                                         title: document.title,
                                         connection: document.querySelector('[data-connection]')?.textContent ?? null,
                                         conversation: document.querySelector('[data-conversation]')?.textContent?.slice(0, 200) ?? null,
                                         degraded: document.querySelector('[data-degraded]')?.getAttribute('data-degraded') ?? null,
                                         // What this client KEPT, by store and entry count. An
                                         // offline page renders out of these, so a store that is
                                         // absent or empty is the difference between "the replay
                                         // is broken" and "there was nothing to replay" — which
                                         // a bare timeout cannot tell you and which cost a full
                                         // run of the gate to tell apart once already.
                                         kept: await (async () => {
                                           try {
                                             const out = {}
                                             for (const n of await caches.keys()) {
                                               out[n] = (await (await caches.open(n)).keys()).length
                                             }
                                             return out
                                           } catch (e) { return 'unreadable: ' + e.message }
                                         })()
                                       })""")
                    with pe -> return "unreadable: " + pe.Message
                }
            printfn "  page: %s" state
            printfn "=== end %s ===" label
            raise e
    }


/// The text an element really carries, with remote-caret widgets subtracted.
///
/// A ProseMirror widget decoration's DOM lives INSIDE the node it is anchored in, so a
/// co-editor's caret label sitting in a heading makes that heading's `textContent` read
/// `"Heading oneada"` rather than `"Heading one"`. An assertion comparing `textContent`
/// exactly is therefore not testing the content — it is testing the content AND whether
/// anybody's caret happens to be parked in it, which is a race nothing in the test controls.
///
/// It cost a full CI run of #210 to learn that, twice, because the failure looks exactly like
/// lost content: the heading never equals the string, the wait never settles, and the page is
/// rendering the words perfectly the whole time.
let private ownTextFn =
    """((el) => el ? [...el.childNodes]
          .filter(n => !(n.nodeType === 1 && n.classList.contains('pm-caret')))
          .map(n => n.textContent).join('') : null)"""

/// The own text of the first match, for a strict comparison.
let private ownText (selector: string) =
    sprintf "%s(document.querySelector('%s'))" ownTextFn selector

/// Does any match carry exactly this text, carets aside? The `.some` form, because a page can
/// hold several editors and the assertion is about one of them saying the words.
let private someOwnText (selector: string) (text: string) =
    sprintf "[...document.querySelectorAll('%s')].some(el => %s(el) === '%s')" selector ownTextFn text

/// A page-function wait that says WHICH wait it was. Playwright reports only "Timeout 30000ms
/// exceeded", and a case with a dozen waits is a case whose red names none of them — telling
/// them apart then costs a full run of the gate per hypothesis, which is how guessing comes to
/// cost the same as looking and wins every time. The label is the whole instrument.
let private waitFor (what: string) (page: IPage) (predicate: string) : Async<unit> =
    async {
        try
            do! await (page.WaitForFunctionAsync predicate) |> Async.Ignore
        with e ->
            failwithf "waiting for %s — %s\n  predicate: %s" what e.Message predicate
    }

// Browser-evaluated predicate strings: JS by necessity — they run inside Chromium via CDP.
let private connected = """document.querySelector('[data-connection]')?.textContent === 'Connected'"""

// The open draft is a ProseMirror editable (`.ProseMirror`) inside the editable
// (`data-rich-readonly="false"`) body-mount host — and it is whichever draft this peer has open,
// which may be someone else's: the composer joins the message already being written. Collapsed
// drafts are read-only one-line summaries.
let private composer = """[data-rich-readonly="false"] .ProseMirror"""

// --- Terminal command lines --------------------------------------------------------------
//
// The composer of the terminal a tab strip is showing: its key names the terminal, so asking
// for one by id also asserts that the pane really moved. `:not([readonly])` picks the line
// this peer may type into rather than a collaborator's mirror of theirs.
let private commandLine (terminal: string) =
    sprintf "[data-terminal-input^='term-draft:%s:']:not([readonly])" terminal

/// What a command line currently says, once it exists.
let private commandLineValue (page: IPage) (selector: string) : Async<string> =
    async {
        let! _ = await (page.WaitForSelectorAsync selector)
        return! await (page.EvaluateAsync<string> ("sel => document.querySelector(sel)?.value ?? ''", selector))
    }

/// Wait for a command line to say something, and when it never does, FAIL SAYING WHAT IT
/// SAYS. The interesting failures here are lines holding the wrong terminal's text or
/// wiping themselves as they are typed into, and a bare timeout hides exactly that.
let private waitCommandLine (page: IPage) (selector: string) (expected: string) : Async<unit> =
    async {
        try
            do!
                await (page.WaitForFunctionAsync (
                        "([sel, want]) => document.querySelector(sel)?.value === want",
                        [| box selector; box expected |]))
                |> Async.Ignore
        with _ ->
            let! actual = commandLineValue page selector
            failwithf "expected the command line %s to say %A, it says %A" selector expected actual
    }

/// The terminals the tab strip is offering, in the order it offers them.
let private terminalTabs (page: IPage) : Async<string[]> =
    await (page.EvaluateAsync<string[]> (
            """() => [...document.querySelectorAll('[data-terminal-tab]')].map(t => t.getAttribute('data-terminal-tab'))"""))

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
                do! waitFor "A to connect" pageA connected
                do! waitFor "B to connect" pageB connected

                // A types Markdown into its rich composer with REAL key events, so the input
                // rules fire: "# " turns the block into a heading rendered live as an <h1> —
                // the syntax itself is never left as literal text (Linear-style WYSIWYG).
                let! _ = await (pageA.WaitForSelectorAsync composer)
                do! awaitU (pageA.ClickAsync composer)
                do! awaitU (pageA.Keyboard.TypeAsync "# Heading one")
                let renderedHeading =
                    sprintf "%s === 'Heading one'" (ownText "[data-rich-readonly=\"false\"] .ProseMirror h1")
                do! waitFor "A's own typing to render as a heading" pageA renderedHeading

                // B converges: it renders A's draft as the same formatted heading.
                // (Regression guard: pushing presence decorations on every render used to starve
                // y-prosemirror's rendering of REMOTE content here, so B's mirror stayed blank.)
                do! waitFor
                        "B's mirror to render A's remote content"
                        pageB
                        (someOwnText ".ProseMirror h1" "Heading one")

                // And B JOINED it rather than opening a rival blank: the composer B is in is A's
                // draft, which is why the "new message" way out is offered at all.
                do! waitFor "B to be offered a way out of A's draft" pageB """!!document.querySelector('[data-draft-new]')"""

                // B overlays A's live caret in it: A's presence (a base64 relative position over
                // the draft body) decodes to a caret widget + name label. This lands just after
                // the content settles (the decoration push is debounced off the active-convergence
                // window). Guards remote BODY cursors end-to-end.
                do! waitFor "B to overlay A's caret" pageB """!!document.querySelector('.pm-caret')"""

                // B CO-EDITS A's draft — the collaboration the read-only mirror used to forbid —
                // and A sees the words appear in the draft it started.
                do! awaitU (pageB.ClickAsync composer)
                do! awaitU (pageB.Keyboard.PressAsync "End")
                do! awaitU (pageB.Keyboard.TypeAsync " and two")
                let coEdited = someOwnText ".ProseMirror h1" "Heading one and two"
                do! waitFor "A to see B's co-edit" pageA coEdited

                // B sends A's draft: any co-editor may. Both timelines show the immutable message.
                // The durable body is MARKDOWN (`# Heading one and two`, from events not Yjs), but
                // the timeline RENDERS it as formatted rich text — the same heading the composer
                // showed — so the sent view mirrors the input: an <h1>, no literal `#`.
                do! awaitU (pageB.ClickAsync "[data-send-draft]")
                let inTimeline = """[...document.querySelectorAll('[data-conversation] [data-message-body] h1')].some(h => h.textContent.trim() === 'Heading one and two')"""
                do! waitFor "A's timeline to show the sent message" pageA inTimeline
                do! waitFor "B's timeline to show the sent message" pageB inTimeline
            }

        // Terminals (Plan 13) in a real browser: the one part of the panel that only a
        // browser can exercise — the `<input>` bound to a `Y.Text` root. Everything under it
        // (the slot rule, the queue, the approval gate, the drain, the transcript) is covered
        // in the cheap tier; what is under test here is the binding itself, and that the
        // command really runs in the session's sandbox.
        //
        // `Srt` because of that last clause, and it is not a formality: the product's default
        // work sandbox IS srt (`SessionMain.fs`, `YESSION_WORK_SANDBOX`), so on a box that
        // cannot host one the command never runs, the block never reaches `ok`, and this waits
        // out its timeout — thirty seconds to report, in effect, "this machine is not a
        // machine this test can run on", which is precisely what a capability is for. It cost
        // an agent a stash-and-re-run to discover that once.
        Tag.needs "a command that really runs" [ Tag.Browser; Tag.Native; Tag.Srt ] (fun () ->
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
            })

        // A command line belongs to ONE terminal. The domain says so — a draft is keyed by
        // terminal AND author precisely so a person can be mid-command in two at once
        // (`BodyKey.terminalDraft`) — and the browser is the only place that promise can
        // break: the pane shows one terminal at a time, so switching tabs hands the SAME
        // `<input>` to a different terminal, and what it writes to has to be the terminal it
        // is showing rather than the one it was first rendered for.
        //
        // The report this comes from: with two terminals open, the older one could not be
        // typed into, and switching away and back left only the last character — a line
        // writing into its neighbour, wiped by every render that pushed its own text back in.
        //
        // `Srt` because opening a terminal starts a shell in the session's work sandbox: on a
        // box that cannot host one, no terminal ever appears and this waits out its timeout
        // instead of failing.
        Tag.needs "two terminals at once" [ Tag.Browser; Tag.Native; Tag.Srt ] (fun () ->
        testCaseAsync "each terminal's composer types into that terminal's command line" <|
            async {
                // The reopen control is present only while the column is SHUT (there are never
                // two controls for one column), and a case that ran before this one may have
                // left it open.
                let! shut =
                    await (pageA.EvaluateAsync<bool> ("""() => !!document.querySelector('[data-terminal-toggle="show"]')"""))
                if shut then do! awaitU (pageA.Locator("[data-terminal-toggle='show']").First.ClickAsync ())
                let! before = terminalTabs pageA
                do! awaitU (pageA.Locator("[data-terminal-new]").First.ClickAsync ())
                do! awaitU (pageA.Locator("[data-terminal-new]").First.ClickAsync ())
                do!
                    await (pageA.WaitForFunctionAsync (
                            "n => document.querySelectorAll('[data-terminal-tab]').length >= n",
                            box (before.Length + 2)))
                    |> Async.Ignore
                let! tabs = terminalTabs pageA
                let opened = tabs |> Array.filter (fun id -> not (Array.contains id before))
                Expect.isTrue (opened.Length >= 2) (sprintf "expected two more terminals, the strip offers: %s" (String.Join (", ", tabs)))
                let one, two = opened.[0], opened.[1]

                // One command, half-written, in the first terminal.
                do! awaitU (pageA.ClickAsync (sprintf "[data-terminal-tab='%s']" one))
                do! awaitU (pageA.ClickAsync (commandLine one))
                do! awaitU (pageA.Keyboard.TypeAsync "echo one")
                do! waitCommandLine pageA (commandLine one) "echo one"

                // The second terminal is a second command line, not the first one's: it opens
                // empty, and typing into it stays in it.
                do! awaitU (pageA.ClickAsync (sprintf "[data-terminal-tab='%s']" two))
                let! fresh = commandLineValue pageA (commandLine two)
                Expect.equal fresh "" "a terminal opens with its own empty command line"
                do! awaitU (pageA.ClickAsync (commandLine two))
                do! awaitU (pageA.Keyboard.TypeAsync "echo two")
                do! waitCommandLine pageA (commandLine two) "echo two"

                // And the half-written command is still where it was written. Both directions,
                // because a line that writes into its neighbour breaks whichever of the two
                // the input was bound to first.
                do! awaitU (pageA.ClickAsync (sprintf "[data-terminal-tab='%s']" one))
                let! kept = commandLineValue pageA (commandLine one)
                Expect.equal kept "echo one" "the first terminal kept the command written in it"
                do! awaitU (pageA.ClickAsync (sprintf "[data-terminal-tab='%s']" two))
                let! keptToo = commandLineValue pageA (commandLine two)
                Expect.equal keptToo "echo two" "and the second kept its own"
            })

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

/// One editor case: a served harness, a browser, a page that is being LISTENED to, and the
/// teardown — so a case is its body and nothing else.
///
/// The listening is the point. `watching`/`reporting` had exactly one call site, in the Native
/// suites, so every case in THIS suite — the one that runs on every pull request — failed
/// saying only that a wait had not settled. A shell that died at load reported as eight
/// anonymous timeouts, and the page had been naming the fault the whole time.
///
/// Teardown runs whether the body throws or not, which is a fix rather than tidying: these
/// cases deliberately re-use ports (`+ 8` and `+ 10` serve two each, and `+ 5`/`+ 7` collide
/// with the mounted suite's), so a listener left bound by a failing case took the NEXT case
/// with it — one failure, two red cases, and the second one a lie.
let private editorCaseOn
    (viewport: (int * int) option)
    (name: string)
    (port: int)
    (body: IPage -> Async<unit>)
    =
    testCaseAsync name <|
        async {
            let server = serveStatic harnessRoot port
            let! pw = await (Playwright.CreateAsync ())
            let! br =
                await (pw.Chromium.LaunchAsync (
                    BrowserTypeLaunchOptions (ExecutablePath = chromiumPath ())))
            let! page =
                match viewport with
                | None -> await (br.NewPageAsync ())
                | Some (width, height) ->
                    async {
                        let! ctx =
                            await (br.NewContextAsync (
                                BrowserNewContextOptions (
                                    ViewportSize = ViewportSize (Width = width, Height = height))))
                        return! await (ctx.NewPageAsync ())
                    }
            page.SetDefaultTimeout 15000.0f
            let evidence = watching page
            let! _ = await (page.GotoAsync (sprintf "http://127.0.0.1:%d/" port))
            let! outcome = Async.Catch (reporting name page evidence (body page))
            do! awaitU (br.CloseAsync ())
            pw.Dispose ()
            server.Stop ()
            match outcome with
            | Choice1Of2 () -> ()
            | Choice2Of2 e -> raise e
        }

/// A case at the browser's own window size.
let private editorCase = editorCaseOn None

/// A case at a stated viewport. `ViewportSize` alone, never `IsMobile`: that additionally asks
/// Chromium to fit the layout to a device window, which measured here lands at 648px rather
/// than 390 — the very lie the ui-exploration skill warns about, arriving through another door.
let private editorCaseIn (width: int) (height: int) = editorCaseOn (Some (width, height))

let editorTests =
    testList "Editor rendering (browser)" [
        editorCase "Markdown typed in the rich editor renders formatted and round-trips to Markdown" EDITOR_PORT <| fun page ->
            async {
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
            }

        editorCase "Enter sends, Shift+Enter breaks the line, Alt+Enter opens a paragraph" (EDITOR_PORT + 2) <| fun page ->
            async {
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
                        "document.querySelectorAll('#host .ProseMirror > p').length === 1")
                let! broken = await (page.EvaluateAsync<string> "() => window.__md()")
                Expect.stringContains broken "first line" "the text before the break survived"
                Expect.stringContains broken "same paragraph" "and the text after it"
                Expect.isFalse (broken.Trim().Contains "\n\n") "a line break is not a paragraph break"

                // Alt+Enter is where the PARAGRAPH went: a second block, and no second send.
                do! awaitU (page.Keyboard.PressAsync "Alt+Enter")
                do! awaitU (page.Keyboard.TypeAsync "second block")
                let! _ =
                    await (page.WaitForFunctionAsync
                        "document.querySelectorAll('#host .ProseMirror > p').length === 2")
                let! md = await (page.EvaluateAsync<string> "() => window.__md()")
                Expect.stringContains md "first line" "the first block survived"
                Expect.stringContains md "second block" "Alt+Enter opened a second block"
                let! sends = await (page.EvaluateAsync<int> "() => window.__sends")
                Expect.equal sends 1 "neither Shift+Enter nor Alt+Enter sent"
            }

        editorCase "a remote peer's selection renders as a caret widget, label, and highlight" (EDITOR_PORT + 1) <| fun page ->
            async {
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
                return ()
            }

        // The invariant a caret has to keep out of the way of. Pinned in the CHEAP browser
        // tier because the only thing that could see it before was the two-peer WebRTC E2E,
        // and only under enough CI load to lose the race — a thirty-second wait that says
        // "timed out" and nothing else, twice, before the labels went on.
        //
        // What it pins is not the caret. It is that drawing one cannot cost a co-editor the
        // words: `PushPresences` dispatches a stepless ProseMirror transaction, ProseMirror
        // runs `ySyncPlugin`'s `view.update` on every state update regardless, and that hook
        // reconciles the WHOLE document back into Yjs. Do it while content is still arriving
        // and the push races the words it is drawing over.
        editorCase "a caret drawn on every frame never costs the mirror its content" (EDITOR_PORT + 11) <| fun page ->
            async {
                do! waitFor "both peers to mount" page "!!document.querySelector('#peer-a .ProseMirror') && !!document.querySelector('#peer-b .ProseMirror')"

                // Carets first, and left running for the whole case: the push has to be in
                // flight WHILE the content arrives, which is the only arrangement in which it
                // can race anything. Started before a single keystroke so no frame of the
                // convergence happens un-stormed.
                do! awaitU (page.EvaluateAsync "() => window.__caretStorm(true)")

                // A types into its own composer. Real key events, so every keystroke is its own
                // doc update and its own relay — the drip a collaborator actually produces,
                // rather than one paste the mirror could absorb in a single frame.
                do! awaitU (page.ClickAsync "#peer-a .ProseMirror")
                do! awaitU (page.Keyboard.TypeAsync "# Heading one")

                // The co-editor shows what was typed. This is the assertion the whole surface
                // exists for: it goes red when a caret push costs the content it draws over.
                //
                // On failure it says WHICH of the four stories happened, because the DOM alone
                // cannot: what each doc holds and what each editor rendered, side by side.
                try
                    do! waitFor
                            "the co-editor to render the author's remote content"
                            page
                            (sprintf "%s === 'Heading one'" (ownText "#peer-b .ProseMirror h1"))
                with e ->
                    let! state = await (page.EvaluateAsync<string> "() => window.__convState()")
                    return failwithf "%s\n  state: %s" e.Message state

                // ...and the caret really is drawn there, so this is a test of a caret over
                // content and not of an editor nobody decorated.
                do! waitFor "the author's caret to be drawn in the mirror" page "!!document.querySelector('#peer-b .pm-caret')"

                do! awaitU (page.EvaluateAsync "() => window.__caretStorm(false)")

                // Anti-vacuity, and the reason a green here means anything: a storm that never
                // ran converges beautifully. Frames are not free to assume — a page the browser
                // decided not to paint would push none of them.
                let! pushes = await (page.EvaluateAsync<int> "() => window.__caretPushes")
                if pushes < 5 then
                    failwithf
                        "the caret storm pushed %d times — too few for convergence to have been raced at all, so this case proved nothing"
                        pushes
            }

        // The other half of the same story, and the one the whole `pushPresences` debate turned
        // on: drawing a remote caret must not WRITE to the shared document.
        //
        // It is not obvious that it doesn't. `PushPresences` dispatches a stepless ProseMirror
        // transaction, and `ySyncPlugin`'s `view.update` hook — which runs on every state update,
        // `docChanged` or not — reconciles the whole document back into Yjs through
        // `_prosemirrorChanged`. The reconciliation is real and it is O(document); what this
        // pins is that it emits nothing, so a caret is a read of the doc and never a write to it.
        //
        // Counted from OUR side rather than by patching the library: Yjs updates on the
        // co-editor's doc whose origin is `ySyncPluginKey`, which is what that write-back tags
        // its transaction with. Real doc updates, not a hook we hoped was called.
        editorCase "drawing a remote caret writes nothing to the shared document" (EDITOR_PORT + 12) <| fun page ->
            async {
                do! waitFor "both peers to mount" page "!!document.querySelector('#peer-a .ProseMirror') && !!document.querySelector('#peer-b .ProseMirror')"
                do! awaitU (page.EvaluateAsync "() => window.__caretStorm(true)")
                do! awaitU (page.ClickAsync "#peer-a .ProseMirror")
                do! awaitU (page.Keyboard.TypeAsync "# Heading one")
                // Over CONTENT: an empty document is the case y-prosemirror short-circuits
                // anyway (`initialContentChanged`), so a storm over nothing would prove nothing.
                do! waitFor
                        "the co-editor to render the author's remote content"
                        page
                        (sprintf "%s === 'Heading one'" (ownText "#peer-b .ProseMirror h1"))
                do! awaitU (page.EvaluateAsync "() => window.__caretStorm(false)")

                let! pushes = await (page.EvaluateAsync<int> "() => window.__caretPushes")
                if pushes < 5 then
                    failwithf "the caret storm pushed %d times — too few to have exercised the write-back at all" pushes
                // The doc really moved under the storm — otherwise a write-back count of zero
                // says the observer was never wired, not that nothing was written.
                let! updates = await (page.EvaluateAsync<int> "() => window.__docUpdates")
                if updates < 1 then
                    failwith "the co-editor's doc took no updates at all, so a zero write-back count means the counter is broken, not that a caret is harmless"
                let! writebacks = await (page.EvaluateAsync<int> "() => window.__writebacks")
                Expect.equal
                    writebacks
                    0
                    (sprintf
                        "%d caret pushes produced %d Yjs updates from y-prosemirror's own write-back — drawing a caret is mutating the shared document, which is a co-editor's words at risk"
                        pushes writebacks)
            }

        // The replay (Plan 13, stage 3e). Everything else about it is pinned DOM-free — the
        // `.cast` rebuild against the real file, the closed tab, the retention gap — but not
        // this: whether `asciinema-player`'s named export resolves through the bundle and
        // actually plays what was recorded. An import that silently failed would leave every
        // other test green and the feature dead in the browser.
        editorCase "a recorded terminal replays in a real player, and prints what it printed" (EDITOR_PORT + 3) <| fun page ->
            async {
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
                return ()
            }

        // The same player over a recording with DEAD AIR in it (Plan 25, stage 1) — the shape
        // the pane actually replays, and the one the case above cannot fail in: its recording
        // has no gap to compress and its single chapter sits at t=0, so chapters on the wrong
        // clock still look right there.
        //
        // The arrangement is shared by the two cases below and lives in the harness
        // (`#replay-gappy`): thirty seconds of nothing between two commands, a chapter on each,
        // and a start position naming the far one. What each case asserts is its own.
        editorCase "a chapter past a long idle gap still reaches the chapter list" (EDITOR_PORT + 13) <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#replay-gappy .ap-player")
                let! _ = await (page.WaitForSelectorAsync "#replay-gappy .ap-overlay-start")
                do! awaitU (page.ClickAsync "#replay-gappy .ap-overlay-start")
                // Both of them. The player idle-compresses the EVENTS it loads, so a chapter
                // list built on the recording's raw clock disagrees with them: the far chapter
                // becomes the last event and the list's own `time < duration` filter drops it,
                // leaving one mark where the recording has two. Chapters written into the cast
                // as `"m"` events ride the same compression as the records around them.
                //
                // Asserted after play, like the case above: where a mark sits on the bar is
                // not known until the recording's duration is.
                let! _ =
                    await (page.WaitForFunctionAsync
                        "document.querySelectorAll('#replay-gappy .ap-marker').length === 2")
                return ()
            }

        editorCase "a watch that starts past a long idle gap lands there, not before it" (EDITOR_PORT + 14) <| fun page ->
            async {
                let! _ = await (page.WaitForSelectorAsync "#replay-gappy .ap-overlay-start")
                do! awaitU (page.ClickAsync "#replay-gappy .ap-overlay-start")
                // The WAIT is the assertion, and the timeout is what makes it one: a start
                // position the player honours puts this text on screen as fast as it can
                // start, while one that lands short of the gap could only reach it after the
                // gap has played out in real time.
                let! _ =
                    await (page.WaitForFunctionAsync (
                        "document.querySelector('#replay-gappy').textContent.includes('second')",
                        null,
                        PageWaitForFunctionOptions (Timeout = 10_000f)))
                return ()
            }

        // Terminal work in the chat, and the pane's tabs (Plan 14, stages 1-2). Host-free,
        // like the editor and the replay beside it: what needs a real browser here is not the
        // Session Process but the DOM swaps — where FOCUS goes when a chip in the chat opens
        // a tab in the pane, and whether the tab strip is a tablist the arrow keys walk.
        // Neither is visible to a rendered string, and both are the WCAG floor rather than a
        // nicety: a chip that opens a pane and leaves focus behind, or a close that strands
        // focus on a control it just removed, is exactly the failure the floor names.
        editorCase "a chat chip opens a pane tab that plays, and the strip walks" (EDITOR_PORT + 4) <| fun page ->
            async {
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

                // The block reads as TEXT, and its recording is one press away — the two reads
                // of one history, with the cheap one first. Pressing play mounts the real
                // player over the ranged cast the model built, inside the tab the chip
                // opened: a stream renderer would show a cursor-moving program as garbage,
                // which is the whole reason the transcript was written as asciicast.
                let! _ = await (page.WaitForSelectorAsync "#shell [data-pane-block] [data-terminal-output]")
                do! awaitU (page.ClickAsync "#shell [data-pane-play]")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-pane-replay] .ap-overlay-start")
                do! awaitU (page.ClickAsync "#shell [data-pane-replay] .ap-overlay-start")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.querySelector('#shell [data-pane-block]')?.textContent.includes('total 0') === true""")
                return ()
            }
        // The phone (Plan 14, stage 5). Headless Chromium clamps its WINDOW to ~500px, which
        // is why a naive narrow screenshot lies; Playwright's viewport is a real CDP device
        // metrics override, so 390 here is 390. The two things this asserts are the two the
        // plan is about: the pane takes the whole column rather than sitting over the chat
        // as a dismissible overlay, and nothing overflows sideways — an overflow a phone
        // user cannot scroll away is a reachability bug, not a cosmetic one.
        editorCaseIn 390 844 "on a phone the pane IS the column, the strip stays, and the chat is one control away" (EDITOR_PORT + 5) <| fun page ->
            async {
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
                return ()
            }
        // What an agent says does not fit a phone: paths, URLs and fenced commands are all
        // longer than a 326px column and none of them has a space where the break has to go.
        // The timeline is a scroller on the vertical axis and therefore on both, so anything
        // that hangs out of the column slides the whole conversation sideways under a header
        // that stays put — which is what it looked like on iOS: every message shifted a
        // character or two off the left edge, with no way to put it back.
        //
        // The document-level check the case above makes cannot see this: the timeline's own
        // scrollbox absorbs the overflow, so `documentElement.scrollWidth` stays honest while
        // the conversation is unreadable. What is asserted is the column, and only the column.
        editorCaseIn 390 844 "a message no line break fits inside never scrolls the timeline sideways" (EDITOR_PORT + 10) <| fun page ->
            async {
                let! width = await (page.EvaluateAsync<int> "() => window.innerWidth")
                Expect.equal width 390 "a true phone viewport, not a clamped window"

                // The fixture's wide message is present — otherwise this passes by rendering
                // nothing that could have overflowed.
                let! _ = await (page.WaitForSelectorAsync "#shell [data-conversation] [data-message-body] pre")
                let! sideways =
                    await (page.EvaluateAsync<bool>
                            """() => {
                                 const timeline = document.querySelector('#shell [data-conversation]')
                                 return timeline.scrollWidth > timeline.clientWidth + 1
                               }""")
                Expect.isFalse sideways "the conversation column does not scroll sideways"
            }
        // The split between the two columns is the reader's to set. What is pinned is the
        // PROMISE, not the geometry: that the divider can be moved without a pointer at all.
        // A splitter that only answers a drag is a control a keyboard user cannot reach, and
        // nothing else in this suite would notice — the column would still render, still
        // scroll, and still be exactly the width somebody else chose for them.
        //
        // Deliberately not asserted: the default width, the step size, the bounds. Those are
        // the design, and the design changing is not a regression.
        editorCaseIn 1440 900 "the column divider moves from the keyboard, not only from a drag" (EDITOR_PORT + 8) <| fun page ->
            async {
                // Waited for on the CONTROL, never on the panel: a shut pane is `w-0`, which
                // Playwright reports as hidden, so waiting for the panel to be visible before
                // opening it waits for something that only happens afterwards.
                let! _ = await (page.WaitForSelectorAsync "#shell [data-terminal-toggle='show']")
                // This page carries two other fixtures ABOVE the shell — the editor host and
                // the player — so the shell starts a viewport and a half down. Every control
                // in it is reachable by scroll, which is fine for a person and a trap for a
                // test: the assertions here are about a WIDTH, and a click that has to scroll
                // first is one more thing that can be the reason a width did not change.
                let! _ =
                    await (page.EvaluateAsync<bool>
                            """() => {
                                 for (const id of ['host', 'replay']) {
                                   const el = document.getElementById(id)
                                   if (el) el.style.display = 'none'
                                 }
                                 document.querySelector('#shell [data-terminal-toggle="show"]').click()
                                 return true
                               }""")
                // Read on `aria-valuenow`, not on the rendered width.
                //
                // Not a convenience: the column animates, so its rendered width spends 200ms
                // being neither the old value nor the new one, and every way of asking "has it
                // settled" from the outside is a heuristic. Sampling twice and comparing was
                // the one tried here, and it accepted a 1px panel — a shut pane is its own left
                // border — as "open and settled" because two polls happened to agree before the
                // transition started. It passed twice and failed the third time, which is worse
                // than not having been written.
                //
                // The separator's value is the state the shell actually owns: the keydown
                // handler sets it synchronously, so there is nothing to wait for and nothing to
                // race. It is also the thing this test is ABOUT — what a keyboard user is told
                // the split is. That the pixels follow it is asserted once at the end, where
                // the target is known and the wait is therefore deterministic.
                let value () =
                    page.EvaluateAsync<float>
                        "() => Number(document.querySelector('#shell [data-term-resize]').getAttribute('aria-valuenow'))"

                // Focusable, and it says what it is: a separator with a value is the one
                // shape assistive technology can report and move.
                let! _ =
                    await (page.EvaluateAsync<bool>
                            "() => { document.querySelector('#shell [data-term-resize]').focus(); return true }")
                let! isSeparator =
                    await (page.EvaluateAsync<bool>
                            """() => {
                                 const h = document.activeElement
                                 return h?.getAttribute('role') === 'separator'
                                     && h.hasAttribute('aria-valuenow')
                               }""")
                Expect.isTrue isSeparator "the focused divider is a separator carrying its value"
                let! before = await (value ())

                // The two arrows move it, and they are opposites. Which one GROWS the column
                // is deliberately not asserted: this one is on the right, so left-grows reads
                // as "drag its edge", but a design that put the pane elsewhere or read the
                // keys the other way round would be a different choice rather than a broken
                // one — and a test that failed for it would be reporting taste as a
                // regression. What has to hold is that a keyboard can move the split at all,
                // and that the second press undoes the first.
                do! awaitU (page.Keyboard.PressAsync "ArrowLeft")
                let! moved = await (value ())
                Expect.isTrue
                    (abs (moved - before) > 1.0)
                    (sprintf "an arrow key must move the split (was %f, still %f)" before moved)
                do! awaitU (page.Keyboard.PressAsync "ArrowRight")
                let! back = await (value ())
                Expect.isTrue
                    (abs (back - before) < abs (moved - before))
                    (sprintf "the opposite arrow must move it back (started %f, went %f, now %f)"
                        before moved back)

                // And the column is really that wide, once it has finished travelling there.
                // Deterministic because the destination is declared: what is waited for is the
                // pixels catching up to the value, not a guess about when a transition ended.
                // Without this the test would be happy with a separator that narrates a resize
                // nothing performed.
                let! _ =
                    await (page.WaitForFunctionAsync
                            """() => {
                                 const h = document.querySelector('#shell [data-term-resize]')
                                 const pane = document.querySelector('#shell [data-terminal-panel]')
                                 const said = Number(h.getAttribute('aria-valuenow'))
                                 return Math.abs(pane.getBoundingClientRect().width - said) <= 1
                               }""")
                return ()
            }
        // The live viewport (Plan 14, stage 6). What only a browser can answer here is the
        // KEYSTROKE TRANSLATION: a `KeyboardEvent` is not a byte stream, and turning one
        // into what a pty expects — printable characters as themselves, Ctrl-<key> as the
        // control code, the keys with no character at all as their escape sequences — is the
        // whole of what a terminal front end does with a keyboard.
        editorCase "the holder types into the live screen, and the keys reach it as a pty expects" (EDITOR_PORT + 6) <| fun page ->
            async {
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
            }
        // The DVR (Plan 14, stage 7). What only a browser can answer: that rewinding a LIVE
        // terminal really mounts a player over what it has recorded so far — the same player
        // and the same cast a finished terminal's replay uses, which is what "rewound like
        // live TV, through the same mechanism" has to mean — that it lands ON the pinned
        // edge rather than at the recording's start, that focus survives the control swap,
        // and that playing off the pinned end catches the reader back up to live by itself.
        editorCase "a live terminal rewinds to its pinned edge, and playing off it catches back up" (EDITOR_PORT + 7) <| fun page ->
            async {
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
                return ()
            }

        // Pins (Plan 20, stage 1). The pin's STATE is a rendered attribute the cheap tier can
        // read; what needs a browser is the keyboard release — Delete on a focused tab
        // removes that tab from the document, and focus has to land on what took its place
        // rather than on `body`. Same floor the DVR's control swap answers, in the surface a
        // keyboard user actually walks.
        editorCase "a tab is kept by its pin and released from the keyboard, without stranding focus" (EDITOR_PORT + 9) <| fun page ->
            async {
                // A chip's tab arrives previewed — kept by nothing — and selected, since
                // tapping the chip is what put it there.
                do! awaitU (page.ClickAsync "#shell [data-chat-block]")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-pane-tab^='block:']")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.querySelector("#shell [data-pane-tab^='block:']")?.getAttribute('data-pane-tab-pinned') === 'false'""")

                // Activating the tab you are already on is the pin. There is no second
                // control to hit — which is the point on a touch screen, where the second
                // control was a 24px target beside a 30px one.
                do! awaitU (page.ClickAsync "#shell [data-pane-tab^='block:']")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.querySelector("#shell [data-pane-tab^='block:']")?.getAttribute('data-pane-tab-pinned') === 'true'""")
                // And it says so where anything that cannot see a blue glyph can read it.
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.querySelector("#shell [data-pane-tab^='block:'] [role='img']")?.getAttribute('aria-label') === 'pinned'""")

                // Move on to something else. The pinned tab stays, which is what a pin is
                // for — a preview would have been replaced here.
                do! awaitU (page.ClickAsync "#shell [data-terminal-tab='term-harness']")
                let! _ = await (page.WaitForSelectorAsync "#shell [data-pane-tab^='block:']")

                // Delete on the focused tab releases it. The tab leaves the strip, and focus
                // lands on whatever took its position — never nowhere.
                do! awaitU (page.FocusAsync "#shell [data-pane-tab^='block:']")
                do! awaitU (page.Keyboard.PressAsync "Delete")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.querySelectorAll("#shell [data-pane-tab^='block:']").length === 0""")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.activeElement?.closest('[role="tab"]') !== null""")
                return ()
            }

        // The terminal list (Plan 20, stage 0). WHICH verbs a row offers is a fold the cheap
        // tier already pins; what only a browser can answer is the DOM swap — the list
        // replaces the strip and the pane's body at once, so choosing a row removes the
        // control that was pressed, and focus has to land on what replaced it rather than
        // on `body`. That is the WCAG floor, not a nicety.
        editorCase "the list opens a terminal and hands focus to the pane it replaced itself with" (EDITOR_PORT + 8) <| fun page ->
            async {
                do! awaitU (page.ClickAsync "#shell [data-terminal-toggle='show']")
                do! awaitU (page.ClickAsync "#shell [data-terminal-list-toggle='list']")

                // One surface at a time: the tablist promises a panel showing one of its
                // tabs, and it must not be left standing over a list that replaced it.
                let! _ = await (page.WaitForSelectorAsync "#shell [data-terminal-list]")
                let! _ = await (page.WaitForFunctionAsync """!document.querySelector("#shell [role='tablist']")""")

                // Every terminal the session has is reachable here, whether or not the strip
                // would have carried it.
                let! rows = await (page.EvaluateAsync<int> "() => document.querySelectorAll('#shell [data-terminal-list-row]').length")
                Expect.equal rows 3 "every terminal the harness has, the closed one included"

                // Choosing a row shows that terminal AND leaves the list — one act — so the
                // row that was pressed is gone from the document by the time focus moves.
                // Driven from the KEYBOARD, because that is the half of this a click cannot
                // answer: a row has to be a real control somebody can reach and press
                // without a pointer, and the focus move afterwards is what the floor asks
                // for when the pressed control leaves the document.
                do! awaitU (page.FocusAsync "#shell [data-terminal-list-row='term-live']")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                let! _ = await (page.WaitForFunctionAsync """!document.querySelector('#shell [data-terminal-list]')""")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.querySelector('#shell [data-pane-panel]')?.getAttribute('data-pane-panel') === 'terminal:term-live'""")
                let! _ = await (page.WaitForFunctionAsync """document.activeElement?.hasAttribute('data-pane-panel') === true""")
                return ()
            }

        // Task cards (Plan 20, stage 4). WHICH commands group, in what order, and what the
        // summary counts are all folds the cheap tier pins. What only a browser can answer is
        // that grouping does not cost a person a control: a burst's commands are behind a
        // disclosure now, and a line inside it has to be the same reachable, pressable thing
        // the chip was before it was grouped. Nothing here asserts the card's layout — that
        // is the design, and the design is what a card is FOR.
        editorCase "a task card's lines stay real controls, reachable and pressable without a pointer" (EDITOR_PORT + 10) <| fun page ->
            async {
                // A real `<details>`, so the disclosure is the browser's: keyboard-operable
                // and announced without a handler or an ARIA role of our own.
                let! _ = await (page.WaitForSelectorAsync "#shell [data-chat-task-card]")
                do! awaitU (page.FocusAsync "#shell [data-chat-task-card] summary")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.querySelector('#shell [data-chat-task-card]')?.open === true""")

                // The failed command leads, which is the one thing the ordering promises —
                // and it is a BUTTON, not a div someone hung a click on.
                let! first =
                    await (page.EvaluateAsync<string>
                        """() => {
                             const line = document.querySelector('#shell [data-chat-task-card] [data-chat-block]')
                             return line.tagName + ':' + line.getAttribute('data-chat-block')
                           }""")
                Expect.equal first "BUTTON:block-burst-failed" "the failure leads, as a real button"

                // And pressing it does what an ungrouped chip does: opens that block.
                do! awaitU (page.Keyboard.PressAsync "Tab")
                do! awaitU (page.Keyboard.PressAsync "Enter")
                let! _ =
                    await (page.WaitForFunctionAsync
                        """document.querySelector('#shell [data-pane-panel]')?.getAttribute('data-pane-panel') === 'block:term-harness:block-burst-failed'""")
                return ()
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

// --- Reopening a session that is gone (Plan 20, Plan 22) ---------------------------------
// The arrangement every offline-reopen case shares: a mounted session behind a proxy, a
// browser on it, something done that makes history, the worker confirmed running, the session
// killed, and a reload. Hoisted because it is the SETUP that repeats — what each case then
// asserts about the page that came back is its own, and its red names it alone.

let private saidInTimeline = "still here when the session is not"

let private inTimeline =
    sprintf
        """document.querySelector('[data-conversation]')?.textContent?.includes('%s') === true"""
        saidInTimeline

let private printedInTerminal = "printed-before-the-session-died"

/// Has this client actually KEPT the thing the reload will replay?
///
/// On screen is not kept. The live leg puts a record in the model the moment it arrives, and
/// the SAME record triggers a separate HTTP read that is what writes it to the store
/// (`App.fs`: `TerminalRecordMsg` / `EventsAvailable` -> fetch -> `cache.Write`), started with
/// `Async.StartImmediate` and awaited by nothing. So there is a window in which the assertion
/// these cases make about the LIVE page is already true and the store behind the reload is
/// still empty — measured at 1 run in 3 on an idle box, and wider on a loaded CI runner, where
/// killing the session inside it left the reload nothing to replay and the case timed out
/// naming no cause. Waiting on the store is the difference between testing this and testing
/// that race, exactly as waiting on the worker's registration is below.
let private keptIn (cachePart: string) (text: string) =
    sprintf
        """(async () => {
             try {
               const names = (await caches.keys()).filter(n => n.includes('%s'))
               for (const n of names) {
                 const c = await caches.open(n)
                 for (const req of await c.keys()) {
                   const r = await c.match(req)
                   if (r && (await r.text()).includes('%s')) return true
                 }
               }
             } catch (e) { return false }
             return false
           })()"""
        cachePart
        text

let private transcriptKept = keptIn "/terminals/" printedInTerminal
let private conversationKept = keptIn "/events" saidInTimeline

let private terminalPrinted =
    sprintf
        """[...document.querySelectorAll('[data-terminal-output]')].some(o => o.textContent.includes('%s'))"""
        printedInTerminal

/// `make` runs against a live session and leaves history behind; `check` runs against the
/// page that came back with the session dead.
let private offlineReopen (name: string) (make: IPage -> Async<unit>) (check: IPage -> Async<unit>) =
    testCaseAsync name <|
        async {
            if Directory.Exists mountDataDir then Directory.Delete (mountDataDir, true)
            startMountedHost ()
            let proxy = startMountProxy MOUNT_PROXY_PORT (fun () -> mountSessionPort)
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
                let evidence = watching page
                do! reporting name page evidence <| async {
                let! _ = await (page.GotoAsync publicUrl)
                let! _ = await (page.WaitForFunctionAsync connected)

                do! make page

                // The worker has to be RUNNING before the session goes, or the reload has
                // nothing serving it. Waiting on the registration is the difference between
                // testing this and testing a race.
                let! _ =
                    await (page.WaitForFunctionAsync
                            "navigator.serviceWorker.ready.then(r => !!r.active)")

                // Gone, and staying gone. The proxy stays up, which is the honest shape of a
                // reaped session behind an operator's front door — and it means the shell
                // request comes back 502 rather than failing outright, which the worker has
                // to treat as the failure it is.
                mountedHost.Kill true
                mountedHost.WaitForExit ()

                let! _ = await (page.ReloadAsync ())

                // The page loaded at all — the whole of what the worker adds, and the
                // precondition every case here is really about rather than the thing it
                // asserts.
                let! _ = await (page.WaitForSelectorAsync "[data-conversation]")

                do! check page
                }
            finally
                browserToClose |> Option.iter (fun b -> b.CloseAsync () |> ignore)
                playwrightToDispose |> Option.iter (fun p -> p.Dispose ())
                proxy.Stop ()
                try mountedHost.Kill true with _ -> ()
        }

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

                    // Installable, and installable AS ITSELF. The manifest is addressed
                    // relatively like every other route here, and the URLs INSIDE it resolve
                    // against its own address — so the app a person adds to their home screen
                    // launches at this session rather than at whatever sits on the origin's
                    // root. Resolved by the browser rather than compared as text, because the
                    // resolution is the property; the icon is fetched for the same reason a
                    // manifest naming an icon nobody serves would still parse.
                    let! installed =
                        await (page.EvaluateAsync<string>
                                """async () => {
                                     const href = document.querySelector('link[rel=manifest]').href
                                     const manifest = await (await fetch(href)).json()
                                     const icon = await fetch(new URL(manifest.icons[0].src, href))
                                     return [new URL(manifest.start_url, href).pathname,
                                             new URL(icon.url).pathname,
                                             icon.status,
                                             icon.headers.get('content-type')].join(' ')
                                   }""")
                    Expect.equal
                        installed
                        (sprintf "/s/%s/ /s/%s/icon.png 200 image/png" MOUNT_SESSION MOUNT_SESSION)
                        "the installed app starts at this session, and its mark is served under the same mount"

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

        // THE bug report, and the last thing plan 20 owed: open a session you have read
        // before, with the session gone, and read it. Plan 22 adds the terminal.
        //
        // Everything it needs was built in the four steps before this one — the log addressed
        // so its tail can be kept, the client keeping it, the replay that runs before the
        // client knows whether it may connect, and a timeline that does not claim emptiness
        // it has not checked. What was missing was a PAGE: the shell is `no-cache`, and a dead
        // host cannot answer a revalidation, so until the worker there was nothing to render
        // any of it into. That is why this case could not go green before now, and why it
        // merges with the worker rather than sitting red beside it.
        offlineReopen
            "the session is gone, the page is still there, and so is its conversation"
            (fun page ->
                async {
                    // Say something, so there is history to lose.
                    let composerSel = """[data-rich-readonly="false"] .ProseMirror"""
                    let! _ = await (page.WaitForSelectorAsync composerSel)
                    do! awaitU (page.ClickAsync composerSel)
                    do! awaitU (page.Keyboard.TypeAsync saidInTimeline)
                    do! awaitU (page.Locator("[data-send-draft]").First.ClickAsync ())
                    do! waitFor "the message in the timeline" page inTimeline
                    do! waitFor "the conversation to be kept" page conversationKept
                })
            (fun page ->
                async {
                    // It has what this client had been reading. Scoped to the conversation
                    // element: a page-level match is satisfied by the roster and by the
                    // composer this text was typed into.
                    do! waitFor "the conversation to come back offline" page inTimeline
                })

        // The connection state is its own promise, and its own way to break: a client that
        // replayed its history out of its own store and then claimed to be CONNECTED to the
        // session it replayed without would be lying about the one leg that is down.
        offlineReopen
            "a client reading its own history does not claim to be connected"
            (fun _ -> async { return () })
            (fun page ->
                async {
                    do!
                        waitFor
                            "the client to stop claiming it is connected"
                            page
                            """document.querySelector('[data-connection]')?.textContent !== 'Connected'"""
                })

        // Plan 22, and the other half of the bug report: the conversation came back offline
        // and the terminal under it did not. `Srt` because this really runs a command — on a
        // box that cannot host a sandbox the block never reaches `ok` and this would HANG
        // rather than fail.
        Tag.needs "a terminal that really ran something" [ Tag.Browser; Tag.Native; Tag.Srt ] (fun () ->
        offlineReopen
            "the session is gone, the page is still there, and so is what its terminal printed"
            (fun page ->
                async {
                    do! awaitU (page.Locator("[data-terminal-toggle='show']").First.ClickAsync ())
                    do! awaitU (page.Locator("[data-terminal-new]").First.ClickAsync ())
                    let composerInput = "[data-terminal-input^='term-draft:']:not([readonly])"
                    let! _ = await (page.WaitForSelectorAsync composerInput)
                    do! awaitU (page.ClickAsync composerInput)
                    do! awaitU (page.Keyboard.TypeAsync (sprintf "echo %s" printedInTerminal))
                    do! awaitU (page.ClickAsync "[data-terminal-send]")
                    do! waitFor "the printed line on screen" page terminalPrinted
                    do! waitFor "the transcript to be kept" page transcriptKept
                })
            (fun page ->
                async {
                    // The column starts shut on a fresh load, so this also says the replayed
                    // records are there to be shown BEFORE anyone opens it — which is what a
                    // store read before the network buys.
                    do! awaitU (page.Locator("[data-terminal-toggle='show']").First.ClickAsync ())
                    do! waitFor "the terminal output to come back offline" page terminalPrinted
                }))
    ]

#else

// Fable (JS on Node): Playwright is a .NET driver and does not exist here, so the flows above
// are compiled out. These stubs only exist so the module compiles under Fable; they are never
// forced — the `[Browser]` need fails on Node and reports the skip itself.
let tests : Fable.Pyxpecto.Model.TestCase = testList "Browser E2E" []
let editorTests : Fable.Pyxpecto.Model.TestCase = testList "Editor rendering (browser)" []
let mountedTests : Fable.Pyxpecto.Model.TestCase = testList "Path-mounted session (browser)" []

#endif
