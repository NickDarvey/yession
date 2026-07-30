module Yession.Host.ManagerUi

// The management UI (Phase 4, Step 25): a deliberately server-side-rendered admin surface
// — list sessions with live status, create, launch/resume, stop, open. Pure F# render
// functions produce full pages and FRAGMENTS from Fable.Lit templates (rendered to strings
// by our own `Ssr` wrapper — no client bundle, no Elmish, no Yjs); a tiny inline vanilla
// script swaps the fragments on create/launch/stop and takes live status from an SSE stream
// of rendered tables (`GET /sessions/rows`) — the server pushes, the page never polls. It
// shares the session client's `Style` (the same locally served /app.css), and — being
// online-only — is the natural home for server-side Lit SSR. This is not the collaborative
// client; it shares the Manager's 127.0.0.1 endpoint with the control RPC.

open Fable.Core.JsInterop
open Yession.Domain
open Yession.Manager
open Yession.Oidc
open Yession.App
open Yession.Host.Interop
open Lit

// --- Rendering (pure Lit templates, rendered to strings by Ssr) -------------------------

let private statusView (status: ProcessManager.SessionStatus) : TemplateResult =
    match status with
    | ProcessManager.NotRunning ->
        html $"""<span class="{Style.statusFaint}" data-status="{Dom.Manager.statusStopped}">stopped</span>"""
    | ProcessManager.Running (port, pid) ->
        html $"""<span class="{Style.statusOk}" data-status="{Dom.Manager.statusRunning}"><span class="{Style.statusDot}"></span>running · port {port} · pid {pid}</span>"""
    | ProcessManager.Exited code ->
        let reason = code |> Option.map string |> Option.defaultValue "signal"
        html $"""<span class="{Style.statusErr}" data-status="{Dom.Manager.statusExited}">exited ({reason})</span>"""

let private actions (view: ProcessManager.SessionView) : TemplateResult =
    let id = SessionId.value view.Record.SessionId
    match view.Status with
    | ProcessManager.Running (port, _) ->
        // A plain URL: access is authorized by the OIDC bounce (session -> manager ->
        // back), not by a token in the link. The origin is the configured public one
        // (docs/plans/09) so a remote browser gets a link it can follow; loopback when
        // unset.
        let openUrl = sprintf "%s:%d/" (publicSessionOrigin ()) port
        html $"""
            <a class="{Style.statusRun} mr-3" href="{openUrl}" target="_blank" data-open>open ↗</a>
            <button type="button" class="{Style.btnDanger}" data-stop="{id}">Stop</button>"""
    | ProcessManager.NotRunning
    | ProcessManager.Exited _ ->
        html $"""<button type="button" class="{Style.btnPrimary}" data-launch="{id}">Launch</button>"""

/// One session row — an action's swap unit: launch/stop replace it wholesale, so the markup is
/// always a pure function of the Manager's current view. (Live status replaces the whole table
/// instead; see the rows stream.)
let private rowTemplate (view: ProcessManager.SessionView) : TemplateResult =
    let id = SessionId.value view.Record.SessionId
    html $"""
        <tr class="border-b border-hair" data-session="{id}">
          <td class="px-3 py-2 {Style.mono}">{id}</td>
          <td class="px-3 py-2 {Style.body}">{view.Record.DisplayName}</td>
          <td class="px-3 py-2">{statusView view.Status}</td>
          <td class="px-3 py-2">{actions view}</td>
        </tr>"""

let private tableTemplate (views: ProcessManager.SessionView list) : TemplateResult =
    html $"""
        <table class="w-full text-left border-collapse" data-sessions>
          <thead>
            <tr class="border-b border-hair">
              <th class="px-3 py-2 {Style.label}">session</th>
              <th class="px-3 py-2 {Style.label}">name</th>
              <th class="px-3 py-2 {Style.label}">status</th>
              <th class="px-3 py-2 {Style.label}">actions</th>
            </tr>
          </thead>
          <tbody>{views |> List.map rowTemplate}</tbody>
        </table>"""

/// A rendered fragment (a single row), served as an action's answer.
let sessionRow (view: ProcessManager.SessionView) : string = Ssr.render (rowTemplate view)

/// A rendered fragment (the whole table), served after a create and pushed on the rows stream.
let sessionsTable (views: ProcessManager.SessionView list) : string = Ssr.render (tableTemplate views)

// The interactivity, without htmx: a tiny vanilla script that swaps fragments on
// create/launch/stop and takes live status from the rows stream. Inline (no external src) so
// the page is self-contained — local first, no CDN.
let private script =
    """
    const swap = (el, htmlText) => { const t = document.createElement('template'); t.innerHTML = htmlText.trim(); const n = t.content.firstElementChild; if (n && el) el.replaceWith(n) }
    document.addEventListener('click', async (e) => {
      const b = e.target.closest('[data-launch],[data-stop]'); if (!b) return
      const id = b.getAttribute('data-launch') || b.getAttribute('data-stop')
      const action = b.hasAttribute('data-launch') ? 'launch' : 'stop'
      const row = b.closest('tr')
      const r = await fetch('/sessions/' + id + '/' + action, { method: 'POST' })
      if (r.ok) swap(row, await r.text())
    })
    document.addEventListener('submit', async (e) => {
      const f = e.target.closest('[data-create-session]'); if (!f) return
      e.preventDefault()
      const r = await fetch('/sessions', { method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' }, body: new URLSearchParams(new FormData(f)) })
      if (r.ok) { swap(document.querySelector('[data-sessions]'), await r.text()); f.reset() }
    })
    const rows = new EventSource('/sessions/rows')
    rows.onmessage = (e) => { if (e.data) swap(document.querySelector('[data-sessions]'), e.data) }
    """

