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

let private actions (access: PublicAccess) (view: ProcessManager.SessionView) : TemplateResult =
    let id = SessionId.value view.Record.SessionId
    match view.Status with
    | ProcessManager.Running (port, _) ->
        // A plain URL: access is authorized by the OIDC bounce (session -> manager ->
        // back), not by a token in the link. The origin is the configured public one
        // (docs/plans/09) so a remote browser gets a link it can follow; loopback when
        // unset.
        // The address comes from the deployment's session template (docs/plans/10), the
        // same declaration the session itself used to register its redirect URI.
        let openUrl = sprintf "%s/" (PublicAccess.sessionAddress view.Record.SessionId port access).Url
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

/// One session row — an action's swap unit: launch/stop replace it wholesale, so the markup is
/// always a pure function of the Manager's current view. (Live status replaces the whole table
/// instead; see the rows stream.) The human name leads (content is the interface); the minted
/// id is plumbing, faint mono, and yields on narrow screens. Actions anchor the right edge so
/// the row reads name → state → verb.
let private rowTemplate (access: PublicAccess) (view: ProcessManager.SessionView) : TemplateResult =
    let id = SessionId.value view.Record.SessionId
    html $"""
        <tr class="border-b border-hair hover:bg-surface transition-colors" data-session="{id}">
          <td class="py-3 pr-4 align-middle {Style.body} max-md:max-w-[38vw] max-md:truncate">{view.Record.DisplayName}</td>
          <td class="py-3 pr-4 align-middle font-mono text-[12px] leading-4 text-ink-faint max-md:hidden">{id}</td>
          <td class="py-3 pr-4 align-middle whitespace-nowrap">{statusView view.Status}</td>
          <td class="py-3 pl-4 align-middle">
            <div class="flex flex-row-reverse flex-wrap items-center gap-x-4 gap-y-2">{actions access view}</div>
          </td>
        </tr>"""

// The swap unit for a create is the whole section (label, count, table), so the count
// and the empty state can never go stale against the rows they describe.
let private tableTemplate (access: PublicAccess) (views: ProcessManager.SessionView list) : TemplateResult =
    let rows =
        match views with
        | [] ->
            [ html $"""
                <tr>
                  <td colspan="4" class="py-10 text-center {Style.small}">no sessions yet</td>
                </tr>""" ]
        | views -> views |> List.map (rowTemplate access)
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

/// A rendered fragment (a single row), served as an action's answer.
let sessionRow (access: PublicAccess) (view: ProcessManager.SessionView) : string =
    Ssr.render (rowTemplate access view)

/// A rendered fragment (the whole table), served after a create and pushed on the rows stream.
let sessionsTable (access: PublicAccess) (views: ProcessManager.SessionView list) : string =
    Ssr.render (tableTemplate access views)

// The interactivity, without htmx: a tiny vanilla script that swaps fragments on
// create/launch/stop and takes live status from the rows stream. Inline (no external src) so
// the page is self-contained — local first, no CDN.
let private script =
    """
    const swap = (el, htmlText) => {
      const t = document.createElement('template'); t.innerHTML = htmlText.trim()
      const n = t.content.firstElementChild
      if (!n || !el || n.outerHTML === el.outerHTML) return
      const hadFocus = el.contains(document.activeElement)
      el.replaceWith(n)
      // Keyboard continuity (WCAG 2.0): replacing the focused element strands focus on
      // <body>; land it on the replacement's first action instead (the primary, by DOM order).
      if (hadFocus) { const f = n.querySelector('a[href], button, input'); if (f) f.focus() }
    }
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
    // Declaring an MCP server (Plan 17): the only place a url is written, and the only
    // management action that can be REFUSED for a reason a human needs to read — a name
    // clash. So this one reports, where create/launch/stop only swap.
    const mcpSwap = (htmlText) => swap(document.querySelector('[data-mcp]'), htmlText)
    document.addEventListener('submit', async (e) => {
      const f = e.target.closest('[data-declare-mcp]'); if (!f) return
      e.preventDefault()
      const r = await fetch('/mcp/servers', { method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' }, body: new URLSearchParams(new FormData(f)) })
      const text = await r.text()
      if (r.ok) { mcpSwap(text) } else { const p = f.querySelector('[data-mcp-error]'); if (p) p.textContent = text }
    })
    document.addEventListener('click', async (e) => {
      const b = e.target.closest('[data-mcp-withdraw]'); if (!b) return
      const body = new URLSearchParams({ name: b.getAttribute('data-mcp-withdraw'), session: b.getAttribute('data-mcp-audience') || '' })
      const r = await fetch('/mcp/servers/withdraw', { method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' }, body })
      if (r.ok) mcpSwap(await r.text())
    })
    const rows = new EventSource('/sessions/rows')
    rows.onmessage = (e) => { if (e.data) swap(document.querySelector('[data-sessions]'), e.data) }
    """

