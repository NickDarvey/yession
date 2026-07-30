module Yession.Host.ManagerUi

// The management UI (Phase 4, Step 25): a deliberately server-side-rendered admin surface
// — list sessions with live status, create, launch/resume, stop, open. Pure F# render
// functions produce full pages and FRAGMENTS from Fable.Lit templates (rendered to strings
// by our own `Ssr` wrapper — no client bundle, no Elmish, no Yjs); a tiny inline vanilla
// script swaps the fragments on create/launch/stop and refreshes rows by polling. It
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

// The status word carries the colour (text, never boxed — the affordance rule); the
// process detail (port · pid) is plumbing, so it sits beside the word in faint mono
// rather than shouting in the status voice, and yields on narrow screens.
let private statusView (status: ProcessManager.SessionStatus) : TemplateResult =
    match status with
    | ProcessManager.NotRunning ->
        html $"""<span class="{Style.statusFaint}" data-status="{Dom.Manager.statusStopped}">stopped</span>"""
    | ProcessManager.Running (port, pid) ->
        html
            $"""<span class="{Style.statusOk}" data-status="{Dom.Manager.statusRunning}"><span class="{Style.statusDotPulse}"></span>running</span><span class="font-mono text-[12px] leading-4 text-ink-faint tabular-nums ml-2.5 max-md:hidden">port {port} · pid {pid}</span>"""
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
        // Open is THE action on a running session, so it is first in the DOM (first in
        // focus order) and wears the primary button; Stop is secondary — its border
        // stays dim until hovered (btnDanger's affordance). The row-reverse container
        // puts the primary on the right rail (the column Launch holds on stopped rows)
        // and, when the pair wraps on a narrow screen, on the top line.
        html $"""
            <a class="{Style.btnPrimary} min-w-[88px] inline-block text-center no-underline" href="{openUrl}" target="_blank" data-open>Open ↗</a>
            <button type="button" class="{Style.btnDanger}" data-stop="{id}">Stop</button>"""
    | ProcessManager.NotRunning
    | ProcessManager.Exited _ ->
        html $"""<button type="button" class="{Style.btnPrimary} min-w-[88px]" data-launch="{id}">Launch</button>"""

/// One session row — the swap unit: every action and the status poll replace it wholesale,
/// so the markup is always a pure function of the Manager's current view. The human name
/// leads (content is the interface); the minted id is plumbing, faint mono, and yields on
/// narrow screens. Actions anchor the right edge so the row reads name → state → verb.
let private rowTemplate (view: ProcessManager.SessionView) : TemplateResult =
    let id = SessionId.value view.Record.SessionId
    html $"""
        <tr class="border-b border-hair hover:bg-surface transition-colors" data-session="{id}">
          <td class="py-3 pr-4 align-middle {Style.body} max-md:max-w-[38vw] max-md:truncate">{view.Record.DisplayName}</td>
          <td class="py-3 pr-4 align-middle font-mono text-[12px] leading-4 text-ink-faint max-md:hidden">{id}</td>
          <td class="py-3 pr-4 align-middle whitespace-nowrap">{statusView view.Status}</td>
          <td class="py-3 pl-4 align-middle">
            <div class="flex flex-row-reverse flex-wrap items-center gap-x-4 gap-y-2">{actions view}</div>
          </td>
        </tr>"""

// The swap unit for a create is the whole section (label, count, table), so the count
// and the empty state can never go stale against the rows they describe.
let private tableTemplate (views: ProcessManager.SessionView list) : TemplateResult =
    let rows =
        match views with
        | [] ->
            [ html $"""
                <tr>
                  <td colspan="4" class="py-10 text-center {Style.small}">no sessions yet — name one above and create it</td>
                </tr>""" ]
        | views -> views |> List.map rowTemplate
    html $"""
        <section class="flex flex-col gap-3" data-sessions>
          <div class="flex items-baseline gap-2.5">
            <span class="{Style.label}">sessions</span>
            <span class="font-semibold text-[11px] leading-4 tracking-[0.18em] text-ink-faint tabular-nums">{List.length views}</span>
          </div>
          <table class="w-full text-left border-collapse">
            <thead>
              <tr class="border-b border-hair">
                <th scope="col" class="py-2 pr-4 {Style.label}">name</th>
                <th scope="col" class="py-2 pr-4 {Style.label} max-md:hidden">id</th>
                <th scope="col" class="py-2 pr-4 {Style.label}">status</th>
                <th scope="col" class="py-2 pl-4"><span class="sr-only">actions</span></th>
              </tr>
            </thead>
            <tbody>{rows}</tbody>
          </table>
        </section>"""

/// A rendered fragment (a single row), served to the poll/action swaps.
let sessionRow (view: ProcessManager.SessionView) : string = Ssr.render (rowTemplate view)