let private bodyTemplate (views: ProcessManager.SessionView list) : TemplateResult =
    html $"""
        <main class="max-w-4xl mx-auto px-8 py-10 flex flex-col gap-8">
          <h1 class="{Style.wordmark}">yession<span class="text-green">.</span> <span class="{Style.label}">manager</span></h1>
          <!-- The id is minted server-side (a Docker-safe Crockford id); only a human
               name is entered here. -->
          <form class="flex flex-wrap items-end gap-3" data-create-session>
            <input name="name" placeholder="display name" class="bg-surface {Style.body} px-3 py-2 outline-none border border-hair focus:border-blue">
            <button type="submit" class="{Style.btnPrimary}">Create</button>
          </form>
          {tableTemplate views}
        </main>"""

let page (views: ProcessManager.SessionView list) : string =
    String.concat "" [
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">"
        "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
        "<title>Yession Manager</title>"
        Style.headTags
        sprintf "</head><body class=\"%s\">" Style.app
        Ssr.render (bodyTemplate views)
        sprintf "<script>%s</script>" script
        "</body></html>"
    ]

// --- Routing ----------------------------------------------------------------------------

[<Fable.Core.Emit("new URL($0, 'http://local').pathname")>]
let private pathnameOf (url: string) : string = Fable.Core.Util.jsNative

[<Fable.Core.Emit("Object.fromEntries(new URLSearchParams($0))[$1] ?? ''")>]
let private formField (body: string) (name: string) : string = Fable.Core.Util.jsNative

[<Fable.Core.ImportAll("node:fs")>]
let private fs : obj = Fable.Core.Util.jsNative

let private cssPath = envOr "YESSION_APP_CSS" "app/out/public/app.css"

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
/// Every UI route is gated by `identify` — the Manager's authentication strategy
/// (docs/plans/07): a denial is a 401 on every route; both attributed and unattributed
/// outcomes are let through (under trust-localhost every loopback request is
/// unattributed, which is exactly today's behaviour).
let tryHandle
    (pm: ProcessManager.ProcessManager)
    (identify: IncomingMessage -> Async<AuthenticationOutcome>)
    (req: IncomingMessage)
    (res: ServerResponse)
    : bool =
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
    // Route first (pure — did the UI claim this path?), authenticate second: the gate
    // runs once, ahead of every claimed route, and unclaimed paths fall through to the
    // composing server untouched.
    let route : (unit -> unit) option =
        match req.``method``, path with
        | "GET", "/" ->
            Some (fun () -> html res (page (pm.Sessions ())))
        | "GET", "/app.css" ->
            // The same locally built stylesheet the session shell uses — shared style, no CDN.
            Some (fun () ->
                match readAsset "app.css" cssPath fs with
                | Some css -> respond res 200 "text/css; charset=utf-8" css
                | None -> respond res 404 "text/plain" "stylesheet not built (run: build)")
        | "POST", "/sessions" ->
            Some (fun () ->
                readBody req (fun body ->
                    // The human UI omits the id, so mint a Docker-safe Crockford one; a caller that
                    // supplies an explicit id (automation, tests) keeps it.
                    let id =
                        match formField body "id" with
                        | "" -> SessionId.value (SessionId.mint ())
                        | provided -> provided
                    match pm.CreateSession id (formField body "name") with
                    | Ok _ -> html res (sessionsTable (pm.Sessions ()))
                    | Error e -> respond res 400 "text/plain" e))
        | method', path when path.StartsWith "/sessions/" ->
            let rest = path.Substring "/sessions/".Length
            match method', rest.Split '/' with
            // Both streams are the same subscription projected differently — one publish per
            // launch, exit, and rename; snapshots, never deltas, so a reconnect is the whole
            // recovery protocol and a consumer that connects, reads one frame, and disconnects
            // has done a poll.
            | "GET", [| "stream" |] ->
                // The session registry (docs/plans/09): the Running set as wire frames. An
                // operator's serving binding holds this open to reconcile its proxy.
                Some (fun () ->
                    Sse.stream req res
                        (ProcessManager.registryFrameOf >> ControlWire.toString ControlWire.sessionRegistryFrame)
                        pm.SubscribeSessions
                    |> ignore)
            | "GET", [| "rows" |] ->
                // The management page's live status, pushed rather than polled: the WHOLE table,
                // rendered by the same `tableTemplate` the page and the action swaps use, so the
                // browser keeps no reconciliation logic. Stopped and exited rows are in the
                // published views, which is why the page renders them and the registry does not.
                Some (fun () -> Sse.stream req res sessionsTable pm.SubscribeSessions |> ignore)
            | "POST", [| id; "launch" |] ->
                Some (fun () -> sessionAction id (fun sessionId -> pm.Launch sessionId |> Async.Ignore))
            | "POST", [| id; "stop" |] ->
                Some (fun () -> sessionAction id (fun sessionId -> pm.Stop sessionId |> Async.Ignore))
            | _ -> None
        | _ -> None
    match route with
    | None -> false
    | Some handle ->
        Async.StartImmediate (
            async {
                match! identify req with
                | Denied reason -> respond res 401 "text/plain" reason
                | Attributed _ | Unattributed _ -> handle ()
            })
        true