/// One declared MCP server (Plan 17). The AUDIENCE is a column rather than a separate
/// table, because host-wide and session-scoped declarations are the same kind of fact and
/// splitting them would make "which of these does session A get?" a question you answer by
/// reading two places.
let private mcpRowTemplate (views: ProcessManager.SessionView list) (declaration: McpDeclaration) : TemplateResult =
    let name = McpServerName.value declaration.Server.Name
    let audience, audienceValue =
        match declaration.Audience with
        | AnySession -> "any session", ""
        | OneSession id ->
            let label =
                views
                |> List.tryFind (fun v -> v.Record.SessionId = id)
                |> Option.map (fun v -> v.Record.DisplayName)
                |> Option.defaultValue (SessionId.value id)
            label, SessionId.value id
    html $"""
        <tr class="border-b border-hair hover:bg-surface transition-colors" data-mcp-server="{name}">
          <td class="py-3 pr-4 align-middle {Style.body}">{name}</td>
          <td class="py-3 pr-4 align-middle font-mono text-[12px] leading-4 text-ink-faint max-md:hidden">{McpTransport.describe declaration.Server.Transport}</td>
          <td class="py-3 pr-4 align-middle {Style.small}">{audience}</td>
          <td class="py-3 pl-4 align-middle">
            <div class="flex flex-row-reverse">
              <button type="button" class="{Style.btnDanger}" data-mcp-withdraw="{name}" data-mcp-audience="{audienceValue}">Withdraw</button>
            </div>
          </td>
        </tr>"""

