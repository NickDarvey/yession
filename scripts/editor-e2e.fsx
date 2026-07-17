// High-signal browser E2E for the Linear-style rich-text composer (docs/plans/03-rich-text-editing.md).
// The headline behaviour — TYPE MARKDOWN, SEE IT FORMATTED — can only be checked in a real
// contenteditable with real key events, so it lives here (Microsoft.Playwright .NET, the repo's
// browser driver). One high-signal assertion, not coverage: the DOM-free serialization / copy /
// convergence / registry guards live in the cheap tier (tests/Yession.Tests/Editor.fs).
//
// Self-contained: it statically serves the already-built client bundle (app/out/public/client.js,
// produced by the verify tier before this runs) and drives it with `?rich=1`. No Session Process
// and no WebRTC — the app is local-first, so the composer mounts before the network — which also
// lets this run wherever the native transport can't be built.
//
//     dotnet fsi scripts/editor-e2e.fsx

#r "nuget: Microsoft.Playwright, 1.61.0"

open System
open System.IO
open System.Net
open System.Text
open System.Text.RegularExpressions
open System.Threading.Tasks
open Microsoft.Playwright

let PORT = 8188
let BASE = sprintf "http://127.0.0.1:%d/" PORT
let publicDir = "app/out/public"
let clientBundle = Path.Combine (publicDir, "client.js")

// --- Chromium discovery (as in browser-e2e.fsx) ----------------------------------------
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
                try Directory.EnumerateFiles (root, "chrome", SearchOption.AllDirectories) |> Seq.toList with _ -> []
            else []
        match fromRoot @ [ "/usr/bin/chromium"; "/usr/bin/chromium-browser"; "/usr/bin/google-chrome" ] |> List.tryFind File.Exists with
        | Some c -> c
        | None -> eprintfn "no Chromium found; set CHROMIUM_PATH"; exit 4

// The bootstrap page: #app + the session meta (session-keyed IndexedDB) + the client bundle,
// exactly the shape the Session Process serves, minus the server.
let indexHtml =
    """<!doctype html><html><head><meta charset="utf-8">
<meta name="yession-session" content="editor-e2e"></head>
<body><div id="app"></div><script type="module" src="/client.js"></script></body></html>"""

// --- A tiny static file server (no authored JS; F# HttpListener) -----------------------
let startServer () : HttpListener =
    let listener = new HttpListener ()
    listener.Prefixes.Add BASE
    listener.Start ()
    let rec loop () =
        task {
            let! ctx = listener.GetContextAsync ()
            let path = ctx.Request.Url.AbsolutePath
            let write (bytes: byte[]) (ct: string) =
                ctx.Response.ContentType <- ct
                ctx.Response.OutputStream.Write (bytes, 0, bytes.Length)
                ctx.Response.Close ()
            if path = "/" || path = "/index.html" then
                write (Encoding.UTF8.GetBytes indexHtml) "text/html"
            elif File.Exists (publicDir + path) then
                let ct = if path.EndsWith ".js" then "text/javascript" elif path.EndsWith ".css" then "text/css" else "application/octet-stream"
                write (File.ReadAllBytes (publicDir + path)) ct
            else
                ctx.Response.StatusCode <- 404
                ctx.Response.Close ()
            return! loop ()
        }
    loop () |> ignore
    listener

// --- The test flow ---------------------------------------------------------------------
let run () : Task =
    task {
        let! playwright = Playwright.CreateAsync ()
        let! browser = playwright.Chromium.LaunchAsync (BrowserTypeLaunchOptions (ExecutablePath = chromiumPath (), Args = [| "--no-sandbox" |]))
        try
            let! page = browser.NewPageAsync ()
            let! _ = page.GotoAsync (BASE + "?rich=1")

            // The flagged composer mounts a ProseMirror editor into its host.
            let! _ = page.WaitForSelectorAsync "[data-rich-composer] .ProseMirror"
            do! page.ClickAsync "[data-rich-composer] .ProseMirror"

            // Type Markdown — the input rules should transform it live.
            do! page.Keyboard.TypeAsync "# Heading here"
            do! page.Keyboard.PressAsync "Enter"
            do! page.Keyboard.TypeAsync "say **bold** now"

            let! html = page.EvaluateAsync<string> ("() => document.querySelector('[data-rich-composer] .ProseMirror').innerHTML")
            if not (Regex.IsMatch (html, "<h1[^>]*>Heading here</h1>")) then
                failwithf "expected a rendered heading, got: %s" html
            if not (html.Contains "<strong>bold</strong>") then
                failwithf "expected rendered bold, got: %s" html
            printfn "editor E2E: typing Markdown in the composer renders formatted rich text (heading + bold)"
        finally
            browser.CloseAsync().GetAwaiter().GetResult ()
    }

// --- Entry -----------------------------------------------------------------------------
if not (File.Exists clientBundle) then
    eprintfn "editor E2E: %s not found — build the client first (the verify tier does this)" clientBundle
    exit 4

let server = startServer ()
try
    let flow = run ()
    if Task.WaitAny [| flow; Task.Delay 60_000 |] = 1 then
        eprintfn "editor E2E watchdog fired after 60000ms"; exit 3
    flow.GetAwaiter().GetResult ()   // rethrow assertion failures → non-zero exit
finally
    server.Stop ()