/// A rendered fragment (the whole table), served after a create.
let sessionsTable (views: ProcessManager.SessionView list) : string = Ssr.render (tableTemplate views)

// The interactivity, without htmx: a tiny vanilla script that swaps fragments on
// create/launch/stop and refreshes rows by polling. Inline (no external src) so the page
// is self-contained — local first, no CDN.
let private script =
    """
    const swap = (el, htmlText) => { const t = document.createElement('template'); t.innerHTML = htmlText.trim(); const n = t.content.firstElementChild; if (n && el && n.outerHTML !== el.outerHTML) el.replaceWith(n) }
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
    setInterval(async () => {
      for (const row of document.querySelectorAll('[data-session]')) {
        const id = row.getAttribute('data-session')
        try { const r = await fetch('/sessions/' + id + '/row'); if (r.ok) swap(row, await r.text()) } catch {}
      }
    }, 2000)
    """

// The page keeps the workspace anatomy: the shared 88px header band (wordmark on the
// common baseline, hairline below), then labelled sections on one left rail. The body
// shell is `Style.app` (h-screen, overflow-hidden), so <main> owns the scrolling — a
// long registry scrolls under a fixed viewport instead of clipping.
let private bodyTemplate (views: ProcessManager.SessionView list) : TemplateResult =
    html $"""
        <main class="flex-1 min-w-0 overflow-y-auto">
          <div class="max-w-4xl w-full mx-auto flex flex-col px-8 max-md:px-4">
            <header class="h-[88px] shrink-0 flex items-end pb-5 border-b border-hair">
              <h1 class="{Style.wordmark}">yession<span class="text-green">.</span> <span class="{Style.label}">manager</span></h1>
            </header>
            <!-- The id is minted server-side (a Docker-safe Crockford id); only a human
                 name is entered here. -->
            <form class="flex flex-col gap-3 pt-6 pb-8" data-create-session>
              <label class="{Style.label}" for="new-session-name">new session</label>
              <div class="flex flex-wrap items-center gap-3">
                <input id="new-session-name" name="name" placeholder="display name" autocomplete="off"
                  class="w-72 max-w-full bg-surface {Style.body} px-3 py-2 outline-none border border-hair focus:border-blue transition-colors">
                <button type="submit" class="{Style.btnPrimary}">Create</button>
              </div>
            </form>
            <div class="pb-10">{tableTemplate views}</div>
          </div>
        </main>"""

let page (views: ProcessManager.SessionView list) : string =
    String.concat "" [
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">"
        "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
        "<meta name=\"color-scheme\" content=\"dark\">"
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

// SSE keep-alive for the registry stream, so an idle subscription is not reaped by an
// HTTP idle timeout (the consumer also reconnects on drop — snapshots make that cheap).
[<Fable.Core.Emit("setInterval($1, $0)")>]
let private setInterval (ms: int) (callback: unit -> unit) : obj = Fable.Core.Util.jsNative

[<Fable.Core.Emit("clearInterval($0)")>]
let private clearInterval (handle: obj) : unit = Fable.Core.Util.jsNative

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
            | "GET", [| "stream" |] ->
                // The session registry stream (docs/plans/09): the Running set as
                // full-snapshot SSE frames — the current state on subscribe, then a
                // fresh frame on every launch, exit, and rename. An operator's serving
                // binding holds this open to reconcile its proxy; a consumer that
                // connects, reads the first frame, and disconnects has done a poll.
                Some (fun () ->
                    res.writeHead (200, createObj [ "content-type", box "text/event-stream"; "cache-control", box "no-store"; "connection", box "keep-alive" ]) |> ignore
                    res.write ": subscribed\n\n" |> ignore
                    let sink (frame: ControlWire.SessionRegistryFrame) =
                        res.write (sprintf "data: %s\n\n" (ControlWire.toString ControlWire.sessionRegistryFrame frame)) |> ignore
                    let unsubscribe = pm.SubscribeSessions sink
                    let heartbeat = setInterval 15000 (fun () -> res.write ": ping\n\n" |> ignore)
                    req.on ("close", fun _ ->
                        clearInterval heartbeat
                        unsubscribe ()) |> ignore)
            | "POST", [| id; "launch" |] ->
                Some (fun () -> sessionAction id (fun sessionId -> pm.Launch sessionId |> Async.Ignore))
            | "POST", [| id; "stop" |] ->
                Some (fun () -> sessionAction id (fun sessionId -> pm.Stop sessionId |> Async.Ignore))
            | "GET", [| id; "row" |] ->
                Some (fun () ->
                    match SessionId.create id with
                    | Ok sessionId -> html res (rowOf sessionId)
                    | Error e -> respond res 400 "text/plain" e)
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