/// The whole MCP section — the swap unit, so the count and the empty state can never go
/// stale against the rows they describe. This is the ONE place a url is ever written: a
/// session does not select from these, and the agent has no command that changes them.
let private mcpTemplate (views: ProcessManager.SessionView list) (declarations: McpDeclaration list) : TemplateResult =
    let rows =
        match declarations with
        | [] ->
            [ html $"""
                <tr>
                  <td colspan="4" class="py-10 text-center {Style.small}">no MCP servers declared — a session reaches only its own tools</td>
                </tr>""" ]
        | declarations -> declarations |> List.map (mcpRowTemplate views)
    let options =
        views
        |> List.map (fun view ->
            html $"""<option value="{SessionId.value view.Record.SessionId}">{view.Record.DisplayName}</option>""")
    html $"""
        <section class="flex flex-col gap-3" data-mcp>
          <div class="flex items-baseline gap-2.5">
            <span class="{Style.label}">MCP servers</span>
            <span class="font-semibold text-[11px] leading-4 tracking-[0.18em] text-ink-faint tabular-nums">{List.length declarations}</span>
          </div>
          <table class="w-full text-left border-collapse">
            <thead>
              <tr class="border-b border-hair">
                <th scope="col" class="py-2 pr-4 {Style.label}">name</th>
                <th scope="col" class="py-2 pr-4 {Style.label} max-md:hidden">address</th>
                <th scope="col" class="py-2 pr-4 {Style.label}">reaches</th>
                <th scope="col" class="py-2 pl-4"><span class="sr-only">actions</span></th>
              </tr>
            </thead>
            <tbody>{rows}</tbody>
          </table>
          <form class="flex flex-col gap-3 pt-3" data-declare-mcp>
            <div class="flex flex-wrap items-end gap-3">
              <div class="flex flex-col gap-1.5">
                <label class="{Style.label}" for="mcp-name">name</label>
                <input id="mcp-name" name="name" placeholder="serial" autocomplete="off" required
                  class="w-40 max-w-full bg-surface {Style.body} px-3 py-2 outline-none border border-hair focus:border-blue transition-colors">
              </div>
              <div class="flex flex-col gap-1.5">
                <label class="{Style.label}" for="mcp-url">address</label>
                <input id="mcp-url" name="url" type="url" placeholder="http://127.0.0.1:7333" autocomplete="off" required
                  class="w-72 max-w-full bg-surface {Style.body} px-3 py-2 outline-none border border-hair focus:border-blue transition-colors">
              </div>
              <div class="flex flex-col gap-1.5">
                <label class="{Style.label}" for="mcp-audience">reaches</label>
                <select id="mcp-audience" name="session"
                  class="w-56 max-w-full bg-surface {Style.body} px-3 py-2 outline-none border border-hair focus:border-blue transition-colors">
                  <option value="">any session</option>
                  {options}
                </select>
              </div>
              <button type="submit" class="{Style.btnPrimary}">Declare</button>
            </div>
            <p class="{Style.small}" data-mcp-error aria-live="polite"></p>
          </form>
        </section>"""

/// The MCP section, rendered as an action's answer.
let mcpSection (views: ProcessManager.SessionView list) (declarations: McpDeclaration list) : string =
    Ssr.render (mcpTemplate views declarations)

// The page keeps the workspace anatomy: the shared 88px header band (wordmark on the
// common baseline, hairline below), then labelled sections on one left rail. The body
// shell is `Style.app` (h-screen, overflow-hidden), so <main> owns the scrolling — a
// long registry scrolls under a fixed viewport instead of clipping.
let private bodyTemplate
    (access: PublicAccess)
    (views: ProcessManager.SessionView list)
    (declarations: McpDeclaration list)
    : TemplateResult =
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
            <div class="pb-10">{tableTemplate access views}</div>
            <div class="pb-10">{mcpTemplate views declarations}</div>
          </div>
        </main>"""

/// `styleSheetUrl` is passed in rather than read from the module below: F# scopes top-down, and
/// the stylesheet's address is derived from bytes read further down the file.
let page
    (styleSheetUrl: string)
    (access: PublicAccess)
    (views: ProcessManager.SessionView list)
    (declarations: McpDeclaration list)
    : string =
    String.concat "" [
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">"
        "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
        "<meta name=\"color-scheme\" content=\"dark\">"
        "<title>Yession Manager</title>"
        Style.headTags styleSheetUrl
        sprintf "</head><body class=\"%s\">" Style.app
        Ssr.render (bodyTemplate access views declarations)
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

/// The same stylesheet the session shell serves, read and addressed once at boot rather than
/// per request — the Manager page is rendered per request, so this is what keeps every render
/// naming the same bytes. Addressed by `Interop.contentDigest`, the same function the session
/// uses, so one stylesheet never has two spellings.
let private css = readAsset "app.css" cssPath fs

let private cssUrl = SessionRoute.relative (AppCss (contentDigest css))

let private readBody (req: IncomingMessage) (cont: string -> unit) =
    let mutable acc = ""
    req.on ("data", fun chunk -> acc <- acc + bufferToString chunk) |> ignore
    req.on ("end", fun _ -> cont acc) |> ignore

let private respondWith (res: ServerResponse) (status: int) (contentType: string) (cacheControl: string) (body: string) =
    res.writeHead (status, createObj [ "content-type", box contentType; "cache-control", box cacheControl ]) |> ignore
    res.``end`` body

/// Every management response but one is a live view of mutable process state, so `no-store` is
/// the default here. The stylesheet is the exception — see its route.
let private respond (res: ServerResponse) (status: int) (contentType: string) (body: string) =
    respondWith res status contentType "no-store" body

let private html (res: ServerResponse) (body: string) = respond res 200 "text/html; charset=utf-8" body

/// A string as a JS literal, for the one inline script below — so a URL containing a quote
/// is data rather than syntax.
[<Fable.Core.Emit("JSON.stringify($0)")>]
let private jsonLiteral (s: string) : string = Fable.Core.Util.jsNative

/// The `/sessions/{id}/open` landing page (Plan 11).
///
/// Not a bare 302. A session that had to be launched is reachable at its own address only
/// once the operator's proxy has a mapping for it, and a reconciler driven by
/// `/sessions/stream` gets there in a few hundred milliseconds — quick, but a race against
/// a redirect the browser follows immediately. So the page polls its own target and goes
/// when it answers.
///
/// Bounded, and it says why it gave up. An `/open` that spins forever is indistinguishable
/// from one that is about to work, which is the failure mode this whole feature is supposed
/// to remove rather than add.
let private openingPage (target: string) : string =
    sprintf
        """<!doctype html>
