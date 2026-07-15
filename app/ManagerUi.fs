module Yession.Host.ManagerUi

// The management UI (Phase 4, Step 25): a deliberately server-side-rendered admin
// surface — list sessions with live status, create, launch/resume, stop, open. Pure F#
// render functions produce full pages and FRAGMENTS; htmx swaps the fragments on
// hx-post/hx-get, and row status refreshes by polling — no Elmish, no Yjs, no client
// bundle. htmx itself is vendored and served from this endpoint (local-first: no CDN).
// This is not the collaborative client; it shares the Manager's 127.0.0.1 endpoint
// with the control RPC.

open Fable.Core.JsInterop
open Yession.Domain
open Yession.Host.Interop

// --- Rendering (pure) -------------------------------------------------------------------

let private escapeHtml (s: string) =
    s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;")

let private statusHtml (status: ProcessManager.SessionStatus) : string =
    match status with
    | ProcessManager.NotRunning -> "<span data-status=\"stopped\">stopped</span>"
    | ProcessManager.Running (port, pid) ->
        sprintf "<span data-status=\"running\">running · port %d · pid %d</span>" port pid
    | ProcessManager.Exited code ->
        sprintf "<span data-status=\"exited\">exited (%s)</span>"
            (code |> Option.map string |> Option.defaultValue "signal")

/// One session row. The row is the swap unit: every action and the status poll replace
/// it wholesale (`hx-swap="outerHTML"`), so the markup is always a pure function of
/// the Manager's current view.
let sessionRow (view: ProcessManager.SessionView) : string =
    let id = SessionId.value view.Record.SessionId
    let actions =
        match view.Status with
        | ProcessManager.Running (port, _) ->
            String.concat "" [
                sprintf "<a href=\"http://127.0.0.1:%d/?token=%s\" target=\"_blank\" data-open>open</a> "
                    port (System.Uri.EscapeDataString view.Record.Token)
                sprintf "<button hx-post=\"/sessions/%s/stop\" hx-target=\"closest tr\" hx-swap=\"outerHTML\" data-stop>Stop</button>" id
            ]
        | ProcessManager.NotRunning
        | ProcessManager.Exited _ ->
            sprintf "<button hx-post=\"/sessions/%s/launch\" hx-target=\"closest tr\" hx-swap=\"outerHTML\" data-launch>Launch</button>" id
    String.concat "" [
        sprintf "<tr id=\"session-%s\" data-session=\"%s\" hx-get=\"/sessions/%s/row\" hx-trigger=\"every 2s\" hx-swap=\"outerHTML\">" id id id
        sprintf "<td>%s</td>" (escapeHtml (SessionId.value view.Record.SessionId))
        sprintf "<td>%s</td>" (escapeHtml view.Record.DisplayName)
        sprintf "<td>%s</td>" (statusHtml view.Status)
        sprintf "<td>%s</td>" actions
        "</tr>"
    ]

let sessionsTable (views: ProcessManager.SessionView list) : string =
    String.concat "" [
        "<table id=\"sessions\" data-sessions>"
        "<thead><tr><th>session</th><th>name</th><th>status</th><th>actions</th></tr></thead>"
        "<tbody>"
        views |> List.map sessionRow |> String.concat ""
        "</tbody></table>"
    ]

let page (views: ProcessManager.SessionView list) : string =
    String.concat "" [
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">"
        "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
        "<title>Yession Manager</title>"
        // Vendored, served by this endpoint — never a CDN.
        "<script src=\"/htmx.js\"></script>"
        "</head><body>"
        "<main><h1>Yession Manager</h1>"
        // Creating returns the whole refreshed table (the new row rides along).
        "<form hx-post=\"/sessions\" hx-target=\"#sessions\" hx-swap=\"outerHTML\" data-create-session>"
        "<input name=\"id\" placeholder=\"session id\" required> "
        "<input name=\"name\" placeholder=\"display name\"> "
        "<input name=\"token\" placeholder=\"token\" required> "
        "<button type=\"submit\">Create</button>"
        "</form>"
        sessionsTable views
        "</main></body></html>"
    ]

// --- Routing ----------------------------------------------------------------------------

[<Fable.Core.Emit("new URL($0, 'http://local').pathname")>]
let private pathnameOf (url: string) : string = Fable.Core.Util.jsNative

[<Fable.Core.Emit("Object.fromEntries(new URLSearchParams($0))[$1] ?? ''")>]
let private formField (body: string) (name: string) : string = Fable.Core.Util.jsNative

[<Fable.Core.ImportAll("node:fs")>]
let private fs : obj = Fable.Core.Util.jsNative

let private htmxBundlePath = envOr "YESSION_HTMX_BUNDLE" "node_modules/htmx.org/dist/htmx.min.js"

let private readBody (req: IncomingMessage) (cont: string -> unit) =
    let mutable acc = ""
    req.on ("data", fun chunk -> acc <- acc + bufferToString chunk) |> ignore
    req.on ("end", fun _ -> cont acc) |> ignore

let private respond (res: ServerResponse) (status: int) (contentType: string) (body: string) =
    res.writeHead (status, createObj [ "content-type", box contentType; "cache-control", box "no-store" ]) |> ignore
    res.``end`` body

let private html (res: ServerResponse) (body: string) = respond res 200 "text/html; charset=utf-8" body

/// Handle a management-UI request against the Manager. Returns false for paths that
/// are not the UI's (the composing server falls through — e.g. to the control routes).
let tryHandle (pm: ProcessManager.ProcessManager) (req: IncomingMessage) (res: ServerResponse) : bool =
    let path = pathnameOf req.url
    let rowOf (sessionId: SessionId) =
        match pm.TryFind sessionId with
        | Some view -> sessionRow view
        | None -> ""
    let sessionAction (id: string) (action: SessionId -> Async<unit>) =
        match SessionId.create id with
        | Error e -> respond res 400 "text/plain" e
        | Ok sessionId ->
            Async.StartImmediate (
                async {
                    do! action sessionId
                    html res (rowOf sessionId)
                })
    match req.``method``, path with
    | "GET", "/" ->
        html res (page (pm.Sessions ()))
        true
    | "GET", "/htmx.js" ->
        // From the SEA blob when packaged; from node_modules in development.
        match readAsset "htmx.min.js" htmxBundlePath fs with
        | Some js -> respond res 200 "text/javascript; charset=utf-8" js
        | None -> respond res 404 "text/plain" "htmx bundle not found (npm install)"
        true
    | "POST", "/sessions" ->
        readBody req (fun body ->
            match pm.CreateSession (formField body "id") (formField body "name") (formField body "token") with
            | Ok _ -> html res (sessionsTable (pm.Sessions ()))
            | Error e -> respond res 400 "text/plain" e)
        true
    | method', path when path.StartsWith "/sessions/" ->
        let rest = path.Substring "/sessions/".Length
        match method', rest.Split '/' with
        | "POST", [| id; "launch" |] ->
            sessionAction id (fun sessionId -> pm.Launch sessionId |> Async.Ignore)
            true
        | "POST", [| id; "stop" |] ->
            sessionAction id (fun sessionId -> pm.Stop sessionId |> Async.Ignore)
            true
        | "GET", [| id; "row" |] ->
            (match SessionId.create id with
             | Ok sessionId -> html res (rowOf sessionId)
             | Error e -> respond res 400 "text/plain" e)
            true
        | _ -> false
    | _ -> false