<html lang="en"><head><meta charset="utf-8"><title>Opening session</title>
<style>body{font-family:system-ui,sans-serif;max-width:32rem;margin:4rem auto;padding:0 1rem}p{color:#444}</style>
</head><body>
<h1>Opening your session…</h1>
<p id="status">Waiting for it to answer.</p>
<p><a id="target" href="%s">Open it directly</a></p>
<script>
  const target = %s
  let attempts = 0
  async function poll () {
    attempts++
    try {
      await fetch(target, { mode: 'no-cors', cache: 'no-store' })
      location.replace(target)
      return
    } catch (e) {
      if (attempts >= 40) {
        document.getElementById('status').textContent =
          'The session started, but its address is still not answering after 20 seconds. ' +
          'If this deployment maps session ports through a proxy, that mapping has not appeared.'
        return
      }
      setTimeout(poll, 500)
    }
  }
  poll()
</script>
</body></html>"""
        (Ssr.escapeAttr target)
        (jsonLiteral target)

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
        | Some view -> sessionRow pm.Public view
        | None -> ""
    // An action's outcome is not discarded: a launch that fails leaves the session stopped,
    // and answering with an ordinary row said nothing about why. The row still comes back on
    // success (it is the swap unit); a failure answers with its reason.
    let sessionAction (id: string) (action: SessionId -> Async<Result<unit, string>>) =
        match SessionId.create id with
        | Error e -> respond res 400 "text/plain" e
        | Ok sessionId ->
            Async.StartImmediate (
                async {
                    match! action sessionId with
                    | Ok () -> html res (rowOf sessionId)
                    | Error reason -> respond res 500 "text/plain" reason
                })
    // Route first (pure — did the UI claim this path?), authenticate second: the gate
    // runs once, ahead of every claimed route, and unclaimed paths fall through to the
    // composing server untouched.
    let route : (unit -> unit) option =
        match req.``method``, path with
        | "GET", "/" ->
            Some (fun () -> html res (page cssUrl pm.Public (pm.Sessions ()) (pm.McpServers ())))
        | "GET", path when path = "/" + cssUrl ->
            // The same locally built stylesheet the session shell uses — shared style, no CDN
            // — and the one management response that is cacheable, because it is addressed by
            // a digest of its own bytes. An address that is not the current one is never
            // served: see `Signalling.serveAsset` for why answering it would be worse than a
            // 404. The Manager has no `<base href>` and always sits at its origin root, so the
            // relative URL the page emits resolves to exactly this path.
            Some (fun () ->
                match css with
                | Some body -> respondWith res 200 "text/css; charset=utf-8" CachePolicy.asset body
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
                    | Ok _ -> html res (sessionsTable pm.Public (pm.Sessions ()))
                    | Error e -> respond res 400 "text/plain" e))
        // Declaring an MCP server (Plan 17). The ONE act that names a url, and the only
        // one that is not read-only. There is deliberately no per-session enable beside it:
        // an operator who declares a server declares it in order for it to be used.
        | "POST", "/mcp/servers" ->
            Some (fun () ->
                readBody req (fun body ->
                    let audience =
                        match formField body "session" with
                        | "" -> Ok AnySession
                        | id -> SessionId.create id |> Result.map OneSession
                    let declared =
                        match McpServerName.create (formField body "name"), audience with
                        | Error e, _ -> Error e
                        | _, Error e -> Error e
                        | Ok name, Ok audience ->
                            match formField body "url" with
                            | "" -> Error "an MCP server needs an address"
                            | url ->
                                Ok
                                    { Server =
                                        { Name = name
                                          Transport = McpHttp url
                                          Description = formField body "description" }
                                      Audience = audience }
                    match declared |> Result.bind pm.DeclareMcpServer with
                    | Ok () -> html res (mcpSection (pm.Sessions ()) (pm.McpServers ()))
                    | Error e -> respond res 400 "text/plain" e))
        | "POST", "/mcp/servers/withdraw" ->
            Some (fun () ->
                readBody req (fun body ->
                    let audience =
                        match formField body "session" with
                        | "" -> Ok AnySession
                        | id -> SessionId.create id |> Result.map OneSession
                    match McpServerName.create (formField body "name"), audience with
                    | Error e, _
                    | _, Error e -> respond res 400 "text/plain" e
                    | Ok name, Ok audience ->
                        pm.WithdrawMcpServer name audience
                        html res (mcpSection (pm.Sessions ()) (pm.McpServers ()))))
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
                Some (fun () -> Sse.stream req res (sessionsTable pm.Public) pm.SubscribeSessions |> ignore)
            | "POST", [| id; "launch" |] ->
                Some (fun () ->
                    sessionAction id (fun sessionId ->
                        async {
                            let! outcome = pm.Launch sessionId
                            return outcome |> Result.map ignore
                        }))
            | "POST", [| id; "stop" |] ->
                Some (fun () -> sessionAction id pm.Stop)
            // The stable way back into a session (Plan 11). A session's own address changes
            // whenever it is relaunched, and under idle reaping that is routine rather than
            // rare — so THIS is the URL to bookmark and the one the session client's
            // reconnect offer points at. Launch it if it is stopped, then hand the browser
            // to wherever this deployment says the session lives.
            | "GET", [| id; "open" |] ->
                Some (fun () ->
                    match SessionId.create id with
                    | Error e -> respond res 400 "text/plain" e
                    | Ok sessionId ->
                        Async.StartImmediate (
                            async {
                                match pm.TryFind sessionId with
                                | None -> respond res 404 "text/plain" (sprintf "unknown session %s" id)
                                | Some view ->
                                    // Already running is the common case once a client has
                                    // reconnected on its own; asking for the port it already
                                    // has is not a relaunch.
                                    let! port =
                                        match view.Status with
                                        | ProcessManager.Running (port, _) -> async { return Ok port }
                                        | ProcessManager.NotRunning
                                        | ProcessManager.Exited _ -> pm.Launch sessionId
                                    match port with
                                    | Error reason -> respond res 500 "text/plain" reason
                                    | Ok port ->
                                        let address = PublicAccess.sessionAddress sessionId port pm.Public
                                        html res (openingPage (sprintf "%s/" address.Url))
                            }))
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
